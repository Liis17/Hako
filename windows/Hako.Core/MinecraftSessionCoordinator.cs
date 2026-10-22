using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Hako.Core;

public enum MinecraftSessionStatus
{
    Idle,
    Connecting,
    Connected,
    Failed
}

/// <summary>
/// Refreshes an online Minecraft session for launch. Notifications are raised on the thread that
/// performs the refresh; UI subscribers must marshal them to their DispatcherQueue.
/// </summary>
public sealed class MinecraftSessionCoordinator : INotifyPropertyChanged
{
    private readonly AppCoordinator _app;
    private readonly IAccountStore _accountStore;
    private readonly IMicrosoftAuthService _authService;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private MinecraftSessionStatus _status;
    private AuthErrorKind? _error;
    private CancellationTokenSource? _refreshCancellation;
    private TaskCompletionSource<AccountSession>? _refreshCompletion;

    internal MinecraftSessionCoordinator(
        AppCoordinator app,
        IAccountStore accountStore,
        IMicrosoftAuthService authService,
        TimeProvider? timeProvider = null)
    {
        _app = app;
        _accountStore = accountStore;
        _authService = authService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _app.AccountGenerationChanged += OnAccountGenerationChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MinecraftSessionStatus Status
    {
        get { lock (_sync) return _status; }
        private set => SetProperty(ref _status, value);
    }

    public AuthErrorKind? Error
    {
        get { lock (_sync) return _error; }
        private set => SetProperty(ref _error, value);
    }

    public Task<AccountSession> ConnectForLaunchAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _app.CaptureAccountState();
        Task<AccountSession> sharedTask;
        TaskCompletionSource<AccountSession>? completion = null;
        CancellationTokenSource? refreshCancellation = null;

        lock (_sync)
        {
            if (_refreshCompletion is { Task.IsCompleted: false } active)
            {
                sharedTask = active.Task;
            }
            else if (snapshot.Account is { MinecraftAccessToken.Length: > 0, MinecraftTokenExpiresAt: { } expiresAt,
                         MinecraftProfileId.Length: > 0, MinecraftName.Length: > 0 } account
                     && expiresAt > _timeProvider.GetUtcNow().AddMinutes(1))
            {
                Status = MinecraftSessionStatus.Connected;
                Error = null;
                return Task.FromResult(account);
            }
            else if (snapshot.Account is null)
            {
                Status = MinecraftSessionStatus.Failed;
                Error = AuthErrorKind.MinecraftProfileMissing;
                return Task.FromException<AccountSession>(new AuthServiceException(AuthErrorKind.MinecraftProfileMissing));
            }
            else
            {
                completion = new TaskCompletionSource<AccountSession>(TaskCreationOptions.RunContinuationsAsynchronously);
                refreshCancellation = new CancellationTokenSource();
                _refreshCompletion = completion;
                _refreshCancellation = refreshCancellation;
                Status = MinecraftSessionStatus.Connecting;
                Error = null;
                sharedTask = completion.Task;
            }
        }

        if (completion is not null && refreshCancellation is not null && snapshot.Account is { } accountToRefresh)
        {
            _ = RunRefreshAsync(accountToRefresh, snapshot.Generation, refreshCancellation, completion);
        }

        return sharedTask.WaitAsync(cancellationToken);
    }

    public void CancelRefresh()
    {
        lock (_sync)
        {
            _refreshCancellation?.Cancel();
            _refreshCancellation = null;
            _refreshCompletion = null;
            Status = MinecraftSessionStatus.Idle;
            Error = null;
        }
    }

    private async Task RunRefreshAsync(
        AccountSession originalAccount,
        long generation,
        CancellationTokenSource cancellation,
        TaskCompletionSource<AccountSession> completion)
    {
        try
        {
            AccountSession connected;
            try
            {
                connected = await RefreshOnceAsync(originalAccount, generation, cancellation.Token).ConfigureAwait(false);
            }
            catch (AccountChangedException)
            {
                // The account was replaced within the same generation (for example by a refresh cancelled via
                // CancelRefresh that had already committed a rotated token): retry once from the current account.
                var state = _app.CaptureAccountState();
                if (state.Generation != generation || state.Account is null)
                {
                    throw new OperationCanceledException(cancellation.Token);
                }

                connected = await RefreshOnceAsync(state.Account, generation, cancellation.Token).ConfigureAwait(false);
            }

            if (!IsCurrentGeneration(generation))
            {
                throw new OperationCanceledException(cancellation.Token);
            }

            SetResultIfCurrent(generation, cancellation, completion, connected);
        }
        catch (AccountChangedException)
        {
            // The account changed again during the retry; it is not a network failure, so the launch is cancelled.
            completion.TrySetCanceled(cancellation.Token);
            SetIdleIfCurrentRefresh(cancellation, completion);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || !IsCurrentGeneration(generation))
        {
            completion.TrySetCanceled(cancellation.Token);
            SetIdleIfCurrentRefresh(cancellation, completion);
        }
        catch (AuthServiceException exception)
        {
            SetFailureIfCurrent(generation, cancellation, completion, exception.Kind);
        }
        catch (AccountStoreException)
        {
            SetFailureIfCurrent(generation, cancellation, completion, AuthErrorKind.Persistence);
        }
        catch (HttpRequestException)
        {
            SetFailureIfCurrent(generation, cancellation, completion, AuthErrorKind.Network);
        }
        catch (JsonException)
        {
            SetFailureIfCurrent(generation, cancellation, completion, AuthErrorKind.InvalidResponse);
        }
        catch (OperationCanceledException)
        {
            SetFailureIfCurrent(generation, cancellation, completion, AuthErrorKind.Network);
        }
        catch
        {
            SetFailureIfCurrent(generation, cancellation, completion, AuthErrorKind.InvalidResponse);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_refreshCancellation, cancellation))
                {
                    _refreshCancellation = null;
                    _refreshCompletion = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private async Task<AccountSession> RefreshOnceAsync(AccountSession account, long generation, CancellationToken cancellationToken)
    {
        var currentAccount = account;
        var connection = await _authService.ConnectMinecraftAsync(
            account.MicrosoftRefreshToken,
            async (rotatedRefreshToken, token) =>
            {
                var updated = currentAccount with { MicrosoftRefreshToken = rotatedRefreshToken };
                await SaveCurrentAccountAsync(currentAccount, updated, generation, token).ConfigureAwait(false);
                currentAccount = updated;
            },
            cancellationToken).ConfigureAwait(false);

        var connected = currentAccount with
        {
            MinecraftProfileId = connection.ProfileId,
            MinecraftName = connection.ProfileName,
            MinecraftAccessToken = connection.AccessToken,
            MinecraftTokenExpiresAt = connection.ExpiresAt
        };
        await SaveCurrentAccountAsync(currentAccount, connected, generation, cancellationToken).ConfigureAwait(false);
        return connected;
    }

    private async Task SaveCurrentAccountAsync(
        AccountSession expected,
        AccountSession updated,
        long generation,
        CancellationToken cancellationToken)
    {
        var saved = await _accountStore.SaveAsync(updated, persist =>
            _app.TryCommitAccountUpdate(expected, generation, updated, persist), cancellationToken).ConfigureAwait(false);
        if (!saved)
        {
            if (cancellationToken.IsCancellationRequested || !IsCurrentGeneration(generation))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw new AccountChangedException();
        }
    }

    private sealed class AccountChangedException : Exception;

    private bool IsCurrentGeneration(long generation) => _app.CaptureAccountState().Generation == generation;

    private void SetResultIfCurrent(
        long generation,
        CancellationTokenSource cancellation,
        TaskCompletionSource<AccountSession> completion,
        AccountSession account)
    {
        if (!IsCurrentGeneration(generation) || cancellation.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellation.Token);
            return;
        }

        lock (_sync)
        {
            if (!ReferenceEquals(_refreshCompletion, completion) || cancellation.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellation.Token);
                return;
            }

            Error = null;
            Status = MinecraftSessionStatus.Connected;
            completion.TrySetResult(account);
        }
    }

    private void SetFailureIfCurrent(
        long generation,
        CancellationTokenSource cancellation,
        TaskCompletionSource<AccountSession> completion,
        AuthErrorKind error)
    {
        var isCurrent = IsCurrentGeneration(generation);
        lock (_sync)
        {
            if (!ReferenceEquals(_refreshCompletion, completion))
            {
                completion.TrySetCanceled(cancellation.Token);
                return;
            }

            if (isCurrent && !cancellation.IsCancellationRequested)
            {
                Error = error;
                Status = MinecraftSessionStatus.Failed;
                completion.TrySetException(new AuthServiceException(error));
            }
            else
            {
                Status = MinecraftSessionStatus.Idle;
                Error = null;
                completion.TrySetCanceled(cancellation.Token);
            }
        }
    }

    private void SetIdleIfCurrentRefresh(CancellationTokenSource cancellation, TaskCompletionSource<AccountSession> completion)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_refreshCompletion, completion) && ReferenceEquals(_refreshCancellation, cancellation))
            {
                Status = MinecraftSessionStatus.Idle;
                Error = null;
            }
        }
    }

    private void OnAccountGenerationChanged(object? sender, EventArgs args) => CancelRefresh();

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        lock (_sync)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
        }

        var handlers = PropertyChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (PropertyChangedEventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
            catch (Exception)
            {
            }
        }
    }
}

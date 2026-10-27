using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Hako.Core;

public sealed class HakoAccountCoordinator : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan VerificationThrottle = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan VerificationInterval = TimeSpan.FromHours(24);

    private readonly IHakoAuthService? _authService;
    private readonly IHakoSessionStore? _sessionStore;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private HakoSession? _session;
    private HakoSessionStatus _sessionStatus = HakoSessionStatus.SignedOut;
    private HakoAttemptPhase _attemptPhase = HakoAttemptPhase.Idle;
    private HakoAuthorizationMode _authorizationMode;
    private HakoAuthErrorKind? _attemptError;
    private HakoAuthErrorKind? _verificationError;
    private Uri? _botLink;
    private DateTimeOffset? _pendingExpiresAt;
    private bool _requiresFreshLogin;
    private bool _disposed;
    private long _attemptGeneration;
    private long _sessionGeneration;
    private long _verificationGeneration;
    private CancellationTokenSource? _activeAttempt;
    private CancellationTokenSource? _activeVerification;
    private Task? _verificationTask;
    private DateTimeOffset? _lastVerificationAttemptAt;

    public HakoAccountCoordinator(
        IHakoAuthService? authService = null,
        IHakoSessionStore? sessionStore = null,
        TimeProvider? timeProvider = null)
    {
        _authService = authService;
        _sessionStore = sessionStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public HakoSession? Session => _session;
    public HakoSessionStatus SessionStatus => _sessionStatus;
    public HakoAttemptPhase AttemptPhase => _attemptPhase;
    public HakoAuthorizationMode AuthorizationMode => _authorizationMode;
    public HakoAuthErrorKind? AttemptError => _attemptError;
    public HakoAuthErrorKind? VerificationError => _verificationError;
    public Uri? BotLink => _botLink;
    public DateTimeOffset? PendingExpiresAt => _pendingExpiresAt;
    public bool RequiresFreshLogin => _requiresFreshLogin;
    public TimeSpan PendingTimeRemaining
    {
        get
        {
            if (_pendingExpiresAt is not { } expiresAt)
            {
                return TimeSpan.Zero;
            }

            var remaining = expiresAt - _timeProvider.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
    public bool IsAvailable => _authService is not null && _sessionStore is not null;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_sessionStore is null)
        {
            return;
        }

        var loaded = await _sessionStore.LoadAsync(cancellationToken);
        lock (_sync)
        {
            ThrowIfDisposed();
            SetSessionLocked(loaded);
            SetSessionStatusLocked(loaded is null
                ? HakoSessionStatus.SignedOut
                : loaded.ExpiresAt <= _timeProvider.GetUtcNow()
                    ? HakoSessionStatus.Expired
                    : HakoSessionStatus.Unverified);
        }
    }

    public Task BeginLoginAsync(string username, CancellationToken cancellationToken = default)
    {
        if (!HakoUsername.TryNormalize(username, out var normalizedUsername))
        {
            throw new HakoAuthException(HakoAuthErrorKind.InvalidUsername);
        }

        return StartAttemptAsync(HakoAuthorizationMode.Login, normalizedUsername, cancellationToken);
    }

    public Task BeginRegistrationAsync(CancellationToken cancellationToken = default) =>
        StartAttemptAsync(HakoAuthorizationMode.Register, null, cancellationToken);

    public void CancelAttempt()
    {
        lock (_sync)
        {
            InvalidateAttemptLocked();
            ClearPendingRequestLocked();
            SetAttemptErrorLocked(null);
            SetAttemptPhaseLocked(HakoAttemptPhase.Idle);
        }
    }

    private Task StartAttemptAsync(
        HakoAuthorizationMode mode,
        string? username,
        CancellationToken cancellationToken)
    {
        if (_authService is null || _sessionStore is null)
        {
            throw new HakoAuthException(HakoAuthErrorKind.ServiceUnavailable);
        }

        CancellationTokenSource attempt;
        long generation;
        lock (_sync)
        {
            ThrowIfDisposed();
            InvalidateAttemptLocked();
            CancelVerificationLocked();
            attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeAttempt = attempt;
            generation = _attemptGeneration;
            _authorizationMode = mode;
            _requiresFreshLogin = false;
            ClearPendingRequestLocked();
            SetAttemptErrorLocked(null);
            SetAttemptPhaseLocked(HakoAttemptPhase.Requesting);
        }

        return RunAttemptAsync(generation, mode, username, attempt);
    }

    private async Task RunAttemptAsync(
        long generation,
        HakoAuthorizationMode mode,
        string? username,
        CancellationTokenSource attempt)
    {
        var cancellationToken = attempt.Token;
        var exchangeStarted = false;
        string? issuedToken = null;
        try
        {
            var deviceName = GetDeviceName();
            var request = mode == HakoAuthorizationMode.Register
                ? await _authService!.RegisterAsync(deviceName, cancellationToken)
                : await _authService!.LoginAsync(username!, deviceName, cancellationToken);
            if (!TryUpdateAttempt(generation, attempt, () =>
                {
                    SetBotLinkLocked(request.BotLink);
                    SetPendingExpiresAtLocked(request.ExpiresAt);
                    SetAttemptPhaseLocked(HakoAttemptPhase.Pending);
                }))
            {
                return;
            }

            var effectiveExpiresAt = request.ExpiresAt;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = _timeProvider.GetUtcNow();
                if (now >= effectiveExpiresAt)
                {
                    throw new HakoAuthException(HakoAuthErrorKind.RequestExpired);
                }

                var delay = effectiveExpiresAt - now < PollInterval ? effectiveExpiresAt - now : PollInterval;
                await Task.Delay(delay, _timeProvider, cancellationToken);
                if (_timeProvider.GetUtcNow() >= effectiveExpiresAt)
                {
                    throw new HakoAuthException(HakoAuthErrorKind.RequestExpired);
                }

                var status = await _authService.GetRequestStatusAsync(request, cancellationToken);
                if (status.ExpiresAt < effectiveExpiresAt)
                {
                    effectiveExpiresAt = status.ExpiresAt;
                }
                if (!TryUpdateAttempt(generation, attempt, () => SetPendingExpiresAtLocked(
                    effectiveExpiresAt)))
                {
                    return;
                }

                switch (status.Status)
                {
                    case HakoRequestStatus.Pending:
                        continue;
                    case HakoRequestStatus.Approved:
                        goto Approved;
                    case HakoRequestStatus.Denied:
                        throw new HakoAuthException(HakoAuthErrorKind.AuthorizationDenied, isKnownServerError: true);
                    case HakoRequestStatus.Expired:
                        throw new HakoAuthException(HakoAuthErrorKind.RequestExpired, isKnownServerError: true);
                    case HakoRequestStatus.AccountExists:
                        throw new HakoAuthException(HakoAuthErrorKind.AccountExists, isKnownServerError: true);
                    case HakoRequestStatus.Consumed:
                        throw new HakoAuthException(HakoAuthErrorKind.RequestConsumed, isKnownServerError: true);
                    default:
                        throw new HakoAuthException(HakoAuthErrorKind.InvalidResponse);
                }
            }

        Approved:
            if (!TryUpdateAttempt(generation, attempt, () => SetAttemptPhaseLocked(HakoAttemptPhase.Exchanging)))
            {
                return;
            }

            exchangeStarted = true;
            var exchange = await _authService!.ExchangeAsync(request, cancellationToken);
            issuedToken = exchange.Token;
            if (exchange.TokenExpiresAt <= _timeProvider.GetUtcNow())
            {
                await BestEffortRevokeAsync(issuedToken);
                issuedToken = null;
                throw new HakoAuthException(HakoAuthErrorKind.InvalidResponse);
            }

            var session = new HakoSession(exchange.Token, exchange.Account, deviceName, exchange.TokenExpiresAt, _timeProvider.GetUtcNow());
            bool saved;
            try
            {
                saved = await _sessionStore!.SaveAsync(session, commit =>
                {
                    lock (_sync)
                    {
                        if (!IsCurrentAttempt(generation, attempt))
                        {
                            return false;
                        }

                        commit();
                        SetSessionLocked(session);
                        SetSessionStatusLocked(HakoSessionStatus.Verified);
                        SetVerificationErrorLocked(null);
                        _lastVerificationAttemptAt = _timeProvider.GetUtcNow();
                        ClearPendingRequestLocked();
                        SetAttemptErrorLocked(null);
                        SetAttemptPhaseLocked(HakoAttemptPhase.Idle);
                        return true;
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await BestEffortRevokeAsync(issuedToken);
                throw;
            }
            catch (Exception exception) when (exception is HakoSessionStoreException or IOException or UnauthorizedAccessException)
            {
                await BestEffortRevokeAsync(issuedToken);
                throw new HakoAuthException(HakoAuthErrorKind.Persistence, innerException: exception);
            }

            if (!saved)
            {
                await BestEffortRevokeAsync(issuedToken);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (HakoAuthException exception)
        {
            SetAttemptFailure(generation, attempt, exception.Kind, requiresFreshLogin: exchangeStarted);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            SetAttemptFailure(generation, attempt,
                exception is TaskCanceledException ? HakoAuthErrorKind.Timeout : HakoAuthErrorKind.Network,
                requiresFreshLogin: exchangeStarted);
        }
        catch (HakoSessionStoreException)
        {
            if (issuedToken is not null)
            {
                await BestEffortRevokeAsync(issuedToken);
            }

            SetAttemptFailure(generation, attempt, HakoAuthErrorKind.Persistence);
        }
        catch (Exception)
        {
            SetAttemptFailure(generation, attempt, HakoAuthErrorKind.InvalidResponse, requiresFreshLogin: exchangeStarted);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeAttempt, attempt))
                {
                    _activeAttempt = null;
                }
            }

            attempt.Dispose();
        }
    }

    public Task VerifyIfNeededAsync(bool manual = false, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource completion;
        HakoSession session;
        long sessionGeneration;
        long verificationGeneration;
        CancellationTokenSource verification;
        lock (_sync)
        {
            if (_disposed || _session is null || _authService is null || _sessionStore is null)
            {
                return Task.CompletedTask;
            }

            if (_verificationTask is not null)
            {
                return _verificationTask;
            }

            var now = _timeProvider.GetUtcNow();
            if (!manual)
            {
                if (_lastVerificationAttemptAt is { } lastAttempt && now - lastAttempt < VerificationThrottle)
                {
                    return Task.CompletedTask;
                }

                if (_session.ExpiresAt > now && _session.VerifiedAt is { } verifiedAt
                    && now - verifiedAt < VerificationInterval)
                {
                    return Task.CompletedTask;
                }
            }

            session = _session;
            sessionGeneration = _sessionGeneration;
            verificationGeneration = ++_verificationGeneration;
            verification = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeVerification = verification;
            _lastVerificationAttemptAt = now;
            _verificationError = null;
            _sessionStatus = HakoSessionStatus.Verifying;
            NotifyLocked(nameof(VerificationError));
            NotifyLocked(nameof(SessionStatus));
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _verificationTask = completion.Task;
        }

        _ = RunVerificationAsync(session, sessionGeneration, verificationGeneration, verification, completion);
        return completion.Task;
    }

    private async Task RunVerificationAsync(
        HakoSession expectedSession,
        long sessionGeneration,
        long verificationGeneration,
        CancellationTokenSource verification,
        TaskCompletionSource completion)
    {
        try
        {
            var result = await _authService!.GetMeAsync(expectedSession.Token, verification.Token);
            if (result.SessionExpiresAt <= _timeProvider.GetUtcNow())
            {
                throw new HakoAuthException(HakoAuthErrorKind.InvalidResponse);
            }

            var renewed = expectedSession with
            {
                Account = result.Account,
                DeviceName = result.DeviceName,
                ExpiresAt = result.SessionExpiresAt,
                VerifiedAt = _timeProvider.GetUtcNow()
            };
            var saved = await _sessionStore!.SaveAsync(renewed, commit =>
            {
                lock (_sync)
                {
                    if (!IsCurrentVerification(expectedSession, sessionGeneration, verificationGeneration, verification))
                    {
                        return false;
                    }

                    commit();
                    SetSessionLocked(renewed);
                    SetSessionStatusLocked(HakoSessionStatus.Verified);
                    SetVerificationErrorLocked(null);
                    return true;
                }
            }, verification.Token);

            if (!saved)
            {
                SetVerificationFailure(expectedSession, sessionGeneration, verificationGeneration, verification, HakoAuthErrorKind.Persistence);
            }
        }
        catch (OperationCanceledException) when (verification.IsCancellationRequested)
        {
        }
        catch (HakoAuthException exception) when (exception.Kind == HakoAuthErrorKind.Unauthorized)
        {
            await ClearUnauthorizedSessionAsync(expectedSession, sessionGeneration, verificationGeneration, verification);
        }
        catch (HakoAuthException exception)
        {
            SetVerificationFailure(expectedSession, sessionGeneration, verificationGeneration, verification, exception.Kind);
        }
        catch (Exception exception) when (exception is HakoSessionStoreException or IOException or UnauthorizedAccessException)
        {
            SetVerificationFailure(expectedSession, sessionGeneration, verificationGeneration, verification, HakoAuthErrorKind.Persistence);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            SetVerificationFailure(expectedSession, sessionGeneration, verificationGeneration, verification,
                exception is TaskCanceledException ? HakoAuthErrorKind.Timeout : HakoAuthErrorKind.Network);
        }
        catch (Exception)
        {
            SetVerificationFailure(expectedSession, sessionGeneration, verificationGeneration, verification, HakoAuthErrorKind.InvalidResponse);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeVerification, verification))
                {
                    _activeVerification = null;
                    _verificationTask = null;
                }
            }

            verification.Dispose();
            completion.TrySetResult();
        }
    }

    private async Task ClearUnauthorizedSessionAsync(
        HakoSession expectedSession,
        long sessionGeneration,
        long verificationGeneration,
        CancellationTokenSource verification)
    {
        try
        {
            var cleared = await _sessionStore!.ClearAsync(commit =>
            {
                lock (_sync)
                {
                    if (!IsCurrentVerification(expectedSession, sessionGeneration, verificationGeneration, verification))
                    {
                        return false;
                    }

                    commit();
                    SetSessionLocked(null);
                    SetSessionStatusLocked(HakoSessionStatus.SignedOut);
                    SetVerificationErrorLocked(null);
                    return true;
                }
            }, verification.Token);
            if (!cleared)
            {
                SetVerificationFailure(expectedSession, sessionGeneration, verificationGeneration, verification, HakoAuthErrorKind.Persistence);
            }
        }
        catch (Exception exception) when (exception is HakoSessionStoreException or IOException or UnauthorizedAccessException)
        {
            SetVerificationFailure(expectedSession, sessionGeneration, verificationGeneration, verification, HakoAuthErrorKind.Persistence);
        }
    }

    public async Task<HakoLogoutResult> LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (_session is null)
        {
            return HakoLogoutResult.SignedOut;
        }

        HakoSession expected;
        long sessionGeneration;
        lock (_sync)
        {
            if (_session is null)
            {
                return HakoLogoutResult.SignedOut;
            }

            expected = _session;
            sessionGeneration = _sessionGeneration;
            InvalidateAttemptLocked();
            CancelVerificationLocked();
            SetAttemptPhaseLocked(HakoAttemptPhase.Idle);
            SetAttemptErrorLocked(null);
            ClearPendingRequestLocked();
        }

        try
        {
            await _authService!.LogoutAsync(expected.Token, cancellationToken);
        }
        catch (HakoAuthException exception) when (exception.Kind == HakoAuthErrorKind.Unauthorized)
        {
            // A revoked or expired server token is already logged out.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HakoAuthException or HttpRequestException or TaskCanceledException)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_session, expected) && _sessionGeneration == sessionGeneration)
                {
                    SetSessionStatusLocked(HakoSessionStatus.LogoutFailed);
                    SetVerificationErrorLocked(exception is HakoAuthException auth ? auth.Kind :
                        exception is TaskCanceledException ? HakoAuthErrorKind.Timeout : HakoAuthErrorKind.Network);
                }
            }

            return HakoLogoutResult.RemoteLogoutFailed;
        }

        return await ClearLocalSessionAsync(expected, sessionGeneration, cancellationToken)
            ? HakoLogoutResult.SignedOut
            : HakoLogoutResult.LocalClearFailed;
    }

    public async Task<bool> ClearLocalSessionAsync(CancellationToken cancellationToken = default)
    {
        HakoSession? expected;
        long sessionGeneration;
        lock (_sync)
        {
            expected = _session;
            if (expected is null)
            {
                return true;
            }

            sessionGeneration = _sessionGeneration;
            InvalidateAttemptLocked();
            CancelVerificationLocked();
        }

        return await ClearLocalSessionAsync(expected, sessionGeneration, cancellationToken);
    }

    private async Task<bool> ClearLocalSessionAsync(
        HakoSession expected,
        long sessionGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _sessionStore!.ClearAsync(commit =>
            {
                lock (_sync)
                {
                    if (!ReferenceEquals(_session, expected) || _sessionGeneration != sessionGeneration)
                    {
                        return false;
                    }

                    commit();
                    SetSessionLocked(null);
                    SetSessionStatusLocked(HakoSessionStatus.SignedOut);
                    SetVerificationErrorLocked(null);
                    return true;
                }
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is HakoSessionStoreException or IOException or UnauthorizedAccessException)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_session, expected) && _sessionGeneration == sessionGeneration)
                {
                    SetSessionStatusLocked(HakoSessionStatus.LogoutFailed);
                    SetVerificationErrorLocked(HakoAuthErrorKind.Persistence);
                }
            }

            return false;
        }
    }

    private void SetVerificationFailure(
        HakoSession expectedSession,
        long sessionGeneration,
        long verificationGeneration,
        CancellationTokenSource verification,
        HakoAuthErrorKind error)
    {
        lock (_sync)
        {
            if (!IsCurrentVerification(expectedSession, sessionGeneration, verificationGeneration, verification))
            {
                return;
            }

            SetVerificationErrorLocked(error);
            SetSessionStatusLocked(expectedSession.ExpiresAt <= _timeProvider.GetUtcNow()
                ? HakoSessionStatus.Expired
                : HakoSessionStatus.Unverified);
        }
    }

    private bool TryUpdateAttempt(long generation, CancellationTokenSource attempt, Action update)
    {
        lock (_sync)
        {
            if (!IsCurrentAttempt(generation, attempt))
            {
                return false;
            }

            update();
            return true;
        }
    }

    private void SetAttemptFailure(
        long generation,
        CancellationTokenSource attempt,
        HakoAuthErrorKind error,
        bool requiresFreshLogin = false)
    {
        lock (_sync)
        {
            if (!IsCurrentAttempt(generation, attempt))
            {
                return;
            }

            ClearPendingRequestLocked(clearFreshLogin: false);
            SetPropertyLocked(ref _requiresFreshLogin, requiresFreshLogin, nameof(RequiresFreshLogin));
            SetAttemptErrorLocked(error);
            SetAttemptPhaseLocked(HakoAttemptPhase.Failed);
        }
    }

    private bool IsCurrentAttempt(long generation, CancellationTokenSource attempt) =>
        generation == _attemptGeneration
        && ReferenceEquals(_activeAttempt, attempt)
        && !attempt.IsCancellationRequested;

    private bool IsCurrentVerification(
        HakoSession expectedSession,
        long sessionGeneration,
        long verificationGeneration,
        CancellationTokenSource verification) =>
        sessionGeneration == _sessionGeneration
        && verificationGeneration == _verificationGeneration
        && ReferenceEquals(_session, expectedSession)
        && ReferenceEquals(_activeVerification, verification)
        && !verification.IsCancellationRequested;

    private async Task BestEffortRevokeAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || _authService is null)
        {
            return;
        }

        try
        {
            await _authService.LogoutAsync(token, CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    private static string GetDeviceName()
    {
        var value = Environment.MachineName.Trim();
        if (value.Length == 0)
        {
            return "Hako";
        }

        return value.Length <= 128 ? value : value[..128].TrimEnd();
    }

    private void InvalidateAttemptLocked()
    {
        _attemptGeneration++;
        _activeAttempt?.Cancel();
        _activeAttempt = null;
    }

    private void CancelVerificationLocked()
    {
        _verificationGeneration++;
        _activeVerification?.Cancel();
        _activeVerification = null;
        _verificationTask = null;
        if (_session is not null)
        {
            SetSessionStatusLocked(_session.ExpiresAt <= _timeProvider.GetUtcNow()
                ? HakoSessionStatus.Expired
                : HakoSessionStatus.Unverified);
        }
    }

    private void SetSessionLocked(HakoSession? value)
    {
        if (ReferenceEquals(_session, value))
        {
            return;
        }

        _session = value;
        _sessionGeneration++;
        NotifyLocked(nameof(Session));
    }

    private void SetSessionStatusLocked(HakoSessionStatus value) => SetPropertyLocked(ref _sessionStatus, value);
    private void SetAttemptPhaseLocked(HakoAttemptPhase value) => SetPropertyLocked(ref _attemptPhase, value);
    private void SetAttemptErrorLocked(HakoAuthErrorKind? value) => SetPropertyLocked(ref _attemptError, value);
    private void SetVerificationErrorLocked(HakoAuthErrorKind? value) => SetPropertyLocked(ref _verificationError, value);
    private void SetBotLinkLocked(Uri? value) => SetPropertyLocked(ref _botLink, value);
    private void SetPendingExpiresAtLocked(DateTimeOffset? value) => SetPropertyLocked(ref _pendingExpiresAt, value);

    private void ClearPendingRequestLocked(bool clearFreshLogin = true)
    {
        SetBotLinkLocked(null);
        SetPendingExpiresAtLocked(null);
        if (clearFreshLogin)
        {
            SetPropertyLocked(ref _requiresFreshLogin, false, nameof(RequiresFreshLogin));
        }
    }

    private void SetPropertyLocked<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        NotifyLocked(propertyName!);
    }

    private void NotifyLocked(string propertyName)
    {
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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            InvalidateAttemptLocked();
            CancelVerificationLocked();
        }

        if (_authService is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

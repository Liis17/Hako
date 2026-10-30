using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Hako.Core.Playtime;

public interface IHakoPlaytimeService
{
    Task SubmitPlaytimeAsync(string token, Guid sessionId, long durationMs, CancellationToken cancellationToken);
    Task<long> GetPlaytimeAsync(string token, CancellationToken cancellationToken);
}

internal sealed record HakoPlaytimeSessionDto(
    [property: JsonPropertyName("session_id")] Guid SessionId,
    [property: JsonPropertyName("duration_ms")] long DurationMs);
internal sealed record HakoPlaytimeAckDto([property: JsonPropertyName("session_id"), JsonRequired] Guid SessionId);
internal sealed record HakoPlaytimeTotalDto([property: JsonPropertyName("total_duration_ms"), JsonRequired] long TotalDurationMs);

/// <summary>Serializes durable account boundaries separately from retryable network delivery.</summary>
public sealed class HakoPlaytimeSync : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly PlaytimeCoordinator _playtime;
    private readonly HakoAccountCoordinator _account;
    private readonly IHakoPlaytimeService _api;
    private readonly SemaphoreSlim _networkGate = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Queue<string?> _boundaries = new();
    private readonly SemaphoreSlim _boundaryGate = new(1, 1);
    private Task? _polling;
    private string? _displayAccount;
    private bool _disposed;
    private HashSet<Guid> _seenUploads = [];

    public HakoPlaytimeSync(PlaytimeCoordinator playtime, HakoAccountCoordinator account, IHakoPlaytimeService api)
    {
        _playtime = playtime;
        _account = account;
        _api = api;
        playtime.Hako = this;
        playtime.BeforeSessionStart = WaitForBoundaryAsync;
        account.PropertyChanged += AccountChanged;
        playtime.PropertyChanged += PlaytimeChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public long? TotalDurationMs { get; private set; }
    public long PendingDurationMs { get; private set; }
    public bool HasError { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        QueueBoundary();
        try { await WaitForBoundaryAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { HasError = true; }
        _polling = PollAsync(_stop.Token);
        _ = RefreshAsync();
    }

    private void AccountChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HakoAccountCoordinator.Session)) return;
        if (_displayAccount != _account.Session?.Account.Id)
        {
            TotalDurationMs = null;
            PendingDurationMs = 0;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TotalDurationMs)));
        }
        QueueBoundary();
        _ = ApplyBoundaryAndRefreshAsync();
    }

    private async Task ApplyBoundaryAndRefreshAsync()
    {
        // Authentication raises notifications under its own lock. Apply the durable
        // transition independently of any slow upload, after leaving that notification.
        await Task.Yield();
        try { await WaitForBoundaryAsync(_stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
        catch (Exception)
        {
            HasError = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasError)));
            return; // The queued boundary is retried by refresh or the next launch.
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PendingDurationMs)));
        await RefreshAsync(_stop.Token).ConfigureAwait(false);
    }

    private void QueueBoundary()
    {
        var id = _account.Session?.Account.Id;
        lock (_sync)
        {
            if (_disposed) return;
            _boundaries.Enqueue(id);
        }
    }

    public async Task WaitForBoundaryAsync(CancellationToken cancellationToken = default)
    {
        await _boundaryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                string? id;
                lock (_sync) { if (_boundaries.Count == 0) return; id = _boundaries.Peek(); }
                await _playtime.SetHakoAccountAsync(id, cancellationToken).ConfigureAwait(false);
                lock (_sync) _boundaries.Dequeue();
            }
        }
        finally { _boundaryGate.Release(); }
    }

    private void PlaytimeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaytimeCoordinator.Revision)) _ = RefreshPendingAsync();
    }

    private async Task RefreshPendingAsync()
    {
        if (_disposed) return;
        try
        {
            var id = _account.Session?.Account.Id;
            if (id is null) return;
            var ledger = await _playtime.ReadHakoLedgerAsync(_stop.Token).ConfigureAwait(false);
            if (_account.Session?.Account.Id != id) return;
            PendingDurationMs = PendingMilliseconds(ledger, id);
            var uploads = ledger.Uploads.Values.Where(x => x.AccountId == id).Select(x => x.SessionId).ToHashSet();
            var becameReady = uploads.Except(_seenUploads).Any();
            _seenUploads = uploads;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PendingDurationMs)));
            if (becameReady) await RefreshAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { HasError = true; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasError))); }
    }

    private static long PendingMilliseconds(PlaytimeLedger ledger, string id) =>
        checked(ledger.Uploads.Values.Where(x => x.AccountId == id).Sum(x => x.DurationMs)
            + (long)Math.Floor(ledger.Sessions.Values.Where(x => x.HakoAccountId == id).Sum(x => x.HakoSeconds) * 1000));

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        if (!_networkGate.Wait(0)) return;
        try
        {
            await WaitForBoundaryAsync(linked.Token).ConfigureAwait(false);
            var session = _account.Session;
            var id = session?.Account.Id;
            if (_displayAccount != id)
            {
                _displayAccount = id;
                TotalDurationMs = null;
                PendingDurationMs = 0;
            }
            HasError = false;
            if (session is null) return;
            id = session.Account.Id;
            var ledger = await _playtime.ReadHakoLedgerAsync(linked.Token).ConfigureAwait(false);
            PendingDurationMs = PendingMilliseconds(ledger, id);
            foreach (var upload in ledger.Uploads.Values.Where(x => x.AccountId == id))
            {
                if (_account.Session?.Account.Id != id) return;
                await _api.SubmitPlaytimeAsync(session.Token, upload.SessionId, upload.DurationMs, linked.Token).ConfigureAwait(false);
                await _playtime.AcknowledgeHakoUploadAsync(upload.SessionId, linked.Token).ConfigureAwait(false);
                PendingDurationMs -= upload.DurationMs;
            }
            var total = await _api.GetPlaytimeAsync(session.Token, linked.Token).ConfigureAwait(false);
            if (_account.Session?.Account.Id == id) TotalDurationMs = total;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception) { HasError = true; }
        finally
        {
            // A profile switch can happen while a request using the previous token is in flight.
            if (_displayAccount != _account.Session?.Account.Id)
            {
                TotalDurationMs = null;
                PendingDurationMs = 0;
            }
            _networkGate.Release();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TotalDurationMs)));
            if (!_disposed && _displayAccount != _account.Session?.Account.Id) _ = RefreshAsync();
        }
    }

    private async Task PollAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) await RefreshAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _account.PropertyChanged -= AccountChanged;
        _playtime.PropertyChanged -= PlaytimeChanged;
        _playtime.BeforeSessionStart = null;
        _stop.Cancel();
        if (_polling is not null) await _polling.ConfigureAwait(false);
        await _networkGate.WaitAsync().ConfigureAwait(false);
        _networkGate.Release();
        await _boundaryGate.WaitAsync().ConfigureAwait(false);
        _boundaryGate.Release();
        if (_api is IDisposable disposable) disposable.Dispose();
    }
}

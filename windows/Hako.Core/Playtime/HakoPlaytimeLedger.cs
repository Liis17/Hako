using Hako.Core.Minecraft;

namespace Hako.Core.Playtime;

public sealed partial class PlaytimeCoordinator
{
    // Launches wait for queued identity boundaries before creating their durable recipient.
    public HakoPlaytimeSync? Hako { get; internal set; }

    internal Func<CancellationToken, Task>? BeforeSessionStart { get; set; }

    internal async Task SetHakoAccountAsync(string? accountId, CancellationToken cancellationToken = default)
    {
        if (_preview) return;
        await _sessionStartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReconcileAsync(cancellationToken).ConfigureAwait(false);
            await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var initial = await _ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (initial.HakoAccountId == accountId && accountId is null) return;
                if (initial.PendingTransfer is not null)
                    throw new IOException("A Microsoft playtime transfer is pending.");
                var boundaries = new Dictionary<Guid, double>();
                foreach (var session in initial.Sessions.Values)
                {
                    var path = PlaytimePaths.Journal(_gameRoot, session.SessionID);
                    if (!File.Exists(path)) continue;
                    var journal = await Task.Run(() => PlaytimeJson.ReadJournal(path), cancellationToken).ConfigureAwait(false);
                    var elapsed = journal.ElapsedSeconds;
                    if (!journal.IsFinished && journal.Process is { } process && _processProbe.IsRunning(
                        new PlaytimeProcessIdentity(process.Pid, process.StartSeconds, process.StartMicroseconds)))
                        elapsed = Math.Max(elapsed, journal.BaseElapsedSeconds +
                            PlaytimeHelperHost.Elapsed(journal.StartedUnbiasedTicks, _clock.GetUnbiasedTicks()));
                    boundaries[session.SessionID] = elapsed;
                }
                await _ledger.MutateAsync(ledger =>
                {
                    if (ledger.PendingTransfer is not null) throw new IOException("A Microsoft playtime transfer is pending.");
                    if (ledger.HakoAccountId != accountId)
                    {
                        foreach (var session in ledger.Sessions.Values)
                        {
                            if (boundaries.TryGetValue(session.SessionID, out var elapsed)) Credit(ledger, session, elapsed);
                            QueueHakoSegment(ledger, session);
                            session.HakoAccountId = accountId;
                            session.SegmentId = Guid.NewGuid();
                            session.HakoSeconds = 0;
                        }
                        ledger.HakoAccountId = accountId;
                    }
                    if (accountId is not null)
                    {
                        var seconds = ledger.Owners.Values.Sum(owner => owner.TotalSeconds);
                        if (seconds > 0)
                        {
                            var id = Guid.NewGuid();
                            ledger.Uploads.Add(id, new HakoPlaytimeUpload
                            {
                                SessionId = id, AccountId = accountId, DurationMs = ToMilliseconds(seconds)
                            });
                            // Move the source into the outbox in the same atomic write. It remains
                            // durable until acknowledgement, without depending on Microsoft owner moves.
                            foreach (var owner in ledger.Owners.Values) owner.TotalSeconds = 0;
                        }
                    }
                    return true;
                }, cancellationToken).ConfigureAwait(false);
            }
            finally { _reconcileGate.Release(); }
            await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _sessionStartGate.Release(); }
    }

    internal Task<PlaytimeLedger> ReadHakoLedgerAsync(CancellationToken cancellationToken = default) =>
        _ledger.ReadAsync(cancellationToken);

    internal async Task AcknowledgeHakoUploadAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _ledger.MutateAsync(ledger => ledger.Uploads.Remove(id), cancellationToken).ConfigureAwait(false);

    private static long ToMilliseconds(double seconds) => checked((long)Math.Floor(seconds * 1000));

    private static void QueueHakoSegment(PlaytimeLedger ledger, PlaytimeSessionRecord session)
    {
        if (session.HakoAccountId is not { } account || session.HakoSeconds <= 0) return;
        ledger.Uploads.Add(session.SegmentId, new HakoPlaytimeUpload
        {
            SessionId = session.SegmentId, AccountId = account, DurationMs = ToMilliseconds(session.HakoSeconds)
        });
        session.HakoSeconds = 0;
        session.SegmentId = Guid.NewGuid();
    }
}

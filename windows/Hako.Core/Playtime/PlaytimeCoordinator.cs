using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hako.Core.Minecraft;

namespace Hako.Core.Playtime;

public interface IAccountPlaytimeTransfer
{
    Task<Guid?> PrepareGuestTransferAsync(string targetXuid, CancellationToken cancellationToken = default);
    Task CompleteGuestTransferAsync(Guid transferID, CancellationToken cancellationToken = default);
    Task CancelGuestTransferAsync(Guid transferID, CancellationToken cancellationToken = default);
}

internal enum PlaytimeTransferCheckpoint
{
    PendingWritten,
    BeforeApply,
    Applied
}

public sealed record PlaytimeTrackingRequest(Guid SessionID, string JournalPath, string OwnerKey, ulong StartedUnbiasedTicks);

/// <summary>Shared game-root ledger and session-journal coordinator; journals are checkpointed by Hako.exe's helper.</summary>
public sealed partial class PlaytimeCoordinator : INotifyPropertyChanged, IAccountPlaytimeTransfer, IAsyncDisposable
{
    public static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(10);
    private readonly PlaytimeLedgerStore _ledger;
    private readonly string _gameRoot;
    private readonly IPlaytimeClock _clock;
    private readonly IPlaytimeProcessProbe _processProbe;
    private readonly IPlaytimeHelperLauncher? _helperLauncher;
    private readonly TimeProvider _timeProvider;
    private readonly bool _preview;
    private readonly Action<PlaytimeTransferCheckpoint>? _transferCheckpoint;
    private readonly SemaphoreSlim _sessionStartGate = new(1, 1);
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly HashSet<Guid> _launchingSessions = [];
    private readonly Dictionary<Guid, DateTimeOffset> _helperNextAttempt = [];
    private readonly Dictionary<Guid, GameProcessRecord> _activeRecords = [];
    private readonly object _stateSync = new();
    private Dictionary<string, PlaytimeSnapshot> _owners = new(StringComparer.Ordinal);
    private CancellationTokenSource? _pollingCancellation;
    private Task? _pollingTask;
    private string? _errorResourceKey;
    private string? _errorDetail;
    private long _revision;
    private HashSet<Guid> _uploadIds = [];

    public PlaytimeCoordinator(string gameRoot, IPlaytimeHelperLauncher? helperLauncher = null,
        IPlaytimeClock? clock = null, IPlaytimeProcessProbe? processProbe = null,
        TimeProvider? timeProvider = null)
        : this(gameRoot, helperLauncher, clock, processProbe, timeProvider, preview: false, transferCheckpoint: null)
    {
    }

    public static PlaytimeCoordinator CreatePreview(string gameRoot) =>
        new(gameRoot, helperLauncher: null, clock: null, processProbe: null, timeProvider: null,
            preview: true, transferCheckpoint: null);

    internal void SeedPreviewInstance(Guid instanceID)
    {
        if (!_preview || instanceID == Guid.Empty) return;
        lock (_stateSync)
        {
            _owners["account:preview-xuid"] = new PlaytimeSnapshot(12_345,
                new Dictionary<Guid, double> { [instanceID] = 3_726 });
        }
    }

    internal PlaytimeCoordinator(string gameRoot, IPlaytimeHelperLauncher? helperLauncher,
        IPlaytimeClock? clock, IPlaytimeProcessProbe? processProbe, TimeProvider? timeProvider,
        bool preview, Action<PlaytimeTransferCheckpoint>? transferCheckpoint)
    {
        _gameRoot = Path.GetFullPath(gameRoot);
        _ledger = new PlaytimeLedgerStore(_gameRoot);
        _helperLauncher = helperLauncher;
        _clock = clock ?? new WindowsPlaytimeClock();
        _processProbe = processProbe ?? new WindowsPlaytimeProcessProbe();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _preview = preview;
        _transferCheckpoint = transferCheckpoint;
        if (preview)
        {
            _owners["account:preview-xuid"] = new PlaytimeSnapshot(12_345, new Dictionary<Guid, double>());
            _owners["guest"] = new PlaytimeSnapshot(4_321, new Dictionary<Guid, double>());
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? ErrorResourceKey { get { lock (_stateSync) return _errorResourceKey; } }
    public string? ErrorDetail { get { lock (_stateSync) return _errorDetail; } }
    public long Revision { get { lock (_stateSync) return _revision; } }

    public double TotalSeconds(string? xuid) => GetSnapshot(OwnerKey(xuid)).TotalSeconds;

    public double InstanceSeconds(Guid instanceID, string? xuid) =>
        GetSnapshot(OwnerKey(xuid)).Instances.GetValueOrDefault(instanceID);

    public async Task InitializeAsync(string? currentXuid, CancellationToken cancellationToken = default)
    {
        if (_preview) return;
        await RecoverPendingTransferAsync(currentXuid, cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Start()
    {
        if (_preview || _pollingCancellation is not null) return;
        _pollingCancellation = new CancellationTokenSource();
        _pollingTask = PollAsync(_pollingCancellation.Token);
    }

    public async Task StopAsync()
    {
        var cancellation = _pollingCancellation;
        if (cancellation is null) return;
        _pollingCancellation = null;
        cancellation.Cancel();
        try { if (_pollingTask is not null) await _pollingTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        cancellation.Dispose();
        _pollingTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (Hako is not null) await Hako.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Includes helpers and sessions whose build has been removed from the instance registry.</summary>
    public async Task<bool> HasActiveProcessesAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateSync)
        {
            if (_launchingSessions.Count != 0) return true;
        }
        var ledger = await _ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var session in ledger.Sessions.Values)
        {
            var path = PlaytimePaths.Journal(_gameRoot, session.SessionID);
            if (!File.Exists(path)) continue;
            var journal = await Task.Run(() => PlaytimeJson.ReadJournal(path), cancellationToken).ConfigureAwait(false);
            if (journal.Helper is { } helper && _processProbe.IsRunning(helper)) return true;
            if (journal.Process is { } process && _processProbe.IsRunning(
                new PlaytimeProcessIdentity(process.Pid, process.StartSeconds, process.StartMicroseconds))) return true;
        }
        return false;
    }

    public async Task<PlaytimeTrackingRequest?> BeginSessionAsync(Guid instanceID, string? xuid,
        CancellationToken cancellationToken = default)
    {
        if (_preview || instanceID == Guid.Empty) return null;
        if (BeforeSessionStart is { } beforeStart) await beforeStart(cancellationToken).ConfigureAwait(false);
        await _sessionStartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var sessionID = Guid.NewGuid();
        var ownerKey = OwnerKey(xuid);
        lock (_stateSync) _launchingSessions.Add(sessionID);
        try
        {
            try
            {
                await _ledger.MutateAsync(ledger =>
                {
                    ledger.Sessions.Add(sessionID, new PlaytimeSessionRecord
                    {
                        SessionID = sessionID,
                        InstanceID = instanceID,
                        OwnerKey = ownerKey,
                        HakoAccountId = ledger.HakoAccountId
                    });
                    return true;
                }, cancellationToken).ConfigureAwait(false);

                var started = _clock.GetUnbiasedTicks();
                var journalPath = PlaytimePaths.Journal(_gameRoot, sessionID);
                var journal = new PlaytimeSessionJournal
                {
                    SessionID = sessionID,
                    InstanceID = instanceID,
                    OwnerKey = ownerKey,
                    StartedUnbiasedTicks = started
                };
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(PlaytimePaths.JournalDirectory(_gameRoot));
                    using var journalLock = PlaytimeJournalLock.TryAcquire(PlaytimePaths.JournalLock(_gameRoot, sessionID))
                        ?? throw new IOException("The playtime session journal is already locked.");
                    PlaytimeJson.AtomicWrite(journalPath, PlaytimeJson.SerializeJournal(journal));
                }, cancellationToken).ConfigureAwait(false);
                return new PlaytimeTrackingRequest(sessionID, journalPath, ownerKey, started);
            }
            catch (OperationCanceledException)
            {
                await CleanupStartingSessionAsync(sessionID).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                await CleanupStartingSessionAsync(sessionID).ConfigureAwait(false);
                SetError("PlaytimeStorageError", exception.Message);
                return null;
            }
        }
        finally { _sessionStartGate.Release(); }
    }

    private async Task CleanupStartingSessionAsync(Guid sessionID)
    {
        lock (_stateSync) _launchingSessions.Remove(sessionID);
        try { await _ledger.MutateAsync(ledger => ledger.Sessions.Remove(sessionID), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { SetError("PlaytimeStorageError", exception.Message); }
        DeleteJournalFiles(sessionID);
    }

    /// <summary>Attaches a durable journal to the exact process recorded by the game runner and starts its observer.</summary>
    public async Task AttachProcessAsync(PlaytimeTrackingRequest tracking, GameProcessRecord process,
        CancellationToken cancellationToken = default)
    {
        if (_preview || process.SessionID != tracking.SessionID || process.InstanceID == Guid.Empty) return;
        await _sessionStartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = await Task.Run(() => UpdateJournal(tracking.JournalPath, tracking.SessionID, journal =>
            {
                if (journal.IsFinished || journal.InstanceID != process.InstanceID)
                    throw new JsonException("The playtime session journal is invalid.");
                journal.Process = process;
                journal.Helper = null;
                journal.StartedUnbiasedTicks = _clock.GetUnbiasedTicks();
                journal.BaseElapsedSeconds = 0;
                journal.ElapsedSeconds = 0;
                return true;
            }), cancellationToken).ConfigureAwait(false);
            if (updated?.Process != process) throw new JsonException("The playtime session journal could not be attached to the game process.");
            lock (_stateSync)
            {
                _launchingSessions.Remove(tracking.SessionID);
                _activeRecords[tracking.SessionID] = process;
            }

            await TryStartHelperAsync(tracking.JournalPath, cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { SetError("PlaytimeStorageError", exception.Message); }
        finally
        {
            lock (_stateSync) _launchingSessions.Remove(tracking.SessionID);
            _sessionStartGate.Release();
        }
    }

    /// <summary>Restores an already running process without creating a second session for its saved SessionID.</summary>
    public async Task<GameProcessRecord> ResumeSessionAsync(GameProcessRecord process, string? currentXuid,
        CancellationToken cancellationToken = default)
    {
        if (_preview) return process;
        var sessionID = process.SessionID ?? LegacySessionID(process);
        var tracked = process with { SessionID = sessionID };
        var ownerKey = OwnerKey(currentXuid);
        var creditedSeconds = 0d;
        try
        {
            var journalPath = PlaytimePaths.Journal(_gameRoot, sessionID);
            var existingJournal = File.Exists(journalPath)
                ? await Task.Run(() => PlaytimeJson.ReadJournal(journalPath), cancellationToken).ConfigureAwait(false)
                : null;
            if (existingJournal is not null)
            {
                if (existingJournal.SessionID != sessionID || existingJournal.InstanceID != tracked.InstanceID)
                    throw new JsonException("The saved playtime session is invalid.");
                ownerKey = existingJournal.OwnerKey;
            }

            await _ledger.MutateAsync(ledger =>
            {
                if (!ledger.Sessions.TryGetValue(sessionID, out var session))
                {
                    ledger.Sessions.Add(sessionID, new PlaytimeSessionRecord
                    {
                        SessionID = sessionID,
                        InstanceID = tracked.InstanceID,
                        OwnerKey = ownerKey,
                        HakoAccountId = ledger.HakoAccountId
                    });
                }
                else if (session.InstanceID != tracked.InstanceID)
                {
                    throw new JsonException("The running game record refers to a different playtime session.");
                }
                else
                {
                    ownerKey = session.OwnerKey;
                    creditedSeconds = session.CreditedSeconds;
                }

                return true;
            }, cancellationToken).ConfigureAwait(false);

            if (existingJournal?.Helper is { } existingHelper
                && _processProbe.IsRunning(existingHelper))
            {
                if (existingJournal.Process is not { } existingProcess
                    || !SameProcess(existingProcess, tracked))
                    throw new JsonException("A live helper belongs to a different game process.");
                lock (_stateSync) _activeRecords[sessionID] = tracked;
                return tracked;
            }

            await Task.Run(() =>
            {
                Directory.CreateDirectory(PlaytimePaths.JournalDirectory(_gameRoot));
                if (File.Exists(journalPath))
                {
                    UpdateJournal(journalPath, sessionID, journal =>
                    {
                        if (journal.InstanceID != tracked.InstanceID || journal.IsFinished)
                            throw new JsonException("The saved playtime session is invalid.");
                        if (journal.Process is { } existingProcess && !SameProcess(existingProcess, tracked))
                            throw new JsonException("The saved playtime session belongs to a different game process.");
                        journal.Process = tracked;
                        journal.Helper = null;
                        return true;
                    });
                    return;
                }

                var journal = new PlaytimeSessionJournal
                {
                    SessionID = sessionID,
                    InstanceID = tracked.InstanceID,
                    OwnerKey = ownerKey,
                    Process = tracked,
                    StartedUnbiasedTicks = _clock.GetUnbiasedTicks(),
                    BaseElapsedSeconds = creditedSeconds,
                    ElapsedSeconds = creditedSeconds
                };
                using var journalLock = PlaytimeJournalLock.TryAcquire(PlaytimePaths.JournalLock(_gameRoot, sessionID))
                    ?? throw new IOException("The playtime session journal is already locked.");
                PlaytimeJson.AtomicWrite(journalPath, PlaytimeJson.SerializeJournal(journal));
            }, cancellationToken).ConfigureAwait(false);

            lock (_stateSync) _activeRecords[sessionID] = tracked;
            await TryStartHelperAsync(journalPath, cancellationToken).ConfigureAwait(false);
            return tracked;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            SetError("PlaytimeStorageError", exception.Message);
            return process;
        }
    }

    public async Task CancelSessionAsync(Guid sessionID, CancellationToken cancellationToken = default)
    {
        if (_preview) return;
        lock (_stateSync)
        {
            _launchingSessions.Remove(sessionID);
            _activeRecords.Remove(sessionID);
        }

        try
        {
            var path = PlaytimePaths.Journal(_gameRoot, sessionID);
            if (File.Exists(path))
            {
                var journal = await Task.Run(() => PlaytimeJson.ReadJournal(path), cancellationToken).ConfigureAwait(false);
                if (journal.Process is not null)
                {
                    // Cancellation of a launch cannot discard time once a game has been attached.
                    await ReconcileAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                await Task.Run(() => UpdateJournal(path, sessionID, journal =>
                {
                    journal.IsFinished = true;
                    return true;
                }), cancellationToken).ConfigureAwait(false);
            }

            await _ledger.MutateAsync(ledger =>
            {
                if (ledger.Sessions.TryGetValue(sessionID, out var session)) QueueHakoSegment(ledger, session);
                return ledger.Sessions.Remove(sessionID);
            }, cancellationToken).ConfigureAwait(false);
            DeleteJournalFiles(sessionID);
            await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { SetError("PlaytimeStorageError", exception.Message); }
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (_preview) return;
        await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = PlaytimePaths.JournalDirectory(_gameRoot);
            if (!Directory.Exists(directory))
            {
                await RemoveOrphanSessionsAsync([], cancellationToken).ConfigureAwait(false);
                await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
                ClearErrorIf("PlaytimeStorageError");
                return;
            }

            if (!await Task.Run(() => PlaytimePaths.HasNoReparsePoints(directory), cancellationToken).ConfigureAwait(false))
            {
                SetError("PlaytimeStorageError", "The playtime journal directory contains a reparse point.");
                return;
            }

            var journals = await Task.Run(() => Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly), cancellationToken).ConfigureAwait(false);
            var succeeded = true;
            foreach (var journalPath in journals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                succeeded &= await ReconcileJournalAsync(journalPath, cancellationToken).ConfigureAwait(false);
            }

            await RemoveOrphanSessionsAsync(journals, cancellationToken).ConfigureAwait(false);

            await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (succeeded) ClearErrorIf("PlaytimeStorageError");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { SetError("PlaytimeStorageError", exception.Message); }
        finally { _reconcileGate.Release(); }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await ReconcileAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid?> PrepareGuestTransferAsync(string targetXuid, CancellationToken cancellationToken = default)
    {
        if (_preview || string.IsNullOrWhiteSpace(targetXuid)) return null;
        await _sessionStartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReconcileAsync(cancellationToken).ConfigureAwait(false);
            var id = await PrepareTransferAtBoundaryAsync(targetXuid, cancellationToken).ConfigureAwait(false);
            if (id == Guid.Empty) return null;
            _transferCheckpoint?.Invoke(PlaytimeTransferCheckpoint.PendingWritten);
            await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return id;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            SetError("PlaytimeStorageError", exception.Message);
            return null;
        }
        finally { _sessionStartGate.Release(); }
    }

    public async Task CompleteGuestTransferAsync(Guid transferID, CancellationToken cancellationToken = default)
    {
        if (_preview || transferID == Guid.Empty) return;
        try
        {
            _transferCheckpoint?.Invoke(PlaytimeTransferCheckpoint.BeforeApply);
            await _ledger.MutateAsync(ledger => ApplyPendingTransfer(ledger, transferID), cancellationToken).ConfigureAwait(false);
            try { _transferCheckpoint?.Invoke(PlaytimeTransferCheckpoint.Applied); }
            finally { await RefreshSnapshotAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { SetError("PlaytimeStorageError", exception.Message); }
    }

    public async Task CancelGuestTransferAsync(Guid transferID, CancellationToken cancellationToken = default)
    {
        if (_preview || transferID == Guid.Empty) return;
        try
        {
            await _ledger.MutateAsync(ledger =>
            {
                if (ledger.PendingTransfer?.ID == transferID) ledger.PendingTransfer = null;
                return true;
            }, cancellationToken).ConfigureAwait(false);
            await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) { SetError("PlaytimeStorageError", exception.Message); }
    }

    public async Task RecoverPendingTransferAsync(string? currentXuid, CancellationToken cancellationToken = default)
    {
        if (_preview) return;
        try
        {
            await _ledger.MutateAsync(ledger =>
            {
                var pending = ledger.PendingTransfer;
                if (pending is null) return false;
                if (currentXuid is not null && string.Equals(currentXuid, pending.TargetXuid, StringComparison.Ordinal))
                    ApplyPendingTransfer(ledger, pending.ID);
                else
                    ledger.PendingTransfer = null;
                return true;
            }, cancellationToken).ConfigureAwait(false);
            await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) { SetError("PlaytimeStorageError", exception.Message); }
    }

    public static Guid LegacySessionID(GameProcessRecord record)
    {
        Span<byte> bytes = stackalloc byte[32];
        record.InstanceID.TryWriteBytes(bytes);
        BitConverter.TryWriteBytes(bytes[16..20], record.Pid);
        BitConverter.TryWriteBytes(bytes[20..28], record.StartSeconds);
        BitConverter.TryWriteBytes(bytes[28..32], (uint)record.StartMicroseconds);
        return new Guid(SHA256.HashData(bytes)[..16]);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(CheckpointInterval, _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> ReconcileJournalAsync(string path, CancellationToken cancellationToken)
    {
        PlaytimeSessionJournal journal;
        try { journal = await Task.Run(() => PlaytimeJson.ReadJournal(path), cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            SetError("PlaytimeStorageError", exception.Message);
            return false;
        }

        var initialLedger = await _ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
        var sessionExists = initialLedger.Sessions.ContainsKey(journal.SessionID);
        var transferHoldsSession = initialLedger.PendingTransfer?.SessionIDs.Contains(journal.SessionID) == true;
        var isLaunching = false;
        lock (_stateSync) isLaunching = _launchingSessions.Contains(journal.SessionID);
        var helperRunning = journal.Helper is { } helper && _processProbe.IsRunning(helper);
        if (!sessionExists)
        {
            if (!helperRunning && !isLaunching)
            {
                var finalized = await Task.Run(() => TryUpdateJournalUnderLock(path, journal.SessionID, current =>
                {
                    if (current.Helper is { } helper && _processProbe.IsRunning(helper)) return false;
                    current.IsFinished = true;
                    return true;
                }), cancellationToken).ConfigureAwait(false);
                if (finalized is not null) DeleteJournalFiles(journal.SessionID);
            }
            return true;
        }

        var gameIdentity = journal.Process is { } record
            ? new PlaytimeProcessIdentity(record.Pid, record.StartSeconds, record.StartMicroseconds)
            : null;
        var gameRunning = gameIdentity is not null && _processProbe.IsRunning(gameIdentity);

        if (!journal.IsFinished && !transferHoldsSession && !helperRunning && !isLaunching)
        {
            var finalizedOrCheckpointed = await Task.Run(() => TryUpdateJournalUnderLock(path, journal.SessionID, current =>
            {
                var currentGame = current.Process is { } currentProcess
                    ? new PlaytimeProcessIdentity(currentProcess.Pid, currentProcess.StartSeconds, currentProcess.StartMicroseconds)
                    : null;
                var currentGameRunning = currentGame is not null && _processProbe.IsRunning(currentGame);
                var currentHelperRunning = current.Helper is { } currentHelper && _processProbe.IsRunning(currentHelper);
                if (currentHelperRunning) return false;
                current.Helper = null;
                if (currentGameRunning)
                {
                    current.ElapsedSeconds = Math.Max(current.ElapsedSeconds,
                        current.BaseElapsedSeconds + PlaytimeHelperHost.Elapsed(current.StartedUnbiasedTicks, _clock.GetUnbiasedTicks()));
                    return true;
                }

                current.IsFinished = true;
                return true;
            }), cancellationToken).ConfigureAwait(false);
            if (finalizedOrCheckpointed is { } updated)
            {
                journal = updated;
                gameRunning = updated.Process is { } updatedProcess
                    && _processProbe.IsRunning(new PlaytimeProcessIdentity(updatedProcess.Pid, updatedProcess.StartSeconds, updatedProcess.StartMicroseconds));
                if (gameRunning) await TryStartHelperAsync(path, cancellationToken).ConfigureAwait(false);
            }
        }

        var elapsed = journal.ElapsedSeconds;
        var isFinished = journal.IsFinished;
        var credited = await _ledger.MutateAsync(ledger =>
        {
            if (!ledger.Sessions.TryGetValue(journal.SessionID, out var session)) return false;
            if (ledger.PendingTransfer?.SessionIDs.Contains(journal.SessionID) == true) return false;
            Credit(ledger, session, elapsed);
            if (isFinished)
            {
                QueueHakoSegment(ledger, session);
                ledger.Sessions.Remove(journal.SessionID);
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);

        // A pending transfer pins the ledger owner and must retain the final checkpoint until credit commits.
        if (isFinished && credited)
        {
            lock (_stateSync) _activeRecords.Remove(journal.SessionID);
            await Task.Run(() => DeleteFinishedJournal(path, journal.SessionID), cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private PlaytimeSessionJournal? TryUpdateJournalUnderLock(string path, Guid sessionID, Func<PlaytimeSessionJournal, bool> update)
    {
        var lockPath = Path.ChangeExtension(path, ".lock");
        using var journalLock = PlaytimeJournalLock.TryAcquire(lockPath);
        if (journalLock is null || !File.Exists(path)) return null;
        var current = PlaytimeJson.ReadJournal(path);
        if (!update(current)) return null;
        PlaytimeJson.AtomicWrite(path, PlaytimeJson.SerializeJournal(current));
        return current;
    }

    private PlaytimeSessionJournal? UpdateJournal(string path, Guid sessionID, Func<PlaytimeSessionJournal, bool> update)
    {
        using var journalLock = PlaytimeJournalLock.TryAcquire(Path.ChangeExtension(path, ".lock"));
        if (journalLock is null) throw new IOException("The playtime session journal is locked by another observer.");
        var journal = File.Exists(path) ? PlaytimeJson.ReadJournal(path) : null;
        if (journal is null || journal.SessionID != sessionID || !update(journal)) return journal;
        PlaytimeJson.AtomicWrite(path, PlaytimeJson.SerializeJournal(journal));
        return journal;
    }

    private async Task TryStartHelperAsync(string journalPath, CancellationToken cancellationToken)
    {
        if (_helperLauncher is null) return;
        try
        {
            var journal = await Task.Run(() => PlaytimeJson.ReadJournal(journalPath), cancellationToken).ConfigureAwait(false);
            if (journal.Helper is { } helper && _processProbe.IsRunning(helper))
            {
                ClearErrorIf("PlaytimeHelperStartFailed");
                lock (_stateSync) _helperNextAttempt.Remove(journal.SessionID);
                return;
            }

            lock (_stateSync)
            {
                if (_helperNextAttempt.TryGetValue(journal.SessionID, out var nextAttempt)
                    && _timeProvider.GetUtcNow() < nextAttempt) return;
            }

            var started = await _helperLauncher.TryStartAsync(journalPath, cancellationToken).ConfigureAwait(false);
            lock (_stateSync) _helperNextAttempt[journal.SessionID] = _timeProvider.GetUtcNow() + CheckpointInterval;
            if (!started)
                SetError("PlaytimeHelperStartFailed", null);
            else
                ClearErrorIf("PlaytimeHelperStartFailed");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { SetError("PlaytimeHelperStartFailed", exception.Message); }
    }

    private async Task RefreshSnapshotAsync(CancellationToken cancellationToken)
    {
        var ledger = await _ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
        var next = ledger.Owners.ToDictionary(
            pair => pair.Key,
            pair => new PlaytimeSnapshot(pair.Value.TotalSeconds, new Dictionary<Guid, double>(pair.Value.Instances)),
            StringComparer.Ordinal);
        bool changed;
        lock (_stateSync)
        {
            changed = !SnapshotsEqual(_owners, next) || !_uploadIds.SetEquals(ledger.Uploads.Keys);
            _uploadIds = ledger.Uploads.Keys.ToHashSet();
            if (changed)
            {
                _owners = next;
                _revision++;
            }
        }

        if (changed) OnPropertyChanged(nameof(Revision));
    }

    private void ClearErrorIf(string key)
    {
        bool changed;
        lock (_stateSync)
        {
            changed = _errorResourceKey == key;
            if (changed)
            {
                _errorResourceKey = null;
                _errorDetail = null;
            }
        }

        if (changed) OnPropertyChanged(nameof(ErrorResourceKey));
    }

    private PlaytimeSnapshot GetSnapshot(string ownerKey)
    {
        lock (_stateSync) return _owners.GetValueOrDefault(ownerKey) ?? new PlaytimeSnapshot(0, new Dictionary<Guid, double>());
    }

    private void SetError(string? key, string? detail)
    {
        bool changed;
        lock (_stateSync)
        {
            changed = _errorResourceKey != key || _errorDetail != detail;
            _errorResourceKey = key;
            _errorDetail = detail;
        }

        if (changed) OnPropertyChanged(nameof(ErrorResourceKey));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        var handlers = PropertyChanged;
        if (handlers is null) return;
        foreach (PropertyChangedEventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, new PropertyChangedEventArgs(propertyName)); }
            catch (Exception) { }
        }
    }

    private static bool SnapshotsEqual(Dictionary<string, PlaytimeSnapshot> first, Dictionary<string, PlaytimeSnapshot> second)
    {
        if (first.Count != second.Count) return false;
        foreach (var (key, value) in first)
        {
            if (!second.TryGetValue(key, out var other) || value.TotalSeconds != other.TotalSeconds
                || value.Instances.Count != other.Instances.Count
                || value.Instances.Any(pair => other.Instances.GetValueOrDefault(pair.Key, double.NaN) != pair.Value)) return false;
        }

        return true;
    }

    private static string OwnerKey(string? xuid) => xuid is null ? "guest" : "account:" + xuid;

    private static bool ApplyPendingTransfer(PlaytimeLedger ledger, Guid transferID)
    {
        if (ledger.AppliedTransfers.Contains(transferID))
        {
            if (ledger.PendingTransfer?.ID == transferID) ledger.PendingTransfer = null;
            return true;
        }

        var pending = ledger.PendingTransfer;
        if (pending is null || pending.ID != transferID) return false;
        var targetKey = OwnerKey(pending.TargetXuid);
        var guest = ledger.Owners.GetValueOrDefault("guest") ?? new PlaytimeOwnerRecord();
        var target = ledger.Owners.GetValueOrDefault(targetKey) ?? new PlaytimeOwnerRecord();
        target.TotalSeconds += pending.GuestSecondsAtBoundary;
        guest.TotalSeconds = Math.Max(0, guest.TotalSeconds - pending.GuestSecondsAtBoundary);
        foreach (var (instanceID, seconds) in pending.PerInstanceAtBoundary)
        {
            target.Instances[instanceID] = target.Instances.GetValueOrDefault(instanceID) + seconds;
            guest.Instances[instanceID] = Math.Max(0, guest.Instances.GetValueOrDefault(instanceID) - seconds);
            if (guest.Instances[instanceID] == 0) guest.Instances.Remove(instanceID);
        }

        foreach (var sessionID in pending.SessionIDs)
        {
            if (ledger.Sessions.TryGetValue(sessionID, out var session) && session.OwnerKey == "guest")
                session.OwnerKey = targetKey;
        }

        ledger.Owners["guest"] = guest;
        ledger.Owners[targetKey] = target;
        ledger.AppliedTransfers.Add(transferID);
        ledger.PendingTransfer = null;
        return true;
    }

    private async Task<Guid> PrepareTransferAtBoundaryAsync(string targetXuid, CancellationToken cancellationToken)
    {
        await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var initial = await _ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (initial.PendingTransfer is { } existing)
            {
                if (existing.TargetXuid == targetXuid) return existing.ID;
                throw new IOException("Another guest playtime transfer is pending.");
            }

            var guestSessions = initial.Sessions.Values
                .Where(session => session.OwnerKey == "guest")
                .ToArray();
            var boundaries = new Dictionary<Guid, (double Elapsed, bool Retain)>();
            var launching = new HashSet<Guid>();
            lock (_stateSync) launching.UnionWith(_launchingSessions);

            foreach (var session in guestSessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = PlaytimePaths.Journal(_gameRoot, session.SessionID);
                if (!File.Exists(path))
                {
                    boundaries[session.SessionID] = (session.CreditedSeconds, launching.Contains(session.SessionID));
                    continue;
                }

                var journal = await Task.Run(() => PlaytimeJson.ReadJournal(path), cancellationToken).ConfigureAwait(false);
                if (journal.SessionID != session.SessionID || journal.InstanceID != session.InstanceID)
                    throw new JsonException("The playtime session journal does not match its ledger entry.");

                var active = launching.Contains(session.SessionID);
                if (journal.Process is { } process)
                {
                    var identity = new PlaytimeProcessIdentity(process.Pid, process.StartSeconds, process.StartMicroseconds);
                    active = _processProbe.IsRunning(identity);
                }
                var elapsed = journal.ElapsedSeconds;
                if (active && !journal.IsFinished && journal.Process is not null)
                    elapsed = Math.Max(elapsed,
                        journal.BaseElapsedSeconds + PlaytimeHelperHost.Elapsed(journal.StartedUnbiasedTicks, _clock.GetUnbiasedTicks()));
                else if (active && !journal.IsFinished)
                    elapsed = Math.Max(elapsed, session.CreditedSeconds);
                // The helper may hold the journal lock for the lifetime of the game. Its atomic checkpoint
                // can be read without that lock; this snapshot and uptime define the transfer boundary.
                // A dead game is not a final checkpoint: an observer may still be publishing its last interval.
                // Pin unfinished sessions too; after apply, their remaining delta belongs to the target owner.
                boundaries[session.SessionID] = (elapsed, !journal.IsFinished);
            }

            return await _ledger.MutateAsync(ledger =>
            {
                if (ledger.PendingTransfer is { } pending)
                {
                    if (pending.TargetXuid == targetXuid) return pending.ID;
                    throw new IOException("Another guest playtime transfer is pending.");
                }

                foreach (var (sessionID, boundary) in boundaries)
                {
                    if (!ledger.Sessions.TryGetValue(sessionID, out var session) || session.OwnerKey != "guest") continue;
                    Credit(ledger, session, boundary.Elapsed);
                    if (!boundary.Retain)
                    {
                        QueueHakoSegment(ledger, session);
                        ledger.Sessions.Remove(sessionID);
                    }
                }

                var guest = ledger.Owners.GetValueOrDefault("guest") ?? new PlaytimeOwnerRecord();
                var sessionIDs = boundaries.Where(pair => pair.Value.Retain
                    && ledger.Sessions.TryGetValue(pair.Key, out var session) && session.OwnerKey == "guest")
                    .Select(pair => pair.Key).ToArray();
                if (guest.TotalSeconds <= 0 && guest.Instances.Count == 0 && sessionIDs.Length == 0) return Guid.Empty;

                var transfer = new PlaytimePendingTransfer
                {
                    ID = Guid.NewGuid(),
                    TargetXuid = targetXuid,
                    GuestSecondsAtBoundary = guest.TotalSeconds,
                    PerInstanceAtBoundary = new Dictionary<Guid, double>(guest.Instances),
                    SessionIDs = sessionIDs
                };
                ledger.PendingTransfer = transfer;
                return transfer.ID;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private async Task RemoveOrphanSessionsAsync(IReadOnlyCollection<string> journalPaths, CancellationToken cancellationToken)
    {
        var journalIDs = journalPaths.Select(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).ToHashSet();
        var ledger = await _ledger.ReadAsync(cancellationToken).ConfigureAwait(false);
        var active = new HashSet<Guid>();
        lock (_stateSync) active.UnionWith(_launchingSessions);
        lock (_stateSync) active.UnionWith(_activeRecords.Keys);
        var pending = ledger.PendingTransfer?.SessionIDs.ToHashSet() ?? [];
        var orphans = ledger.Sessions.Keys.Where(id => !journalIDs.Contains(id) && !active.Contains(id) && !pending.Contains(id)).ToHashSet();
        if (orphans.Count == 0) return;
        await _ledger.MutateAsync(current =>
        {
            foreach (var id in orphans)
            {
                if (current.Sessions.TryGetValue(id, out var session)) QueueHakoSegment(current, session);
                current.Sessions.Remove(id);
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool SameProcess(GameProcessRecord first, GameProcessRecord second) =>
        first.InstanceID == second.InstanceID && first.Pid == second.Pid
        && first.StartSeconds == second.StartSeconds && first.StartMicroseconds == second.StartMicroseconds;

    private static void Credit(PlaytimeLedger ledger, PlaytimeSessionRecord session, double elapsedSeconds)
    {
        if (!PlaytimeJson.IsFiniteNonnegative(elapsedSeconds)) return;
        var delta = elapsedSeconds - session.CreditedSeconds;
        if (delta <= 0) return;
        var owner = ledger.Owners.GetValueOrDefault(session.OwnerKey) ?? new PlaytimeOwnerRecord();
        if (session.HakoAccountId is null) owner.TotalSeconds += delta;
        else session.HakoSeconds += delta;
        owner.Instances[session.InstanceID] = owner.Instances.GetValueOrDefault(session.InstanceID) + delta;
        session.CreditedSeconds = elapsedSeconds;
        ledger.Owners[session.OwnerKey] = owner;
    }

    private void DeleteJournalFiles(Guid sessionID) => DeleteFinishedJournal(
        PlaytimePaths.Journal(_gameRoot, sessionID), sessionID);

    private void DeleteFinishedJournal(string path, Guid sessionID)
    {
        try { File.Delete(path); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var lockPath = PlaytimePaths.JournalLock(_gameRoot, sessionID);
        try { File.Delete(lockPath); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using Hako.Core.Minecraft;

namespace Hako.Core.Playtime;

public static class PlaytimePaths
{
    public static string Root(string gameRoot) => Path.Combine(Path.GetFullPath(gameRoot), ".hako-playtime");
    public static string Ledger(string gameRoot) => Path.Combine(Root(gameRoot), "playtime.json");
    public static string JournalDirectory(string gameRoot) => Path.Combine(Root(gameRoot), "sessions");
    public static string Journal(string gameRoot, Guid sessionID) => Path.Combine(JournalDirectory(gameRoot), $"{sessionID:D}.json");
    public static string JournalLock(string gameRoot, Guid sessionID) => Path.Combine(JournalDirectory(gameRoot), $"{sessionID:D}.lock");

    public static bool TryValidateHelperPath(string journalPath, out string fullPath, out Guid sessionID)
    {
        fullPath = string.Empty;
        sessionID = Guid.Empty;
        try
        {
            if (!Path.IsPathFullyQualified(journalPath)) return false;
            fullPath = Path.GetFullPath(journalPath);
            if (!string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(fullPath), "D", out sessionID)) return false;

            var sessions = Path.GetDirectoryName(fullPath);
            var playtime = sessions is null ? null : Path.GetDirectoryName(sessions);
            if (sessions is null || playtime is null
                || !string.Equals(Path.GetFileName(sessions), "sessions", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(playtime), ".hako-playtime", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(fullPath)) return false;

            return HasNoReparsePoints(fullPath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool HasNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var volumeRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(volumeRoot)) return false;
        var current = volumeRoot;
        var relative = fullPath[volumeRoot.Length..];
        var components = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < components.Length; index++)
        {
            current = Path.Combine(current, components[index]);
            var exists = index == components.Length - 1 ? File.Exists(current) || Directory.Exists(current) : Directory.Exists(current);
            if (!exists) return false;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        }

        return true;
    }

    internal static bool RequireSafePath(string path, bool directory, bool allowMissingLeaf)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (parent is null || !HasNoReparsePoints(parent))
            throw new IOException("The playtime path has a missing or reparse parent.");
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) when (allowMissingLeaf) { return false; }
        catch (DirectoryNotFoundException) when (allowMissingLeaf) { return false; }
        if ((attributes & FileAttributes.ReparsePoint) != 0
            || ((attributes & FileAttributes.Directory) != 0) != directory)
            throw new IOException("The playtime path has an invalid file type or reparse point.");
        return true;
    }
}

internal static class PlaytimeJson
{
    internal const int MaximumJournalBytes = 64 * 1024;
    internal const int MaximumLedgerBytes = 1024 * 1024;

    internal static PlaytimeSessionJournal ReadJournal(string path)
    {
        var journal = JsonSerializer.Deserialize(ReadBoundedBytes(path, MaximumJournalBytes), HakoJsonContext.Default.PlaytimeSessionJournal)
            ?? throw new JsonException("The playtime journal is empty.");
        if (journal.SessionID == Guid.Empty || journal.InstanceID == Guid.Empty
            || journal.SessionID != Guid.Parse(Path.GetFileNameWithoutExtension(path))
            || !IsOwnerKey(journal.OwnerKey) || !IsFiniteNonnegative(journal.ElapsedSeconds)
            || !IsFiniteNonnegative(journal.BaseElapsedSeconds)
            || (journal.Process is { } process && (process.SessionID != journal.SessionID
                || process.InstanceID != journal.InstanceID || process.Pid <= 0
                || process.StartSeconds == 0 || process.StartMicroseconds >= 1_000_000))
            || (journal.Helper is { } helper && (helper.Pid <= 0 || helper.StartSeconds == 0 || helper.StartMicroseconds >= 1_000_000)))
        {
            throw new JsonException("The playtime journal is invalid.");
        }

        return journal;
    }

    internal static bool IsOwnerKey(string? ownerKey) => ownerKey == "guest"
        || (ownerKey is { Length: > 8 and <= 264 }
            && ownerKey.StartsWith("account:", StringComparison.Ordinal)
            && !ownerKey.AsSpan(8).ContainsAny('\0', '\r', '\n'));

    internal static bool IsFiniteNonnegative(double value) => double.IsFinite(value) && value >= 0;

    internal static byte[] SerializeJournal(PlaytimeSessionJournal journal) =>
        JsonSerializer.SerializeToUtf8Bytes(journal, HakoJsonContext.Default.PlaytimeSessionJournal);

    internal static byte[] ReadBoundedBytes(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
            throw new JsonException("The playtime data size is invalid.");
        var bytes = new byte[(int)stream.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new JsonException("The playtime data ended unexpectedly.");
            offset += read;
        }

        if (stream.ReadByte() != -1) throw new JsonException("The playtime data exceeds its size limit.");
        return bytes;
    }

    internal static void AtomicWrite(string path, ReadOnlySpan<byte> bytes)
    {
        PlaytimePaths.RequireSafePath(path, directory: false, allowMissingLeaf: true);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            PlaytimePaths.RequireSafePath(temporary, directory: false, allowMissingLeaf: true);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            PlaytimePaths.RequireSafePath(path, directory: false, allowMissingLeaf: true);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

internal sealed class PlaytimeJournalLock : IDisposable
{
    private readonly FileStream _stream;
    private PlaytimeJournalLock(FileStream stream) => _stream = stream;

    internal static PlaytimeJournalLock? TryAcquire(string path)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new IOException("A containing directory is required.");
        Directory.CreateDirectory(parent);
        if (!PlaytimePaths.HasNoReparsePoints(parent)) throw new IOException("The playtime journal directory contains a reparse point.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The playtime journal lock is a reparse point.");
        try
        {
            return new PlaytimeJournalLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough));
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}

internal sealed class PlaytimeLedgerStore
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _directory;
    private readonly string _gameRoot;
    private readonly string _path;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _gate;

    internal PlaytimeLedgerStore(string gameRoot)
    {
        _gameRoot = Path.GetFullPath(gameRoot);
        _directory = PlaytimePaths.Root(gameRoot);
        _path = Path.Combine(_directory, "playtime.json");
        _lockPath = Path.Combine(_directory, "playtime.lock");
        _gate = Gates.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
    }

    internal async Task<T> MutateAsync<T>(Func<PlaytimeLedger, T> update, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                ValidatePaths();
                Directory.CreateDirectory(_directory);
                ValidatePaths();
            }, cancellationToken).ConfigureAwait(false);
            FileStream ledgerLock;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidatePaths();
                try
                {
                    ledgerLock = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
                    break;
                }
                catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
                }
            }

            using (ledgerLock)
            {
                return await Task.Run(() =>
                {
                    var ledger = ReadCore();
                    var result = update(ledger);
                    WriteCore(ledger);
                    return result;
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<PlaytimeLedger> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(ReadCore, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private PlaytimeLedger ReadCore()
    {
        ValidatePaths();
        if (!File.Exists(_path)) return new PlaytimeLedger();
        var ledger = JsonSerializer.Deserialize(PlaytimeJson.ReadBoundedBytes(_path, PlaytimeJson.MaximumLedgerBytes), HakoJsonContext.Default.PlaytimeLedger)
            ?? throw new JsonException("The playtime ledger is empty.");
        if (ledger.FormatVersion is not (1 or 2) || ledger.Owners is null || ledger.Sessions is null || ledger.AppliedTransfers is null || ledger.Uploads is null)
            throw new JsonException("The playtime ledger format is unsupported.");
        if (ledger.HakoAccountId is not null && !Guid.TryParse(ledger.HakoAccountId, out _))
            throw new JsonException("Invalid Hako playtime owner.");
        foreach (var (id, upload) in ledger.Uploads)
            if (id == Guid.Empty || upload is null || upload.SessionId != id || upload.DurationMs < 0
                || !Guid.TryParse(upload.AccountId, out _)) throw new JsonException("Invalid pending playtime upload.");
        foreach (var session in ledger.Sessions.Values)
            if (session is null || !PlaytimeJson.IsFiniteNonnegative(session.HakoSeconds)
                || (session.HakoAccountId is not null && (!Guid.TryParse(session.HakoAccountId, out _) || session.SegmentId == Guid.Empty)))
                throw new JsonException("Invalid Hako playtime session.");
        foreach (var (key, owner) in ledger.Owners)
        {
            if (!PlaytimeJson.IsOwnerKey(key) || owner is null || owner.Instances is null
                || !PlaytimeJson.IsFiniteNonnegative(owner.TotalSeconds)
                || owner.Instances.Any(pair => pair.Key == Guid.Empty || !PlaytimeJson.IsFiniteNonnegative(pair.Value)))
                throw new JsonException("The playtime owner data is invalid.");
        }

        foreach (var (id, session) in ledger.Sessions)
        {
            if (id == Guid.Empty || session is null || session.SessionID != id || session.InstanceID == Guid.Empty
                || !PlaytimeJson.IsOwnerKey(session.OwnerKey) || !PlaytimeJson.IsFiniteNonnegative(session.CreditedSeconds))
                throw new JsonException("The playtime session data is invalid.");
        }

        if (ledger.AppliedTransfers.Contains(Guid.Empty)
            || (ledger.PendingTransfer is { } pending
                && (pending.ID == Guid.Empty || !PlaytimeJson.IsOwnerKey("account:" + pending.TargetXuid)
                    || !PlaytimeJson.IsFiniteNonnegative(pending.GuestSecondsAtBoundary)
                    || pending.PerInstanceAtBoundary is null
                    || pending.PerInstanceAtBoundary.Any(pair => pair.Key == Guid.Empty || !PlaytimeJson.IsFiniteNonnegative(pair.Value))
                    || pending.SessionIDs is null || pending.SessionIDs.Any(id => id == Guid.Empty)
                    || pending.SessionIDs.Distinct().Count() != pending.SessionIDs.Length)))
            throw new JsonException("The playtime transfer data is invalid.");

        return ledger;
    }

    private void ValidatePaths()
    {
        PlaytimePaths.RequireSafePath(_gameRoot, directory: true, allowMissingLeaf: false);
        if (!PlaytimePaths.RequireSafePath(_directory, directory: true, allowMissingLeaf: true)) return;
        PlaytimePaths.RequireSafePath(_path, directory: false, allowMissingLeaf: true);
        PlaytimePaths.RequireSafePath(_lockPath, directory: false, allowMissingLeaf: true);
    }

    private void WriteCore(PlaytimeLedger ledger)
    {
        ledger.FormatVersion = 2;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ledger, HakoJsonContext.Default.PlaytimeLedger);
        if (bytes.Length > PlaytimeJson.MaximumLedgerBytes) throw new IOException("The playtime ledger is too large.");
        PlaytimeJson.AtomicWrite(_path, bytes);
    }
}

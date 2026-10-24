using System.Collections.Generic;

namespace Hako;

internal sealed record BackupRestoreQueueEntry(string Path, string Key);

internal sealed class BackupRestoreQueue
{
    private readonly object _sync = new();
    private readonly Queue<BackupRestoreQueueEntry> _pending = new();
    private readonly HashSet<string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);

    public bool Enqueue(string path)
    {
        if (!IsArchivePath(path)) return false;

        string normalized;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        lock (_sync)
        {
            if (!_knownPaths.Add(normalized)) return false;
            _pending.Enqueue(new BackupRestoreQueueEntry(normalized, normalized));
            return true;
        }
    }

    public bool TryDequeue(out BackupRestoreQueueEntry entry)
    {
        lock (_sync)
        {
            if (_pending.TryDequeue(out var next))
            {
                entry = next;
                return true;
            }
        }

        entry = null!;
        return false;
    }

    public void Complete(BackupRestoreQueueEntry entry)
    {
        lock (_sync) _knownPaths.Remove(entry.Key);
    }

    public bool HasPending
    {
        get { lock (_sync) return _pending.Count > 0; }
    }

    private static bool IsArchivePath(string path) =>
        Path.GetExtension(path).Equals(".hakobackup", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".hakoworld", StringComparison.OrdinalIgnoreCase);
}

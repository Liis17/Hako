namespace Hako.Core;

public enum HakoRootLockError
{
    AlreadyInUse
}

public sealed class HakoRootInUseException : IOException
{
    public HakoRootInUseException(string path, Exception? innerException = null)
        : base("Hako is already using this data folder.", innerException)
    {
        LockPath = path;
        Error = HakoRootLockError.AlreadyInUse;
    }

    public string LockPath { get; }
    public HakoRootLockError Error { get; }
}

public sealed class HakoRootLock : IDisposable
{
    private readonly IReadOnlyList<FileStream> _streams;

    private HakoRootLock(IReadOnlyList<FileStream> streams) => _streams = streams;

    public static HakoRootLock Acquire(HakoPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var streams = new List<FileStream>(2);
        try
        {
            foreach (var root in new[] { paths.GameRoot, paths.DataRoot })
            {
                Directory.CreateDirectory(root);
                var lockPath = Path.Combine(root, ".hako-lock");
                if (TryGetAttributes(lockPath, out var attributes))
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException("The Hako lock cannot be a reparse point or symbolic link.");
                    }
                }

                try
                {
                    streams.Add(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }
                catch (IOException exception) when (IsLockViolation(exception))
                {
                    throw new HakoRootInUseException(lockPath, exception);
                }
            }

            return new HakoRootLock(streams);
        }
        catch
        {
            foreach (var stream in streams)
            {
                stream.Dispose();
            }

            throw;
        }
    }

    internal static bool IsLockViolation(IOException exception)
    {
        var error = exception.HResult & 0xFFFF;
        return error is 32 or 33;
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var stream in _streams)
        {
            stream.Dispose();
        }
    }
}

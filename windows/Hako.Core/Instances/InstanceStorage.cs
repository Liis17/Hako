using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Hako.Core.Instances;

public enum InstanceStorageError
{
    OperationFailed,
    InvalidPath,
    PathTraversal,
    ReparsePointNotAllowed,
    InvalidBuildName,
    ReservedBuildName,
    DuplicateBuildName,
    BuildFolderExists,
    BuildInstalling,
    BuildRunning,
    BuildFilesBusy,
    InstanceRegistryNotLoaded,
    InstanceRegistryUnavailable,
    InvalidOfflineUsername,
    MissingFabricLoaderVersion
}

public sealed class InstanceStorageException : IOException
{
    public InstanceStorageException(string message) : this(InstanceStorageError.OperationFailed, message) { }

    public InstanceStorageException(InstanceStorageError error, string message, Exception? innerException = null)
        : base(message, innerException) => Error = error;

    public InstanceStorageError Error { get; }
}

public sealed class InstanceStorage(HakoPaths paths)
{
    public const string BackupsFolder = "backups";
    public const string WorldsFolder = "worlds";

    public string Root { get; } = Path.GetFullPath(paths.GameRoot);

    /// <summary>Counts logical file lengths instead of allocated file size.</summary>
    public InstanceStorageUsage Usage() => Usage(CancellationToken.None);

    public InstanceStorageUsage Usage(CancellationToken cancellationToken)
    {
        var result = new InstanceStorageUsage();
        var walkRoot = Root;
        try
        {
            walkRoot = System.IO.Directory.ResolveLinkTarget(Root, returnFinalTarget: true)?.FullName ?? Root;
        }
        catch (Exception)
        {
            // A non-link or unresolved junction uses the configured Hako path.
        }

        if (!TryGetAttributes(walkRoot, out var rootAttributes)) return result;
        if ((rootAttributes & FileAttributes.Directory) == 0)
        {
            throw new IOException("The Hako game root is not a directory.");
        }

        var directories = new Stack<string>();
        directories.Push(walkRoot);
        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = directories.Pop();
            foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetAttributes(entry, out var attributes) || (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entry);
                    continue;
                }

                var relativePath = Path.GetRelativePath(walkRoot, entry);
                var segments = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries);
                result.Add(InstanceStorageUsage.Classify(segments), new FileInfo(entry).Length);
            }
        }

        return result;
    }

    public DiskSpaceInfo GetDiskSpaceInfo()
    {
        var volumePath = Root;
        try
        {
            volumePath = System.IO.Directory.ResolveLinkTarget(Root, returnFinalTarget: true)?.FullName ?? Root;
        }
        catch (Exception)
        {
            // A non-link or unresolved junction uses the configured Hako path.
        }

        var driveRoot = Path.GetPathRoot(volumePath);
        if (string.IsNullOrWhiteSpace(driveRoot) || driveRoot.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new IOException("The Hako game root is not on a supported local drive.");
        }

        var drive = new DriveInfo(driveRoot);
        if (!drive.IsReady)
        {
            throw new IOException("The drive containing the Hako game root is unavailable.");
        }

        var total = drive.TotalSize;
        if (total <= 0) throw new IOException("The drive size is unavailable.");
        var available = drive.AvailableFreeSpace;
        if (available < 0) throw new IOException("The free space on the drive is unavailable.");
        var free = Math.Min(available, total);
        string? label;
        try
        {
            label = drive.VolumeLabel;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            label = null;
        }

        var volumeName = string.IsNullOrWhiteSpace(label)
            ? null
            : $"{label} ({drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)})";
        return new DiskSpaceInfo(volumeName, total, free);
    }

    public string Directory(string folderName)
    {
        if (!IsSinglePathSegment(folderName))
        {
            throw new InstanceStorageException(InstanceStorageError.InvalidPath, "Invalid build folder name.");
        }

        return ContainedPath(folderName, Root, allowBaseReparsePoint: true);
    }

    public static string ContainedPath(string relativePath, string baseDirectory, bool allowBaseReparsePoint = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var root = Path.GetFullPath(baseDirectory);
        if (Path.IsPathRooted(relativePath) || relativePath.StartsWith('\\') || relativePath.StartsWith('/')
            || relativePath.Contains(':') || relativePath.Contains('\0'))
        {
            throw new InstanceStorageException(InstanceStorageError.InvalidPath, "Absolute or invalid paths are not allowed.");
        }

        var parts = relativePath.Split(['\\', '/'], StringSplitOptions.None);
        if (parts.Any(part => part.Length == 0 || part is "." or ".."))
        {
            throw new InstanceStorageException(InstanceStorageError.PathTraversal, "Path traversal is not allowed.");
        }

        var target = root;
        foreach (var part in parts)
        {
            target = Path.Combine(target, part);
        }

        target = Path.GetFullPath(target);
        var relative = Path.GetRelativePath(root, target);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InstanceStorageException(InstanceStorageError.PathTraversal, "The path escapes its parent folder.");
        }

        EnsureNoReparsePoints(root, parts, checkRoot: !allowBaseReparsePoint);
        return target;
    }

    public void EnsureAvailable(string folderName, IReadOnlyCollection<GameInstance> instances, Guid? excluding = null)
    {
        if (instances.Any(instance => instance.Id != excluding
            && string.Equals(instance.FolderName, folderName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InstanceStorageException(InstanceStorageError.DuplicateBuildName, "A build with this name already exists.");
        }

        if (!System.IO.Directory.Exists(Root))
        {
            return;
        }

        var excludedFolder = instances.FirstOrDefault(instance => instance.Id == excluding)?.FolderName;
        foreach (var existing in System.IO.Directory.EnumerateFileSystemEntries(Root))
        {
            var name = Path.GetFileName(existing);
            if (string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, excludedFolder, StringComparison.OrdinalIgnoreCase))
            {
                throw new InstanceStorageException(InstanceStorageError.BuildFolderExists, "A build folder with this name already exists.");
            }
        }
    }

    internal void CreateDirectoryExclusive(string path)
    {
        EnsureNoReparsePoints(Root, [Path.GetFileName(path)], checkRoot: false);
        if (!CreateDirectoryW(path, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 183)
            {
                throw new IOException("The build folder already exists.", new Win32Exception(error));
            }

            throw new Win32Exception(error);
        }
    }

    internal static void CreateDirectoryExclusiveAt(string parentDirectory, string name)
    {
        if (!IsSinglePathSegment(name))
        {
            throw new InstanceStorageException(InstanceStorageError.InvalidPath, "Invalid directory name.");
        }

        var path = ContainedPath(name, parentDirectory);
        if (!CreateDirectoryW(path, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 183)
            {
                throw new IOException("The directory already exists.", new Win32Exception(error));
            }

            throw new Win32Exception(error);
        }
    }

    internal static void MoveDirectory(string from, string to) => System.IO.Directory.Move(from, to);

    internal static bool IsSinglePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".."
            || value.Contains('\\') || value.Contains('/') || value.Contains(':') || value.Contains('\0')
            || value.EndsWith(' ') || value.EndsWith('.') || Path.IsPathRooted(value)
            || value.Any(character => character < 32 || "<>\"|?*".Contains(character)))
        {
            return false;
        }

        var stem = value.Split('.')[0];
        return !stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            && !stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            && !stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            && !stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            && !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9');
    }

    internal static void DeleteCreatedDirectory(string path)
    {
        DeleteEntryWithoutFollowingReparsePoints(path);
    }

    internal static void DeleteEntryWithoutFollowingReparsePoints(string path)
    {
        var root = Path.GetFullPath(path);
        DeleteEntry(root, root);
    }

    private static void DeleteEntry(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InstanceStorageException(InstanceStorageError.PathTraversal, "The path escapes its deletion root.");
        }

        if (!TryGetAttributes(fullPath, out var attributes))
        {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0)
            {
                System.IO.Directory.Delete(fullPath);
            }
            else
            {
                File.Delete(fullPath);
            }

            return;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            foreach (var child in System.IO.Directory.EnumerateFileSystemEntries(fullPath))
            {
                DeleteEntry(child, root);
            }

            System.IO.Directory.Delete(fullPath);
        }
        else
        {
            File.Delete(fullPath);
        }
    }

    private static void EnsureNoReparsePoints(string root, IReadOnlyList<string> children, bool checkRoot)
    {
        var fullRoot = Path.GetFullPath(root);
        if (checkRoot)
        {
            CheckReparsePoint(fullRoot);
        }

        var current = fullRoot;
        foreach (var child in children)
        {
            current = Path.Combine(current, child);
            if (!TryGetAttributes(current, out var attributes))
            {
                break;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InstanceStorageException(InstanceStorageError.ReparsePointNotAllowed,
                    "Reparse points and symbolic links are not allowed in build paths.");
            }
        }
    }

    private static void CheckReparsePoint(string path)
    {
        if (TryGetAttributes(path, out var attributes) && (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InstanceStorageException(InstanceStorageError.ReparsePointNotAllowed,
                "Reparse points and symbolic links are not allowed in build paths.");
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        // On Windows, FileSystemInfo returns -1 for missing paths while preserving other I/O errors.
        // Installation checks thousands of paths before their parent directories exist.
        attributes = new FileInfo(path).Attributes;
        return attributes != (FileAttributes)(-1);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);
}

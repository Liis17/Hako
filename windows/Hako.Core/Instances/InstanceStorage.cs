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

    internal static void MoveDirectory(string from, string to) => System.IO.Directory.Move(from, to);

    internal static bool IsSinglePathSegment(string value) => !string.IsNullOrWhiteSpace(value)
        && value is not "." and not ".."
        && !value.Contains('\\') && !value.Contains('/') && !value.Contains(':') && !value.Contains('\0')
        && !Path.IsPathRooted(value);

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

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);
}

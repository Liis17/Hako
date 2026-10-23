using System.IO.Compression;
using System.Security.Cryptography;

namespace Hako.Core.Instances;

internal sealed record InstanceFileTreeEntry(
    string RelativePath,
    string FullPath,
    bool IsDirectory,
    FileAttributes Attributes,
    DateTime LastWriteUtc,
    long Length);

/// <summary>Safe, streaming traversal for copies and ZIPs of build-owned files.</summary>
internal static class InstanceFileTree
{
    private const int BufferSize = 81920;

    internal static IReadOnlyList<InstanceFileTreeEntry> Snapshot(string root, Func<string, bool>? exclude,
        CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(root);
        var rootAttributes = File.GetAttributes(fullRoot);
        if ((rootAttributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InstanceFileTreeReparseException("The source folder cannot be a reparse point or device.");
        if ((rootAttributes & FileAttributes.Directory) == 0)
            throw new IOException("The source folder is not a directory.");

        var entries = new List<InstanceFileTreeEntry>();
        Walk(fullRoot, fullRoot, "", exclude, entries, cancellationToken);
        return entries.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static void Walk(string root, string directory, string relativeDirectory,
        Func<string, bool>? exclude, List<InstanceFileTreeEntry> entries, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(directory);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InstanceFileTreeReparseException("The source tree contains a reparse point or device.");
        if ((attributes & FileAttributes.Directory) == 0)
            throw new IOException("The source tree contains a non-directory parent.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (!InstanceStorage.IsSinglePathSegment(name) || !names.Add(name))
                throw new IOException("The source tree contains a path that cannot be represented safely on Windows.");

            var relative = relativeDirectory.Length == 0 ? name : relativeDirectory + "/" + name;
            var safePath = InstanceStorage.ContainedPath(relative.Replace('/', Path.DirectorySeparatorChar), root);
            var childAttributes = File.GetAttributes(safePath);
            if ((childAttributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new InstanceFileTreeReparseException("The source tree contains a reparse point or device entry.");
            if (exclude?.Invoke(relative) == true) continue;

            var isDirectory = (childAttributes & FileAttributes.Directory) != 0;
            var info = isDirectory ? null : new FileInfo(safePath);
            entries.Add(new InstanceFileTreeEntry(relative, safePath, isDirectory, childAttributes,
                isDirectory ? Directory.GetLastWriteTimeUtc(safePath) : info!.LastWriteTimeUtc,
                isDirectory ? 0 : info!.Length));
            if (isDirectory) Walk(root, safePath, relative, exclude, entries, cancellationToken);
        }
    }

    internal static async Task<IReadOnlyList<InstanceFileTreeEntry>> CopyAsync(string sourceRoot, string destinationRoot, Func<string, bool>? exclude,
        CancellationToken cancellationToken)
    {
        var entries = Snapshot(sourceRoot, exclude, cancellationToken);
        var destination = Path.GetFullPath(destinationRoot);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (entry.IsDirectory)
            {
                InstanceStorage.CreateDirectoryExclusiveAt(Path.GetDirectoryName(target)!, Path.GetFileName(target));
                continue;
            }

            using (var input = new FileStream(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            var sourceInfo = new FileInfo(entry.FullPath);
            if (sourceInfo.Length != entry.Length || sourceInfo.LastWriteTimeUtc != entry.LastWriteUtc)
                throw new InstanceFileTreeChangedException($"The source file changed while it was being copied: {entry.RelativePath}");
            File.SetLastWriteTimeUtc(target, entry.LastWriteUtc);
            File.SetAttributes(target, entry.Attributes & ~FileAttributes.ReparsePoint);
        }

        foreach (var entry in entries.Where(item => item.IsDirectory).OrderByDescending(item => item.RelativePath.Length))
        {
            var target = Path.Combine(destination, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.SetLastWriteTimeUtc(target, entry.LastWriteUtc);
            File.SetAttributes(target, entry.Attributes & ~FileAttributes.ReparsePoint);
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureUnchanged(sourceRoot, entries, exclude, cancellationToken);
        return entries;
    }

    internal static void EnsureUnchanged(string root, IReadOnlyList<InstanceFileTreeEntry> before,
        Func<string, bool>? exclude, CancellationToken cancellationToken)
    {
        var after = Snapshot(root, exclude, cancellationToken);
        if (before.Count != after.Count)
            throw new InstanceFileTreeChangedException("The source tree changed during the operation.");
        for (var index = 0; index < before.Count; index++)
        {
            var left = before[index];
            var right = after[index];
            if (!string.Equals(left.RelativePath, right.RelativePath, StringComparison.Ordinal)
                || left.IsDirectory != right.IsDirectory || left.Length != right.Length
                || left.LastWriteUtc != right.LastWriteUtc)
                throw new InstanceFileTreeChangedException("The source tree changed during the operation.");
        }
    }

    internal static async Task AddToArchiveAsync(ZipArchive archive, IReadOnlyList<InstanceFileTreeEntry> entries,
        string pathPrefix, CancellationToken cancellationToken, Action? beforeEntry = null)
    {
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            beforeEntry?.Invoke();
            var name = pathPrefix + entry.RelativePath;
            if (entry.IsDirectory)
            {
                archive.CreateEntry(name.TrimEnd('/') + "/");
                continue;
            }

            var zipped = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var input = new FileStream(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = zipped.Open();
            await input.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
            var info = new FileInfo(entry.FullPath);
            if (info.Length != entry.Length || info.LastWriteTimeUtc != entry.LastWriteUtc)
                throw new InstanceFileTreeChangedException($"The source file changed while it was being archived: {entry.RelativePath}");
        }
    }

    internal static async Task<string> Sha512Async(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var buffer = new byte[BufferSize];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static void DeleteOwnedDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        FileAttributes rootAttributes;
        try { rootAttributes = File.GetAttributes(fullPath); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((rootAttributes & FileAttributes.Directory) != 0) Directory.Delete(fullPath);
            else File.Delete(fullPath);
            return;
        }
        if ((rootAttributes & FileAttributes.Directory) == 0)
            throw new IOException("Owned staging path is not a directory.");

        foreach (var entry in Directory.EnumerateFileSystemEntries(fullPath))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(entry);
                else File.Delete(entry);
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteOwnedDirectory(entry);
            }
            else
            {
                File.SetAttributes(entry, FileAttributes.Normal);
                File.Delete(entry);
            }
        }
        File.SetAttributes(fullPath, FileAttributes.Directory);
        Directory.Delete(fullPath);
    }
}

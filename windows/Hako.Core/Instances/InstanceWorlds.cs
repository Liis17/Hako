using System.Runtime.Versioning;
using Hako.Core.Minecraft;

namespace Hako.Core.Instances;

public sealed record InstanceWorld(
    string Id,
    string Name,
    string? Version,
    int? GameType,
    DateTimeOffset? LastPlayed,
    long? Size,
    byte[]? IconData,
    WorldMetadataError? MetadataError);

public enum InstanceWorldFailureReason
{
    BuildUnavailable,
    BuildNotReady,
    BuildInstalling,
    BuildRunning,
    BuildFilesBusy,
    InvalidWorldId,
    WorldMissing,
    ReparsePointNotAllowed,
    NoAvailableName,
    MetadataRenameFailed,
    CopyFailed,
    RecycleBinFailed,
    NotRecyclable
}

public sealed record WorldDuplicateResult(string? WorldId, InstanceWorldFailureReason? FailureReason)
{
    public bool Succeeded => WorldId is not null && FailureReason is null;
}

public sealed record WorldTrashResult(RecycleBinItemStatus Status, InstanceWorldFailureReason? FailureReason)
{
    public bool Succeeded => Status == RecycleBinItemStatus.Recycled && FailureReason is null;
}

public sealed class InstanceWorldException(InstanceWorldFailureReason error, string message, Exception? innerException = null)
    : IOException(message, innerException)
{
    public InstanceWorldFailureReason Error { get; } = error;
}

/// <summary>Lists and manages the local saves folder for each registered build.</summary>
[SupportedOSPlatform("windows")]
public sealed class InstanceWorlds
{
    private static readonly string[] MetadataFiles = ["level.dat", "level.dat_old"];
    private const int MaximumIconBytes = 4 * 1024 * 1024;
    private const string StagePrefix = ".hako-world-copy-";

    private readonly InstanceStore _store;
    private readonly InstanceContent _content;
    private readonly RecycleBin _recycleBin;

    public InstanceWorlds(InstanceStore store, RecycleBin? recycleBin = null)
    {
        _store = store;
        _recycleBin = recycleBin ?? new RecycleBin();
        _content = new InstanceContent(store, _recycleBin);
    }

    /// <summary>Lists the registered build's local worlds without creating the saves folder.</summary>
    public async Task<IReadOnlyList<InstanceWorld>> ListAsync(Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var root = await Task.Run(() => _store.GetInstanceDirectory(instanceId), cancellationToken)
            .ConfigureAwait(false);
        return await Task.Run(() => ListAt(root, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns an existing world folder only after checking its path and regular metadata files.</summary>
    public static string WorldFolder(string worldId, string instanceRoot)
    {
        if (!InstanceStorage.IsSinglePathSegment(worldId))
        {
            throw new InstanceWorldException(InstanceWorldFailureReason.InvalidWorldId,
                "The world folder name is invalid.");
        }

        var folder = ContainedPath(Path.Combine("minecraft", "saves", worldId), instanceRoot);
        if (!Directory.Exists(folder))
        {
            throw new InstanceWorldException(InstanceWorldFailureReason.WorldMissing,
                "The world no longer exists.");
        }

        var attributes = File.GetAttributes(folder);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InstanceWorldException(InstanceWorldFailureReason.ReparsePointNotAllowed,
                "World folders cannot be reparse points.");
        }

        if ((attributes & FileAttributes.Directory) == 0 || !MetadataFiles.Any(file => IsOrdinaryFile(folder, file)))
        {
            throw new InstanceWorldException(InstanceWorldFailureReason.WorldMissing,
                "The world no longer has regular level metadata.");
        }

        return folder;
    }

    /// <summary>Returns the contained datapacks path without creating it; the world itself must still exist.</summary>
    public static string DatapacksFolder(string worldId, string instanceRoot)
    {
        var worldFolder = WorldFolder(worldId, instanceRoot);
        var folder = ContainedPath("datapacks", worldFolder);
        if (TryGetAttributes(folder, out var attributes)
            && ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0))
        {
            throw new InstanceWorldException((attributes & FileAttributes.ReparsePoint) != 0
                    ? InstanceWorldFailureReason.ReparsePointNotAllowed
                    : InstanceWorldFailureReason.WorldMissing,
                "The datapacks path is not a regular folder.");
        }

        return folder;
    }

    /// <summary>Creates an independent copy of a ready build's world under the same content lease as other file operations.</summary>
    public async Task<WorldDuplicateResult> DuplicateAsync(Guid instanceId, string worldId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _content.WithContentLeaseAsync(instanceId, (root, instance, token) =>
            {
                if (instance.State != InstanceInstallationState.Ready)
                {
                    return Task.FromResult(new WorldDuplicateResult(null,
                        InstanceWorldFailureReason.BuildNotReady));
                }

                return Task.FromResult(DuplicateUnderLease(root, worldId, token));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new WorldDuplicateResult(null, MapFailure(exception, InstanceWorldFailureReason.CopyFailed));
        }
    }

    /// <summary>Moves a revalidated world folder to the Windows Recycle Bin under the build content lease.</summary>
    public async Task<WorldTrashResult> TrashAsync(Guid instanceId, string worldId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _content.WithContentLeaseAsync(instanceId, async (root, instance, token) =>
            {
                if (instance.State != InstanceInstallationState.Ready)
                {
                    return new WorldTrashResult(RecycleBinItemStatus.Failed,
                        InstanceWorldFailureReason.BuildNotReady);
                }

                token.ThrowIfCancellationRequested();
                var folder = WorldFolder(worldId, root);
                var result = await _recycleBin.DeleteAsync(folder, token).ConfigureAwait(false);
                if (result.Items.Count != 1)
                {
                    return new WorldTrashResult(RecycleBinItemStatus.Failed,
                        InstanceWorldFailureReason.RecycleBinFailed);
                }

                var item = result.Items[0];
                return item.Status switch
                {
                    RecycleBinItemStatus.Recycled => new WorldTrashResult(item.Status, null),
                    RecycleBinItemStatus.NotRecyclable => new WorldTrashResult(item.Status,
                        InstanceWorldFailureReason.NotRecyclable),
                    RecycleBinItemStatus.Cancelled => new WorldTrashResult(item.Status,
                        InstanceWorldFailureReason.RecycleBinFailed),
                    _ => new WorldTrashResult(item.Status, InstanceWorldFailureReason.RecycleBinFailed)
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new WorldTrashResult(RecycleBinItemStatus.Failed,
                MapFailure(exception, InstanceWorldFailureReason.RecycleBinFailed));
        }
    }

    private static IReadOnlyList<InstanceWorld> ListAt(string instanceRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var saves = ContainedPath(Path.Combine("minecraft", "saves"), instanceRoot);
        if (!Directory.Exists(saves)) return Array.Empty<InstanceWorld>();

        var savesAttributes = File.GetAttributes(saves);
        if ((savesAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InstanceWorldException(InstanceWorldFailureReason.ReparsePointNotAllowed,
                "The saves folder cannot be a reparse point.");
        }
        if ((savesAttributes & FileAttributes.Directory) == 0)
        {
            throw new InstanceWorldException(InstanceWorldFailureReason.WorldMissing,
                "The saves path is not a folder.");
        }

        var worlds = new List<InstanceWorld>();
        foreach (var folder in Directory.EnumerateDirectories(saves))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(folder);
            if (!InstanceStorage.IsSinglePathSegment(id)) continue;

            var attributes = File.GetAttributes(folder);
            if ((attributes & FileAttributes.ReparsePoint) != 0
                || (attributes & FileAttributes.Directory) == 0
                || !MetadataFiles.Any(file => IsOrdinaryFile(folder, file))) continue;

            var metadata = ReadMetadata(folder, cancellationToken);
            worlds.Add(new InstanceWorld(id, metadata.Value?.Name ?? id, metadata.Value?.Version,
                metadata.Value?.GameType, metadata.Value?.LastPlayed, TryGetSize(folder, cancellationToken),
                TryReadIcon(folder, cancellationToken), metadata.Error));
        }

        return worlds
            .OrderByDescending(world => world.LastPlayed.HasValue)
            .ThenByDescending(world => world.LastPlayed)
            .ThenBy(world => world.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(world => world.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static (WorldMetadata? Value, WorldMetadataError? Error) ReadMetadata(string folder,
        CancellationToken cancellationToken)
    {
        WorldMetadataError? firstError = null;
        foreach (var file in MetadataFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsOrdinaryFile(folder, file)) continue;
            try
            {
                return (WorldMetadata.ReadFile(ContainedPath(file, folder), cancellationToken), null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (WorldMetadataException exception)
            {
                firstError ??= exception.Error;
            }
        }

        return (null, firstError ?? WorldMetadataError.ReadFailure);
    }

    private static WorldDuplicateResult DuplicateUnderLease(string instanceRoot, string worldId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = WorldFolder(worldId, instanceRoot);
        CheckNoReparsePoints(source, cancellationToken);
        var sourceMetadata = ReadMetadata(source, cancellationToken);
        var metadataWasReadable = sourceMetadata.Value is not null;
        var sourceName = sourceMetadata.Value?.Name ?? worldId;
        var saves = ContainedPath(Path.Combine("minecraft", "saves"), instanceRoot);
        var stageId = StagePrefix + Guid.NewGuid().ToString("N");
        var stage = ContainedPath(stageId, saves);
        InstanceStorage.CreateDirectoryExclusiveAt(saves, stageId);
        var published = false;
        try
        {
            var attributes = new List<(string Path, FileAttributes Value, DateTime LastWriteUtc)>();
            attributes.Add((stage, File.GetAttributes(source), Directory.GetLastWriteTimeUtc(source)));
            CopyDirectory(source, source, stage, attributes, cancellationToken);
            CheckNoReparsePoints(stage, cancellationToken);

            CheckNoReparsePoints(source, cancellationToken);
            _ = WorldFolder(worldId, instanceRoot);

            for (var suffix = 2; suffix <= 999; suffix++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var newWorldId = $"{worldId} {suffix}";
                if (!InstanceStorage.IsSinglePathSegment(newWorldId)) continue;
                if (WorldNameExists(saves, newWorldId)) continue;
                var destination = ContainedPath(newWorldId, saves);

                var renamedCount = RenameReadableMetadata(stage, $"{sourceName} {suffix}", cancellationToken);
                if (metadataWasReadable && renamedCount == 0)
                {
                    throw new InstanceWorldException(InstanceWorldFailureReason.MetadataRenameFailed,
                        "The copied world metadata could not be renamed.");
                }

                ApplyAttributes(attributes);
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    Directory.Move(stage, destination);
                    published = true;
                    return new WorldDuplicateResult(newWorldId, null);
                }
                catch (IOException) when (WorldNameExists(saves, newWorldId))
                {
                    // An external writer claimed the candidate after it was checked; try the next suffix.
                }
            }

            return new WorldDuplicateResult(null, InstanceWorldFailureReason.NoAvailableName);
        }
        finally
        {
            if (!published && Directory.Exists(stage)) InstanceStorage.DeleteCreatedDirectory(stage);
        }
    }

    private static int RenameReadableMetadata(string stage, string name, CancellationToken cancellationToken)
    {
        var renamed = 0;
        foreach (var file in MetadataFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsOrdinaryFile(stage, file)) continue;
            var path = ContainedPath(file, stage);
            byte[] compressed;
            try
            {
                compressed = ReadBounded(path, WorldMetadata.MaximumBytes, cancellationToken);
                _ = WorldMetadata.Read(compressed, cancellationToken);
            }
            catch (IOException)
            {
                // Unreadable metadata is copied byte-for-byte; a valid alternate file can still be renamed.
                continue;
            }

            var renamedBytes = WorldMetadata.Renamed(compressed, name, cancellationToken);
            File.WriteAllBytes(path, renamedBytes);
            renamed++;
        }

        return renamed;
    }

    private static void CopyDirectory(string sourceRoot, string currentSource, string destination,
        List<(string Path, FileAttributes Value, DateTime LastWriteUtc)> attributes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relativeDirectory = Path.GetRelativePath(sourceRoot, currentSource);
        var checkedDirectory = relativeDirectory == "." ? sourceRoot : ContainedPath(relativeDirectory, sourceRoot);
        CheckNoReparsePoint(checkedDirectory);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourcePath in Directory.EnumerateFileSystemEntries(checkedDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(sourcePath);
            if (!InstanceStorage.IsSinglePathSegment(name) || !names.Add(name))
            {
                throw new InstanceWorldException(InstanceWorldFailureReason.CopyFailed,
                    "The world contains a name that cannot be copied safely on Windows.");
            }

            var relativePath = Path.Combine(relativeDirectory == "." ? string.Empty : relativeDirectory, name);
            var safeSource = ContainedPath(relativePath, sourceRoot);
            var sourceAttributes = File.GetAttributes(safeSource);
            if ((sourceAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InstanceWorldException(InstanceWorldFailureReason.ReparsePointNotAllowed,
                    "Worlds with reparse points cannot be copied.");
            }

            var destinationPath = Path.Combine(destination, name);
            if ((sourceAttributes & FileAttributes.Directory) != 0)
            {
                InstanceStorage.CreateDirectoryExclusiveAt(destination, name);
                attributes.Add((destinationPath, sourceAttributes, Directory.GetLastWriteTimeUtc(safeSource)));
                CopyDirectory(sourceRoot, safeSource, destinationPath, attributes, cancellationToken);
            }
            else
            {
                if ((sourceAttributes & FileAttributes.Device) != 0)
                {
                    throw new InstanceWorldException(InstanceWorldFailureReason.CopyFailed,
                        "Device entries cannot be copied as world files.");
                }

                using var input = new FileStream(safeSource, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
                using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, FileOptions.SequentialScan);
                var buffer = new byte[81920];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = input.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    output.Write(buffer, 0, read);
                }

                attributes.Add((destinationPath, sourceAttributes, File.GetLastWriteTimeUtc(safeSource)));
            }
        }
    }

    private static void ApplyAttributes(List<(string Path, FileAttributes Value, DateTime LastWriteUtc)> attributes)
    {
        const FileAttributes preserved = FileAttributes.Hidden | FileAttributes.System
            | FileAttributes.Archive | FileAttributes.NotContentIndexed | FileAttributes.Temporary;
        for (var index = attributes.Count - 1; index >= 0; index--)
        {
            var entry = attributes[index];
            if ((entry.Value & FileAttributes.Directory) != 0)
            {
                Directory.SetLastWriteTimeUtc(entry.Path, entry.LastWriteUtc);
                File.SetAttributes(entry.Path, FileAttributes.Directory | (entry.Value & preserved));
            }
            else
            {
                File.SetLastWriteTimeUtc(entry.Path, entry.LastWriteUtc);
                var fileAttributes = entry.Value & preserved;
                File.SetAttributes(entry.Path, fileAttributes == 0 ? FileAttributes.Normal : fileAttributes);
            }
        }
    }

    private static bool WorldNameExists(string savesFolder, string worldId) =>
        Directory.EnumerateFileSystemEntries(savesFolder).Any(path =>
            string.Equals(Path.GetFileName(path), worldId, StringComparison.OrdinalIgnoreCase));

    private static void CheckNoReparsePoints(string folder, CancellationToken cancellationToken)
    {
        CheckNoReparsePoint(folder);
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InstanceWorldException(InstanceWorldFailureReason.ReparsePointNotAllowed,
                        "Worlds with reparse points cannot be copied.");
                }
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }

    private static void CheckNoReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InstanceWorldException(InstanceWorldFailureReason.ReparsePointNotAllowed,
                "World paths cannot be reparse points.");
        }
    }

    private static long? TryGetSize(string folder, CancellationToken cancellationToken)
    {
        try
        {
            long size = 0;
            var pending = new Stack<string>();
            pending.Push(folder);
            while (pending.TryPop(out var current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                    }
                    else if ((attributes & FileAttributes.Device) == 0)
                    {
                        size = checked(size + new FileInfo(entry).Length);
                    }
                }
            }

            return size;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            return null;
        }
    }

    private static byte[]? TryReadIcon(string folder, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsOrdinaryFile(folder, "icon.png")) return null;
            return ReadBounded(ContainedPath("icon.png", folder), MaximumIconBytes, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static byte[] ReadBounded(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
            {
                throw new IOException("The file exceeds the configured size limit.");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static bool IsOrdinaryFile(string directory, string name)
    {
        try
        {
            var path = ContainedPath(name, directory);
            if (!TryGetAttributes(path, out var attributes)) return false;
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;
        }
        catch (InstanceWorldException exception)
            when (exception.Error == InstanceWorldFailureReason.ReparsePointNotAllowed)
        {
            return false;
        }
    }

    private static string ContainedPath(string relativePath, string baseDirectory)
    {
        try
        {
            return InstanceStorage.ContainedPath(relativePath, baseDirectory);
        }
        catch (InstanceStorageException exception) when (exception.Error == InstanceStorageError.ReparsePointNotAllowed)
        {
            throw new InstanceWorldException(InstanceWorldFailureReason.ReparsePointNotAllowed,
                "World paths cannot contain reparse points.", exception);
        }
        catch (InstanceStorageException exception) when (exception.Error is InstanceStorageError.InvalidPath
                                                          or InstanceStorageError.PathTraversal)
        {
            throw new InstanceWorldException(InstanceWorldFailureReason.InvalidWorldId,
                "The world path is invalid.", exception);
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

    private static InstanceWorldFailureReason MapFailure(Exception exception,
        InstanceWorldFailureReason fallback)
    {
        if (exception is InstanceWorldException worldException) return worldException.Error;
        if (exception is WorldMetadataException) return InstanceWorldFailureReason.MetadataRenameFailed;
        if (exception is InstanceStorageException storageException)
        {
            return storageException.Error switch
            {
                InstanceStorageError.BuildInstalling => InstanceWorldFailureReason.BuildInstalling,
                InstanceStorageError.BuildRunning => InstanceWorldFailureReason.BuildRunning,
                InstanceStorageError.BuildFilesBusy => InstanceWorldFailureReason.BuildFilesBusy,
                InstanceStorageError.InvalidPath or InstanceStorageError.PathTraversal => InstanceWorldFailureReason.InvalidWorldId,
                InstanceStorageError.ReparsePointNotAllowed => InstanceWorldFailureReason.ReparsePointNotAllowed,
                _ => InstanceWorldFailureReason.BuildUnavailable
            };
        }
        return fallback;
    }
}

using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.Versioning;

namespace Hako.Core.Instances;

public sealed record WorldBackupManifest
{
    public const int CurrentFormat = 1;

    public sealed record SourceInstanceData(Guid Id, string Name, string FolderName, string MinecraftVersion,
        string ModLoader, string? LoaderVersion);

    public sealed record Datapack(string File, string Path, bool Enabled, bool IsDirectory, string? Sha512,
        byte[]? IconPNG);

    public int FormatVersion { get; init; } = CurrentFormat;
    public string WorldPath { get; init; } = "world";

    [JsonConverter(typeof(UtcWholeSecondDateTimeOffsetConverter))]
    public DateTimeOffset BackupCreatedAt { get; init; }
    public string Name { get; init; } = "";
    public string FolderName { get; init; } = "";
    public string? SaveVersion { get; init; }
    public int? GameType { get; init; }

    [JsonConverter(typeof(UtcWholeSecondNullableDateTimeOffsetConverter))]
    public DateTimeOffset? LastPlayed { get; init; }
    public long? Size { get; init; }
    public byte[]? IconPNG { get; init; }
    public SourceInstanceData SourceInstance { get; init; } = new(Guid.Empty, "", "", "", "vanilla", null);
    public IReadOnlyList<Datapack> Datapacks { get; init; } = [];
}

/// <summary>Creates streaming .hakoworld archives without modifying the source save.</summary>
[SupportedOSPlatform("windows")]
public sealed class WorldBackup
{
    private const int MaximumPackIconBytes = 4 * 1024 * 1024;
    private readonly InstanceStore _store;
    private readonly InstanceContent _content;
    private readonly InstanceWorlds _worlds;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action? _beforeArchiveEntry;

    public WorldBackup(InstanceStore store, InstanceContent content, InstanceWorlds worlds)
        : this(store, content, worlds, null, null)
    {
    }

    internal WorldBackup(InstanceStore store, InstanceContent content, InstanceWorlds worlds,
        Action? beforeArchiveEntry)
        : this(store, content, worlds, null, beforeArchiveEntry)
    {
    }

    internal WorldBackup(InstanceStore store, InstanceContent content, InstanceWorlds worlds,
        Func<DateTimeOffset>? utcNow, Action? beforeArchiveEntry)
    {
        _store = store;
        _content = content;
        _worlds = worlds;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _beforeArchiveEntry = beforeArchiveEntry;
    }

    public async Task<BackupResult> CreateAsync(Guid instanceId, string worldId,
        CancellationToken cancellationToken = default)
    {
        string? partialPath = null;
        var ownsPartial = false;
        BackupResult result;
        Exception? cleanupFailure = null;
        try
        {
            result = await _content.WithContentLeaseAsync(instanceId, async (root, instance, token) =>
            {
                if (instance.State != InstanceInstallationState.Ready)
                    return BackupResult.Failed(BackupFailureReason.BuildNotReady,
                        "Wait for the build installation to finish before creating a world backup.");
                EnsureReservedFolderAvailable(InstanceStorage.WorldsFolder);
                var outputDirectory = EnsureOutputDirectory(InstanceStorage.WorldsFolder);
                var source = InstanceWorlds.WorldFolder(worldId, root);
                var backupTime = _utcNow();
                var entries = await Task.Run(() => InstanceFileTree.Snapshot(source, null, token), token)
                    .ConfigureAwait(false);
                var listedWorld = (await _worlds.ListAsync(instanceId, token).ConfigureAwait(false))
                    .FirstOrDefault(world => string.Equals(world.Id, worldId, StringComparison.OrdinalIgnoreCase));
                if (listedWorld is null)
                    return BackupResult.Failed(BackupFailureReason.WorldUnavailable,
                        "The world no longer exists.", source);

                var target = Path.Combine(outputDirectory,
                    $"{instance.FolderName}_{worldId}_{backupTime.UtcDateTime.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}.hakoworld");
                if (EntryExists(target))
                    return BackupResult.Failed(BackupFailureReason.DestinationConflict,
                        "A world backup with this name already exists.", target);

                var packs = await BuildDatapacksAsync(root, instance, worldId, token).ConfigureAwait(false);
                var manifest = new WorldBackupManifest
                {
                    BackupCreatedAt = backupTime,
                    Name = listedWorld.Name,
                    FolderName = worldId,
                    SaveVersion = listedWorld.Version,
                    GameType = listedWorld.GameType,
                    LastPlayed = listedWorld.LastPlayed,
                    Size = listedWorld.Size,
                    IconPNG = listedWorld.IconData,
                    SourceInstance = new WorldBackupManifest.SourceInstanceData(
                        instance.Id,
                        instance.Name,
                        instance.FolderName,
                        instance.VersionID,
                        instance.ModLoader == InstanceModLoader.Fabric ? "fabric" : "vanilla",
                        instance.FabricConfiguration?.LoaderVersion),
                    Datapacks = packs
                };

                partialPath = Path.Combine(outputDirectory, "." + Guid.NewGuid().ToString("N") + ".partial");
                await WriteArchiveAsync(partialPath, manifest, entries, token, () => ownsPartial = true).ConfigureAwait(false);
                var latestSource = InstanceWorlds.WorldFolder(worldId, _store.GetInstanceDirectory(instanceId));
                InstanceFileTree.EnsureUnchanged(latestSource, entries, null, token);
                EnsureOutputDirectory(InstanceStorage.WorldsFolder);
                if (EntryExists(target))
                    return BackupResult.Failed(BackupFailureReason.DestinationConflict,
                        "A world backup with this name already exists.", target);
                try { File.Move(partialPath, target); }
                catch (IOException) when (EntryExists(target))
                {
                    return BackupResult.Failed(BackupFailureReason.DestinationConflict,
                        "A world backup with this name already exists.", target);
                }
                partialPath = null;
                ownsPartial = false;
                return BackupResult.Success(target);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = BackupResult.Failed(BackupFailureMapping.From(exception), exception.Message);
        }
        finally
        {
            if (ownsPartial && partialPath is not null)
            {
                try { File.Delete(partialPath); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    cleanupFailure = exception;
                }
            }
        }

        if (cleanupFailure is not null)
        {
            var failure = result.Failure;
            var diagnostic = failure?.Diagnostic ?? "Backup creation failed.";
            return BackupResult.Failed(failure?.Reason ?? BackupFailureReason.OperationFailed,
                $"{diagnostic} Cleanup failed: {cleanupFailure.Message}", partialPath);
        }

        return result;
    }

    private async Task<IReadOnlyList<WorldBackupManifest.Datapack>> BuildDatapacksAsync(string root,
        GameInstance instance, string worldId, CancellationToken cancellationToken)
    {
        var target = InstanceContentTarget.WorldDatapacks(worldId);
        var items = await _content.ListUnderLeaseAsync(root, instance, target, true, cancellationToken)
            .ConfigureAwait(false);
        var packs = new List<WorldBackupManifest.Datapack>(items.Count);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = item.Name;
            var folder = item.DisabledPack ? ".hako-disabled-datapacks" : "datapacks";
            var hash = item.IsDirectory ? null : await InstanceFileTree.Sha512Async(item.Path, cancellationToken)
                .ConfigureAwait(false);
            var icon = await TryReadPackIconAsync(item, cancellationToken).ConfigureAwait(false);
            packs.Add(new WorldBackupManifest.Datapack(name, $"{folder}/{name}", item.Enabled,
                item.IsDirectory, hash, icon));
        }
        return packs;
    }

    private static async Task<byte[]?> TryReadPackIconAsync(InstanceContentItem item,
        CancellationToken cancellationToken)
    {
        try
        {
            if (item.IsDirectory)
            {
                var icon = InstanceStorage.ContainedPath("pack.png", item.Path);
                if (!File.Exists(icon)) return null;
                var attributes = File.GetAttributes(icon);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                    || new FileInfo(icon).Length > MaximumPackIconBytes) return null;
                return await File.ReadAllBytesAsync(icon, cancellationToken).ConfigureAwait(false);
            }

            await using var input = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            var entry = archive.GetEntry("pack.png");
            if (entry is null || entry.Length > MaximumPackIconBytes) return null;
            await using var stream = entry.Open();
            using var output = new MemoryStream((int)entry.Length);
            var buffer = new byte[81920];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > MaximumPackIconBytes) return null;
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void EnsureReservedFolderAvailable(string name)
    {
        if (_store.List().Any(instance => string.Equals(instance.FolderName, name, StringComparison.OrdinalIgnoreCase)))
            throw new BackupFolderOccupiedException(name);
    }

    private string EnsureOutputDirectory(string name)
    {
        var path = InstanceStorage.ContainedPath(name, _store.GameRoot, allowBaseReparsePoint: true);
        if (!Directory.Exists(path) && !EntryExists(path))
        {
            try { InstanceStorage.CreateDirectoryExclusiveAt(_store.GameRoot, name); }
            catch (IOException) when (EntryExists(path)) { }
            path = InstanceStorage.ContainedPath(name, _store.GameRoot, allowBaseReparsePoint: true);
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != FileAttributes.Directory)
            throw new BackupFolderOccupiedException(name);
        return path;
    }

    private async Task WriteArchiveAsync(string partialPath, WorldBackupManifest manifest,
        IReadOnlyList<InstanceFileTreeEntry> entries, CancellationToken cancellationToken, Action onCreated)
    {
        await using var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        onCreated();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var data = archive.CreateEntry("data.json", CompressionLevel.Optimal);
            await using (var stream = data.Open())
                await JsonSerializer.SerializeAsync(stream, manifest, HakoJsonContext.Default.WorldBackupManifest,
                    cancellationToken).ConfigureAwait(false);
            await InstanceFileTree.AddToArchiveAsync(archive, entries, "world/", cancellationToken,
                _beforeArchiveEntry).ConfigureAwait(false);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    internal static bool EntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}

using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hako.Core.Instances;

public sealed record InstanceBackupManifest
{
    public const int CurrentFormat = 1;

    public sealed record Mod(string File, bool Enabled, string Source, string? ProjectID, string? VersionID);

    public sealed record ProfileData(
        string MetadataURL,
        string MetadataSHA1,
        LaunchArgumentSource ArgumentSource,
        bool OfflineMode,
        string OfflineUsername,
        InstanceParameters Parameters,
        string JavaExecutable,
        bool LegacyTexturepacks,
        FabricConfiguration? FabricConfiguration,
        string? FabricProfileSHA1);

    public int FormatVersion { get; init; } = CurrentFormat;

    [JsonConverter(typeof(UtcWholeSecondDateTimeOffsetConverter))]
    public DateTimeOffset BackupCreatedAt { get; init; }
    public string Name { get; init; } = "";
    public string FolderName { get; init; } = "";

    [JsonConverter(typeof(UtcWholeSecondDateTimeOffsetConverter))]
    public DateTimeOffset InstanceCreatedAt { get; init; }
    public string MinecraftVersion { get; init; } = "";
    public string ModLoader { get; init; } = "vanilla";
    public string? LoaderVersion { get; init; }
    public int JavaMajorVersion { get; init; }
    public string IconSymbol { get; init; } = "";
    public byte[]? IconPNG { get; init; }
    public int ModCount { get; init; }
    public IReadOnlyList<Mod> Mods { get; init; } = [];
    public ProfileData Profile { get; init; } = new("", "", LaunchArgumentSource.Mojang, false, "Player",
        new InstanceParameters(), "bin/javaw.exe", false, null, null);
}

/// <summary>Creates Swift-compatible .hakobackup files. Restore/import is intentionally not implemented.</summary>
public sealed class InstanceBackup
{
    private readonly InstanceStore _store;
    private readonly InstanceContent _content;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action? _beforeArchiveEntry;

    public InstanceBackup(InstanceStore store, InstanceContent content)
        : this(store, content, null, null)
    {
    }

    internal InstanceBackup(InstanceStore store, InstanceContent content, Action? beforeArchiveEntry)
        : this(store, content, null, beforeArchiveEntry)
    {
    }

    internal InstanceBackup(InstanceStore store, InstanceContent content, Func<DateTimeOffset>? utcNow,
        Action? beforeArchiveEntry)
    {
        _store = store;
        _content = content;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _beforeArchiveEntry = beforeArchiveEntry;
    }

    public async Task<BackupResult> CreateAsync(Guid instanceId, CancellationToken cancellationToken = default)
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
                        "Wait for the build installation to finish before creating a backup.");
                EnsureReservedFolderAvailable(InstanceStorage.BackupsFolder);
                var outputDirectory = EnsureOutputDirectory(InstanceStorage.BackupsFolder);
                var backupTime = _utcNow();
                var target = Path.Combine(outputDirectory,
                    $"{instance.FolderName}_{backupTime.UtcDateTime.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}.hakobackup");
                if (EntryExists(target))
                    return BackupResult.Failed(BackupFailureReason.DestinationConflict,
                        "A backup with this name already exists.", target);

                var modItems = await ListModsAsync(root, instance, token).ConfigureAwait(false);
                var mods = modItems.Select(item => new InstanceBackupManifest.Mod(
                    item.LogicalName,
                    item.Enabled,
                    item.Origin?.Source switch
                    {
                        ModSource.Modrinth => "modrinth",
                        ModSource.CurseForge => "curseForge",
                        _ => "local"
                    },
                    item.Origin?.ProjectID,
                    item.Origin?.VersionID)).ToArray();
                var icon = ReadBuildIcon(root, instance);
                var loader = instance.ModLoader == InstanceModLoader.Fabric ? "fabric" : "vanilla";
                var manifest = new InstanceBackupManifest
                {
                    BackupCreatedAt = backupTime,
                    Name = instance.Name,
                    FolderName = instance.FolderName,
                    InstanceCreatedAt = instance.CreatedAt,
                    MinecraftVersion = instance.VersionID,
                    ModLoader = loader,
                    LoaderVersion = instance.FabricConfiguration?.LoaderVersion,
                    JavaMajorVersion = instance.JavaMajorVersion,
                    IconSymbol = instance.IconSymbol,
                    IconPNG = icon,
                    ModCount = mods.Length,
                    Mods = mods,
                    Profile = new InstanceBackupManifest.ProfileData(
                        instance.MetadataURL,
                        instance.MetadataSHA1,
                        instance.ArgumentSource,
                        instance.OfflineMode,
                        instance.OfflineUsername,
                        instance.Parameters,
                        instance.JavaExecutable,
                        instance.LegacyTexturepacks,
                        instance.FabricConfiguration,
                        instance.FabricProfileSHA1)
                };

                var excluded = IsExcludedPath;
                var entries = await Task.Run(() => InstanceFileTree.Snapshot(root, excluded, token), token)
                    .ConfigureAwait(false);
                partialPath = Path.Combine(outputDirectory, "." + Guid.NewGuid().ToString("N") + ".partial");
                await WriteArchiveAsync(partialPath, manifest, entries, token, () => ownsPartial = true).ConfigureAwait(false);
                InstanceFileTree.EnsureUnchanged(_store.GetInstanceDirectory(instanceId), entries, excluded, token);
                EnsureOutputDirectory(InstanceStorage.BackupsFolder);
                if (EntryExists(target))
                    return BackupResult.Failed(BackupFailureReason.DestinationConflict,
                        "A backup with this name already exists.", target);
                try { File.Move(partialPath, target); }
                catch (IOException) when (EntryExists(target))
                {
                    return BackupResult.Failed(BackupFailureReason.DestinationConflict,
                        "A backup with this name already exists.", target);
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

    private async Task<IReadOnlyList<InstanceContentItem>> ListModsAsync(string root, GameInstance instance,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _content.ListUnderLeaseAsync(root, instance, InstanceContentTarget.Mods, true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return await _content.ListUnderLeaseAsync(root, instance, InstanceContentTarget.Mods, true,
                cancellationToken, readOrigins: false).ConfigureAwait(false);
        }
    }

    private static byte[]? ReadBuildIcon(string root, GameInstance instance)
    {
        if (!string.IsNullOrEmpty(instance.IconSymbol)) return null;
        var path = InstanceStorage.ContainedPath("icon.png", root, allowBaseReparsePoint: true);
        if (!EntryExists(path)) return null;
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            return null;
        if (new FileInfo(path).Length > 4 * 1024 * 1024) return null;
        return File.ReadAllBytes(path);
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

    private async Task WriteArchiveAsync(string partialPath, InstanceBackupManifest manifest,
        IReadOnlyList<InstanceFileTreeEntry> entries, CancellationToken cancellationToken, Action onCreated)
    {
        await using var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        onCreated();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var data = archive.CreateEntry("data.json", CompressionLevel.Optimal);
            await using (var stream = data.Open())
                await JsonSerializer.SerializeAsync(stream, manifest, HakoJsonContext.Default.InstanceBackupManifest,
                    cancellationToken).ConfigureAwait(false);
            await InstanceFileTree.AddToArchiveAsync(archive, entries, "", cancellationToken,
                _beforeArchiveEntry).ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    internal static bool IsExcludedPath(string path) =>
        path.Equals("minecraft/.hako-running.json", StringComparison.OrdinalIgnoreCase)
        || path.Equals("minecraft/.hako-launch", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("minecraft/.hako-launch/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("minecraft/logs/hako-launch.log", StringComparison.OrdinalIgnoreCase)
        || path.Equals(".hako-game", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(".hako-game/", StringComparison.OrdinalIgnoreCase);

    internal static bool EntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

}

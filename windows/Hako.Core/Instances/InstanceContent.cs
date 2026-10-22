using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hako.Core;
using Hako.Core.Minecraft;

namespace Hako.Core.Instances;

[JsonConverter(typeof(ModSourceJsonConverter))]
public enum ModSource
{
    Local,
    Modrinth,
    CurseForge
}

public sealed record ModOrigin
{
    [JsonRequired]
    public ModSource Source { get; init; }
    [JsonRequired]
    public string ProjectID { get; init; } = "";
    [JsonRequired]
    public string VersionID { get; init; } = "";
    [JsonRequired]
    public Uri PageURL { get; init; } = new("https://modrinth.com/");
    [JsonRequired]
    public string Sha512 { get; init; } = "";
    public FabricApiDescriptor? Api { get; init; }
    public string? VersionNumber { get; init; }
    public Uri? IconURL { get; init; }
    public string? FileType { get; init; }

    public static ModOrigin FromApi(FabricApiDescriptor api) => new()
    {
        Source = ModSource.Modrinth,
        ProjectID = api.ProjectID,
        VersionID = api.VersionID,
        PageURL = new Uri($"https://modrinth.com/mod/fabric-api/version/{Uri.EscapeDataString(api.VersionID)}"),
        Sha512 = api.Sha512,
        Api = api,
        VersionNumber = api.Version
    };
}

public sealed record ModRegistry
{
    [JsonRequired]
    public bool ApiProvisioned { get; init; }
    [JsonRequired]
    public Dictionary<string, ModOrigin> Files { get; init; } = new(StringComparer.Ordinal);
}

public sealed record InstanceContentItem(string Path, bool IsDirectory, ModOrigin? Origin, DateTimeOffset? ModifiedAt,
    bool DisabledPack = false)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string LogicalName => Name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase) ? Name[..^9] : Name;
    public bool Enabled => !DisabledPack && !Name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);
    public ModSource Source => Origin?.Source ?? ModSource.Local;
}

internal sealed class ModSourceJsonConverter : JsonConverter<ModSource>
{
    public override ModSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "local" => ModSource.Local,
            "modrinth" => ModSource.Modrinth,
            "curseForge" => ModSource.CurseForge,
            _ => throw new JsonException("Unknown content source.")
        };

    public override void Write(Utf8JsonWriter writer, ModSource value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            ModSource.Local => "local",
            ModSource.Modrinth => "modrinth",
            ModSource.CurseForge => "curseForge",
            _ => throw new JsonException("Unknown content source.")
        });
}

/// <summary>Minimal Fabric content operations. InstanceStore leases serialize them with installation and launch.</summary>
public sealed class InstanceContent
{
    private const string RegistryName = ".hako-mods.json";
    private readonly InstanceStore store;
    private readonly RecycleBin recycleBin;
    private readonly Action? beforeRegistryCommit;
    private readonly Action? beforeReplacementPublish;

    [SupportedOSPlatform("windows")]
    public InstanceContent(InstanceStore store, RecycleBin? recycleBin = null)
        : this(store, recycleBin, null)
    {
    }

    [SupportedOSPlatform("windows")]
    internal InstanceContent(InstanceStore store, RecycleBin? recycleBin, Action? beforeRegistryCommit,
        Action? beforeReplacementPublish = null)
    {
        this.store = store;
        this.recycleBin = recycleBin ?? new RecycleBin();
        this.beforeRegistryCommit = beforeRegistryCommit;
        this.beforeReplacementPublish = beforeReplacementPublish;
    }

    /// <summary>Lists regular JARs, ZIPs, and resourcepack folders without following reparse points.</summary>
    public async Task<IReadOnlyList<InstanceContentItem>> ListAsync(Guid instanceId, ModrinthContentKind kind,
        bool includeDisabled = true, CancellationToken cancellationToken = default)
    {
        return await ListAsync(instanceId, kind, includeDisabled, readOrigins: true, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<InstanceContentItem>> ListAsync(Guid instanceId, ModrinthContentKind kind,
        bool includeDisabled, bool readOrigins, CancellationToken cancellationToken = default)
    {
        var instance = store.List().FirstOrDefault(candidate => candidate.Id == instanceId)
            ?? throw new InstanceStorageException(InstanceStorageError.InstanceRegistryUnavailable, "The build no longer exists.");
        var root = await Task.Run(() => store.GetInstanceDirectory(instanceId), cancellationToken).ConfigureAwait(false);
        return await ListUnderLeaseAsync(root, instance, kind, includeDisabled, cancellationToken, readOrigins).ConfigureAwait(false);
    }

    /// <summary>Imports a local JAR, ZIP, or resourcepack folder. Local imports clear stored Modrinth origin.</summary>
    public Task ImportAsync(Guid instanceId, ModrinthContentKind kind, string sourcePath, bool replace = false,
        CancellationToken cancellationToken = default) =>
        WithContentLeaseAsync(instanceId, (root, instance, token) => ImportUnderLeaseAsync(root, instance, kind,
            sourcePath, replace, origin: null, token), cancellationToken);

    /// <summary>Enables or disables one previously listed item while preserving its logical file and origin.</summary>
    public Task SetEnabledAsync(Guid instanceId, InstanceContentItem item, ModrinthContentKind kind, bool enabled,
        CancellationToken cancellationToken = default) =>
        WithContentLeaseAsync(instanceId, (root, instance, token) => Task.Run(
            () => SetEnabledUnderLease(root, instance, item, kind, enabled), token), cancellationToken);

    /// <summary>Moves an item to the Windows Recycle Bin; a non-recyclable or failed delete leaves its registry entry.</summary>
    [SupportedOSPlatform("windows")]
    public Task<RecycleBinItemStatus> TrashAsync(Guid instanceId, InstanceContentItem item, ModrinthContentKind kind,
        CancellationToken cancellationToken = default) =>
        WithContentLeaseAsync(instanceId, (root, instance, token) => TrashUnderLeaseAsync(root, instance, item, kind, token),
            cancellationToken);

    /// <summary>Replaces a regular file only if its currently installed SHA-512 still matches the caller's snapshot.</summary>
    public Task ReplaceAsync(Guid instanceId, InstanceContentItem item, ModrinthContentKind kind, string sourcePath,
        string filename, string expectedSha512, ModOrigin? origin = null, CancellationToken cancellationToken = default) =>
        WithContentLeaseAsync(instanceId, (root, instance, token) => ReplaceUnderLeaseAsync(root, instance, item, kind,
            sourcePath, filename, expectedSha512, origin, token), cancellationToken);

    /// <summary>
    /// Internal transaction boundary for the upcoming catalog controller. It can hold one content lease across
    /// downloads and several nested publications without trying to acquire another lease.
    /// </summary>
    internal async Task<TResult> WithContentLeaseAsync<TResult>(Guid instanceId,
        Func<string, GameInstance, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var lease = await BeginContentAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var instance = store.List().FirstOrDefault(candidate => candidate.Id == instanceId)
            ?? throw new InstanceStorageException(InstanceStorageError.InstanceRegistryUnavailable, "The build no longer exists.");
        var root = await Task.Run(() => store.GetInstanceDirectory(instanceId), cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => operation(root, instance, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    internal async Task WithContentLeaseAsync(Guid instanceId,
        Func<string, GameInstance, CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        await WithContentLeaseAsync(instanceId, async (root, instance, token) =>
        {
            await operation(root, instance, token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task ImportUnderLeaseAsync(string instanceRoot, GameInstance instance, ModrinthContentKind kind,
        string sourcePath, bool replace, ModOrigin? origin, CancellationToken cancellationToken)
    {
        var folder = ContentFolder(instanceRoot, instance, kind);
        await ImportAtAsync(instanceRoot, folder, kind, sourcePath, replace, origin, cancellationToken).ConfigureAwait(false);
    }

    internal Task<IReadOnlyList<InstanceContentItem>> ListUnderLeaseAsync(string instanceRoot, GameInstance instance,
        ModrinthContentKind kind, bool includeDisabled, CancellationToken cancellationToken, bool readOrigins = true) =>
        Task.Run(() => ListAtAsync(instanceRoot, instance, kind, includeDisabled, cancellationToken, readOrigins), cancellationToken);

    internal async Task RecordOriginUnderLeaseAsync(string instanceRoot, GameInstance instance, InstanceContentItem item,
        ModrinthContentKind kind, string expectedSha512, ModOrigin origin, CancellationToken cancellationToken)
    {
        if (item.IsDirectory || !IsHash(expectedSha512, 128) || !IsHash(origin.Sha512, 128)
            || !string.Equals(expectedSha512, origin.Sha512, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The content origin does not match a regular file checksum.");
        var folder = ContentFolder(instanceRoot, instance, kind);
        var path = CheckedItemPath(instanceRoot, folder, kind, item);
        if (!string.Equals(await HashFileAsync(path, cancellationToken).ConfigureAwait(false), expectedSha512,
            StringComparison.OrdinalIgnoreCase))
            throw new IOException("The installed content changed before origin recording.");
        var registryName = RegistryNameFor(kind);
        var registry = ReadRegistry(instanceRoot, registryName);
        var files = new Dictionary<string, ModOrigin>(registry.Files, StringComparer.Ordinal)
        {
            [LogicalContentName(kind, item.Name).ToLowerInvariant()] = origin
        };
        cancellationToken.ThrowIfCancellationRequested();
        CommitRegistry(instanceRoot, registryName, registry with { Files = files });
    }

    internal async Task ValidateExpectedHashUnderLeaseAsync(string instanceRoot, GameInstance instance,
        InstanceContentItem item, ModrinthContentKind kind, string expectedSha512, CancellationToken cancellationToken)
    {
        if (item.IsDirectory || !IsHash(expectedSha512, 128))
            throw new IOException("The installed content snapshot is invalid.");
        var folder = ContentFolder(instanceRoot, instance, kind);
        var path = CheckedItemPath(instanceRoot, folder, kind, item);
        if (!string.Equals(await HashFileAsync(path, cancellationToken).ConfigureAwait(false), expectedSha512,
            StringComparison.OrdinalIgnoreCase))
            throw new IOException("The installed content changed before replacement.");
    }

    internal async Task ReplaceUnderLeaseAsync(string instanceRoot, GameInstance instance, InstanceContentItem item,
        ModrinthContentKind kind, string sourcePath, string filename, string expectedSha512, ModOrigin? origin,
        CancellationToken cancellationToken, InstanceContentItem? confirmedCollision = null)
    {
        var folder = ContentFolder(instanceRoot, instance, kind);
        await ReplaceAtAsync(instanceRoot, folder, kind, item, sourcePath, filename, expectedSha512, origin,
            cancellationToken, confirmedCollision).ConfigureAwait(false);
    }

    internal void SetEnabledUnderLease(string instanceRoot, GameInstance instance, InstanceContentItem item,
        ModrinthContentKind kind, bool enabled)
    {
        var folder = ContentFolder(instanceRoot, instance, kind);
        SetEnabledAt(instanceRoot, folder, kind, item, enabled);
    }

    [SupportedOSPlatform("windows")]
    internal async Task<RecycleBinItemStatus> TrashUnderLeaseAsync(string instanceRoot, GameInstance instance,
        InstanceContentItem item, ModrinthContentKind kind, CancellationToken cancellationToken)
    {
        var folder = ContentFolder(instanceRoot, instance, kind);
        return await TrashAtAsync(instanceRoot, folder, kind, item, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<InstanceContentItem>> ListAtAsync(string instanceRoot, GameInstance instance,
        ModrinthContentKind kind, bool includeDisabled, CancellationToken cancellationToken, bool readOrigins = true)
    {
        var folder = ContentFolder(instanceRoot, instance, kind);
        var registry = readOrigins ? ReadRegistry(instanceRoot, RegistryNameFor(kind)) : new ModRegistry();
        var disabledFolder = IsPack(kind) ? DisabledPacksFolder(instanceRoot, folder) : null;
        var folders = new List<(string Path, bool Disabled)> { (folder, false) };
        if (includeDisabled && disabledFolder is not null) folders.Add((disabledFolder, true));
        var items = new List<InstanceContentItem>();
        foreach (var (directory, disabled) in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory)) continue;
            EnsureDirectoryIsSafe(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var name = Path.GetFileName(path);
                var logicalName = kind == ModrinthContentKind.Mod ? LogicalJarName(name) : name;
                var accepted = kind switch
                {
                    ModrinthContentKind.Mod => !isDirectory && (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                        || includeDisabled && name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)),
                    ModrinthContentKind.ResourcePack => isDirectory ? HasPackMetadata(instanceRoot, path)
                        : name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
                    _ => false
                };
                if (!accepted) continue;
                var origin = !isDirectory && registry.Files.TryGetValue(logicalName.ToLowerInvariant(), out var found) ? found : null;
                if (origin is not null && !string.Equals(await HashFileAsync(path, cancellationToken).ConfigureAwait(false),
                    origin.Sha512, StringComparison.OrdinalIgnoreCase))
                    origin = null;
                var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                items.Add(new InstanceContentItem(path, isDirectory, origin, modified,
                    kind == ModrinthContentKind.Mod ? name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase) : disabled));
            }
        }

        return items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static string ContentFolder(string instanceRoot, GameInstance instance, ModrinthContentKind kind)
    {
        var relative = kind switch
        {
            ModrinthContentKind.Mod => "minecraft/mods",
            ModrinthContentKind.ResourcePack => instance.LegacyTexturepacks ? "minecraft/texturepacks" : "minecraft/resourcepacks",
            _ => throw new NotSupportedException("Local datapack operations are implemented in a later stage.")
        };
        return InstanceStorage.ContainedPath(relative, instanceRoot, allowBaseReparsePoint: true);
    }

    private static string? DisabledPacksFolder(string instanceRoot, string folder)
    {
        var name = ".hako-disabled-" + Path.GetFileName(folder);
        return InstanceStorage.ContainedPath("minecraft/" + name, instanceRoot, allowBaseReparsePoint: true);
    }

    private static bool IsPack(ModrinthContentKind kind) => kind == ModrinthContentKind.ResourcePack;

    private static string RegistryNameFor(ModrinthContentKind kind) => kind switch
    {
        ModrinthContentKind.Mod => RegistryName,
        ModrinthContentKind.ResourcePack => ".hako-resourcepacks.json",
        _ => throw new NotSupportedException("Local datapack operations are implemented in a later stage.")
    };

    private async Task ImportAtAsync(string instanceRoot, string folder, ModrinthContentKind kind,
        string sourcePath, bool replace, ModOrigin? origin, CancellationToken cancellationToken)
    {
        var source = ValidateImportSource(sourcePath, kind);
        if (Directory.Exists(source) && IsPathInside(source, folder))
            throw new IOException("A resourcepack folder cannot be imported into itself or one of its children.");
        if (origin is not null && (kind == ModrinthContentKind.ResourcePack && Directory.Exists(source)))
            throw new ArgumentException("A directory resourcepack cannot have a file origin.", nameof(origin));
        if (origin is not null && !IsHash(origin.Sha512, 128))
            throw new InvalidDataException("The content origin has an invalid SHA-512.");
        var registryName = RegistryNameFor(kind);
        var registry = ReadRegistry(instanceRoot, registryName);
        Directory.CreateDirectory(folder);
        EnsureDirectoryIsSafe(folder);
        var name = Path.GetFileName(source);
        ValidateContentName(name);
        var existing = FindExisting(instanceRoot, folder, kind, name);
        if (existing is not null && !replace) throw new IOException($"Content named '{name}' already exists.");
        var disabled = kind == ModrinthContentKind.Mod && existing is not null
            && existing.Value.Name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);
        var destinationFolder = existing?.Disabled == true && IsPack(kind)
            ? DisabledPacksFolder(instanceRoot, folder)!
            : folder;
        var finalName = kind == ModrinthContentKind.Mod && disabled && !name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? name + ".disabled" : name;
        var destination = ChildPath(instanceRoot, destinationFolder, finalName);
        var staged = ChildPath(instanceRoot, folder, ".hako-import-" + Guid.NewGuid().ToString("N") + ".tmp");
        var backup = existing is null ? null : existing.Value.Path + ".hako-backup-" + Guid.NewGuid().ToString("N");
        var backupCreated = false;
        try
        {
            await CopyToStageAsync(source, staged, cancellationToken).ConfigureAwait(false);
            if (origin is not null && !string.Equals(await HashFileAsync(staged, cancellationToken).ConfigureAwait(false),
                origin.Sha512, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The imported file does not match its Modrinth origin.");
            cancellationToken.ThrowIfCancellationRequested();
            if (existing is not null)
            {
                MoveEntry(existing.Value.Path, backup!);
                backupCreated = true;
            }
            try
            {
                MoveEntry(staged, destination);
                var files = new Dictionary<string, ModOrigin>(registry.Files, StringComparer.Ordinal);
                if (existing is not null) files.Remove(LogicalContentName(kind, existing.Value.Name).ToLowerInvariant());
                var key = LogicalContentName(kind, finalName).ToLowerInvariant();
                files.Remove(key);
                if (origin is not null) files[key] = origin;
                CommitRegistry(instanceRoot, registryName, registry with { Files = files });
            }
            catch (Exception original)
            {
                RollBackPublishedEntry(original, destination, backup, existing?.Path, backupCreated);
                throw;
            }

            if (backup is not null) TryDeleteEntry(backup);
        }
        finally
        {
            TryDeleteEntry(staged);
        }
    }

    private async Task ReplaceAtAsync(string instanceRoot, string folder, ModrinthContentKind kind,
        InstanceContentItem item, string sourcePath, string filename, string expectedSha512, ModOrigin? origin,
        CancellationToken cancellationToken, InstanceContentItem? confirmedCollision)
    {
        ValidateContentFilename(filename, kind);
        if (!IsHash(expectedSha512, 128)) throw new ArgumentException("An expected SHA-512 is required.", nameof(expectedSha512));
        if (item.IsDirectory) throw new IOException("Pack folders cannot be replaced by a file update.");
        var old = CheckedItemPath(instanceRoot, folder, kind, item);
        if (!string.Equals(await HashFileAsync(old, cancellationToken).ConfigureAwait(false), expectedSha512,
            StringComparison.OrdinalIgnoreCase))
            throw new IOException("The installed content changed before replacement.");
        var source = ValidateImportSource(sourcePath, kind);
        if (string.Equals(Path.GetFullPath(source), old, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The replacement source must be a separate staged file.");
        if (Directory.Exists(source)) throw new IOException("A content update must be a regular file.");
        if (origin is not null && !IsHash(origin.Sha512, 128)) throw new InvalidDataException("The content origin has an invalid SHA-512.");
        var registryName = RegistryNameFor(kind);
        var registry = ReadRegistry(instanceRoot, registryName);
        var match = FindExisting(instanceRoot, folder, kind, filename);
        var collisionPath = match is not null && !PathEquals(match.Value.Path, old) ? match.Value.Path : null;
        if (collisionPath is not null && (confirmedCollision is null
            || !PathEquals(CheckedItemPath(instanceRoot, folder, kind, confirmedCollision), collisionPath)))
            throw new IOException($"Content named '{filename}' already exists.");
        if (confirmedCollision is not null && collisionPath is null)
            throw new IOException("The confirmed collision changed before replacement.");
        var disabled = kind == ModrinthContentKind.Mod ? !item.Enabled : item.DisabledPack;
        var destinationFolder = disabled && IsPack(kind)
            ? DisabledPacksFolder(instanceRoot, folder)!
            : folder;
        var finalName = kind == ModrinthContentKind.Mod && disabled ? filename + ".disabled" : filename;
        var target = ChildPath(instanceRoot, destinationFolder, finalName);
        var staged = ChildPath(instanceRoot, folder, ".hako-update-" + Guid.NewGuid().ToString("N") + ".tmp");
        var backup = old + ".hako-backup-" + Guid.NewGuid().ToString("N");
        var backupCreated = false;
        var collisionBackup = collisionPath is null ? null : collisionPath + ".hako-backup-" + Guid.NewGuid().ToString("N");
        var collisionBackupCreated = false;
        var published = false;
        try
        {
            await CopyFileAsync(source, staged, cancellationToken).ConfigureAwait(false);
            if (origin is not null && !string.Equals(await HashFileAsync(staged, cancellationToken).ConfigureAwait(false),
                origin.Sha512, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The replacement file does not match its Modrinth origin.");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (collisionPath is not null)
                {
                    MoveEntry(collisionPath, collisionBackup!);
                    collisionBackupCreated = true;
                }
                beforeReplacementPublish?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                MoveEntry(old, backup);
                backupCreated = true;
                MoveEntry(staged, target);
                published = true;
                var files = new Dictionary<string, ModOrigin>(registry.Files, StringComparer.Ordinal);
                files.Remove(item.LogicalName.ToLowerInvariant());
                files.Remove(filename.ToLowerInvariant());
                if (origin is not null) files[filename.ToLowerInvariant()] = origin;
                CommitRegistry(instanceRoot, registryName, registry with { Files = files });
            }
            catch (Exception original)
            {
                RollBackPublishedEntry(original, target, backup, old, backupCreated,
                    collisionBackup, collisionPath, collisionBackupCreated, published);
                throw;
            }

            TryDeleteEntry(backup);
            if (collisionBackupCreated)
            {
                // Registry commit is the boundary: finish recycling even if the caller cancels now.
                if (!OperatingSystem.IsWindows())
                    throw new IOException($"Content replacement committed. Recycling requires Windows; backup remains at '{collisionBackup}'.");
                var result = await recycleBin.DeleteAsync(collisionBackup!, CancellationToken.None).ConfigureAwait(false);
                if (result.Items.Count != 1 || result.Items[0].Status != RecycleBinItemStatus.Recycled)
                    throw new IOException("Content replacement committed, but recycling the displaced file failed. "
                        + $"Backup remains at '{collisionBackup}'. "
                        + string.Join("; ", result.Items.Select(entry => $"{entry.Status}: {entry.Error}")));
            }
        }
        finally
        {
            TryDeleteEntry(staged);
        }
    }

    private static void SetEnabledAt(string instanceRoot, string folder, ModrinthContentKind kind,
        InstanceContentItem item, bool enabled)
    {
        var source = CheckedItemPath(instanceRoot, folder, kind, item);
        if (item.Enabled == enabled) return;
        var destinationFolder = kind == ModrinthContentKind.Mod || enabled
            ? folder
            : DisabledPacksFolder(instanceRoot, folder)!;
        var name = kind == ModrinthContentKind.Mod
            ? item.LogicalName + (enabled ? "" : ".disabled")
            : item.Name;
        var collision = FindExisting(instanceRoot, folder, kind, name, source);
        if (collision is not null) throw new IOException($"Content named '{name}' already exists.");
        Directory.CreateDirectory(destinationFolder);
        EnsureDirectoryIsSafe(destinationFolder);
        MoveEntry(source, ChildPath(instanceRoot, destinationFolder, name));
    }

    [SupportedOSPlatform("windows")]
    private async Task<RecycleBinItemStatus> TrashAtAsync(string instanceRoot, string folder, ModrinthContentKind kind,
        InstanceContentItem item, CancellationToken cancellationToken)
    {
        var path = CheckedItemPath(instanceRoot, folder, kind, item);
        var registryName = RegistryNameFor(kind);
        var registry = ReadRegistry(instanceRoot, registryName);
        var result = await recycleBin.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        if (result.Items.Count != 1) throw new IOException("The Recycle Bin returned an invalid result.");
        var status = result.Items[0].Status;
        if (status != RecycleBinItemStatus.Recycled) return status;
        var key = item.LogicalName.ToLowerInvariant();
        if (!registry.Files.ContainsKey(key)) return status;
        var files = new Dictionary<string, ModOrigin>(registry.Files, StringComparer.Ordinal);
        files.Remove(key);
        await Task.Run(() => CommitRegistry(instanceRoot, registryName, registry with { Files = files }), CancellationToken.None)
            .ConfigureAwait(false);
        return status;
    }

    private void CommitRegistry(string instanceRoot, string registryName, ModRegistry registry)
    {
        beforeRegistryCommit?.Invoke();
        WriteRegistry(instanceRoot, registryName, registry);
    }

    private static (string Path, string Name, bool Disabled)? FindExisting(string instanceRoot, string folder,
        ModrinthContentKind kind, string name, string? exceptPath = null)
    {
        ValidateContentName(name);
        var logical = LogicalContentName(kind, name);
        var candidates = new List<(string Path, string Name, bool Disabled)>();
        var folders = new List<(string Path, bool Disabled)> { (folder, false) };
        if (IsPack(kind) && DisabledPacksFolder(instanceRoot, folder) is { } disabled) folders.Add((disabled, true));
        foreach (var (directory, isDisabled) in folders)
        {
            if (!Directory.Exists(directory)) continue;
            EnsureDirectoryIsSafe(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var existingName = Path.GetFileName(path);
                if (!string.Equals(LogicalContentName(kind, existingName), logical, StringComparison.OrdinalIgnoreCase)) continue;
                if (exceptPath is not null && PathEquals(path, exceptPath)) continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A colliding content entry is a reparse point.");
                candidates.Add((path, existingName, kind == ModrinthContentKind.Mod
                    ? existingName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase) : isDisabled));
            }
        }

        if (candidates.Count > 1) throw new IOException($"More than one content entry matches '{name}'.");
        return candidates.Count == 0 ? null : candidates[0];
    }

    private static string ValidateImportSource(string sourcePath, ModrinthContentKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var source = Path.GetFullPath(sourcePath);
        var attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Content sources cannot be reparse points.");
        var directory = (attributes & FileAttributes.Directory) != 0;
        if (kind == ModrinthContentKind.Mod)
        {
            if (directory || !source.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choose a regular JAR file for a mod.");
        }
        else if (kind == ModrinthContentKind.ResourcePack)
        {
            if (directory)
            {
                _ = InstanceStorage.ContainedPath("pack.mcmeta", source);
                var metadata = Path.Combine(source, "pack.mcmeta");
                var metadataAttributes = File.GetAttributes(metadata);
                if ((metadataAttributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new IOException("A resourcepack folder must contain a regular pack.mcmeta file.");
                ValidateDirectoryTree(source);
            }
            else if (!source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Choose a ZIP file or a folder with pack.mcmeta for a resourcepack.");
            }
        }
        else
        {
            throw new NotSupportedException("Local datapack operations are implemented in a later stage.");
        }

        ValidateContentName(Path.GetFileName(source));
        return source;
    }

    private static void ValidateDirectoryTree(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                ValidateContentName(Path.GetFileName(entry));
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Resourcepack folders cannot contain reparse points.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }

    private static async Task CopyToStageAsync(string source, string staged, CancellationToken cancellationToken)
    {
        if (Directory.Exists(source)) await CopyDirectoryAsync(source, staged, cancellationToken).ConfigureAwait(false);
        else await CopyFileAsync(source, staged, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry);
            ValidateContentName(name);
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Resourcepack folders cannot contain reparse points.");
            var target = Path.Combine(destination, name);
            if ((attributes & FileAttributes.Directory) != 0)
                await CopyDirectoryAsync(entry, target, cancellationToken).ConfigureAwait(false);
            else
                await CopyFileAsync(entry, target, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var attributes = File.GetAttributes(source);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Content files must be regular files.");
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Only regular files can be hashed.");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool HasPackMetadata(string instanceRoot, string path)
    {
        var metadata = InstanceStorage.ContainedPath(Path.GetRelativePath(instanceRoot, Path.Combine(path, "pack.mcmeta")),
            instanceRoot, allowBaseReparsePoint: true);
        if (!File.Exists(metadata)) return false;
        return (File.GetAttributes(metadata) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
    }

    private static string CheckedItemPath(string instanceRoot, string folder, ModrinthContentKind kind, InstanceContentItem item)
    {
        ValidateContentName(item.Name);
        var enabledFolder = folder;
        var disabledFolder = IsPack(kind) ? DisabledPacksFolder(instanceRoot, folder) : null;
        var expectedFolder = item.DisabledPack && disabledFolder is not null ? disabledFolder : enabledFolder;
        var expected = ChildPath(instanceRoot, expectedFolder, item.Name);
        if (!PathEquals(expected, item.Path)) throw new IOException("The item does not belong to this build content folder.");
        var attributes = File.GetAttributes(expected);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Content entries cannot be reparse points.");
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (kind == ModrinthContentKind.Mod && isDirectory) throw new IOException("A mod must be a regular JAR file.");
        if (kind == ModrinthContentKind.Mod && !item.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            && !item.Name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            throw new IOException("A mod must be a JAR file.");
        if (kind == ModrinthContentKind.ResourcePack && !isDirectory
            && !item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new IOException("A resourcepack must be a ZIP file or a folder with pack.mcmeta.");
        if (kind == ModrinthContentKind.ResourcePack && isDirectory && !HasPackMetadata(instanceRoot, expected))
            throw new IOException("A resourcepack folder must contain pack.mcmeta.");
        return expected;
    }

    private static string ChildPath(string instanceRoot, string folder, string name)
    {
        ValidateContentName(name);
        var relativeFolder = Path.GetRelativePath(instanceRoot, folder).Replace(Path.DirectorySeparatorChar, '/');
        return InstanceStorage.ContainedPath(relativeFolder + "/" + name, instanceRoot, allowBaseReparsePoint: true);
    }

    private static void EnsureDirectoryIsSafe(string folder)
    {
        if (!Directory.Exists(folder)) return;
        if ((File.GetAttributes(folder) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new IOException("Content folders cannot be reparse points.");
    }

    private static string LogicalContentName(ModrinthContentKind kind, string name) =>
        kind == ModrinthContentKind.Mod ? LogicalJarName(name) : name;

    private static void ValidateContentFilename(string filename, ModrinthContentKind kind)
    {
        ValidateContentName(filename);
        var suffix = kind switch
        {
            ModrinthContentKind.Mod => ".jar",
            ModrinthContentKind.ResourcePack => ".zip",
            _ => throw new NotSupportedException("Local datapack operations are implemented in a later stage.")
        };
        if (!filename.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"A content filename must end in {suffix}.", nameof(filename));
    }

    private static void ValidateContentName(string name)
    {
        if (!InstanceStorage.IsSinglePathSegment(name))
            throw new ArgumentException("The content name is not a valid Windows path segment.", nameof(name));
    }

    private static bool IsHash(string value, int length) => value.Length == length
        && value.All(Uri.IsHexDigit);

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsPathInside(string parent, string child)
    {
        var fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var fullChild = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        if (PathEquals(fullParent, fullChild)) return true;
        return fullChild.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void MoveEntry(string from, string to)
    {
        if ((File.GetAttributes(from) & FileAttributes.Directory) != 0) Directory.Move(from, to);
        else File.Move(from, to);
    }

    private static void RollBackPublishedEntry(Exception original, string publishedPath, string? backupPath,
        string? restorePath, bool backupCreated, string? collisionBackup = null, string? collisionPath = null,
        bool collisionBackupCreated = false, bool removePublished = true)
    {
        var failures = new List<(string Description, Exception Error)>();
        try { if (removePublished) TryDeleteEntry(publishedPath); }
        catch (Exception error) { failures.Add(($"Could not remove published entry '{publishedPath}'", error)); }

        var retainedBackups = new List<string>();
        if (backupCreated)
        {
            try { MoveEntry(backupPath!, restorePath!); }
            catch (Exception error)
            {
                failures.Add(($"Could not restore backup '{backupPath}' to '{restorePath}'", error));
                retainedBackups.Add(backupPath!);
            }
        }

        if (collisionBackupCreated)
        {
            try { MoveEntry(collisionBackup!, collisionPath!); }
            catch (Exception error)
            {
                failures.Add(($"Could not restore backup '{collisionBackup}' to '{collisionPath}'", error));
                retainedBackups.Add(collisionBackup!);
            }
        }

        if (failures.Count == 0) return;
        var rollbackDetails = string.Join("; ", failures.Select(failure =>
            $"{failure.Description}: {failure.Error.Message}"));
        var backupDetails = string.Concat(retainedBackups.Select(path => $" Backup remains at '{path}'."));
        throw new IOException($"Content operation failed: {original.Message} Rollback failed: {rollbackDetails}.{backupDetails}",
            new AggregateException("The original operation and rollback both failed.",
                new[] { original }.Concat(failures.Select(failure => failure.Error))));
    }

    private static void TryDeleteEntry(string path)
    {
        try { InstanceStorage.DeleteEntryWithoutFollowingReparsePoints(path); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }


    public async Task PublishJarAsync(Guid instanceId, string sourcePath, string filename, ModOrigin? origin = null,
        CancellationToken cancellationToken = default)
    {
        using var lease = await BeginContentAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var root = await Task.Run(() => store.GetInstanceDirectory(instanceId), cancellationToken).ConfigureAwait(false);
        await Task.Run(() => PublishJarAsync(sourcePath, filename, root, origin, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task PublishJarAsync(string sourcePath, string filename, string instanceRoot, ModOrigin? origin,
        CancellationToken cancellationToken)
    {
        ValidateJarName(filename);
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The JAR source is missing or is a reparse point.");

        var folder = InstanceStorage.ContainedPath("minecraft/mods", instanceRoot, allowBaseReparsePoint: true);
        Directory.CreateDirectory(folder);
        var registry = ReadRegistry(instanceRoot);
        EnsureNoCollision(folder, filename);
        var destination = InstanceStorage.ContainedPath($"minecraft/mods/{filename}", instanceRoot, allowBaseReparsePoint: true);
        var staged = InstanceStorage.ContainedPath($"minecraft/mods/.hako-{Guid.NewGuid():N}.tmp", instanceRoot, allowBaseReparsePoint: true);
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staged, destination);
            try
            {
                if (origin is not null)
                {
                    var files = new Dictionary<string, ModOrigin>(registry.Files, StringComparer.Ordinal)
                    {
                        [filename.ToLowerInvariant()] = origin
                    };
                    WriteRegistry(instanceRoot, registry with { Files = files });
                }
            }
            catch
            {
                File.Delete(destination);
                throw;
            }
        }
        finally
        {
            try { File.Delete(staged); }
            catch (FileNotFoundException) { }
        }
    }

    internal static async Task ProvisionAPIAsync(FabricApiDescriptor api, string cachedPath, string instanceRoot,
        CancellationToken cancellationToken)
    {
        var registry = ReadRegistry(instanceRoot);
        if (registry.ApiProvisioned) return;
        await FabricClient.ValidateApiFileAsync(cachedPath, api, cancellationToken).ConfigureAwait(false);
        ValidateJarName(api.Filename);
        var folder = InstanceStorage.ContainedPath("minecraft/mods", instanceRoot, allowBaseReparsePoint: true);
        Directory.CreateDirectory(folder);
        var existing = FindLogicalFile(folder, api.Filename);
        if (existing is not null)
        {
            await FabricClient.ValidateApiFileAsync(existing, api, cancellationToken).ConfigureAwait(false);
            WriteRegistry(instanceRoot, WithApi(registry, api));
            return;
        }

        await PublishJarAsync(cachedPath, api.Filename, instanceRoot, ModOrigin.FromApi(api), cancellationToken).ConfigureAwait(false);
        WriteRegistry(instanceRoot, WithApi(ReadRegistry(instanceRoot), api));
    }

    internal static bool ApiWasProvisioned(string instanceRoot) => ReadRegistry(instanceRoot).ApiProvisioned;

    internal static ModRegistry ReadRegistry(string instanceRoot) => ReadRegistry(instanceRoot, RegistryName);

    internal static ModRegistry ReadRegistry(string instanceRoot, string registryName)
    {
        if (!InstanceStorage.IsSinglePathSegment(registryName))
            throw new ArgumentException("The registry name must be one path segment.", nameof(registryName));
        var path = InstanceStorage.ContainedPath($"minecraft/{registryName}", instanceRoot, allowBaseReparsePoint: true);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return new ModRegistry(); }
        catch (DirectoryNotFoundException) { return new ModRegistry(); }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The mod registry must be a regular file.");
        if (new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("The mod registry is too large.");
        var bytes = File.ReadAllBytes(path);
        ValidateRegistry(bytes);
        return JsonSerializer.Deserialize(bytes, HakoJsonContext.Default.ModRegistry)
            ?? throw new InvalidDataException("The mod registry is empty.");
    }

    private static void ValidateRegistry(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("apiProvisioned", out var apiProvisioned)
            || apiProvisioned.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !root.TryGetProperty("files", out var files)
            || files.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The mod registry is missing required fields or has an invalid structure.");
        }

        foreach (var file in files.EnumerateObject())
        {
            try { ValidateContentName(file.Name); }
            catch (ArgumentException exception) { throw new JsonException("A content registry key is not a valid filename.", exception); }
            if (!string.Equals(file.Name, file.Name.ToLowerInvariant(), StringComparison.Ordinal))
                throw new JsonException("Content registry keys must be lowercase logical filenames.");
            ValidateOrigin(file.Value);
        }
    }

    private static void ValidateOrigin(JsonElement origin)
    {
        if (origin.ValueKind != JsonValueKind.Object)
            throw new JsonException("A mod origin must be an object.");
        RequireString(origin, "source");
        RequireString(origin, "projectID");
        RequireString(origin, "versionID");
        RequireString(origin, "pageURL");
        RequireString(origin, "sha512");
        RequireOptionalString(origin, "versionNumber");
        RequireOptionalString(origin, "iconURL");
        RequireOptionalString(origin, "fileType");
        if (origin.TryGetProperty("api", out var api) && api.ValueKind != JsonValueKind.Null)
        {
            ValidateApiDescriptor(api);
        }
    }

    private static void ValidateApiDescriptor(JsonElement api)
    {
        if (api.ValueKind != JsonValueKind.Object)
            throw new JsonException("A Fabric API descriptor must be an object.");
        RequireString(api, "projectID");
        RequireString(api, "versionID");
        RequireString(api, "version");
        RequireString(api, "channel");
        RequireString(api, "filename");
        RequireString(api, "url");
        RequireString(api, "sha1");
        RequireString(api, "sha512");
        if (!api.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number)
            throw new JsonException("A Fabric API descriptor is missing its required size.");
    }

    private static void RequireString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            throw new JsonException($"A required mod registry field is missing or null: {name}.");
    }

    private static void RequireOptionalString(JsonElement value, string name)
    {
        if (value.TryGetProperty(name, out var property) && property.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new JsonException($"An optional mod registry field has an invalid structure: {name}.");
    }

    internal static void WriteRegistry(string instanceRoot, ModRegistry registry) => WriteRegistry(instanceRoot, RegistryName, registry);

    internal static void WriteRegistry(string instanceRoot, string registryName, ModRegistry registry)
    {
        if (!InstanceStorage.IsSinglePathSegment(registryName))
            throw new ArgumentException("The registry name must be one path segment.", nameof(registryName));
        foreach (var key in registry.Files.Keys)
        {
            ValidateContentName(key);
            if (!string.Equals(key, key.ToLowerInvariant(), StringComparison.Ordinal))
                throw new InvalidDataException("Content registry keys must be lowercase logical filenames.");
        }
        var path = InstanceStorage.ContainedPath($"minecraft/{registryName}", instanceRoot, allowBaseReparsePoint: true);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("The mod registry must be a regular file.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(registry, HakoJsonContext.Default.ModRegistry);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (FileNotFoundException) { }
        }
    }

    private async Task<IDisposable> BeginContentAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var result = await Task.Run(() => store.TryBeginContent(instanceId, out var lease, out var reason)
            ? (lease, reason)
            : (null, reason), cancellationToken).ConfigureAwait(false);
        return result.lease ?? throw new InstanceStorageException(result.reason ?? InstanceStorageError.BuildFilesBusy,
            "The build files are busy.");
    }

    private static ModRegistry WithApi(ModRegistry registry, FabricApiDescriptor api)
    {
        var files = new Dictionary<string, ModOrigin>(registry.Files, StringComparer.Ordinal)
        {
            [api.Filename.ToLowerInvariant()] = ModOrigin.FromApi(api)
        };
        return registry with { ApiProvisioned = true, Files = files };
    }

    private static void EnsureNoCollision(string folder, string filename)
    {
        if (FindLogicalFile(folder, filename) is not null)
            throw new IOException($"A mod named '{filename}' is already installed.");
    }

    private static string? FindLogicalFile(string folder, string filename)
    {
        var logical = LogicalJarName(filename);
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0
                && string.Equals(LogicalJarName(name), logical, StringComparison.OrdinalIgnoreCase))
                return path;
        }

        return null;
    }

    private static string LogicalJarName(string filename) => filename.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
        ? filename[..^9]
        : filename;

    private static void ValidateJarName(string filename)
    {
        if (!InstanceStorage.IsSinglePathSegment(filename) || !filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A mod filename must be a single JAR filename.", nameof(filename));
    }
}

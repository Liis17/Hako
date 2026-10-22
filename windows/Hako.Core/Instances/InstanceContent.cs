using System.Text.Json;
using System.Text.Json.Serialization;
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
public sealed class InstanceContent(InstanceStore store)
{
    private const string RegistryName = ".hako-mods.json";

    public async Task PublishJarAsync(Guid instanceId, string sourcePath, string filename, ModOrigin? origin = null,
        CancellationToken cancellationToken = default)
    {
        using var lease = await BeginContentAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var root = await Task.Run(() => store.GetInstanceDirectory(instanceId), cancellationToken).ConfigureAwait(false);
        await PublishJarAsync(sourcePath, filename, root, origin, cancellationToken).ConfigureAwait(false);
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

    internal static ModRegistry ReadRegistry(string instanceRoot)
    {
        var path = InstanceStorage.ContainedPath($"minecraft/{RegistryName}", instanceRoot, allowBaseReparsePoint: true);
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

    internal static void WriteRegistry(string instanceRoot, ModRegistry registry)
    {
        var path = InstanceStorage.ContainedPath($"minecraft/{RegistryName}", instanceRoot, allowBaseReparsePoint: true);
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

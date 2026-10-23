using System.Text.Json.Serialization;
using Hako.Core;
using Hako.Core.Instances;

namespace Hako.Core.Minecraft;

public sealed record ModpackManifest
{
    [JsonRequired]
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; }

    [JsonRequired]
    public string Game { get; init; } = "";

    [JsonRequired]
    [JsonPropertyName("versionId")]
    public string VersionId { get; init; } = "";

    [JsonRequired]
    public string Name { get; init; } = "";

    [JsonRequired]
    public List<ModpackManifestFile> Files { get; init; } = [];

    [JsonRequired]
    public Dictionary<string, string> Dependencies { get; init; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public string Minecraft => Dependencies.GetValueOrDefault("minecraft", "");

    [JsonIgnore]
    public string? LoaderVersion => Dependencies.GetValueOrDefault("fabric-loader");

    public void Validate(ModpackArchiveLimits? limits = null)
    {
        limits ??= ModpackArchiveLimits.Default;
        if (FormatVersion != 1 || Game != "minecraft" || string.IsNullOrWhiteSpace(Minecraft)
            || Dependencies.Count is < 1 or > 2
            || Dependencies.Keys.Any(key => key is not ("minecraft" or "fabric-loader"))
            || Dependencies.Values.Any(string.IsNullOrWhiteSpace)
            || Files.Count > limits.MaximumEntries)
        {
            throw new ModpackException(ModpackError.InvalidManifest);
        }

        long total = 0;
        var paths = new ModpackPathRegistry();
        foreach (var file in Files)
        {
            ModpackPaths.Validate(file.Path);
            paths.AddFile(file.Path);
            if (file.FileSize < 0 || file.FileSize > limits.MaximumFileSize
                || file.Hashes.GetValueOrDefault("sha1") is not { Length: 40 } sha1
                || file.Hashes.GetValueOrDefault("sha512") is not { Length: 128 } sha512
                || !(sha1 + sha512).All(Uri.IsHexDigit)
                || file.Downloads.Count == 0
                || file.Downloads.Any(uri => !ModpackPaths.IsAllowedDownload(uri))
                || file.Environment is { } environment
                   && (environment.Client is not ("required" or "optional" or "unsupported")
                       || environment.Server is not ("required" or "optional" or "unsupported")))
            {
                throw new ModpackException(ModpackError.InvalidManifest);
            }

            total = checked(total + file.FileSize);
            if (total > limits.MaximumExpandedSize)
            {
                throw new ModpackException(ModpackError.InvalidManifest);
            }
        }
    }

    public bool EquivalentTo(ModpackManifest other) =>
        FormatVersion == other.FormatVersion
        && Game == other.Game
        && VersionId == other.VersionId
        && Name == other.Name
        && Dependencies.Count == other.Dependencies.Count
        && Dependencies.All(item => other.Dependencies.TryGetValue(item.Key, out var value) && value == item.Value)
        && Files.Count == other.Files.Count
        && Files.Zip(other.Files).All(pair =>
            pair.First.Path == pair.Second.Path
            && pair.First.FileSize == pair.Second.FileSize
            && pair.First.Downloads.SequenceEqual(pair.Second.Downloads)
            && pair.First.Hashes.Count == pair.Second.Hashes.Count
            && pair.First.Hashes.All(item => pair.Second.Hashes.TryGetValue(item.Key, out var value) && value == item.Value)
            && (pair.First.Environment is null && pair.Second.Environment is null
                || pair.First.Environment == pair.Second.Environment));
}

public sealed record ModpackManifestFile
{
    [JsonRequired]
    public string Path { get; init; } = "";

    [JsonRequired]
    public Dictionary<string, string> Hashes { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("env")]
    public ModpackEnvironment? Environment { get; init; }

    [JsonRequired]
    public List<Uri> Downloads { get; init; } = [];

    [JsonRequired]
    [JsonPropertyName("fileSize")]
    public long FileSize { get; init; }

    [JsonIgnore]
    public bool InstallOnClient => Environment?.Client != "unsupported";
}

public sealed record ModpackEnvironment
{
    [JsonRequired]
    public string Client { get; init; } = "";

    [JsonRequired]
    public string Server { get; init; } = "";
}

/// <summary>The pinned Modrinth file shape is shared with ModrinthVersion.File in Swift.</summary>
public sealed record ModpackFile
{
    [JsonRequired]
    public string Filename { get; init; } = "";

    [JsonRequired]
    public Uri Url { get; init; } = new("https://cdn.modrinth.com/");

    [JsonRequired]
    public long Size { get; init; }

    [JsonRequired]
    public Dictionary<string, string> Hashes { get; init; } = new(StringComparer.Ordinal);

    [JsonRequired]
    public bool Primary { get; init; }

    [JsonPropertyName("file_type")]
    public string? FileType { get; init; }

    [JsonIgnore]
    public string Sha1 => Hashes.GetValueOrDefault("sha1", "").ToLowerInvariant();

    [JsonIgnore]
    public string Sha512 => Hashes.GetValueOrDefault("sha512", "").ToLowerInvariant();

    public static ModpackFile From(ModrinthFile file) => new()
    {
        Filename = file.Filename,
        Url = file.Url,
        Size = file.Size,
        Hashes = new Dictionary<string, string>(file.Hashes, StringComparer.Ordinal),
        Primary = file.Primary,
        FileType = file.FileType
    };

    public bool IsInstallable(long maximumSize) =>
        Filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)
        && InstanceStorage.IsSinglePathSegment(Filename)
        && !Filename.StartsWith('.')
        && Url.IsAbsoluteUri
        && Url.Scheme == Uri.UriSchemeHttps
        && Url.IdnHost == "cdn.modrinth.com"
        && Url.Port == 443
        && string.IsNullOrEmpty(Url.UserInfo)
        && Size > 0
        && Size <= maximumSize
        && Sha1.Length == 40 && Sha512.Length == 128
        && (Sha1 + Sha512).All(Uri.IsHexDigit);
}

public sealed record ModpackPlan
{
    public const string Filename = ".hako-modpack.json";

    [JsonRequired]
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; } = 1;

    [JsonRequired]
    [JsonPropertyName("projectID")]
    public string ProjectId { get; init; } = "";

    [JsonRequired]
    [JsonPropertyName("versionID")]
    public string VersionId { get; init; } = "";

    [JsonRequired]
    [JsonPropertyName("versionNumber")]
    public string VersionNumber { get; init; } = "";

    [JsonRequired]
    [JsonPropertyName("pageURL")]
    public Uri PageUrl { get; init; } = new("https://modrinth.com/");

    [JsonRequired]
    public ModpackFile Archive { get; init; } = new();

    [JsonRequired]
    public ModpackManifest Manifest { get; init; } = new();

    public bool Completed { get; init; }

    public void Validate(ModpackArchiveLimits? limits = null)
    {
        limits ??= ModpackArchiveLimits.Default;
        if (FormatVersion != 1
            || !IsId(ProjectId)
            || !IsId(VersionId)
            || string.IsNullOrWhiteSpace(VersionNumber)
            || !PageUrl.IsAbsoluteUri || PageUrl.Scheme != Uri.UriSchemeHttps
            || PageUrl.IdnHost != "modrinth.com"
            || !Archive.IsInstallable(limits.MaximumFileSize))
        {
            throw new ModpackException(ModpackError.InvalidManifest);
        }

        Manifest.Validate(limits);
    }

    public async Task WriteAsync(string instanceRoot, CancellationToken cancellationToken = default)
    {
        Validate();
        var destination = InstanceStorage.ContainedPath(Filename, instanceRoot);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await System.Text.Json.JsonSerializer.SerializeAsync(output, this, HakoJsonContext.Default.ModpackPlan,
                    cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (FileNotFoundException) { }
        }
    }

    public void Write(string instanceRoot)
    {
        Validate();
        var destination = InstanceStorage.ContainedPath(Filename, instanceRoot);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       81920, FileOptions.WriteThrough))
            {
                System.Text.Json.JsonSerializer.Serialize(output, this, HakoJsonContext.Default.ModpackPlan);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (FileNotFoundException) { }
        }
    }

    public static ModpackPlan? Read(string instanceRoot)
    {
        var path = InstanceStorage.ContainedPath(Filename, instanceRoot);
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) throw new ModpackException(ModpackError.InvalidManifest);
            return null;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || new FileInfo(path).Length > 16 * 1024 * 1024)
        {
            throw new ModpackException(ModpackError.InvalidManifest);
        }

        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var plan = System.Text.Json.JsonSerializer.Deserialize(input, HakoJsonContext.Default.ModpackPlan)
                ?? throw new ModpackException(ModpackError.InvalidManifest);
            plan.Validate();
            return plan;
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new ModpackException(ModpackError.InvalidManifest, exception);
        }
    }

    private static bool IsId(string value) =>
        value.Length is > 0 and <= 128 && value.All(char.IsAsciiLetterOrDigit);
}

public sealed record PreparedModpack(ModpackPlan Plan, PreparedInstallation Minecraft, FabricProfile? FabricProfile = null);

public enum ModpackError
{
    InvalidManifest,
    UnsafePath,
    Archive,
    DamagedFile
}

public sealed class ModpackException : IOException
{
    public ModpackException(ModpackError error, Exception? innerException = null)
        : base("The Modrinth modpack could not be prepared safely.", innerException) => Error = error;

    public ModpackError Error { get; }
}

public sealed record ModpackArchiveLimits(
    int MaximumEntries,
    long MaximumArchiveSize,
    long MaximumIndexSize,
    long MaximumFileSize,
    long MaximumExpandedSize,
    long MaximumCentralDirectorySize)
{
    public static ModpackArchiveLimits Default { get; } = new(
        MaximumEntries: 50_000,
        MaximumArchiveSize: 2_147_483_648,
        MaximumIndexSize: 16_777_216,
        MaximumFileSize: 2_147_483_648,
        MaximumExpandedSize: 21_474_836_480,
        MaximumCentralDirectorySize: 8_388_608);
}

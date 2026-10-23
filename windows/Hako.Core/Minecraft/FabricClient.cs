using System.Security.Cryptography;
using System.Text.Json;
using Hako.Core.Instances;

namespace Hako.Core.Minecraft;

/// <summary>Minimal official Fabric Meta and Modrinth client used by installation and creation preflight.</summary>
public sealed class FabricClient : IDisposable
{
    public const string FabricApiProjectId = "P7dR8mSH";
    public const string UserAgent = ModrinthClient.UserAgent;
    private const long MaximumApiSize = 64L * 1024 * 1024;
    private const int MaximumMetadataSize = 1024 * 1024;
    private const int MaximumResponseSize = 32 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ModrinthClient _modrinth;
    private readonly string _cacheRoot;
    private readonly Uri _metaBase;

    public ModrinthClient Modrinth => _modrinth;

    public FabricClient(HttpClient? httpClient = null, HakoPaths? paths = null, string? cacheRoot = null,
        Uri? metaBase = null, Uri? modrinthBase = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _cacheRoot = Path.GetFullPath(cacheRoot ?? Path.Combine(paths?.CacheRoot
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hako", "Cache"), "FabricAPI"));
        _metaBase = EnsureTrailingSlash(metaBase ?? new Uri("https://meta.fabricmc.net/v2/"));
        _modrinth = new ModrinthClient(_httpClient, modrinthBase, paths);
    }

    public async Task<FabricPreparation> PrepareAsync(string minecraft, int java, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minecraft) || java < 1)
            throw new ArgumentException("Minecraft version and Java major version are required.");

        var loadersTask = GetLoaderVersionsAsync(minecraft, cancellationToken);
        var api = await GetLatestApiAsync(minecraft, cancellationToken).ConfigureAwait(false);
        var cached = await DownloadApiAsync(api, cancellationToken).ConfigureAwait(false);
        var metadata = await ReadModMetadataAsync(cached, cancellationToken).ConfigureAwait(false);
        var loaders = await loadersTask.ConfigureAwait(false);
        var compatible = loaders.Where(loader => metadata.Supports(loader.Version, java)).ToArray();
        if (compatible.Length == 0)
            throw new MojangException(MojangError.Unsupported, "Fabric API is incompatible with this Java and Minecraft version.");
        return new FabricPreparation(api, metadata, compatible);
    }

    public async Task<IReadOnlyList<FabricLoaderVersion>> GetLoaderVersionsAsync(string minecraft, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(_metaBase, $"versions/loader/{Uri.EscapeDataString(minecraft)}");
        var bytes = await GetBytesAsync(uri, MaximumResponseSize, allowEmptyBadRequest: true, cancellationToken).ConfigureAwait(false);
        var entries = JsonSerializer.Deserialize(bytes, FabricJsonContext.Default.FabricLoaderEntryArray)
            ?? throw new MojangException(MojangError.Invalid, "Fabric returned an empty loader list.");
        return entries.Select(entry => entry.Loader).ToArray();
    }

    public async Task<FabricApiDescriptor> GetLatestApiAsync(string minecraft, CancellationToken cancellationToken = default)
    {
        var versions = await _modrinth.GetProjectVersionsAsync(FabricApiProjectId, [minecraft], ["fabric"], cancellationToken)
            .ConfigureAwait(false);
        var selected = SelectLatestApiVersion(versions, minecraft);
        var file = selected.Files.FirstOrDefault(candidate => IsMainJar(candidate) && candidate.Primary)
            ?? selected.Files.FirstOrDefault(IsMainJar);
        if (file is null || file.Size is <= 0 or > MaximumApiSize
            || !InstanceStorage.IsSinglePathSegment(file.Filename)
            || !file.Filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            || !file.Hashes.TryGetValue("sha1", out var sha1) || !IsHash(sha1, 40)
            || !file.Hashes.TryGetValue("sha512", out var sha512) || !IsHash(sha512, 128))
            throw new MojangException(MojangError.Invalid, "Modrinth returned a Fabric API file without valid checksums.");
        RequireHttps(file.Url);
        return new FabricApiDescriptor(selected.ProjectId, selected.Id, selected.VersionNumber,
            selected.VersionType, file.Filename, file.Url.AbsoluteUri, file.Size, sha1.ToLowerInvariant(), sha512.ToLowerInvariant());
    }

    internal static ModrinthVersion SelectLatestApiVersion(IEnumerable<ModrinthVersion> versions, string minecraft)
    {
        var priority = new Dictionary<string, int>(StringComparer.Ordinal) { ["release"] = 0, ["beta"] = 1, ["alpha"] = 2 };
        return versions.Where(version => version.ProjectId == FabricApiProjectId
                && version.GameVersions.Contains(minecraft, StringComparer.Ordinal)
                && version.Loaders.Contains("fabric", StringComparer.Ordinal)
                && priority.ContainsKey(version.VersionType))
            .OrderBy(version => priority[version.VersionType])
            .ThenByDescending(version => version.DatePublished)
            .FirstOrDefault()
            ?? throw new MojangException(MojangError.Unsupported, $"No compatible Fabric API exists for Minecraft {minecraft}.");
    }

    public async Task<(FabricProfile Profile, byte[] Data)> GetProfileAsync(string minecraft, string loader, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(_metaBase,
            $"versions/loader/{Uri.EscapeDataString(minecraft)}/{Uri.EscapeDataString(loader)}/profile/json");
        var bytes = await GetBytesAsync(uri, MaximumResponseSize, false, cancellationToken).ConfigureAwait(false);
        var profile = JsonSerializer.Deserialize(bytes, FabricJsonContext.Default.FabricProfile)
            ?? throw new MojangException(MojangError.Invalid, "Fabric returned an empty profile.");
        profile.Validate(minecraft, loader);
        return (profile, bytes);
    }

    public static async Task<FabricProfile> ReadInstalledProfileAsync(string instanceRoot, string minecraft, string loader,
        string? expectedSha1, CancellationToken cancellationToken = default)
    {
        if (!IsHash(expectedSha1, 40))
            throw new MojangException(MojangError.Invalid, "Fabric profile checksum is missing.");
        var path = InstanceStorage.ContainedPath("minecraft/.hako-fabric.json", instanceRoot, allowBaseReparsePoint: true);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new MojangException(MojangError.Invalid, "Fabric profile is missing.");
        if (new FileInfo(path).Length > MaximumResponseSize)
            throw new MojangException(MojangError.Invalid, "Fabric profile is too large.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (!Convert.ToHexString(SHA1.HashData(bytes)).Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
            throw new MojangException(MojangError.Integrity, "Fabric profile checksum does not match this build.");
        var profile = JsonSerializer.Deserialize(bytes, FabricJsonContext.Default.FabricProfile)
            ?? throw new MojangException(MojangError.Invalid, "Fabric profile is empty.");
        profile.Validate(minecraft, loader);
        return profile;
    }

    public async Task<IReadOnlyList<LibraryInstallation>> ResolveLibrariesAsync(FabricProfile profile, CancellationToken cancellationToken = default)
    {
        var result = new List<LibraryInstallation>(profile.Libraries.Count);
        foreach (var library in profile.Libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var maven = FabricMavenLibrary.Parse(library.Name);
            RequireHttps(library.Url);
            if (library.Size is < 0) throw new MojangException(MojangError.Invalid, "Fabric returned an invalid Maven library size.");
            var url = new Uri(library.Url, maven.Path);
            RequireHttps(url);
            var sha1 = library.Sha1;
            if (sha1 is null)
            {
                var checksum = await GetBytesAsync(new Uri(url.AbsoluteUri + ".sha1"), 256, false, cancellationToken).ConfigureAwait(false);
                sha1 = System.Text.Encoding.UTF8.GetString(checksum).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            }

            if (!IsHash(sha1, 40)) throw new MojangException(MojangError.Invalid, "Fabric omitted a valid Maven library SHA-1 checksum.");
            result.Add(new LibraryInstallation(new MojangDownload
            {
                Url = url.AbsoluteUri,
                Sha1 = sha1!.ToLowerInvariant(),
                Size = library.Size,
                Path = maven.Path
            }, maven.Path));
        }

        return result;
    }

    public async Task<string> DownloadApiAsync(FabricApiDescriptor api, CancellationToken cancellationToken = default)
    {
        ValidateDescriptor(api);
        var destination = InstanceStorage.ContainedPath(api.Sha512.ToLowerInvariant() + ".jar", _cacheRoot, allowBaseReparsePoint: true);
        try
        {
            await ValidateApiFileAsync(destination, api, cancellationToken).ConfigureAwait(false);
            return destination;
        }
        catch (Exception exception) when (exception is IOException or MojangException)
        {
            if (File.Exists(destination)) File.Delete(destination);
        }

        await _modrinth.DownloadFileAsync(new Uri(api.Url), destination, api.Size, api.Sha1, api.Sha512, cancellationToken)
            .ConfigureAwait(false);
        return destination;
    }

    public static async Task ValidateApiFileAsync(string path, FabricApiDescriptor api, CancellationToken cancellationToken = default)
    {
        ValidateDescriptor(api);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new MojangException(MojangError.Integrity, "Fabric API file is missing or unsafe.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != api.Size) throw new MojangException(MojangError.Integrity, "Fabric API file size does not match Modrinth.");
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            sha1.AppendData(buffer, 0, read);
            sha512.AppendData(buffer, 0, read);
        }

        if (!HashEquals(sha1.GetHashAndReset(), api.Sha1) || !HashEquals(sha512.GetHashAndReset(), api.Sha512))
            throw new MojangException(MojangError.Integrity, "Fabric API file failed its SHA-1 or SHA-512 check.");
    }

    public static async Task<FabricModMetadata> ReadModMetadataAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.GetEntry("fabric.mod.json")
            ?? throw new MojangException(MojangError.Invalid, "Fabric API JAR has no fabric.mod.json.");
        if (entry.Length > MaximumMetadataSize) throw new MojangException(MojangError.Invalid, "fabric.mod.json exceeds the allowed size.");
        await using var input = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumMetadataSize)
                throw new MojangException(MojangError.Invalid, "fabric.mod.json exceeds the allowed size.");
            output.Write(buffer, 0, read);
        }

        return JsonSerializer.Deserialize(output.ToArray(), FabricJsonContext.Default.FabricModMetadata)
            ?? throw new MojangException(MojangError.Invalid, "fabric.mod.json is empty.");
    }

    public void Dispose()
    {
        _modrinth.Dispose();
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private async Task<byte[]> GetBytesAsync(Uri uri, int maximumBytes, bool allowEmptyBadRequest, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(uri, cancellationToken, allowEmptyBadRequest).ConfigureAwait(false);
        if (allowEmptyBadRequest && response.StatusCode == System.Net.HttpStatusCode.BadRequest) return "[]"u8.ToArray();
        if (response.Content.Headers.ContentLength is { } length && length > maximumBytes)
            throw new MojangException(MojangError.Invalid, "Fabric or Modrinth response is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes) throw new MojangException(MojangError.Invalid, "Fabric or Modrinth response is too large.");
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken, bool allowEmptyBadRequest = false)
    {
        RequireHttps(uri);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (allowEmptyBadRequest && response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.AsSpan().SequenceEqual("[]"u8)) return response;
            response.Dispose();
            throw new MojangException(MojangError.Http, "Fabric returned an invalid loader response.", (int)System.Net.HttpStatusCode.BadRequest);
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new MojangException(MojangError.Http, $"Fabric or Modrinth returned HTTP {status}.", status);
        }

        return response;
    }

    private static bool IsMainJar(ModrinthFile file) =>
        file.Filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
        && file.FileType is not ("sources-jar" or "dev-jar" or "javadoc-jar");

    private static void ValidateDescriptor(FabricApiDescriptor api)
    {
        if (api.ProjectID != FabricApiProjectId || !InstanceStorage.IsSinglePathSegment(api.Filename)
            || !api.Filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            || api.Size is <= 0 or > MaximumApiSize || !IsHash(api.Sha1, 40) || !IsHash(api.Sha512, 128)
            || !Uri.TryCreate(api.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new MojangException(MojangError.Invalid, "Invalid Fabric API descriptor.");
    }

    private static bool IsHash(string? value, int length) => value is { Length: var size } && size == length && value.All(Uri.IsHexDigit);
    private static bool HashEquals(byte[] hash, string expected) => Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    private static void RequireHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
            throw new MojangException(MojangError.Invalid, "Fabric and Modrinth URLs must use HTTPS.");
    }
    private static Uri EnsureTrailingSlash(Uri uri) => new(uri.AbsoluteUri.TrimEnd('/') + "/");
}

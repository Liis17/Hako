using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hako.Core.Instances;

namespace Hako.Core.Minecraft;

public enum ModrinthContentKind
{
    Mod,
    ResourcePack,
    Datapack
}

public enum ModrinthSort
{
    Relevance,
    Downloads,
    Newest,
    Updated
}

[JsonConverter(typeof(ModrinthProjectJsonConverter))]
public sealed record ModrinthProject
{
    public string Id { get; init; } = "";
    public string? Slug { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public Uri? IconUrl { get; init; }
    public string ProjectType { get; init; } = "";
    public IReadOnlyList<string> AllProjectTypes { get; init; } = [];

    public Uri PageUrl => new($"https://modrinth.com/{Uri.EscapeDataString(ProjectType)}/{Uri.EscapeDataString(Slug ?? Id)}");
}

internal sealed class ModrinthProjectJsonConverter : JsonConverter<ModrinthProject>
{
    public override ModrinthProject Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var id = ReadString(root, "project_id") ?? ReadString(root, "id")
            ?? throw new JsonException("Modrinth returned a project without an ID.");
        var projectType = ReadString(root, "project_type") ?? "";
        var allTypes = root.TryGetProperty("all_project_types", out var types) && types.ValueKind == JsonValueKind.Array
            ? types.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
            : [projectType];
        var iconText = ReadString(root, "icon_url");
        return new ModrinthProject
        {
            Id = id,
            Slug = ReadString(root, "slug"),
            Title = ReadString(root, "title") ?? "",
            Description = ReadString(root, "description") ?? "",
            IconUrl = Uri.TryCreate(iconText, UriKind.Absolute, out var icon) ? icon : null,
            ProjectType = projectType,
            AllProjectTypes = allTypes
        };
    }

    public override void Write(Utf8JsonWriter writer, ModrinthProject value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        if (value.Slug is not null) writer.WriteString("slug", value.Slug);
        writer.WriteString("title", value.Title);
        writer.WriteString("description", value.Description);
        if (value.IconUrl is not null) writer.WriteString("icon_url", value.IconUrl.AbsoluteUri);
        writer.WriteString("project_type", value.ProjectType);
        writer.WritePropertyName("all_project_types");
        JsonSerializer.Serialize(writer, value.AllProjectTypes, options);
        writer.WriteEndObject();
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

public sealed record ModrinthSearchPage
{
    public List<ModrinthProject> Hits { get; init; } = [];
    [JsonPropertyName("total_hits")]
    public int TotalHits { get; init; }
}

public sealed record ModrinthFileMatch(string ProjectId, string VersionId, DateTimeOffset DatePublished, string Sha512);

internal sealed record ModrinthVersionHashMatch
{
    public string Id { get; init; } = "";
    [JsonPropertyName("project_id")]
    public string ProjectId { get; init; } = "";
    [JsonPropertyName("date_published")]
    public DateTimeOffset DatePublished { get; init; }
}

internal sealed record ModrinthFileCache
{
    public Dictionary<string, ModrinthFileCacheEntry> Files { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, ModrinthFileMatch> Versions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed record ModrinthFileCacheEntry(long Size, long ModifiedUtcTicks, string Sha512);
internal sealed record ModrinthHashesRequest(IReadOnlyList<string> Hashes, string Algorithm);
internal sealed record ModrinthVersionUpdateRequest(
    IReadOnlyList<string> Hashes,
    string Algorithm,
    IReadOnlyList<string> Loaders,
    [property: JsonPropertyName("game_versions")] IReadOnlyList<string> GameVersions,
    [property: JsonPropertyName("version_types")] IReadOnlyList<string> VersionTypes);

/// <summary>The shared HTTPS boundary for Modrinth search, metadata, hash lookup, cache, and verified downloads.</summary>
public sealed class ModrinthClient : IDisposable
{
    public const string UserAgent = "Hako/1.0 (https://github.com/Liis17/Hako)";
    private const int MaximumResponseSize = 32 * 1024 * 1024;
    private const int SearchPageSize = 20;
    private static readonly string[] Channels = ["release", "beta", "alpha"];
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CacheGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _base;
    private readonly string _cacheFile;
    private readonly SemaphoreSlim _cacheGate;
    private bool _cacheWasInvalid;
    /// <summary>Unknown hashes are retried only after a new client is created.</summary>
    private readonly HashSet<string> _unknownHashes = new(StringComparer.OrdinalIgnoreCase);

    public ModrinthClient(HttpClient? httpClient = null, Uri? baseUri = null, HakoPaths? paths = null, string? cacheRoot = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _base = EnsureTrailingSlash(baseUri ?? new Uri("https://api.modrinth.com/v2/"));
        var root = cacheRoot ?? paths?.CacheRoot
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hako", "Cache");
        _cacheFile = Path.Combine(Path.GetFullPath(root), "Modrinth", "files.json");
        _cacheGate = CacheGates.GetOrAdd(_cacheFile, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<ModrinthSearchPage> SearchAsync(string query, ModrinthContentKind kind, string minecraft,
        ModrinthSort sort = ModrinthSort.Relevance, int offset = 0, int limit = SearchPageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraft);
        if (offset < 0 || limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(offset < 0 ? nameof(offset) : nameof(limit));

        string[][] facets = kind switch
        {
            ModrinthContentKind.Mod => [["project_type:mod"], ["categories:fabric"], [$"versions:{minecraft}"], ["environment!=dedicated_server_only"]],
            ModrinthContentKind.ResourcePack => [["project_type:resourcepack"], [$"versions:{minecraft}"]],
            ModrinthContentKind.Datapack => [["all_project_types:datapack"], ["categories:datapack"], [$"versions:{minecraft}"]],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["facets"] = JsonSerializer.Serialize(facets, HakoJsonContext.Default.StringArrayArray),
            ["index"] = SortName(sort),
            ["offset"] = offset.ToString(CultureInfo.InvariantCulture),
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture)
        };
        var trimmed = query.Trim();
        if (trimmed.Length > 0) parameters["query"] = trimmed;
        return JsonSerializer.Deserialize(await GetBytesAsync(BuildUri("search", parameters), cancellationToken).ConfigureAwait(false),
            HakoJsonContext.Default.ModrinthSearchPage)
            ?? throw InvalidResponse("Modrinth returned an empty search page.");
    }

    public Task<ModrinthSearchPage> SearchAsync(string query, bool mods, string minecraft,
        ModrinthSort sort = ModrinthSort.Relevance, int offset = 0, CancellationToken cancellationToken = default) =>
        SearchAsync(query, mods ? ModrinthContentKind.Mod : ModrinthContentKind.ResourcePack, minecraft, sort, offset,
            SearchPageSize, cancellationToken);

    public async Task<IReadOnlyList<ModrinthVersion>> GetProjectVersionsAsync(
        string projectId,
        IReadOnlyList<string> gameVersions,
        IReadOnlyList<string> loaders,
        CancellationToken cancellationToken = default)
    {
        var id = CheckIds([projectId])[0];
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["game_versions"] = JsonSerializer.Serialize(gameVersions.ToArray(), HakoJsonContext.Default.StringArray),
            ["loaders"] = JsonSerializer.Serialize(loaders.ToArray(), HakoJsonContext.Default.StringArray),
            ["include_changelog"] = "false"
        };
        var bytes = await GetBytesAsync(BuildUri($"project/{id}/version", parameters), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, FabricJsonContext.Default.ModrinthVersionArray)
            ?? throw InvalidResponse("Modrinth returned an empty project version list.");
    }

    public async Task<IReadOnlyList<ModrinthVersion>> GetCompatibleVersionsAsync(string projectId,
        ModrinthContentKind kind, string minecraft, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraft);
        var loader = Loader(kind);
        var versions = await GetProjectVersionsAsync(projectId, [minecraft], [loader], cancellationToken).ConfigureAwait(false);
        return versions
            .Where(version => version.ProjectId == projectId
                && version.GameVersions.Contains(minecraft, StringComparer.Ordinal)
                && version.Loaders.Contains(loader, StringComparer.Ordinal)
                && ChannelPriority(version.VersionType) >= 0
                && version.InstallableFile(kind) is not null)
            .OrderBy(version => ChannelPriority(version.VersionType))
            .ThenByDescending(version => version.DatePublished)
            .ThenBy(version => version.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<ModrinthVersion?> GetLatestVersionAsync(string projectId, ModrinthContentKind kind,
        string minecraft, string? channel = null, CancellationToken cancellationToken = default)
    {
        if (channel is not null && ChannelPriority(channel) < 0)
            throw new ArgumentException("Unknown Modrinth release channel.", nameof(channel));
        var versions = await GetCompatibleVersionsAsync(projectId, kind, minecraft, cancellationToken).ConfigureAwait(false);
        return versions.FirstOrDefault(version => channel is null || version.VersionType == channel);
    }

    public Task<ModrinthVersion?> GetLatestVersionAsync(string projectId, bool mods, string minecraft,
        string? channel = null, CancellationToken cancellationToken = default) =>
        GetLatestVersionAsync(projectId, mods ? ModrinthContentKind.Mod : ModrinthContentKind.ResourcePack,
            minecraft, channel, cancellationToken);

    public async Task<IReadOnlyList<ModrinthProject>> GetProjectsAsync(IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return [];
        var checkedIds = CheckIds(ids);
        var query = JsonSerializer.Serialize(checkedIds, HakoJsonContext.Default.StringArray);
        var bytes = await GetBytesAsync(BuildUri("projects", new Dictionary<string, string> { ["ids"] = query }), cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, HakoJsonContext.Default.ModrinthProjectArray)
            ?? throw InvalidResponse("Modrinth returned an empty project list.");
    }

    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return [];
        var checkedIds = CheckIds(ids);
        var query = JsonSerializer.Serialize(checkedIds, HakoJsonContext.Default.StringArray);
        var bytes = await GetBytesAsync(BuildUri("versions", new Dictionary<string, string> { ["ids"] = query }), cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, FabricJsonContext.Default.ModrinthVersionArray)
            ?? throw InvalidResponse("Modrinth returned an empty version list.");
    }

    /// <summary>Maps local files to known Modrinth versions. File hashes are reused by normalized path, size, and mtime.</summary>
    public async Task<IReadOnlyDictionary<string, ModrinthFileMatch>> VersionsOfAsync(
        IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
    {
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = await LoadFileCacheAsync(cancellationToken).ConfigureAwait(false);
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var changed = _cacheWasInvalid;
            foreach (var path in filePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(path);
                var key = fullPath.ToUpperInvariant();
                var attributes = File.GetAttributes(fullPath);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new IOException("Modrinth file recognition requires a regular, non-reparse file.");

                var info = new FileInfo(fullPath);
                var size = info.Length;
                var modified = info.LastWriteTimeUtc.Ticks;
                if (cache.Files.TryGetValue(key, out var entry) && entry.Size == size
                    && entry.ModifiedUtcTicks == modified && IsHash(entry.Sha512, 128))
                {
                    hashes[fullPath] = entry.Sha512.ToLowerInvariant();
                    continue;
                }

                var hash = await HashFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
                hashes[fullPath] = hash;
                cache.Files[key] = new ModrinthFileCacheEntry(size, modified, hash);
                changed = true;
            }

            var unknown = hashes.Values.Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(hash => !cache.Versions.ContainsKey(hash) && !_unknownHashes.Contains(hash)).ToArray();
            if (unknown.Length > 0)
            {
                var body = new ModrinthHashesRequest(unknown, "sha512");
                var bytes = await PostJsonAsync("version_files", body, HakoJsonContext.Default.ModrinthHashesRequest, cancellationToken)
                    .ConfigureAwait(false);
                var matches = JsonSerializer.Deserialize(bytes, HakoJsonContext.Default.ModrinthVersionHashMatchDictionary)
                    ?? throw InvalidResponse("Modrinth returned an empty hash lookup response.");
                foreach (var hash in unknown)
                {
                    var match = matches.FirstOrDefault(pair => string.Equals(pair.Key, hash, StringComparison.OrdinalIgnoreCase)).Value;
                    if (match is not null && IsProjectId(match.ProjectId) && IsProjectId(match.Id))
                    {
                        cache.Versions[hash] = new ModrinthFileMatch(match.ProjectId, match.Id, match.DatePublished, hash);
                        changed = true;
                    }
                    else
                    {
                        _unknownHashes.Add(hash);
                    }
                }
            }

            foreach (var key in cache.Files.Keys.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(key)) continue;
                cache.Files.Remove(key);
                changed = true;
            }

            var usedHashes = cache.Files.Values.Select(entry => entry.Sha512).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var hash in cache.Versions.Keys.Where(hash => !usedHashes.Contains(hash)).ToArray())
            {
                cache.Versions.Remove(hash);
                changed = true;
            }

            if (changed)
            {
                await SaveFileCacheAsync(cache, cancellationToken).ConfigureAwait(false);
                _cacheWasInvalid = false;
            }

            return hashes.Where(pair => cache.Versions.ContainsKey(pair.Value))
                .ToDictionary(pair => pair.Key, pair => cache.Versions[pair.Value], StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    /// <summary>Looks up the latest compatible release, beta, then alpha version for each SHA-512.</summary>
    public async Task<IReadOnlyDictionary<string, ModrinthVersion>> LatestVersionsAsync(
        IReadOnlyList<string> hashes, ModrinthContentKind kind, string minecraft,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraft);
        var loader = Loader(kind);
        var remaining = hashes.Where(hash => IsHash(hash, 128)).Select(hash => hash.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in Channels)
        {
            if (remaining.Count == 0) break;
            var body = new ModrinthVersionUpdateRequest(remaining.ToArray(), "sha512", [loader], [minecraft], [channel]);
            var bytes = await PostJsonAsync("version_files/update", body, HakoJsonContext.Default.ModrinthVersionUpdateRequest,
                cancellationToken).ConfigureAwait(false);
            var found = JsonSerializer.Deserialize(bytes, HakoJsonContext.Default.ModrinthVersionDictionary)
                ?? throw InvalidResponse("Modrinth returned an empty version update response.");
            foreach (var pair in found)
            {
                var version = pair.Value;
                if (!remaining.Contains(pair.Key) || version.VersionType != channel
                    || !version.GameVersions.Contains(minecraft, StringComparer.Ordinal)
                    || !version.Loaders.Contains(loader, StringComparer.Ordinal)
                    || version.InstallableFile(kind) is null)
                    continue;
                result[pair.Key.ToLowerInvariant()] = version;
                remaining.Remove(pair.Key);
            }
        }

        return result;
    }

    public async Task DownloadFileAsync(ModrinthFile file, string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!InstanceStorage.IsSinglePathSegment(file.Filename)
            || !(file.Filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                || file.Filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            || !file.IsInstallable(Path.GetExtension(file.Filename)))
            throw InvalidResponse("Modrinth returned invalid file metadata.");
        await DownloadFileAsync(file.Url, destination, file.Size, file.Sha1, file.Sha512, cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadFileAsync(Uri url, string destination, long size, string sha1, string sha512,
        CancellationToken cancellationToken = default)
    {
        RequireHttps(url);
        if (size <= 0 || !IsHash(sha1, 40) || !IsHash(sha512, 128))
            throw InvalidResponse("Modrinth returned invalid file metadata.");

        var fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        var temporary = fullDestination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } length && length != size)
                throw IntegrityFailure("Modrinth file size did not match its version metadata.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var sha1Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
            using (var sha512Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512))
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    if (total > size) throw IntegrityFailure("Modrinth file exceeded its declared size.");
                    sha1Hash.AppendData(buffer, 0, read);
                    sha512Hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                if (total != size || !HashEquals(sha1Hash.GetHashAndReset(), sha1)
                    || !HashEquals(sha512Hash.GetHashAndReset(), sha512))
                    throw IntegrityFailure("Modrinth file failed its SHA-1 or SHA-512 check.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, fullDestination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (FileNotFoundException) { }
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private async Task<byte[]> GetBytesAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } length && length > MaximumResponseSize)
            throw InvalidResponse("Modrinth response is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumResponseSize) throw InvalidResponse("Modrinth response is too large.");
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private async Task<byte[]> PostJsonAsync<T>(string path, T body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, path))
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, typeInfo))
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } length && length > MaximumResponseSize)
            throw InvalidResponse("Modrinth response is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumResponseSize) throw InvalidResponse("Modrinth response is too large.");
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        using (request)
        {
            RequireHttps(request.RequestUri ?? throw InvalidResponse("Modrinth request has no URL."));
            request.Headers.UserAgent.ParseAdd(UserAgent);
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new MojangException(MojangError.Http, $"Modrinth returned HTTP {status}.", status);
        }

        return response;
    }

    private async Task<ModrinthFileCache> LoadFileCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_cacheFile)) return new ModrinthFileCache();
            var info = new FileInfo(_cacheFile);
            if (info.Length > MaximumResponseSize) throw new InvalidDataException("Modrinth file cache is too large.");
            var bytes = await File.ReadAllBytesAsync(_cacheFile, cancellationToken).ConfigureAwait(false);
            var cache = JsonSerializer.Deserialize(bytes, HakoJsonContext.Default.ModrinthFileCache)
                ?? throw new JsonException("Modrinth file cache is empty.");
            if (!IsValidCache(cache)) throw new InvalidDataException("Modrinth file cache has an invalid structure.");
            return cache;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            _cacheWasInvalid = true;
            return new ModrinthFileCache();
        }
    }

    private static bool IsValidCache(ModrinthFileCache cache)
    {
        if (cache.Files is null || cache.Versions is null) return false;
        foreach (var (path, entry) in cache.Files)
        {
            if (entry is null || !Path.IsPathFullyQualified(path) || entry.Size < 0
                || entry.ModifiedUtcTicks < 0 || entry.ModifiedUtcTicks > DateTime.MaxValue.Ticks
                || !IsHash(entry.Sha512, 128))
                return false;
        }

        foreach (var (hash, match) in cache.Versions)
        {
            if (match is null || !IsHash(hash, 128) || !IsProjectId(match.ProjectId) || !IsProjectId(match.VersionId)
                || !string.Equals(hash, match.Sha512, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private async Task SaveFileCacheAsync(ModrinthFileCache cache, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
        var temporary = _cacheFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(cache, HakoJsonContext.Default.ModrinthFileCache);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _cacheFile, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (FileNotFoundException) { }
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private Uri BuildUri(string path, IReadOnlyDictionary<string, string> parameters)
    {
        var query = string.Join('&', parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        return new Uri(_base, path + (query.Length == 0 ? "" : "?" + query));
    }

    private static string[] CheckIds(IReadOnlyList<string> ids)
    {
        if (ids.Any(id => !IsProjectId(id)))
            throw new MojangException(MojangError.Invalid, "Modrinth IDs must contain only ASCII letters and numbers.");
        return ids.ToArray();
    }

    private static bool IsProjectId(string? id) => !string.IsNullOrEmpty(id) && id.All(char.IsAsciiLetterOrDigit);

    private static string Loader(ModrinthContentKind kind) => kind switch
    {
        ModrinthContentKind.Mod => "fabric",
        ModrinthContentKind.ResourcePack => "minecraft",
        ModrinthContentKind.Datapack => "datapack",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string SortName(ModrinthSort sort) => sort switch
    {
        ModrinthSort.Relevance => "relevance",
        ModrinthSort.Downloads => "downloads",
        ModrinthSort.Newest => "newest",
        ModrinthSort.Updated => "updated",
        _ => throw new ArgumentOutOfRangeException(nameof(sort))
    };

    private static int ChannelPriority(string channel) => channel switch
    {
        "release" => 0,
        "beta" => 1,
        "alpha" => 2,
        _ => -1
    };

    private static bool IsHash(string? value, int length) => value is { Length: var size }
        && size == length && value.All(Uri.IsHexDigit);
    private static bool HashEquals(byte[] hash, string expected) => Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    private static void RequireHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
            throw InvalidResponse("Modrinth URLs must use HTTPS.");
    }
    private static MojangException InvalidResponse(string message) => new(MojangError.Invalid, message);
    private static MojangException IntegrityFailure(string message) => new(MojangError.Integrity, message);
    private static Uri EnsureTrailingSlash(Uri uri) => new(uri.AbsoluteUri.TrimEnd('/') + "/");
}

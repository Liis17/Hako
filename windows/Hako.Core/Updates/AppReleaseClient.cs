using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hako.Core.Updates;

public sealed record AppReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string DownloadUrl,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("digest")] string? Digest);

public sealed record AppRelease(
    [property: JsonPropertyName("tag_name")] string Tag,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
    [property: JsonPropertyName("assets")] AppReleaseAsset[] Assets)
{
    public Version? GetVersion()
    {
        if (Tag is null || !Tag.StartsWith("windows-v", StringComparison.Ordinal)) return null;
        var parts = Tag[9..].Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || part.Any(c => c is < '0' or > '9')
            || part.Length > 1 && part[0] == '0')) return null;
        if (!Version.TryParse(string.Join('.', parts) + ".0", out var version)
            || version.Major > ushort.MaxValue || version.Minor > ushort.MaxValue || version.Build > ushort.MaxValue)
            return null;
        return version;
    }

    public AppReleaseAsset? GetPackage(string architecture) => architecture is "x64" or "arm64"
        ? Assets?.SingleOrDefault(asset => asset.Name == $"Hako-{Tag[9..]}-windows-{architecture}.msix") : null;

    public IReadOnlyList<AppReleaseAsset> GetDependencies(string architecture) => (Assets ?? [])
        .Where(asset => asset.Name is not null
            && asset.Name.StartsWith($"Hako-{Tag[9..]}-windows-{architecture}-dependency-", StringComparison.Ordinal)
            && (asset.Name.EndsWith(".msix", StringComparison.Ordinal) || asset.Name.EndsWith(".appx", StringComparison.Ordinal)))
        .ToArray();
}

public sealed class AppUpdateException(string resourceKey, Exception? inner = null) : IOException(resourceKey, inner)
{
    public string ResourceKey { get; } = resourceKey;
}

public sealed class AppReleaseClient(HttpClient http)
{
    public static readonly Uri ReleasesUri = new("https://api.github.com/repos/Liis17/Hako/releases");
    public static readonly Uri ReleasesPage = new("https://github.com/Liis17/Hako/releases");

    public async Task<AppRelease?> LatestAsync(string architecture, CancellationToken cancellationToken = default)
    {
        var next = new Uri(ReleasesUri + "?per_page=100&page=1");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        AppRelease? latest = null;
        while (next is not null)
        {
            if (!visited.Add(next.AbsoluteUri)) throw new AppUpdateException("AppUpdateInvalidRelease");
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.UserAgent.ParseAdd("Hako/1.0 (+https://github.com/Liis17/Hako)");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new AppUpdateException("AppUpdateRateLimited");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            AppRelease[] releases;
            try
            {
                releases = await JsonSerializer.DeserializeAsync(stream, HakoJsonContext.Default.AppReleaseArray, cancellationToken).ConfigureAwait(false)
                    ?? throw new JsonException();
            }
            catch (JsonException exception) { throw new AppUpdateException("AppUpdateInvalidRelease", exception); }
            foreach (var release in releases)
            {
                if (release is null || release.Draft || release.Prerelease || release.PublishedAt is null
                    || release.GetVersion() is not { } version) continue;
                // A partially published release is not an installable update.
                if (release.Assets is null || release.Assets.Any(asset => asset is null)
                    || release.Assets.Count(asset => asset.Name == $"Hako-{release.Tag[9..]}-windows-{architecture}.msix") != 1) continue;
                if (latest is null || version > latest.GetVersion()) latest = release;
            }
            next = NextPage(response, next);
        }
        return latest;
    }

    private static Uri? NextPage(HttpResponseMessage response, Uri current)
    {
        if (!response.Headers.TryGetValues("Link", out var headers)) return null;
        foreach (var link in headers.SelectMany(value => value.Split(',')))
        {
            var parts = link.Split(';', StringSplitOptions.TrimEntries);
            if (!parts.Skip(1).Any(part => part is "rel=\"next\"" or "rel=next")) continue;
            var target = parts[0];
            if (!target.StartsWith('<') || !target.EndsWith('>')
                || !Uri.TryCreate(current, target[1..^1], out var uri) || uri.Scheme != "https"
                || uri.Host != "api.github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
                throw new AppUpdateException("AppUpdateInvalidRelease");
            var path = uri.AbsolutePath.Split('/');
            if (uri.AbsolutePath != ReleasesUri.AbsolutePath
                && !(path.Length == 4 && path[1] == "repositories" && path[3] == "releases"
                    && ulong.TryParse(path[2], NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                throw new AppUpdateException("AppUpdateInvalidRelease");
            return uri;
        }
        return null;
    }
}

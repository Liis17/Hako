using System.Security.Cryptography;
using System.Text.Json;
using Hako.Core.Instances;

namespace Hako.Core.Minecraft;

/// <summary>The shared HTTPS boundary for Modrinth version queries and verified file downloads.</summary>
public sealed class ModrinthClient : IDisposable
{
    public const string UserAgent = "Hako/1.0 (https://github.com/Liis17/Hako)";
    private const int MaximumResponseSize = 32 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _base;

    public ModrinthClient(HttpClient? httpClient = null, Uri? baseUri = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _base = EnsureTrailingSlash(baseUri ?? new Uri("https://api.modrinth.com/v2/"));
    }

    public async Task<IReadOnlyList<ModrinthVersion>> GetProjectVersionsAsync(
        string projectId,
        IReadOnlyList<string> gameVersions,
        IReadOnlyList<string> loaders,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var games = Uri.EscapeDataString(JsonSerializer.Serialize(gameVersions.ToArray(), HakoJsonContext.Default.StringArray));
        var loaderFilter = Uri.EscapeDataString(JsonSerializer.Serialize(loaders.ToArray(), HakoJsonContext.Default.StringArray));
        var relative = $"project/{Uri.EscapeDataString(projectId)}/version?game_versions={games}&loaders={loaderFilter}&include_changelog=false";
        var bytes = await GetBytesAsync(new Uri(_base, relative), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, FabricJsonContext.Default.ModrinthVersionArray)
            ?? throw new MojangException(MojangError.Invalid, "Modrinth returned an empty project version list.");
    }

    public async Task DownloadFileAsync(Uri url, string destination, long size, string sha1, string sha512,
        CancellationToken cancellationToken = default)
    {
        RequireHttps(url);
        if (size <= 0 || !IsHash(sha1, 40) || !IsHash(sha512, 128))
            throw new MojangException(MojangError.Invalid, "Modrinth returned invalid file metadata.");

        var fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        var temporary = fullDestination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var response = await SendAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } length && length != size)
                throw new MojangException(MojangError.Integrity, "Modrinth file size did not match its version metadata.");
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
                    if (total > size)
                        throw new MojangException(MojangError.Integrity, "Modrinth file exceeded its declared size.");
                    sha1Hash.AppendData(buffer, 0, read);
                    sha512Hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                if (total != size || !HashEquals(sha1Hash.GetHashAndReset(), sha1)
                    || !HashEquals(sha512Hash.GetHashAndReset(), sha512))
                    throw new MojangException(MojangError.Integrity, "Modrinth file failed its SHA-1 or SHA-512 check.");
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
        using var response = await SendAsync(uri, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } length && length > MaximumResponseSize)
            throw new MojangException(MojangError.Invalid, "Modrinth response is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumResponseSize)
                throw new MojangException(MojangError.Invalid, "Modrinth response is too large.");
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        RequireHttps(uri);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new MojangException(MojangError.Http, $"Modrinth returned HTTP {status}.", status);
        }

        return response;
    }

    private static bool IsHash(string? value, int length) => value is { Length: var size } && size == length && value.All(Uri.IsHexDigit);
    private static bool HashEquals(byte[] hash, string expected) => Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    private static void RequireHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
            throw new MojangException(MojangError.Invalid, "Modrinth URLs must use HTTPS.");
    }
    private static Uri EnsureTrailingSlash(Uri uri) => new(uri.AbsoluteUri.TrimEnd('/') + "/");
}

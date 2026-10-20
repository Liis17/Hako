using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hako.Core;

public sealed record MinecraftSkinData(byte[] SkinPng, MinecraftSkinVariant Variant, byte[]? CapePng);

/// <summary>Loads public Minecraft skin textures without account credentials.</summary>
public sealed class MinecraftSkinLoader(HttpClient httpClient)
{
    private const int MaximumProfileBytes = 1024 * 1024;
    private const int MaximumTextureBytes = 4 * 1024 * 1024;
    private const string ProfileHost = "sessionserver.mojang.com";
    private const string TextureHost = "textures.minecraft.net";

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ConcurrentDictionary<string, ResolvedProfile> _profileCache = new(StringComparer.Ordinal);

    public async Task<MinecraftSkinData?> LoadAsync(string? uuid, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(uuid))
        {
            return null;
        }

        if (!Guid.TryParseExact(uuid, "N", out var parsedUuid) && !Guid.TryParseExact(uuid, "D", out parsedUuid))
        {
            throw new ArgumentException("The Minecraft profile ID must be a UUID.", nameof(uuid));
        }

        var normalizedUuid = parsedUuid.ToString("N");
        var profile = _profileCache.TryGetValue(normalizedUuid, out var cached)
            ? cached
            : await ResolveProfileAsync(normalizedUuid, cancellationToken).ConfigureAwait(false);

        var skinPng = await DownloadAsync(profile.SkinUri, MaximumTextureBytes, IsTextureUri, cancellationToken)
            .ConfigureAwait(false);

        byte[]? capePng = null;
        if (profile.CapeUri is not null)
        {
            try
            {
                capePng = await DownloadAsync(profile.CapeUri, MaximumTextureBytes, IsTextureUri, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                !cancellationToken.IsCancellationRequested &&
                exception is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
            {
                capePng = null;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        _profileCache.TryAdd(normalizedUuid, profile);
        return new(skinPng, profile.Variant, capePng);
    }

    private async Task<ResolvedProfile> ResolveProfileAsync(string uuid, CancellationToken cancellationToken)
    {
        var profileUri = new Uri($"https://{ProfileHost}/session/minecraft/profile/{uuid}");
        var profileBytes = await DownloadAsync(profileUri, MaximumProfileBytes, IsProfileUri, cancellationToken)
            .ConfigureAwait(false);
        var sessionProfile = JsonSerializer.Deserialize(profileBytes, HakoJsonContext.Default.MinecraftSessionProfileResponse)
            ?? throw new InvalidDataException("The Mojang profile response was empty.");
        var encodedTextures = sessionProfile.Properties?
            .FirstOrDefault(property => string.Equals(property.Name, "textures", StringComparison.Ordinal))?.Value;
        if (string.IsNullOrEmpty(encodedTextures))
        {
            throw new InvalidDataException("The Mojang profile does not contain a textures property.");
        }

        byte[] texturePayload;
        try
        {
            texturePayload = Convert.FromBase64String(encodedTextures);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The Mojang textures property is not valid base64.", exception);
        }

        var textures = JsonSerializer.Deserialize(texturePayload, HakoJsonContext.Default.MinecraftTexturePayload)
            ?? throw new InvalidDataException("The Mojang textures payload was empty.");
        var skin = textures.Textures?.Skin;
        if (!TryGetTextureUri(skin?.Url, out var skinUri))
        {
            throw new InvalidDataException("The Mojang profile does not contain a safe skin URL.");
        }

        TryGetTextureUri(GetCapeUrl(textures.Textures?.Cape), out var capeUri);
        var variant = string.Equals(skin?.Metadata?.Model, "slim", StringComparison.OrdinalIgnoreCase)
            ? MinecraftSkinVariant.Slim
            : MinecraftSkinVariant.Classic;

        return new(skinUri, variant, capeUri);
    }

    private async Task<byte[]> DownloadAsync(
        Uri uri,
        int maximumBytes,
        Func<Uri, bool> isAllowedUri,
        CancellationToken cancellationToken)
    {
        if (!isAllowedUri(uri))
        {
            throw new InvalidDataException("The requested URL is not allowed.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (response.RequestMessage?.RequestUri is { } responseUri && !isAllowedUri(responseUri))
        {
            throw new InvalidDataException("The server redirected to a URL that is not allowed.");
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException(
                $"The skin server returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        if (response.Content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
        {
            throw new InvalidDataException("The server response exceeds the allowed size.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(response.Content.Headers.ContentLength is long length ? (int)length : 0);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var remaining = maximumBytes - (int)output.Length;
            var bytesRead = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            if (bytesRead > remaining)
            {
                throw new InvalidDataException("The server response exceeds the allowed size.");
            }

            output.Write(buffer, 0, bytesRead);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    private static bool TryGetTextureUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
            candidate.UserInfo.Length == 0 &&
            candidate.IsDefaultPort &&
            string.IsNullOrEmpty(candidate.Fragment) &&
            string.Equals(candidate.Host, TextureHost, StringComparison.OrdinalIgnoreCase) &&
            (candidate.Scheme == Uri.UriSchemeHttps || candidate.Scheme == Uri.UriSchemeHttp))
        {
            if (candidate.Scheme == Uri.UriSchemeHttp)
            {
                var builder = new UriBuilder(candidate) { Scheme = Uri.UriSchemeHttps };
                builder.Port = -1;
                uri = builder.Uri;
            }
            else
            {
                uri = candidate;
            }

            return true;
        }

        uri = null!;
        return false;
    }

    private static string? GetCapeUrl(JsonElement? cape)
    {
        if (!cape.HasValue || cape.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var capeObject = cape.Value;
        if (!capeObject.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return url.GetString();
    }

    private static bool IsProfileUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 &&
        string.Equals(uri.Host, ProfileHost, StringComparison.OrdinalIgnoreCase);

    private static bool IsTextureUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 &&
        string.Equals(uri.Host, TextureHost, StringComparison.OrdinalIgnoreCase);

    private sealed record ResolvedProfile(Uri SkinUri, MinecraftSkinVariant Variant, Uri? CapeUri);
}

internal sealed record MinecraftSessionProfileResponse(List<MinecraftSessionProfileProperty>? Properties);

internal sealed record MinecraftSessionProfileProperty(string? Name, string? Value);

internal sealed record MinecraftTexturePayload(MinecraftTextureSet? Textures);

internal sealed record MinecraftTextureSet(
    [property: JsonPropertyName("SKIN")] MinecraftTexture? Skin,
    [property: JsonPropertyName("CAPE")] JsonElement? Cape);

internal sealed record MinecraftTexture(string? Url, MinecraftTextureMetadata? Metadata);

internal sealed record MinecraftTextureMetadata(string? Model);

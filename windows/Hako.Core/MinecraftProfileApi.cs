using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Hako.Core;

public interface IMinecraftProfileApi
{
    Task<MinecraftAppearanceProfile> ProfileAsync(string token, CancellationToken cancellationToken);
    Task UploadSkinAsync(string token, byte[] png, MinecraftSkinVariant variant, CancellationToken cancellationToken);
    Task SetCapeAsync(string token, string? capeId, CancellationToken cancellationToken);
    Task<byte[]> TextureAsync(string url, CancellationToken cancellationToken);
}

public sealed class MinecraftProfileApi(HttpClient httpClient) : IMinecraftProfileApi
{
    private const string ProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    public async Task<MinecraftAppearanceProfile> ProfileAsync(string token, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, ProfileUrl, token);
        var bytes = await SendAsync(request, 1024 * 1024, cancellationToken);
        var profile = JsonSerializer.Deserialize(bytes, HakoJsonContext.Default.MinecraftAppearanceProfile);
        if (profile is null || string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name)
            || profile.Skins is null || profile.Capes is null
            || profile.Capes.Any(c => string.IsNullOrWhiteSpace(c.Id)))
            throw new InvalidDataException("CharacterProfileError");
        return profile;
    }

    public async Task UploadSkinAsync(string token, byte[] png, MinecraftSkinVariant variant, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Post, ProfileUrl + "/skins", token);
        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(variant == MinecraftSkinVariant.Slim ? "slim" : "classic"), "variant");
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        multipart.Add(file, "file", "skin.png");
        request.Content = multipart;
        await SendAsync(request, 1024 * 1024, cancellationToken);
    }

    public async Task SetCapeAsync(string token, string? capeId, CancellationToken cancellationToken)
    {
        using var request = Authorized(capeId is null ? HttpMethod.Delete : HttpMethod.Put, ProfileUrl + "/capes/active", token);
        if (capeId is not null) request.Content = JsonContent.Create(new MinecraftCapeSelection(capeId), HakoJsonContext.Default.MinecraftCapeSelection);
        await SendAsync(request, 1024 * 1024, cancellationToken);
    }

    public async Task<byte[]> TextureAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host != "textures.minecraft.net"
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || uri.Scheme is not ("http" or "https")) throw new InvalidDataException("CharacterPreviewError");
        uri = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        return await SendAsync(request, MinecraftSkinUpload.MaximumBytes, cancellationToken);
    }

    private async Task<byte[]> SendAsync(HttpRequestMessage request, int limit, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (httpClient.Timeout != Timeout.InfiniteTimeSpan) timeout.CancelAfter(httpClient.Timeout);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != request.RequestUri)
            throw new InvalidDataException("CharacterProfileError");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("CharacterRequestError", null, response.StatusCode);
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("CharacterProfileError");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)output.Length)), timeout.Token);
            if (count == 0) break;
            output.Write(buffer, 0, count);
            if (output.Length > limit) throw new InvalidDataException("CharacterProfileError");
        }
        timeout.Token.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}

internal sealed record MinecraftCapeSelection(string CapeId);

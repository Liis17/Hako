using Hako.Core;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hako.Tests;

[TestClass]
public sealed class MinecraftSkinLoaderTests
{
    private const string ProfileId = "12345678-1234-1234-1234-123456789abc";
    private const string SkinTextureUrl = "https://textures.minecraft.net/texture/skin";
    private static readonly byte[] SkinBytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
    private static readonly byte[] CapeBytes = [0x89, 0x50, 0x4E, 0x47, 4, 5, 6];

    [TestMethod]
    public async Task LoadAsync_UsesPublicProfile_UpgradesMojangHttpUrls_AndReturnsTextures()
    {
        var requests = new List<Uri>();
        var authHeaders = new List<string?>();
        var handler = new CallbackHttpHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            requests.Add(uri);
            authHeaders.Add(request.Headers.Authorization?.ToString());
            if (uri.Host == "sessionserver.mojang.com")
            {
                return Task.FromResult(JsonResponse(ProfileJson(
                    "http://textures.minecraft.net/texture/skin",
                    "slim",
                    "http://textures.minecraft.net/texture/cape")));
            }

            return Task.FromResult(ByteResponse(uri.AbsolutePath.EndsWith("cape", StringComparison.Ordinal)
                ? CapeBytes
                : SkinBytes, request));
        });
        using var client = new HttpClient(handler);
        var loader = new MinecraftSkinLoader(client);

        var result = await loader.LoadAsync(ProfileId);

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(SkinBytes, result.SkinPng);
        CollectionAssert.AreEqual(CapeBytes, result.CapePng);
        Assert.AreEqual(MinecraftSkinVariant.Slim, result.Variant);
        Assert.AreEqual(3, requests.Count);
        Assert.AreEqual($"/session/minecraft/profile/{ProfileId.Replace("-", string.Empty)}", requests[0].AbsolutePath);
        Assert.IsTrue(requests.Skip(1).All(uri => uri.Scheme == Uri.UriSchemeHttps));
        Assert.IsTrue(authHeaders.All(header => header is null));
    }

    [TestMethod]
    public async Task LoadAsync_WithoutUuid_DoesNotSendRequests()
    {
        var handler = new CallbackHttpHandler((_, _) => throw new AssertFailedException("Unexpected request."));
        using var client = new HttpClient(handler);
        var loader = new MinecraftSkinLoader(client);

        Assert.IsNull(await loader.LoadAsync(null));
        Assert.IsNull(await loader.LoadAsync(string.Empty));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    [DataRow("not-a-uuid")]
    [DataRow("{12345678-1234-1234-1234-123456789abc}")]
    [DataRow("12345678-1234-1234-1234-123456789abz")]
    public async Task LoadAsync_InvalidUuid_IsRejectedWithoutRequest(string uuid)
    {
        var handler = new CallbackHttpHandler((_, _) => throw new AssertFailedException("Unexpected request."));
        using var client = new HttpClient(handler);
        var loader = new MinecraftSkinLoader(client);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => loader.LoadAsync(uuid));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task LoadAsync_CachesProfileMetadataButDownloadsSkinBytesForEachLoad()
    {
        var profileRequests = 0;
        var skinRequests = 0;
        var handler = new CallbackHttpHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "sessionserver.mojang.com")
            {
                profileRequests++;
                return Task.FromResult(JsonResponse(ProfileJson(SkinTextureUrl)));
            }

            skinRequests++;
            return Task.FromResult(ByteResponse([(byte)skinRequests], request));
        });
        using var client = new HttpClient(handler);
        var loader = new MinecraftSkinLoader(client);

        var first = await loader.LoadAsync(ProfileId);
        var second = await loader.LoadAsync(ProfileId.Replace("-", string.Empty, StringComparison.Ordinal));

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        CollectionAssert.AreEqual(new byte[] { 1 }, first.SkinPng);
        CollectionAssert.AreEqual(new byte[] { 2 }, second.SkinPng);
        Assert.AreEqual(MinecraftSkinVariant.Classic, first.Variant);
        Assert.AreEqual(1, profileRequests);
        Assert.AreEqual(2, skinRequests);
    }

    [TestMethod]
    public async Task LoadAsync_InvalidBase64_IsNotCachedAndCanBeRetried()
    {
        var profileRequests = 0;
        var handler = new CallbackHttpHandler((request, _) =>
        {
            if (request.RequestUri!.Host != "sessionserver.mojang.com")
            {
                return Task.FromResult(ByteResponse(SkinBytes, request));
            }

            profileRequests++;
            var body = profileRequests == 1
                ? "{\"properties\":[{\"name\":\"textures\",\"value\":\"not base64!\"}]}"
                : ProfileJson(SkinTextureUrl);
            return Task.FromResult(JsonResponse(body));
        });
        using var client = new HttpClient(handler);
        var loader = new MinecraftSkinLoader(client);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => loader.LoadAsync(ProfileId));
        var result = await loader.LoadAsync(ProfileId);

        Assert.IsNotNull(result);
        Assert.AreEqual(2, profileRequests);
    }

    [TestMethod]
    public async Task LoadAsync_MalformedProfileJson_IsRejected()
    {
        var handler = new CallbackHttpHandler((request, _) => Task.FromResult(
            request.RequestUri!.Host == "sessionserver.mojang.com"
                ? JsonResponse("{")
                : ByteResponse(SkinBytes, request)));
        using var client = new HttpClient(handler);
        var loader = new MinecraftSkinLoader(client);

        await Assert.ThrowsExactlyAsync<JsonException>(() => loader.LoadAsync(ProfileId));
    }

    [TestMethod]
    public async Task LoadAsync_CapeFailure_DoesNotPreventSkinResult()
    {
        var profileRequests = 0;
        var handler = new CallbackHttpHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "sessionserver.mojang.com")
            {
                profileRequests++;
                return Task.FromResult(JsonResponse(ProfileJson(SkinTextureUrl, capeUrl: "https://textures.minecraft.net/texture/cape")));
            }

            return Task.FromResult(uri.AbsolutePath.EndsWith("cape", StringComparison.Ordinal)
                ? EmptyResponse(HttpStatusCode.ServiceUnavailable, request)
                : ByteResponse(SkinBytes, request));
        });
        using var client = new HttpClient(handler);
        var loader = new MinecraftSkinLoader(client);

        var result = await loader.LoadAsync(ProfileId);

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(SkinBytes, result.SkinPng);
        Assert.IsNull(result.CapePng);
        Assert.AreEqual(1, profileRequests);
    }

    [TestMethod]
    public async Task LoadAsync_MalformedCapeMetadata_DoesNotPreventSkinResult()
    {
        var textures = $"{{\"textures\":{{\"SKIN\":{{\"url\":\"{SkinTextureUrl}\"}},\"CAPE\":false}}}}";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(textures));
        var profile = JsonSerializer.Serialize(new { properties = new[] { new { name = "textures", value = encoded } } });
        var handler = new CallbackHttpHandler((request, _) => Task.FromResult(
            request.RequestUri!.Host == "sessionserver.mojang.com"
                ? JsonResponse(profile)
                : ByteResponse(SkinBytes, request)));
        using var client = new HttpClient(handler);
        var loader = new MinecraftSkinLoader(client);

        var result = await loader.LoadAsync(ProfileId);

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(SkinBytes, result.SkinPng);
        Assert.IsNull(result.CapePng);
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task LoadAsync_Cancellation_DoesNotCacheProfileAndAllowsRetry()
    {
        var profileRequests = 0;
        var profileRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CallbackHttpHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Host != "sessionserver.mojang.com")
            {
                return ByteResponse(SkinBytes, request);
            }

            profileRequests++;
            if (profileRequests == 1)
            {
                profileRequestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return JsonResponse(ProfileJson(SkinTextureUrl));
        });
        using var client = new HttpClient(handler);
        var loader = new MinecraftSkinLoader(client);
        using var cancellation = new CancellationTokenSource();

        var pending = loader.LoadAsync(ProfileId, cancellation.Token);
        await profileRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => pending);
        Assert.IsNotNull(await loader.LoadAsync(ProfileId));
        Assert.AreEqual(2, profileRequests);
    }

    [TestMethod]
    [DataRow("file:///tmp/skin.png")]
    [DataRow("https://example.com/texture/skin")]
    [DataRow("http://example.com/texture/skin")]
    [DataRow("https://user@textures.minecraft.net/texture/skin")]
    public async Task LoadAsync_RejectsUnsafeSkinUrl(string url)
    {
        var handler = new CallbackHttpHandler((request, _) => Task.FromResult(
            request.RequestUri!.Host == "sessionserver.mojang.com"
                ? JsonResponse(ProfileJson(url))
                : ByteResponse(SkinBytes, request)));
        using var client = new HttpClient(handler);
        var loader = new MinecraftSkinLoader(client);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => loader.LoadAsync(ProfileId));
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task LoadAsync_RejectsOversizedProfileResponse()
    {
        var oversized = new ByteArrayContent(new byte[1024 * 1024 + 1]);
        var handler = new CallbackHttpHandler((request, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = oversized, RequestMessage = request }));
        using var client = new HttpClient(handler);
        var loader = new MinecraftSkinLoader(client);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => loader.LoadAsync(ProfileId));
    }

    [TestMethod]
    public async Task LoadAsync_RejectsOversizedSkinResponse()
    {
        var handler = new CallbackHttpHandler((request, _) => Task.FromResult(
            request.RequestUri!.Host == "sessionserver.mojang.com"
                ? JsonResponse(ProfileJson(SkinTextureUrl))
                : ByteResponse(new byte[4 * 1024 * 1024 + 1], request)));
        using var client = new HttpClient(handler);
        var loader = new MinecraftSkinLoader(client);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => loader.LoadAsync(ProfileId));
    }

    private static string ProfileJson(string skinUrl, string? model = null, string? capeUrl = null)
    {
        var skin = new Dictionary<string, object?> { ["url"] = skinUrl };
        if (model is not null)
        {
            skin["metadata"] = new { model };
        }

        var textures = new Dictionary<string, object?> { ["SKIN"] = skin };
        if (capeUrl is not null)
        {
            textures["CAPE"] = new { url = capeUrl };
        }

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { textures })));
        return JsonSerializer.Serialize(new { properties = new[] { new { name = "textures", value = encoded } } });
    }

    private static HttpResponseMessage JsonResponse(string json) => TestData.Json(HttpStatusCode.OK, json);

    private static HttpResponseMessage ByteResponse(byte[] bytes, HttpRequestMessage request) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request };

    private static HttpResponseMessage EmptyResponse(HttpStatusCode status, HttpRequestMessage request) =>
        new(status) { RequestMessage = request };
}

[TestClass]
public sealed class MinecraftSkinNormalizerTests
{
    [TestMethod]
    public void NormalizeRgba_64By64_PreservesVariantAndOverlayAlpha_AndMakesBaseOpaque()
    {
        var rgba = new byte[64 * 64 * 4];
        rgba[(0 * 64 + 40) * 4 + 3] = 71;

        var result = MinecraftSkinNormalizer.NormalizeRgba(rgba, 64, 64, MinecraftSkinVariant.Slim);

        Assert.AreEqual(MinecraftSkinVariant.Slim, result.Variant);
        Assert.AreEqual(byte.MaxValue, result.Rgba[(0 * 64 + 0) * 4 + 3]);
        Assert.AreEqual((byte)71, result.Rgba[(0 * 64 + 40) * 4 + 3]);
        Assert.AreEqual((byte)0, result.Rgba[(0 * 64 + 63) * 4 + 3]);
    }

    [TestMethod]
    public void NormalizeRgba_LegacyFormat_MirrorsLeftLimbsAndForcesClassicVariant()
    {
        var rgba = new byte[64 * 32 * 4];
        for (var y = 20; y < 32; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                rgba[(y * 64 + x) * 4] = (byte)(x + 1);
                rgba[(y * 64 + x) * 4 + 1] = (byte)(y + 1);
            }
        }

        var result = MinecraftSkinNormalizer.NormalizeRgba(rgba, 64, 32, MinecraftSkinVariant.Slim);

        Assert.AreEqual(MinecraftSkinVariant.Classic, result.Variant);
        Assert.AreEqual((byte)4, result.Rgba[(52 * 64 + 24) * 4]);
        Assert.AreEqual((byte)1, result.Rgba[(52 * 64 + 27) * 4]);
        Assert.AreEqual((byte)21, result.Rgba[(52 * 64 + 24) * 4 + 1]);
        Assert.AreEqual(byte.MaxValue, result.Rgba[(52 * 64 + 24) * 4 + 3]);
    }

    [TestMethod]
    public void NormalizeRgba_LegacyFormat_ClearsOpaqueOldHatAndPreservesTransparentOverlay()
    {
        var opaque = Enumerable.Repeat(byte.MaxValue, 64 * 32 * 4).ToArray();
        var clearedHat = MinecraftSkinNormalizer.NormalizeRgba(opaque, 64, 32, MinecraftSkinVariant.Classic);
        Assert.AreEqual((byte)0, clearedHat.Rgba[(0 * 64 + 32) * 4 + 3]);
        Assert.AreEqual((byte)0, clearedHat.Rgba[(15 * 64 + 63) * 4 + 3]);

        opaque[(0 * 64 + 32) * 4 + 3] = 0;
        opaque[(0 * 64 + 40) * 4 + 3] = 79;
        var preservedOverlay = MinecraftSkinNormalizer.NormalizeRgba(opaque, 64, 32, MinecraftSkinVariant.Classic);
        Assert.AreEqual((byte)79, preservedOverlay.Rgba[(0 * 64 + 40) * 4 + 3]);
    }

    [TestMethod]
    public void NormalizeCapeRgba_64By32_PreservesPixels()
    {
        var rgba = Enumerable.Range(0, 64 * 32 * 4).Select(value => (byte)value).ToArray();

        var normalized = MinecraftSkinNormalizer.NormalizeCapeRgba(rgba, 64, 32);

        CollectionAssert.AreEqual(rgba, normalized);
    }

    [TestMethod]
    public void NormalizeRgba_RejectsUnsupportedDimensionsAndCorruptLengths()
    {
        Assert.ThrowsExactly<ArgumentException>(() => MinecraftSkinNormalizer.NormalizeRgba(new byte[64 * 64 * 4], 63, 64, MinecraftSkinVariant.Classic));
        Assert.ThrowsExactly<ArgumentException>(() => MinecraftSkinNormalizer.NormalizeRgba(new byte[64 * 64 * 4], 64, 30, MinecraftSkinVariant.Classic));
        Assert.ThrowsExactly<ArgumentException>(() => MinecraftSkinNormalizer.NormalizeRgba(new byte[32], 64, 64, MinecraftSkinVariant.Classic));
        Assert.ThrowsExactly<ArgumentException>(() => MinecraftSkinNormalizer.NormalizeCapeRgba(new byte[64 * 32 * 4], 64, 64));
    }

    [TestMethod]
    public void SteveAsset_IsAnExactCopyOfMacResourceWithDocumentedHash()
    {
        var windowsPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "WindowsSteve.png");
        var macPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MacSteve.png");
        var windowsBytes = File.ReadAllBytes(windowsPath);
        var macBytes = File.ReadAllBytes(macPath);

        CollectionAssert.AreEqual(macBytes, windowsBytes);
        Assert.AreEqual(
            "06628f9cef88520da742ab280a01677ce17c99717f964ed89ed791673121094c",
            Convert.ToHexString(SHA256.HashData(windowsBytes)).ToLowerInvariant());
    }
}

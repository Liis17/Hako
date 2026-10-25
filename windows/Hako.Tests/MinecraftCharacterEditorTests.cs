using System.Buffers.Binary;
using System.Net;
using Hako.Core;

namespace Hako.Tests;

[TestClass]
public sealed class MinecraftCharacterEditorTests
{
    private const string ProfileId = "00112233445566778899aabbccddeeff";

    [TestMethod]
    public async Task Save_ConfirmsSkinAndCapeAndDoesNotUploadConfirmedSkinAgain()
    {
        var (app, _) = await AppAsync();
        var api = new FakeApi { FailCape = true };
        using var editor = new MinecraftCharacterEditor(app, api, new FakeDecoder());
        await editor.OpenAsync();
        using var file = new UploadFile(Png(2));
        await editor.ImportAsync(file.Path);
        await editor.SelectCapeAsync("cape");
        editor.SetPreviewReady(editor.PreviewRevision, true);
        Assert.IsFalse(await editor.SaveAsync());
        Assert.AreEqual(1, api.Uploads);
        Assert.AreEqual("CharacterSaveError", editor.ErrorKey);
        api.FailCape = false;
        Assert.IsTrue(await editor.SaveAsync());
        Assert.AreEqual(1, api.Uploads);
        Assert.AreEqual("cape", editor.Profile!.ActiveCape!.Id);
        Assert.IsFalse(editor.HasChanges);
    }

    [TestMethod]
    public async Task Save_LostUploadResponseIsReconciledWithoutDuplicateWrite()
    {
        var (app, _) = await AppAsync();
        var api = new FakeApi { LoseUploadResponse = true };
        using var editor = new MinecraftCharacterEditor(app, api, new FakeDecoder());
        await editor.OpenAsync();
        using var file = new UploadFile(Png(2));
        await editor.ImportAsync(file.Path);
        editor.SetPreviewReady(editor.PreviewRevision, true);
        Assert.IsTrue(await editor.SaveAsync());
        Assert.AreEqual(1, api.Uploads);
    }

    [TestMethod]
    public async Task Save_UnconfirmedUploadRetainsDraftAndReconcilesBeforeRetry()
    {
        var (app, _) = await AppAsync();
        var api = new FakeApi { IgnoreUpload = true };
        using var editor = new MinecraftCharacterEditor(app, api, new FakeDecoder());
        await editor.OpenAsync();
        using var file = new UploadFile(Png(2));
        await editor.ImportAsync(file.Path);
        editor.SetPreviewReady(editor.PreviewRevision, true);
        Assert.IsFalse(await editor.SaveAsync());
        Assert.AreEqual("CharacterSaveUnconfirmed", editor.ErrorKey);
        Assert.IsTrue(editor.HasChanges);
        api.Skin = Png(2); // The previously accepted upload has now become visible.
        Assert.IsTrue(await editor.SaveAsync());
        Assert.AreEqual(1, api.Uploads);
    }

    [TestMethod]
    public async Task SelectCape_UnequipAndPreviewRevisionPreventStaleReadiness()
    {
        var (app, _) = await AppAsync();
        var api = new FakeApi { ActiveCape = "cape" };
        using var editor = new MinecraftCharacterEditor(app, api, new FakeDecoder());
        await editor.OpenAsync();
        var oldRevision = editor.PreviewRevision;
        await editor.SelectCapeAsync(null);
        Assert.IsNull(editor.Preview!.CapeRgba);
        editor.SetPreviewReady(oldRevision, true);
        Assert.IsFalse(editor.CanSave);
        editor.SetPreviewReady(editor.PreviewRevision, true);
        Assert.IsTrue(await editor.SaveAsync());
        Assert.IsNull(editor.Profile!.ActiveCape);
    }

    [TestMethod]
    public async Task Import_InvalidPngRetainsPreviousDraftAndLegacyForcesClassic()
    {
        var (app, _) = await AppAsync();
        using var editor = new MinecraftCharacterEditor(app, new FakeApi(), new FakeDecoder());
        await editor.OpenAsync();
        using var legacy = new UploadFile(Png(2, 32));
        await editor.ImportAsync(legacy.Path);
        await editor.SelectVariantAsync(MinecraftSkinVariant.Slim);
        Assert.IsTrue(editor.IsLegacy);
        Assert.AreEqual(MinecraftSkinVariant.Classic, editor.SelectedVariant);
        var preview = editor.Preview;
        using var invalid = new UploadFile([1, 2, 3]);
        await editor.ImportAsync(invalid.Path);
        Assert.AreSame(preview, editor.Preview);
        Assert.AreEqual("CharacterInvalidPng", editor.ErrorKey);
    }

    [TestMethod]
    public void Upload_RejectsOversizedAndWrongDimensionsBeforeDecoding()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => MinecraftSkinUpload.Validate(new byte[MinecraftSkinUpload.MaximumBytes + 1]));
        var png = Png(1);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 128);
        Assert.ThrowsExactly<InvalidDataException>(() => MinecraftSkinUpload.Validate(png));
    }

    [TestMethod]
    public async Task Profile_UnauthorizedRefreshesSessionOnce()
    {
        var (app, auth) = await AppAsync();
        var api = new FakeApi { UnauthorizedReads = 1 };
        using var editor = new MinecraftCharacterEditor(app, api, new FakeDecoder());
        await editor.OpenAsync();
        Assert.IsNotNull(editor.Profile);
        Assert.AreEqual(1, auth.ConnectionCount);
        Assert.AreEqual(2, api.ProfileReads);
    }

    [TestMethod]
    public async Task Profile_PersistentUnauthorizedDoesNotLoop()
    {
        var (app, auth) = await AppAsync();
        using var editor = new MinecraftCharacterEditor(app, new FakeApi { UnauthorizedReads = 3 }, new FakeDecoder());
        await editor.OpenAsync();
        Assert.AreEqual("CharacterSessionError", editor.ErrorKey);
        Assert.AreEqual(1, auth.ConnectionCount);
    }

    [TestMethod]
    public async Task Profile_LateResponseAfterCloseDoesNotPublishAppearance()
    {
        var (app, _) = await AppAsync();
        var api = new FakeApi { ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var editor = new MinecraftCharacterEditor(app, api, new FakeDecoder());
        var published = 0;
        editor.AppearanceConfirmed += (_, _) => published++;
        var opening = editor.OpenAsync();
        await api.ReadStarted.Task;
        editor.Dispose();
        api.ReadGate.SetResult();
        await opening;
        Assert.AreEqual(0, published);
        Assert.IsNull(editor.Profile);
    }

    [TestMethod]
    public async Task Profile_LateResponseAfterSignOutDoesNotPublishAppearance()
    {
        var (app, _) = await AppAsync();
        var api = new FakeApi { ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var editor = new MinecraftCharacterEditor(app, api, new FakeDecoder());
        var opening = editor.OpenAsync();
        await api.ReadStarted.Task;
        await app.LogoutAsync();
        api.ReadGate.SetResult();
        await opening;
        Assert.IsNull(editor.Profile);
        Assert.IsFalse(editor.CanSave);
    }

    [TestMethod]
    public async Task Profile_NoCapesStillAllowsSkinChanges()
    {
        var (app, _) = await AppAsync();
        using var editor = new MinecraftCharacterEditor(app, new FakeApi { NoCapes = true }, new FakeDecoder());
        await editor.OpenAsync();
        Assert.HasCount(0, editor.Profile!.Capes);
        using var file = new UploadFile(Png(2));
        await editor.ImportAsync(file.Path);
        await editor.SelectVariantAsync(MinecraftSkinVariant.Slim);
        editor.SetPreviewReady(editor.PreviewRevision, true);
        Assert.IsTrue(await editor.SaveAsync());
    }

    [TestMethod]
    public async Task Api_UsesMultipartAndCapePutDeleteWithoutTextureAuthorization()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new CallbackHttpHandler(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "textures.minecraft.net") Assert.IsNull(request.Headers.Authorization);
            else Assert.AreEqual("Bearer test-token", request.Headers.Authorization!.ToString());
            requests.Add(request.Method + " " + request.RequestUri.AbsolutePath + " "
                + (request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }));
        var api = new MinecraftProfileApi(http);
        await api.UploadSkinAsync("test-token", [1, 2], MinecraftSkinVariant.Slim, default);
        await api.SetCapeAsync("test-token", "cape", default);
        await api.SetCapeAsync("test-token", null, default);
        await api.TextureAsync("http://textures.minecraft.net/texture/cape", default);
        StringAssert.Contains(requests[0], "name=variant");
        StringAssert.Contains(requests[0], "slim");
        StringAssert.Contains(requests[0], "image/png");
        StringAssert.Contains(requests[1], "\"capeId\": \"cape\"");
        StringAssert.StartsWith(requests[2], "DELETE /minecraft/profile/capes/active");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => api.TextureAsync("https://example.com/cape", default));
    }

    private static async Task<(AppCoordinator, RefreshAuthService)> AppAsync()
    {
        var account = TestData.Account() with { MinecraftProfileId = ProfileId, MinecraftTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        var auth = new RefreshAuthService((_, _, _) => Task.FromResult(new MinecraftConnection(
            ProfileId, "Player", "refreshed", DateTimeOffset.UtcNow.AddHours(2))));
        var app = new AppCoordinator(new UserSettingsService(new MemorySettingsStore()), new MemoryAccountStore(account), auth);
        await app.InitializeAsync();
        return (app, auth);
    }

    private static byte[] Png(byte marker, int height = 64)
    {
        var bytes = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 64);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), (uint)height);
        bytes[32] = marker;
        return bytes;
    }

    private sealed class UploadFile(byte[] bytes) : IDisposable
    {
        public string Path { get; } = Create(bytes);
        private static string Create(byte[] data)
        {
            var path = System.IO.Path.GetTempFileName();
            File.WriteAllBytes(path, data);
            return path;
        }
        public void Dispose() => File.Delete(Path);
    }

    private sealed class FakeDecoder : IMinecraftAppearanceDecoder
    {
        public Task<NormalizedMinecraftSkin> DecodeSkinAsync(byte[] png, MinecraftSkinVariant variant, CancellationToken ct) =>
            Task.FromResult(new NormalizedMinecraftSkin(Enumerable.Repeat(png[32], 64 * 64 * 4).ToArray(), variant));
        public Task<byte[]> DecodeCapeAsync(byte[] png, CancellationToken ct) => Task.FromResult(new byte[64 * 32 * 4]);
    }

    private sealed class FakeApi : IMinecraftProfileApi
    {
        public byte[] Skin = Png(1);
        public string? ActiveCape;
        public string Variant = "CLASSIC";
        public bool FailCape, LoseUploadResponse, IgnoreUpload, NoCapes;
        public int Uploads, ProfileReads, UnauthorizedReads;
        public TaskCompletionSource? ReadGate;
        public TaskCompletionSource ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MinecraftAppearanceProfile> ProfileAsync(string token, CancellationToken ct)
        {
            ProfileReads++;
            ReadStarted.TrySetResult();
            if (ReadGate is not null) await ReadGate.Task;
            if (UnauthorizedReads-- > 0) throw new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized);
            return new(ProfileId, "Player", [new("skin", "ACTIVE", "skin", Variant)],
                NoCapes ? [] : [new("cape", ActiveCape == "cape" ? "ACTIVE" : "INACTIVE", "cape", "Test cape")]);
        }
        public Task UploadSkinAsync(string token, byte[] png, MinecraftSkinVariant variant, CancellationToken ct)
        {
            Uploads++;
            if (!IgnoreUpload) { Skin = png; Variant = variant == MinecraftSkinVariant.Slim ? "SLIM" : "CLASSIC"; }
            if (LoseUploadResponse) throw new HttpRequestException("lost response");
            return Task.CompletedTask;
        }
        public Task SetCapeAsync(string token, string? cape, CancellationToken ct)
        {
            if (FailCape) throw new HttpRequestException("cape failed");
            ActiveCape = cape;
            return Task.CompletedTask;
        }
        public Task<byte[]> TextureAsync(string url, CancellationToken ct) => Task.FromResult(url == "skin" ? Skin : Png(3));
    }
}

using System.Net;
using System.Text;
using Hako.Core;

namespace Hako.Tests;

[TestClass]
public sealed class MinecraftAppearanceCacheTests
{
    [TestMethod]
    public async Task ConfirmedAppearanceWinsOverAnOlderInFlightPublicProfileLoad()
    {
        const string id = "00112233445566778899aabbccddeeff";
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var png = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "WindowsSteve.png"));
        using var http = new HttpClient(new CallbackHttpHandler(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "sessionserver.mojang.com")
            {
                reached.SetResult();
                await release.Task.WaitAsync(ct);
                var textures = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    "{\"textures\":{\"SKIN\":{\"url\":\"https://textures.minecraft.net/texture/old\"}}}"));
                return TestData.Json(HttpStatusCode.OK, "{\"properties\":[{\"name\":\"textures\",\"value\":\"" + textures + "\"}]}");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) };
        }));
        using var service = new MinecraftSkinPreviewService(http);
        var pending = service.LoadAsync(id, Guid.NewGuid());
        await reached.Task;
        var newSkin = new byte[64 * 64 * 4];
        newSkin[0] = 123;
        service.ConfirmAppearance(id, new(newSkin, MinecraftSkinVariant.Slim, null));
        release.SetResult();
        var loaded = await pending;
        Assert.AreSame(newSkin, loaded.SkinRgba);
        Assert.IsNull(loaded.CapeRgba, "Known absence must not be replaced by sessionserver data.");
        Assert.AreSame(loaded, await service.LoadAsync(id, Guid.NewGuid()));
    }
}

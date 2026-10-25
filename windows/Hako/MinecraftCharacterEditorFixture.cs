using Hako.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Hako;

/// <summary>Only used by the isolated --profile-preview fixture mode. Never sends HTTP requests.</summary>
internal sealed class MinecraftCharacterEditorFixture : IMinecraftProfileApi
{
    internal const string ProfileId = "00112233445566778899aabbccddeeff";
    private byte[] _skin = [];
    private byte[] _cape = [];
    private string _variant = "CLASSIC";
    private string? _activeCape = "fixture-cape";

    public static async Task<MinecraftCharacterEditorFixture> CreateAsync()
    {
        var fixture = new MinecraftCharacterEditorFixture
        {
            _skin = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "Steve.png"))
        };
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        var pixels = new byte[64 * 32 * 4];
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 64; x++)
        {
            var offset = (y * 64 + x) * 4;
            pixels[offset] = x < 11 ? (byte)232 : (byte)48;
            pixels[offset + 1] = (byte)(y % 4 < 2 ? 105 : 175);
            pixels[offset + 2] = x < 11 ? (byte)145 : (byte)225;
            pixels[offset + 3] = 255;
        }
        encoder.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Straight, 64, 32, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        using var memory = new MemoryStream();
        await stream.AsStreamForRead().CopyToAsync(memory);
        fixture._cape = memory.ToArray();
        return fixture;
    }

    public Task<MinecraftAppearanceProfile> ProfileAsync(string token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new MinecraftAppearanceProfile(ProfileId, "PreviewPlayer",
            [new("fixture-skin", "ACTIVE", "skin", _variant)],
            [new("fixture-cape", _activeCape is null ? "INACTIVE" : "ACTIVE", "cape", "Hako Preview")]));
    }
    public Task UploadSkinAsync(string token, byte[] png, MinecraftSkinVariant variant, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _skin = png;
        _variant = variant == MinecraftSkinVariant.Slim ? "SLIM" : "CLASSIC";
        return Task.CompletedTask;
    }
    public Task SetCapeAsync(string token, string? capeId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _activeCape = capeId;
        return Task.CompletedTask;
    }
    public Task<byte[]> TextureAsync(string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(url == "skin" ? _skin : _cape);
    }
}

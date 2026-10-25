using Hako.Core;

namespace Hako;

internal sealed class MinecraftAppearanceDecoder : IMinecraftAppearanceDecoder
{
    public async Task<NormalizedMinecraftSkin> DefaultSkinAsync(CancellationToken cancellationToken)
    {
        var png = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "Steve.png"), cancellationToken);
        return await DecodeSkinAsync(png, MinecraftSkinVariant.Classic, cancellationToken);
    }
    public Task<NormalizedMinecraftSkin> DecodeSkinAsync(byte[] png, MinecraftSkinVariant variant, CancellationToken cancellationToken) =>
        MinecraftSkinImageDecoder.DecodeSkinAsync(png, variant, cancellationToken);
    public Task<byte[]> DecodeCapeAsync(byte[] png, CancellationToken cancellationToken) =>
        MinecraftSkinImageDecoder.DecodeCapeAsync(png, cancellationToken);
}

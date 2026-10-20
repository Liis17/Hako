using Hako.Core;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace Hako;

internal static class MinecraftSkinImageDecoder
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static Task<NormalizedMinecraftSkin> DecodeSkinAsync(
        byte[] png,
        MinecraftSkinVariant variant,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        var (rgba, width, height) = await DecodeRgbaAsync(png, allowLegacySkin: true, cancellationToken: cancellationToken);
        return MinecraftSkinNormalizer.NormalizeRgba(rgba, width, height, variant);
    }, cancellationToken);

    public static Task<byte[]> DecodeCapeAsync(
        byte[] png,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        var (rgba, width, height) = await DecodeRgbaAsync(png, allowLegacySkin: false, cancellationToken: cancellationToken);
        return MinecraftSkinNormalizer.NormalizeCapeRgba(rgba, width, height);
    }, cancellationToken);

    private static async Task<(byte[] Rgba, int Width, int Height)> DecodeRgbaAsync(
        byte[] png,
        bool allowLegacySkin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(png);
        cancellationToken.ThrowIfCancellationRequested();
        if (png.Length < PngSignature.Length || !png.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
        {
            throw new InvalidDataException("The Minecraft texture is not a PNG image.");
        }

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(CryptographicBuffer.CreateFromByteArray(png));
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        var width = checked((int)decoder.PixelWidth);
        var height = checked((int)decoder.PixelHeight);
        var dimensionsAreValid = width == MinecraftSkinNormalizer.Width
            && (allowLegacySkin
                ? height is MinecraftSkinNormalizer.Height or 32
                : height == MinecraftSkinNormalizer.CapeHeight);
        if (!dimensionsAreValid)
        {
            throw new InvalidDataException(allowLegacySkin
                ? "A Minecraft skin must be 64×64 or 64×32 pixels."
                : "A Minecraft cape must be 64×32 pixels.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Straight,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        cancellationToken.ThrowIfCancellationRequested();
        var rgba = pixelData.DetachPixelData();
        if (rgba.Length != checked(width * height * 4))
        {
            throw new InvalidDataException("The decoded Minecraft texture has an invalid pixel buffer.");
        }

        return (rgba, width, height);
    }
}

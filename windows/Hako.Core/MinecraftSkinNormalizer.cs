namespace Hako.Core;

public enum MinecraftSkinVariant
{
    Classic,
    Slim
}

public sealed record NormalizedMinecraftSkin(byte[] Rgba, MinecraftSkinVariant Variant);

public static class MinecraftSkinNormalizer
{
    public const int Width = 64;
    public const int Height = 64;
    public const int CapeWidth = 64;
    public const int CapeHeight = 32;

    public static NormalizedMinecraftSkin NormalizeRgba(
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        MinecraftSkinVariant variant)
    {
        if (width != Width || height is not (Height or 32))
        {
            throw new ArgumentException("A Minecraft skin must be 64×64 or 64×32 RGBA.");
        }

        if (rgba.Length != checked(width * height * 4))
        {
            throw new ArgumentException("The RGBA data length does not match the texture dimensions.", nameof(rgba));
        }

        var normalized = new byte[Width * Height * 4];
        for (var y = 0; y < height; y++)
        {
            rgba.Slice(y * width * 4, width * 4).CopyTo(normalized.AsSpan(y * Width * 4, Width * 4));
        }

        var legacy = height == 32;
        if (legacy)
        {
            MirrorLegacyLimbs(normalized);
            if (!HasTransparentOverlay(normalized))
            {
                for (var y = 0; y < 16; y++)
                {
                    for (var x = 32; x < 64; x++)
                    {
                        normalized[(y * Width + x) * 4 + 3] = 0;
                    }
                }
            }
        }

        ForceOpaqueBaseLayer(normalized);
        return new(normalized, legacy ? MinecraftSkinVariant.Classic : variant);
    }

    public static byte[] NormalizeCapeRgba(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width != CapeWidth || height != CapeHeight || rgba.Length != CapeWidth * CapeHeight * 4)
        {
            throw new ArgumentException("A Minecraft cape must be 64×32 RGBA.", nameof(rgba));
        }

        return rgba.ToArray();
    }

    private static void MirrorLegacyLimbs(byte[] pixels)
    {
        ReadOnlySpan<(int SourceX, int DestinationX, int SourceY, int DestinationY, int Height)> regions =
        [
            (4, 20, 16, 48, 4), (8, 24, 16, 48, 4),
            (0, 24, 20, 52, 12), (4, 20, 20, 52, 12),
            (8, 16, 20, 52, 12), (12, 28, 20, 52, 12),
            (44, 36, 16, 48, 4), (48, 40, 16, 48, 4),
            (40, 40, 20, 52, 12), (44, 36, 20, 52, 12),
            (48, 32, 20, 52, 12), (52, 44, 20, 52, 12)
        ];

        foreach (var (sourceX, destinationX, sourceY, destinationY, height) in regions)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < 4; x++)
                {
                    var source = ((sourceY + y) * Width + sourceX + 3 - x) * 4;
                    var destination = ((destinationY + y) * Width + destinationX + x) * 4;
                    Array.Copy(pixels, source, pixels, destination, 4);
                }
            }
        }
    }

    private static bool HasTransparentOverlay(byte[] pixels)
    {
        for (var y = 0; y < 32; y++)
        {
            for (var x = 32; x < 64; x++)
            {
                if (pixels[(y * Width + x) * 4 + 3] < 128)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void ForceOpaqueBaseLayer(byte[] pixels)
    {
        SetAlpha(pixels, 0, 32, 0, 16);
        SetAlpha(pixels, 0, 64, 16, 32);
        SetAlpha(pixels, 16, 48, 48, 64);
    }

    private static void SetAlpha(byte[] pixels, int left, int right, int top, int bottom)
    {
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                pixels[(y * Width + x) * 4 + 3] = byte.MaxValue;
            }
        }
    }
}

namespace Hako;

internal static class MinecraftSkinHeadComposer
{
    private const int TextureSize = 64;
    private const int FaceX = 8;
    private const int FaceY = 8;
    private const int HatX = 40;
    private const int HeadTextureSize = 8;
    private const int PixelScale = 8;

    public static byte[] Compose(ReadOnlySpan<byte> normalizedSkinRgba)
    {
        if (normalizedSkinRgba.Length != TextureSize * TextureSize * 4)
        {
            throw new ArgumentException("A normalized Minecraft skin must be 64×64 RGBA.", nameof(normalizedSkinRgba));
        }

        var head = new byte[TextureSize * TextureSize * 4];
        for (var y = 0; y < HeadTextureSize; y++)
        {
            for (var x = 0; x < HeadTextureSize; x++)
            {
                var face = ((FaceY + y) * TextureSize + FaceX + x) * 4;
                var hat = ((FaceY + y) * TextureSize + HatX + x) * 4;
                var pixel = Composite(
                    normalizedSkinRgba[face], normalizedSkinRgba[face + 1], normalizedSkinRgba[face + 2], normalizedSkinRgba[face + 3],
                    normalizedSkinRgba[hat], normalizedSkinRgba[hat + 1], normalizedSkinRgba[hat + 2], normalizedSkinRgba[hat + 3]);

                for (var expandedY = 0; expandedY < PixelScale; expandedY++)
                {
                    for (var expandedX = 0; expandedX < PixelScale; expandedX++)
                    {
                        var destination = ((y * PixelScale + expandedY) * TextureSize + x * PixelScale + expandedX) * 4;
                        head[destination] = pixel.R;
                        head[destination + 1] = pixel.G;
                        head[destination + 2] = pixel.B;
                        head[destination + 3] = pixel.A;
                    }
                }
            }
        }

        return head;
    }

    private static (byte R, byte G, byte B, byte A) Composite(
        byte backgroundR,
        byte backgroundG,
        byte backgroundB,
        byte backgroundA,
        byte foregroundR,
        byte foregroundG,
        byte foregroundB,
        byte foregroundA)
    {
        var inverseForegroundAlpha = byte.MaxValue - foregroundA;
        var alphaNumerator = foregroundA * byte.MaxValue + backgroundA * inverseForegroundAlpha;
        if (alphaNumerator == 0)
        {
            return (0, 0, 0, 0);
        }

        static byte Channel(byte foreground, byte foregroundAlpha, byte background, byte backgroundAlpha, int inverseAlpha, int alpha)
        {
            var numerator = foreground * foregroundAlpha * byte.MaxValue
                + background * backgroundAlpha * inverseAlpha;
            return (byte)((numerator + alpha / 2) / alpha);
        }

        var alpha = (byte)((alphaNumerator + byte.MaxValue / 2) / byte.MaxValue);
        return (
            Channel(foregroundR, foregroundA, backgroundR, backgroundA, inverseForegroundAlpha, alphaNumerator),
            Channel(foregroundG, foregroundA, backgroundG, backgroundA, inverseForegroundAlpha, alphaNumerator),
            Channel(foregroundB, foregroundA, backgroundB, backgroundA, inverseForegroundAlpha, alphaNumerator),
            alpha);
    }
}

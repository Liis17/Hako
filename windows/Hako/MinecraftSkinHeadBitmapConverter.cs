namespace Hako;

internal static class MinecraftSkinHeadBitmapConverter
{
    internal static byte[] ConvertStraightRgbaToPremultipliedBgra(ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length % 4 != 0)
        {
            throw new ArgumentException("RGBA pixel data must contain complete pixels.", nameof(rgba));
        }

        var bgra = new byte[rgba.Length];
        for (var offset = 0; offset < rgba.Length; offset += 4)
        {
            var alpha = rgba[offset + 3];
            bgra[offset] = Premultiply(rgba[offset + 2], alpha);
            bgra[offset + 1] = Premultiply(rgba[offset + 1], alpha);
            bgra[offset + 2] = Premultiply(rgba[offset], alpha);
            bgra[offset + 3] = alpha;
        }

        return bgra;
    }

    private static byte Premultiply(byte channel, byte alpha) =>
        (byte)((channel * alpha + byte.MaxValue / 2) / byte.MaxValue);
}

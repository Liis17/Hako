using Hako.Core;
using System.IO.Compression;
using System.Text;

namespace Hako.Tests;

[TestClass]
public sealed class MinecraftSkinImageDecoderTests
{
    [TestMethod]
    public async Task DecodeSkinAsync_DecodesBundledSteve()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "WindowsSteve.png");

        var result = await MinecraftSkinImageDecoder.DecodeSkinAsync(
            await File.ReadAllBytesAsync(path),
            MinecraftSkinVariant.Classic);

        Assert.AreEqual(MinecraftSkinVariant.Classic, result.Variant);
        Assert.AreEqual(64 * 64 * 4, result.Rgba.Length);
    }

    [TestMethod]
    public async Task DecodeSkinAsync_RejectsWrongPngDimensions()
    {
        var png = CreatePng(32, 32, new byte[32 * 32 * 4]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => MinecraftSkinImageDecoder.DecodeSkinAsync(png, MinecraftSkinVariant.Classic));
    }

    [TestMethod]
    public async Task DecodeSkinAsync_RejectsCorruptPngContainer()
    {
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82];
        var rejected = false;

        try
        {
            await MinecraftSkinImageDecoder.DecodeSkinAsync(png, MinecraftSkinVariant.Classic);
        }
        catch (Exception)
        {
            rejected = true;
        }

        Assert.IsTrue(rejected, "A truncated PNG container must be rejected.");
    }

    [TestMethod]
    public async Task DecodeSkinAsync_PreservesStraightAlphaInOverlayPixels()
    {
        var rgba = new byte[64 * 64 * 4];
        var offset = (8 * 64 + 40) * 4;
        rgba[offset] = 201;
        rgba[offset + 1] = 123;
        rgba[offset + 2] = 45;
        rgba[offset + 3] = 128;
        var png = CreatePng(64, 64, rgba);

        var result = await MinecraftSkinImageDecoder.DecodeSkinAsync(png, MinecraftSkinVariant.Classic);

        CollectionAssert.AreEqual(new byte[] { 201, 123, 45, 128 }, result.Rgba.AsSpan(offset, 4).ToArray());
    }

    [TestMethod]
    public async Task DecodeCapeAsync_DecodesRgbaPixels()
    {
        var rgba = new byte[64 * 32 * 4];
        var offset = (12 * 64 + 17) * 4;
        rgba[offset] = 210;
        rgba[offset + 1] = 80;
        rgba[offset + 2] = 35;
        rgba[offset + 3] = 96;
        var png = CreatePng(64, 32, rgba);

        var result = await MinecraftSkinImageDecoder.DecodeCapeAsync(png);

        CollectionAssert.AreEqual(rgba, result);
    }

    [TestMethod]
    public async Task DecodeCapeAsync_RejectsWrongPngDimensions()
    {
        var png = CreatePng(64, 64, new byte[64 * 64 * 4]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => MinecraftSkinImageDecoder.DecodeCapeAsync(png));
    }

    [TestMethod]
    public async Task DecodeCapeThumbnailAsync_ExtractsUprightFrontFaceAndPreservesAlpha()
    {
        var rgba = new byte[64 * 32 * 4];
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 64; x++)
            SetPixel(rgba, x, y, (byte)(x * 4), (byte)(y * 8), (byte)(x * 13 + y * 7), (byte)(x * 11 + y * 17));
        var png = CreatePng(64, 32, rgba);

        var result = await MinecraftSkinImageDecoder.DecodeCapeThumbnailAsync(png);

        Assert.AreEqual(10 * 16 * 4, result.Length);
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 10; x++)
        {
            var expected = rgba.AsSpan(((y + 1) * 64 + x + 1) * 4, 4).ToArray();
            var actual = result.AsSpan((y * 10 + x) * 4, 4).ToArray();
            CollectionAssert.AreEqual(expected, actual, $"Unexpected crop pixel at ({x}, {y}).");
        }
    }

    [TestMethod]
    public async Task DecodeCapeThumbnailAsync_RejectsWrongPngDimensions()
    {
        var png = CreatePng(64, 64, new byte[64 * 64 * 4]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => MinecraftSkinImageDecoder.DecodeCapeThumbnailAsync(png));
    }

    [TestMethod]
    public async Task DecodeCapeThumbnailAsync_RejectsCorruptPngContainer()
    {
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82];
        var rejected = false;

        try
        {
            await MinecraftSkinImageDecoder.DecodeCapeThumbnailAsync(png);
        }
        catch (Exception)
        {
            rejected = true;
        }

        Assert.IsTrue(rejected, "A truncated PNG container must be rejected.");
    }

    [TestMethod]
    public void HeadComposer_ExpandsNativePixelsAndCompositesHatWithStraightAlpha()
    {
        var rgba = new byte[64 * 64 * 4];
        SetPixel(rgba, 8, 8, 10, 20, 30, 255);
        SetPixel(rgba, 9, 8, 20, 20, 30, 255);
        SetPixel(rgba, 40, 8, 200, 0, 0, 128);
        var skin = MinecraftSkinNormalizer.NormalizeRgba(rgba, 64, 64, MinecraftSkinVariant.Classic);

        var head = MinecraftSkinHeadComposer.Compose(skin.Rgba);

        Assert.AreEqual(64 * 64 * 4, head.Length);
        CollectionAssert.AreEqual(new byte[] { 105, 10, 15, 255 }, head.AsSpan(0, 4).ToArray());
        CollectionAssert.AreEqual(new byte[] { 105, 10, 15, 255 }, head.AsSpan((7 * 64 + 7) * 4, 4).ToArray());
        CollectionAssert.AreEqual(new byte[] { 20, 20, 30, 255 }, head.AsSpan(8 * 4, 4).ToArray());
    }

    [TestMethod]
    public void HeadBitmapConverter_ProducesPremultipliedBgraFromStraightRgba()
    {
        byte[] rgba = [200, 100, 50, 128, 9, 201, 240, 255, 128, 64, 32, 0];

        var bgra = MinecraftSkinHeadBitmapConverter.ConvertStraightRgbaToPremultipliedBgra(rgba);

        CollectionAssert.AreEqual(new byte[] { 25, 50, 100, 128, 240, 201, 9, 255, 0, 0, 0, 0 }, bgra);
    }

    private static void SetPixel(byte[] rgba, int x, int y, byte red, byte green, byte blue, byte alpha)
    {
        var offset = (y * 64 + x) * 4;
        rgba[offset] = red;
        rgba[offset + 1] = green;
        rgba[offset + 2] = blue;
        rgba[offset + 3] = alpha;
    }

    private static byte[] CreatePng(int width, int height, byte[] rgba)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            raw.Write(rgba, y * width * 4, width * 4);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string name, byte[] data)
    {
        var type = Encoding.ASCII.GetBytes(name);
        WriteBigEndian(stream, (uint)data.Length);
        stream.Write(type);
        stream.Write(data);
        WriteBigEndian(stream, ComputeCrc(type, data));
    }

    private static uint ComputeCrc(byte[] type, byte[] data)
    {
        var crc = uint.MaxValue;
        foreach (var value in type.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0 ? crc >> 1 : 0xedb88320 ^ (crc >> 1);
            }
        }

        return ~crc;
    }

    private static void WriteBigEndian(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}

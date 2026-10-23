using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Hako.Tests;

[TestClass]
public sealed class InstanceIconImageTests
{
    [TestMethod]
    public async Task PngAndJpegBecomeSquare256PixelPng()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Root);
        foreach (var (encoder, name) in new[] { (BitmapEncoder.PngEncoderId, "wide.png"), (BitmapEncoder.JpegEncoderId, "tall.jpg") })
        {
            var path = Path.Combine(temporary.Root, name);
            var (width, height) = name == "wide.png" ? (600u, 300u) : (200u, 500u);
            await File.WriteAllBytesAsync(path, await EncodeAsync(encoder, width, height));

            var icon = await Hako.InstanceIconImage.NormalizeAsync(path);

            var decoder = await DecodeAsync(icon);
            Assert.AreEqual(BitmapDecoder.PngDecoderId, decoder.DecoderInformation.CodecId, name);
            Assert.AreEqual(256u, decoder.PixelWidth, name);
            Assert.AreEqual(256u, decoder.PixelHeight, name);
        }
    }

    [TestMethod]
    public async Task OtherFormatsAndDamagedFilesAreRejected()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Root);
        var bmp = Path.Combine(temporary.Root, "icon.bmp");
        await File.WriteAllBytesAsync(bmp, await EncodeAsync(BitmapEncoder.BmpEncoderId, 64, 64));
        var garbage = Path.Combine(temporary.Root, "icon.png");
        await File.WriteAllBytesAsync(garbage, [1, 2, 3, 4, 5]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Hako.InstanceIconImage.NormalizeAsync(bmp));
        await Assert.ThrowsAsync<Exception>(() => Hako.InstanceIconImage.NormalizeAsync(garbage));
    }

    private static async Task<byte[]> EncodeAsync(Guid encoderId, uint width, uint height)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 40;
            pixels[index + 1] = 96;
            pixels[index + 2] = 224;
            pixels[index + 3] = 255;
        }

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
        var result = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(result.AsBuffer(), (uint)result.Length, InputStreamOptions.None);
        return result;
    }

    private static async Task<BitmapDecoder> DecodeAsync(byte[] data)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(data.AsBuffer());
        stream.Seek(0);
        return await BitmapDecoder.CreateAsync(stream);
    }
}

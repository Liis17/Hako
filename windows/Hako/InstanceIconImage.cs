using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Hako;

/// <summary>Turns a PNG or JPEG into the 256×256 PNG stored as a build's icon.png, cropped to a centered square as on macOS.</summary>
internal static class InstanceIconImage
{
    private const int Side = 256;
    private const long MaximumSourceBytes = 64L * 1024 * 1024;

    public static async Task<byte[]> NormalizeAsync(string path)
    {
        var source = await Task.Run(() =>
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaximumSourceBytes)
            {
                throw new InvalidDataException("The picture is empty or too large.");
            }

            return File.ReadAllBytes(path);
        });

        return await NormalizeAsync(source);
    }

    public static async Task<byte[]> NormalizeAsync(byte[] source)
    {
        if (source.Length == 0 || source.LongLength > MaximumSourceBytes)
        {
            throw new InvalidDataException("The picture is empty or too large.");
        }

        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(source.AsBuffer());
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        if (decoder.DecoderInformation.CodecId != BitmapDecoder.PngDecoderId
            && decoder.DecoderInformation.CodecId != BitmapDecoder.JpegDecoderId)
        {
            throw new InvalidDataException("Only PNG and JPEG pictures are supported.");
        }

        var width = decoder.OrientedPixelWidth;
        var height = decoder.OrientedPixelHeight;
        if (width == 0 || height == 0)
        {
            throw new InvalidDataException("The picture has no pixels.");
        }

        // Scale the shorter side to 256 and crop the centre, like CGImage cropping on macOS.
        var scale = (double)Side / Math.Min(width, height);
        var scaledWidth = Math.Max(Side, (uint)Math.Round(width * scale));
        var scaledHeight = Math.Max(Side, (uint)Math.Round(height * scale));
        var transform = new BitmapTransform
        {
            ScaledWidth = scaledWidth,
            ScaledHeight = scaledHeight,
            InterpolationMode = BitmapInterpolationMode.Fant,
            Bounds = new BitmapBounds
            {
                X = (scaledWidth - Side) / 2,
                Y = (scaledHeight - Side) / 2,
                Width = Side,
                Height = Side
            }
        };
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, Side, Side, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync();

        var result = new byte[output.Size];
        output.Seek(0);
        await output.ReadAsync(result.AsBuffer(), (uint)result.Length, InputStreamOptions.None);
        return result;
    }
}

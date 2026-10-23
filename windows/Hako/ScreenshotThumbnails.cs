using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Hako;

/// <summary>Decodes screenshots to bounded WinRT thumbnails and keeps a small per-view LRU cache.</summary>
internal sealed class ScreenshotThumbnails
{
    private const int MaximumPixelSize = 512;
    private const int MaximumCacheItems = 48;
    private readonly SemaphoreSlim _decoders = new(3, 3);
    private readonly Dictionary<string, BitmapImage> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _recent = [];

    internal async Task<BitmapImage?> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await _decoders.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            var fileState = await Task.Run(() =>
            {
                var info = new FileInfo(fullPath);
                return (info.Length, info.LastWriteTimeUtc.Ticks);
            }, cancellationToken);
            var key = $"{fullPath}|{fileState.Length}|{fileState.Ticks}";
            if (_cache.TryGetValue(key, out var cached))
            {
                Touch(key);
                return cached;
            }

            var file = await StorageFile.GetFileFromPathAsync(fullPath).AsTask(cancellationToken);
            using var input = await file.OpenAsync(FileAccessMode.Read).AsTask(cancellationToken);
            var decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken);
            var width = decoder.OrientedPixelWidth;
            var height = decoder.OrientedPixelHeight;
            if (width == 0 || height == 0) return null;

            var scale = Math.Min(1d, MaximumPixelSize / (double)Math.Max(width, height));
            var scaledWidth = Math.Max(1U, (uint)Math.Round(width * scale));
            var scaledHeight = Math.Max(1U, (uint)Math.Round(height * scale));
            var transform = new BitmapTransform
            {
                ScaledWidth = scaledWidth,
                ScaledHeight = scaledHeight
            };

            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask(cancellationToken);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync().AsTask(cancellationToken);
            output.Seek(0);

            var thumbnail = new BitmapImage
            {
                DecodePixelWidth = checked((int)scaledWidth),
                DecodePixelHeight = checked((int)scaledHeight)
            };
            await thumbnail.SetSourceAsync(output).AsTask(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Add(key, thumbnail);
            return thumbnail;
        }
        finally
        {
            _decoders.Release();
        }
    }

    private void Add(string key, BitmapImage image)
    {
        _cache[key] = image;
        Touch(key);
        while (_cache.Count > MaximumCacheItems)
        {
            var oldest = _recent.Last;
            if (oldest is null) break;
            _cache.Remove(oldest.Value);
            _recent.RemoveLast();
        }
    }

    private void Touch(string key)
    {
        var existing = _recent.Find(key);
        if (existing is not null) _recent.Remove(existing);
        _recent.AddFirst(key);
    }
}

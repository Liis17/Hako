using Hako.Core;
using System.Collections.Generic;

namespace Hako;

internal sealed record MinecraftSkinPreviewData(
    byte[] SkinRgba,
    MinecraftSkinVariant Variant,
    byte[]? CapeRgba,
    byte[] HeadRgba);

internal sealed class MinecraftSkinPreviewService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly MinecraftSkinLoader _loader;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly Dictionary<string, CachedSkin> _skins = new(StringComparer.Ordinal);
    private MinecraftSkinPreviewData? _steve;
    private bool _disposed;

    public MinecraftSkinPreviewService()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _loader = new MinecraftSkinLoader(_httpClient);
    }

    public async Task<MinecraftSkinPreviewData> LoadAsync(
        string? uuid,
        Guid requestScope,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return await LoadSteveAsync(cancellationToken);
        }

        if (!Guid.TryParseExact(uuid, "N", out var parsedUuid) && !Guid.TryParseExact(uuid, "D", out parsedUuid))
        {
            throw new ArgumentException("The Minecraft profile ID must be a UUID.", nameof(uuid));
        }

        var cacheKey = parsedUuid.ToString("N");
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (_skins.TryGetValue(cacheKey, out var cached))
            {
                if (!cached.CapeExpected || cached.Data.CapeRgba is not null || cached.LastCapeAttemptScope == requestScope)
                {
                    return cached.Data;
                }

                return await RetryCapeAsync(cacheKey, cached, requestScope, cancellationToken);
            }

            var skin = await _loader.LoadAsync(cacheKey, cancellationToken);
            ThrowIfDisposed();
            if (skin is null)
            {
                return await LoadSteveAsync(cancellationToken);
            }

            var normalized = await MinecraftSkinImageDecoder.DecodeSkinAsync(skin.SkinPng, skin.Variant, cancellationToken);
            ThrowIfDisposed();
            var cape = await TryDecodeCapeAsync(skin.CapePng, cancellationToken);
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var data = CreatePreviewData(normalized.Rgba, normalized.Variant, cape);
            _skins.Add(cacheKey, new(data, skin.HasCape, skin.HasCape && cape is null ? requestScope : null));
            return data;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public async Task<MinecraftSkinPreviewData> LoadFixtureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var steve = await LoadSteveAsync(cancellationToken);
        return steve with { CapeRgba = CreateFixtureCape() };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
        _skins.Clear();
        _steve = null;
    }

    private async Task<MinecraftSkinPreviewData> RetryCapeAsync(
        string cacheKey,
        CachedSkin cached,
        Guid requestScope,
        CancellationToken cancellationToken)
    {
        try
        {
            var skin = await _loader.LoadAsync(cacheKey, cancellationToken);
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (skin is null || !skin.HasCape)
            {
                _skins[cacheKey] = cached with { CapeExpected = false, LastCapeAttemptScope = requestScope };
                return cached.Data;
            }

            var cape = await TryDecodeCapeAsync(skin.CapePng, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (cape is not null)
            {
                var updated = cached.Data with { CapeRgba = cape };
                _skins[cacheKey] = cached with { Data = updated, CapeExpected = true, LastCapeAttemptScope = requestScope };
                return updated;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (Exception)
        {
            // A cape is optional; keep the last successfully decoded skin and allow a later retry.
        }

        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        _skins[cacheKey] = cached with { LastCapeAttemptScope = requestScope };
        return cached.Data;
    }

    private static async Task<byte[]?> TryDecodeCapeAsync(byte[]? capePng, CancellationToken cancellationToken)
    {
        if (capePng is null)
        {
            return null;
        }

        try
        {
            return await MinecraftSkinImageDecoder.DecodeCapeAsync(capePng, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<MinecraftSkinPreviewData> LoadSteveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_steve is not null)
        {
            return _steve;
        }

        ThrowIfDisposed();
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Steve.png");
        var png = await File.ReadAllBytesAsync(path, cancellationToken);
        var normalized = await MinecraftSkinImageDecoder.DecodeSkinAsync(png, MinecraftSkinVariant.Classic, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        _steve = CreatePreviewData(normalized.Rgba, normalized.Variant, null);
        return _steve;
    }

    private static MinecraftSkinPreviewData CreatePreviewData(
        byte[] skinRgba,
        MinecraftSkinVariant variant,
        byte[]? capeRgba) => new(skinRgba, variant, capeRgba, MinecraftSkinHeadComposer.Compose(skinRgba));

    private static byte[] CreateFixtureCape()
    {
        var pixels = new byte[MinecraftSkinNormalizer.CapeWidth * MinecraftSkinNormalizer.CapeHeight * 4];
        for (var y = 0; y < MinecraftSkinNormalizer.CapeHeight; y++)
        {
            for (var x = 0; x < MinecraftSkinNormalizer.CapeWidth; x++)
            {
                var offset = (y * MinecraftSkinNormalizer.CapeWidth + x) * 4;
                var stripe = ((x / 8) + (y / 8)) % 2 == 0;
                pixels[offset] = stripe ? (byte)224 : (byte)62;
                pixels[offset + 1] = stripe ? (byte)96 : (byte)132;
                pixels[offset + 2] = stripe ? (byte)124 : (byte)201;
                pixels[offset + 3] = byte.MaxValue;
            }
        }

        return MinecraftSkinNormalizer.NormalizeCapeRgba(
            pixels,
            MinecraftSkinNormalizer.CapeWidth,
            MinecraftSkinNormalizer.CapeHeight);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record CachedSkin(MinecraftSkinPreviewData Data, bool CapeExpected, Guid? LastCapeAttemptScope);
}

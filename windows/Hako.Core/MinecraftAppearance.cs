using System.Buffers.Binary;

namespace Hako.Core;

public sealed record MinecraftAppearanceSkin(string Id, string State, string Url, string Variant);
public sealed record MinecraftAppearanceCape(string Id, string State, string Url, string Alias);
public sealed record MinecraftAppearanceProfile(string Id, string Name,
    MinecraftAppearanceSkin[] Skins, MinecraftAppearanceCape[] Capes)
{
    public MinecraftAppearanceSkin? ActiveSkin => Skins.FirstOrDefault(s => s.State == "ACTIVE");
    public MinecraftAppearanceCape? ActiveCape => Capes.FirstOrDefault(c => c.State == "ACTIVE");
}

public sealed record MinecraftAppearanceImage(byte[] SkinRgba, MinecraftSkinVariant Variant, byte[]? CapeRgba);

public interface IMinecraftAppearanceDecoder
{
    Task<NormalizedMinecraftSkin> DefaultSkinAsync(CancellationToken cancellationToken);
    Task<NormalizedMinecraftSkin> DecodeSkinAsync(byte[] png, MinecraftSkinVariant variant, CancellationToken cancellationToken);
    Task<byte[]> DecodeCapeAsync(byte[] png, CancellationToken cancellationToken);
}

public sealed record MinecraftSkinUpload(byte[] Png, bool IsLegacy)
{
    public const int MaximumBytes = 4 * 1024 * 1024;

    public static MinecraftSkinUpload Validate(byte[] png)
    {
        if (png.Length > MaximumBytes || png.Length < 33
            || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8, 4)) != 13
            || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8)
            || BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)) != 64)
            throw new InvalidDataException("CharacterInvalidPng");
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        if (height is not (64 or 32)) throw new InvalidDataException("CharacterInvalidPng");
        return new(png.ToArray(), height == 32);
    }

    public static async Task<MinecraftSkinUpload> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > MaximumBytes) throw new InvalidDataException("CharacterInvalidPng");
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaximumBytes + 1 - (int)output.Length)), cancellationToken);
            if (count == 0) break;
            output.Write(buffer, 0, count);
            if (output.Length > MaximumBytes) throw new InvalidDataException("CharacterInvalidPng");
        }
        return Validate(output.ToArray());
    }
}

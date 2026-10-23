using System.Buffers.Binary;
using System.IO.Compression;
namespace Hako.Tests;

internal sealed record WorldMetadataFixture(byte[] Compressed, byte[] Nbt, int? NameLengthOffset,
    int? NameValueOffset, int? NameByteLength);

internal static class WorldTestFixture
{
    public static WorldMetadataFixture Create(string? name = "World", string? version = "1.21.1",
        int? gameType = 0, long? lastPlayed = 0, byte[]? extra = null, bool includeName = true)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(10);
        WriteString(stream, "");
        stream.WriteByte(10);
        WriteString(stream, "Data");

        int? lengthOffset = null;
        int? valueOffset = null;
        int? nameLength = null;
        if (includeName && name is not null)
        {
            stream.WriteByte(8);
            WriteString(stream, "LevelName");
            lengthOffset = checked((int)stream.Position);
            var encoded = ModifiedUtf8(name);
            WriteUInt16(stream, checked((ushort)encoded.Length));
            valueOffset = checked((int)stream.Position);
            nameLength = encoded.Length;
            stream.Write(encoded);
        }

        if (gameType is { } mode)
        {
            stream.WriteByte(3);
            WriteString(stream, "GameType");
            WriteInt32(stream, mode);
        }

        if (lastPlayed is { } played)
        {
            stream.WriteByte(4);
            WriteString(stream, "LastPlayed");
            WriteInt64(stream, played);
        }

        if (version is not null)
        {
            stream.WriteByte(10);
            WriteString(stream, "Version");
            stream.WriteByte(8);
            WriteString(stream, "Name");
            WriteString(stream, version);
            stream.WriteByte(0);
        }

        if (extra is not null) stream.Write(extra);
        stream.WriteByte(0);
        stream.WriteByte(0);
        var nbt = stream.ToArray();
        return new WorldMetadataFixture(Gzip(nbt), nbt, lengthOffset, valueOffset, nameLength);
    }

    public static byte[] Gzip(byte[] input)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(input);
        }
        return output.ToArray();
    }

    public static byte[] Decompress(byte[] compressed)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    public static byte[] StringTag(string tagName, byte[] value)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(8);
        WriteString(stream, tagName);
        WriteUInt16(stream, checked((ushort)value.Length));
        stream.Write(value);
        return stream.ToArray();
    }

    public static byte[] ByteArrayTag(string tagName, byte[] value)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(7);
        WriteString(stream, tagName);
        WriteInt32(stream, value.Length);
        stream.Write(value);
        return stream.ToArray();
    }

    public static byte[] ModifiedUtf8(string value)
    {
        using var stream = new MemoryStream();
        foreach (var unit in value)
        {
            if (unit is > '\0' and <= '\u007f')
            {
                stream.WriteByte((byte)unit);
            }
            else if (unit <= '\u07ff')
            {
                stream.WriteByte((byte)(0xc0 | (unit >> 6)));
                stream.WriteByte((byte)(0x80 | (unit & 0x3f)));
            }
            else
            {
                stream.WriteByte((byte)(0xe0 | (unit >> 12)));
                stream.WriteByte((byte)(0x80 | ((unit >> 6) & 0x3f)));
                stream.WriteByte((byte)(0x80 | (unit & 0x3f)));
            }
        }
        return stream.ToArray();
    }

    public static void WriteString(Stream stream, string value)
    {
        var bytes = ModifiedUtf8(value);
        WriteUInt16(stream, checked((ushort)bytes.Length));
        stream.Write(bytes);
    }

    public static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    public static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    public static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }
}

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Hako.Core.Minecraft;

public enum WorldMetadataError
{
    InvalidGzip,
    InvalidNbt,
    InvalidText,
    CompressedDataTooLarge,
    DecompressedDataTooLarge,
    NestingTooDeep,
    ReadFailure
}

public sealed class WorldMetadataException(WorldMetadataError error, string message, Exception? innerException = null)
    : IOException(message, innerException)
{
    public WorldMetadataError Error { get; } = error;
}

public sealed record WorldMetadata(string? Name, string? Version, int? GameType, DateTimeOffset? LastPlayed)
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    public const int MaximumDepth = 64;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly uint[] Crc32Table = CreateCrc32Table();

    /// <summary>Reads the supported fields from bounded gzip-compressed NBT.</summary>
    public static WorldMetadata Read(ReadOnlySpan<byte> gzipData, CancellationToken cancellationToken = default)
    {
        var bytes = Decompress(gzipData, cancellationToken);
        return new NbtReader(bytes, cancellationToken).Read();
    }

    /// <summary>Reads a level.dat file without changing it.</summary>
    public static WorldMetadata ReadFile(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 81920,
                FileOptions.SequentialScan);
            using var compressed = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (compressed.Length + read > MaximumBytes)
                {
                    throw new WorldMetadataException(WorldMetadataError.CompressedDataTooLarge,
                        "The compressed world metadata exceeds the size limit.");
                }

                compressed.Write(buffer, 0, read);
            }

            return Read(compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WorldMetadataException)
        {
            throw;
        }
        catch (IOException exception)
        {
            throw new WorldMetadataException(WorldMetadataError.ReadFailure,
                "The world metadata could not be read.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new WorldMetadataException(WorldMetadataError.ReadFailure,
                "The world metadata could not be read.", exception);
        }
    }

    /// <summary>Changes only Data/LevelName in the NBT payload; all other NBT bytes retain their order and content.</summary>
    public static byte[] Renamed(ReadOnlySpan<byte> gzipData, string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var bytes = Decompress(gzipData, cancellationToken);
        var reader = new NbtReader(bytes, cancellationToken);
        _ = reader.Read();
        var encodedName = EncodeModifiedUtf8(name);
        if (encodedName.Length > ushort.MaxValue)
        {
            throw new WorldMetadataException(WorldMetadataError.InvalidText,
                "The replacement world name is too long for NBT.");
        }

        byte[] renamed;
        if (reader.LevelNameLengthOffset is { } lengthOffset
            && reader.LevelNameValueOffset is { } valueOffset
            && reader.LevelNameByteLength is { } oldLength)
        {
            var newLength = checked(bytes.Length - 2 - oldLength + 2 + encodedName.Length);
            EnsureExpandedSize(newLength);
            renamed = new byte[newLength];
            bytes.AsSpan(0, lengthOffset).CopyTo(renamed);
            BinaryPrimitives.WriteUInt16BigEndian(renamed.AsSpan(lengthOffset, 2), (ushort)encodedName.Length);
            encodedName.CopyTo(renamed.AsSpan(lengthOffset + 2));
            var tailStart = valueOffset + oldLength;
            bytes.AsSpan(tailStart).CopyTo(renamed.AsSpan(lengthOffset + 2 + encodedName.Length));
        }
        else if (reader.DataEndOffset is { } dataEnd)
        {
            var tagName = EncodeModifiedUtf8("LevelName");
            var additionLength = checked(1 + 2 + tagName.Length + 2 + encodedName.Length);
            var newLength = checked(bytes.Length + additionLength);
            EnsureExpandedSize(newLength);
            renamed = new byte[newLength];
            bytes.AsSpan(0, dataEnd).CopyTo(renamed);
            var offset = dataEnd;
            renamed[offset++] = 8;
            BinaryPrimitives.WriteUInt16BigEndian(renamed.AsSpan(offset, 2), (ushort)tagName.Length);
            offset += 2;
            tagName.CopyTo(renamed.AsSpan(offset));
            offset += tagName.Length;
            BinaryPrimitives.WriteUInt16BigEndian(renamed.AsSpan(offset, 2), (ushort)encodedName.Length);
            offset += 2;
            encodedName.CopyTo(renamed.AsSpan(offset));
            bytes.AsSpan(dataEnd).CopyTo(renamed.AsSpan(offset + encodedName.Length));
        }
        else
        {
            throw InvalidNbt("The world metadata does not contain a Data compound.");
        }

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            const int chunkSize = 32 * 1024;
            for (var offset = 0; offset < renamed.Length; offset += chunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(chunkSize, renamed.Length - offset);
                gzip.Write(renamed, offset, length);
            }
        }

        if (output.Length > MaximumBytes)
        {
            throw new WorldMetadataException(WorldMetadataError.CompressedDataTooLarge,
                "The compressed world metadata exceeds the size limit.");
        }
        return output.ToArray();
    }

    private static byte[] Decompress(ReadOnlySpan<byte> gzipData, CancellationToken cancellationToken)
    {
        if (gzipData.Length > MaximumBytes)
        {
            throw new WorldMetadataException(WorldMetadataError.CompressedDataTooLarge,
                "The compressed world metadata exceeds the size limit.");
        }

        if (gzipData.Length < 18)
        {
            throw new WorldMetadataException(WorldMetadataError.InvalidGzip, "The world metadata is not valid gzip data.");
        }

        try
        {
            using var input = new MemoryStream(gzipData.ToArray(), writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = gzip.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (output.Length + read > MaximumBytes)
                {
                    throw new WorldMetadataException(WorldMetadataError.DecompressedDataTooLarge,
                        "The decompressed world metadata exceeds the size limit.");
                }

                output.Write(buffer, 0, read);
            }

            var decompressed = output.ToArray();
            var footer = gzipData[^8..];
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer[..4]);
            var expectedSize = BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]);
            if (expectedSize != (uint)decompressed.Length || expectedCrc != ComputeCrc32(decompressed, cancellationToken))
            {
                throw new InvalidDataException("The gzip trailer does not match the decompressed content.");
            }

            return decompressed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WorldMetadataException)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            throw new WorldMetadataException(WorldMetadataError.InvalidGzip,
                "The world metadata is not valid gzip data.", exception);
        }
        catch (IOException exception)
        {
            throw new WorldMetadataException(WorldMetadataError.InvalidGzip,
                "The world metadata is not valid gzip data.", exception);
        }
    }

    private static byte[] EncodeModifiedUtf8(string value)
    {
        var output = new byte[checked(value.Length * 3)];
        var offset = 0;
        foreach (var unit in value)
        {
            if (unit is > '\0' and <= '\u007f')
            {
                output[offset++] = (byte)unit;
            }
            else if (unit <= '\u07ff')
            {
                output[offset++] = (byte)(0xc0 | (unit >> 6));
                output[offset++] = (byte)(0x80 | (unit & 0x3f));
            }
            else
            {
                output[offset++] = (byte)(0xe0 | (unit >> 12));
                output[offset++] = (byte)(0x80 | ((unit >> 6) & 0x3f));
                output[offset++] = (byte)(0x80 | (unit & 0x3f));
            }
        }

        return output.AsSpan(0, offset).ToArray();
    }

    private static string DecodeUtf8(byte[] bytes, int offset, int length)
    {
        try
        {
            return StrictUtf8.GetString(bytes, offset, length);
        }
        catch (DecoderFallbackException)
        {
            var end = checked(offset + length);
            var units = new char[length];
            var count = 0;
            while (offset < end)
            {
                var first = bytes[offset++];
                if (first is >= 1 and <= 0x7f)
                {
                    units[count++] = (char)first;
                    continue;
                }

                if (first is >= 0xc0 and <= 0xdf)
                {
                    if (offset >= end) throw InvalidText();
                    var second = bytes[offset++];
                    if ((second & 0xc0) != 0x80) throw InvalidText();
                    var unit = (char)(((first & 0x1f) << 6) | (second & 0x3f));
                    if (unit == '\0')
                    {
                        if (first != 0xc0 || second != 0x80) throw InvalidText();
                    }
                    else if (unit < 0x80)
                    {
                        throw InvalidText();
                    }

                    units[count++] = unit;
                    continue;
                }

                if (first is >= 0xe0 and <= 0xef)
                {
                    if (offset + 1 >= end) throw InvalidText();
                    var second = bytes[offset++];
                    var third = bytes[offset++];
                    if ((second & 0xc0) != 0x80 || (third & 0xc0) != 0x80) throw InvalidText();
                    var unit = (char)(((first & 0x0f) << 12) | ((second & 0x3f) << 6) | (third & 0x3f));
                    if (unit < 0x0800) throw InvalidText();
                    units[count++] = unit;
                    continue;
                }

                throw InvalidText();
            }

            return new string(units, 0, count);
        }
        catch (WorldMetadataException)
        {
            throw;
        }
    }

    private static void EnsureExpandedSize(int length)
    {
        if (length > MaximumBytes)
        {
            throw new WorldMetadataException(WorldMetadataError.DecompressedDataTooLarge,
                "The decompressed world metadata exceeds the size limit.");
        }
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken)
    {
        var crc = uint.MaxValue;
        for (var index = 0; index < bytes.Length; index++)
        {
            if ((index & 0xffff) == 0) cancellationToken.ThrowIfCancellationRequested();
            crc = (crc >> 8) ^ Crc32Table[(crc ^ bytes[index]) & 0xff];
        }
        return ~crc;
    }

    private static uint[] CreateCrc32Table()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) == 0 ? value >> 1 : 0xedb88320 ^ (value >> 1);
            }
            table[index] = value;
        }
        return table;
    }

    private static WorldMetadataException InvalidNbt(string message = "The world metadata contains invalid NBT data.") =>
        new(WorldMetadataError.InvalidNbt, message);

    private static WorldMetadataException InvalidText() =>
        new(WorldMetadataError.InvalidText, "The world metadata contains invalid UTF-8 text.");

    private enum ContainerKind { None, Root, Data, Version }

    private sealed class NbtReader(byte[] bytes, CancellationToken cancellationToken)
    {
        private int _offset;
        private bool _hasData;
        private string? _name;
        private string? _version;
        private int? _gameType;
        private DateTimeOffset? _lastPlayed;

        public int? LevelNameLengthOffset { get; private set; }
        public int? LevelNameValueOffset { get; private set; }
        public int? LevelNameByteLength { get; private set; }
        public int? DataEndOffset { get; private set; }

        public WorldMetadata Read()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadByte() != 10) throw InvalidNbt("The world metadata root must be an NBT compound.");
            _ = ReadString();
            ReadCompound(ContainerKind.Root, depth: 0);
            if (!_hasData || _offset != bytes.Length) throw InvalidNbt();
            return new WorldMetadata(_name, _version, _gameType, _lastPlayed);
        }

        private void ReadCompound(ContainerKind container, int depth)
        {
            EnsureDepth(depth);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var type = ReadByte();
                if (type == 0)
                {
                    if (container == ContainerKind.Data) DataEndOffset = _offset - 1;
                    return;
                }

                if (type is < 1 or > 12) throw InvalidNbt("The world metadata contains an unsupported NBT tag.");
                var name = ReadString();
                var child = container switch
                {
                    ContainerKind.Root when type == 10 && name == "Data" => ContainerKind.Data,
                    ContainerKind.Data when type == 10 && name == "Version" => ContainerKind.Version,
                    _ => ContainerKind.None
                };
                if (child == ContainerKind.Data) _hasData = true;
                ReadPayload(type, name, child, depth + 1, container);
            }
        }

        private void ReadPayload(byte type, string name, ContainerKind child, int depth, ContainerKind parent)
        {
            EnsureDepth(depth);
            switch (type)
            {
                case 1: Skip(1); break;
                case 2: Skip(2); break;
                case 3:
                    var integer = ReadInt32();
                    if (parent == ContainerKind.Data && name == "GameType") _gameType = integer;
                    break;
                case 4:
                    var timestamp = ReadInt64();
                    if (parent == ContainerKind.Data && name == "LastPlayed" && timestamp > 0)
                    {
                        try { _lastPlayed = DateTimeOffset.FromUnixTimeMilliseconds(timestamp); }
                        catch (ArgumentOutOfRangeException) { _lastPlayed = null; }
                    }
                    break;
                case 5: Skip(4); break;
                case 6: Skip(8); break;
                case 7: SkipArray(1); break;
                case 8:
                    var lengthOffset = _offset;
                    var length = ReadUInt16();
                    var valueOffset = _offset;
                    if (parent == ContainerKind.Data && name == "LevelName")
                    {
                        EnsureAvailable(length);
                        var value = DecodeUtf8(bytes, valueOffset, length);
                        LevelNameLengthOffset = lengthOffset;
                        LevelNameValueOffset = valueOffset;
                        LevelNameByteLength = length;
                        if (!string.IsNullOrEmpty(value)) _name = value;
                    }
                    else if (parent == ContainerKind.Version && name == "Name")
                    {
                        EnsureAvailable(length);
                        var value = DecodeUtf8(bytes, valueOffset, length);
                        if (!string.IsNullOrEmpty(value)) _version = value;
                    }
                    Skip(length);
                    break;
                case 9:
                    var elementType = ReadByte();
                    var count = ReadLength();
                    if (!(elementType is >= 1 and <= 12) && !(elementType == 0 && count == 0))
                        throw InvalidNbt("The world metadata contains an invalid NBT list.");
                    if (count > bytes.Length - _offset) throw InvalidNbt();
                    for (var index = 0; index < count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ReadPayload(elementType, string.Empty, ContainerKind.None, depth + 1, ContainerKind.None);
                    }
                    break;
                case 10:
                    ReadCompound(child, depth);
                    break;
                case 11: SkipArray(4); break;
                case 12: SkipArray(8); break;
                default: throw InvalidNbt("The world metadata contains an unsupported NBT tag.");
            }
        }

        private void SkipArray(int elementWidth)
        {
            var count = ReadLength();
            var byteLength = (long)count * elementWidth;
            if (byteLength > int.MaxValue) throw InvalidNbt();
            Skip((int)byteLength);
        }

        private int ReadLength()
        {
            var length = ReadInt32();
            if (length < 0) throw InvalidNbt("The world metadata contains a negative NBT length.");
            return length;
        }

        private string ReadString()
        {
            var length = ReadUInt16();
            EnsureAvailable(length);
            var text = DecodeUtf8(bytes, _offset, length);
            Skip(length);
            return text;
        }

        private byte ReadByte()
        {
            if (_offset >= bytes.Length) throw InvalidNbt();
            return bytes[_offset++];
        }

        private ushort ReadUInt16()
        {
            EnsureAvailable(2);
            var value = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(_offset, 2));
            _offset += 2;
            return value;
        }

        private int ReadInt32()
        {
            EnsureAvailable(4);
            var value = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(_offset, 4));
            _offset += 4;
            return value;
        }

        private long ReadInt64()
        {
            EnsureAvailable(8);
            var value = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(_offset, 8));
            _offset += 8;
            return value;
        }

        private void Skip(int count)
        {
            if (count < 0) throw InvalidNbt();
            EnsureAvailable(count);
            _offset += count;
        }

        private void EnsureAvailable(int count)
        {
            if (count < 0 || count > bytes.Length - _offset) throw InvalidNbt();
        }

        private static void EnsureDepth(int depth)
        {
            if (depth > MaximumDepth)
            {
                throw new WorldMetadataException(WorldMetadataError.NestingTooDeep,
                    "The world metadata exceeds the NBT nesting limit.");
            }
        }
    }
}

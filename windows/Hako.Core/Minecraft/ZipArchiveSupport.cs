using System.Buffers.Binary;
using System.Text;

namespace Hako.Core.Minecraft;

internal enum ZipArchiveValidationFailure
{
    InvalidArchive,
    UnsafeEntry
}

internal sealed class ZipArchiveValidationException(
    ZipArchiveValidationFailure failure,
    Exception? innerException = null)
    : IOException("The ZIP archive is invalid or contains an unsupported entry.", innerException)
{
    public ZipArchiveValidationFailure Failure { get; } = failure;
}

internal sealed record ZipArchiveValidationLimits(
    int MaximumEntries,
    long MaximumArchiveSize,
    long MaximumFileSize,
    long MaximumExpandedSize,
    long MaximumCentralDirectorySize);

internal sealed record ZipArchiveEntryRecord(
    int Index,
    string Name,
    byte[] RawName,
    ushort Flags,
    ushort CompressionMethod,
    uint Crc32,
    long CompressedLength,
    long Length,
    uint ExternalAttributes,
    long LocalHeaderOffset,
    long DataOffset,
    long EndOffset,
    bool IsDirectory,
    bool IsSymbolicLink);

internal sealed record ZipArchiveScanResult(
    long CentralDirectoryOffset,
    long CentralDirectorySize,
    IReadOnlyList<ZipArchiveEntryRecord> Entries);

/// <summary>Shared central-directory, local-header, and entry-type checks for imported ZIP archives.</summary>
internal static class ZipArchiveSupport
{
    private const uint EndSignature = 0x06054b50;
    private const uint Zip64EndSignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint EntrySignature = 0x02014b50;
    private const uint LocalEntrySignature = 0x04034b50;
    private const uint DescriptorSignature = 0x08074b50;
    private const int EndRecordSize = 22;
    private const int Zip64LocatorSize = 20;
    private const int EntryRecordSize = 46;
    private const int LocalEntryRecordSize = 30;
    private const int MaximumZipCommentLength = ushort.MaxValue;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Encoding Cp437 = GetCp437();
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static ZipArchiveScanResult ScanCentralDirectory(string archivePath, ZipArchiveValidationLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > limits.MaximumArchiveSize)
        {
            throw InvalidArchive();
        }

        var end = ReadEndRecord(stream);
        if (end.EntryCount > limits.MaximumEntries
            || end.CentralDirectorySize > limits.MaximumCentralDirectorySize
            || end.CentralDirectoryOffset < 0
            || end.CentralDirectorySize < 0
            || checked(end.CentralDirectoryOffset + end.CentralDirectorySize) > end.EndRecordOffset)
        {
            throw InvalidArchive();
        }

        stream.Position = end.CentralDirectoryOffset;
        var entries = new List<ZipArchiveEntryRecord>(end.EntryCount);
        var fixedEntry = new byte[EntryRecordSize];
        long expandedSize = 0;
        for (var index = 0; index < end.EntryCount; index++)
        {
            stream.ReadExactly(fixedEntry);
            var header = fixedEntry.AsSpan();
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != EntrySignature)
            {
                throw InvalidArchive();
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
            var crc32 = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            var compressed32 = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            var length32 = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            var diskStart = BinaryPrimitives.ReadUInt16LittleEndian(header[34..]);
            var externalAttributes = BinaryPrimitives.ReadUInt32LittleEndian(header[38..]);
            var localOffset32 = BinaryPrimitives.ReadUInt32LittleEndian(header[42..]);

            if ((flags & (0x0001 | 0x0020 | 0x0040 | 0x2000)) != 0
                || (flags & ~(0x0006 | 0x0008 | 0x0800)) != 0
                || method is not (0 or 8)
                || diskStart is not (0 or ushort.MaxValue))
            {
                throw InvalidArchive();
            }

            var metadataLength = (long)nameLength + extraLength + commentLength;
            if (checked(stream.Position + metadataLength) > checked(end.CentralDirectoryOffset + end.CentralDirectorySize))
            {
                throw InvalidArchive();
            }

            var rawName = new byte[nameLength];
            stream.ReadExactly(rawName);
            var extra = new byte[extraLength];
            stream.ReadExactly(extra);
            stream.Position += commentLength;

            var zip64 = ReadZip64Extra(extra, length32, compressed32, localOffset32, diskStart);
            var length = zip64.Length;
            var compressedLength = zip64.CompressedLength;
            var localOffset = zip64.LocalHeaderOffset;
            if (length < 0 || compressedLength < 0 || localOffset < 0
                || length > limits.MaximumFileSize || compressedLength > limits.MaximumArchiveSize)
            {
                throw InvalidArchive();
            }

            expandedSize = checked(expandedSize + length);
            if (expandedSize > limits.MaximumExpandedSize)
            {
                throw InvalidArchive();
            }

            var name = DecodeName(rawName, extra, flags);
            var isDirectory = name.EndsWith("/", StringComparison.Ordinal);
            var isSymbolicLink = ValidateEntryAttributes(externalAttributes, isDirectory);
            if (method == 0 && compressedLength != length)
            {
                throw InvalidArchive();
            }

            var nextCentralPosition = stream.Position;
            var entry = ValidateLocalEntry(stream, end.CentralDirectoryOffset, index, name, rawName, flags, method,
                crc32, compressedLength, length, externalAttributes, localOffset, isDirectory, isSymbolicLink,
                zip64.UsesZip64);
            entries.Add(entry);
            stream.Position = nextCentralPosition;
        }

        if (stream.Position != checked(end.CentralDirectoryOffset + end.CentralDirectorySize))
        {
            throw InvalidArchive();
        }

        var orderedRanges = entries.OrderBy(entry => entry.LocalHeaderOffset).ToArray();
        for (var index = 1; index < orderedRanges.Length; index++)
        {
            if (orderedRanges[index - 1].EndOffset > orderedRanges[index].LocalHeaderOffset)
            {
                throw InvalidArchive();
            }
        }

        return new ZipArchiveScanResult(end.CentralDirectoryOffset, end.CentralDirectorySize, entries);
    }

    public static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        }

        return crc;
    }

    public static uint FinishCrc32(uint crc) => ~crc;

    private static ZipArchiveEntryRecord ValidateLocalEntry(
        FileStream stream,
        long centralOffset,
        int index,
        string name,
        byte[] rawName,
        ushort flags,
        ushort method,
        uint crc32,
        long compressedLength,
        long length,
        uint externalAttributes,
        long localOffset,
        bool isDirectory,
        bool isSymbolicLink,
        bool usesZip64)
    {
        if (localOffset < 0 || checked(localOffset + LocalEntryRecordSize) > centralOffset)
        {
            throw InvalidArchive();
        }

        stream.Position = localOffset;
        Span<byte> header = stackalloc byte[LocalEntryRecordSize];
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != LocalEntrySignature
            || BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) != flags
            || BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) != method)
        {
            throw InvalidArchive();
        }

        var localCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[14..]);
        var localCompressed32 = BinaryPrimitives.ReadUInt32LittleEndian(header[18..]);
        var localLength32 = BinaryPrimitives.ReadUInt32LittleEndian(header[22..]);
        var localNameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
        var localExtraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
        if (localNameLength != rawName.Length)
        {
            throw InvalidArchive();
        }

        var localName = new byte[localNameLength];
        stream.ReadExactly(localName);
        if (!rawName.AsSpan().SequenceEqual(localName))
        {
            throw InvalidArchive();
        }

        var localExtra = new byte[localExtraLength];
        stream.ReadExactly(localExtra);
        var dataOffset = checked(localOffset + LocalEntryRecordSize + localNameLength + localExtraLength);
        var dataEnd = checked(dataOffset + compressedLength);
        if (dataEnd > centralOffset)
        {
            throw InvalidArchive();
        }

        var localZip64 = ReadZip64Extra(localExtra, localLength32, localCompressed32, 0, 0);
        var descriptor = (flags & 0x0008) != 0;
        if (!descriptor)
        {
            if (localCrc != crc32 || localZip64.Length != length || localZip64.CompressedLength != compressedLength)
            {
                throw InvalidArchive();
            }
        }
        else if ((localCrc != 0 && localCrc != crc32)
                 || (localLength32 != 0 && localZip64.Length != length)
                 || (localCompressed32 != 0 && localZip64.CompressedLength != compressedLength))
        {
            throw InvalidArchive();
        }

        var endOffset = descriptor
            ? ValidateDataDescriptor(stream, dataEnd, centralOffset, crc32, compressedLength, length, usesZip64 || localZip64.UsesZip64)
            : dataEnd;
        return new ZipArchiveEntryRecord(index, name, rawName, flags, method, crc32, compressedLength, length,
            externalAttributes, localOffset, dataOffset, endOffset, isDirectory, isSymbolicLink);
    }

    private static long ValidateDataDescriptor(
        FileStream stream,
        long offset,
        long centralOffset,
        uint expectedCrc,
        long expectedCompressed,
        long expectedLength,
        bool zip64)
    {
        Span<byte> signature = stackalloc byte[sizeof(uint)];
        Span<byte> descriptor = stackalloc byte[20];
        foreach (var withSignature in new[] { true, false })
        {
            var cursor = offset;
            if (withSignature)
            {
                if (checked(cursor + sizeof(uint)) > centralOffset) continue;
                stream.Position = cursor;
                stream.ReadExactly(signature);
                if (BinaryPrimitives.ReadUInt32LittleEndian(signature) != DescriptorSignature) continue;
                cursor += sizeof(uint);
            }

            var valueLength = zip64 ? 20 : 12;
            if (checked(cursor + valueLength) > centralOffset) continue;
            stream.Position = cursor;
            stream.ReadExactly(descriptor[..valueLength]);
            var actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(descriptor);
            var actualCompressed = zip64
                ? ReadUInt64AsInt64(descriptor[4..])
                : BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]);
            var actualLength = zip64
                ? ReadUInt64AsInt64(descriptor[12..])
                : BinaryPrimitives.ReadUInt32LittleEndian(descriptor[8..]);
            if (actualCrc == expectedCrc && actualCompressed == expectedCompressed && actualLength == expectedLength)
            {
                return cursor + valueLength;
            }
        }

        throw InvalidArchive();
    }

    private static (int EntryCount, long CentralDirectoryOffset, long CentralDirectorySize, long EndRecordOffset) ReadEndRecord(FileStream stream)
    {
        var tailLength = (int)Math.Min(stream.Length, EndRecordSize + MaximumZipCommentLength + Zip64LocatorSize);
        var tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        stream.ReadExactly(tail);
        var endOffset = -1;
        for (var index = tail.Length - EndRecordSize; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, sizeof(uint))) != EndSignature) continue;
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20, sizeof(ushort)));
            if (index + EndRecordSize + commentLength == tail.Length)
            {
                endOffset = index;
                break;
            }
        }

        if (endOffset < 0) throw InvalidArchive();
        var end = tail.AsSpan(endOffset, EndRecordSize);
        var absoluteEndOffset = stream.Length - tailLength + endOffset;
        if (BinaryPrimitives.ReadUInt16LittleEndian(end[4..]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(end[6..]) != 0)
        {
            throw InvalidArchive();
        }

        var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(end[8..]);
        var entries = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        var centralSize = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        var requiresZip64 = entriesOnDisk == ushort.MaxValue || entries == ushort.MaxValue
            || centralSize == uint.MaxValue || centralOffset == uint.MaxValue;
        if (!requiresZip64)
        {
            if (entriesOnDisk != entries) throw InvalidArchive();
            return (entries, centralOffset, centralSize, absoluteEndOffset);
        }

        var locatorOffset = absoluteEndOffset - Zip64LocatorSize;
        if (locatorOffset < 0) throw InvalidArchive();
        stream.Position = locatorOffset;
        Span<byte> locator = stackalloc byte[Zip64LocatorSize];
        stream.ReadExactly(locator);
        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature
            || BinaryPrimitives.ReadUInt32LittleEndian(locator[4..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(locator[16..]) != 1)
        {
            throw InvalidArchive();
        }

        var zip64EndOffset = ReadUInt64AsInt64(locator[8..]);
        if (checked(zip64EndOffset + 56) > locatorOffset) throw InvalidArchive();
        stream.Position = zip64EndOffset;
        Span<byte> zip64End = stackalloc byte[56];
        stream.ReadExactly(zip64End);
        if (BinaryPrimitives.ReadUInt32LittleEndian(zip64End) != Zip64EndSignature
            || ReadUInt64AsInt64(zip64End[4..]) < 44
            || BinaryPrimitives.ReadUInt32LittleEndian(zip64End[16..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(zip64End[20..]) != 0)
        {
            throw InvalidArchive();
        }

        var zip64EntriesOnDisk = ReadUInt64AsInt64(zip64End[24..]);
        var zip64Entries = ReadUInt64AsInt64(zip64End[32..]);
        if (zip64EntriesOnDisk != zip64Entries || zip64Entries > int.MaxValue)
        {
            throw InvalidArchive();
        }

        return ((int)zip64Entries, ReadUInt64AsInt64(zip64End[48..]), ReadUInt64AsInt64(zip64End[40..]), absoluteEndOffset);
    }

    private static (long Length, long CompressedLength, long LocalHeaderOffset, bool UsesZip64) ReadZip64Extra(
        byte[] extra,
        uint length32,
        uint compressed32,
        uint localOffset32,
        ushort diskStart)
    {
        var fields = ParseExtraFields(extra);
        var usesZip64 = length32 == uint.MaxValue || compressed32 == uint.MaxValue
            || localOffset32 == uint.MaxValue || diskStart == ushort.MaxValue;
        var length = length32 == uint.MaxValue ? (long?)null : length32;
        var compressedLength = compressed32 == uint.MaxValue ? (long?)null : compressed32;
        var localOffset = localOffset32 == uint.MaxValue ? (long?)null : localOffset32;
        if (usesZip64)
        {
            var zip64 = fields.FirstOrDefault(field => field.Id == 0x0001).Data;
            if (zip64 is null) throw InvalidArchive();
            var cursor = 0;
            if (length is null) length = ReadZip64Value(zip64, ref cursor);
            if (compressedLength is null) compressedLength = ReadZip64Value(zip64, ref cursor);
            if (localOffset is null) localOffset = ReadZip64Value(zip64, ref cursor);
            if (diskStart == ushort.MaxValue)
            {
                if (cursor + sizeof(uint) > zip64.Length || BinaryPrimitives.ReadUInt32LittleEndian(zip64.AsSpan(cursor)) != 0)
                {
                    throw InvalidArchive();
                }
            }
        }

        return (length ?? 0, compressedLength ?? 0, localOffset ?? 0, usesZip64);
    }

    private static long ReadZip64Value(byte[] data, ref int cursor)
    {
        if (cursor + sizeof(ulong) > data.Length) throw InvalidArchive();
        var value = ReadUInt64AsInt64(data.AsSpan(cursor));
        cursor += sizeof(ulong);
        return value;
    }

    private static IReadOnlyList<(ushort Id, byte[] Data)> ParseExtraFields(byte[] extra)
    {
        var fields = new List<(ushort, byte[])>();
        var cursor = 0;
        while (cursor < extra.Length)
        {
            if (cursor + 4 > extra.Length) throw InvalidArchive();
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(cursor));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(cursor + 2));
            cursor += 4;
            if (cursor + length > extra.Length) throw InvalidArchive();
            fields.Add((id, extra.AsSpan(cursor, length).ToArray()));
            cursor += length;
        }

        return fields;
    }

    private static string DecodeName(byte[] rawName, byte[] extra, ushort flags)
    {
        try
        {
            if ((flags & 0x0800) != 0) return StrictUtf8.GetString(rawName);
            try { return StrictUtf8.GetString(rawName); }
            catch (DecoderFallbackException) { }

            var crc = FinishCrc32(UpdateCrc32(uint.MaxValue, rawName));
            foreach (var field in ParseExtraFields(extra).Where(field => field.Id == 0x7075))
            {
                if (field.Data.Length < 5 || field.Data[0] != 1
                    || BinaryPrimitives.ReadUInt32LittleEndian(field.Data.AsSpan(1)) != crc) continue;
                try { return StrictUtf8.GetString(field.Data.AsSpan(5)); }
                catch (DecoderFallbackException) { }
            }

            return Cp437.GetString(rawName);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ZipArchiveValidationException(ZipArchiveValidationFailure.UnsafeEntry, exception);
        }
    }

    internal static bool ValidateEntryAttributes(uint attributes, bool directory)
    {
        var dosAttributes = attributes & 0xFFFF;
        var unixType = (attributes >> 16) & 0xF000;
        var isSymbolicLink = unixType == 0xA000;
        var isSupportedUnixType = unixType is 0 or 0x4000 or 0x8000 or 0xA000;
        var dosReparse = (dosAttributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Device)) != 0;
        if (!isSupportedUnixType || dosReparse
            || (unixType == 0x4000 && !directory)
            || (unixType == 0x8000 && directory)
            || (isSymbolicLink && directory))
        {
            throw new ZipArchiveValidationException(ZipArchiveValidationFailure.UnsafeEntry);
        }

        return isSymbolicLink;
    }

    private static long ReadUInt64AsInt64(ReadOnlySpan<byte> value)
    {
        var result = BinaryPrimitives.ReadUInt64LittleEndian(value);
        if (result > long.MaxValue) throw InvalidArchive();
        return (long)result;
    }

    private static Encoding GetCp437()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(437, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : 0xedb88320 ^ (value >> 1);
            table[index] = value;
        }

        return table;
    }

    private static ZipArchiveValidationException InvalidArchive() =>
        new(ZipArchiveValidationFailure.InvalidArchive);
}

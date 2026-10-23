using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;

namespace Hako.Core.Minecraft;

public sealed record ModpackArchiveOverride(string Path, string EntryPath, long Size);

public sealed record ModpackArchiveContents(ModpackManifest Manifest, IReadOnlyList<ModpackArchiveOverride> Overrides);

public static class ModpackArchive
{
    private const uint EndSignature = 0x06054b50;
    private const uint EntrySignature = 0x02014b50;
    private const int EndRecordSize = 22;
    private const int EntryRecordSize = 46;
    private const int MaximumZipCommentLength = ushort.MaxValue;

    public static ModpackArchiveContents Inspect(string archivePath) =>
        Inspect(archivePath, ModpackArchiveLimits.Default);

    internal static ModpackArchiveContents Inspect(string archivePath, ModpackArchiveLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var archiveLength = new FileInfo(archivePath).Length;
        if (archiveLength <= 0 || archiveLength > limits.MaximumArchiveSize)
        {
            throw new ModpackException(ModpackError.Archive);
        }

        var centralDirectory = ScanCentralDirectory(archivePath, limits);
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count != centralDirectory.EntryCount)
        {
            throw new ModpackException(ModpackError.Archive);
        }

        var archivePaths = new ModpackPathRegistry();
        var indexEntries = new List<ZipArchiveEntry>();
        long expandedSize = 0;
        foreach (var entry in archive.Entries)
        {
            var isDirectory = entry.FullName.EndsWith('/');
            var path = isDirectory ? entry.FullName[..^1] : entry.FullName;
            ModpackPaths.Validate(path);
            ValidateEntryAttributes(entry, isDirectory);
            if (entry.Length < 0 || entry.Length > limits.MaximumFileSize)
            {
                throw new ModpackException(ModpackError.Archive);
            }

            expandedSize = checked(expandedSize + entry.Length);
            if (expandedSize > limits.MaximumExpandedSize)
            {
                throw new ModpackException(ModpackError.Archive);
            }

            if (isDirectory)
            {
                if (entry.Length != 0) throw new ModpackException(ModpackError.Archive);
                archivePaths.AddDirectory(path, explicitEntry: true);
            }
            else
            {
                archivePaths.AddFile(path);
                if (string.Equals(path, "modrinth.index.json", StringComparison.Ordinal))
                {
                    if (entry.Length > limits.MaximumIndexSize) throw new ModpackException(ModpackError.Archive);
                    indexEntries.Add(entry);
                }
            }
        }

        if (indexEntries.Count != 1)
        {
            throw new ModpackException(ModpackError.Archive);
        }

        var manifest = ReadManifest(indexEntries[0], limits);
        var destinationPaths = new ModpackPathRegistry();
        foreach (var file in manifest.Files)
        {
            destinationPaths.AddFile(file.Path);
        }

        var overrides = new Dictionary<string, ModpackArchiveOverride>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in new[] { "overrides/", "client-overrides/", "server-overrides/" })
        {
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.StartsWith(layer, StringComparison.Ordinal))
                {
                    continue;
                }

                var isDirectory = entry.FullName.EndsWith('/');
                var entryPath = isDirectory ? entry.FullName[..^1] : entry.FullName;
                var destination = ModpackPaths.OverrideDestination(entryPath, isDirectory);
                if (destination is null || layer == "server-overrides/")
                {
                    continue;
                }

                if (isDirectory)
                {
                    destinationPaths.AddDirectory(destination);
                    continue;
                }

                destinationPaths.AddFile(destination, replacing: true);
                if (overrides.TryGetValue(destination, out var previous)
                    && !string.Equals(previous.Path, destination, StringComparison.Ordinal))
                {
                    throw new ModpackException(ModpackError.UnsafePath);
                }

                overrides[destination] = new ModpackArchiveOverride(destination, entry.FullName, entry.Length);
            }
        }

        return new ModpackArchiveContents(manifest, overrides.Values.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray());
    }

    public static async Task ExtractAsync(
        string archivePath,
        ModpackArchiveOverride entry,
        string destination,
        long limit,
        CancellationToken cancellationToken)
    {
        ModpackPaths.Validate(entry.Path);
        ModpackPaths.Validate(entry.EntryPath);
        if (limit < 0 || entry.Size > limit)
        {
            throw new ModpackException(ModpackError.Archive);
        }

        try
        {
            await using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
            var matches = archive.Entries.Where(item => string.Equals(item.FullName, entry.EntryPath, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || matches[0].FullName.EndsWith('/')
                || matches[0].Length != entry.Size)
            {
                throw new ModpackException(ModpackError.Archive);
            }

            ValidateEntryAttributes(matches[0], directory: false);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = matches[0].Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[81920];
            long written = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                written = checked(written + read);
                if (written > limit) throw new ModpackException(ModpackError.Archive);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (written != entry.Size)
            {
                throw new ModpackException(ModpackError.Archive);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { File.Delete(destination); }
            catch (FileNotFoundException) { }
            throw;
        }
    }

    private static ModpackManifest ReadManifest(ZipArchiveEntry entry, ModpackArchiveLimits limits)
    {
        try
        {
            using var input = entry.Open();
            using var output = new MemoryStream((int)entry.Length);
            var buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                total = checked(total + read);
                if (total > limits.MaximumIndexSize) throw new ModpackException(ModpackError.Archive);
                output.Write(buffer, 0, read);
            }

            if (total != entry.Length) throw new ModpackException(ModpackError.Archive);
            var manifest = JsonSerializer.Deserialize(output.GetBuffer().AsSpan(0, (int)output.Length),
                HakoJsonContext.Default.ModpackManifest)
                ?? throw new ModpackException(ModpackError.InvalidManifest);
            manifest.Validate(limits);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new ModpackException(ModpackError.InvalidManifest, exception);
        }
    }

    private static (int EntryCount, long CentralOffset, long CentralSize) ScanCentralDirectory(
        string archivePath,
        ModpackArchiveLimits limits)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var tailLength = (int)Math.Min(stream.Length, EndRecordSize + MaximumZipCommentLength);
        var tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        stream.ReadExactly(tail);

        var endOffset = -1;
        for (var index = tail.Length - EndRecordSize; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, sizeof(uint))) != EndSignature)
            {
                continue;
            }

            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20, sizeof(ushort)));
            if (index + EndRecordSize + commentLength == tail.Length)
            {
                endOffset = index;
                break;
            }
        }

        if (endOffset < 0) throw new ModpackException(ModpackError.Archive);
        var end = tail.AsSpan(endOffset);
        if (BinaryPrimitives.ReadUInt16LittleEndian(end[4..]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(end[6..]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(end[8..]) != BinaryPrimitives.ReadUInt16LittleEndian(end[10..]))
        {
            throw new ModpackException(ModpackError.Archive);
        }

        var entries = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        var centralSize = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        if (entries == ushort.MaxValue || centralSize == uint.MaxValue || centralOffset == uint.MaxValue
            || entries > limits.MaximumEntries || centralSize > limits.MaximumCentralDirectorySize
            || (long)centralOffset + centralSize > stream.Length)
        {
            throw new ModpackException(ModpackError.Archive);
        }

        stream.Position = centralOffset;
        var fixedEntry = new byte[EntryRecordSize];
        long expandedSize = 0;
        for (var index = 0; index < entries; index++)
        {
            stream.ReadExactly(fixedEntry);
            var header = fixedEntry.AsSpan();
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != EntrySignature)
            {
                throw new ModpackException(ModpackError.Archive);
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
            var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            var disk = BinaryPrimitives.ReadUInt16LittleEndian(header[34..]);
            if ((flags & (0x0001 | 0x0040 | 0x2000)) != 0
                || method is not (0 or 8)
                || disk != 0
                || compressedSize == uint.MaxValue
                || uncompressedSize == uint.MaxValue)
            {
                throw new ModpackException(ModpackError.Archive);
            }

            if (uncompressedSize > limits.MaximumFileSize)
            {
                throw new ModpackException(ModpackError.Archive);
            }

            expandedSize = checked(expandedSize + uncompressedSize);
            if (expandedSize > limits.MaximumExpandedSize)
            {
                throw new ModpackException(ModpackError.Archive);
            }

            var metadataLength = (long)nameLength + extraLength + commentLength;
            if (stream.Position + metadataLength > (long)centralOffset + centralSize)
            {
                throw new ModpackException(ModpackError.Archive);
            }

            stream.Position += metadataLength;
        }

        if (stream.Position != (long)centralOffset + centralSize)
        {
            throw new ModpackException(ModpackError.Archive);
        }

        return (entries, centralOffset, centralSize);
    }

    private static void ValidateEntryAttributes(ZipArchiveEntry entry, bool directory)
    {
        var attributes = unchecked((uint)entry.ExternalAttributes);
        var dosAttributes = attributes & 0xFFFF;
        var unixType = (attributes >> 16) & 0xF000;
        var isSymlink = unixType == 0xA000;
        var isSupportedUnixType = unixType is 0 or 0x4000 or 0x8000;
        var dosReparse = (dosAttributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Device)) != 0;
        if (isSymlink || !isSupportedUnixType || dosReparse
            || (unixType == 0x4000 && !directory)
            || (unixType == 0x8000 && directory))
        {
            throw new ModpackException(ModpackError.UnsafePath);
        }
    }
}

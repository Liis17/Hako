using System.IO.Compression;
using System.Text.Json;

namespace Hako.Core.Minecraft;

public sealed record ModpackArchiveOverride(string Path, string EntryPath, long Size);

public sealed record ModpackArchiveContents(ModpackManifest Manifest, IReadOnlyList<ModpackArchiveOverride> Overrides);

public static class ModpackArchive
{
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

        ZipArchiveScanResult centralDirectory;
        try
        {
            centralDirectory = ZipArchiveSupport.ScanCentralDirectory(archivePath, new ZipArchiveValidationLimits(
                limits.MaximumEntries, limits.MaximumArchiveSize, limits.MaximumFileSize,
                limits.MaximumExpandedSize, limits.MaximumCentralDirectorySize));
        }
        catch (ZipArchiveValidationException exception)
        {
            throw new ModpackException(exception.Failure == ZipArchiveValidationFailure.UnsafeEntry
                ? ModpackError.UnsafePath
                : ModpackError.Archive, exception);
        }

        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count != centralDirectory.Entries.Count)
        {
            throw new ModpackException(ModpackError.Archive);
        }

        var archivePaths = new ModpackPathRegistry();
        var indexEntries = new List<ZipArchiveEntry>();
        long expandedSize = 0;
        for (var entryIndex = 0; entryIndex < archive.Entries.Count; entryIndex++)
        {
            var entry = archive.Entries[entryIndex];
            var centralEntry = centralDirectory.Entries[entryIndex];
            var isDirectory = centralEntry.IsDirectory;
            var path = isDirectory ? centralEntry.Name[..^1] : centralEntry.Name;
            ModpackPaths.Validate(path);
            if (centralEntry.IsSymbolicLink)
            {
                throw new ModpackException(ModpackError.UnsafePath);
            }
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
            for (var entryIndex = 0; entryIndex < archive.Entries.Count; entryIndex++)
            {
                var entry = archive.Entries[entryIndex];
                var centralEntry = centralDirectory.Entries[entryIndex];
                if (!centralEntry.Name.StartsWith(layer, StringComparison.Ordinal))
                {
                    continue;
                }

                var isDirectory = centralEntry.IsDirectory;
                var entryPath = isDirectory ? centralEntry.Name[..^1] : centralEntry.Name;
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

                overrides[destination] = new ModpackArchiveOverride(destination, centralEntry.Name, entry.Length);
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
            var limits = ModpackArchiveLimits.Default;
            ZipArchiveScanResult centralDirectory;
            try
            {
                centralDirectory = ZipArchiveSupport.ScanCentralDirectory(archivePath, new ZipArchiveValidationLimits(
                    limits.MaximumEntries, limits.MaximumArchiveSize, limits.MaximumFileSize,
                    limits.MaximumExpandedSize, limits.MaximumCentralDirectorySize));
            }
            catch (ZipArchiveValidationException exception)
            {
                throw new ModpackException(exception.Failure == ZipArchiveValidationFailure.UnsafeEntry
                    ? ModpackError.UnsafePath
                    : ModpackError.Archive, exception);
            }
            await using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
            var matches = centralDirectory.Entries.Select((item, index) => (item, index))
                .Where(item => string.Equals(item.item.Name, entry.EntryPath, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || matches[0].item.IsDirectory || matches[0].item.IsSymbolicLink
                || archive.Entries.Count != centralDirectory.Entries.Count
                || archive.Entries[matches[0].index].Length != entry.Size)
            {
                throw new ModpackException(ModpackError.Archive);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = archive.Entries[matches[0].index].Open();
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

}

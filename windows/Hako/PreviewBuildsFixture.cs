using System.Globalization;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;

namespace Hako;

/// <summary>`--profile-preview builds` fixtures use only their temporary root and canned HTTP responses.</summary>
internal static class PreviewBuildsFixture
{
    public static readonly Uri ModrinthBase = new("https://modrinth.fixture/v2/");
    public static readonly Uri FabricMetaBase = new("https://fabric.fixture/v2/");

    private static string CatalogJson => BuildCatalogJson();

    private static readonly byte[] OldModBytes = Encoding.UTF8.GetBytes("Hako preview mod version 1");
    private static readonly byte[] NewModBytes = Encoding.UTF8.GetBytes("Hako preview mod version 2");
    private static readonly byte[] BetaModBytes = Encoding.UTF8.GetBytes("Hako preview mod beta");
    private static readonly byte[] AlphaModBytes = Encoding.UTF8.GetBytes("Hako preview mod alpha");
    private static readonly byte[] OldPackBytes = Encoding.UTF8.GetBytes("Hako preview resource pack version 1");
    private static readonly byte[] NewPackBytes = Encoding.UTF8.GetBytes("Hako preview resource pack version 2");
    private static readonly byte[] BetaPackBytes = Encoding.UTF8.GetBytes("Hako preview resource pack beta");
    private static readonly byte[] AlphaPackBytes = Encoding.UTF8.GetBytes("Hako preview resource pack alpha");
    private static readonly byte[] OldDatapackBytes = PreviewDatapackZip("Preview datapack 1.0");
    private static readonly byte[] NewDatapackBytes = PreviewDatapackZip("Preview datapack 2.0");
    private static readonly byte[] RequiredLinkedPackBytes = PreviewDatapackZip("Required linked resource pack");
    private static readonly byte[] OptionalLinkedPackBytes = PreviewDatapackZip("Optional linked resource pack");
    private static readonly byte[] ModpackArchiveBytes = PreviewModpackArchive();
    private static readonly byte[] ModpackIconBytes = ReadPreviewModpackIcon();
    private const string ModpackId = "PreviewModpack";
    private const string ModpackVersionId = "PreviewPack001";

    private const string ModId = "HakoMod1";
    private const string PackId = "HakoPack";
    private const string ModOldVersion = "HakoM001";
    private const string ModReleaseVersion = "HakoM002";
    private const string ModBetaVersion = "HakoM003";
    private const string ModAlphaVersion = "HakoM004";
    private const string PackOldVersion = "HakoP001";
    private const string PackReleaseVersion = "HakoP002";
    private const string PackBetaVersion = "HakoP003";
    private const string PackAlphaVersion = "HakoP004";
    private const string DatapackId = "HakoDpack";
    private const string DatapackDependencyId = "HakoDdep";
    private const string DatapackOldVersion = "HakoD001";
    private const string DatapackReleaseVersion = "HakoD002";
    private static readonly DateTimeOffset OldDate = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ReleaseDate = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    public static HttpClient CreateContentHttpClient() => new(new ContentHandler());

    public static InstallationCoordinator CreateInstallations(InstanceStore store) => new(
        store,
        (version, pinned, _) => Task.FromResult(new PreparedInstallation(
            version,
            pinned ?? GameArchitecture.X64,
            new MojangVersionMetadata { Id = version.Id },
            [],
            new MojangJavaRuntimePackage(),
            new MojangJavaRuntimeManifest(),
            [],
            [])),
        async (_, _, progress, cancellationToken) =>
        {
            progress?.Report(new InstallationProgress(InstallationProgressStage.Downloading, 420L << 20, 1000L << 20, 1200, 3100));
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        });

    /// <summary>Seeds isolated Vanilla and Fabric profiles and returns the one used by the requested page.</summary>
    public static Guid Seed(InstanceStore store, PreviewPage page)
    {
        GameInstance Create(string name, string symbol, bool offline = false, InstanceModLoader loader = InstanceModLoader.Vanilla) => store.Create(
            new InstanceDraft
            {
                Name = name,
                IconSymbol = symbol,
                OfflineMode = offline,
                OfflineUsername = "PreviewPlayer",
                ModLoader = loader,
                FabricConfiguration = loader == InstanceModLoader.Fabric ? new FabricConfiguration("0.16.10") : null
            },
            "1.21.4",
            "https://piston-meta.mojang.com/v1/packages/0000000000000000000000000000000000000002/1.21.4.json",
            "0000000000000000000000000000000000000002",
            javaMajorVersion: 21);

        GameInstance Complete(GameInstance instance)
        {
            store.PinArchitecture(instance.Id, GameArchitecture.X64);
            store.CompleteInstallation(instance.Id, new InstallationResult(GameArchitecture.X64, "bin/javaw.exe", 21, false));
            return store.List().First(item => item.Id == instance.Id);
        }

        var vanilla = Complete(Create("Survival", "leaf.fill", offline: true));
        SeedWorlds(store, vanilla);
        if (page == PreviewPage.Screenshots) SeedScreenshots(store, vanilla);
        Create("Creative", "cube.fill");
        Create("Skyblock", "moon.stars.fill");

        var fabric = Complete(Create("Fabric Preview", "hammer.fill", offline: true, InstanceModLoader.Fabric));
        SeedContent(store, fabric);
        SeedWorlds(store, fabric);

        var paused = Create("Adventure", "bolt.fill");
        store.SetInstallationStatus(paused.Id, InstanceInstallationState.Paused, pauseRequested: true);

        var failed = Create("Hardcore", "flame.fill");
        store.SetInstallationStatus(failed.Id, InstanceInstallationState.Failed, pauseRequested: false, "mojang.http", "Preview failure");

        return page switch
        {
            PreviewPage.Content or PreviewPage.ResourcePacks or PreviewPage.Catalog
                or PreviewPage.Datapacks or PreviewPage.DatapackCatalog => fabric.Id,
            _ => vanilla.Id
        };
    }

    private static void SeedContent(InstanceStore store, GameInstance instance)
    {
        var root = store.GetInstanceDirectory(instance.Id);
        var mods = Path.Combine(root, "minecraft", "mods");
        var packs = Path.Combine(root, "minecraft", "resourcepacks");
        var disabledPacks = Path.Combine(root, "minecraft", ".hako-disabled-resourcepacks");
        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(packs);
        Directory.CreateDirectory(disabledPacks);
        File.WriteAllBytes(Path.Combine(mods, "preview-mod.jar"), OldModBytes);
        File.WriteAllBytes(Path.Combine(mods, "preview-local.jar.disabled"), Encoding.UTF8.GetBytes("Local disabled preview mod"));
        File.WriteAllBytes(Path.Combine(packs, "preview-pack.zip"), OldPackBytes);
        File.WriteAllBytes(Path.Combine(disabledPacks, "preview-disabled-pack.zip"), Encoding.UTF8.GetBytes("Disabled preview pack"));

        WriteOriginRegistry(root, ".hako-mods.json", "preview-mod.jar", ModId, ModOldVersion,
            "https://modrinth.com/mod/preview-mod", "Preview 1.0", OldModBytes);
        WriteOriginRegistry(root, ".hako-resourcepacks.json", "preview-pack.zip", PackId, PackOldVersion,
            "https://modrinth.com/resourcepack/preview-pack", "Preview 1.0", OldPackBytes);
    }

    private static void SeedWorlds(InstanceStore store, GameInstance instance)
    {
        var root = store.GetInstanceDirectory(instance.Id);
        var saves = Path.Combine(root, "minecraft", "saves");
        var survival = Path.Combine(saves, "Survival World");
        var creative = Path.Combine(saves, "Creative World");
        Directory.CreateDirectory(Path.Combine(survival, "datapacks"));
        Directory.CreateDirectory(Path.Combine(survival, ".hako-disabled-datapacks"));
        Directory.CreateDirectory(Path.Combine(creative, "datapacks", "local-folder"));
        File.WriteAllBytes(Path.Combine(survival, "level.dat"), PreviewLevelData("Survival Preview", 0,
            DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds()));
        File.WriteAllBytes(Path.Combine(creative, "level.dat"), PreviewLevelData("Creative Preview", 1,
            DateTimeOffset.UtcNow.AddDays(-9).ToUnixTimeMilliseconds()));

        var datapack = OldDatapackBytes;
        var datapackPath = Path.Combine(survival, "datapacks", "preview-datapack.zip");
        File.WriteAllBytes(datapackPath, datapack);
        File.WriteAllBytes(Path.Combine(survival, ".hako-disabled-datapacks", "disabled-local.zip"),
            PreviewDatapackZip("Disabled local datapack"));
        File.WriteAllText(Path.Combine(creative, "datapacks", "local-folder", "pack.mcmeta"),
            "{\"pack\":{\"pack_format\":48,\"description\":\"Local folder datapack\"}}", new UTF8Encoding(false));

        WriteOriginRegistryAt(Path.Combine(survival, ".hako-datapacks.json"), "preview-datapack.zip",
            DatapackId, DatapackOldVersion, "https://modrinth.com/datapack/preview-datapack", "1.0", datapack);
    }

    private static void SeedScreenshots(InstanceStore store, GameInstance instance)
    {
        var folder = Path.Combine(store.GetInstanceDirectory(instance.Id), "minecraft", "screenshots");
        Directory.CreateDirectory(folder);
        for (var index = 0; index < 4; index++)
        {
            var name = $"Preview_{index + 1:00}.png";
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, PreviewScreenshotPng(index));
            File.SetCreationTimeUtc(path, DateTime.UtcNow.AddMinutes(-index * 7));
        }
    }

    private static byte[] PreviewScreenshotPng(int variant)
    {
        const int width = 480;
        const int height = 270;
        var pixels = new byte[(width * 4 + 1) * height];
        var palette = new[]
        {
            (Sky: (byte)88, Ground: (byte)65, Sun: (byte)255),
            (Sky: (byte)50, Ground: (byte)78, Sun: (byte)235),
            (Sky: (byte)105, Ground: (byte)54, Sun: (byte)250),
            (Sky: (byte)70, Ground: (byte)88, Sun: (byte)255)
        }[variant % 4];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * 4 + 1);
            pixels[row] = 0;
            for (var x = 0; x < width; x++)
            {
                var groundLine = height * 2 / 3 + (int)(18 * Math.Sin((x + variant * 37) / 54d));
                var ground = y >= groundLine;
                var isSun = Math.Pow(x - (width * 3 / 4), 2) + Math.Pow(y - (height / 4), 2) < 34 * 34;
                var offset = row + 1 + x * 4;
                pixels[offset] = isSun ? palette.Sun : ground ? palette.Ground : (byte)(palette.Sky + y / 9);
                pixels[offset + 1] = isSun ? (byte)220 : ground ? (byte)(105 + variant * 8) : (byte)(145 + y / 8);
                pixels[offset + 2] = isSun ? (byte)85 : ground ? (byte)(48 + variant * 6) : (byte)(205 + y / 12);
                pixels[offset + 3] = 255;
                if (ground && x > 85 && x < 188 && y < groundLine + 34 && ((x / 18 + y / 14) % 3 == 0))
                {
                    pixels[offset] = (byte)(78 + variant * 8);
                    pixels[offset + 1] = (byte)(132 + variant * 5);
                    pixels[offset + 2] = (byte)(62 + variant * 4);
                }
            }
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 6;
        WritePngChunk(png, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(pixels);
        WritePngChunk(png, "IDAT", compressed.ToArray());
        WritePngChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WritePngChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);
        output.Write(typeBytes);
        output.Write(data);
        var crc = uint.MaxValue;
        foreach (var value in typeBytes) crc = Crc32(crc, value);
        foreach (var value in data) crc = Crc32(crc, value);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, ~crc);
        output.Write(checksum);
    }

    private static uint Crc32(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc & 1) != 0 ? 0xedb88320U ^ (crc >> 1) : crc >> 1;
        return crc;
    }

    private static byte[] PreviewLevelData(string name, int gameType, long lastPlayed)
    {
        using var nbt = new MemoryStream();
        nbt.WriteByte(10);
        WriteNbtString(nbt, "");
        nbt.WriteByte(10);
        WriteNbtString(nbt, "Data");
        nbt.WriteByte(8);
        WriteNbtString(nbt, "LevelName");
        WriteNbtString(nbt, name);
        nbt.WriteByte(3);
        WriteNbtString(nbt, "GameType");
        Span<byte> intBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(intBytes, gameType);
        nbt.Write(intBytes);
        nbt.WriteByte(4);
        WriteNbtString(nbt, "LastPlayed");
        Span<byte> longBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(longBytes, lastPlayed);
        nbt.Write(longBytes);
        nbt.WriteByte(10);
        WriteNbtString(nbt, "Version");
        nbt.WriteByte(8);
        WriteNbtString(nbt, "Name");
        WriteNbtString(nbt, "1.21.4");
        nbt.WriteByte(0);
        nbt.WriteByte(0);
        nbt.WriteByte(0);

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(nbt.GetBuffer(), 0, checked((int)nbt.Length));
        return output.ToArray();
    }

    private static byte[] PreviewDatapackZip(string description)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var metadata = archive.CreateEntry("pack.mcmeta", CompressionLevel.Fastest);
            using (var writer = new StreamWriter(metadata.Open(), new UTF8Encoding(false)))
                writer.Write($"{{\"pack\":{{\"pack_format\":48,\"description\":\"{description}\"}}}}");
            var function = archive.CreateEntry("data/hako_preview/function/tick.mcfunction", CompressionLevel.Fastest);
            using var functionWriter = new StreamWriter(function.Open(), new UTF8Encoding(false));
            functionWriter.Write("say Hako preview datapack");
        }
        return output.ToArray();
    }

    private static byte[] PreviewModpackArchive()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("modrinth.index.json", CompressionLevel.Fastest);
            using (var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false)))
            {
                writer.Write("""{"formatVersion":1,"game":"minecraft","versionId":"preview-pack","name":"Preview Modpack","files":[],"dependencies":{"minecraft":"1.21.4"}}""");
            }

            var config = archive.CreateEntry("overrides/config/hako-preview.txt", CompressionLevel.Fastest);
            using var configWriter = new StreamWriter(config.Open(), new UTF8Encoding(false));
            configWriter.Write("Hako preview modpack configuration");
        }

        return output.ToArray();
    }

    private static byte[] ReadPreviewModpackIcon()
    {
        try { return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "StoreLogo.png")); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static string BuildCatalogJson()
    {
        var versions = new[] { ("25w02a", "snapshot"), ("1.21.4", "release"), ("1.20.1", "release"), ("b1.7.3", "old_beta") };
        var entries = versions.Select((version, index) =>
        {
            var metadata = MojangMetadataBytes(version.Item1);
            var sha1 = Convert.ToHexString(SHA1.HashData(metadata)).ToLowerInvariant();
            return new MojangVersionEntry
            {
                Id = version.Item1,
                Type = version.Item2,
                Url = $"https://piston-meta.mojang.com/v1/packages/{index + 1}/{Uri.EscapeDataString(version.Item1)}.json",
                Sha1 = sha1
            };
        }).ToArray();
        return JsonSerializer.Serialize(new MojangCatalog
        {
            Latest = new MojangLatestVersions { Release = "1.21.4", Snapshot = "25w02a" },
            Versions = entries.ToList()
        }, MojangJsonContext.Default.MojangCatalog);
    }

    private static byte[] MojangMetadataBytes(string id) => JsonSerializer.SerializeToUtf8Bytes(new MojangVersionMetadata
    {
        Id = id,
        Downloads = new Dictionary<string, MojangDownload>(StringComparer.OrdinalIgnoreCase)
        {
            ["client"] = new MojangDownload
            {
                Url = "https://piston-meta.mojang.com/client.bin",
                Sha1 = new string('0', 40),
                Size = 0
            }
        },
        Libraries = [],
        JavaVersion = new MojangJavaRequirement { Component = "java-runtime", MajorVersion = 21 },
        MainClass = "net.minecraft.client.main.Main",
        Arguments = new Dictionary<string, List<MojangArgument>>(StringComparer.Ordinal)
        {
            ["game"] = [],
            ["jvm"] = []
        }
    }, MojangJsonContext.Default.MojangVersionMetadata);

    private static byte[] MojangRuntimeManifestBytes() => JsonSerializer.SerializeToUtf8Bytes(new MojangJavaRuntimeManifest
    {
        Files = new Dictionary<string, MojangJavaRuntimeEntry>(StringComparer.Ordinal)
        {
            ["bin/javaw.exe"] = new MojangJavaRuntimeEntry
            {
                Type = "file",
                Executable = true,
                Downloads = new Dictionary<string, MojangDownload>(StringComparer.Ordinal)
                {
                    ["raw"] = new MojangDownload
                    {
                        Url = "https://piston-meta.mojang.com/javaw.exe",
                        Sha1 = new string('0', 40),
                        Size = 0
                    }
                }
            }
        }
    }, MojangJsonContext.Default.MojangJavaRuntimeManifest);

    private static string MojangRuntimeIndexJson()
    {
        var runtime = MojangRuntimeManifestBytes();
        var sha1 = Convert.ToHexString(SHA1.HashData(runtime)).ToLowerInvariant();
        var index = new Dictionary<string, Dictionary<string, List<MojangJavaRuntimePackage>>>(StringComparer.Ordinal)
        {
            ["windows-x64"] = new Dictionary<string, List<MojangJavaRuntimePackage>>(StringComparer.Ordinal)
            {
                ["java-runtime"] =
                {
                    new MojangJavaRuntimePackage
                    {
                        Manifest = new MojangDownload
                        {
                            Url = "https://piston-meta.mojang.com/runtime.json",
                            Sha1 = sha1,
                            Size = runtime.Length
                        },
                        Version = new MojangRuntimeVersion { Name = "21.0.1", Released = "2026-01-01T00:00:00Z" }
                    }
                }
            }
        };
        var typeInfo = MojangJsonContext.Default.GetTypeInfo(index.GetType())
            ?? throw new InvalidOperationException("The runtime index fixture must be registered for source generation.");
        return JsonSerializer.Serialize(index, typeInfo);
    }

    private static string ModpackSearchJson() => JsonSerializer.Serialize(new ModrinthSearchPage
    {
        Hits = [new ModrinthProject
        {
            Id = ModpackId,
            Slug = "preview-modpack",
            Title = "Hako Preview Modpack",
            Description = "A local fixture for the ready-made Modrinth modpack flow.",
            ProjectType = "modpack",
            IconUrl = new Uri("https://cdn.modrinth.fixture/icons/preview.png")
        }],
        TotalHits = 1
    }, HakoJsonContext.Default.ModrinthSearchPage);

    private static string ModpackVersionJson()
    {
        var bytes = ModpackArchiveBytes;
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        var sha512 = Hash(bytes);
        return JsonSerializer.Serialize(new[]
        {
            new ModrinthVersion
            {
                Id = ModpackVersionId,
                ProjectId = ModpackId,
                VersionNumber = "1.0.0",
                VersionType = "release",
                DatePublished = ReleaseDate,
                GameVersions = ["1.21.4"],
                Loaders = ["minecraft"],
                Files =
                [
                    new ModrinthFile
                    {
                        Filename = "preview-modpack.mrpack",
                        Url = new Uri("https://cdn.modrinth.com/files/preview-modpack.mrpack"),
                        Size = bytes.Length,
                        Hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["sha1"] = sha1,
                            ["sha512"] = sha512
                        },
                        Primary = true
                    }
                ],
                Dependencies = []
            }
        }, HakoJsonContext.Default.ModrinthVersionArray);
    }

    private static void WriteNbtString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)bytes.Length));
        stream.Write(length);
        stream.Write(bytes);
    }

    private static void WriteOriginRegistry(string instanceRoot, string registryFilename, string contentFilename,
        string projectId, string versionId,
        string pageUrl, string versionNumber, byte[] data)
    {
        WriteOriginRegistryAt(Path.Combine(instanceRoot, "minecraft", registryFilename), contentFilename,
            projectId, versionId, pageUrl, versionNumber, data);
    }

    private static void WriteOriginRegistryAt(string path, string contentFilename, string projectId, string versionId,
        string pageUrl, string versionNumber, byte[] data)
    {
        var hash = Hash(data);
        var json = $"{{\"apiProvisioned\":false,\"files\":{{\"{contentFilename}\":{{\"source\":\"modrinth\",\"projectID\":\"{projectId}\",\"versionID\":\"{versionId}\",\"pageURL\":\"{pageUrl}\",\"sha512\":\"{hash}\",\"versionNumber\":\"{versionNumber}\",\"iconURL\":null,\"fileType\":null,\"api\":null}}}}}}";
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA512.HashData(data)).ToLowerInvariant();

    private static string SearchJson(bool resourcePack, int offset)
    {
        const int pageSize = 20;
        const int projectCount = 25;
        var hits = new StringBuilder("[ ");
        var end = Math.Min(offset + pageSize, projectCount);
        for (var index = offset; index < end; index++)
        {
            if (index > offset) hits.Append(',');
            hits.Append(ProjectJson(ProjectId(resourcePack, index), ProjectSlug(resourcePack, index),
                ProjectTitle(resourcePack, index), resourcePack ? "resourcepack" : "mod",
                index == 0 ? "A preview project with an installed update available." : "A local fixture project for catalog browsing."));
        }

        hits.Append(']');
        return $"{{\"hits\":{hits},\"total_hits\":{projectCount}}}";
    }

    private static string DatapackSearchJson(int offset)
    {
        var projects = new[]
        {
            ProjectJson(DatapackId, "preview-datapack", "Preview datapack with dependencies",
                "datapack", "A fixture datapack with required dependencies and linked resource packs."),
            ProjectJson(DatapackDependencyId, "preview-datapack-dependency", "Preview datapack dependency",
                "datapack", "A required fixture dependency.")
        };
        return $"{{\"hits\":[{string.Join(',', projects.Skip(offset))}],\"total_hits\":{projects.Length}}}";
    }

    private static string ProjectId(bool resourcePack, int index) => index == 0
        ? resourcePack ? PackId : ModId
        : (resourcePack ? "HakoR" : "HakoM") + index.ToString("D5", CultureInfo.InvariantCulture);

    private static string ProjectSlug(bool resourcePack, int index) => index == 0
        ? resourcePack ? "preview-pack" : "preview-mod"
        : "fixture-" + ProjectId(resourcePack, index).ToLowerInvariant();

    private static string ProjectTitle(bool resourcePack, int index) => index == 0
        ? resourcePack ? "Preview resource pack" : "Preview mod"
        : $"Fixture {(resourcePack ? "resource pack" : "mod")} {index + 1}";

    private static string ProjectJson(string id, string slug, string title, string type, string description) =>
        $"{{\"id\":\"{id}\",\"slug\":\"{slug}\",\"title\":\"{title}\",\"description\":\"{description}\",\"icon_url\":null,\"project_type\":\"{type}\",\"all_project_types\":[\"{type}\"]}}";

    private static string VersionsJson(string id)
    {
        if (id == DatapackId)
            return "[" + DatapackVersionJson(DatapackOldVersion, DatapackId, "1.0", OldDate,
                       "preview-datapack.zip", OldDatapackBytes, includePacks: true, includeDependency: true)
                   + "," + DatapackVersionJson(DatapackReleaseVersion, DatapackId, "2.0", ReleaseDate,
                       "preview-datapack.zip", NewDatapackBytes, includePacks: true, includeDependency: true) + "]";
        if (id == DatapackDependencyId)
            return "[" + DatapackVersionJson("HakoDD01", DatapackDependencyId, "1.0", ReleaseDate,
                "preview-dependency.zip", NewDatapackBytes, includePacks: false, includeDependency: false) + "]";

        var resourcePack = id == PackId || id.StartsWith("HakoR", StringComparison.Ordinal);
        if (id == ModId)
        {
            return "[" + string.Join(',',
                VersionJson(false, ModOldVersion, ModId, "1.0", "release", OldDate, "preview-mod.jar", "https://cdn.modrinth.fixture/files/preview-mod-v1.jar", OldModBytes),
                VersionJson(false, ModReleaseVersion, ModId, "2.0", "release", ReleaseDate, "preview-mod.jar", "https://cdn.modrinth.fixture/files/preview-mod-v2.jar", NewModBytes),
                VersionJson(false, ModBetaVersion, ModId, "2.1-beta.1", "beta", ReleaseDate.AddDays(-1), "preview-mod.jar", "https://cdn.modrinth.fixture/files/preview-mod-beta.jar", BetaModBytes),
                VersionJson(false, ModAlphaVersion, ModId, "2.2-alpha.1", "alpha", ReleaseDate.AddDays(-2), "preview-mod.jar", "https://cdn.modrinth.fixture/files/preview-mod-alpha.jar", AlphaModBytes)) + "]";
        }

        if (id == PackId)
        {
            return "[" + string.Join(',',
                VersionJson(true, PackOldVersion, PackId, "1.0", "release", OldDate, "preview-pack.zip", "https://cdn.modrinth.fixture/files/preview-pack-v1.zip", OldPackBytes),
                VersionJson(true, PackReleaseVersion, PackId, "2.0", "release", ReleaseDate, "preview-pack.zip", "https://cdn.modrinth.fixture/files/preview-pack-v2.zip", NewPackBytes),
                VersionJson(true, PackBetaVersion, PackId, "2.1-beta.1", "beta", ReleaseDate.AddDays(-1), "preview-pack.zip", "https://cdn.modrinth.fixture/files/preview-pack-beta.zip", BetaPackBytes),
                VersionJson(true, PackAlphaVersion, PackId, "2.2-alpha.1", "alpha", ReleaseDate.AddDays(-2), "preview-pack.zip", "https://cdn.modrinth.fixture/files/preview-pack-alpha.zip", AlphaPackBytes)) + "]";
        }

        var genericId = id;
        var genericVersion = "V" + id;
        var genericFile = "fixture-" + id.ToLowerInvariant() + (resourcePack ? ".zip" : ".jar");
        return "[" + VersionJson(resourcePack, genericVersion, genericId, "1.0", "release", ReleaseDate,
            genericFile, "https://cdn.modrinth.fixture/files/" + genericFile, resourcePack ? NewPackBytes : NewModBytes) + "]";
    }

    private static string VersionJson(bool resourcePack, string versionId, string projectId, string number,
        string channel, DateTimeOffset published, string filename, string url, byte[] data)
    {
        var fileType = resourcePack ? "null" : "null";
        return $"{{\"id\":\"{versionId}\",\"project_id\":\"{projectId}\",\"version_number\":\"{number}\",\"version_type\":\"{channel}\",\"date_published\":\"{published:O}\",\"game_versions\":[\"1.21.4\"],\"loaders\":[\"{(resourcePack ? "minecraft" : "fabric")}\"],\"files\":[{{\"filename\":\"{filename}\",\"url\":\"{url}\",\"size\":{data.Length},\"hashes\":{{\"sha1\":\"{Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant()}\",\"sha512\":\"{Hash(data)}\"}},\"primary\":true,\"file_type\":{fileType}}}],\"dependencies\":[]}}";
    }

    private static string DatapackVersionJson(string versionId, string projectId, string number,
        DateTimeOffset published, string filename, byte[] data, bool includePacks, bool includeDependency)
    {
        var downloadName = projectId == DatapackDependencyId ? "preview-dependency.zip"
            : versionId == DatapackOldVersion ? "preview-datapack-v1.zip" : "preview-datapack-v2.zip";
        var files = new List<string> { FileJson(filename, "https://cdn.modrinth.fixture/files/" + downloadName, data, null, true) };
        if (includePacks)
        {
            files.Add(FileJson("preview-linked-required.zip", "https://cdn.modrinth.fixture/files/preview-linked-required.zip",
                RequiredLinkedPackBytes, "required-resource-pack", false));
            files.Add(FileJson("preview-linked-optional.zip", "https://cdn.modrinth.fixture/files/preview-linked-optional.zip",
                OptionalLinkedPackBytes, "optional-resource-pack", false));
        }

        var dependencies = includeDependency
            ? $"[{{\"project_id\":\"{DatapackDependencyId}\",\"dependency_type\":\"required\"}}]"
            : "[]";
        return $"{{\"id\":\"{versionId}\",\"project_id\":\"{projectId}\",\"version_number\":\"{number}\",\"version_type\":\"release\",\"date_published\":\"{published:O}\",\"game_versions\":[\"1.21.4\"],\"loaders\":[\"minecraft\"],\"files\":[{string.Join(',', files)}],\"dependencies\":{dependencies}}}";
    }

    private static string FileJson(string filename, string url, byte[] data, string? fileType, bool primary) =>
        $"{{\"filename\":\"{filename}\",\"url\":\"{url}\",\"size\":{data.Length},\"hashes\":{{\"sha1\":\"{Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant()}\",\"sha512\":\"{Hash(data)}\"}},\"primary\":{primary.ToString().ToLowerInvariant()},\"file_type\":{(fileType is null ? "null" : $"\"{fileType}\"")}}}";

    private static string HashLookupJson() =>
        $"{{\"{Hash(OldModBytes)}\":{{\"id\":\"{ModOldVersion}\",\"project_id\":\"{ModId}\",\"date_published\":\"{OldDate:O}\"}}," +
        $"\"{Hash(OldPackBytes)}\":{{\"id\":\"{PackOldVersion}\",\"project_id\":\"{PackId}\",\"date_published\":\"{OldDate:O}\"}}," +
        $"\"{Hash(OldDatapackBytes)}\":{{\"id\":\"{DatapackOldVersion}\",\"project_id\":\"{DatapackId}\",\"date_published\":\"{OldDate:O}\"}}}}";

    private static string UpdateLookupJson() =>
        $"{{\"{Hash(OldModBytes)}\":{VersionJson(false, ModReleaseVersion, ModId, "2.0", "release", ReleaseDate, "preview-mod.jar", "https://cdn.modrinth.fixture/files/preview-mod-v2.jar", NewModBytes)}," +
        $"\"{Hash(OldPackBytes)}\":{VersionJson(true, PackReleaseVersion, PackId, "2.0", "release", ReleaseDate, "preview-pack.zip", "https://cdn.modrinth.fixture/files/preview-pack-v2.zip", NewPackBytes)}," +
        $"\"{Hash(OldDatapackBytes)}\":{DatapackVersionJson(DatapackReleaseVersion, DatapackId, "2.0", ReleaseDate, "preview-datapack.zip", NewDatapackBytes, includePacks: true, includeDependency: true)}}}";

    private static string ProjectsJson() => "[" + string.Join(',',
        Enumerable.Range(0, 25).SelectMany(index => new[]
            {
                ProjectJson(ProjectId(false, index), ProjectSlug(false, index), ProjectTitle(false, index), "mod", "Preview Modrinth fixture project."),
                ProjectJson(ProjectId(true, index), ProjectSlug(true, index), ProjectTitle(true, index), "resourcepack", "Preview Modrinth fixture project.")
            })
            .Append(ProjectJson(DatapackId, "preview-datapack", "Preview datapack with dependencies", "datapack", "Fixture datapack."))
            .Append(ProjectJson(DatapackDependencyId, "preview-datapack-dependency", "Preview datapack dependency", "datapack", "Fixture dependency."))) + "]";

    private static string VersionsByIdJson() => "[" + string.Join(',',
        VersionsJson(ModId).Trim('[', ']'), VersionsJson(PackId).Trim('[', ']'),
        VersionsJson(DatapackId).Trim('[', ']'), VersionsJson(DatapackDependencyId).Trim('[', ']')) + "]";

    private static byte[] DownloadBytes(string path) => path switch
    {
        "/files/preview-mod-v1.jar" => OldModBytes,
        "/files/preview-mod-v2.jar" => NewModBytes,
        "/files/preview-mod-beta.jar" => BetaModBytes,
        "/files/preview-mod-alpha.jar" => AlphaModBytes,
        "/files/preview-pack-v1.zip" => OldPackBytes,
        "/files/preview-pack-v2.zip" => NewPackBytes,
        "/files/preview-pack-beta.zip" => BetaPackBytes,
        "/files/preview-pack-alpha.zip" => AlphaPackBytes,
        "/files/preview-datapack-v1.zip" => OldDatapackBytes,
        "/files/preview-datapack-v2.zip" => NewDatapackBytes,
        "/files/preview-dependency.zip" => NewDatapackBytes,
        "/files/preview-linked-required.zip" => RequiredLinkedPackBytes,
        "/files/preview-linked-optional.zip" => OptionalLinkedPackBytes,
        _ when path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) => NewPackBytes,
        _ => NewModBytes
    };

    private sealed class ContentHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            if (uri is null) return new HttpResponseMessage(HttpStatusCode.BadRequest);
            if (uri.AbsoluteUri == MojangClient.CatalogUrl) return Json(CatalogJson);
            if (uri.AbsoluteUri == MojangClient.RuntimeIndexUrl) return Json(MojangRuntimeIndexJson());
            if (uri.AbsolutePath == "/runtime.json")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(MojangRuntimeManifestBytes()) };
            if (uri.AbsolutePath.StartsWith("/v1/packages/", StringComparison.Ordinal))
            {
                var id = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(MojangMetadataBytes(id)) };
            }
            if (uri.Host == "cdn.modrinth.fixture")
            {
                if (uri.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && ModpackIconBytes.Length > 0)
                {
                    var image = new ByteArrayContent(ModpackIconBytes);
                    image.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = image };
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(DownloadBytes(uri.AbsolutePath)) };
            }
            if (uri.Host == "cdn.modrinth.com")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ModpackArchiveBytes) };
            if (uri.Host == "fabric.fixture") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

            var path = uri.AbsolutePath;
            if (path == "/v2/search")
            {
                var offset = QueryInt(uri.Query, "offset");
                if (Uri.UnescapeDataString(uri.Query).Contains("project_type:modpack", StringComparison.Ordinal))
                    return Json(ModpackSearchJson());
                if (uri.Query.Contains("datapack", StringComparison.OrdinalIgnoreCase))
                    return Json(DatapackSearchJson(offset));
                var resourcePack = uri.Query.Contains("resourcepack", StringComparison.OrdinalIgnoreCase);
                return Json(SearchJson(resourcePack, offset));
            }

            if (path == "/v2/version_files" && request.Method == HttpMethod.Post)
                return Json(HashLookupJson());
            if (path == "/v2/version_files/update" && request.Method == HttpMethod.Post)
                return Json(UpdateLookupJson());
            if (path == "/v2/projects") return Json(ProjectsJson());
            if (path == "/v2/versions") return Json(VersionsByIdJson());
            if (path.StartsWith("/v2/project/", StringComparison.Ordinal) && path.EndsWith("/version", StringComparison.Ordinal))
            {
                var projectId = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[^2];
                if (projectId == ModpackId) return Json(ModpackVersionJson());
                return Json(VersionsJson(projectId));
            }

            await Task.CompletedTask;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        private static int QueryInt(string query, string name)
        {
            foreach (var component in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = component.Split('=', 2);
                if (parts.Length == 2 && Uri.UnescapeDataString(parts[0]) == name
                    && int.TryParse(Uri.UnescapeDataString(parts[1]), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                    return Math.Max(0, value);
            }

            return 0;
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };
    }
}

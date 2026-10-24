using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;

namespace Hako.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class RestoreArchiveTests
{
    private static readonly byte[] ClientJar = [0x50, 0x4b, 0x03, 0x04, 1, 2, 3, 4];
    private static readonly byte[] IconPNG = [137, 80, 78, 71, 13, 10, 26, 10];

    [TestMethod]
    public async Task SwiftManifestAndMacJavaLinksPrepareWithoutCreatingJavaLinks()
    {
        await using var temporary = new TemporaryDirectory();
        var store = temporary.CreateStore();
        await store.LoadAsync();
        var existing = store.Create(new InstanceDraft { Name = "Build" }, "1.20.4");
        store.SetInstallationStatus(existing.Id, InstanceInstallationState.Ready, pauseRequested: false);
        var archivePath = Path.Combine(temporary.Root, "mac.hakobackup");
        var fixture = CreateFixture(existing.Id, javaExecutable: "Contents/Home/bin/java");
        var swiftJson = SwiftJson(fixture.Manifest);
        await WriteInstanceArchiveAsync(archivePath, fixture, dataJson: swiftJson, entries =>
        {
            AddEntry(entries, "java/Contents/Home/bin/java", [0xcf, 0xfa, 0xed, 0xfe]);
            AddEntry(entries, "java/Contents/Home/lib/runtime", Encoding.UTF8.GetBytes("../libjli.dylib"),
                unchecked((int)((0xA000u | 0x1FFu) << 16)));
        });

        var result = await CreateRestore(store).PrepareAsync(archivePath);

        Assert.IsTrue(result.Succeeded, result.Failure?.Diagnostic);
        Assert.AreEqual(RestoreArchiveKind.Instance, result.Staged!.Kind);
        Assert.AreEqual(existing.Id, result.Staged.InstanceManifest!.InstanceID);
        Assert.AreEqual(existing.Id, result.Staged.MatchingInstance!.Id);
        Assert.IsTrue(result.Staged.NeedsJavaInstallation);
        Assert.IsFalse(Directory.Exists(Path.Combine(result.Staged.DirectoryPath, "java")));
        await CreateRestore(store).DiscardAsync(result.Staged);
        Assert.IsFalse(Directory.Exists(result.Staged.DirectoryPath));
    }

    [TestMethod]
    public async Task InstanceBackupWritesInstanceIdAndLegacyManifestMatchesByProfile()
    {
        await using var temporary = new TemporaryDirectory();
        var store = temporary.CreateStore();
        await store.LoadAsync();
        var existing = store.Create(new InstanceDraft { Name = "Build" }, "1.20.4");
        store.SetInstallationStatus(existing.Id, InstanceInstallationState.Ready, pauseRequested: false);
        var content = new InstanceContent(store);
        var backup = new InstanceBackup(store, content);
        var made = await backup.CreateAsync(existing.Id);
        Assert.IsTrue(made.Succeeded, made.Failure?.Diagnostic);
        using (var file = File.OpenRead(made.Path!))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Read))
        using (var json = await JsonDocument.ParseAsync(zip.GetEntry("data.json")!.Open()))
        {
            Assert.AreEqual(existing.Id, json.RootElement.GetProperty("instanceID").GetGuid());
        }

        var archivePath = Path.Combine(temporary.Root, "legacy.hakobackup");
        var legacy = CreateFixture(instanceID: null, javaExecutable: "Contents/Home/bin/java");
        await WriteInstanceArchiveAsync(archivePath, legacy);

        var result = await CreateRestore(store).PrepareAsync(archivePath);

        Assert.IsTrue(result.Succeeded, result.Failure?.Diagnostic);
        Assert.AreEqual(existing.Id, result.Staged!.MatchingInstance!.Id);
        Assert.IsFalse(result.Staged.InstanceManifest!.InstanceID.HasValue);
        await CreateRestore(store).DiscardAsync(result.Staged);
    }

    [TestMethod]
    [DataRow("../escape.txt")]
    [DataRow("minecraft:stream")]
    [DataRow("CON.txt")]
    [DataRow("trailing. ")]
    [DataRow("minecraft//mods/a.jar")]
    public async Task UnsafeArchivePathsAreRejectedBeforeStaging(string path)
    {
        await using var temporary = new TemporaryDirectory();
        var store = temporary.CreateStore();
        await store.LoadAsync();
        var archivePath = Path.Combine(temporary.Root, "unsafe.hakobackup");
        var fixture = CreateFixture();
        await WriteInstanceArchiveAsync(archivePath, fixture,
            additionalEntries: entries => AddEntry(entries, path, [1, 2, 3]));

        var result = await CreateRestore(store).PrepareAsync(archivePath);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(RestoreArchiveFailureReason.UnsafePath, result.Failure!.Reason);
        AssertNoStaging(store);
    }

    [TestMethod]
    public async Task DuplicatePathsByCaseAndUnicodeNormalizationAreRejected()
    {
        await using var temporary = new TemporaryDirectory();
        var store = temporary.CreateStore();
        await store.LoadAsync();
        var fixture = CreateFixture();
        foreach (var duplicate in new[]
                 {
                     new[] { "minecraft/config/Case.txt", "minecraft/config/case.txt" },
                     new[] { "minecraft/config/\u00e9.txt", "minecraft/config/e\u0301.txt" }
                 })
        {
            var archivePath = Path.Combine(temporary.Root, Guid.NewGuid().ToString("N") + ".hakobackup");
            await WriteInstanceArchiveAsync(archivePath, fixture, additionalEntries: entries =>
            {
                AddEntry(entries, duplicate[0], [1]);
                AddEntry(entries, duplicate[1], [2]);
            });

            var result = await CreateRestore(store).PrepareAsync(archivePath);
            Assert.AreEqual(RestoreArchiveFailureReason.UnsafePath, result.Failure?.Reason);
        }
    }

    [TestMethod]
    public async Task SymlinkOutsideJavaIsRejected()
    {
        await using var temporary = new TemporaryDirectory();
        var store = temporary.CreateStore();
        await store.LoadAsync();
        var archivePath = Path.Combine(temporary.Root, "symlink.hakobackup");
        await WriteInstanceArchiveAsync(archivePath, CreateFixture(), additionalEntries: entries =>
            AddEntry(entries, "minecraft/config/link", Encoding.UTF8.GetBytes("../../outside"),
                unchecked((int)((0xA000u | 0x1FFu) << 16))));

        var result = await CreateRestore(store).PrepareAsync(archivePath);

        Assert.AreEqual(RestoreArchiveFailureReason.UnsafePath, result.Failure?.Reason);
    }

    [TestMethod]
    public async Task EncryptedArchiveIsRejected()
    {
        await using var temporary = new TemporaryDirectory();
        var store = temporary.CreateStore();
        await store.LoadAsync();
        var archivePath = Path.Combine(temporary.Root, "encrypted.hakobackup");
        await WriteInstanceArchiveAsync(archivePath, CreateFixture());
        SetZipFlags(archivePath, "data.json", 1);

        var result = await CreateRestore(store).PrepareAsync(archivePath);

        Assert.AreEqual(RestoreArchiveFailureReason.InvalidArchive, result.Failure?.Reason);
    }

    [TestMethod]
    public async Task CorruptEntryCrcAndDeclaredSizeAreRejected()
    {
        await using var temporary = new TemporaryDirectory();
        var store = temporary.CreateStore();
        await store.LoadAsync();
        var fixture = CreateFixture();

        var badCrcPath = Path.Combine(temporary.Root, "bad-crc.hakobackup");
        await WriteInstanceArchiveAsync(badCrcPath, fixture, additionalEntries: entries => AddEntry(entries, "corrupt.bin", [17, 23, 42]));
        MutatePayload(badCrcPath, [17, 23, 42]);
        var badCrc = await CreateRestore(store).PrepareAsync(badCrcPath);
        Assert.AreEqual(RestoreArchiveFailureReason.InvalidArchive, badCrc.Failure?.Reason);
        AssertNoStaging(store);

        var badSizePath = Path.Combine(temporary.Root, "bad-size.hakobackup");
        await WriteInstanceArchiveAsync(badSizePath, fixture, additionalEntries: entries => AddEntry(entries, "sized.bin", [31, 37, 41]));
        SetLocalDeclaredLength(badSizePath, "sized.bin", 4);
        var badSize = await CreateRestore(store).PrepareAsync(badSizePath);
        Assert.AreEqual(RestoreArchiveFailureReason.InvalidArchive, badSize.Failure?.Reason);
    }

    [TestMethod]
    public async Task MalformedManifestAndMinecraftSha1MismatchAreRejected()
    {
        await using var temporary = new TemporaryDirectory();
        var store = temporary.CreateStore();
        await store.LoadAsync();

        var malformedPath = Path.Combine(temporary.Root, "malformed.hakobackup");
        await WriteInstanceArchiveAsync(malformedPath, CreateFixture(), dataJson: Encoding.UTF8.GetBytes("{"));
        var malformed = await CreateRestore(store).PrepareAsync(malformedPath);
        Assert.AreEqual(RestoreArchiveFailureReason.InvalidManifest, malformed.Failure?.Reason);

        var shaPath = Path.Combine(temporary.Root, "bad-sha.hakobackup");
        var fixture = CreateFixture();
        await WriteInstanceArchiveAsync(shaPath, fixture with { VersionJson = Encoding.UTF8.GetBytes("{}") });
        var sha = await CreateRestore(store).PrepareAsync(shaPath);
        Assert.AreEqual(RestoreArchiveFailureReason.InvalidInstance, sha.Failure?.Reason);
        AssertNoStaging(store);
    }

    [TestMethod]
    public async Task InsufficientSpaceIsTypedAndDoesNotLeaveStaging()
    {
        await using var temporary = new TemporaryDirectory();
        var store = temporary.CreateStore();
        await store.LoadAsync();
        var archivePath = Path.Combine(temporary.Root, "space.hakobackup");
        await WriteInstanceArchiveAsync(archivePath, CreateFixture());

        var result = await new RestoreArchive(store, new FixedArchitectureProvider(), _ => 0).PrepareAsync(archivePath);

        Assert.AreEqual(RestoreArchiveFailureReason.InsufficientSpace, result.Failure?.Reason);
        AssertNoStaging(store);
    }

    private static RestoreArchive CreateRestore(InstanceStore store) =>
        new(store, new FixedArchitectureProvider(), _ => long.MaxValue);

    private static void AssertNoStaging(InstanceStore store) =>
        Assert.AreEqual(0, Directory.Exists(store.GameRoot)
            ? Directory.GetDirectories(store.GameRoot, ".hako-restore-*").Length
            : 0);

    private static InstanceFixture CreateFixture(Guid? instanceID = null, string javaExecutable = "Contents/Home/bin/java")
    {
        var client = ClientJar;
        var version = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "1.20.4",
            downloads = new
            {
                client = new
                {
                    url = "https://launcher.mojang.com/v1/objects/fixture/client.jar",
                    sha1 = Convert.ToHexString(SHA1.HashData(client)).ToLowerInvariant(),
                    size = client.Length
                }
            },
            libraries = Array.Empty<object>(),
            javaVersion = new { component = "java-runtime-gamma", majorVersion = 21 }
        });
        var manifest = new InstanceBackupManifest
        {
            InstanceID = instanceID,
            BackupCreatedAt = new DateTimeOffset(2026, 10, 23, 14, 7, 8, TimeSpan.Zero),
            Name = "Build",
            FolderName = "Build",
            InstanceCreatedAt = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero),
            MinecraftVersion = "1.20.4",
            ModLoader = "vanilla",
            JavaMajorVersion = 21,
            IconSymbol = "shippingbox.fill",
            IconPNG = IconPNG,
            ModCount = 0,
            Mods = [],
            Profile = new InstanceBackupManifest.ProfileData(
                "https://piston-meta.mojang.com/v1/packages/fixture/1.20.4.json",
                Convert.ToHexString(SHA1.HashData(version)).ToLowerInvariant(),
                LaunchArgumentSource.Mojang,
                OfflineMode: false,
                OfflineUsername: "Player",
                new InstanceParameters(),
                javaExecutable,
                LegacyTexturepacks: false,
                FabricConfiguration: null,
                FabricProfileSHA1: null)
        };

        return new InstanceFixture(manifest, version, client);
    }

    private static byte[] SwiftJson(InstanceBackupManifest manifest)
    {
        var json = JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(
            manifest, HakoJsonContext.Default.InstanceBackupManifest))!.AsObject();
        json["profile"]!["javaExecutable"] = "Contents/Home/bin/java";
        json["profileExtra"] = true;
        return Encoding.UTF8.GetBytes(json.ToJsonString());
    }

    private static async Task WriteInstanceArchiveAsync(
        string path,
        InstanceFixture fixture,
        byte[]? dataJson = null,
        Action<ZipArchive>? additionalEntries = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
        AddEntry(archive, "data.json", dataJson ?? JsonSerializer.SerializeToUtf8Bytes(
            fixture.Manifest, HakoJsonContext.Default.InstanceBackupManifest));
        AddEntry(archive, "icon.png", fixture.Manifest.IconPNG!);
        AddEntry(archive, "minecraft/versions/1.20.4/1.20.4.json", fixture.VersionJson);
        AddEntry(archive, "minecraft/versions/1.20.4/1.20.4.jar", fixture.ClientJar);
        additionalEntries?.Invoke(archive);
        archive.Dispose();
    }

    private static void AddEntry(ZipArchive archive, string name, byte[] contents, int? externalAttributes = null)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        if (externalAttributes is { } attributes) entry.ExternalAttributes = attributes;
        using var output = entry.Open();
        output.Write(contents);
    }

    private static void SetZipFlags(string archivePath, string entryName, ushort flags)
    {
        var bytes = File.ReadAllBytes(archivePath);
        var (centralOffset, localOffset) = FindZipEntry(bytes, entryName);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(centralOffset + 8), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(localOffset + 6), flags);
        File.WriteAllBytes(archivePath, bytes);
    }

    private static void SetLocalDeclaredLength(string archivePath, string entryName, uint length)
    {
        var bytes = File.ReadAllBytes(archivePath);
        var (_, localOffset) = FindZipEntry(bytes, entryName);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(localOffset + 22), length);
        File.WriteAllBytes(archivePath, bytes);
    }

    private static (int CentralOffset, int LocalOffset) FindZipEntry(byte[] archive, string entryName)
    {
        var wanted = Encoding.UTF8.GetBytes(entryName);
        for (var offset = 0; offset <= archive.Length - 46; offset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(offset)) != 0x02014b50) continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(offset + 28));
            if (nameLength != wanted.Length || !archive.AsSpan(offset + 46, nameLength).SequenceEqual(wanted)) continue;
            var local = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(offset + 42)));
            return (offset, local);
        }

        throw new AssertFailedException($"ZIP entry was not found: {entryName}");
    }

    private static void MutatePayload(string archivePath, byte[] payload)
    {
        var bytes = File.ReadAllBytes(archivePath);
        var offset = bytes.AsSpan().IndexOf(payload);
        Assert.IsTrue(offset >= 0, "The test payload should be present in stored ZIP data.");
        bytes[offset] ^= 0xff;
        File.WriteAllBytes(archivePath, bytes);
    }

    private sealed record InstanceFixture(InstanceBackupManifest Manifest, byte[] VersionJson, byte[] ClientJar);

    private sealed class FixedArchitectureProvider : IHostArchitectureProvider
    {
        public HostArchitecture GetHostArchitecture() => new(HostMachineArchitecture.X64, Amd64UserEnabled: true);
    }
}

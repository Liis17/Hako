using System.IO.Compression;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;

namespace Hako.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class InstanceDatapackContentTests
{
    [TestMethod]
    public async Task WorldTargetsIsolateFilesOriginsAndDisabledStateWithoutChangingLevelMetadata()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var worldsRoot = Path.Combine(root, "minecraft", "saves");
        var worldA = CreateWorld(worldsRoot, "WorldA", [1, 2, 3]);
        var worldB = CreateWorld(worldsRoot, "WorldB", [4, 5, 6]);
        var levelA = await File.ReadAllBytesAsync(Path.Combine(worldA, "level.dat"));
        var levelB = await File.ReadAllBytesAsync(Path.Combine(worldB, "level.dat"));
        var sourceA = Path.Combine(temporary.Root, "Pack.zip");
        var sourceB = Path.Combine(temporary.Root, "Other.zip");
        var bytesA = CreateDatapackZip("A");
        var bytesB = CreateDatapackZip("B");
        await File.WriteAllBytesAsync(sourceA, bytesA);
        await File.WriteAllBytesAsync(sourceB, bytesB);
        var content = new InstanceContent(store);
        var targetA = InstanceContentTarget.WorldDatapacks("WorldA");
        var targetB = InstanceContentTarget.WorldDatapacks("WorldB");

        await content.ImportAsync(instance.Id, targetA, sourceA);
        await content.ImportAsync(instance.Id, targetB, sourceA);
        var itemA = (await content.ListAsync(instance.Id, targetA)).Single();
        await content.WithContentLeaseAsync(instance.Id, async (leasedRoot, leasedInstance, token) =>
        {
            await content.RecordOriginUnderLeaseAsync(leasedRoot, leasedInstance, itemA, targetA, Hash(bytesA),
                Origin("version-a", bytesA), token);
        });

        var itemB = (await content.ListAsync(instance.Id, targetB)).Single();
        await content.WithContentLeaseAsync(instance.Id, async (leasedRoot, leasedInstance, token) =>
        {
            await content.RecordOriginUnderLeaseAsync(leasedRoot, leasedInstance, itemB, targetB, Hash(bytesA),
                Origin("version-b", bytesA), token);
        });
        var legacyOrigin = new Dictionary<string, object>
        {
            ["source"] = "modrinth",
            ["projectID"] = "datapack01",
            ["versionID"] = "version-a",
            ["pageURL"] = "https://modrinth.com/datapack/datapack01",
            ["sha512"] = Hash(bytesA),
            ["versionNumber"] = "version-a"
        };
        await File.WriteAllTextAsync(Path.Combine(worldA, ".hako-datapacks.json"), JsonSerializer.Serialize(new
        {
            files = new Dictionary<string, Dictionary<string, object>> { ["pack.zip"] = legacyOrigin }
        }));

        var caseVariant = Path.Combine(temporary.Root, "pack.ZIP");
        await File.WriteAllBytesAsync(caseVariant, CreateDatapackZip("collision"));
        await Assert.ThrowsExactlyAsync<IOException>(() => content.ImportAsync(instance.Id, targetA, caseVariant));
        await content.SetEnabledAsync(instance.Id, itemA, targetA, enabled: false);

        var disabledA = (await content.ListAsync(instance.Id, targetA)).Single();
        Assert.IsTrue(disabledA.DisabledPack);
        Assert.AreEqual("version-a", disabledA.Origin?.VersionID);
        Assert.IsFalse((await content.ListAsync(instance.Id, targetB)).Single().DisabledPack);
        Assert.AreEqual("version-b", (await content.ListAsync(instance.Id, targetB)).Single().Origin?.VersionID);

        var replacementFolder = Path.Combine(temporary.Root, "replacement");
        Directory.CreateDirectory(replacementFolder);
        var localReplacement = Path.Combine(replacementFolder, "Pack.zip");
        var replacementBytes = CreateDatapackZip("local replacement");
        await File.WriteAllBytesAsync(localReplacement, replacementBytes);
        await content.ImportAsync(instance.Id, targetA, localReplacement, replace: true);
        var local = (await content.ListAsync(instance.Id, targetA)).Single();
        Assert.IsTrue(local.DisabledPack);
        Assert.IsNull(local.Origin);
        Assert.AreEqual("version-b", (await content.ListAsync(instance.Id, targetB)).Single().Origin?.VersionID);

        var registryA = Path.Combine(worldA, ".hako-datapacks.json");
        var registryB = Path.Combine(worldB, ".hako-datapacks.json");
        Assert.IsTrue(File.Exists(registryA));
        Assert.IsTrue(File.Exists(registryB));
        Assert.IsTrue((await File.ReadAllTextAsync(registryA)).Contains("apiProvisioned", StringComparison.Ordinal));
        Assert.IsTrue((await File.ReadAllTextAsync(registryB)).Contains("version-b", StringComparison.Ordinal));

        await content.SetEnabledAsync(instance.Id, local, targetA, enabled: true);
        CollectionAssert.AreEqual(replacementBytes,
            await File.ReadAllBytesAsync(Path.Combine(worldA, "datapacks", "Pack.zip")));
        CollectionAssert.AreEqual(bytesA,
            await File.ReadAllBytesAsync(Path.Combine(worldB, "datapacks", "Pack.zip")));
        CollectionAssert.AreEqual(levelA, await File.ReadAllBytesAsync(Path.Combine(worldA, "level.dat")));
        CollectionAssert.AreEqual(levelB, await File.ReadAllBytesAsync(Path.Combine(worldB, "level.dat")));
    }

    [TestMethod]
    public async Task DatapackImportsValidateZipAndFolderMetadataBeforeCreatingWorldContentFolders()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var world = CreateWorld(Path.Combine(root, "minecraft", "saves"), "World", [9]);
        var target = InstanceContentTarget.WorldDatapacks("World");
        var content = new InstanceContent(store);
        var validZip = Path.Combine(temporary.Root, "valid.zip");
        await File.WriteAllBytesAsync(validZip, CreateDatapackZip("valid"));
        await content.ImportAsync(instance.Id, target, validZip);

        var folder = Path.Combine(temporary.Root, "FolderPack");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "pack.mcmeta"), "{\"pack\":{\"pack_format\":15}}");
        await File.WriteAllTextAsync(Path.Combine(folder, "data.txt"), "folder payload");
        await content.ImportAsync(instance.Id, target, folder);

        var invalidPackages = new[]
        {
            CreateZip("nested.zip", ("root/pack.mcmeta", "{\"pack\":{}}")),
            CreateZip("malformed.zip", ("pack.mcmeta", "{")),
            CreateZip("not-pack.zip", ("pack.mcmeta", "{\"pack\":[]}")),
            CreateZip("linked-metadata.zip", ("pack.mcmeta", "{\"pack\":{}}"), symlink: true),
            CreateZip("oversized.zip", ("pack.mcmeta", "{\"pack\":{}}" + new string(' ', 1024 * 1024)))
        };

        foreach (var (filename, bytes) in invalidPackages)
        {
            var source = Path.Combine(temporary.Root, filename);
            await File.WriteAllBytesAsync(source, bytes);
            await Assert.ThrowsExactlyAsync<InstanceContent.InvalidDatapackPackageException>(
                () => content.ImportAsync(instance.Id, target, source));
            Assert.IsFalse(File.Exists(Path.Combine(world, "datapacks", filename)));
        }

        var noMetadataFolder = Path.Combine(temporary.Root, "NoMetadata");
        Directory.CreateDirectory(Path.Combine(noMetadataFolder, "nested"));
        await File.WriteAllTextAsync(Path.Combine(noMetadataFolder, "nested", "pack.mcmeta"), "{\"pack\":{}}");
        await Assert.ThrowsExactlyAsync<InstanceContent.InvalidDatapackPackageException>(
            () => content.ImportAsync(instance.Id, target, noMetadataFolder));

        Assert.AreEqual(2, (await content.ListAsync(instance.Id, target)).Count);
        Assert.IsFalse(File.Exists(Path.Combine(world, ".hako-disabled-datapacks")));
    }

    [TestMethod]
    public async Task MissingOrCorruptWorldCannotBeRecreatedByDatapackImport()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var source = Path.Combine(temporary.Root, "valid.zip");
        await File.WriteAllBytesAsync(source, CreateDatapackZip("missing"));
        var target = InstanceContentTarget.WorldDatapacks("Missing");
        var failure = await Assert.ThrowsExactlyAsync<InstanceWorldException>(
            () => new InstanceContent(store).ImportAsync(instance.Id, target, source));
        Assert.AreEqual(InstanceWorldFailureReason.WorldMissing, failure.Error);
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "minecraft", "saves", "Missing")));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "minecraft", "saves")));

        var world = CreateWorld(Path.Combine(root, "minecraft", "saves"), "LostMetadata", [1]);
        File.Delete(Path.Combine(world, "level.dat"));
        await Assert.ThrowsExactlyAsync<InstanceWorldException>(
            () => new InstanceContent(store).ImportAsync(instance.Id,
                InstanceContentTarget.WorldDatapacks("LostMetadata"), source));
        Assert.IsFalse(Directory.Exists(Path.Combine(world, "datapacks")));
    }

    [TestMethod]
    public async Task DatapackReplacementChecksExpectedHashAndRollsBackRegistryFailure()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var world = CreateWorld(Path.Combine(root, "minecraft", "saves"), "World", [7, 8, 9]);
        var target = InstanceContentTarget.WorldDatapacks("World");
        var oldBytes = CreateDatapackZip("old");
        var newBytes = CreateDatapackZip("new");
        var oldSource = Path.Combine(temporary.Root, "Pack.zip");
        var newSource = Path.Combine(temporary.Root, "new.zip");
        await File.WriteAllBytesAsync(oldSource, oldBytes);
        await File.WriteAllBytesAsync(newSource, newBytes);
        var content = new InstanceContent(store);
        await content.ImportAsync(instance.Id, target, oldSource);
        var item = (await content.ListAsync(instance.Id, target)).Single();
        var origin = Origin("old", oldBytes);
        await content.WithContentLeaseAsync(instance.Id, async (leasedRoot, leasedInstance, token) =>
        {
            await content.RecordOriginUnderLeaseAsync(leasedRoot, leasedInstance, item, target, Hash(oldBytes), origin, token);
        });
        var levelBefore = await File.ReadAllBytesAsync(Path.Combine(world, "level.dat"));
        var failure = new InstanceContent(store, null, () => throw new IOException("Injected registry failure."));

        await Assert.ThrowsExactlyAsync<IOException>(() => failure.ReplaceAsync(instance.Id, item, target, newSource,
            "Pack.zip", Hash(oldBytes), Origin("new", newBytes)));
        var stale = await Assert.ThrowsExactlyAsync<IOException>(() => content.ReplaceAsync(instance.Id, item, target,
            newSource, "Pack.zip", Hash(Encoding.UTF8.GetBytes("stale")), Origin("new", newBytes)));

        StringAssert.Contains(stale.Message, "changed");
        CollectionAssert.AreEqual(oldBytes, await File.ReadAllBytesAsync(Path.Combine(world, "datapacks", "Pack.zip")));
        Assert.AreEqual("old", (await content.ListAsync(instance.Id, target)).Single().Origin?.VersionID);
        CollectionAssert.AreEqual(levelBefore, await File.ReadAllBytesAsync(Path.Combine(world, "level.dat")));
    }

    [TestMethod]
    public async Task DatapackFolderImportRejectsNestedJunctionWithoutFollowingIt()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var source = Path.Combine(temporary.Root, "FolderPack");
        var outside = Path.Combine(temporary.Root, "Outside");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(source, "pack.mcmeta"), "{\"pack\":{}}");
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "outside");
        var junction = Path.Combine(source, "linked");
        CreateJunction(junction, outside);
        var world = CreateWorld(Path.Combine(root, "minecraft", "saves"), "World", [2]);
        try
        {
            await Assert.ThrowsExactlyAsync<InstanceContent.InvalidDatapackPackageException>(() => new InstanceContent(store).ImportAsync(instance.Id,
                InstanceContentTarget.WorldDatapacks("World"), source));
            Assert.IsFalse(Directory.Exists(Path.Combine(world, "datapacks", "FolderPack")));
            Assert.AreEqual("outside", await File.ReadAllTextAsync(Path.Combine(outside, "secret.txt")));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [TestMethod]
    public async Task DatapackTargetRejectsJunctionWorldWithoutWritingThroughIt()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var saves = Path.Combine(root, "minecraft", "saves");
        var actualWorld = CreateWorld(saves, "Actual", [1, 2]);
        var linkedWorld = Path.Combine(saves, "Linked");
        CreateJunction(linkedWorld, actualWorld);
        var source = Path.Combine(temporary.Root, "pack.zip");
        await File.WriteAllBytesAsync(source, CreateDatapackZip("linked world"));
        try
        {
            var failure = await Assert.ThrowsExactlyAsync<InstanceWorldException>(() => new InstanceContent(store).ImportAsync(
                instance.Id, InstanceContentTarget.WorldDatapacks("Linked"), source));
            Assert.AreEqual(InstanceWorldFailureReason.ReparsePointNotAllowed, failure.Error);
            Assert.IsFalse(Directory.Exists(Path.Combine(actualWorld, "datapacks")));
        }
        finally
        {
            Directory.Delete(linkedWorld);
        }
    }

    [TestMethod]
    public async Task DatapackArchiveEntryLimitAndCancellationAreEnforced()
    {
        await using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Root);
        var validPath = Path.Combine(temporary.Root, "valid.zip");
        await File.WriteAllBytesAsync(validPath, CreateDatapackZip("valid"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => Task.Run(
            () => InstanceContent.ValidateDatapackPackage(validPath, cancellation.Token)));

        var tooManyEntriesPath = Path.Combine(temporary.Root, "too-many.zip");
        await File.WriteAllBytesAsync(tooManyEntriesPath, CreateZipWithEntries(100_001));
        await Assert.ThrowsExactlyAsync<InstanceContent.InvalidDatapackPackageException>(() => Task.Run(
            () => InstanceContent.ValidateDatapackPackage(tooManyEntriesPath, CancellationToken.None)));
    }

    [TestMethod]
    public async Task LegacyKindApiRejectsDatapacksWithoutAWorldTarget()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, _) = await CreateReadyInstanceAsync(temporary);
        var content = new InstanceContent(store);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(
            () => content.ListAsync(instance.Id, ModrinthContentKind.Datapack));
    }

    [TestMethod]
    [DataRow("World", false)]
    [DataRow("saves", false)]
    [DataRow("root", false)]
    [DataRow("World", true)]
    [DataRow("saves", true)]
    [DataRow("root", true)]
    [DataRow("none", true)]
    public async Task FolderImportNeverRecreatesParentsDeletedDuringNestedCopy(string deletedParent, bool cancel)
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var saves = Path.Combine(root, "minecraft", "saves");
        var world = CreateWorld(saves, "World", [1, 2, 3]);
        var source = Path.Combine(temporary.Root, "FolderPack");
        var nested = Path.Combine(source, "nested");
        Directory.CreateDirectory(Path.Combine(nested, "child"));
        await File.WriteAllTextAsync(Path.Combine(source, "pack.mcmeta"), "{\"pack\":{}}");
        var payload = Path.Combine(nested, "child", "payload.txt");
        await File.WriteAllTextAsync(payload, "source intact");
        var removed = deletedParent == "root" ? root : deletedParent == "saves" ? saves : world;
        using var cancellation = new CancellationTokenSource();
        var hookCalled = false;
        var content = new InstanceContent(store, null, null, beforeCopyDirectory: path =>
        {
            if (path != nested) return;
            hookCalled = true;
            Assert.IsTrue(Directory.EnumerateDirectories(Path.Combine(world, "datapacks"), ".hako-import-*").Any());
            Assert.IsFalse(File.Exists(Path.Combine(world, ".hako-datapacks.json")));
            // All paths are fixture-owned and contained in this test's temporary directory.
            Assert.IsTrue(Path.GetFullPath(removed).StartsWith(Path.GetFullPath(temporary.Root) + Path.DirectorySeparatorChar));
            if (deletedParent != "none") Directory.Delete(removed, recursive: true);
            if (cancel) cancellation.Cancel();
        });

        Exception? failure = null;
        try
        {
            await content.ImportAsync(instance.Id, InstanceContentTarget.WorldDatapacks("World"), source,
                cancellationToken: cancellation.Token);
        }
        catch (Exception exception) { failure = exception; }

        Assert.IsTrue(hookCalled);
        Assert.IsNotNull(failure);
        if (cancel) Assert.IsInstanceOfType<OperationCanceledException>(failure);
        if (deletedParent == "none")
        {
            Assert.IsTrue(Directory.Exists(world));
            Assert.IsTrue(File.Exists(Path.Combine(world, "level.dat")));
            Assert.IsFalse(Directory.EnumerateFileSystemEntries(Path.Combine(world, "datapacks")).Any());
        }
        else
        {
            Assert.IsFalse(Directory.Exists(removed), "The deleted parent must remain absent.");
            Assert.IsFalse(Directory.Exists(world));
            Assert.IsFalse(File.Exists(Path.Combine(world, "level.dat")));
        }
        Assert.IsFalse(File.Exists(Path.Combine(world, ".hako-datapacks.json")));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(temporary.Root, ".hako-import-*", SearchOption.AllDirectories).Any());
        Assert.AreEqual("source intact", await File.ReadAllTextAsync(payload));
        Assert.AreEqual("{\"pack\":{}}", await File.ReadAllTextAsync(Path.Combine(source, "pack.mcmeta")));
    }

    private static async Task<(InstanceStore Store, GameInstance Instance, string Root)> CreateReadyInstanceAsync(
        TemporaryDirectory temporary)
    {
        var store = temporary.CreateStore();
        await store.LoadAsync();
        var instance = store.Create(new InstanceDraft { Name = "Datapack Tests" }, "1.20.4");
        store.SetInstallationStatus(instance.Id, InstanceInstallationState.Ready, pauseRequested: false);
        instance = store.List().Single(candidate => candidate.Id == instance.Id);
        return (store, instance, store.GetInstanceDirectory(instance.Id));
    }

    private static string CreateWorld(string savesRoot, string worldId, byte[] payload)
    {
        var world = Path.Combine(savesRoot, worldId);
        Directory.CreateDirectory(world);
        var metadata = WorldTestFixture.Create(worldId, extra: WorldTestFixture.ByteArrayTag("fixture", payload));
        File.WriteAllBytes(Path.Combine(world, "level.dat"), metadata.Compressed);
        return world;
    }

    private static byte[] CreateDatapackZip(string payload)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("pack.mcmeta").Open(), new UTF8Encoding(false)))
                writer.Write("{\"pack\":{\"pack_format\":15,\"description\":\"test\"}}");
            using (var writer = new StreamWriter(archive.CreateEntry("data/fixture.txt").Open(), new UTF8Encoding(false)))
                writer.Write(payload);
        }

        return output.ToArray();
    }

    private static (string Filename, byte[] Bytes) CreateZip(string filename, (string Entry, string Content) metadata,
        bool symlink = false)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(metadata.Entry);
            if (symlink) entry.ExternalAttributes = unchecked((int)(0xA000u << 16));
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(metadata.Content);
        }

        return (filename, output.ToArray());
    }

    private static byte[] CreateZipWithEntries(int payloadCount)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("pack.mcmeta").Open(), new UTF8Encoding(false)))
                writer.Write("{\"pack\":{}}");
            for (var index = 0; index < payloadCount; index++)
                archive.CreateEntry($"entry-{index}.txt");
        }

        return output.ToArray();
    }

    private static ModOrigin Origin(string versionId, byte[] bytes) => new()
    {
        Source = ModSource.Modrinth,
        ProjectID = "datapack01",
        VersionID = versionId,
        PageURL = new Uri($"https://modrinth.com/datapack/datapack01/version/{versionId}"),
        Sha512 = Hash(bytes),
        VersionNumber = versionId
    };

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();

    private static void CreateJunction(string junction, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/c", "mklink", "/J", junction, target }
        });
        process!.WaitForExit();
        if (process.ExitCode != 0) Assert.Inconclusive("Could not create a junction for the isolated datapack test.");
    }
}

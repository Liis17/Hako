using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;

namespace Hako.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class InstanceWorldsTests
{
    [TestMethod]
    public async Task ListsModernOldAndCorruptWorldsWithFallbackAndUnknownFields()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var modern = CreateWorld(root, "Modern", WorldTestFixture.Create("Modern", "1.21.1", 123, 2000).Compressed);
        var old = CreateWorld(root, "Old", levelOld: WorldTestFixture.Create("Old metadata", version: null,
            gameType: 0, lastPlayed: 1000).Compressed);
        var recovered = CreateWorld(root, "Recovered", [1, 2, 3],
            WorldTestFixture.Create("Recovered from old", lastPlayed: 3000).Compressed);
        var broken = CreateWorld(root, "Broken", [1, 2, 3], [4, 5, 6]);
        var worlds = new InstanceWorlds(store);

        var listed = await worlds.ListAsync(instance.Id);

        CollectionAssert.AreEqual(new[] { "Recovered", "Modern", "Old", "Broken" }, listed.Select(item => item.Id).ToArray());
        Assert.AreEqual("Recovered from old", listed[0].Name);
        Assert.IsNull(listed[0].MetadataError);
        Assert.AreEqual("Old metadata", listed.Single(item => item.Id == "Old").Name);
        Assert.IsNull(listed.Single(item => item.Id == "Old").Version);
        Assert.AreEqual(0, listed.Single(item => item.Id == "Old").GameType);
        Assert.AreEqual(123, listed.Single(item => item.Id == "Modern").GameType);
        Assert.AreEqual(WorldMetadataError.InvalidGzip, listed.Single(item => item.Id == "Broken").MetadataError);
        Assert.AreEqual("Broken", listed.Single(item => item.Id == "Broken").Name);
        Assert.IsTrue(File.Exists(Path.Combine(modern, "level.dat")));
        Assert.IsTrue(Directory.Exists(recovered));
        Assert.IsTrue(Directory.Exists(broken));
    }

    [TestMethod]
    public async Task MissingSavesReturnsEmptyWithoutCreatingItAndHiddenWorldsAreIncluded()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var worlds = new InstanceWorlds(store);

        Assert.AreEqual(0, (await worlds.ListAsync(instance.Id)).Count);
        var saves = Path.Combine(root, "minecraft", "saves");
        Assert.IsFalse(Directory.Exists(saves));

        var hidden = CreateWorld(root, ".Hidden", WorldTestFixture.Create("Hidden World").Compressed);
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        CreateWorld(root, "FolderFallback", WorldTestFixture.Create(includeName: false).Compressed);
        var listed = await worlds.ListAsync(instance.Id);
        Assert.AreEqual(".Hidden", listed.Single(item => item.Id == ".Hidden").Id);
        Assert.AreEqual("Hidden World", listed.Single(item => item.Id == ".Hidden").Name);
        Assert.AreEqual("FolderFallback", listed.Single(item => item.Id == "FolderFallback").Name);
    }

    [TestMethod]
    public async Task SizeCountsHiddenDimensionsAndDatapacksAndIconHasAFourMiBLimit()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var folder = CreateWorld(root, "Sized", WorldTestFixture.Create().Compressed);
        var dimensionFile = Path.Combine(folder, "DIM-1", "region", "r.0.0.mca");
        Directory.CreateDirectory(Path.GetDirectoryName(dimensionFile)!);
        await File.WriteAllBytesAsync(dimensionFile, new byte[131]);
        await File.WriteAllBytesAsync(Path.Combine(folder, ".hidden"), new byte[29]);
        var datapack = Path.Combine(folder, "datapacks", "local.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(datapack)!);
        await File.WriteAllBytesAsync(datapack, new byte[53]);
        var iconPath = Path.Combine(folder, "icon.png");
        await File.WriteAllBytesAsync(iconPath, new byte[8]);
        var metadataLength = new FileInfo(Path.Combine(folder, "level.dat")).Length;
        var expectedSize = metadataLength + 131 + 29 + 53 + 8;

        var world = (await new InstanceWorlds(store).ListAsync(instance.Id)).Single();

        Assert.AreEqual(expectedSize, world.Size);
        CollectionAssert.AreEqual(new byte[8], world.IconData);
        await File.WriteAllBytesAsync(iconPath, new byte[4 * 1024 * 1024 + 1]);
        world = (await new InstanceWorlds(store).ListAsync(instance.Id)).Single();
        Assert.IsNull(world.IconData);
        Assert.AreEqual(expectedSize + 4 * 1024 * 1024 - 7, world.Size);
    }

    [TestMethod]
    public void FolderContractsRejectWindowsUnsafeIdsAndDoNotCreateDatapacks()
    {
        using var temporary = new TemporaryDirectory();
        var (store, instance, root) = CreateReadyInstanceAsync(temporary).GetAwaiter().GetResult();
        var folder = CreateWorld(root, "Real", WorldTestFixture.Create().Compressed);
        var expectedDatapacks = Path.Combine(folder, "datapacks");

        Assert.AreEqual(expectedDatapacks, InstanceWorlds.DatapacksFolder("Real", root));
        Assert.IsFalse(Directory.Exists(expectedDatapacks));
        foreach (var invalid in new[] { "", ".", "..", "../Outside", "name:stream", "CON", "name.", "name ",
                     @"C:\outside", @"\\server\share" })
        {
            Assert.AreEqual(InstanceWorldFailureReason.InvalidWorldId,
                Assert.ThrowsExactly<InstanceWorldException>(() => InstanceWorlds.WorldFolder(invalid, root)).Error);
        }

        Directory.Delete(folder, recursive: true);
        Assert.AreEqual(InstanceWorldFailureReason.WorldMissing,
            Assert.ThrowsExactly<InstanceWorldException>(() => InstanceWorlds.DatapacksFolder("Real", root)).Error);
        Assert.IsFalse(Directory.Exists(folder));
    }

    [TestMethod]
    public async Task FolderContractsRejectWorldAndDatapacksJunctionsAndListRejectsSavesJunction()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var realWorld = CreateWorld(root, "Real", WorldTestFixture.Create().Compressed);
        var outsideWorld = Path.Combine(temporary.Root, "outside-world");
        Directory.CreateDirectory(outsideWorld);
        await File.WriteAllBytesAsync(Path.Combine(outsideWorld, "level.dat"), WorldTestFixture.Create().Compressed);
        var saves = Path.Combine(root, "minecraft", "saves");
        var worldJunction = Path.Combine(saves, "Alias");
        using var worldLink = CreateJunction(temporary.Root, worldJunction, outsideWorld);

        Assert.AreEqual(InstanceWorldFailureReason.ReparsePointNotAllowed,
            Assert.ThrowsExactly<InstanceWorldException>(() => InstanceWorlds.WorldFolder("Alias", root)).Error);
        Assert.AreEqual("Real", (await new InstanceWorlds(store).ListAsync(instance.Id)).Single().Id);
        worldLink.Remove();

        var datapacksTarget = Path.Combine(temporary.Root, "outside-datapacks");
        Directory.CreateDirectory(datapacksTarget);
        var datapacks = Path.Combine(realWorld, "datapacks");
        using var datapacksLink = CreateJunction(temporary.Root, datapacks, datapacksTarget);
        Assert.AreEqual(InstanceWorldFailureReason.ReparsePointNotAllowed,
            Assert.ThrowsExactly<InstanceWorldException>(() => InstanceWorlds.DatapacksFolder("Real", root)).Error);

        datapacksLink.Remove();
        var movedSaves = Path.Combine(root, "minecraft", "saves-real");
        Directory.Move(saves, movedSaves);
        using var savesLink = CreateJunction(temporary.Root, saves, movedSaves);
        var listing = await Assert.ThrowsExactlyAsync<InstanceWorldException>(() => new InstanceWorlds(store).ListAsync(instance.Id));
        Assert.AreEqual(InstanceWorldFailureReason.ReparsePointNotAllowed, listing.Error);
    }

    [TestMethod]
    public async Task ListingFallsBackToOrdinaryOldMetadataWhenPrimaryIsAReparsePoint()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var world = CreateWorld(root, "Fallback", levelOld: WorldTestFixture.Create("Old metadata").Compressed);
        var outside = Path.Combine(temporary.Root, "outside-primary-metadata");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "sentinel.txt"), "keep");
        using var primaryLink = CreateJunction(temporary.Root, Path.Combine(world, "level.dat"), outside);

        var listed = await new InstanceWorlds(store).ListAsync(instance.Id);

        var recovered = listed.Single();
        Assert.AreEqual("Fallback", recovered.Id);
        Assert.AreEqual("Old metadata", recovered.Name);
        Assert.IsNull(recovered.MetadataError);
        Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Combine(outside, "sentinel.txt")));
    }

    [TestMethod]
    public async Task ListingKeepsWorldWhenIconIsAReparsePoint()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var world = CreateWorld(root, "IconLink", WorldTestFixture.Create("Listed world").Compressed);
        var outside = Path.Combine(temporary.Root, "outside-icon");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "sentinel.txt"), "keep");
        using var iconLink = CreateJunction(temporary.Root, Path.Combine(world, "icon.png"), outside);

        var listed = await new InstanceWorlds(store).ListAsync(instance.Id);

        Assert.AreEqual(1, listed.Count);
        Assert.AreEqual("Listed world", listed[0].Name);
        Assert.IsNull(listed[0].IconData);
        Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Combine(outside, "sentinel.txt")));
    }

    [TestMethod]
    public async Task ListingSkipsWorldWhenBothMetadataPathsAreReparsePoints()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var world = CreateWorld(root, "MetadataLinks");
        var outsidePrimary = Path.Combine(temporary.Root, "outside-primary");
        var outsideOld = Path.Combine(temporary.Root, "outside-old");
        Directory.CreateDirectory(outsidePrimary);
        Directory.CreateDirectory(outsideOld);
        var outsidePrimaryMetadata = WorldTestFixture.Create("Outside primary").Compressed;
        var outsideOldMetadata = WorldTestFixture.Create("Outside old").Compressed;
        await File.WriteAllBytesAsync(Path.Combine(outsidePrimary, "level.dat"), outsidePrimaryMetadata);
        await File.WriteAllBytesAsync(Path.Combine(outsideOld, "level.dat_old"), outsideOldMetadata);
        using var primaryLink = CreateJunction(temporary.Root, Path.Combine(world, "level.dat"), outsidePrimary);
        using var oldLink = CreateJunction(temporary.Root, Path.Combine(world, "level.dat_old"), outsideOld);

        var listed = await new InstanceWorlds(store).ListAsync(instance.Id);

        Assert.AreEqual(0, listed.Count);
        CollectionAssert.AreEqual(outsidePrimaryMetadata,
            await File.ReadAllBytesAsync(Path.Combine(outsidePrimary, "level.dat")));
        CollectionAssert.AreEqual(outsideOldMetadata,
            await File.ReadAllBytesAsync(Path.Combine(outsideOld, "level.dat_old")));
    }

    [TestMethod]
    public async Task DuplicateUsesCaseInsensitiveSuffixAndRenamesBothFilesWithoutChangingSource()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var main = WorldTestFixture.Create("Display name", extra: WorldTestFixture.ByteArrayTag("future", [8, 7, 6]));
        var old = WorldTestFixture.Create("Old display name", lastPlayed: 123);
        var source = CreateWorld(root, "World", main.Compressed, old.Compressed);
        Directory.CreateDirectory(Path.Combine(source, "datapacks", "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "datapacks", "nested", "pack.mcmeta"), "keep");
        await File.WriteAllTextAsync(Path.Combine(source, ".secret"), "private");
        var originalMain = await File.ReadAllBytesAsync(Path.Combine(source, "level.dat"));
        var originalOld = await File.ReadAllBytesAsync(Path.Combine(source, "level.dat_old"));
        Directory.CreateDirectory(Path.Combine(root, "minecraft", "saves", "wORLD 2"));
        var worlds = new InstanceWorlds(store);

        var result = await worlds.DuplicateAsync(instance.Id, "World");

        Assert.IsTrue(result.Succeeded, result.FailureReason?.ToString());
        Assert.AreEqual("World 3", result.WorldId);
        var duplicate = Path.Combine(root, "minecraft", "saves", result.WorldId!);
        Assert.AreEqual("Display name 3", Hako.Core.Minecraft.WorldMetadata.Read(
            await File.ReadAllBytesAsync(Path.Combine(duplicate, "level.dat"))).Name);
        Assert.AreEqual("Display name 3", Hako.Core.Minecraft.WorldMetadata.Read(
            await File.ReadAllBytesAsync(Path.Combine(duplicate, "level.dat_old"))).Name);
        CollectionAssert.AreEqual(originalMain, await File.ReadAllBytesAsync(Path.Combine(source, "level.dat")));
        CollectionAssert.AreEqual(originalOld, await File.ReadAllBytesAsync(Path.Combine(source, "level.dat_old")));
        Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Combine(duplicate, "datapacks", "nested", "pack.mcmeta")));
        Assert.AreEqual("private", await File.ReadAllTextAsync(Path.Combine(duplicate, ".secret")));
        await File.WriteAllTextAsync(Path.Combine(duplicate, ".secret"), "changed");
        Assert.AreEqual("private", await File.ReadAllTextAsync(Path.Combine(source, ".secret")));
        Assert.IsFalse(Directory.EnumerateDirectories(Path.Combine(root, "minecraft", "saves"))
            .Any(path => Path.GetFileName(path).StartsWith(".hako-world-copy-", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task DuplicateCopiesUnreadableMetadataByteForByte()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var main = new byte[] { 1, 2, 3, 4 };
        var old = new byte[] { 5, 6, 7, 8 };
        var source = CreateWorld(root, "Broken", main, old);

        var result = await new InstanceWorlds(store).DuplicateAsync(instance.Id, "Broken");

        Assert.IsTrue(result.Succeeded, result.FailureReason?.ToString());
        var duplicate = Path.Combine(root, "minecraft", "saves", result.WorldId!);
        CollectionAssert.AreEqual(main, await File.ReadAllBytesAsync(Path.Combine(duplicate, "level.dat")));
        CollectionAssert.AreEqual(old, await File.ReadAllBytesAsync(Path.Combine(duplicate, "level.dat_old")));
        CollectionAssert.AreEqual(main, await File.ReadAllBytesAsync(Path.Combine(source, "level.dat")));
    }

    [TestMethod]
    public async Task DuplicateCancellationCleansItsOwnedStage()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var source = CreateWorld(root, "Large", WorldTestFixture.Create().Compressed);
        var saves = Path.Combine(root, "minecraft", "saves");
        using var cancellation = new CancellationTokenSource();
        var stageCreated = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var duplicate = new InstanceWorlds(store, null, stage =>
        {
            stageCreated.TrySetResult(stage);
            cancellation.Token.WaitHandle.WaitOne();
            cancellation.Token.ThrowIfCancellationRequested();
        }).DuplicateAsync(instance.Id, "Large", cancellation.Token);
        var stage = await stageCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(Directory.Exists(stage));

        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => duplicate);
        Assert.IsFalse(Directory.EnumerateDirectories(saves).Any(path =>
            Path.GetFileName(path).StartsWith(".hako-world-copy-", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(Directory.Exists(source));
    }

    [TestMethod]
    public async Task DuplicateRejectsNestedJunctionWithoutTouchingItsTarget()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var source = CreateWorld(root, "Linked", WorldTestFixture.Create().Compressed);
        var target = Path.Combine(temporary.Root, "outside-world-data");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "secret.txt"), "keep");
        var junction = Path.Combine(source, "linked");
        using var junctionLink = CreateJunction(temporary.Root, junction, target);

        var result = await new InstanceWorlds(store).DuplicateAsync(instance.Id, "Linked");

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(InstanceWorldFailureReason.ReparsePointNotAllowed, result.FailureReason);
        Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Combine(target, "secret.txt")));
        Assert.IsTrue(Directory.Exists(source));
    }

    [TestMethod]
    public async Task ListingSkipsJunctionSizeAndDuplicateRejectsItsReparsePoint()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var source = CreateWorld(root, "Linked", WorldTestFixture.Create().Compressed);
        var target = Path.Combine(temporary.Root, "outside-world-data");
        Directory.CreateDirectory(target);
        await File.WriteAllBytesAsync(Path.Combine(target, "secret.bin"), new byte[4096]);
        var junction = Path.Combine(source, "linked");
        using var junctionLink = CreateJunction(temporary.Root, junction, target);
        var worlds = new InstanceWorlds(store);

        var listed = (await worlds.ListAsync(instance.Id)).Single();

        Assert.AreEqual(new FileInfo(Path.Combine(source, "level.dat")).Length, listed.Size);
        var duplicated = await worlds.DuplicateAsync(instance.Id, "Linked");
        Assert.AreEqual(InstanceWorldFailureReason.ReparsePointNotAllowed, duplicated.FailureReason);
        Assert.AreEqual(4096, new FileInfo(Path.Combine(target, "secret.bin")).Length);
    }

    [TestMethod]
    public async Task TrashFailureRetainsWorldAndReportsTypedStatus()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var world = CreateWorld(root, "Keep", WorldTestFixture.Create().Compressed);
        var worlds = new InstanceWorlds(store, new RecycleBin(new FailedRecycleBinOperation()));

        var result = await worlds.TrashAsync(instance.Id, "Keep");

        Assert.AreEqual(RecycleBinItemStatus.Failed, result.Status);
        Assert.AreEqual(InstanceWorldFailureReason.RecycleBinFailed, result.FailureReason);
        Assert.IsTrue(Directory.Exists(world));
        Assert.IsTrue(File.Exists(Path.Combine(world, "level.dat")));
    }

    [TestMethod]
    public async Task DuplicateHonorsContentGameAndInstallationLeases()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        CreateWorld(root, "World", WorldTestFixture.Create().Compressed);
        var worlds = new InstanceWorlds(store);

        Assert.IsTrue(store.TryBeginLaunch(instance.Id, out var launchLease, out _));
        using (launchLease!)
        {
            var running = await worlds.DuplicateAsync(instance.Id, "World");
            Assert.AreEqual(InstanceWorldFailureReason.BuildRunning, running.FailureReason);
        }

        Assert.IsTrue(store.TryBeginContent(instance.Id, out var contentLease, out _));
        using (contentLease!)
        {
            var busy = await worlds.DuplicateAsync(instance.Id, "World");
            Assert.AreEqual(InstanceWorldFailureReason.BuildFilesBusy, busy.FailureReason);
        }

        var queued = store.Create(new InstanceDraft { Name = "Queued" }, "1.20.4");
        var installing = await worlds.DuplicateAsync(queued.Id, "World");
        Assert.AreEqual(InstanceWorldFailureReason.BuildInstalling, installing.FailureReason);
    }

    [TestMethod]
    public async Task TrashRechecksWorldAfterItDisappears()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var world = CreateWorld(root, "Gone", WorldTestFixture.Create().Compressed);
        Directory.Delete(world, recursive: true);

        var result = await new InstanceWorlds(store, new RecycleBin(new FailedRecycleBinOperation()))
            .TrashAsync(instance.Id, "Gone");

        Assert.AreEqual(InstanceWorldFailureReason.WorldMissing, result.FailureReason);
        Assert.IsFalse(Directory.Exists(world));
    }

    private static async Task<(InstanceStore Store, GameInstance Instance, string Root)> CreateReadyInstanceAsync(
        TemporaryDirectory temporary)
    {
        var store = temporary.CreateStore();
        await store.LoadAsync();
        var instance = store.Create(new InstanceDraft { Name = "World Tests" }, "1.20.4");
        var registryPath = Path.Combine(temporary.Paths.GameRoot, "instances.json");
        var registry = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(registryPath),
            HakoJsonContext.Default.InstanceRegistryFile)!;
        var ready = registry with
        {
            Instances = registry.Instances.Select(item => item.Id == instance.Id
                ? item with { State = InstanceInstallationState.Ready }
                : item).ToList()
        };
        await File.WriteAllBytesAsync(registryPath,
            JsonSerializer.SerializeToUtf8Bytes(ready, HakoJsonContext.Default.InstanceRegistryFile));
        await store.LoadAsync();
        var loaded = store.List().Single(item => item.Id == instance.Id);
        return (store, loaded, store.GetInstanceDirectory(instance.Id));
    }

    private static string CreateWorld(string instanceRoot, string id, byte[]? level = null, byte[]? levelOld = null)
    {
        var folder = Path.Combine(instanceRoot, "minecraft", "saves", id);
        Directory.CreateDirectory(folder);
        if (level is not null) File.WriteAllBytes(Path.Combine(folder, "level.dat"), level);
        if (levelOld is not null) File.WriteAllBytes(Path.Combine(folder, "level.dat_old"), levelOld);
        return folder;
    }

    private static JunctionCleanup CreateJunction(string temporaryRoot, string junction, string target)
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Junction tests require Windows.");

        var cleanup = new JunctionCleanup(temporaryRoot, junction, target);
        try
        {
            var command = "$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path "
                + PowerShellLiteral(junction) + " -Target " + PowerShellLiteral(target) + " | Out-Null";
            using var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command }
            }) ?? throw new InvalidOperationException("Could not start Windows PowerShell to create a test junction.");
            process.WaitForExit();
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            if (process.ExitCode != 0)
            {
                Assert.Fail("Could not create a junction in the isolated test folder (exit code "
                    + process.ExitCode + "): " + standardOutput + standardError);
            }

            cleanup.AssertCreated();
            return cleanup;
        }
        catch
        {
            cleanup.Dispose();
            throw;
        }
    }

    private static string PowerShellLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    private sealed class JunctionCleanup(string temporaryRoot, string junction, string target) : IDisposable
    {
        private bool _removed;

        public void AssertCreated()
        {
            AssertTestOwnedPath(junction);
            AssertTestOwnedPath(target);
            var attributes = File.GetAttributes(junction);
            Assert.IsTrue((attributes & FileAttributes.ReparsePoint) != 0,
                "PowerShell did not create a reparse-point junction at the test-owned path.");
            Assert.IsTrue((attributes & FileAttributes.Directory) != 0,
                "The test junction is not a directory reparse point.");
            Assert.IsTrue(Directory.Exists(target), "The test-owned junction target was not created.");
        }

        public void Remove() => Dispose();

        public void Dispose()
        {
            if (_removed) return;
            AssertTestOwnedPath(junction);
            AssertTestOwnedPath(target);
            Assert.IsTrue(Directory.Exists(target), "The test-owned junction target disappeared before cleanup.");

            if (TryGetAttributes(junction, out var attributes))
            {
                Assert.IsTrue((attributes & FileAttributes.ReparsePoint) != 0
                    && (attributes & FileAttributes.Directory) != 0,
                    "Refusing to delete a test path that is not a directory reparse point.");
                Directory.Delete(junction);
            }

            Assert.IsTrue(Directory.Exists(target), "Removing the junction deleted its target.");
            _removed = true;
        }

        private void AssertTestOwnedPath(string path)
        {
            var root = Path.GetFullPath(temporaryRoot).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            Assert.IsTrue(fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase),
                "Refusing to remove a path outside this test's temporary directory.");
        }

        private static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                attributes = default;
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                attributes = default;
                return false;
            }
        }
    }

    private sealed class FailedRecycleBinOperation : IRecycleBinOperation
    {
        public Task<RecycleBinItemStatus> DeleteAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(RecycleBinItemStatus.Failed);
    }
}

using System.Diagnostics;
using System.IO.Compression;
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
public sealed class InstanceBackupsAndScreenshotsTests
{
    private static readonly DateTimeOffset BackupTime =
        new DateTimeOffset(2026, 10, 23, 14, 7, 8, TimeSpan.Zero).AddTicks(9876543);

    [TestMethod]
    public async Task ScreenshotsListDirectVisiblePngFilesInNewestFirstOrderAndMissingFolderStaysMissing()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await temporary.CreateReadyInstanceAsync();
        var screenshots = new InstanceScreenshots(store);

        Assert.AreEqual(0, (await screenshots.ListAsync(instance.Id)).Count);
        var folder = Path.Combine(root, "minecraft", "screenshots");
        Assert.IsFalse(Directory.Exists(folder));

        Directory.CreateDirectory(folder);
        var newest = Path.Combine(folder, "new.png");
        var firstByName = Path.Combine(folder, "a.png");
        var secondByName = Path.Combine(folder, "z.png");
        await File.WriteAllBytesAsync(newest, [1]);
        await File.WriteAllBytesAsync(firstByName, [2]);
        await File.WriteAllBytesAsync(secondByName, [3]);
        await File.WriteAllBytesAsync(Path.Combine(folder, "not-png.jpg"), [4]);
        await File.WriteAllBytesAsync(Path.Combine(folder, ".hidden.png"), [5]);
        Directory.CreateDirectory(Path.Combine(folder, "folder.png"));
        File.SetAttributes(Path.Combine(folder, ".hidden.png"),
            File.GetAttributes(Path.Combine(folder, ".hidden.png")) | FileAttributes.Hidden);
        var sameTime = new DateTime(2026, 10, 23, 14, 7, 8, DateTimeKind.Utc);
        File.SetCreationTimeUtc(firstByName, sameTime);
        File.SetCreationTimeUtc(secondByName, sameTime);
        File.SetCreationTimeUtc(newest, sameTime.AddMinutes(1));

        var listed = await screenshots.ListAsync(instance.Id);

        CollectionAssert.AreEqual(new[] { "new.png", "a.png", "z.png" }, listed.Select(item => item.Name).ToArray());
    }

    [TestMethod]
    public async Task ScreenshotTrashRevalidatesOwnershipAndReturnsPartialRecycleResultsWhileGameRuns()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await temporary.CreateReadyInstanceAsync();
        var folder = Path.Combine(root, "minecraft", "screenshots");
        Directory.CreateDirectory(folder);
        var kept = Path.Combine(folder, "kept.png");
        var failed = Path.Combine(folder, "failed.png");
        var outside = Path.Combine(temporary.Root, "outside.png");
        await File.WriteAllBytesAsync(kept, [1]);
        await File.WriteAllBytesAsync(failed, [2]);
        await File.WriteAllBytesAsync(outside, [3]);
        var operation = new CallbackRecycleOperation((path, _) =>
        {
            if (path == Path.GetFullPath(failed)) return Task.FromResult(RecycleBinItemStatus.Failed);
            File.Delete(path);
            return Task.FromResult(RecycleBinItemStatus.Recycled);
        });
        var screenshots = new InstanceScreenshots(store, new RecycleBin(operation));
        Assert.IsTrue(store.TryBeginLaunch(instance.Id, out var launchLease, out _));
        using (launchLease)
        {
            var result = await screenshots.TrashAsync(instance.Id,
            [
                new InstanceScreenshot(kept, null),
                new InstanceScreenshot(failed, null),
                new InstanceScreenshot(outside, null)
            ]);

            CollectionAssert.AreEqual(new[]
            {
                RecycleBinItemStatus.Recycled, RecycleBinItemStatus.Failed, RecycleBinItemStatus.Failed
            }, result.Results.Items.Select(item => item.Status).ToArray());
        }

        Assert.IsFalse(File.Exists(kept));
        Assert.IsTrue(File.Exists(failed));
        Assert.IsTrue(File.Exists(outside));
    }

    [TestMethod]
    public async Task ScreenshotListingAndTrashRejectScreenshotFolderJunctions()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await temporary.CreateReadyInstanceAsync();
        var screenshotParent = Path.Combine(root, "minecraft");
        Directory.CreateDirectory(screenshotParent);
        var target = Path.Combine(temporary.Root, "outside-screenshots");
        Directory.CreateDirectory(target);
        await File.WriteAllBytesAsync(Path.Combine(target, "outside.png"), [9]);
        using var junction = CreateJunction(temporary.Root, Path.Combine(screenshotParent, "screenshots"), target);

        await Assert.ThrowsExactlyAsync<InstanceStorageException>(() => new InstanceScreenshots(store).ListAsync(instance.Id));
        await Assert.ThrowsExactlyAsync<InstanceStorageException>(() => new InstanceScreenshots(store).TrashAsync(instance.Id,
            [new InstanceScreenshot(Path.Combine(target, "outside.png"), null)]));
        Assert.IsTrue(File.Exists(Path.Combine(target, "outside.png")));
    }

    [TestMethod]
    public async Task DuplicateCopiesIndependentReadyFabricProfileAndModpackStateButStripsLaunchArtifacts()
    {
        await using var temporary = new TemporaryDirectory();
        var plan = CompletedModpackPlan();
        var (store, source, root) = await temporary.CreateReadyInstanceAsync("Build",
            new InstanceDraft
            {
                Name = "Build",
                IconSymbol = "",
                IconData = [10, 11, 12],
                ModLoader = InstanceModLoader.Fabric,
                FabricConfiguration = new FabricConfiguration("0.16.9"),
                ArgumentSource = LaunchArgumentSource.Custom,
                OfflineMode = true,
                OfflineUsername = "PreviewUser",
                Parameters = new InstanceParameters { JavaArguments = "-Dfixture=true", MaximumMemoryMiB = 3072 }
            }, plan);
        Directory.CreateDirectory(Path.Combine(root, "java", "runtime"));
        Directory.CreateDirectory(Path.Combine(root, "minecraft", "mods"));
        Directory.CreateDirectory(Path.Combine(root, "minecraft", ".hako-launch"));
        Directory.CreateDirectory(Path.Combine(root, "minecraft", "logs"));
        Directory.CreateDirectory(Path.Combine(root, ".hako-game"));
        await File.WriteAllBytesAsync(Path.Combine(root, "java", "runtime", "javaw.exe"), [21, 22, 23]);
        await File.WriteAllBytesAsync(Path.Combine(root, "minecraft", "mods", "local.jar"), [31, 32, 33]);
        await File.WriteAllTextAsync(Path.Combine(root, "minecraft", ".hako-running.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(root, "minecraft", ".hako-launch", "args.json"), "session");
        await File.WriteAllTextAsync(Path.Combine(root, "minecraft", "logs", "hako-launch.log"), "session log");
        await File.WriteAllTextAsync(Path.Combine(root, ".hako-game", "arguments"), "transient");
        var occupied = store.Create(new InstanceDraft { Name = "Build 2" }, "1.20.4");
        var occupiedFolder = store.GetInstanceDirectory(occupied.Id);
        await File.WriteAllTextAsync(Path.Combine(occupiedFolder, "owner.txt"), "keep");
        await temporary.SetReadyAsync(store, occupied.Id);

        var duplicate = await store.DuplicateAsync(source.Id);
        var duplicateRoot = store.GetInstanceDirectory(duplicate.Id);

        Assert.AreEqual("Build 3", duplicate.Name);
        Assert.AreNotEqual(source.Id, duplicate.Id);
        Assert.IsTrue(duplicate.CreatedAt > source.CreatedAt);
        Assert.AreEqual(source.IconSymbol, duplicate.IconSymbol);
        Assert.AreNotEqual(source.IconRevision, duplicate.IconRevision);
        Assert.AreEqual(source.FabricConfiguration, duplicate.FabricConfiguration);
        Assert.AreEqual(source.Parameters, duplicate.Parameters);
        Assert.IsTrue(duplicate.OfflineMode);
        Assert.AreEqual("PreviewUser", duplicate.OfflineUsername);
        CollectionAssert.AreEqual(new byte[] { 10, 11, 12 }, await File.ReadAllBytesAsync(Path.Combine(duplicateRoot, "icon.png")));
        CollectionAssert.AreEqual(new byte[] { 21, 22, 23 }, await File.ReadAllBytesAsync(Path.Combine(duplicateRoot, "java", "runtime", "javaw.exe")));
        CollectionAssert.AreEqual(new byte[] { 31, 32, 33 }, await File.ReadAllBytesAsync(Path.Combine(duplicateRoot, "minecraft", "mods", "local.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(duplicateRoot, "minecraft", ".hako-running.json")));
        Assert.IsFalse(Directory.Exists(Path.Combine(duplicateRoot, "minecraft", ".hako-launch")));
        Assert.IsFalse(File.Exists(Path.Combine(duplicateRoot, "minecraft", "logs", "hako-launch.log")));
        Assert.IsFalse(Directory.Exists(Path.Combine(duplicateRoot, ".hako-game")));
        Assert.IsTrue(ModpackPlan.Read(duplicateRoot)!.Completed);
        Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Combine(occupiedFolder, "owner.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "minecraft", ".hako-running.json")));
    }

    [TestMethod]
    public async Task DuplicateRollbackLeavesSourceAndPreexistingUserFoldersUntouchedWhenRegistryCommitFails()
    {
        await using var temporary = new TemporaryDirectory();
        var initial = await temporary.CreateReadyInstanceAsync("Original");
        await File.WriteAllTextAsync(Path.Combine(initial.Root, "user.txt"), "source");
        var failing = await temporary.LoadStoreAsync(() => throw new IOException("registry fault"));

        await Assert.ThrowsExactlyAsync<IOException>(() => failing.DuplicateAsync(initial.Instance.Id));

        Assert.AreEqual(1, failing.List().Count);
        Assert.AreEqual("source", await File.ReadAllTextAsync(Path.Combine(initial.Root, "user.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(temporary.Paths.GameRoot, "Original 2")));
        Assert.AreEqual(0, Directory.GetDirectories(temporary.Paths.GameRoot, ".hako-duplicate-*").Length);
    }

    [TestMethod]
    public async Task DuplicateRejectsLockedSourceFilesAndPrecancelledCopyWithoutPublishing()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await temporary.CreateReadyInstanceAsync();
        var file = Path.Combine(root, "user.dat");
        await File.WriteAllTextAsync(file, "keep");
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsExactlyAsync<IOException>(() => store.DuplicateAsync(instance.Id));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.DuplicateAsync(instance.Id, cancellation.Token));
        Assert.AreEqual("keep", await File.ReadAllTextAsync(file));
        Assert.AreEqual(1, store.List().Count);
        Assert.AreEqual(0, Directory.GetDirectories(temporary.Paths.GameRoot, ".hako-duplicate-*").Length);
    }

    [TestMethod]
    public async Task DuplicateRejectsNestedJunctionWithoutReadingOrChangingItsTarget()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await temporary.CreateReadyInstanceAsync();
        var target = Path.Combine(temporary.Root, "external-tree");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "outside");
        using var junction = CreateJunction(temporary.Root, Path.Combine(root, "minecraft", "linked"), target);

        var exception = await Assert.ThrowsExactlyAsync<InstanceStorageException>(() => store.DuplicateAsync(instance.Id));

        Assert.AreEqual(InstanceStorageError.ReparsePointNotAllowed, exception.Error);
        Assert.AreEqual("outside", await File.ReadAllTextAsync(sentinel));
        Assert.AreEqual(1, store.List().Count);
        Assert.AreEqual(0, Directory.GetDirectories(temporary.Paths.GameRoot, ".hako-duplicate-*").Length);
    }

    [TestMethod]
    public async Task InstanceBackupMatchesMacManifestAndStreamsWholeInstanceWithoutTransientSessionFiles()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await temporary.CreateReadyInstanceAsync(draft: new InstanceDraft
        {
            Name = "Build",
            IconSymbol = "",
            IconData = [8, 9]
        });
        Directory.CreateDirectory(Path.Combine(root, "java", "runtime"));
        Directory.CreateDirectory(Path.Combine(root, "minecraft", "mods"));
        Directory.CreateDirectory(Path.Combine(root, "minecraft", ".hako-launch"));
        Directory.CreateDirectory(Path.Combine(root, "minecraft", "logs"));
        Directory.CreateDirectory(Path.Combine(root, ".hako-game"));
        await File.WriteAllBytesAsync(Path.Combine(root, "java", "runtime", "javaw.exe"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(root, "minecraft", "mods", "local.jar"), [4, 5, 6]);
        await File.WriteAllTextAsync(Path.Combine(root, "minecraft", ".hako-running.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(root, "minecraft", ".hako-launch", "args.json"), "session");
        await File.WriteAllTextAsync(Path.Combine(root, ".hako-game", "arguments"), "transient");
        await File.WriteAllTextAsync(Path.Combine(root, "minecraft", "logs", "hako-launch.log"), "transient launch log");
        var modHash = Convert.ToHexString(SHA512.HashData([4, 5, 6])).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(root, "minecraft", ".hako-mods.json"),
            "{\"apiProvisioned\":false,\"files\":{\"local.jar\":{\"source\":\"modrinth\",\"projectID\":\"project-id\",\"versionID\":\"version-id\",\"pageURL\":\"https://modrinth.com/mod/project-id\",\"sha512\":\""
            + modHash + "\"}}}");
        var content = new InstanceContent(store);
        var backup = new InstanceBackup(store, content, () => BackupTime, null);

        var result = await backup.CreateAsync(instance.Id);

        Assert.IsTrue(result.Succeeded, result.Failure?.Diagnostic);
        using var stream = File.OpenRead(result.Path!);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        CollectionAssert.AreEquivalent(new[]
        {
            "data.json", "icon.png", "java/", "java/runtime/", "java/runtime/javaw.exe",
            "minecraft/", "minecraft/logs/", "minecraft/mods/", "minecraft/mods/local.jar",
            "minecraft/.hako-mods.json"
        }, archive.Entries.Select(entry => entry.FullName).ToArray());
        var json = await ReadJsonAsync(archive.GetEntry("data.json")!);
        Assert.AreEqual(1, json.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.AreEqual("2026-10-23T14:07:08Z", json.RootElement.GetProperty("backupCreatedAt").GetString());
        Assert.AreEqual(instance.Name, json.RootElement.GetProperty("name").GetString());
        Assert.AreEqual(instance.FolderName, json.RootElement.GetProperty("folderName").GetString());
        Assert.AreEqual(instance.Id, json.RootElement.GetProperty("instanceID").GetGuid());
        Assert.IsFalse(json.RootElement.GetProperty("profile").GetProperty("offlineMode").GetBoolean());
        Assert.AreEqual("mojang", json.RootElement.GetProperty("profile").GetProperty("argumentSource").GetString());
        CollectionAssert.AreEqual(new byte[] { 8, 9 }, Convert.FromBase64String(json.RootElement.GetProperty("iconPNG").GetString()!));
        Assert.IsFalse(json.RootElement.TryGetProperty("loaderVersion", out _));
        Assert.AreEqual("project-id", json.RootElement.GetProperty("mods")[0].GetProperty("projectID").GetString());
        Assert.AreEqual("version-id", json.RootElement.GetProperty("mods")[0].GetProperty("versionID").GetString());
        Assert.IsFalse(archive.Entries.Any(entry => entry.FullName.EndsWith("hako-launch.log", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task WorldBackupPreservesNestedDataJsonDatapackOriginsHashesAndSwiftDateShape()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await temporary.CreateReadyInstanceAsync();
        var world = Path.Combine(root, "minecraft", "saves", "Save");
        Directory.CreateDirectory(Path.Combine(world, "datapacks", "local-folder"));
        Directory.CreateDirectory(Path.Combine(world, ".hako-disabled-datapacks"));
        await File.WriteAllBytesAsync(Path.Combine(world, "level.dat"),
            WorldTestFixture.Create("Save", lastPlayed: BackupTime.ToUnixTimeMilliseconds()).Compressed);
        await File.WriteAllTextAsync(Path.Combine(world, "data.json"), "{\"save\":\"mine\"}");
        var packBytes = CreateDatapackZip();
        await File.WriteAllBytesAsync(Path.Combine(world, "datapacks", "enabled.zip"), packBytes);
        await File.WriteAllBytesAsync(Path.Combine(world, ".hako-disabled-datapacks", "disabled.zip"), packBytes);
        await File.WriteAllTextAsync(Path.Combine(world, "datapacks", "local-folder", "pack.mcmeta"),
            "{\"pack\":{\"pack_format\":48,\"description\":\"folder pack\"}}");
        await File.WriteAllBytesAsync(Path.Combine(world, "datapacks", "local-folder", "pack.png"), [20, 21]);
        await File.WriteAllBytesAsync(Path.Combine(world, "icon.png"), [30, 31]);
        var hash = Convert.ToHexString(SHA512.HashData(packBytes)).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(world, ".hako-datapacks.json"),
            "{\"files\":{\"enabled.zip\":{\"source\":\"modrinth\",\"projectID\":\"project\",\"versionID\":\"version\",\"pageURL\":\"https://modrinth.com/datapack/project\",\"sha512\":\""
            + hash + "\"}}}");
        var backups = new WorldBackup(store, new InstanceContent(store), new InstanceWorlds(store),
            () => BackupTime, null);

        var result = await backups.CreateAsync(instance.Id, "Save");

        Assert.IsTrue(result.Succeeded, result.Failure?.Diagnostic);
        using var stream = File.OpenRead(result.Path!);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.IsNotNull(archive.GetEntry("data.json"));
        Assert.IsNotNull(archive.GetEntry("world/data.json"));
        Assert.AreEqual("{\"save\":\"mine\"}", await ReadEntryTextAsync(archive.GetEntry("world/data.json")!));
        Assert.AreEqual("{\"files\":{\"enabled.zip\":{\"source\":\"modrinth\",\"projectID\":\"project\",\"versionID\":\"version\",\"pageURL\":\"https://modrinth.com/datapack/project\",\"sha512\":\""
            + hash + "\"}}}", await ReadEntryTextAsync(archive.GetEntry("world/.hako-datapacks.json")!));
        var json = await ReadJsonAsync(archive.GetEntry("data.json")!);
        Assert.AreEqual("Save", json.RootElement.GetProperty("name").GetString());
        Assert.AreEqual("Save", json.RootElement.GetProperty("folderName").GetString());
        Assert.AreEqual("world", json.RootElement.GetProperty("worldPath").GetString());
        Assert.AreEqual("2026-10-23T14:07:08Z", json.RootElement.GetProperty("backupCreatedAt").GetString());
        Assert.AreEqual("2026-10-23T14:07:08Z", json.RootElement.GetProperty("lastPlayed").GetString());
        CollectionAssert.AreEqual(new byte[] { 30, 31 }, Convert.FromBase64String(json.RootElement.GetProperty("iconPNG").GetString()!));
        var packs = json.RootElement.GetProperty("datapacks").EnumerateArray().ToArray();
        var enabled = packs.Single(item => item.GetProperty("file").GetString() == "enabled.zip");
        Assert.AreEqual("datapacks/enabled.zip", enabled.GetProperty("path").GetString());
        Assert.AreEqual(hash, enabled.GetProperty("sha512").GetString());
        var disabled = packs.Single(item => item.GetProperty("file").GetString() == "disabled.zip");
        Assert.IsFalse(disabled.GetProperty("enabled").GetBoolean());
        Assert.AreEqual(".hako-disabled-datapacks/disabled.zip", disabled.GetProperty("path").GetString());
        var folderPack = packs.Single(item => item.GetProperty("file").GetString() == "local-folder");
        Assert.IsTrue(folderPack.GetProperty("isDirectory").GetBoolean());
        Assert.IsFalse(folderPack.TryGetProperty("sha512", out _));
        CollectionAssert.AreEqual(new byte[] { 20, 21 }, Convert.FromBase64String(folderPack.GetProperty("iconPNG").GetString()!));
    }

    [TestMethod]
    public async Task BackupCollisionNeverOverwritesAndCancellationRemovesOwnedPartialArchive()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await temporary.CreateReadyInstanceAsync();
        var backupsDirectory = Path.Combine(temporary.Paths.GameRoot, InstanceStorage.BackupsFolder);
        Directory.CreateDirectory(backupsDirectory);
        var target = Path.Combine(backupsDirectory, $"{instance.FolderName}_2026-10-23_14-07-08.hakobackup");
        await File.WriteAllTextAsync(target, "keep");
        var backup = new InstanceBackup(store, new InstanceContent(store), () => BackupTime, null);

        var conflict = await backup.CreateAsync(instance.Id);

        Assert.IsFalse(conflict.Succeeded);
        Assert.AreEqual(BackupFailureReason.DestinationConflict, conflict.Failure!.Reason);
        Assert.AreEqual("keep", await File.ReadAllTextAsync(target));

        File.Delete(target);
        using var cancellation = new CancellationTokenSource();
        var cancelledBackup = new InstanceBackup(store, new InstanceContent(store), () => BackupTime, cancellation.Cancel);
        var cancelled = await cancelledBackup.CreateAsync(instance.Id, cancellation.Token);
        Assert.AreEqual(BackupFailureReason.Cancelled, cancelled.Failure!.Reason);
        Assert.AreEqual(0, Directory.GetFiles(backupsDirectory, "*.partial").Length);
        Assert.AreEqual(0, Directory.GetFiles(backupsDirectory, ".*.partial").Length);
        Assert.IsTrue(Directory.Exists(root));
    }

    [TestMethod]
    public async Task BackupDateOptionalFieldsAndLargeFileRoundTripWithoutLoadingArchiveAsOneBuffer()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await temporary.CreateReadyInstanceAsync();
        var largePath = Path.Combine(root, "java", "large-runtime.bin");
        var expectedHash = await WriteLargeFileAsync(largePath, 20 * 1024 * 1024);
        var backup = new InstanceBackup(store, new InstanceContent(store), () => BackupTime, null);

        var result = await backup.CreateAsync(instance.Id);

        Assert.IsTrue(result.Succeeded, result.Failure?.Diagnostic);
        using var stream = File.OpenRead(result.Path!);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var json = await ReadJsonAsync(archive.GetEntry("data.json")!);
        Assert.IsFalse(json.RootElement.TryGetProperty("iconPNG", out _));
        Assert.IsFalse(json.RootElement.TryGetProperty("loaderVersion", out _));
        Assert.IsFalse(json.RootElement.GetProperty("profile").TryGetProperty("fabricConfiguration", out _));
        await using var extracted = archive.GetEntry("java/large-runtime.bin")!.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var buffer = new byte[81920];
        while (true)
        {
            var read = await extracted.ReadAsync(buffer);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        Assert.AreEqual(expectedHash, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    [TestMethod]
    public async Task WorldBackupCollisionNeverOverwritesAndCancellationRemovesPartialArchive()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await temporary.CreateReadyInstanceAsync();
        var world = Path.Combine(root, "minecraft", "saves", "Save");
        Directory.CreateDirectory(world);
        await File.WriteAllBytesAsync(Path.Combine(world, "level.dat"),
            WorldTestFixture.Create("Save", lastPlayed: BackupTime.ToUnixTimeMilliseconds()).Compressed);
        await File.WriteAllTextAsync(Path.Combine(world, "data.json"), "{\"save\":\"kept\"}");
        var content = new InstanceContent(store);
        var worlds = new InstanceWorlds(store);
        var backup = new WorldBackup(store, content, worlds, () => BackupTime, null);

        var created = await backup.CreateAsync(instance.Id, "Save");

        Assert.IsTrue(created.Succeeded, created.Failure?.Diagnostic);
        var expectedPath = Path.Combine(temporary.Paths.GameRoot, InstanceStorage.WorldsFolder,
            $"{instance.FolderName}_Save_2026-10-23_14-07-08.hakoworld");
        Assert.AreEqual(expectedPath, created.Path);
        var originalBytes = await File.ReadAllBytesAsync(expectedPath);
        var conflict = await backup.CreateAsync(instance.Id, "Save");
        Assert.AreEqual(BackupFailureReason.DestinationConflict, conflict.Failure!.Reason);
        CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(expectedPath));

        File.Delete(expectedPath);
        using var cancellation = new CancellationTokenSource();
        var cancelledBackup = new WorldBackup(store, content, worlds, () => BackupTime, cancellation.Cancel);
        var cancelled = await cancelledBackup.CreateAsync(instance.Id, "Save", cancellation.Token);

        Assert.AreEqual(BackupFailureReason.Cancelled, cancelled.Failure!.Reason);
        var outputDirectory = Path.GetDirectoryName(expectedPath)!;
        Assert.AreEqual(0, Directory.GetFiles(outputDirectory, "*.partial").Length);
        Assert.AreEqual(0, Directory.GetFiles(outputDirectory, ".*.partial").Length);
        Assert.AreEqual("{\"save\":\"kept\"}", await File.ReadAllTextAsync(Path.Combine(world, "data.json")));
    }

    private static ModpackPlan CompletedModpackPlan()
    {
        var bytes = Encoding.UTF8.GetBytes("completed modpack archive");
        var plan = new ModpackPlan
        {
            ProjectId = "fixturepack",
            VersionId = "fixtureversion",
            VersionNumber = "1.0",
            PageUrl = new Uri("https://modrinth.com/modpack/fixturepack"),
            Archive = new ModpackFile
            {
                Filename = "fixture.mrpack",
                Url = new Uri("https://cdn.modrinth.com/data/fixturepack/versions/fixtureversion/fixture.mrpack"),
                Size = bytes.Length,
                Hashes = new Dictionary<string, string>
                {
                    ["sha1"] = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(),
                    ["sha512"] = Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant()
                },
                Primary = true
            },
            Manifest = new ModpackManifest
            {
                FormatVersion = 1,
                Game = "minecraft",
                VersionId = "fixtureversion",
                Name = "Fixture pack",
                Dependencies = new Dictionary<string, string> { ["minecraft"] = "1.20.4", ["fabric-loader"] = "0.16.9" }
            },
            Completed = true
        };
        plan.Validate();
        return plan;
    }

    private static byte[] CreateDatapackZip()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("pack.mcmeta").Open()))
            writer.Write("{\"pack\":{\"pack_format\":48,\"description\":\"fixture\"}}");
        return output.ToArray();
    }

    private static async Task<string> WriteLargeFileAsync(string path, int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var buffer = new byte[81920];
        for (var index = 0; index < buffer.Length; index++) buffer[index] = (byte)(index * 31);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var remaining = length;
        while (remaining > 0)
        {
            var count = Math.Min(remaining, buffer.Length);
            await output.WriteAsync(buffer.AsMemory(0, count));
            hash.AppendData(buffer, 0, count);
            remaining -= count;
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<JsonDocument> ReadJsonAsync(ZipArchiveEntry entry)
    {
        await using var stream = entry.Open();
        return await JsonDocument.ParseAsync(stream);
    }

    private static async Task<string> ReadEntryTextAsync(ZipArchiveEntry entry)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static JunctionCleanup CreateJunction(string root, string junction, string target)
    {
        var command = "$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path '"
            + junction.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null";
        using var process = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command }
        }) ?? throw new InvalidOperationException("Could not start PowerShell to create the isolated test junction.");
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd());
        Assert.IsTrue((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0);
        return new JunctionCleanup(root, junction, target);
    }

    private sealed class JunctionCleanup(string root, string junction, string target) : IDisposable
    {
        public void Dispose()
        {
            var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            Assert.IsTrue(Path.GetFullPath(junction).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(Path.GetFullPath(target).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase));
            if ((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0) Directory.Delete(junction);
            Assert.IsTrue(Directory.Exists(target));
        }
    }

    private sealed class CallbackRecycleOperation(Func<string, CancellationToken, Task<RecycleBinItemStatus>> action)
        : IRecycleBinOperation
    {
        public Task<RecycleBinItemStatus> DeleteAsync(string path, CancellationToken cancellationToken) =>
            action(path, cancellationToken);
    }

    private sealed class TemporaryDirectory : IDisposable, IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Hako.Tests", Guid.NewGuid().ToString("N"));
        internal HakoPaths Paths { get; }

        internal TemporaryDirectory()
        {
            Paths = HakoPaths.CreateIsolated(Path.Combine(Root, "Game"), Path.Combine(Root, "Data"));
            Directory.CreateDirectory(Root);
        }

        internal async Task<InstanceStore> LoadStoreAsync(Action? beforeRegistryCommit = null)
        {
            var store = new InstanceStore(Paths, beforeRegistryCommit);
            await store.LoadAsync();
            return store;
        }

        internal async Task<(InstanceStore Store, GameInstance Instance, string Root)> CreateReadyInstanceAsync(
            string name = "Build", InstanceDraft? draft = null, ModpackPlan? plan = null)
        {
            var store = await LoadStoreAsync();
            var sourceDraft = draft ?? new InstanceDraft { Name = name };
            sourceDraft = sourceDraft with { Name = name };
            var instance = store.Create(sourceDraft, "1.20.4", "https://example.test/version", "fixture-sha1",
                javaMajorVersion: 21, modpackPlan: plan);
            await SetReadyAsync(store, instance.Id);
            instance = store.List().Single(item => item.Id == instance.Id);
            return (store, instance, store.GetInstanceDirectory(instance.Id));
        }

        internal async Task SetReadyAsync(InstanceStore store, Guid id)
        {
            await Task.Run(() => store.SetInstallationStatus(id, InstanceInstallationState.Ready, pauseRequested: false));
        }

        public void Dispose()
        {
            var fullRoot = Path.GetFullPath(Root);
            if (Directory.Exists(fullRoot)) Directory.Delete(fullRoot, recursive: true);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

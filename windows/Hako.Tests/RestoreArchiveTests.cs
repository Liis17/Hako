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
    private static readonly byte[] TestIconPNG = [137, 80, 78, 71, 13, 10, 26, 10];

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

        Assert.IsTrue(result.Succeeded, $"{result.Failure?.Reason}: {result.Failure?.Diagnostic}");
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
        var roundTripFixture = CreateFixture(javaExecutable: "bin/javaw.exe");
        var existing = store.Create(new InstanceDraft { Name = "Build" }, "1.20.4",
            roundTripFixture.Manifest.Profile.MetadataURL, roundTripFixture.Manifest.Profile.MetadataSHA1,
            javaMajorVersion: 21);
        store.SetInstallationStatus(existing.Id, InstanceInstallationState.Ready, pauseRequested: false);
        var existingRoot = store.GetInstanceDirectory(existing.Id);
        var versionDirectory = Path.Combine(existingRoot, "minecraft", "versions", "1.20.4");
        Directory.CreateDirectory(versionDirectory);
        await File.WriteAllBytesAsync(Path.Combine(versionDirectory, "1.20.4.json"), roundTripFixture.VersionJson);
        await File.WriteAllBytesAsync(Path.Combine(versionDirectory, "1.20.4.jar"), roundTripFixture.ClientJar);
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
        using (var legacyJson = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(
                   legacy.Manifest, HakoJsonContext.Default.InstanceBackupManifest)))
        {
            Assert.IsFalse(legacyJson.RootElement.TryGetProperty("instanceID", out _));
        }
        await WriteInstanceArchiveAsync(archivePath, legacy);

        var result = await CreateRestore(store).PrepareAsync(archivePath);

        Assert.IsTrue(result.Succeeded, result.Failure?.Diagnostic);
        Assert.AreEqual(existing.Id, result.Staged!.MatchingInstance!.Id);
        Assert.IsFalse(result.Staged.InstanceManifest!.InstanceID.HasValue);
        await CreateRestore(store).DiscardAsync(result.Staged);

        var roundTrip = await CreateRestore(store).PrepareAsync(made.Path!);
        Assert.IsTrue(roundTrip.Succeeded, roundTrip.Failure?.Diagnostic);
        Assert.AreEqual(existing.Id, roundTrip.Staged!.MatchingInstance!.Id);
        Assert.AreEqual(existing.Id, roundTrip.Staged.InstanceManifest!.InstanceID);
        var restored = await CreateRestore(store).CommitInstanceAsync(roundTrip.Staged, "Build Restored");
        Assert.IsTrue(restored.Succeeded, restored.Failure?.Diagnostic);
        Assert.AreEqual("Build Restored", restored.Instance!.Name);
        Assert.AreEqual(InstanceInstallationState.Queued, restored.Instance.State);
    }

    [TestMethod]
    public async Task NewRestoreChoosesUniqueBuildNameWhenRequestedNameExists()
    {
        await using var temporary = new TemporaryDirectory();
        var store = temporary.CreateStore();
        await store.LoadAsync();
        var existing = store.Create(new InstanceDraft { Name = "Build" }, "1.20.4");
        var archivePath = Path.Combine(temporary.Root, "unique-name.hakobackup");
        await WriteInstanceArchiveAsync(archivePath, CreateFixture(javaExecutable: "Contents/Home/bin/java"));
        var restore = CreateRestore(store);
        var prepared = await restore.PrepareAsync(archivePath);
        Assert.IsTrue(prepared.Succeeded, $"{prepared.Failure?.Reason}: {prepared.Failure?.Diagnostic}");

        var result = await restore.CommitInstanceAsync(prepared.Staged!, "Build");

        Assert.IsTrue(result.Succeeded, result.Failure?.Diagnostic);
        Assert.AreNotEqual(existing.Id, result.Instance!.Id);
        Assert.AreEqual("Build 2", result.Instance.Name);
        Assert.AreEqual("Build_2", result.Instance.FolderName);
        Assert.IsTrue(Directory.Exists(store.GetInstanceDirectory(result.Instance.Id)));
    }

    [TestMethod]
    public async Task NativeJavaRestoresReadyBuildAndReplacementPreservesIdentityAndCreationDate()
    {
        await using var temporary = new TemporaryDirectory();
        var store = temporary.CreateStore();
        await store.LoadAsync();
        var original = store.Create(new InstanceDraft { Name = "Build" }, "1.20.4");
        store.SetInstallationStatus(original.Id, InstanceInstallationState.Ready, pauseRequested: false);
        var originalDirectory = store.GetInstanceDirectory(original.Id);
        await File.WriteAllTextAsync(Path.Combine(originalDirectory, "old.txt"), "old");
        var archivePath = Path.Combine(temporary.Root, "native.hakobackup");
        await WriteInstanceArchiveAsync(archivePath, CreateFixture(original.Id, "bin/javaw.exe"),
            additionalEntries: AddNativeJavaEntries);

        var restore = CreateRestore(store);
        var prepared = await restore.PrepareAsync(archivePath);
        Assert.IsTrue(prepared.Succeeded, prepared.Failure?.Diagnostic);
        Assert.IsFalse(prepared.Staged!.NeedsJavaInstallation);
        Assert.AreEqual(GameArchitecture.X64, prepared.Staged.Architecture);

        var committed = await restore.CommitInstanceAsync(prepared.Staged, "Ignored name", original.Id);

        Assert.IsTrue(committed.Succeeded, committed.Failure?.Diagnostic);
        Assert.AreEqual(original.Id, committed.Instance!.Id);
        Assert.AreEqual(original.CreatedAt, committed.Instance.CreatedAt);
        Assert.AreEqual("Build", committed.Instance.Name);
        Assert.AreEqual("X64", committed.Instance.Architecture);
        Assert.AreEqual(InstanceInstallationState.Ready, committed.Instance.State);
        Assert.IsFalse(File.Exists(Path.Combine(originalDirectory, "old.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(originalDirectory, "java", "bin", "javaw.exe")));
        Assert.IsTrue(prepared.Staged.IsCompleted);
    }

    [TestMethod]
    public async Task QueuedVanillaFabricAndCompletedModpackRestoreThroughInstallationCoordinator()
    {
        await using var temporary = new TemporaryDirectory();
        var store = temporary.CreateStore();
        await store.LoadAsync();
        var fixtures = new[]
        {
            ("Vanilla", CreateFixture(javaExecutable: "bin/javaw.exe")),
            ("Fabric", CreateFabricFixture("Fabric", includeModpack: false)),
            ("Modpack", CreateFabricFixture("Modpack", includeModpack: true))
        };
        var restored = new List<GameInstance>();
        foreach (var (name, fixture) in fixtures)
        {
            var archivePath = Path.Combine(temporary.Root, name + ".hakobackup");
            await WriteInstanceArchiveAsync(archivePath, fixture);
            var restore = CreateRestore(store);
            var prepared = await restore.PrepareAsync(archivePath);
            Assert.IsTrue(prepared.Succeeded, $"{name}: {prepared.Failure?.Reason} {prepared.Failure?.Diagnostic}");
            Assert.IsTrue(prepared.Staged!.NeedsJavaInstallation);
            var committed = await restore.CommitInstanceAsync(prepared.Staged, name);
            Assert.IsTrue(committed.Succeeded, committed.Failure?.Diagnostic);
            Assert.AreEqual(InstanceInstallationState.Queued, committed.Instance!.State);
            Assert.IsNull(committed.Instance.Architecture);
            Assert.AreEqual("bin/javaw.exe", committed.Instance.JavaExecutable);
            Assert.IsFalse(Directory.Exists(Path.Combine(store.GetInstanceDirectory(committed.Instance.Id), "java")));
            restored.Add(committed.Instance);
        }

        var fabricConfigurations = new Dictionary<string, FabricConfiguration?>(StringComparer.Ordinal);
        var installedModpacks = 0;
        var coordinator = temporary.Track(new InstallationCoordinator(
            store,
            (version, pinned, _) => Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64)),
            (_, _, _, _) => Task.FromResult(new InstallationResult(GameArchitecture.X64, "bin/javaw.exe", 21, false)),
            timeProvider: null,
            installFabric: (_, root, fabric, _, _) =>
            {
                fabricConfigurations[Path.GetFileName(root)] = fabric;
                return Task.FromResult(new InstallationResult(GameArchitecture.X64, "bin/javaw.exe", 21, false));
            },
            validateModpack: (root, instance) =>
            {
                var plan = ModpackPlan.Read(root);
                if (plan is not null) Assert.AreEqual(instance.ModpackVersionID, plan.VersionId);
                return plan;
            },
            installModpack: (_, _, _) =>
            {
                installedModpacks++;
                return Task.CompletedTask;
            }));

        await coordinator.StartAsync();
        await TestData.WaitUntilAsync(() => coordinator.ActiveInstanceId is null && coordinator.Progress.Count == 0
            && restored.All(instance => store.Instances.Single(item => item.Id == instance.Id).State == InstanceInstallationState.Ready));
        Assert.AreEqual(0, coordinator.Progress.Count);
        Assert.AreEqual(3, fabricConfigurations.Count);
        Assert.IsNull(fabricConfigurations["Vanilla"]);
        Assert.IsNotNull(fabricConfigurations["Fabric"]?.Api);
        Assert.IsNull(fabricConfigurations["Modpack"]?.Api);
        Assert.AreEqual(1, installedModpacks);
        Assert.IsNull(coordinator.ActiveInstanceId);
        Assert.AreEqual("version", store.Instances.Single(item => item.Name == "Modpack").ModpackVersionID);
    }

    [TestMethod]
    public async Task WorldRestoreCopiesAndReplacesAndRejectsLockedDestinationWithoutDataLoss()
    {
        await using var temporary = new TemporaryDirectory();
        var failWorldMove = 0;
        var store = temporary.Track(new InstanceStore(temporary.Paths, null, null, (from, to) =>
        {
            if (Volatile.Read(ref failWorldMove) == 1
                && Path.GetFileName(from) == "world"
                && Path.GetFileName(to) == "Restored")
            {
                throw new IOException("Injected world move failure.");
            }
        }));
        await store.LoadAsync();
        var target = store.Create(new InstanceDraft { Name = "World target" }, "1.20.4");
        store.SetInstallationStatus(target.Id, InstanceInstallationState.Ready, pauseRequested: false);
        var instanceRoot = store.GetInstanceDirectory(target.Id);
        var archivePath = Path.Combine(temporary.Root, "world.hakoworld");
        await WriteWorldArchiveAsync(archivePath, target);
        var restore = CreateRestore(store);
        var first = await restore.PrepareAsync(archivePath);
        Assert.IsTrue(first.Succeeded, first.Failure?.Diagnostic);

        var copied = await restore.CommitWorldAsync(first.Staged!, target.Id, "Copied World", "Restored", replace: false);

        Assert.IsTrue(copied.Succeeded, copied.Failure?.Diagnostic);
        Assert.AreEqual("Restored", copied.WorldFolderName);
        var worldPath = Path.Combine(instanceRoot, "minecraft", "saves", "Restored");
        Assert.AreEqual("Copied World", WorldMetadata.ReadFile(Path.Combine(worldPath, "level.dat")).Name);
        Assert.IsTrue(File.Exists(Path.Combine(worldPath, "new-only.txt")));
        await File.WriteAllTextAsync(Path.Combine(worldPath, "old-only.txt"), "retired");

        var copyAgain = await restore.PrepareAsync(archivePath);
        Assert.IsTrue(copyAgain.Succeeded, copyAgain.Failure?.Diagnostic);
        var copiedAgain = await restore.CommitWorldAsync(copyAgain.Staged!, target.Id, "Another Copy", "Restored", replace: false);
        Assert.IsTrue(copiedAgain.Succeeded, copiedAgain.Failure?.Diagnostic);
        Assert.AreEqual("Restored 2", copiedAgain.WorldFolderName);
        Assert.AreEqual("Another Copy", WorldMetadata.ReadFile(Path.Combine(instanceRoot, "minecraft", "saves", "Restored 2", "level.dat")).Name);

        var second = await restore.PrepareAsync(archivePath);
        Assert.IsTrue(second.Succeeded, second.Failure?.Diagnostic);
        var replaced = await restore.CommitWorldAsync(second.Staged!, target.Id, "Replacement", "Restored", replace: true);
        Assert.IsTrue(replaced.Succeeded, replaced.Failure?.Diagnostic);
        Assert.AreEqual("Replacement", WorldMetadata.ReadFile(Path.Combine(worldPath, "level.dat")).Name);
        Assert.IsFalse(File.Exists(Path.Combine(worldPath, "old-only.txt")));
        await File.WriteAllTextAsync(Path.Combine(worldPath, "old-only.txt"), "preserve after rollback");

        await File.WriteAllTextAsync(Path.Combine(worldPath, "locked.txt"), "keep");
        var third = await restore.PrepareAsync(archivePath);
        Assert.IsTrue(third.Succeeded, third.Failure?.Diagnostic);
        using (new FileStream(Path.Combine(worldPath, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failed = await restore.CommitWorldAsync(third.Staged!, target.Id, "Blocked", "Restored", replace: true);
            Assert.IsFalse(failed.Succeeded);
        }
        Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Combine(worldPath, "locked.txt")));
        Assert.AreEqual("Replacement", WorldMetadata.ReadFile(Path.Combine(worldPath, "level.dat")).Name);
        await restore.DiscardAsync(third.Staged!);

        var fourth = await restore.PrepareAsync(archivePath);
        Assert.IsTrue(fourth.Succeeded, fourth.Failure?.Diagnostic);
        Volatile.Write(ref failWorldMove, 1);
        var rollback = await restore.CommitWorldAsync(fourth.Staged!, target.Id, "Failed Replacement", "Restored", replace: true);
        Volatile.Write(ref failWorldMove, 0);

        Assert.IsFalse(rollback.Succeeded);
        Assert.AreEqual(RestoreArchiveFailureReason.OperationFailed, rollback.Failure!.Reason);
        Assert.AreEqual("preserve after rollback", await File.ReadAllTextAsync(Path.Combine(worldPath, "old-only.txt")));
        Assert.AreEqual("Replacement", WorldMetadata.ReadFile(Path.Combine(worldPath, "level.dat")).Name);
        Assert.IsTrue(Directory.Exists(Path.Combine(fourth.Staged!.DirectoryPath, "world")));
        await restore.DiscardAsync(fourth.Staged);
    }

    [TestMethod]
    public async Task RegistryWriteFailureRestoresOriginalAndKeepsStagingDiscardable()
    {
        await using var temporary = new TemporaryDirectory();
        var failRegistryWrite = 0;
        var store = temporary.Track(new InstanceStore(temporary.Paths, () =>
        {
            if (Volatile.Read(ref failRegistryWrite) == 1) throw new IOException("Injected registry failure.");
        }));
        await store.LoadAsync();
        var archivePath = Path.Combine(temporary.Root, "rollback.hakobackup");
        await WriteInstanceArchiveAsync(archivePath, CreateFixture(javaExecutable: "bin/javaw.exe"));
        var restore = CreateRestore(store);
        var prepared = await restore.PrepareAsync(archivePath);
        Assert.IsTrue(prepared.Succeeded, prepared.Failure?.Diagnostic);
        var staged = prepared.Staged!;
        Volatile.Write(ref failRegistryWrite, 1);

        var committed = await restore.CommitInstanceAsync(staged, "Restored");

        Assert.IsFalse(committed.Succeeded);
        Assert.AreEqual(RestoreArchiveFailureReason.OperationFailed, committed.Failure!.Reason);
        Assert.AreEqual(0, store.Instances.Count);
        Assert.IsFalse(Directory.Exists(Path.Combine(store.GameRoot, "Restored")));
        Assert.IsTrue(Directory.Exists(staged.DirectoryPath));
        await restore.DiscardAsync(staged);
    }

    [TestMethod]
    public async Task LockedBuildFilesPreventReplacementWithoutChangingRegistryOrFiles()
    {
        await using var temporary = new TemporaryDirectory();
        var store = temporary.CreateStore();
        await store.LoadAsync();
        var original = store.Create(new InstanceDraft { Name = "Build" }, "1.20.4");
        store.SetInstallationStatus(original.Id, InstanceInstallationState.Ready, pauseRequested: false);
        var originalRoot = store.GetInstanceDirectory(original.Id);
        var lockedFile = Path.Combine(originalRoot, "locked.txt");
        await File.WriteAllTextAsync(lockedFile, "preserve");
        var archivePath = Path.Combine(temporary.Root, "replace.hakobackup");
        await WriteInstanceArchiveAsync(archivePath, CreateFixture(original.Id, "bin/javaw.exe"),
            additionalEntries: AddNativeJavaEntries);
        var restore = CreateRestore(store);
        var prepared = await restore.PrepareAsync(archivePath);
        Assert.IsTrue(prepared.Succeeded, prepared.Failure?.Diagnostic);
        var staged = prepared.Staged!;

        RestoreArchiveCommitResult failed;
        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failed = await restore.CommitInstanceAsync(staged, "Ignored", original.Id);
        }

        Assert.IsFalse(failed.Succeeded);
        Assert.AreEqual(original.Id, store.Instances.Single().Id);
        Assert.AreEqual("preserve", await File.ReadAllTextAsync(lockedFile));
        Assert.IsTrue(Directory.Exists(staged.DirectoryPath));
        await restore.DiscardAsync(staged);
    }

    [TestMethod]
    public async Task RegistryCommitRollbackFailureKeepsUnregisteredFilesForRecovery()
    {
        await using var temporary = new TemporaryDirectory();
        var failRegistryWrite = 0;
        var store = temporary.Track(new InstanceStore(
            temporary.Paths,
            () =>
            {
                if (Volatile.Read(ref failRegistryWrite) == 1) throw new IOException("Injected registry failure.");
            },
            null,
            (from, to) =>
            {
                if (Path.GetFileName(from) == "Rollback"
                    && Path.GetFileName(to).StartsWith(".hako-restore-", StringComparison.Ordinal))
                {
                    throw new IOException("Injected rollback failure.");
                }
            }));
        await store.LoadAsync();
        var original = store.Create(new InstanceDraft { Name = "Rollback" }, "1.20.4");
        store.SetInstallationStatus(original.Id, InstanceInstallationState.Ready, pauseRequested: false);
        var originalRoot = store.GetInstanceDirectory(original.Id);
        await File.WriteAllTextAsync(Path.Combine(originalRoot, "original.txt"), "original files");
        var archivePath = Path.Combine(temporary.Root, "rollback-failure.hakobackup");
        await WriteInstanceArchiveAsync(archivePath, CreateFixture(original.Id, javaExecutable: "bin/javaw.exe"),
            additionalEntries: AddNativeJavaEntries);
        var restore = CreateRestore(store);
        var prepared = await restore.PrepareAsync(archivePath);
        Assert.IsTrue(prepared.Succeeded, prepared.Failure?.Diagnostic);
        var staged = prepared.Staged!;
        Volatile.Write(ref failRegistryWrite, 1);

        var result = await restore.CommitInstanceAsync(staged, "Ignored", original.Id);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(RestoreArchiveFailureReason.RollbackFailed, result.Failure!.Reason);
        StringAssert.Contains(result.Failure.Diagnostic, Path.Combine(store.GameRoot, "Rollback"));
        Assert.AreEqual(original.Id, store.Instances.Single().Id);
        Assert.IsTrue(File.Exists(Path.Combine(store.GameRoot, "Rollback", "icon.png")));
        var retired = Directory.GetDirectories(store.GameRoot, ".hako-retired-*", SearchOption.TopDirectoryOnly).Single();
        Assert.AreEqual("original files", await File.ReadAllTextAsync(Path.Combine(retired, "original.txt")));
        Assert.IsFalse(Directory.Exists(staged.DirectoryPath));
        await store.LoadAsync();
        await store.DeleteStagingCleanupTask!;
        Assert.IsTrue(Directory.Exists(retired));
        await restore.DiscardAsync(staged);
        Assert.IsTrue(staged.IsCompleted);
    }

    [TestMethod]
    public async Task RetiredDirectoryDeleteFailureKeepsCommittedReplacementSuccessfulAndLoadCleansIt()
    {
        await using var temporary = new TemporaryDirectory();
        var store = temporary.Track(new InstanceStore(temporary.Paths, null, null,
            beforeRetiredDelete: _ => throw new IOException("Injected retired delete failure.")));
        await store.LoadAsync();
        var original = store.Create(new InstanceDraft { Name = "Build" }, "1.20.4");
        store.SetInstallationStatus(original.Id, InstanceInstallationState.Ready, pauseRequested: false);
        var archivePath = Path.Combine(temporary.Root, "retired.hakobackup");
        await WriteInstanceArchiveAsync(archivePath, CreateFixture(original.Id, "bin/javaw.exe"),
            additionalEntries: AddNativeJavaEntries);
        var restore = CreateRestore(store);
        var prepared = await restore.PrepareAsync(archivePath);
        Assert.IsTrue(prepared.Succeeded, $"{prepared.Failure?.Reason}: {prepared.Failure?.Diagnostic}");

        var result = await restore.CommitInstanceAsync(prepared.Staged!, "Ignored", original.Id);

        Assert.IsTrue(result.Succeeded, result.Failure?.Diagnostic);
        Assert.IsTrue(result.CleanupWarning);
        Assert.AreEqual(1, Directory.GetDirectories(store.GameRoot, ".hako-retired-*").Length);
        await store.LoadAsync();
        await store.DeleteStagingCleanupTask!;
        Assert.AreEqual(0, Directory.GetDirectories(store.GameRoot, ".hako-retired-*").Length);
    }

    [TestMethod]
    public async Task LoadCleansAbandonedRestoreAndRetiredDirectories()
    {
        await using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Paths.GameRoot);
        var abandonedRestore = Path.Combine(temporary.Paths.GameRoot, ".hako-restore-" + Guid.NewGuid().ToString("N"));
        var abandonedRetired = Path.Combine(temporary.Paths.GameRoot, ".hako-retired-" + Guid.NewGuid().ToString("N") + ".committed");
        var recoveryRetired = Path.Combine(temporary.Paths.GameRoot, ".hako-retired-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(abandonedRestore);
        Directory.CreateDirectory(abandonedRetired);
        Directory.CreateDirectory(recoveryRetired);
        var store = temporary.CreateStore();

        await store.LoadAsync();
        await store.DeleteStagingCleanupTask!;

        Assert.IsFalse(Directory.Exists(abandonedRestore));
        Assert.IsFalse(Directory.Exists(abandonedRetired));
        Assert.IsTrue(Directory.Exists(recoveryRetired));
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
            IconSymbol = string.Empty,
            IconPNG = (byte[])TestIconPNG.Clone(),
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

    private static InstanceFixture CreateFabricFixture(string name, bool includeModpack)
    {
        var fixture = CreateFixture(javaExecutable: "bin/javaw.exe");
        var fabricProfile = new FabricProfile
        {
            Id = "fabric-loader-0.16.9-1.20.4",
            InheritsFrom = "1.20.4",
            MainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient"
        };
        var profileBytes = JsonSerializer.SerializeToUtf8Bytes(fabricProfile, HakoJsonContext.Default.FabricProfile);
        byte[] apiBytes = [0x50, 0x4b, 0x03, 0x04, 5, 6, 7, 8];
        var api = new FabricApiDescriptor(
            "P7dR8mSH",
            "api-version",
            "0.119.3+1.20.4",
            "stable",
            "fabric-api.jar",
            "https://cdn.modrinth.com/data/P7dR8mSH/versions/api-version/fabric-api.jar",
            apiBytes.Length,
            Convert.ToHexString(SHA1.HashData(apiBytes)).ToLowerInvariant(),
            Convert.ToHexString(SHA512.HashData(apiBytes)).ToLowerInvariant());
        var mod = new InstanceBackupManifest.Mod("fabric-api.jar", true, "modrinth", "P7dR8mSH", "api-version");
        var manifest = fixture.Manifest with
        {
            Name = name,
            FolderName = name,
            ModLoader = "fabric",
            LoaderVersion = "0.16.9",
            ModCount = 1,
            Mods = [mod],
            Profile = fixture.Manifest.Profile with
            {
                FabricConfiguration = new FabricConfiguration("0.16.9", api),
                FabricProfileSHA1 = Convert.ToHexString(SHA1.HashData(profileBytes)).ToLowerInvariant()
            }
        };
        return fixture with
        {
            Manifest = manifest,
            FabricProfile = profileBytes,
            FabricApiJar = apiBytes,
            ModpackPlan = includeModpack ? CreateCompletedModpackPlan() : null
        };
    }

    private static ModpackPlan CreateCompletedModpackPlan() => new()
    {
        FormatVersion = 1,
        ProjectId = "project",
        VersionId = "version",
        VersionNumber = "1.0",
        PageUrl = new Uri("https://modrinth.com/modpack/project"),
        Archive = new ModpackFile
        {
            Filename = "fixture.mrpack",
            Url = new Uri("https://cdn.modrinth.com/data/project/versions/version/fixture.mrpack"),
            Size = 1,
            Hashes = new Dictionary<string, string> { ["sha1"] = new string('a', 40), ["sha512"] = new string('b', 128) },
            Primary = true
        },
        Manifest = new ModpackManifest
        {
            FormatVersion = 1,
            Game = "minecraft",
            VersionId = "pack-version",
            Name = "Completed pack",
            Files = [],
            Dependencies = new Dictionary<string, string>
            {
                ["minecraft"] = "1.20.4",
                ["fabric-loader"] = "0.16.9"
            }
        },
        Completed = true
    };

    private static PreparedInstallation CreatePrepared(string versionId, GameArchitecture architecture)
    {
        var version = new MojangVersionEntry
        {
            Id = versionId,
            Type = "release",
            Url = "https://piston-meta.mojang.com/v1/packages/fixture/1.20.4.json",
            Sha1 = new string('a', 40)
        };
        var manifest = new MojangVersionMetadata
        {
            Id = versionId,
            JavaVersion = new MojangJavaRequirement { Component = "java-runtime-gamma", MajorVersion = 21 }
        };
        return new PreparedInstallation(version, architecture, manifest, [],
            new MojangJavaRuntimePackage(), new MojangJavaRuntimeManifest(), [], []);
    }

    private static void AddNativeJavaEntries(ZipArchive archive)
    {
        AddEntry(archive, "java/bin/javaw.exe", CreatePeImage(0x8664));
        AddEntry(archive, "java/release", Encoding.UTF8.GetBytes("JAVA_VERSION=\"21.0.6\"\n"));
    }

    private static byte[] CreatePeImage(ushort machine)
    {
        var bytes = new byte[72];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x3c), 64);
        "PE\0\0"u8.CopyTo(bytes.AsSpan(64));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), machine);
        return bytes;
    }

    private static async Task WriteWorldArchiveAsync(string path, GameInstance sourceInstance)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var worldMetadata = WorldTestFixture.Create(name: "Archive World", version: "1.21.1", gameType: 0, lastPlayed: 1_000);
        var manifest = new WorldBackupManifest
        {
            BackupCreatedAt = new DateTimeOffset(2026, 10, 23, 14, 7, 8, TimeSpan.Zero),
            Name = "Archive World",
            FolderName = "SourceWorld",
            SaveVersion = "1.21.1",
            GameType = 0,
            LastPlayed = DateTimeOffset.UnixEpoch.AddSeconds(1),
            SourceInstance = new WorldBackupManifest.SourceInstanceData(sourceInstance.Id, sourceInstance.Name,
                sourceInstance.FolderName, sourceInstance.VersionID, "vanilla", null),
            Datapacks = []
        };
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
        AddEntry(archive, "data.json", JsonSerializer.SerializeToUtf8Bytes(manifest, HakoJsonContext.Default.WorldBackupManifest));
        AddEntry(archive, "world/level.dat", worldMetadata.Compressed);
        AddEntry(archive, "world/new-only.txt", Encoding.UTF8.GetBytes("new"));
        archive.Dispose();
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
        if (fixture.FabricProfile is not null) AddEntry(archive, "minecraft/.hako-fabric.json", fixture.FabricProfile);
        if (fixture.FabricApiJar is not null) AddEntry(archive, "minecraft/mods/fabric-api.jar", fixture.FabricApiJar);
        if (fixture.ModpackPlan is not null)
        {
            AddEntry(archive, ModpackPlan.Filename,
                JsonSerializer.SerializeToUtf8Bytes(fixture.ModpackPlan, HakoJsonContext.Default.ModpackPlan));
        }
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

    private sealed record InstanceFixture(
        InstanceBackupManifest Manifest,
        byte[] VersionJson,
        byte[] ClientJar,
        byte[]? FabricProfile = null,
        byte[]? FabricApiJar = null,
        ModpackPlan? ModpackPlan = null);

    private sealed class FixedArchitectureProvider : IHostArchitectureProvider
    {
        public HostArchitecture GetHostArchitecture() => new(HostMachineArchitecture.X64, Amd64UserEnabled: true);
    }
}

using Hako.Core;
using Hako.Core.Instances;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Hako.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class InstanceStoreTests
{
    [TestMethod]
    public async Task NamesAreTrimmedAndRestrictedToOneToSixtyAsciiLettersDigitsAndSpaces()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);

        Assert.AreEqual("My_Build_2", store.ValidateName("  My Build 2  "));
        foreach (var value in new[] { "", " ", new string('a', 61), "Build!", "Сборка" })
        {
            Assert.ThrowsExactly<InstanceStorageException>(() => store.ValidateName(value));
        }
    }

    [TestMethod]
    public async Task ReservedAndCaseInsensitiveNameCollisionsAreRejected()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        foreach (var value in new[] { "backups", "WORLDS", "con", "PRN", "Aux", "nul", "COM1", "com9", "LPT1", "lpt9" })
        {
            Assert.ThrowsExactly<InstanceStorageException>(() => store.ValidateName(value));
        }

        var created = store.Create(new InstanceDraft { Name = "My Build" }, "1.20.4");
        Assert.ThrowsExactly<InstanceStorageException>(() => store.ValidateName("my build"));
        Assert.AreEqual("My_Build", store.ValidateName("My Build", excluding: created.Id));
    }

    [TestMethod]
    public async Task CaseInsensitiveFilesystemCollisionIsRejected()
    {
        await using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Paths.GameRoot);
        Directory.CreateDirectory(Path.Combine(temporary.Paths.GameRoot, "Loose_Files"));
        var store = await LoadStoreAsync(temporary);

        Assert.ThrowsExactly<InstanceStorageException>(() => store.ValidateName("loose files"));
    }

    [TestMethod]
    public async Task RegistryIsCamelCaseAndContainsFullProfileState()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var instance = store.Create(new InstanceDraft
        {
            Name = "Fabric Build",
            ModLoader = InstanceModLoader.Fabric,
            FabricConfiguration = new FabricConfiguration("0.16.9"),
            ArgumentSource = LaunchArgumentSource.Custom,
            OfflineMode = true,
            OfflineUsername = "Player_2",
            Parameters = new InstanceParameters { JavaArguments = "-Dtest=yes", MaximumMemoryMiB = 2048 }
        }, "1.21.4", "https://example.test/version", "sha1");

        var json = File.ReadAllText(Path.Combine(temporary.Paths.GameRoot, "instances.json"), Encoding.UTF8);
        StringAssert.Contains(json, "\"formatVersion\": 1");
        StringAssert.Contains(json, "\"folderName\": \"Fabric_Build\"");
        StringAssert.Contains(json, "\"argumentSource\": \"custom\"");
        StringAssert.Contains(json, "\"modLoader\": \"fabric\"");
        StringAssert.Contains(json, "\"state\": \"queued\"");
        StringAssert.Contains(json, "\"pauseRequested\": false");
        StringAssert.Contains(json, "\"fabricConfiguration\":");
        Assert.AreEqual(instance, store.List().Single());
    }

    [TestMethod]
    public async Task FailedAtomicWriteLeavesPreviousRegistryAndRemovesTemporaryFile()
    {
        await using var temporary = new TemporaryDirectory();
        var initial = await LoadStoreAsync(temporary);
        var instance = initial.Create(new InstanceDraft { Name = "Original" }, "1.20.4");
        await SetStateAsync(temporary.Paths, initial, InstanceInstallationState.Ready);
        var registryPath = Path.Combine(temporary.Paths.GameRoot, "instances.json");
        var original = File.ReadAllBytes(registryPath);
        var failing = await LoadStoreAsync(temporary, () => throw new IOException("Injected before atomic replace."));

        Assert.ThrowsExactly<IOException>(() => failing.Rename(instance.Id, "Changed"));

        CollectionAssert.AreEqual(original, File.ReadAllBytes(registryPath));
        Assert.IsTrue(Directory.Exists(Path.Combine(temporary.Paths.GameRoot, "Original")));
        Assert.IsFalse(Directory.Exists(Path.Combine(temporary.Paths.GameRoot, "Changed")));
        Assert.AreEqual(0, Directory.GetFiles(temporary.Paths.GameRoot, "instances.json.*.tmp").Length);
    }

    [TestMethod]
    public async Task CorruptAndUnknownVersionRegistriesAreNeverOverwritten()
    {
        await using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Paths.GameRoot);
        var registryPath = Path.Combine(temporary.Paths.GameRoot, "instances.json");
        var store = await LoadStoreAsync(temporary);
        var instance = store.Create(new InstanceDraft { Name = "Protected Build" }, "1.20.4");
        var update = new InstanceDraft { Name = "Renamed Build" };
        foreach (var content in new[] { "{ broken", "{\"formatVersion\":2,\"instances\":[]}" })
        {
            File.WriteAllText(registryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var original = File.ReadAllBytes(registryPath);
            await store.LoadAsync();
            Assert.IsNotNull(store.LoadError);
            var expectedError = content.Contains("formatVersion", StringComparison.Ordinal)
                ? InstanceRegistryError.UnsupportedVersion
                : InstanceRegistryError.Corrupt;
            Assert.AreEqual(expectedError, store.LoadError!.Error, store.LoadError.Message);
            Assert.AreEqual("InstanceRegistryDamaged", store.LoadError!.ResourceKey);
            foreach (var operation in new Action[]
            {
                () => store.Create(new InstanceDraft { Name = "Blocked" }, "1.20.4"),
                () => store.Rename(instance.Id, "Blocked"),
                () => store.Update(instance.Id, update),
                () => store.UpdateSettings(instance.Id, update),
                () => store.Delete(instance.Id)
            })
            {
                var exception = Assert.ThrowsExactly<InstanceRegistryException>(operation);
                Assert.AreEqual(expectedError, exception.Error);
            }

            CollectionAssert.AreEqual(original, File.ReadAllBytes(registryPath));
            Assert.IsFalse(Directory.Exists(Path.Combine(temporary.Paths.GameRoot, "Blocked")));
        }
    }

    [TestMethod]
    public async Task InvalidRegistryEntriesAreReportedAsCorrupt()
    {
        await using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Paths.GameRoot);
        var registryPath = Path.Combine(temporary.Paths.GameRoot, "instances.json");
        var valid = new GameInstance { Name = "First", FolderName = "First" };
        var invalidRegistries = new[]
        {
            new InstanceRegistryFile(1, [valid, valid with { Name = "Second", FolderName = "Second" }]),
            new InstanceRegistryFile(1, [valid, valid with { Id = Guid.NewGuid(), Name = "Second", FolderName = "first" }]),
            new InstanceRegistryFile(1, [valid with { Name = "Bad!", FolderName = "Bad!" }]),
            new InstanceRegistryFile(1, [valid with
            {
                Name = "Fabric",
                FolderName = "Fabric",
                ModLoader = InstanceModLoader.Fabric,
                FabricConfiguration = new FabricConfiguration("")
            }])
        };

        foreach (var invalid in invalidRegistries)
        {
            var original = JsonSerializer.SerializeToUtf8Bytes(invalid, HakoJsonContext.Default.InstanceRegistryFile);
            await File.WriteAllBytesAsync(registryPath, original);
            var store = temporary.CreateStore();

            await store.LoadAsync();

            Assert.AreEqual(InstanceRegistryError.Corrupt, store.LoadError?.Error);
            Assert.ThrowsExactly<InstanceRegistryException>(() => store.Create(new InstanceDraft { Name = "Blocked" }, "1.20.4"));
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(registryPath));
        }
    }

    [TestMethod]
    public async Task RegistrySymlinkIsReportedAsLoadErrorInsteadOfThrowingFromConstruction()
    {
        await using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Paths.GameRoot);
        var target = Path.Combine(temporary.Path, "external-instances.json");
        await File.WriteAllTextAsync(target, "{\"formatVersion\":1,\"instances\":[]}");
        var registryPath = Path.Combine(temporary.Paths.GameRoot, "instances.json");
        try
        {
            File.CreateSymbolicLink(registryPath, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("This environment does not allow creating a file symlink: " + exception.Message);
        }

        var store = temporary.CreateStore();
        await store.LoadAsync();

        Assert.IsNotNull(store.LoadError);
        Assert.AreEqual(InstanceRegistryError.ReadFailure, store.LoadError.Error);
        Assert.AreEqual("InstanceRegistryUnavailable", store.LoadError.ResourceKey);
        Assert.AreEqual("{\"formatVersion\":1,\"instances\":[]}", await File.ReadAllTextAsync(target));
    }

    [TestMethod]
    public async Task InstanceListsUseTheLoadedSnapshotAndEventFailuresDoNotFailCommittedOperations()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        store.InstancesChanged += (_, _) => throw new InvalidOperationException("Injected observer failure.");
        store.PropertyChanged += (_, _) => throw new InvalidOperationException("Injected observer failure.");

        var created = store.Create(new InstanceDraft { Name = "Snapshot" }, "1.20.4");
        Assert.AreEqual(created, store.List().Single());

        var registryPath = Path.Combine(temporary.Paths.GameRoot, "instances.json");
        await File.WriteAllTextAsync(registryPath, "{ broken");
        Assert.AreEqual(created, store.Instances.Single());

        await store.LoadAsync();

        Assert.AreEqual(0, store.List().Count);
        Assert.AreEqual(InstanceRegistryError.Corrupt, store.LoadError?.Error);
    }

    [TestMethod]
    public async Task TryBeginOperationsReturnRegistryReasonsBeforeLoadAndAfterLoadFailure()
    {
        await using var temporary = new TemporaryDirectory();
        var store = temporary.CreateStore();
        var id = Guid.NewGuid();

        Assert.IsFalse(store.TryBeginLaunch(id, out var launchBeforeLoad, out var launchBeforeLoadReason));
        Assert.IsNull(launchBeforeLoad);
        Assert.AreEqual(InstanceStorageError.InstanceRegistryNotLoaded, launchBeforeLoadReason);
        Assert.IsFalse(store.TryBeginContent(id, out var contentBeforeLoad, out var contentBeforeLoadReason));
        Assert.IsNull(contentBeforeLoad);
        Assert.AreEqual(InstanceStorageError.InstanceRegistryNotLoaded, contentBeforeLoadReason);

        Directory.CreateDirectory(temporary.Paths.GameRoot);
        await File.WriteAllTextAsync(Path.Combine(temporary.Paths.GameRoot, "instances.json"), "{ broken");
        await store.LoadAsync();

        Assert.IsFalse(store.TryBeginLaunch(id, out var launchAfterFailure, out var launchAfterFailureReason));
        Assert.IsNull(launchAfterFailure);
        Assert.AreEqual(InstanceStorageError.InstanceRegistryUnavailable, launchAfterFailureReason);
        Assert.IsFalse(store.TryBeginContent(id, out var contentAfterFailure, out var contentAfterFailureReason));
        Assert.IsNull(contentAfterFailure);
        Assert.AreEqual(InstanceStorageError.InstanceRegistryUnavailable, contentAfterFailureReason);
    }

    [TestMethod]
    public async Task CreateFailureRemovesOnlyItsNewFolder()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary, () => throw new IOException("Injected registry failure."));

        Assert.ThrowsExactly<IOException>(() => store.Create(new InstanceDraft { Name = "New Build" }, "1.20.4"));

        Assert.IsFalse(Directory.Exists(Path.Combine(temporary.Paths.GameRoot, "New_Build")));
        Assert.IsFalse(File.Exists(Path.Combine(temporary.Paths.GameRoot, "instances.json")));
        Assert.AreEqual(0, Directory.GetDirectories(temporary.Paths.GameRoot).Length);
    }

    [TestMethod]
    public async Task RenameFailureRollsBackAndCaseOnlyRenameUsesTemporaryFolder()
    {
        await using var temporary = new TemporaryDirectory();
        var initial = await LoadStoreAsync(temporary);
        var instance = initial.Create(new InstanceDraft { Name = "First Build" }, "1.20.4");
        await SetStateAsync(temporary.Paths, initial, InstanceInstallationState.Ready);
        var failing = await LoadStoreAsync(temporary, () => throw new IOException("Injected registry failure."));

        Assert.ThrowsExactly<IOException>(() => failing.Rename(instance.Id, "Second Build"));
        Assert.IsTrue(Directory.Exists(Path.Combine(temporary.Paths.GameRoot, "First_Build")));
        Assert.IsFalse(Directory.Exists(Path.Combine(temporary.Paths.GameRoot, "Second_Build")));

        initial.Rename(instance.Id, "FIRST BUILD");
        var renamedDirectory = Directory.GetDirectories(temporary.Paths.GameRoot).Single(path => Path.GetFileName(path).StartsWith("FIRST", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("FIRST_BUILD", Path.GetFileName(renamedDirectory));
        Assert.AreEqual("FIRST BUILD", initial.List().Single().Name);
    }

    [TestMethod]
    public async Task RenameRollbackFailureKeepsTheOriginalOperationFailureAsPrimary()
    {
        await using var temporary = new TemporaryDirectory();
        var initial = await LoadStoreAsync(temporary);
        var instance = initial.Create(new InstanceDraft { Name = "Original" }, "1.20.4");
        await SetStateAsync(temporary.Paths, initial, InstanceInstallationState.Ready);
        var primaryFailure = new IOException("Primary registry failure.");
        var failing = await LoadStoreAsync(temporary, () =>
        {
            File.WriteAllText(Path.Combine(temporary.Paths.GameRoot, instance.FolderName), "blocks rollback");
            throw primaryFailure;
        });

        var exception = Assert.ThrowsExactly<AggregateException>(() => failing.Rename(instance.Id, "Changed"));

        Assert.AreSame(primaryFailure, exception.InnerExceptions[0]);
    }

    [TestMethod]
    public async Task DeleteFailureRestoresFolderAndMissingFolderDoesNotBlockRemoval()
    {
        await using var temporary = new TemporaryDirectory();
        var initial = await LoadStoreAsync(temporary);
        var instance = initial.Create(new InstanceDraft { Name = "Delete Me" }, "1.20.4");
        await SetStateAsync(temporary.Paths, initial, InstanceInstallationState.Ready);
        var failing = await LoadStoreAsync(temporary, () => throw new IOException("Injected registry failure."));

        Assert.ThrowsExactly<IOException>(() => failing.Delete(instance.Id));
        Assert.IsTrue(Directory.Exists(Path.Combine(temporary.Paths.GameRoot, "Delete_Me")));
        Assert.AreEqual(instance.Id, initial.List().Single().Id);

        Directory.Delete(Path.Combine(temporary.Paths.GameRoot, "Delete_Me"), recursive: true);
        initial.Delete(instance.Id);
        Assert.AreEqual(0, initial.List().Count);
        await initial.DeleteStagingCleanupTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(temporary.Paths.GameRoot)
            .Any(path => Path.GetFileName(path).StartsWith(".delete-", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task DeleteAfterDisposeLeavesStagingForTheNextLoadToClean()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        await store.DeleteStagingCleanupTask!.WaitAsync(TimeSpan.FromSeconds(10));
        var instance = store.Create(new InstanceDraft { Name = "Delete After Dispose" }, "1.20.4");
        await SetStateAsync(temporary.Paths, store, InstanceInstallationState.Ready);
        await store.DisposeAsync();

        store.Delete(instance.Id);

        Assert.AreEqual(0, store.List().Count);
        var staging = Directory.EnumerateDirectories(temporary.Paths.GameRoot)
            .Single(path => Path.GetFileName(path).StartsWith(".delete-", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(Directory.Exists(staging));

        var nextStore = await LoadStoreAsync(temporary);
        await nextStore.DeleteStagingCleanupTask!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsFalse(Directory.Exists(staging));
    }

    [TestMethod]
    public async Task DeleteCleanupRemovesNestedJunctionWithoutTouchingItsTarget()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var instance = store.Create(new InstanceDraft { Name = "Junction Build" }, "1.20.4");
        var buildDirectory = Path.Combine(temporary.Paths.GameRoot, instance.FolderName);
        var outsideTarget = Path.Combine(temporary.Path, "outside-target");
        Directory.CreateDirectory(outsideTarget);
        var targetFile = Path.Combine(outsideTarget, "keep.txt");
        await File.WriteAllTextAsync(targetFile, "keep");
        CreateJunction(Path.Combine(buildDirectory, "outside"), outsideTarget);
        await SetStateAsync(temporary.Paths, store, InstanceInstallationState.Ready);

        var removedName = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(temporary.Paths.GameRoot, ".delete-*")
        {
            NotifyFilter = NotifyFilters.DirectoryName,
            EnableRaisingEvents = true
        };
        watcher.Deleted += (_, args) =>
        {
            removedName.TrySetResult(args.Name!);
        };

        store.Delete(instance.Id);
        await store.DeleteStagingCleanupTask!.WaitAsync(TimeSpan.FromSeconds(10));
        var stagingName = await removedName.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(stagingName.StartsWith($".delete-{instance.Id:N}-", StringComparison.Ordinal));
        Assert.AreEqual(73, stagingName.Length);
        Assert.IsFalse(Directory.Exists(Path.Combine(temporary.Paths.GameRoot, stagingName)));
        Assert.AreEqual("keep", await File.ReadAllTextAsync(targetFile));
    }

    [TestMethod]
    public async Task LoadPublishesSnapshotBeforeBackgroundStagingCleanupCompletes()
    {
        await using var temporary = new TemporaryDirectory();
        var initial = await LoadStoreAsync(temporary);
        await initial.DeleteStagingCleanupTask!.WaitAsync(TimeSpan.FromSeconds(10));
        var instance = initial.Create(new InstanceDraft { Name = "Background Cleanup" }, "1.20.4");
        var staging = Path.Combine(temporary.Paths.GameRoot, ".delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? disposeTask = null;
        var store = temporary.CreateStore(null, () =>
        {
            cleanupStarted.TrySetResult();
            continueCleanup.Task.GetAwaiter().GetResult();
        });

        try
        {
            await store.LoadAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(store.IsLoaded);
            Assert.AreEqual(instance.Id, store.List().Single().Id);
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(Directory.Exists(staging));
            disposeTask = store.DisposeAsync().AsTask();
            Assert.IsFalse(disposeTask.IsCompleted);
        }
        finally
        {
            continueCleanup.TrySetResult();
        }

        await disposeTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(Directory.Exists(staging));
    }

    [TestMethod]
    public async Task LoadCleansOnlyExactLegacyAndNewDeleteStagingNames()
    {
        await using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Paths.GameRoot);
        var foreign = Path.Combine(temporary.Paths.GameRoot, ".delete-whatever");
        var legacyN = Path.Combine(temporary.Paths.GameRoot, ".delete-" + Guid.NewGuid().ToString("N"));
        var legacyD = Path.Combine(temporary.Paths.GameRoot, ".delete-" + Guid.NewGuid().ToString("D"));
        var newFormat = Path.Combine(temporary.Paths.GameRoot,
            $".delete-{Guid.NewGuid():N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(foreign);
        Directory.CreateDirectory(legacyN);
        Directory.CreateDirectory(legacyD);
        Directory.CreateDirectory(newFormat);

        var store = temporary.CreateStore();
        await store.LoadAsync();
        await store.DeleteStagingCleanupTask!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(Directory.Exists(foreign));
        Assert.IsFalse(Directory.Exists(legacyN));
        Assert.IsFalse(Directory.Exists(legacyD));
        Assert.IsFalse(Directory.Exists(newFormat));
    }

    [TestMethod]
    public async Task LoadReconcilesInterruptedDeleteAndKeepsBuildWhenItsFolderWasRestored()
    {
        await using var temporary = new TemporaryDirectory();
        var initial = await LoadStoreAsync(temporary);
        await initial.DeleteStagingCleanupTask!.WaitAsync(TimeSpan.FromSeconds(10));
        var moved = initial.Create(new InstanceDraft { Name = "Interrupted Delete" }, "1.20.4");
        var restored = initial.Create(new InstanceDraft { Name = "Restored Build" }, "1.20.4");
        var movedStaging = Path.Combine(temporary.Paths.GameRoot, $".delete-{moved.Id:N}-{Guid.NewGuid():N}");
        Directory.Move(Path.Combine(temporary.Paths.GameRoot, moved.FolderName), movedStaging);
        var restoredStaging = Path.Combine(temporary.Paths.GameRoot, $".delete-{restored.Id:N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(restoredStaging);
        await File.WriteAllTextAsync(Path.Combine(restoredStaging, "leftover.txt"), "leftover");

        var store = temporary.CreateStore();
        await store.LoadAsync();
        await store.DeleteStagingCleanupTask!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(restored.Id, store.List().Single().Id);
        Assert.IsFalse(Directory.Exists(movedStaging));
        Assert.IsFalse(Directory.Exists(restoredStaging));
        Assert.IsFalse(Directory.Exists(Path.Combine(temporary.Paths.GameRoot, moved.FolderName)));
        Assert.IsTrue(Directory.Exists(Path.Combine(temporary.Paths.GameRoot, restored.FolderName)));
    }

    [TestMethod]
    public async Task RenameAndUpdateAreBlockedWhileQueuedOrInstalling()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var created = store.Create(new InstanceDraft { Name = "Queued Build" }, "1.20.4");
        var update = new InstanceDraft { Name = "Renamed Build" };

        var queuedRename = Assert.ThrowsExactly<InstanceStorageException>(() => store.Rename(created.Id, "Renamed Build"));
        var queuedUpdate = Assert.ThrowsExactly<InstanceStorageException>(() => store.Update(created.Id, update));
        var queuedSettingsUpdate = Assert.ThrowsExactly<InstanceStorageException>(() => store.UpdateSettings(created.Id, update));
        var queuedDelete = Assert.ThrowsExactly<InstanceStorageException>(() => store.Delete(created.Id));
        Assert.AreEqual(InstanceStorageError.BuildInstalling, queuedRename.Error);
        Assert.AreEqual(InstanceStorageError.BuildInstalling, queuedUpdate.Error);
        Assert.AreEqual(InstanceStorageError.BuildInstalling, queuedSettingsUpdate.Error);
        Assert.AreEqual(InstanceStorageError.BuildInstalling, queuedDelete.Error);

        await SetStateAsync(temporary.Paths, store, InstanceInstallationState.Installing);
        var installingRename = Assert.ThrowsExactly<InstanceStorageException>(() => store.Rename(created.Id, "Renamed Build"));
        var installingUpdate = Assert.ThrowsExactly<InstanceStorageException>(() => store.Update(created.Id, update));
        var installingSettingsUpdate = Assert.ThrowsExactly<InstanceStorageException>(() => store.UpdateSettings(created.Id, update));
        var installingDelete = Assert.ThrowsExactly<InstanceStorageException>(() => store.Delete(created.Id));
        Assert.AreEqual(InstanceStorageError.BuildInstalling, installingRename.Error);
        Assert.AreEqual(InstanceStorageError.BuildInstalling, installingUpdate.Error);
        Assert.AreEqual(InstanceStorageError.BuildInstalling, installingSettingsUpdate.Error);
        Assert.AreEqual(InstanceStorageError.BuildInstalling, installingDelete.Error);
    }

    [TestMethod]
    public async Task SettingsCanChangeWhileTheGameRunsButRenameCannot()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var created = store.Create(new InstanceDraft { Name = "Running Build" }, "1.20.4");
        await SetStateAsync(temporary.Paths, store, InstanceInstallationState.Ready);

        Assert.IsTrue(store.TryBeginLaunch(created.Id, out var lease, out _));
        using (lease)
        {
            store.UpdateSettings(created.Id, new InstanceDraft { Name = "Ignored", OfflineMode = true, OfflineUsername = "Steve" });
            var rename = Assert.ThrowsExactly<InstanceStorageException>(() => store.Rename(created.Id, "Other Name"));

            Assert.AreEqual(InstanceStorageError.BuildRunning, rename.Error);
            Assert.AreEqual(InstanceStorageError.BuildRunning, store.ManagementBlockedError(store.List().Single()));
        }

        var updated = store.List().Single();
        Assert.IsTrue(updated.OfflineMode);
        Assert.AreEqual("Running Build", updated.Name);
        Assert.IsNull(store.ManagementBlockedError(updated));
    }

    [TestMethod]
    public async Task TryBeginLaunchAndDeleteAreSerializedByTheStoreGate()
    {
        await using var temporary = new TemporaryDirectory();
        var initial = await LoadStoreAsync(temporary);
        var created = initial.Create(new InstanceDraft { Name = "Atomic Busy" }, "1.20.4");
        await SetStateAsync(temporary.Paths, initial, InstanceInstallationState.Ready);

        Assert.IsTrue(initial.TryBeginLaunch(created.Id, out var launchLease, out var launchReason));
        Assert.IsNull(launchReason);
        using (launchLease)
        {
            var blockedDelete = Assert.ThrowsExactly<InstanceStorageException>(() => initial.Delete(created.Id));
            Assert.AreEqual(InstanceStorageError.BuildRunning, blockedDelete.Error);
        }

        var deleteEnteredCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleting = await LoadStoreAsync(temporary, () =>
        {
            deleteEnteredCommit.TrySetResult();
            continueDelete.Task.GetAwaiter().GetResult();
        });
        var deleteTask = Task.Run(() => deleting.Delete(created.Id));
        await deleteEnteredCommit.Task;
        var beginEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beginTask = Task.Run(() =>
        {
            beginEntered.TrySetResult();
            try
            {
                return deleting.TryBeginLaunch(created.Id, out _, out _);
            }
            catch (KeyNotFoundException)
            {
                return false;
            }
        });
        await beginEntered.Task;
        continueDelete.TrySetResult();

        await deleteTask;
        await deleting.DeleteStagingCleanupTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(await beginTask);
        Assert.IsFalse(deleting.IsLaunchBusy(created.Id));
    }

    [TestMethod]
    public async Task BusyFlagsRemainSafeUnderConcurrentReadsAndWrites()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var instance = store.Create(new InstanceDraft { Name = "Busy Build" }, "1.20.4");
        await SetStateAsync(temporary.Paths, store, InstanceInstallationState.Ready);
        var id = instance.Id;
        var notificationCount = 0;
        store.BusyStateChanged += (_, _) => Interlocked.Increment(ref notificationCount);
        using var start = new ManualResetEventSlim();
        var workers = Enumerable.Range(0, 12).Select(workerIndex => Task.Run(() =>
        {
            start.Wait();
            for (var iteration = 0; iteration < 500; iteration++)
            {
                if (store.TryBeginLaunch(id, out var launch, out _))
                {
                    store.IsLaunchBusy(id);
                    launch!.Dispose();
                }

                if (store.TryBeginContent(id, out var content, out _))
                {
                    store.IsContentBusy(id);
                    content!.Dispose();
                }
            }
        })).ToArray();
        start.Set();
        await Task.WhenAll(workers);

        Assert.IsTrue(store.TryBeginLaunch(id, out var finalLease, out _));
        Assert.IsTrue(store.IsLaunchBusy(id));
        Assert.IsTrue(notificationCount > 0);
        finalLease!.Dispose();
    }

    [TestMethod]
    public async Task BusyChecksDoNotWaitForRunningStoreOperation()
    {
        await using var temporary = new TemporaryDirectory();
        var initial = await LoadStoreAsync(temporary);
        initial.Create(new InstanceDraft { Name = "Busy Build" }, "1.20.4");
        await SetStateAsync(temporary.Paths, initial, InstanceInstallationState.Ready);
        using var committing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var store = await LoadStoreAsync(temporary, () =>
        {
            committing.Set();
            release.Wait();
        });
        var instance = store.List().Single();
        Assert.IsTrue(store.TryBeginContent(instance.Id, out var lease, out _));
        lease!.Dispose();
        Assert.IsTrue(store.TryBeginLaunch(instance.Id, out var launch, out _));

        var update = Task.Run(() => store.UpdateSettings(instance.Id, new InstanceDraft { Name = instance.Name, OfflineMode = true, OfflineUsername = "Steve" }));
        try
        {
            Assert.IsTrue(committing.Wait(TimeSpan.FromSeconds(10)));
            Assert.IsTrue(store.IsLaunchBusy(instance.Id));
            Assert.IsFalse(store.IsContentBusy(instance.Id));
            Assert.AreEqual(InstanceStorageError.BuildRunning, store.ManagementBlockedError(instance));
        }
        finally
        {
            release.Set();
            await update;
            launch!.Dispose();
        }

        Assert.IsFalse(store.IsLaunchBusy(instance.Id));
    }

    [TestMethod]
    public void ContainedPathAllowsMissingInstallationPathsWithoutThrowing()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Paths.GameRoot;
        Directory.CreateDirectory(root);
        var threadId = Environment.CurrentManagedThreadId;
        var missingPathExceptions = 0;
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> handler = (_, args) =>
        {
            if (Environment.CurrentManagedThreadId == threadId
                && args.Exception is FileNotFoundException or DirectoryNotFoundException)
            {
                missingPathExceptions++;
            }
        };

        AppDomain.CurrentDomain.FirstChanceException += handler;
        try
        {
            foreach (var path in new[] { "minecraft/assets/objects/ab/test", "java/legal/test/LICENSE" })
            {
                Assert.AreEqual(Path.GetFullPath(Path.Combine(root, path)), InstanceStorage.ContainedPath(path, root));
            }

            Directory.CreateDirectory(Path.Combine(root, "minecraft", "assets"));
            _ = InstanceStorage.ContainedPath("minecraft/assets/missing.json", root);
            _ = InstanceStorage.ContainedPath("file.txt", Path.Combine(root, "missing-root"));
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= handler;
        }

        Assert.AreEqual(0, missingPathExceptions, "Missing installation paths should not generate debugger exception noise.");
    }

    [TestMethod]
    public void ContainedPathRejectsTraversalAbsolutePathsAndReparsePoints()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Paths.GameRoot;
        Directory.CreateDirectory(root);
        foreach (var path in new[] { "..\\outside", "a\\..\\b", Path.Combine(root, "absolute"), "C:\\absolute" })
        {
            Assert.ThrowsExactly<InstanceStorageException>(() => InstanceStorage.ContainedPath(path, root));
        }

        var target = Path.Combine(root, "target");
        var junction = Path.Combine(root, "junction");
        Directory.CreateDirectory(target);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/c", "mklink", "/J", junction, target }
        });
        process!.WaitForExit();
        if (process.ExitCode != 0)
        {
            Assert.Inconclusive("Could not create a junction in the temporary test folder: " + process.StandardError.ReadToEnd());
        }

        try
        {
            Assert.ThrowsExactly<InstanceStorageException>(() => InstanceStorage.ContainedPath("junction\\file.txt", root));
            Directory.Delete(target);
            Assert.ThrowsExactly<InstanceStorageException>(() => InstanceStorage.ContainedPath("junction\\file.txt", root));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [TestMethod]
    public void RootLockCanOnlyBeHeldByOneProcessAtATime()
    {
        using var temporary = new TemporaryDirectory();
        var first = HakoRootLock.Acquire(temporary.Paths);
        try
        {
            Assert.ThrowsExactly<HakoRootInUseException>(() => HakoRootLock.Acquire(temporary.Paths));
        }
        finally
        {
            first.Dispose();
        }

        using var second = HakoRootLock.Acquire(temporary.Paths);
    }

    [TestMethod]
    public void RootLockAlsoPreventsAnotherGameRootFromSharingTheDataRoot()
    {
        using var temporary = new TemporaryDirectory();
        var firstPaths = temporary.Paths;
        var secondPaths = new HakoPaths(
            Path.Combine(temporary.Path, "other-game"),
            firstPaths.DataRoot,
            Path.Combine(temporary.Path, "other-cache"));
        using var first = HakoRootLock.Acquire(firstPaths);

        var exception = Assert.ThrowsExactly<HakoRootInUseException>(() => HakoRootLock.Acquire(secondPaths));

        Assert.AreEqual(Path.Combine(firstPaths.DataRoot, ".hako-lock"), exception.LockPath);
    }

    [TestMethod]
    public void RootLockWrapsOnlySharingAndLockViolationsAsAlreadyOpen()
    {
        Assert.IsTrue(HakoRootLock.IsLockViolation(new SyntheticIOException(32)));
        Assert.IsTrue(HakoRootLock.IsLockViolation(new SyntheticIOException(33)));
        Assert.IsFalse(HakoRootLock.IsLockViolation(new SyntheticIOException(5)));

        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Paths.GameRoot);
        Directory.CreateDirectory(Path.Combine(temporary.Paths.GameRoot, ".hako-lock"));

        try
        {
            using var ignored = HakoRootLock.Acquire(temporary.Paths);
            Assert.Fail("A directory at the lock path should fail as a storage error.");
        }
        catch (HakoRootInUseException)
        {
            Assert.Fail("A non-sharing storage error must not be reported as another Hako instance.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [TestMethod]
    public async Task RootJunctionIsAcceptedButNestedJunctionIsRejected()
    {
        await using var temporary = new TemporaryDirectory();
        var target = Path.Combine(temporary.Path, "game-target");
        var rootJunction = Path.Combine(temporary.Path, "game-link");
        var nestedTarget = Path.Combine(temporary.Path, "nested-target");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(nestedTarget);
        CreateJunction(rootJunction, target);
        var paths = new HakoPaths(rootJunction, Path.Combine(temporary.Path, "data"), Path.Combine(temporary.Path, "cache"));
        var storage = new InstanceStorage(paths);
        CreateJunction(Path.Combine(target, "nested-link"), nestedTarget);

        try
        {
            using var rootLock = HakoRootLock.Acquire(paths);
            Assert.AreEqual(Path.Combine(rootJunction, "Build"), storage.Directory("Build"));
            Assert.ThrowsExactly<InstanceStorageException>(() => storage.Directory("nested-link"));
            var store = temporary.Track(new InstanceStore(paths));
            await store.LoadAsync();
            Assert.AreEqual(0, store.List().Count);
        }
        finally
        {
            Directory.Delete(Path.Combine(target, "nested-link"));
            Directory.Delete(rootJunction);
        }
    }

    [TestMethod]
    public async Task KilledLockHolderReleasesGameRootForTheNextProcess()
    {
        await using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Paths.GameRoot);
        var powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powerShell))
        {
            Assert.Inconclusive("Windows PowerShell is not available to run the lock-holder child process.");
        }

        var lockPath = Path.Combine(temporary.Paths.GameRoot, ".hako-lock").Replace("'", "''", StringComparison.Ordinal);
        var script = "$stream = [System.IO.File]::Open('" + lockPath
            + "', [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None);"
            + " [Console]::Out.WriteLine('LOCKED'); [Console]::Out.Flush();"
            + " ([System.Threading.ManualResetEvent]::new($false)).WaitOne() | Out-Null";
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var process = Process.Start(new ProcessStartInfo(powerShell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-EncodedCommand", encodedScript }
        })!;

        try
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual("LOCKED", line);
            Assert.ThrowsExactly<HakoRootInUseException>(() => HakoRootLock.Acquire(temporary.Paths));
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            using (await AcquireRootLockWhenReleasedAsync(temporary.Paths))
            {
            }
            await DeleteLockFileWhenReleasedAsync(Path.Combine(temporary.Paths.GameRoot, ".hako-lock"));
            await DeleteLockFileWhenReleasedAsync(Path.Combine(temporary.Paths.DataRoot, ".hako-lock"));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [TestMethod]
    public void OfflineUsernameUsesThreeToSixteenAsciiLettersDigitsAndUnderscores()
    {
        Assert.IsTrue(OfflineUsername.IsValid("Player_2"));
        Assert.IsTrue(OfflineUsername.IsValid("Ab1"));
        Assert.IsFalse(OfflineUsername.IsValid("ab"));
        Assert.IsFalse(OfflineUsername.IsValid(new string('a', 17)));
        Assert.IsFalse(OfflineUsername.IsValid("Игрок"));
        Assert.IsFalse(OfflineUsername.IsValid("player-name"));
    }

    [TestMethod]
    public async Task UpdateIsTransactionalAndManagementBlockReasonsTrackBusyFlags()
    {
        await using var temporary = new TemporaryDirectory();
        var initial = await LoadStoreAsync(temporary);
        var created = initial.Create(new InstanceDraft { Name = "Original" }, "1.20.4");
        Assert.IsNotNull(initial.ManagementBlockedReason(created));
        await SetStateAsync(temporary.Paths, initial, InstanceInstallationState.Ready);
        var draft = new InstanceDraft
        {
            Name = "Updated",
            OfflineMode = true,
            OfflineUsername = "Player_2",
            ArgumentSource = LaunchArgumentSource.Custom,
            Parameters = new InstanceParameters { JavaArguments = "-Dchanged=yes", MaximumMemoryMiB = 1024 }
        };
        var failing = await LoadStoreAsync(temporary, () => throw new IOException("Injected registry failure."));

        Assert.ThrowsExactly<IOException>(() => failing.Update(created.Id, draft));
        Assert.IsTrue(Directory.Exists(Path.Combine(temporary.Paths.GameRoot, "Original")));
        Assert.AreEqual("Original", initial.List().Single().Name);

        initial.Update(created.Id, draft);
        var updated = initial.List().Single();
        Assert.AreEqual("Updated", updated.Name);
        Assert.AreEqual("Updated", updated.FolderName);
        Assert.IsTrue(updated.OfflineMode);
        Assert.AreEqual(LaunchArgumentSource.Custom, updated.ArgumentSource);
        Assert.AreEqual("-Dchanged=yes", updated.Parameters.JavaArguments);
        Assert.IsFalse(Directory.Exists(Path.Combine(temporary.Paths.GameRoot, "Original")));
        Assert.IsTrue(Directory.Exists(Path.Combine(temporary.Paths.GameRoot, "Updated")));

        Assert.IsNull(initial.ManagementBlockedReason(updated));
        Assert.IsTrue(initial.TryBeginLaunch(updated.Id, out var launchLease, out _));
        using (launchLease)
        {
            Assert.IsNotNull(initial.ManagementBlockedReason(updated));
        }
        Assert.IsNull(initial.ManagementBlockedReason(updated));
        Assert.IsTrue(initial.TryBeginContent(updated.Id, out var contentLease, out _));
        using (contentLease)
        {
            Assert.IsNotNull(initial.ManagementBlockedReason(updated));
        }
        Assert.IsNull(initial.ManagementBlockedReason(updated));
    }

    [TestMethod]
    public async Task UpdateSettingsChangesOnlyProfileSettingsAndRollsBackIconOnFailure()
    {
        await using var temporary = new TemporaryDirectory();
        var initial = await LoadStoreAsync(temporary);
        var created = initial.Create(new InstanceDraft { Name = "Settings Build" }, "1.20.4");
        var originalIcon = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(Path.Combine(temporary.Paths.GameRoot, "Settings_Build", "icon.png"), originalIcon);
        await SetStateAsync(temporary.Paths, initial, InstanceInstallationState.Ready);
        var update = new InstanceDraft
        {
            Name = "Ignored Name",
            IconSymbol = "",
            IconData = [7, 8, 9],
            ArgumentSource = LaunchArgumentSource.Global,
            Parameters = new InstanceParameters { MinecraftArguments = "--new", MaximumMemoryMiB = 1024 }
        };
        var failing = await LoadStoreAsync(temporary, () => throw new IOException("Injected registry failure."));

        Assert.ThrowsExactly<IOException>(() => failing.UpdateSettings(created.Id, update));
        CollectionAssert.AreEqual(originalIcon, File.ReadAllBytes(Path.Combine(temporary.Paths.GameRoot, "Settings_Build", "icon.png")));

        initial.UpdateSettings(created.Id, update);
        var updated = initial.List().Single();
        Assert.AreEqual("Settings Build", updated.Name);
        Assert.AreEqual(LaunchArgumentSource.Global, updated.ArgumentSource);
        Assert.AreEqual("--new", updated.Parameters.MinecraftArguments);
        CollectionAssert.AreEqual(update.IconData, File.ReadAllBytes(Path.Combine(temporary.Paths.GameRoot, "Settings_Build", "icon.png")));
    }

    [TestMethod]
    public async Task RenamePreservesCustomIconFile()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var created = store.Create(new InstanceDraft { Name = "Icon Build" }, "1.20.4");
        var icon = new byte[] { 8, 6, 7, 5, 3, 0, 9 };
        var oldIconPath = Path.Combine(temporary.Paths.GameRoot, created.FolderName, "icon.png");
        await File.WriteAllBytesAsync(oldIconPath, icon);
        await SetStateAsync(temporary.Paths, store, InstanceInstallationState.Ready);

        store.Rename(created.Id, "Renamed Icon Build");

        CollectionAssert.AreEqual(icon, await File.ReadAllBytesAsync(
            Path.Combine(temporary.Paths.GameRoot, "Renamed_Icon_Build", "icon.png")));
        Assert.AreEqual(created.IconRevision, store.List().Single().IconRevision);
    }

    [TestMethod]
    public void IsolatedArgumentsRequireTwoAbsoluteRootsAndUseTheirOwnInstanceKey()
    {
        using var temporary = new TemporaryDirectory();
        Assert.ThrowsExactly<ArgumentException>(() => HakoPaths.ParseOverrides(["--hako-root", Path.Combine(temporary.Path, "game")]));
        Assert.ThrowsExactly<ArgumentException>(() => HakoPaths.ParseOverrides(["--hako-root"]));

        var game = Path.Combine(temporary.Path, "game");
        var data = Path.Combine(temporary.Path, "data");
        Assert.ThrowsExactly<ArgumentException>(() => HakoPaths.ParseOverrides(["--hako-root", game, "--data-root", Path.Combine(game, "data")]));
        Assert.ThrowsExactly<ArgumentException>(() => HakoPaths.ParseOverrides(["--hako-root", game, "--data-root", temporary.Path]));
        var options = HakoPaths.ParseOverrides(["--hako-root", game, "--data-root", data]);
        var paths = HakoPaths.CreateIsolated(options.GameRoot!, options.DataRoot!);
        Assert.IsTrue(paths.Isolated);
        StringAssert.StartsWith(paths.GetInstanceKey(), "Hako.Isolated.");
        Assert.AreNotEqual("Hako.Main", paths.GetInstanceKey());
        Assert.AreEqual(Path.Combine(data, "Cache"), paths.CacheRoot);
    }

    private sealed class TemporaryDirectory : IDisposable, IAsyncDisposable
    {
        private readonly List<IAsyncDisposable> _resources = [];

        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Hako.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Paths = new HakoPaths(System.IO.Path.Combine(Path, "game"), System.IO.Path.Combine(Path, "data"), System.IO.Path.Combine(Path, "cache"));
        }

        public string Path { get; }
        public HakoPaths Paths { get; }

        public InstanceStore CreateStore(
            Action? beforeRegistryCommit = null,
            Action? beforeDeleteStagingCleanup = null) =>
            Track(new InstanceStore(Paths, beforeRegistryCommit, beforeDeleteStagingCleanup));

        public T Track<T>(T resource) where T : IAsyncDisposable
        {
            _resources.Add(resource);
            return resource;
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        public async ValueTask DisposeAsync()
        {
            List<Exception>? errors = null;
            for (var index = _resources.Count - 1; index >= 0; index--)
            {
                try
                {
                    await _resources[index].DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    errors ??= [];
                    errors.Add(exception);
                }
            }

            if (errors is not null)
            {
                throw new AggregateException("Failed to dispose test resources.", errors);
            }

            if (Directory.Exists(Path))
            {
                var deadline = Stopwatch.StartNew();
                while (true)
                {
                    try
                    {
                        Directory.Delete(Path, recursive: true);
                        break;
                    }
                    catch (IOException exception) when (HakoRootLock.IsLockViolation(exception)
                        && deadline.Elapsed < TimeSpan.FromSeconds(10))
                    {
                        await Task.Delay(25).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    private static async Task DeleteLockFileWhenReleasedAsync(string path)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException exception) when (HakoRootLock.IsLockViolation(exception)
                && deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(25);
            }
        }
    }

    private static async Task<HakoRootLock> AcquireRootLockWhenReleasedAsync(HakoPaths paths)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return HakoRootLock.Acquire(paths);
            }
            catch (HakoRootInUseException exception) when (exception.InnerException is IOException inner
                && HakoRootLock.IsLockViolation(inner)
                && deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(25).ConfigureAwait(false);
            }
        }
    }

    private sealed class SyntheticIOException : IOException
    {
        public SyntheticIOException(int error)
        {
            HResult = unchecked((int)0x80070000) | error;
        }
    }

    private static async Task<InstanceStore> LoadStoreAsync(
        TemporaryDirectory temporary,
        Action? beforeRegistryCommit = null,
        Action? beforeDeleteStagingCleanup = null)
    {
        var store = temporary.CreateStore(beforeRegistryCommit, beforeDeleteStagingCleanup);
        await store.LoadAsync();
        return store;
    }

    private static async Task SetStateAsync(HakoPaths paths, InstanceStore store, InstanceInstallationState state)
    {
        var registryPath = Path.Combine(paths.GameRoot, "instances.json");
        var registry = JsonSerializer.Deserialize(File.ReadAllBytes(registryPath), HakoJsonContext.Default.InstanceRegistryFile)!;
        var updated = registry with
        {
            Instances = registry.Instances.Select(instance => instance with { State = state }).ToList()
        };
        File.WriteAllBytes(registryPath, JsonSerializer.SerializeToUtf8Bytes(updated, HakoJsonContext.Default.InstanceRegistryFile));
        await store.LoadAsync();
    }

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
        if (process.ExitCode != 0)
        {
            Assert.Inconclusive("Could not create a junction in the temporary test folder: " + process.StandardError.ReadToEnd());
        }
    }
}

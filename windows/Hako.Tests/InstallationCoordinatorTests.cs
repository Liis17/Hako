using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;

namespace Hako.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class InstallationCoordinatorTests
{
    [TestMethod]
    public async Task QueueUsesCreatedAtFifoAndRunsOnlyOneInstallerAtATime()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var newest = store.Create(new InstanceDraft { Name = "Newest" }, "newest");
        var oldest = store.Create(new InstanceDraft { Name = "Oldest" }, "oldest");
        var middle = store.Create(new InstanceDraft { Name = "Middle" }, "middle");
        await UpdateInstancesAsync(store, instances => instances.Select(instance => instance.Id switch
        {
            var id when id == oldest.Id => instance with { CreatedAt = DateTimeOffset.Parse("2024-01-01T00:00:00Z") },
            var id when id == middle.Id => instance with { CreatedAt = DateTimeOffset.Parse("2024-02-01T00:00:00Z") },
            _ => instance with { CreatedAt = DateTimeOffset.Parse("2024-03-01T00:00:00Z") }
        }).ToList());

        var releases = new Dictionary<string, TaskCompletionSource>(StringComparer.Ordinal)
        {
            ["oldest"] = NewSignal(),
            ["middle"] = NewSignal(),
            ["newest"] = NewSignal()
        };
        var started = new ConcurrentQueue<string>();
        var startedSignals = releases.Keys.ToDictionary(key => key, _ => NewSignal(), StringComparer.Ordinal);
        var active = 0;
        var maxActive = 0;
        var coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) => Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64)),
            async (prepared, _, _, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maxActive, current);
                started.Enqueue(prepared.Version.Id);
                startedSignals[prepared.Version.Id].TrySetResult();
                try
                {
                    await releases[prepared.Version.Id].Task.WaitAsync(cancellationToken);
                    return Result(prepared.Architecture);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

        await coordinator.StartAsync();
        await startedSignals["oldest"].Task.WaitAsync(TimeSpan.FromSeconds(10));
        releases["oldest"].TrySetResult();
        await startedSignals["middle"].Task.WaitAsync(TimeSpan.FromSeconds(10));
        releases["middle"].TrySetResult();
        await startedSignals["newest"].Task.WaitAsync(TimeSpan.FromSeconds(10));
        releases["newest"].TrySetResult();

        await WaitForStateAsync(store, oldest.Id, InstanceInstallationState.Ready);
        await WaitForStateAsync(store, middle.Id, InstanceInstallationState.Ready);
        await WaitForStateAsync(store, newest.Id, InstanceInstallationState.Ready);

        CollectionAssert.AreEqual(new[] { "oldest", "middle", "newest" }, started.ToArray());
        Assert.AreEqual(1, maxActive);
    }

    [TestMethod]
    public async Task PauseKeepsBuildInstallingUntilCanceledInstallerWorkHasFinished()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var prepared = CreatePrepared("pause-build", GameArchitecture.X64);
        var instance = store.Create(new InstanceDraft { Name = "Pause Build" }, prepared);
        var enteredInstall = NewSignal();
        var cancellationObserved = NewSignal();
        var finishWriter = NewSignal();
        var canceledAfterPauseWasPersisted = false;
        var progressReported = NewSignal();
        var coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) => Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64)),
            async (value, _, progress, cancellationToken) =>
            {
                enteredInstall.TrySetResult();
                progress!.Report(new InstallationProgress(InstallationProgressStage.Downloading, 10, 20, 1, 2));
                progressReported.TrySetResult();
                using var registration = cancellationToken.Register(() =>
                {
                    canceledAfterPauseWasPersisted = store.Instances.Single(item => item.Id == instance.Id).PauseRequested;
                    cancellationObserved.TrySetResult();
                });
                await finishWriter.Task;
                cancellationToken.ThrowIfCancellationRequested();
                return Result(value.Architecture);
            });

        await coordinator.StartAsync();
        await enteredInstall.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await progressReported.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(InstallationProgressStage.Downloading, coordinator.Progress[instance.Id].Stage);
        Assert.AreEqual(10, coordinator.Progress[instance.Id].CompletedBytes);
        Assert.AreEqual(1, coordinator.Progress[instance.Id].CompletedFiles);
        await coordinator.PauseAsync(instance.Id);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(canceledAfterPauseWasPersisted);
        Assert.AreEqual(InstanceInstallationState.Installing, store.Instances.Single(item => item.Id == instance.Id).State);
        Assert.IsTrue(store.Instances.Single(item => item.Id == instance.Id).PauseRequested);
        Assert.IsNotNull(store.ManagementBlockedReason(store.Instances.Single(item => item.Id == instance.Id)));
        Assert.IsFalse(store.TryBeginContent(instance.Id, out _, out var busyReason));
        Assert.AreEqual(InstanceStorageError.BuildInstalling, busyReason);
        Assert.AreEqual(InstallationProgressStage.Pausing, coordinator.Progress[instance.Id].Stage);
        Assert.ThrowsExactly<InstanceStorageException>(() => store.Rename(instance.Id, "Changed"));
        Assert.ThrowsExactly<InstanceStorageException>(() => store.Update(instance.Id, new InstanceDraft { Name = "Changed" }));
        Assert.ThrowsExactly<InstanceStorageException>(() => store.UpdateSettings(instance.Id, new InstanceDraft { Name = "Pause Build" }));
        Assert.ThrowsExactly<InstanceStorageException>(() => store.Delete(instance.Id));

        finishWriter.TrySetResult();
        await WaitForStateAsync(store, instance.Id, InstanceInstallationState.Paused);
        Assert.IsTrue(store.Instances.Single(item => item.Id == instance.Id).PauseRequested);
        Assert.IsNull(store.ManagementBlockedReason(store.Instances.Single(item => item.Id == instance.Id)));
        store.Delete(instance.Id);
        Assert.IsFalse(store.Instances.Any(item => item.Id == instance.Id));
    }

    [TestMethod]
    public async Task PauseAfterInstallerReturnsLeavesBuildPausedWithoutAnActiveTask()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var prepared = CreatePrepared("pause-race", GameArchitecture.X64);
        var instance = store.Create(new InstanceDraft { Name = "Pause Race" }, prepared);
        var installStarted = NewSignal();
        var releaseInstall = NewSignal();
        var installerReturned = NewSignal();
        var releasedForPause = 0;
        var pauseRecordObserved = 0;
        var installerFinishedBeforeCancel = 0;
        var coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) => Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64)),
            async (value, _, _, _) =>
            {
                installStarted.TrySetResult();
                await releaseInstall.Task;
                installerReturned.TrySetResult();
                return Result(value.Architecture);
            });
        store.InstancesChanged += (_, _) =>
        {
            var current = store.Instances.Single(item => item.Id == instance.Id);
            if (current.State == InstanceInstallationState.Installing
                && current.PauseRequested
                && Interlocked.Exchange(ref releasedForPause, 1) == 0)
            {
                Volatile.Write(ref pauseRecordObserved, 1);
                releaseInstall.TrySetResult();
                if (installerReturned.Task.Wait(TimeSpan.FromSeconds(10)))
                {
                    Volatile.Write(ref installerFinishedBeforeCancel, 1);
                }
            }
        };

        await coordinator.StartAsync();
        await installStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await coordinator.PauseAsync(instance.Id);
        var paused = await WaitForStateAsync(store, instance.Id, InstanceInstallationState.Paused);
        await WaitForActiveAsync(coordinator, null);

        Assert.AreEqual(1, Volatile.Read(ref pauseRecordObserved));
        Assert.AreEqual(1, Volatile.Read(ref installerFinishedBeforeCancel));
        Assert.IsTrue(paused.PauseRequested);
        Assert.IsNull(coordinator.ActiveInstanceId);
        Assert.IsFalse(coordinator.Progress.ContainsKey(instance.Id));
    }

    [TestMethod]
    public async Task PauseRequestedInstallationRecoversAsPaused()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var instance = store.Create(new InstanceDraft { Name = "Interrupted Pause" }, CreatePrepared("paused", GameArchitecture.X64));
        await UpdateInstancesAsync(store, instances => instances.Select(item => item.Id == instance.Id
            ? item with { State = InstanceInstallationState.Installing, PauseRequested = true }
            : item).ToList());
        var prepareCount = 0;
        var coordinator = CreateCoordinator(
            temporary,
            store,
            (_, _, _) =>
            {
                Interlocked.Increment(ref prepareCount);
                throw new AssertFailedException("A recovered pause must not start preparation.");
            },
            (_, _, _, _) => throw new AssertFailedException("A recovered pause must not start installation."));

        await coordinator.StartAsync();

        var recovered = store.Instances.Single(item => item.Id == instance.Id);
        Assert.AreEqual(InstanceInstallationState.Paused, recovered.State);
        Assert.IsTrue(recovered.PauseRequested);
        Assert.AreEqual(0, prepareCount);
    }

    [TestMethod]
    public async Task InterruptedInstallationReturnsToQueueAndPreparesWithPinnedArchitecture()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var instance = store.Create(new InstanceDraft { Name = "Interrupted Install" }, CreatePrepared("interrupted", GameArchitecture.Arm64));
        await UpdateInstancesAsync(store, instances => instances.Select(item => item.Id == instance.Id
            ? item with { State = InstanceInstallationState.Installing, PauseRequested = false }
            : item).ToList());
        GameArchitecture? usedPinnedArchitecture = null;
        var installationStarted = NewSignal();
        var finishInstall = NewSignal();
        var coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) =>
            {
                usedPinnedArchitecture = pinned;
                return Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64));
            },
            async (prepared, _, _, cancellationToken) =>
            {
                installationStarted.TrySetResult();
                await finishInstall.Task.WaitAsync(cancellationToken);
                return Result(prepared.Architecture);
            });

        await coordinator.StartAsync();
        await installationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(InstanceInstallationState.Installing, store.Instances.Single(item => item.Id == instance.Id).State);
        Assert.AreEqual(GameArchitecture.Arm64, usedPinnedArchitecture);
        finishInstall.TrySetResult();
        await WaitForStateAsync(store, instance.Id, InstanceInstallationState.Ready);
    }

    [TestMethod]
    public async Task ResumeAndRetryUsePinnedArchitectureAndSuccessfulInstallStoresJavaDetails()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var instance = store.Create(new InstanceDraft { Name = "Resume Retry" }, CreatePrepared("resume-retry", GameArchitecture.Arm64));
        await UpdateInstancesAsync(store, instances => instances.Select(item => item.Id == instance.Id
            ? item with { State = InstanceInstallationState.Paused, PauseRequested = true }
            : item).ToList());
        var pinnedArchitectures = new ConcurrentQueue<GameArchitecture?>();
        var installAttempt = 0;
        var coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) =>
            {
                pinnedArchitectures.Enqueue(pinned);
                return Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64));
            },
            (prepared, _, _, _) =>
            {
                if (Interlocked.Increment(ref installAttempt) == 1)
                {
                    throw new MojangException(MojangError.Http, "The test download failed.");
                }

                return Task.FromResult(new InstallationResult(prepared.Architecture, "bin/javaw.exe", 21, false));
            });

        await coordinator.StartAsync();
        await coordinator.ResumeAsync(instance.Id);
        var failed = await WaitForStateAsync(store, instance.Id, InstanceInstallationState.Failed);

        Assert.AreEqual("mojang.http", failed.ErrorCode);
        Assert.AreEqual("The test download failed.", failed.ErrorMessage);
        await coordinator.RetryAsync(instance.Id);
        var ready = await WaitForStateAsync(store, instance.Id, InstanceInstallationState.Ready);

        Assert.AreEqual(2, pinnedArchitectures.Count);
        Assert.IsTrue(pinnedArchitectures.All(architecture => architecture == GameArchitecture.Arm64));
        Assert.AreEqual("bin/javaw.exe", ready.JavaExecutable);
        Assert.AreEqual(21, ready.JavaMajorVersion);
        Assert.AreEqual("Arm64", ready.Architecture);
        Assert.IsNull(ready.ErrorCode);
        Assert.IsFalse(ready.PauseRequested);
    }

    [TestMethod]
    public async Task FailureOfOneBuildDoesNotBlockTheNextQueuedBuild()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var first = store.Create(new InstanceDraft { Name = "First Failure" }, "first-failure");
        var second = store.Create(new InstanceDraft { Name = "Second Success" }, "second-success");
        var started = new ConcurrentQueue<string>();
        var coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) => Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64)),
            (prepared, _, _, _) =>
            {
                started.Enqueue(prepared.Version.Id);
                if (prepared.Version.Id == "first-failure")
                {
                    throw new MojangException(MojangError.Unsupported, "No compatible runtime.");
                }

                return Task.FromResult(Result(prepared.Architecture));
            });

        await coordinator.StartAsync();
        var failed = await WaitForStateAsync(store, first.Id, InstanceInstallationState.Failed);
        var ready = await WaitForStateAsync(store, second.Id, InstanceInstallationState.Ready);

        Assert.AreEqual("mojang.unsupported", failed.ErrorCode);
        Assert.AreEqual(InstanceInstallationState.Ready, ready.State);
        CollectionAssert.AreEqual(new[] { "first-failure", "second-success" }, started.ToArray());
    }

    [TestMethod]
    public async Task FirstEnqueueUsesVerifiedPreparationAndCreatePinsItsMetadataAndArchitecture()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var coordinator = CreateCoordinator(
            temporary,
            store,
            (_, _, _) => throw new AssertFailedException("The verified preparation should be reused."),
            (prepared, root, progress, _) =>
            {
                Assert.AreEqual(GameArchitecture.X64, prepared.Architecture);
                Assert.IsTrue(Directory.Exists(Path.Combine(root, "java")));
                progress!.Report(new InstallationProgress(InstallationProgressStage.Downloading, 4, 8, 1, 2));
                return Task.FromResult(new InstallationResult(GameArchitecture.X64, "bin/javaw.exe", 17, true));
            });
        await coordinator.StartAsync();

        var prepared = CreatePrepared("verified", GameArchitecture.X64);
        var instance = await coordinator.CreateAndEnqueueAsync(new InstanceDraft { Name = "Verified Build" }, prepared);
        Assert.AreEqual(prepared.Version.Url, instance.MetadataURL);
        Assert.AreEqual(prepared.Version.Sha1, instance.MetadataSHA1);
        Assert.AreEqual("X64", instance.Architecture);
        Assert.AreEqual(17, instance.JavaMajorVersion);

        var ready = await WaitForStateAsync(store, instance.Id, InstanceInstallationState.Ready);

        Assert.AreEqual("bin/javaw.exe", ready.JavaExecutable);
        Assert.AreEqual(17, ready.JavaMajorVersion);
        Assert.IsTrue(ready.LegacyTexturepacks);
    }

    [TestMethod]
    public async Task CreateAndEnqueueKeepsPauseAndRepreparesWhenPausedDuringCreate()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        InstallationCoordinator? coordinator = null;
        Exception? pauseError = null;
        var pauseRequested = 0;
        var prepareCount = 0;
        var installCount = 0;
        GameArchitecture? pinnedArchitecture = null;
        coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) =>
            {
                Interlocked.Increment(ref prepareCount);
                pinnedArchitecture = pinned;
                return Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64));
            },
            (prepared, _, _, _) =>
            {
                Interlocked.Increment(ref installCount);
                return Task.FromResult(Result(prepared.Architecture));
            });
        store.InstancesChanged += (_, _) =>
        {
            var created = store.Instances.FirstOrDefault(item => item.Name == "Paused During Create");
            if (created?.State == InstanceInstallationState.Queued
                && Interlocked.Exchange(ref pauseRequested, 1) == 0)
            {
                try
                {
                    coordinator!.PauseAsync(created.Id).GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    pauseError = exception;
                }
            }
        };

        await coordinator.StartAsync();
        var createdBuild = await coordinator.CreateAndEnqueueAsync(
            new InstanceDraft { Name = "Paused During Create" },
            CreatePrepared("paused-during-create", GameArchitecture.X64));

        Assert.IsNull(pauseError);
        Assert.AreEqual(1, Volatile.Read(ref pauseRequested));
        Assert.AreEqual(InstanceInstallationState.Paused, createdBuild.State);
        Assert.IsTrue(createdBuild.PauseRequested);
        Assert.AreEqual(0, Volatile.Read(ref prepareCount));
        Assert.AreEqual(0, Volatile.Read(ref installCount));

        await coordinator.ResumeAsync(createdBuild.Id);
        var ready = await WaitForStateAsync(store, createdBuild.Id, InstanceInstallationState.Ready);

        Assert.AreEqual(1, Volatile.Read(ref prepareCount));
        Assert.AreEqual(GameArchitecture.X64, pinnedArchitecture);
        Assert.AreEqual(1, Volatile.Read(ref installCount));
        Assert.IsFalse(ready.PauseRequested);
    }

    [TestMethod]
    public async Task PauseWithIOExceptionPersistsPausedAndKeepsPausingProgress()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var instance = store.Create(new InstanceDraft { Name = "Pause IOException" }, CreatePrepared("pause-io", GameArchitecture.X64));
        var installationStarted = NewSignal();
        var cancellationObserved = NewSignal();
        var progressAfterPause = NewSignal<InstallationProgressStage>();
        var pauseWasPersisted = false;
        InstallationCoordinator? coordinator = null;
        coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) => Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64)),
            async (_, _, progress, cancellationToken) =>
            {
                installationStarted.TrySetResult();
                progress!.Report(new InstallationProgress(InstallationProgressStage.Downloading, 1, 1000));
                using var registration = cancellationToken.Register(() =>
                {
                    pauseWasPersisted = store.Instances.Single(item => item.Id == instance.Id).PauseRequested;
                    cancellationObserved.TrySetResult();
                });
                await cancellationObserved.Task;
                progress.Report(new InstallationProgress(InstallationProgressStage.Downloading, 2, 1000));
                progressAfterPause.TrySetResult(coordinator!.Progress[instance.Id].Stage);
                throw new IOException("The stream failed while cancellation was in progress.");
            });
        var terminal = WaitForTerminalAndCheckAccessAsync(store, instance.Id, InstanceInstallationState.Paused);

        await coordinator.StartAsync();
        await installationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await coordinator.PauseAsync(instance.Id);
        var visibleStage = await progressAfterPause.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await terminal.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(pauseWasPersisted);
        Assert.AreEqual(InstallationProgressStage.Pausing, visibleStage);
        Assert.AreEqual(InstanceInstallationState.Paused, result.Instance.State);
        Assert.IsTrue(result.Instance.PauseRequested);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public async Task DisposeAfterSuccessfulInstallationResultPersistsReady()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var instance = store.Create(new InstanceDraft { Name = "Dispose Successful Install" },
            CreatePrepared("dispose-success", GameArchitecture.X64));
        var installationStarted = NewSignal();
        var releaseInstallation = NewSignal();
        var leaseReleased = NewSignal();
        var allowFinalization = NewSignal();
        var cancellationObserved = NewSignal();
        CancellationTokenRegistration cancellationRegistration = default;
        var coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) => Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64)),
            async (prepared, _, _, cancellationToken) =>
            {
                installationStarted.TrySetResult();
                cancellationRegistration = cancellationToken.Register(() => cancellationObserved.TrySetResult());
                await releaseInstallation.Task;
                return Result(prepared.Architecture);
            });
        EventHandler holdFinalization = (_, _) =>
        {
            leaseReleased.TrySetResult();
            allowFinalization.Task.GetAwaiter().GetResult();
        };

        await coordinator.StartAsync();
        await installationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        store.BusyStateChanged += holdFinalization;
        releaseInstallation.TrySetResult();
        try
        {
            await leaseReleased.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var disposeTask = coordinator.DisposeAsync().AsTask();
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(disposeTask.IsCompleted);

            allowFinalization.TrySetResult();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            allowFinalization.TrySetResult();
            store.BusyStateChanged -= holdFinalization;
            cancellationRegistration.Dispose();
        }

        var completed = store.Instances.Single(item => item.Id == instance.Id);
        Assert.AreEqual(InstanceInstallationState.Ready, completed.State);
        Assert.IsFalse(completed.PauseRequested);
        Assert.IsNull(completed.ErrorCode);
    }

    [TestMethod]
    public async Task DisposeLeavesActiveInstallationForTheNextCoordinatorToResume()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var instance = store.Create(new InstanceDraft { Name = "Dispose Active Install" },
            CreatePrepared("dispose-active", GameArchitecture.X64));
        var installationStarted = NewSignal();
        var releaseInstallation = NewSignal();
        var cancellationObserved = NewSignal();
        var coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) => Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64)),
            async (prepared, _, _, cancellationToken) =>
            {
                installationStarted.TrySetResult();
                try
                {
                    await releaseInstallation.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }

                return Result(prepared.Architecture);
            });

        await coordinator.StartAsync();
        await installationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await coordinator.DisposeAsync();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsNull(coordinator.ActiveInstanceId);
        var interrupted = store.Instances.Single(item => item.Id == instance.Id);
        Assert.AreEqual(InstanceInstallationState.Installing, interrupted.State);
        Assert.IsFalse(interrupted.PauseRequested);

        var resumedInstallationStarted = NewSignal();
        var releaseResumedInstallation = NewSignal();
        var resumedCoordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) => Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64)),
            async (prepared, _, _, cancellationToken) =>
            {
                resumedInstallationStarted.TrySetResult();
                await releaseResumedInstallation.Task.WaitAsync(cancellationToken);
                return Result(prepared.Architecture);
            });

        await resumedCoordinator.StartAsync();
        await resumedInstallationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var resumed = store.Instances.Single(item => item.Id == instance.Id);
        Assert.AreEqual(InstanceInstallationState.Installing, resumed.State);
        Assert.IsFalse(resumed.PauseRequested);

        releaseResumedInstallation.TrySetResult();
        await WaitForStateAsync(store, instance.Id, InstanceInstallationState.Ready);
    }

    [TestMethod]
    public async Task UserPauseRemainsPausedWhenCoordinatorIsDisposed()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var instance = store.Create(new InstanceDraft { Name = "Dispose Paused Install" },
            CreatePrepared("dispose-paused", GameArchitecture.X64));
        var installationStarted = NewSignal();
        var cancellationObserved = NewSignal();
        var allowUnwind = NewSignal();
        var coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) => Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64)),
            async (_, _, _, cancellationToken) =>
            {
                installationStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    await allowUnwind.Task;
                    throw;
                }

                throw new InvalidOperationException("The installation was not cancelled.");
            });

        await coordinator.StartAsync();
        await installationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await coordinator.PauseAsync(instance.Id);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disposeTask = coordinator.DisposeAsync().AsTask();
        Assert.IsFalse(disposeTask.IsCompleted);

        allowUnwind.TrySetResult();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(10));

        var paused = store.Instances.Single(item => item.Id == instance.Id);
        Assert.AreEqual(InstanceInstallationState.Paused, paused.State);
        Assert.IsTrue(paused.PauseRequested);
    }

    [TestMethod]
    public async Task ReadyEventFiresAfterLeaseIsReleased()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var instance = store.Create(new InstanceDraft { Name = "Ready Lease" }, CreatePrepared("ready-lease", GameArchitecture.X64));
        var coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) => Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64)),
            (prepared, _, _, _) => Task.FromResult(Result(prepared.Architecture)));
        var terminal = WaitForTerminalAndCheckAccessAsync(store, instance.Id, InstanceInstallationState.Ready);

        await coordinator.StartAsync();
        var result = await terminal.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(InstanceInstallationState.Ready, result.Instance.State);
        Assert.IsNull(result.Error);
        Assert.IsFalse(store.Instances.Any(item => item.Id == instance.Id));
    }

    [TestMethod]
    public async Task QueuedBuildCanBePausedWhileEarlierBuildIsInstalling()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var first = store.Create(new InstanceDraft { Name = "Active First" }, "active-first");
        var second = store.Create(new InstanceDraft { Name = "Queued Second" }, "queued-second");
        await UpdateInstancesAsync(store, instances => instances.Select(item => item.Id == first.Id
            ? item with { CreatedAt = DateTimeOffset.Parse("2024-01-01T00:00:00Z") }
            : item with { CreatedAt = DateTimeOffset.Parse("2024-02-01T00:00:00Z") }).ToList());
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var started = new ConcurrentQueue<string>();
        var coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) => Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64)),
            async (prepared, _, _, cancellationToken) =>
            {
                started.Enqueue(prepared.Version.Id);
                if (prepared.Version.Id == "active-first")
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }

                return Result(prepared.Architecture);
            });

        await coordinator.StartAsync();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await coordinator.PauseAsync(second.Id);
        Assert.AreEqual(InstanceInstallationState.Paused, store.Instances.Single(item => item.Id == second.Id).State);
        releaseFirst.TrySetResult();
        await WaitForStateAsync(store, first.Id, InstanceInstallationState.Ready);
        await WaitForActiveAsync(coordinator, null);

        CollectionAssert.AreEqual(new[] { "active-first" }, started.ToArray());
    }

    [TestMethod]
    public async Task ResumeDuringInstallingPauseRequestIsRejected()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var instance = store.Create(new InstanceDraft { Name = "Pause Transition" }, CreatePrepared("pause-transition", GameArchitecture.X64));
        var installationStarted = NewSignal();
        var releaseInstall = NewSignal();
        var coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) => Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64)),
            async (prepared, _, _, cancellationToken) =>
            {
                installationStarted.TrySetResult();
                await releaseInstall.Task;
                cancellationToken.ThrowIfCancellationRequested();
                return Result(prepared.Architecture);
            });

        await coordinator.StartAsync();
        await installationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await coordinator.PauseAsync(instance.Id);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ResumeAsync(instance.Id));
        releaseInstall.TrySetResult();
        await WaitForStateAsync(store, instance.Id, InstanceInstallationState.Paused);
    }

    [TestMethod]
    public async Task BusyFirstBuildDoesNotBlockNextQueuedBuild()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var first = store.Create(new InstanceDraft { Name = "Busy First" }, "busy-first");
        var second = store.Create(new InstanceDraft { Name = "Free Second" }, "free-second");
        await UpdateInstancesAsync(store, instances => instances.Select(item => item.Id == first.Id
            ? item with { CreatedAt = DateTimeOffset.Parse("2024-01-01T00:00:00Z") }
            : item with { CreatedAt = DateTimeOffset.Parse("2024-02-01T00:00:00Z") }).ToList());
        store.SetInstallationStatus(first.Id, InstanceInstallationState.Ready, pauseRequested: false);
        Assert.IsTrue(store.TryBeginContent(first.Id, out var busyLease, out _));
        store.SetInstallationStatus(first.Id, InstanceInstallationState.Queued, pauseRequested: false);
        var secondStarted = NewSignal();
        var releaseSecond = NewSignal();
        var firstStarted = NewSignal();
        var coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) => Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64)),
            async (prepared, _, _, cancellationToken) =>
            {
                if (prepared.Version.Id == "free-second")
                {
                    secondStarted.TrySetResult();
                    await releaseSecond.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    firstStarted.TrySetResult();
                }

                return Result(prepared.Architecture);
            });

        await coordinator.StartAsync();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(second.Id, coordinator.ActiveInstanceId);
        busyLease!.Dispose();
        releaseSecond.TrySetResult();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForStateAsync(store, first.Id, InstanceInstallationState.Ready);
        await WaitForStateAsync(store, second.Id, InstanceInstallationState.Ready);
    }

    [TestMethod]
    public async Task EnqueueingSameIdRepeatedlyStartsOnlyOneInstallation()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var instance = store.Create(new InstanceDraft { Name = "Repeated Enqueue" }, CreatePrepared("repeat-enqueue", GameArchitecture.X64));
        store.SetInstallationStatus(instance.Id, InstanceInstallationState.Ready, pauseRequested: false);
        Assert.IsTrue(store.TryBeginContent(instance.Id, out var busyLease, out _));
        store.SetInstallationStatus(instance.Id, InstanceInstallationState.Queued, pauseRequested: false);
        var installCount = 0;
        var coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) => Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64)),
            (prepared, _, _, _) =>
            {
                Interlocked.Increment(ref installCount);
                return Task.FromResult(Result(prepared.Architecture));
            });

        await coordinator.StartAsync();
        await coordinator.EnqueueAsync(instance.Id);
        await coordinator.EnqueueAsync(instance.Id);
        busyLease!.Dispose();
        await WaitForStateAsync(store, instance.Id, InstanceInstallationState.Ready);

        Assert.AreEqual(1, installCount);
    }

    [TestMethod]
    public async Task ProgressIsThrottledByFractionAndTimeButStageChangesAreAlwaysReported()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var instance = store.Create(new InstanceDraft { Name = "Progress Throttle" }, CreatePrepared("progress-throttle", GameArchitecture.X64));
        var timeProvider = new ManualTimeProvider();
        var observed = new ConcurrentQueue<InstallationProgress>();
        InstallationCoordinator? coordinator = null;
        coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) => Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64)),
            (prepared, _, progress, _) =>
            {
                progress!.Report(new InstallationProgress(InstallationProgressStage.Downloading, 0, 1000));
                progress.Report(new InstallationProgress(InstallationProgressStage.Downloading, 1, 1000));
                timeProvider.Advance(TimeSpan.FromMilliseconds(149));
                progress.Report(new InstallationProgress(InstallationProgressStage.Downloading, 1, 1000));
                timeProvider.Advance(TimeSpan.FromMilliseconds(1));
                progress.Report(new InstallationProgress(InstallationProgressStage.Downloading, 1, 1000));
                progress.Report(new InstallationProgress(InstallationProgressStage.PreparingAssets, 1, 1000));
                progress.Report(new InstallationProgress(InstallationProgressStage.Complete, 1000, 1000));
                return Task.FromResult(Result(prepared.Architecture));
            },
            timeProvider);
        coordinator.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(InstallationCoordinator.Progress)
                && coordinator.Progress.TryGetValue(instance.Id, out var current))
            {
                observed.Enqueue(current);
            }
        };

        await coordinator.StartAsync();
        await WaitForStateAsync(store, instance.Id, InstanceInstallationState.Ready);

        var reports = observed.ToArray();
        CollectionAssert.AreEqual(new long[] { 0, 1 }, reports
            .Where(item => item.Stage == InstallationProgressStage.Downloading)
            .Select(item => item.CompletedBytes).ToArray());
        Assert.IsTrue(reports.Any(item => item.Stage == InstallationProgressStage.PreparingAssets));
        Assert.IsTrue(reports.Any(item => item.Stage == InstallationProgressStage.Complete));
    }

    [TestMethod]
    public async Task LoadErrorDuringInstallationIsReportedAsQueueError()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var instance = store.Create(new InstanceDraft { Name = "Load Error" }, CreatePrepared("load-error", GameArchitecture.X64));
        var installationStarted = NewSignal();
        var releaseInstall = NewSignal();
        var coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) => Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64)),
            async (prepared, _, _, _) =>
            {
                installationStarted.TrySetResult();
                await releaseInstall.Task;
                return Result(prepared.Architecture);
            });

        await coordinator.StartAsync();
        await installationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await File.WriteAllTextAsync(Path.Combine(temporary.Paths.GameRoot, "instances.json"), "not-json");
        await store.LoadAsync();
        Assert.IsNotNull(store.LoadError);
        var queueError = WaitForQueueErrorAsync(coordinator);
        releaseInstall.TrySetResult();

        var message = await queueError.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForActiveAsync(coordinator, null);

        Assert.IsFalse(string.IsNullOrWhiteSpace(message));
        Assert.IsNull(coordinator.ActiveInstanceId);
    }

    [TestMethod]
    public async Task RetryCanRecoverInstallingBuildWhenTerminalRegistryWriteFailed()
    {
        await using var temporary = new TemporaryDirectory();
        var originalStore = await LoadStoreAsync(temporary);
        var instance = originalStore.Create(new InstanceDraft { Name = "Orphaned Install" }, CreatePrepared("orphaned-install", GameArchitecture.X64));
        var failTerminalWrite = 0;
        var store = temporary.CreateStore(() =>
        {
            if (Volatile.Read(ref failTerminalWrite) != 0)
            {
                throw new IOException("Injected terminal registry write failure.");
            }
        });
        await store.LoadAsync();
        var attempt = 0;
        var coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) => Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64)),
            (prepared, _, _, _) =>
            {
                if (Interlocked.Increment(ref attempt) == 1)
                {
                    Volatile.Write(ref failTerminalWrite, 1);
                }

                return Task.FromResult(Result(prepared.Architecture));
            });

        await coordinator.StartAsync();
        await WaitForQueueErrorAsync(coordinator);
        await WaitForActiveAsync(coordinator, null);
        Assert.AreEqual(InstanceInstallationState.Installing, store.Instances.Single(item => item.Id == instance.Id).State);
        Assert.IsFalse(store.Instances.Single(item => item.Id == instance.Id).PauseRequested);
        Assert.IsNotNull(coordinator.QueueError);

        Volatile.Write(ref failTerminalWrite, 0);
        await coordinator.RetryAsync(instance.Id);
        var ready = await WaitForStateAsync(store, instance.Id, InstanceInstallationState.Ready);

        Assert.AreEqual(2, attempt);
        Assert.AreEqual(InstanceInstallationState.Ready, ready.State);
    }

    [TestMethod]
    public async Task CompleteInstallationValidationFailureIsPersistedAsFailed()
    {
        await using var temporary = new TemporaryDirectory();
        var store = await LoadStoreAsync(temporary);
        var instance = store.Create(new InstanceDraft { Name = "Architecture Mismatch" },
            CreatePrepared("architecture-mismatch", GameArchitecture.X64));
        var coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) => Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64)),
            (_, _, _, _) => Task.FromResult(Result(GameArchitecture.Arm64)));

        await coordinator.StartAsync();
        var failed = await WaitForStateAsync(store, instance.Id, InstanceInstallationState.Failed);
        await WaitForActiveAsync(coordinator, null);

        Assert.AreEqual("storage.operationfailed", failed.ErrorCode);
        StringAssert.Contains(failed.ErrorMessage!, "different architecture");
        Assert.IsFalse(failed.PauseRequested);
    }

    [TestMethod]
    public async Task TerminalRegistryWriteFailureFallsBackToFailedState()
    {
        await using var temporary = new TemporaryDirectory();
        var failNextCommit = 0;
        var store = temporary.CreateStore(() =>
        {
            if (Interlocked.Exchange(ref failNextCommit, 0) == 1)
            {
                throw new IOException("Injected terminal registry write failure.");
            }
        });
        await store.LoadAsync();
        var instance = store.Create(new InstanceDraft { Name = "Failed Terminal Write" },
            CreatePrepared("failed-terminal-write", GameArchitecture.X64));
        var coordinator = CreateCoordinator(
            temporary,
            store,
            (version, pinned, _) => Task.FromResult(CreatePrepared(version.Id, pinned ?? GameArchitecture.X64)),
            (prepared, _, _, _) =>
            {
                Volatile.Write(ref failNextCommit, 1);
                return Task.FromResult(Result(prepared.Architecture));
            });

        await coordinator.StartAsync();
        var failed = await WaitForStateAsync(store, instance.Id, InstanceInstallationState.Failed);
        await WaitForActiveAsync(coordinator, null);

        Assert.AreEqual("installation.unexpected", failed.ErrorCode);
        StringAssert.Contains(failed.ErrorMessage!, "Injected terminal registry write failure.");
        Assert.IsFalse(failed.PauseRequested);
        Assert.AreEqual("Injected terminal registry write failure.", coordinator.QueueError);
    }

    private static InstallationCoordinator CreateCoordinator(
        TemporaryDirectory temporary,
        InstanceStore store,
        Func<MojangVersionEntry, GameArchitecture?, CancellationToken, Task<PreparedInstallation>> prepare,
        Func<PreparedInstallation, string, IProgress<InstallationProgress>?, CancellationToken, Task<InstallationResult>> install,
        TimeProvider? timeProvider = null) =>
        temporary.Track(new InstallationCoordinator(store, prepare, install, timeProvider));

    private static PreparedInstallation CreatePrepared(string versionId, GameArchitecture architecture)
    {
        var version = new MojangVersionEntry
        {
            Id = versionId,
            Type = "release",
            Url = $"https://test.invalid/{versionId}.json",
            Sha1 = new string('a', 40)
        };
        var manifest = new MojangVersionMetadata
        {
            Id = versionId,
            JavaVersion = new MojangJavaRequirement { Component = "java-runtime-gamma", MajorVersion = 17 }
        };
        return new PreparedInstallation(
            version,
            architecture,
            manifest,
            [],
            new MojangJavaRuntimePackage(),
            new MojangJavaRuntimeManifest(),
            [],
            []);
    }

    private static InstallationResult Result(GameArchitecture architecture) =>
        new(architecture, "bin/javaw.exe", 17, false);

    private static async Task<InstanceStore> LoadStoreAsync(TemporaryDirectory temporary)
    {
        var store = temporary.CreateStore();
        await store.LoadAsync();
        return store;
    }

    private static async Task UpdateInstancesAsync(
        InstanceStore store,
        Func<List<GameInstance>, List<GameInstance>> update)
    {
        var registryPath = Path.Combine(store.Instances.Count > 0
            ? Path.GetDirectoryName(store.GetInstanceDirectory(store.Instances[0].Id))!
            : throw new InvalidOperationException("At least one build is needed to update the test registry."), "instances.json");
        var registry = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(registryPath),
            HakoJsonContext.Default.InstanceRegistryFile)!;
        var instances = update(registry.Instances);
        var updated = registry with { Instances = instances };
        await File.WriteAllBytesAsync(registryPath, JsonSerializer.SerializeToUtf8Bytes(updated,
            HakoJsonContext.Default.InstanceRegistryFile));
        await store.LoadAsync();
    }

    private static async Task<GameInstance> WaitForStateAsync(
        InstanceStore store,
        Guid id,
        InstanceInstallationState state)
    {
        GameInstance? matching = null;
        var completed = NewSignal();
        void Check()
        {
            matching = store.Instances.FirstOrDefault(instance => instance.Id == id && instance.State == state);
            if (matching is not null)
            {
                completed.TrySetResult();
            }
        }

        EventHandler handler = (_, _) => Check();
        store.InstancesChanged += handler;
        try
        {
            Check();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return matching!;
        }
        finally
        {
            store.InstancesChanged -= handler;
        }
    }

    private static async Task WaitForActiveAsync(InstallationCoordinator coordinator, Guid? id)
    {
        var completed = NewSignal();
        void Check()
        {
            if (coordinator.ActiveInstanceId == id)
            {
                completed.TrySetResult();
            }
        }

        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(InstallationCoordinator.ActiveInstanceId))
            {
                Check();
            }
        };
        coordinator.PropertyChanged += handler;
        try
        {
            Check();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            coordinator.PropertyChanged -= handler;
        }
    }

    private static async Task<string?> WaitForQueueErrorAsync(InstallationCoordinator coordinator)
    {
        var completed = NewSignal<string?>();
        void Check()
        {
            if (coordinator.QueueError is { } value)
            {
                completed.TrySetResult(value);
            }
        }

        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(InstallationCoordinator.QueueError))
            {
                Check();
            }
        };
        coordinator.PropertyChanged += handler;
        try
        {
            Check();
            return await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            coordinator.PropertyChanged -= handler;
        }
    }

    private static Task<TerminalEventResult> WaitForTerminalAndCheckAccessAsync(
        InstanceStore store,
        Guid id,
        InstanceInstallationState expectedState)
    {
        var completed = NewSignal<TerminalEventResult>();
        var checking = 0;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            var instance = store.Instances.FirstOrDefault(item => item.Id == id);
            if (instance?.State != expectedState || Interlocked.Exchange(ref checking, 1) != 0)
            {
                return;
            }

            store.InstancesChanged -= handler;
            Exception? error = null;
            try
            {
                if (!store.TryBeginLaunch(id, out var launchLease, out var reason))
                {
                    throw new InvalidOperationException($"TryBeginLaunch failed at {expectedState} with {reason}.");
                }

                launchLease!.Dispose();
                store.Rename(id, "After Terminal");
                store.Delete(id);
            }
            catch (Exception exception)
            {
                error = exception;
            }

            completed.TrySetResult(new TerminalEventResult(instance, error));
        };
        store.InstancesChanged += handler;
        return completed.Task;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record TerminalEventResult(GameInstance Instance, Exception? Error);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }

    private sealed class TemporaryDirectory : IDisposable, IAsyncDisposable
    {
        private readonly List<IAsyncDisposable> _resources = [];

        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Hako.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Paths = HakoPaths.CreateIsolated(System.IO.Path.Combine(Path, "game"), System.IO.Path.Combine(Path, "data"));
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
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

using System.IO;
using Hako.Core;
using Hako.Core.Minecraft;
using Hako.Core.Playtime;

namespace Hako.Tests;

[TestClass]
public sealed class PlaytimeTests
{
    [TestMethod]
    public async Task HakoFinishedHelperCheckpointIsQueuedOnRestartBeforeJournalDeletion()
    {
        await using var fixture = new PlaytimeFixture();
        var account = Guid.NewGuid().ToString();
        await fixture.Coordinator.SetHakoAccountAsync(account);
        var (id, _) = await fixture.StartSessionAsync("guest");
        var path = PlaytimePaths.Journal(fixture.GameRoot, id);
        var journal = PlaytimeJson.ReadJournal(path);
        journal.ElapsedSeconds = 65;
        journal.IsFinished = true;
        PlaytimeJson.AtomicWrite(path, PlaytimeJson.SerializeJournal(journal));
        fixture.Probe.SetRunning(new PlaytimeProcessIdentity(151, 100, 0), false);
        await using var restarted = fixture.CreateCoordinator();
        await restarted.SetHakoAccountAsync(account);
        Assert.IsFalse(File.Exists(path));
        var queued = (await fixture.ReadLedgerAsync()).Uploads.Values.Single();
        Assert.AreEqual(account, queued.AccountId);
        Assert.AreEqual(65000L, queued.DurationMs);
        await restarted.SetHakoAccountAsync(account);
        Assert.AreEqual(queued.SessionId, (await fixture.ReadLedgerAsync()).Uploads.Values.Single().SessionId);
    }

    [TestMethod]
    public async Task HakoImportUpgradesOldLedgerAndRejectsConcurrentMicrosoftReservationUntilResolved()
    {
        await using var fixture = new PlaytimeFixture();
        await fixture.StartSessionAsync("guest");
        fixture.Clock.AdvanceSeconds(8);
        var transfer = await fixture.Coordinator.PrepareGuestTransferAsync("microsoft");
        Assert.IsNotNull(transfer);
        var account = Guid.NewGuid().ToString();
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Coordinator.SetHakoAccountAsync(account));
        Assert.AreEqual(0, (await fixture.ReadLedgerAsync()).Uploads.Count);
        await fixture.Coordinator.CompleteGuestTransferAsync(transfer.Value);
        var legacy = await fixture.ReadLedgerAsync();
        legacy.FormatVersion = 1;
        PlaytimeJson.AtomicWrite(PlaytimePaths.Ledger(fixture.GameRoot),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(legacy, HakoJsonContext.Default.PlaytimeLedger));
        await fixture.Coordinator.SetHakoAccountAsync(account);
        var migrated = await fixture.ReadLedgerAsync();
        Assert.AreEqual(2, migrated.FormatVersion);
        Assert.AreEqual(8000L, migrated.Uploads.Values.Single().DurationMs);
        Assert.AreEqual(8, fixture.Coordinator.InstanceSeconds(fixture.InstanceID, "microsoft"), 0.001);
    }

    [TestMethod]
    public async Task HakoHistoryReservationSurvivesRestartAndPreservesInstanceTime()
    {
        await using var fixture = new PlaytimeFixture();
        await new PlaytimeLedgerStore(fixture.GameRoot).MutateAsync(ledger => {
            ledger.Owners["guest"] = new PlaytimeOwnerRecord { TotalSeconds = 12, Instances = new() { [fixture.InstanceID] = 12 } };
            ledger.Owners["account:old"] = new PlaytimeOwnerRecord { TotalSeconds = 30 };
            return true;
        });
        var account = Guid.NewGuid().ToString();
        await fixture.Coordinator.SetHakoAccountAsync(account);
        var reserved = (await fixture.ReadLedgerAsync()).Uploads.Values.Single();
        Assert.AreEqual(42000L, reserved.DurationMs);
        Assert.AreEqual(0, fixture.Coordinator.TotalSeconds(null));
        Assert.AreEqual(12, fixture.Coordinator.InstanceSeconds(fixture.InstanceID, null));
        await using var restarted = fixture.CreateCoordinator();
        await restarted.SetHakoAccountAsync(account);
        Assert.AreEqual(reserved.SessionId, (await fixture.ReadLedgerAsync()).Uploads.Values.Single().SessionId);
        await restarted.AcknowledgeHakoUploadAsync(reserved.SessionId);
        await restarted.SetHakoAccountAsync(account);
        Assert.AreEqual(0, (await fixture.ReadLedgerAsync()).Uploads.Count);
        await restarted.SetHakoAccountAsync(null);
        await restarted.SetHakoAccountAsync(Guid.NewGuid().ToString());
        Assert.AreEqual(0, (await fixture.ReadLedgerAsync()).Uploads.Count);
    }

    [TestMethod]
    public async Task HakoAccountBoundariesSplitActiveGameAndQueueOnlyCompletedSegments()
    {
        await using var fixture = new PlaytimeFixture();
        var (id, _) = await fixture.StartSessionAsync("guest");
        fixture.Clock.AdvanceSeconds(10);
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        await fixture.Coordinator.SetHakoAccountAsync(first);
        Assert.AreEqual(10000L, (await fixture.ReadLedgerAsync()).Uploads.Values.Single().DurationMs);
        fixture.Clock.AdvanceSeconds(20);
        await fixture.Coordinator.RefreshAsync();
        Assert.AreEqual(1, (await fixture.ReadLedgerAsync()).Uploads.Count);
        await fixture.Coordinator.SetHakoAccountAsync(second);
        var ledger = await fixture.ReadLedgerAsync();
        Assert.AreEqual(first, ledger.Uploads.Values.Single(x => x.DurationMs == 20000).AccountId);
        fixture.Clock.AdvanceSeconds(5);
        await fixture.Coordinator.RefreshAsync();
        var journal = PlaytimeJson.ReadJournal(PlaytimePaths.Journal(fixture.GameRoot, id));
        journal.IsFinished = true;
        PlaytimeJson.AtomicWrite(PlaytimePaths.Journal(fixture.GameRoot, id), PlaytimeJson.SerializeJournal(journal));
        fixture.Probe.SetRunning(new PlaytimeProcessIdentity(151, 100, 0), false);
        await fixture.Coordinator.RefreshAsync();
        await fixture.Coordinator.RefreshAsync();
        ledger = await fixture.ReadLedgerAsync();
        Assert.AreEqual(3, ledger.Uploads.Count);
        Assert.AreEqual(5000L, ledger.Uploads.Values.Single(x => x.AccountId == second).DurationMs);
        Assert.AreEqual(0, ledger.Sessions.Count);
        Assert.AreEqual(35, fixture.Coordinator.InstanceSeconds(fixture.InstanceID, null), 0.001);
        Assert.AreEqual(0, fixture.Coordinator.TotalSeconds(null));
    }

    [TestMethod]
    public async Task HakoLogoutKeepsPendingRecipientAndNewGuestTimeIsImportedSeparately()
    {
        await using var fixture = new PlaytimeFixture();
        var first = Guid.NewGuid().ToString();
        await fixture.Coordinator.SetHakoAccountAsync(first);
        await fixture.StartSessionAsync("guest");
        fixture.Clock.AdvanceSeconds(9);
        await fixture.Coordinator.SetHakoAccountAsync(null);
        fixture.Clock.AdvanceSeconds(4);
        var second = Guid.NewGuid().ToString();
        await fixture.Coordinator.SetHakoAccountAsync(second);
        var ledger = await fixture.ReadLedgerAsync();
        Assert.AreEqual(9000L, ledger.Uploads.Values.Single(x => x.AccountId == first).DurationMs);
        Assert.AreEqual(4000L, ledger.Uploads.Values.Single(x => x.AccountId == second).DurationMs);
    }

    [TestMethod]
    public async Task AppUpdateWaitsForGameAndHelperIndependentlyOfInstanceRegistry()
    {
        await using var fixture = new PlaytimeFixture();
        Assert.IsFalse(await fixture.Coordinator.HasActiveProcessesAsync());
        var (sessionID, _) = await fixture.StartSessionAsync("guest");
        Assert.IsTrue(await fixture.Coordinator.HasActiveProcessesAsync());
        var path = PlaytimePaths.Journal(fixture.GameRoot, sessionID);
        var journal = PlaytimeJson.ReadJournal(path);
        var helper = new PlaytimeProcessIdentity(999, 200, 0);
        journal.Helper = helper;
        PlaytimeJson.AtomicWrite(path, PlaytimeJson.SerializeJournal(journal));
        fixture.Probe.SetRunning(new PlaytimeProcessIdentity(151, 100, 0), false);
        fixture.Probe.SetRunning(helper, true);
        Assert.IsTrue(await fixture.Coordinator.HasActiveProcessesAsync());
        fixture.Probe.SetRunning(helper, false);
        Assert.IsFalse(await fixture.Coordinator.HasActiveProcessesAsync());
    }

    [TestMethod]
    public async Task ReconcileCreditsOnlyPositiveElapsedDeltaAndIsIdempotent()
    {
        await using var fixture = new PlaytimeFixture();
        var (sessionID, _) = await fixture.StartSessionAsync("guest");
        fixture.Clock.AdvanceSeconds(30);

        await fixture.Coordinator.RefreshAsync();
        await fixture.Coordinator.RefreshAsync();
        Assert.AreEqual(30, fixture.Coordinator.TotalSeconds(null), 0.001);
        Assert.AreEqual(30, fixture.Coordinator.InstanceSeconds(fixture.InstanceID, null), 0.001);

        fixture.Clock.AdvanceSeconds(10);
        await fixture.Coordinator.RefreshAsync();
        Assert.AreEqual(40, fixture.Coordinator.TotalSeconds(null), 0.001);
        Assert.IsTrue(File.Exists(PlaytimePaths.Journal(fixture.GameRoot, sessionID)));
    }

    [TestMethod]
    public async Task SleepAndWallClockChangesDoNotAddElapsedTime()
    {
        await using var fixture = new PlaytimeFixture();
        await fixture.StartSessionAsync("guest");
        fixture.Clock.JumpWallClock(TimeSpan.FromHours(8));
        fixture.Clock.AdvanceSleep(TimeSpan.FromHours(2));
        await fixture.Coordinator.RefreshAsync();
        Assert.AreEqual(0, fixture.Coordinator.TotalSeconds(null), 0.001);

        fixture.Clock.AdvanceSeconds(5);
        await fixture.Coordinator.RefreshAsync();
        Assert.AreEqual(5, fixture.Coordinator.TotalSeconds(null), 0.001);
        Assert.AreEqual(TimeSpan.FromHours(10), fixture.Clock.WallClockElapsed);
    }

    [TestMethod]
    public async Task SessionOwnerDoesNotChangeOnLogoutAndResumeKeepsSavedOwner()
    {
        await using var fixture = new PlaytimeFixture();
        var (sessionID, process) = await fixture.StartSessionAsync("account:first");
        fixture.Clock.AdvanceSeconds(15);
        await fixture.Coordinator.RefreshAsync();

        await using var second = fixture.CreateCoordinator();
        await second.ResumeSessionAsync(process, "second");
        fixture.Clock.AdvanceSeconds(10);
        await second.RefreshAsync();

        var ledger = await fixture.ReadLedgerAsync();
        Assert.AreEqual(1, ledger.Sessions.Count);
        Assert.AreEqual("account:first", ledger.Sessions[sessionID].OwnerKey);
        Assert.AreEqual(25, second.TotalSeconds("first"), 0.001);
        Assert.AreEqual(0, second.TotalSeconds("second"), 0.001);
    }

    [TestMethod]
    public async Task ResumeAfterMissingJournalContinuesFromAlreadyCreditedSeconds()
    {
        await using var fixture = new PlaytimeFixture();
        var (sessionID, process) = await fixture.StartSessionAsync("guest");
        fixture.Clock.AdvanceSeconds(10);
        await fixture.Coordinator.RefreshAsync();
        File.Delete(PlaytimePaths.Journal(fixture.GameRoot, sessionID));

        await using var restored = fixture.CreateCoordinator();
        await restored.ResumeSessionAsync(process, null);
        fixture.Clock.AdvanceSeconds(5);
        await restored.RefreshAsync();

        Assert.AreEqual(15, restored.TotalSeconds(null), 0.001);
        var journal = PlaytimeJson.ReadJournal(PlaytimePaths.Journal(fixture.GameRoot, sessionID));
        Assert.AreEqual(10, journal.BaseElapsedSeconds, 0.001);
    }

    [TestMethod]
    public async Task ReusedPidWithDifferentCreationTimeIsNeverCredited()
    {
        await using var fixture = new PlaytimeFixture();
        await fixture.StartSessionAsync("guest", pid: 271);
        fixture.Clock.AdvanceSeconds(45);
        fixture.Probe.SetRunning(new PlaytimeProcessIdentity(271, 100, 0), false);
        fixture.Probe.SetRunning(new PlaytimeProcessIdentity(271, 200, 0), true);

        await fixture.Coordinator.RefreshAsync();

        Assert.AreEqual(0, fixture.Coordinator.TotalSeconds(null), 0.001);
        Assert.AreEqual(0, (await fixture.ReadLedgerAsync()).Sessions.Count);
    }

    [TestMethod]
    public async Task FinishedSessionKeepsTotalsAfterItsInstanceIsGone()
    {
        await using var fixture = new PlaytimeFixture();
        var (sessionID, _) = await fixture.StartSessionAsync("account:123");
        fixture.Clock.AdvanceSeconds(17);
        await fixture.Coordinator.RefreshAsync();
        fixture.Probe.SetRunning(new PlaytimeProcessIdentity(151, 100, 0), false);
        fixture.Clock.AdvanceSeconds(3_600);

        await fixture.Coordinator.RefreshAsync();

        var ledger = await fixture.ReadLedgerAsync();
        Assert.AreEqual(17, fixture.Coordinator.TotalSeconds("123"), 0.001);
        Assert.AreEqual(17, fixture.Coordinator.InstanceSeconds(fixture.InstanceID, "123"), 0.001);
        Assert.IsTrue(ledger.Owners["account:123"].Instances.ContainsKey(fixture.InstanceID));
        Assert.IsFalse(ledger.Sessions.ContainsKey(sessionID));
    }

    [TestMethod]
    public async Task ReconcileRemovesOrphanLedgerRowsAndJournalsSafely()
    {
        await using var fixture = new PlaytimeFixture();
        var missingJournalSession = Guid.NewGuid();
        var orphanJournalSession = Guid.NewGuid();
        await new PlaytimeLedgerStore(fixture.GameRoot).MutateAsync(ledger =>
        {
            ledger.Sessions[missingJournalSession] = new PlaytimeSessionRecord
            {
                SessionID = missingJournalSession,
                InstanceID = fixture.InstanceID,
                OwnerKey = "guest"
            };
            return true;
        });
        var orphanPath = PlaytimePaths.Journal(fixture.GameRoot, orphanJournalSession);
        Directory.CreateDirectory(Path.GetDirectoryName(orphanPath)!);
        PlaytimeJson.AtomicWrite(orphanPath, PlaytimeJson.SerializeJournal(new PlaytimeSessionJournal
        {
            SessionID = orphanJournalSession,
            InstanceID = fixture.InstanceID,
            OwnerKey = "guest"
        }));

        await fixture.Coordinator.RefreshAsync();

        var ledger = await fixture.ReadLedgerAsync();
        Assert.IsFalse(ledger.Sessions.ContainsKey(missingJournalSession));
        Assert.IsFalse(File.Exists(orphanPath));
    }

    [TestMethod]
    public async Task TwoAppServicesSharingGameRootCreditOnlyOnce()
    {
        await using var fixture = new PlaytimeFixture();
        await fixture.StartSessionAsync("guest");
        await using var second = fixture.CreateCoordinator();
        fixture.Clock.AdvanceSeconds(13);

        await fixture.Coordinator.RefreshAsync();
        await second.RefreshAsync();

        Assert.AreEqual(13, fixture.Coordinator.TotalSeconds(null), 0.001);
        Assert.AreEqual(13, second.TotalSeconds(null), 0.001);
    }

    [TestMethod]
    public async Task FailedHelperStartRetriesWhileLauncherIsOpen()
    {
        var timeProvider = new SignalingTimeProvider();
        await using var fixture = new PlaytimeFixture(timeProvider: timeProvider);
        await fixture.StartSessionAsync("guest");
        Assert.AreEqual(1, fixture.Launcher.Attempts);
        Assert.AreEqual("PlaytimeHelperStartFailed", fixture.Coordinator.ErrorResourceKey);

        fixture.Clock.AdvanceSeconds(12);
        timeProvider.Advance(PlaytimeCoordinator.CheckpointInterval);
        await fixture.Coordinator.RefreshAsync();

        Assert.IsTrue(fixture.Launcher.Attempts >= 2);
        Assert.AreEqual(12, fixture.Coordinator.TotalSeconds(null), 0.001);
    }

    [TestMethod]
    public async Task PeriodicReconcileUsesTenSecondTimerAndUnbiasedClock()
    {
        await using var fixture = new PlaytimeFixture();
        await fixture.StartSessionAsync("guest");
        var timerProvider = new SignalingTimeProvider();
        await using var polling = new PlaytimeCoordinator(fixture.GameRoot, fixture.Launcher,
            fixture.Clock, fixture.Probe, timerProvider);
        await polling.ResumeSessionAsync((await fixture.ReadLedgerAsync()).Sessions.Values
            .Select(session => new GameProcessRecord(session.InstanceID, 151, 100, 0, session.SessionID)).Single(), null);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        polling.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PlaytimeCoordinator.Revision)) changed.TrySetResult();
        };
        polling.Start();
        await timerProvider.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.AdvanceSeconds(10);
        timerProvider.Advance(TimeSpan.FromSeconds(10));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(10, polling.TotalSeconds(null), 0.001);
    }

    [TestMethod]
    public async Task GuestTransferCapturesBoundaryAndLeavesLaterGuestTimeBehind()
    {
        await using var fixture = new PlaytimeFixture();
        await fixture.StartSessionAsync("guest", pid: 151);
        fixture.Clock.AdvanceSeconds(25);
        await fixture.Coordinator.RefreshAsync();
        fixture.Probe.SetRunning(new PlaytimeProcessIdentity(151, 100, 0), false);
        await fixture.Coordinator.RefreshAsync();

        var (ongoingSession, _) = await fixture.StartSessionAsync("guest", pid: 152);
        fixture.Clock.AdvanceSeconds(5);
        var transferID = await fixture.Coordinator.PrepareGuestTransferAsync("target-xuid");
        Assert.IsNotNull(transferID);
        var pending = (await fixture.ReadLedgerAsync()).PendingTransfer;
        Assert.IsNotNull(pending);
        Assert.AreEqual(30, pending.GuestSecondsAtBoundary, 0.001);
        CollectionAssert.Contains(pending.SessionIDs, ongoingSession);

        fixture.Clock.AdvanceSeconds(7);
        var (postBoundarySession, _) = await fixture.StartSessionAsync("guest", pid: 153,
            instanceID: Guid.NewGuid());
        fixture.Clock.AdvanceSeconds(3);
        await fixture.Coordinator.RefreshAsync();
        await fixture.Coordinator.CompleteGuestTransferAsync(transferID!.Value);
        await fixture.Coordinator.RefreshAsync();

        Assert.AreEqual(40, fixture.Coordinator.TotalSeconds("target-xuid"), 0.001);
        Assert.AreEqual(3, fixture.Coordinator.TotalSeconds(null), 0.001);
        var ledger = await fixture.ReadLedgerAsync();
        Assert.AreEqual("account:target-xuid", ledger.Sessions[ongoingSession].OwnerKey);
        Assert.AreEqual("guest", ledger.Sessions[postBoundarySession].OwnerKey);
        Assert.IsNull(ledger.PendingTransfer);
        Assert.IsTrue(ledger.AppliedTransfers.Contains(transferID.Value));
    }

    [TestMethod]
    public async Task GuestTransferDoesNotWaitForTheRunningHelpersJournalLock()
    {
        await using var fixture = new PlaytimeFixture();
        var (sessionID, _) = await fixture.StartSessionAsync("guest", pid: 152);
        using var helperJournalLock = PlaytimeJournalLock.TryAcquire(PlaytimePaths.JournalLock(fixture.GameRoot, sessionID));
        Assert.IsNotNull(helperJournalLock);
        fixture.Clock.AdvanceSeconds(5);

        var transferID = await fixture.Coordinator.PrepareGuestTransferAsync("target-xuid")
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsNotNull(transferID);
        var pending = (await fixture.ReadLedgerAsync()).PendingTransfer;
        Assert.IsNotNull(pending);
        Assert.AreEqual(5, pending.GuestSecondsAtBoundary, 0.001);
        CollectionAssert.Contains(pending.SessionIDs, sessionID);
        await fixture.Coordinator.CompleteGuestTransferAsync(transferID!.Value);
        Assert.AreEqual("account:target-xuid", (await fixture.ReadLedgerAsync()).Sessions[sessionID].OwnerKey);
    }

    [TestMethod]
    public async Task TransferPendingAfterFirstPhaseRecoversAgainstSavedAccount()
    {
        await using var fixture = new PlaytimeFixture(checkpoint =>
        {
            if (checkpoint == PlaytimeTransferCheckpoint.PendingWritten) throw new IOException("fail after phase A");
        });
        await fixture.StartSessionAsync("guest");
        fixture.Clock.AdvanceSeconds(8);
        await fixture.Coordinator.RefreshAsync();

        var result = await fixture.Coordinator.PrepareGuestTransferAsync("target");

        Assert.IsNull(result);
        Assert.IsNotNull((await fixture.ReadLedgerAsync()).PendingTransfer);
        await fixture.Coordinator.RecoverPendingTransferAsync("target");
        var ledger = await fixture.ReadLedgerAsync();
        Assert.IsNull(ledger.PendingTransfer);
        Assert.IsTrue(ledger.AppliedTransfers.Count == 1);
        Assert.AreEqual(8, fixture.Coordinator.TotalSeconds("target"), 0.001);
    }

    [TestMethod]
    public async Task PendingTransferAfterAccountSaveAppliesOnStartup()
    {
        await using var fixture = new PlaytimeFixture(checkpoint =>
        {
            if (checkpoint == PlaytimeTransferCheckpoint.BeforeApply) throw new IOException("fail after phase B");
        });
        await fixture.StartSessionAsync("guest");
        fixture.Clock.AdvanceSeconds(12);
        await fixture.Coordinator.RefreshAsync();
        var transferID = await fixture.Coordinator.PrepareGuestTransferAsync("saved-xuid");
        Assert.IsNotNull(transferID);
        await fixture.Coordinator.CompleteGuestTransferAsync(transferID!.Value);

        await using var relaunched = fixture.CreateCoordinator();
        await relaunched.RecoverPendingTransferAsync("saved-xuid");

        Assert.AreEqual(12, relaunched.TotalSeconds("saved-xuid"), 0.001);
        Assert.AreEqual(0, relaunched.TotalSeconds(null), 0.001);
        var ledger = await fixture.ReadLedgerAsync();
        Assert.IsNull(ledger.PendingTransfer);
        Assert.IsTrue(ledger.AppliedTransfers.Contains(transferID.Value));
    }

    [TestMethod]
    public async Task FailedSecondPhaseLeavesPendingForRecoveryAndWrongAccountDiscardsIt()
    {
        await using var fixture = new PlaytimeFixture(checkpoint =>
        {
            if (checkpoint == PlaytimeTransferCheckpoint.BeforeApply) throw new IOException("fail before phase C");
        });
        await fixture.StartSessionAsync("guest");
        fixture.Clock.AdvanceSeconds(9);
        await fixture.Coordinator.RefreshAsync();
        var transferID = await fixture.Coordinator.PrepareGuestTransferAsync("target");
        Assert.IsNotNull(transferID);

        await fixture.Coordinator.CompleteGuestTransferAsync(transferID!.Value);
        Assert.AreEqual(transferID, (await fixture.ReadLedgerAsync()).PendingTransfer?.ID);
        await fixture.Coordinator.RecoverPendingTransferAsync("different-account");

        var ledger = await fixture.ReadLedgerAsync();
        Assert.IsNull(ledger.PendingTransfer);
        Assert.AreEqual(9, fixture.Coordinator.TotalSeconds(null), 0.001);
        Assert.AreEqual(0, fixture.Coordinator.TotalSeconds("target"), 0.001);
    }

    [TestMethod]
    public async Task TransferAppliedTwiceDoesNotDuplicateCredit()
    {
        await using var fixture = new PlaytimeFixture(checkpoint =>
        {
            if (checkpoint == PlaytimeTransferCheckpoint.Applied) throw new IOException("fail after atomic phase C");
        });
        await fixture.StartSessionAsync("guest");
        fixture.Clock.AdvanceSeconds(11);
        await fixture.Coordinator.RefreshAsync();
        var transferID = await fixture.Coordinator.PrepareGuestTransferAsync("target");
        Assert.IsNotNull(transferID);

        await fixture.Coordinator.CompleteGuestTransferAsync(transferID!.Value);
        await fixture.Coordinator.CompleteGuestTransferAsync(transferID.Value);

        var ledger = await fixture.ReadLedgerAsync();
        Assert.AreEqual(11, fixture.Coordinator.TotalSeconds("target"), 0.001);
        Assert.AreEqual(0, fixture.Coordinator.TotalSeconds(null), 0.001);
        Assert.AreEqual(1, ledger.AppliedTransfers.Count);
    }

    [TestMethod]
    public async Task AccountSaveFailureCancelsPreparedGuestTransfer()
    {
        var accounts = new MemoryAccountStore { FailSave = true };
        var transfer = new RecordingTransfer();
        var coordinator = new AppCoordinator(new MemorySettingsStore(), accounts, new SequenceAuthService(), playtimeTransfer: transfer);
        await coordinator.InitializeAsync();

        await coordinator.BeginSignIn();

        CollectionAssert.AreEqual(new[] { "prepare", "cancel" }, transfer.Events.ToArray());
        Assert.IsNull(accounts.SavedAccount);
    }

    [TestMethod]
    public async Task StaleSignInCancelsPendingTransferBeforeItCanCommit()
    {
        var accounts = new PausingAccountStore();
        var transfer = new RecordingTransfer();
        var coordinator = new AppCoordinator(new MemorySettingsStore(), accounts, new SequenceAuthService(), playtimeTransfer: transfer);
        await coordinator.InitializeAsync();
        var attempt = coordinator.BeginSignIn();
        await accounts.FirstSaveReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        coordinator.BackFromSignIn();
        accounts.ReleaseFirstSave.TrySetResult();
        await attempt.WaitAsync(TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(new[] { "prepare", "cancel" }, transfer.Events.ToArray());
        Assert.IsNull(accounts.SavedAccount);
        Assert.IsNull(coordinator.Account);
    }

    [TestMethod]
    public async Task LogoutWaitsUntilCommittedAccountTransferFinishes()
    {
        var accounts = new MemoryAccountStore();
        var transfer = new RecordingTransfer { PauseCompletion = true };
        var coordinator = new AppCoordinator(new MemorySettingsStore(), accounts, new SequenceAuthService(), playtimeTransfer: transfer);
        await coordinator.InitializeAsync();
        var signIn = coordinator.BeginSignIn();
        await transfer.CompleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var logout = coordinator.LogoutAsync();
        Assert.IsFalse(logout.IsCompleted);
        transfer.ReleaseCompletion.TrySetResult();
        await Task.WhenAll(signIn, logout).WaitAsync(TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(new[] { "prepare", "complete" }, transfer.Events.ToArray());
        Assert.IsNull(accounts.SavedAccount);
        Assert.IsNull(coordinator.Account);
    }

    [TestMethod]
    public async Task HelperKeepsCheckpointingAfterCoordinatorDisposalAndRecoveryCreditsIt()
    {
        await using var fixture = new PlaytimeFixture();
        var (sessionID, process) = await fixture.StartSessionAsync("guest", pid: 154);
        var helperIdentity = new PlaytimeProcessIdentity(900, 500, 0);
        var helperProbe = new ControlledHelperProbe(process, helperIdentity);
        var journalPath = PlaytimePaths.Journal(fixture.GameRoot, sessionID);
        var host = Task.Run(() => PlaytimeHelperHost.Run([journalPath], fixture.Clock, helperProbe,
            TimeSpan.FromMilliseconds(1)));

        await helperProbe.FirstWaitReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.AdvanceSeconds(30);
        helperProbe.ContinueFirst.TrySetResult();
        await helperProbe.SecondWaitReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var checkpoint = PlaytimeJson.ReadJournal(journalPath);
        Assert.AreEqual(30, checkpoint.ElapsedSeconds, 0.001);
        await fixture.Coordinator.DisposeAsync();

        fixture.Clock.AdvanceSleep(TimeSpan.FromHours(2));
        fixture.Clock.JumpWallClock(TimeSpan.FromDays(1));
        fixture.Clock.AdvanceSeconds(10);
        helperProbe.GameExited = true;
        helperProbe.ContinueSecond.TrySetResult();
        Assert.AreEqual(0, await host.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(PlaytimeJson.ReadJournal(journalPath).IsFinished);

        await using var relaunched = fixture.CreateCoordinator(helperLauncher: null);
        await relaunched.ReconcileAsync();
        Assert.AreEqual(40, relaunched.TotalSeconds(null), 0.001);
        Assert.IsFalse(File.Exists(journalPath));
    }

    [TestMethod]
    public void HelperPathMustBeARealSessionJournalInsidePlaytimeDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var id = Guid.NewGuid();
        var validPath = PlaytimePaths.Journal(temporary.Paths.GameRoot, id);
        Directory.CreateDirectory(Path.GetDirectoryName(validPath)!);
        File.WriteAllText(validPath, "{}");

        Assert.IsTrue(PlaytimePaths.TryValidateHelperPath(validPath, out var fullPath, out var sessionID));
        Assert.AreEqual(Path.GetFullPath(validPath), fullPath);
        Assert.AreEqual(id, sessionID);
        Assert.IsFalse(PlaytimePaths.TryValidateHelperPath(Path.Combine(temporary.Root, "outside", $"{id:D}.json"), out _, out _));
        Assert.IsFalse(PlaytimePaths.TryValidateHelperPath("relative.json", out _, out _));
    }

    [TestMethod]
    public async Task PreviewPlaytimeUsesFixturesWithoutCreatingItsStore()
    {
        using var temporary = new TemporaryDirectory();
        var preview = PlaytimeCoordinator.CreatePreview(temporary.Paths.GameRoot);
        var instanceID = Guid.NewGuid();
        preview.SeedPreviewInstance(instanceID);
        await preview.InitializeAsync("preview-xuid");
        preview.Start();

        Assert.AreEqual(12_345, preview.TotalSeconds("preview-xuid"), 0.001);
        Assert.AreEqual(4_321, preview.TotalSeconds(null), 0.001);
        Assert.AreEqual(3_726, preview.InstanceSeconds(instanceID, "preview-xuid"), 0.001);
        Assert.IsFalse(Directory.Exists(PlaytimePaths.Root(temporary.Paths.GameRoot)));
        await preview.DisposeAsync();
    }

    [TestMethod]
    public void LedgerLivesUnderGameRootWhileAccountDataRemainsSeparate()
    {
        using var temporary = new TemporaryDirectory();
        var ledgerPath = PlaytimePaths.Ledger(temporary.Paths.GameRoot);

        Assert.IsTrue(ledgerPath.StartsWith(PlaytimePaths.Root(temporary.Paths.GameRoot), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(ledgerPath.StartsWith(temporary.Paths.DataRoot, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void FormatterUsesLocalizedPluralKeysAndDayUnits()
    {
        string Localize(string key) => key switch
        {
            "PlaytimeMinuteOne" => "{0} minute",
            "PlaytimeMinuteFew" => "{0} minutes-few",
            "PlaytimeMinuteMany" => "{0} minutes",
            "PlaytimeMinuteOther" => "{0} minutes",
            "PlaytimeHourOne" => "{0} hour",
            "PlaytimeHourFew" => "{0} hours-few",
            "PlaytimeHourMany" => "{0} hours",
            "PlaytimeHourOther" => "{0} hours",
            "PlaytimeDayOne" => "{0} day",
            "PlaytimeDayFew" => "{0} days-few",
            "PlaytimeDayMany" => "{0} days",
            "PlaytimeDayOther" => "{0} days",
            "PlaytimeZero" => "zero",
            "PlaytimeLessThanMinute" => "under minute",
            _ => key
        };

        Assert.AreEqual("1 minute", PlaytimeFormatter.Format(60, AppLanguage.English, Localize));
        Assert.AreEqual("2 minutes-few", PlaytimeFormatter.Format(120, AppLanguage.Russian, Localize));
        Assert.AreEqual("5 hours", PlaytimeFormatter.Format(18_000, AppLanguage.Russian, Localize));
        Assert.AreEqual("2 days-few", PlaytimeFormatter.Format(172_800, AppLanguage.Russian, Localize));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task FinishedHelperJournalSurvivesPendingTransferAndCleanupRetry(bool failApply, bool failCleanup)
    {
        await using var fixture = new PlaytimeFixture();
        var (sessionID, process) = await fixture.StartSessionAsync("guest");
        var helperIdentity = new PlaytimeProcessIdentity(900, 500, 0);
        fixture.Probe.SetRunning(helperIdentity, true);
        var helperProbe = new ControlledHelperProbe(process, helperIdentity);
        var path = PlaytimePaths.Journal(fixture.GameRoot, sessionID);
        var host = Task.Run(() => PlaytimeHelperHost.Run([path], fixture.Clock, helperProbe));
        await helperProbe.FirstWaitReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.AdvanceSeconds(25);
        var transferID = await fixture.Coordinator.PrepareGuestTransferAsync("target");
        Assert.IsNotNull(transferID);
        Assert.AreEqual(25, (await fixture.ReadLedgerAsync()).PendingTransfer!.GuestSecondsAtBoundary, 0.001);
        var (laterSession, _) = await fixture.StartSessionAsync("guest", 152, Guid.NewGuid());
        fixture.Clock.AdvanceSeconds(7);
        helperProbe.ContinueFirst.TrySetResult();
        await helperProbe.SecondWaitReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.AdvanceSeconds(3);
        helperProbe.GameExited = true;
        helperProbe.ContinueSecond.TrySetResult();
        Assert.AreEqual(0, await host.WaitAsync(TimeSpan.FromSeconds(5)));
        fixture.Probe.SetRunning(helperIdentity, false);
        fixture.Probe.SetRunning(new PlaytimeProcessIdentity(151, 100, 0), false);
        await fixture.Coordinator.ReconcileAsync();
        Assert.IsTrue(File.Exists(path), "Pending transfer must retain the final checkpoint.");
        Assert.AreEqual(35, PlaytimeJson.ReadJournal(path).ElapsedSeconds, 0.001);
        using (var preventApply = failApply
            ? new FileStream(PlaytimePaths.Ledger(fixture.GameRoot), FileMode.Open, FileAccess.Read, FileShare.Read)
            : null)
            await fixture.Coordinator.CompleteGuestTransferAsync(transferID.Value);
        if (failApply)
        {
            await fixture.Coordinator.ReconcileAsync();
            Assert.IsTrue(File.Exists(path));
            Assert.IsNotNull((await fixture.ReadLedgerAsync()).PendingTransfer);
        }
        await using var startup = fixture.CreateCoordinator();
        using (var preventCleanup = failCleanup ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
        {
            await startup.InitializeAsync("target");
            Assert.AreEqual(35, startup.TotalSeconds("target"), 0.001);
            Assert.AreEqual(35, startup.InstanceSeconds(fixture.InstanceID, "target"), 0.001);
            Assert.AreEqual(10, startup.TotalSeconds(null), 0.001);
            var ledger = await fixture.ReadLedgerAsync();
            Assert.IsFalse(ledger.Sessions.ContainsKey(sessionID));
            Assert.AreEqual("guest", ledger.Sessions[laterSession].OwnerKey);
            Assert.AreEqual(1, ledger.AppliedTransfers.Count);
            if (failCleanup) Assert.IsTrue(File.Exists(path));
        }
        await using var repeatedStartup = fixture.CreateCoordinator();
        await repeatedStartup.InitializeAsync("target");
        await repeatedStartup.CompleteGuestTransferAsync(transferID.Value);
        await repeatedStartup.ReconcileAsync();
        Assert.AreEqual(35, repeatedStartup.TotalSeconds("target"), 0.001);
        Assert.AreEqual(10, repeatedStartup.TotalSeconds(null), 0.001);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    [DataRow(true, "complete", true, 7d)]
    [DataRow(false, "complete", true, 7d)]
    [DataRow(true, "recover", true, 7d)]
    [DataRow(false, "recover", true, 7d)]
    [DataRow(true, "cancel", true, 7d)]
    [DataRow(false, "discard", true, 7d)]
    [DataRow(true, "complete", false, 7d)]
    [DataRow(false, "complete", false, 7d)]
    [DataRow(true, "complete", true, 0d)]
    [DataRow(false, "complete", true, 0d)]
    public async Task DeadGameBeforeTransferRetainsUnfinishedCheckpointUntilCredit(
        bool finalBeforeResolution, string resolution, bool helperPublished, double checkpoint)
    {
        await using var fixture = new PlaytimeFixture();
        var (sessionID, _) = await fixture.StartSessionAsync("guest");
        var path = PlaytimePaths.Journal(fixture.GameRoot, sessionID);
        var journal = PlaytimeJson.ReadJournal(path);
        var helper = new PlaytimeProcessIdentity(900, 500, 0);
        journal.Helper = helperPublished ? helper : null;
        journal.ElapsedSeconds = checkpoint;
        PlaytimeJson.AtomicWrite(path, PlaytimeJson.SerializeJournal(journal));
        fixture.Probe.SetRunning(helper, true);
        fixture.Probe.SetRunning(new PlaytimeProcessIdentity(151, 100, 0), false);
        fixture.Clock.AdvanceSeconds(25);

        Guid transferID;
        Guid laterSession;
        using (var helperLock = PlaytimeJournalLock.TryAcquire(PlaytimePaths.JournalLock(fixture.GameRoot, sessionID)))
        {
            Assert.IsNotNull(helperLock);
            // The helper observed exit at 19s but has not yet published its final checkpoint.
            var prepared = await fixture.Coordinator.PrepareGuestTransferAsync("target")
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(prepared);
            transferID = prepared.Value;
            var pendingLedger = await fixture.ReadLedgerAsync();
            Assert.IsTrue(pendingLedger.Sessions.ContainsKey(sessionID), "Phase A must retain the unfinished session.");
            CollectionAssert.Contains(pendingLedger.PendingTransfer!.SessionIDs, sessionID);
            Assert.AreEqual(checkpoint, pendingLedger.PendingTransfer.GuestSecondsAtBoundary, 0.001);
            (laterSession, _) = await fixture.StartSessionAsync("guest", 152, Guid.NewGuid());
            fixture.Clock.AdvanceSeconds(3);

            if (finalBeforeResolution) PublishFinal();
            await fixture.Coordinator.ReconcileAsync();
            Assert.IsTrue(File.Exists(path));
            Assert.IsTrue((await fixture.ReadLedgerAsync()).Sessions.ContainsKey(sessionID));

            if (resolution == "complete")
                await fixture.Coordinator.CompleteGuestTransferAsync(transferID);
            else if (resolution == "cancel")
                await fixture.Coordinator.CancelGuestTransferAsync(transferID);
            else
            {
                await using var recovered = fixture.CreateCoordinator();
                await recovered.RecoverPendingTransferAsync(resolution == "recover" ? "target" : "different");
            }

            if (!finalBeforeResolution)
            {
                await fixture.Coordinator.ReconcileAsync();
                Assert.IsTrue(File.Exists(path));
                Assert.IsTrue((await fixture.ReadLedgerAsync()).Sessions.ContainsKey(sessionID));
                PublishFinal();
            }
        }

        fixture.Probe.SetRunning(helper, false);
        await using var startup = fixture.CreateCoordinator();
        await startup.InitializeAsync("target");
        await startup.ReconcileAsync();
        var applied = resolution is "complete" or "recover";
        Assert.AreEqual(applied ? 19 : 0, startup.TotalSeconds("target"), 0.001);
        Assert.AreEqual(applied ? 19 : 0, startup.InstanceSeconds(fixture.InstanceID, "target"), 0.001);
        Assert.AreEqual(applied ? 3 : 22, startup.TotalSeconds(null), 0.001);
        var ledger = await fixture.ReadLedgerAsync();
        Assert.IsNull(ledger.PendingTransfer);
        Assert.AreEqual(applied ? 1 : 0, ledger.AppliedTransfers.Count);
        Assert.IsFalse(ledger.Sessions.ContainsKey(sessionID));
        Assert.AreEqual("guest", ledger.Sessions[laterSession].OwnerKey);
        Assert.IsFalse(File.Exists(path));
        Assert.IsFalse(File.Exists(PlaytimePaths.JournalLock(fixture.GameRoot, sessionID)));

        void PublishFinal()
        {
            journal.ElapsedSeconds = 19;
            journal.IsFinished = true;
            PlaytimeJson.AtomicWrite(path, PlaytimeJson.SerializeJournal(journal));
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CancelAttachedSessionCreditsFinalCheckpointBeforeCleanup(bool finalBeforeCancel)
    {
        await using var fixture = new PlaytimeFixture();
        var (sessionID, _) = await fixture.StartSessionAsync("guest");
        var path = PlaytimePaths.Journal(fixture.GameRoot, sessionID);
        var journal = PlaytimeJson.ReadJournal(path);
        var helper = new PlaytimeProcessIdentity(900, 500, 0);
        fixture.Probe.SetRunning(helper, true);
        journal.Helper = helper;
        journal.ElapsedSeconds = finalBeforeCancel ? 19 : 7;
        journal.IsFinished = finalBeforeCancel;
        PlaytimeJson.AtomicWrite(path, PlaytimeJson.SerializeJournal(journal));
        fixture.Probe.SetRunning(new PlaytimeProcessIdentity(151, 100, 0), false);

        using (var helperLock = PlaytimeJournalLock.TryAcquire(PlaytimePaths.JournalLock(fixture.GameRoot, sessionID)))
        {
            Assert.IsNotNull(helperLock);
            await fixture.Coordinator.CancelSessionAsync(sessionID).WaitAsync(TimeSpan.FromSeconds(5));
            if (!finalBeforeCancel)
            {
                Assert.IsTrue((await fixture.ReadLedgerAsync()).Sessions.ContainsKey(sessionID));
                Assert.IsTrue(File.Exists(path));
                journal.ElapsedSeconds = 19;
                journal.IsFinished = true;
                PlaytimeJson.AtomicWrite(path, PlaytimeJson.SerializeJournal(journal));
            }
        }
        fixture.Probe.SetRunning(helper, false);
        await fixture.Coordinator.ReconcileAsync();

        Assert.AreEqual(19, fixture.Coordinator.TotalSeconds(null), 0.001);
        Assert.IsFalse((await fixture.ReadLedgerAsync()).Sessions.ContainsKey(sessionID));
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    [DataRow(false, 0d)]
    [DataRow(false, 17d)]
    [DataRow(true, 17d)]
    public async Task LateHelperCannotCreditDeadOrReusedProcess(bool reusedPid, double checkpoint)
    {
        await using var fixture = new PlaytimeFixture();
        var (sessionID, _) = await fixture.StartSessionAsync("guest");
        var path = PlaytimePaths.Journal(fixture.GameRoot, sessionID);
        var journal = PlaytimeJson.ReadJournal(path);
        journal.BaseElapsedSeconds = Math.Min(5, checkpoint);
        journal.ElapsedSeconds = checkpoint;
        PlaytimeJson.AtomicWrite(path, PlaytimeJson.SerializeJournal(journal));
        fixture.Probe.SetRunning(new PlaytimeProcessIdentity(151, 100, 0), false);
        if (reusedPid) fixture.Probe.SetRunning(new PlaytimeProcessIdentity(151, 101, 0), true);
        fixture.Clock.AdvanceSeconds(3_600);
        fixture.Clock.AdvanceSleep(TimeSpan.FromHours(2));
        fixture.Clock.JumpWallClock(TimeSpan.FromDays(-1));
        Assert.AreEqual(0, PlaytimeHelperHost.Run([path], fixture.Clock, fixture.Probe));
        var finished = PlaytimeJson.ReadJournal(path);
        Assert.IsTrue(finished.IsFinished);
        Assert.AreEqual(checkpoint, finished.ElapsedSeconds, 0.001);
        Assert.AreEqual(Math.Min(5, checkpoint), finished.BaseElapsedSeconds, 0.001);
    }

    [TestMethod]
    [DataRow("playtime")]
    [DataRow("parent")]
    [DataRow("root")]
    [DataRow("ledger")]
    [DataRow("lock")]
    public async Task LedgerRecoveryRejectsReparseChainWithoutTouchingExternalData(string location)
    {
        using var temporary = new TemporaryDirectory();
        var external = Path.Combine(temporary.Root, "External");
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "keep.txt");
        File.WriteAllText(sentinel, "external bytes");
        var gameRoot = temporary.Paths.GameRoot;
        string link;
        if (location == "parent")
        {
            Directory.CreateDirectory(Path.Combine(external, "Game"));
            link = Path.Combine(temporary.Root, "Parent");
            gameRoot = Path.Combine(link, "Game");
        }
        else if (location == "root") link = gameRoot;
        else
        {
            Directory.CreateDirectory(gameRoot);
            link = location == "playtime" ? PlaytimePaths.Root(gameRoot)
                : Path.Combine(PlaytimePaths.Root(gameRoot), location == "ledger" ? "playtime.json" : "playtime.lock");
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        }
        var command = "$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path '"
            + link.Replace("'", "''") + "' -Target '" + external.Replace("'", "''") + "' | Out-Null";
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command }
        })!;
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd());
        try
        {
            var store = new PlaytimeLedgerStore(gameRoot);
            await Assert.ThrowsExactlyAsync<IOException>(() => store.ReadAsync());
            await Assert.ThrowsExactlyAsync<IOException>(() => store.MutateAsync(ledger =>
            {
                ledger.Owners["guest"] = new PlaytimeOwnerRecord { TotalSeconds = 99 };
                return true;
            }));
            await using var coordinator = new PlaytimeCoordinator(gameRoot);
            await coordinator.InitializeAsync(null);
            Assert.AreEqual("PlaytimeStorageError", coordinator.ErrorResourceKey);
            Assert.AreEqual("external bytes", File.ReadAllText(sentinel));
            CollectionAssert.AreEquivalent(new[] { "keep.txt" }, Directory.GetFiles(external, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName).ToArray());
            Assert.AreEqual(0, Directory.GetFiles(temporary.Root, "*.tmp", SearchOption.AllDirectories).Length);
            Assert.AreEqual(0, Directory.GetFiles(temporary.Root, "*.lock", SearchOption.AllDirectories).Length);
        }
        finally { Directory.Delete(link); }
    }

    [TestMethod]
    [DataRow("playtime.json")]
    [DataRow("playtime.lock")]
    public async Task LedgerRejectsFileSymlinksBeforeReadingWritingOrCreatingLock(string filename)
    {
        using var temporary = new TemporaryDirectory();
        var directory = PlaytimePaths.Root(temporary.Paths.GameRoot);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(temporary.Root, "external.json");
        File.WriteAllText(target, "external bytes");
        var link = Path.Combine(directory, filename);
        try { File.CreateSymbolicLink(link, target); }
        catch (UnauthorizedAccessException) { Assert.Inconclusive("File symlinks require Windows developer mode or privilege."); }
        try
        {
            var store = new PlaytimeLedgerStore(temporary.Paths.GameRoot);
            await Assert.ThrowsExactlyAsync<IOException>(() => store.ReadAsync());
            await Assert.ThrowsExactlyAsync<IOException>(() => store.MutateAsync(_ => true));
            Assert.ThrowsExactly<IOException>(() => PlaytimeJson.AtomicWrite(link, new byte[] { 1, 2, 3 }));
            Assert.AreEqual("external bytes", File.ReadAllText(target));
            Assert.IsTrue((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            CollectionAssert.AreEquivalent(new[] { filename }, Directory.GetFiles(directory).Select(Path.GetFileName).ToArray());
        }
        finally { File.Delete(link); }
    }

    [TestMethod]
    public async Task LedgerRejectsMissingGameRootButCreatesItsOwnDirectory()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Root);
        var store = new PlaytimeLedgerStore(temporary.Paths.GameRoot);
        await Assert.ThrowsAsync<IOException>(() => store.ReadAsync());
        await Assert.ThrowsAsync<IOException>(() => store.MutateAsync(_ => true));
        Assert.IsFalse(Directory.Exists(temporary.Paths.GameRoot));
        Directory.CreateDirectory(temporary.Paths.GameRoot);
        Assert.AreEqual(0, (await store.ReadAsync()).Owners.Count);
        await store.MutateAsync(_ => true);
        Assert.IsTrue(File.Exists(PlaytimePaths.Ledger(temporary.Paths.GameRoot)));
    }

    private sealed class PlaytimeFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _temporary = new();
        private readonly List<PlaytimeCoordinator> _coordinators = [];
        private readonly TimeProvider _timeProvider;

        public string GameRoot => _temporary.Paths.GameRoot;
        public Guid InstanceID { get; } = Guid.NewGuid();
        public FakePlaytimeClock Clock { get; } = new();
        public FakeProcessProbe Probe { get; } = new();
        public FakeHelperLauncher Launcher { get; } = new();
        public PlaytimeCoordinator Coordinator { get; }

        public PlaytimeFixture(Action<PlaytimeTransferCheckpoint>? checkpoint = null, TimeProvider? timeProvider = null)
        {
            Directory.CreateDirectory(GameRoot);
            _timeProvider = timeProvider ?? TimeProvider.System;
            Coordinator = CreateCoordinator(checkpoint: checkpoint);
        }

        public PlaytimeCoordinator CreateCoordinator(IPlaytimeHelperLauncher? helperLauncher = null,
            Action<PlaytimeTransferCheckpoint>? checkpoint = null) =>
            Track(new PlaytimeCoordinator(GameRoot, helperLauncher ?? Launcher, Clock, Probe, _timeProvider,
                preview: false, transferCheckpoint: checkpoint));

        public async Task<(Guid SessionID, GameProcessRecord Process)> StartSessionAsync(string owner,
            int pid = 151, Guid? instanceID = null)
        {
            var instance = instanceID ?? InstanceID;
            var tracking = await Coordinator.BeginSessionAsync(instance, owner == "guest" ? null : owner["account:".Length..]);
            Assert.IsNotNull(tracking);
            var process = new GameProcessRecord(instance, pid, 100, 0, tracking.SessionID);
            Probe.SetRunning(new PlaytimeProcessIdentity(pid, 100, 0), true);
            await Coordinator.AttachProcessAsync(tracking, process);
            return (tracking.SessionID, process);
        }

        public async Task<PlaytimeLedger> ReadLedgerAsync() => await new PlaytimeLedgerStore(GameRoot).ReadAsync();

        private PlaytimeCoordinator Track(PlaytimeCoordinator coordinator)
        {
            _coordinators.Add(coordinator);
            return coordinator;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var coordinator in _coordinators.AsEnumerable().Reverse()) await coordinator.DisposeAsync();
            await _temporary.DisposeAsync();
        }
    }

    private sealed class FakePlaytimeClock(ulong initial = 0) : IPlaytimeClock
    {
        private long _ticks = checked((long)initial);
        private TimeSpan _wallClockElapsed;
        public ulong GetUnbiasedTicks() => checked((ulong)Interlocked.Read(ref _ticks));
        public TimeSpan WallClockElapsed => _wallClockElapsed;
        public void AdvanceSeconds(double seconds) => Interlocked.Add(ref _ticks, checked((long)(seconds * 10_000_000)));
        public void AdvanceSleep(TimeSpan duration) => _wallClockElapsed += duration;
        public void JumpWallClock(TimeSpan duration) => _wallClockElapsed += duration;
    }

    private class FakeProcessProbe : IPlaytimeProcessProbe
    {
        private readonly object _sync = new();
        private readonly Dictionary<PlaytimeProcessIdentity, bool> _running = [];
        private PlaytimeProcessIdentity? _current = new(900, 500, 0);

        public virtual PlaytimeProcessIdentity? ReadCurrentIdentity() => _current;
        public void SetCurrent(PlaytimeProcessIdentity? identity) => _current = identity;

        public void SetRunning(PlaytimeProcessIdentity identity, bool running)
        {
            lock (_sync) _running[identity] = running;
        }

        public virtual bool IsRunning(PlaytimeProcessIdentity identity)
        {
            lock (_sync) return _running.GetValueOrDefault(identity);
        }

        public virtual bool WaitForExitOrTimeout(PlaytimeProcessIdentity identity, TimeSpan timeout) => !IsRunning(identity);
    }

    private sealed class ControlledHelperProbe(GameProcessRecord process, PlaytimeProcessIdentity helper)
        : FakeProcessProbe
    {
        private int _waitNumber;
        public TaskCompletionSource FirstWaitReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondWaitReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool GameExited { get; set; }

        public override PlaytimeProcessIdentity? ReadCurrentIdentity() => helper;

        public override bool IsRunning(PlaytimeProcessIdentity identity)
        {
            if (identity.Pid == process.Pid) return !GameExited;
            return identity == helper;
        }

        public override bool WaitForExitOrTimeout(PlaytimeProcessIdentity identity, TimeSpan timeout)
        {
            var number = Interlocked.Increment(ref _waitNumber);
            if (number == 1)
            {
                FirstWaitReached.TrySetResult();
                ContinueFirst.Task.GetAwaiter().GetResult();
                return false;
            }

            SecondWaitReached.TrySetResult();
            ContinueSecond.Task.GetAwaiter().GetResult();
            return true;
        }
    }

    private sealed class FakeHelperLauncher : IPlaytimeHelperLauncher
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);
        public bool Succeeds { get; set; }

        public Task<bool> TryStartAsync(string journalPath, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _attempts);
            return Task.FromResult(Succeeds);
        }
    }

    private sealed class SignalingTimeProvider : TimeProvider
    {
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private ManualTimer? _timer;

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimerCreated.TrySetResult();
            _timer = new ManualTimer(this, callback, state, dueTime, period);
            return _timer;
        }

        public void Advance(TimeSpan amount)
        {
            _now += amount;
            _timer?.Fire();
        }

        private sealed class ManualTimer(SignalingTimeProvider owner, TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period) : ITimer
        {
            private DateTimeOffset _next = owner._now + dueTime;
            private TimeSpan _period = period;
            private bool _disposed;

            public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
            {
                _period = newPeriod;
                _next = owner._now + newDueTime;
                return !_disposed;
            }

            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

            public void Fire()
            {
                if (_disposed || owner._now < _next) return;
                callback(state);
                _next = _period == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : _next + _period;
            }
        }
    }

    private sealed class RecordingTransfer : IAccountPlaytimeTransfer
    {
        private readonly List<string> _events = [];
        public IReadOnlyList<string> Events { get { lock (_events) return _events.ToArray(); } }
        public bool PauseCompletion { get; set; }
        public TaskCompletionSource CompleteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Guid?> PrepareGuestTransferAsync(string targetXuid, CancellationToken cancellationToken = default)
        {
            lock (_events) _events.Add("prepare");
            return Task.FromResult<Guid?>(Guid.NewGuid());
        }

        public async Task CompleteGuestTransferAsync(Guid transferID, CancellationToken cancellationToken = default)
        {
            lock (_events) _events.Add("complete");
            CompleteStarted.TrySetResult();
            if (PauseCompletion) await ReleaseCompletion.Task;
        }

        public Task CancelGuestTransferAsync(Guid transferID, CancellationToken cancellationToken = default)
        {
            lock (_events) _events.Add("cancel");
            return Task.CompletedTask;
        }
    }
}

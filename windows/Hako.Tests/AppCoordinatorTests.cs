using Hako.Core;

namespace Hako.Tests;

[TestClass]
public sealed class AppCoordinatorTests
{
    [TestMethod]
    public async Task SavedAccountRestoresToBuildsWithoutNetwork()
    {
        var saved = TestData.Account();
        var auth = new SequenceAuthService();
        var coordinator = new AppCoordinator(new MemorySettingsStore(), new MemoryAccountStore(saved), auth);

        await coordinator.InitializeAsync();

        Assert.AreEqual(LauncherScreen.Builds, coordinator.Screen);
        Assert.AreEqual(saved, coordinator.Account);
        Assert.AreEqual(0, auth.Attempt);
        Assert.IsFalse(coordinator.IsGuest);
    }

    [TestMethod]
    public async Task GuestDoesNotPersistAndRestartReturnsToWelcome()
    {
        var settings = new MemorySettingsStore();
        var accounts = new MemoryAccountStore();
        var coordinator = new AppCoordinator(settings, accounts, new SequenceAuthService());
        await coordinator.InitializeAsync();
        coordinator.ContinueWithoutAccount();

        Assert.AreEqual(LauncherScreen.Builds, coordinator.Screen);
        Assert.IsTrue(coordinator.IsGuest);
        Assert.IsNull(accounts.SavedAccount);

        var restarted = new AppCoordinator(settings, accounts, new SequenceAuthService());
        await restarted.InitializeAsync();

        Assert.AreEqual(LauncherScreen.Welcome, restarted.Screen);
        Assert.IsFalse(restarted.IsGuest);
        Assert.IsNull(restarted.Account);
    }

    [TestMethod]
    public async Task BackFromSignInReturnsToTheSameBuildAndClosingDeletedBuildOpensBuilds()
    {
        var auth = new SequenceAuthService { HoldTokenWait = true };
        var coordinator = new AppCoordinator(new MemorySettingsStore(), new MemoryAccountStore(), auth);
        await coordinator.InitializeAsync();
        coordinator.ContinueWithoutAccount();
        var id = Guid.NewGuid();
        coordinator.OpenInstance(id);
        var attempt = coordinator.BeginSignIn();
        await auth.TokenWaitStarted.Task;

        coordinator.BackFromSignIn();
        await attempt;

        Assert.AreEqual(LauncherScreen.Instance, coordinator.Screen);
        Assert.AreEqual(id, coordinator.SelectedInstanceId);
        coordinator.CloseInstance(Guid.NewGuid());
        Assert.AreEqual(LauncherScreen.Instance, coordinator.Screen);
        coordinator.CloseInstance(id);
        Assert.AreEqual(LauncherScreen.Builds, coordinator.Screen);
        Assert.IsNull(coordinator.SelectedInstanceId);
    }

    [TestMethod]
    public async Task BackFromGuestProfileReturnsToSameProfileAndLanguageSwitchKeepsLoginAttempt()
    {
        var settings = new MemorySettingsStore();
        var accounts = new MemoryAccountStore();
        var auth = new SequenceAuthService { HoldTokenWait = true };
        var coordinator = new AppCoordinator(settings, accounts, auth);
        await coordinator.InitializeAsync();
        coordinator.ContinueWithoutAccount();
        coordinator.OpenSettings(SettingsSection.Profile);
        var attempt = coordinator.BeginSignIn();
        await auth.TokenWaitStarted.Task;

        await coordinator.SetLanguageAsync(AppLanguage.English);
        Assert.AreEqual(LauncherScreen.Login, coordinator.Screen);
        Assert.AreEqual(SignInPhase.WaitingForCode, coordinator.SignInPhase);
        Assert.AreEqual("ABCD-EFGH", coordinator.CurrentDeviceCode?.UserCode);

        coordinator.BackFromSignIn();
        await attempt;

        Assert.AreEqual(LauncherScreen.Settings, coordinator.Screen);
        Assert.AreEqual(SettingsSection.Profile, coordinator.SelectedSettingsSection);
        Assert.IsTrue(coordinator.IsGuest);
        Assert.AreEqual("en", settings.Settings?.Language);
        Assert.IsNull(accounts.SavedAccount);
    }

    [TestMethod]
    public async Task LogoutKeepsSelectedLanguage()
    {
        var settings = new MemorySettingsStore(new UserSettings("ru"));
        var accounts = new MemoryAccountStore(TestData.Account());
        var coordinator = new AppCoordinator(settings, accounts, new SequenceAuthService());
        await coordinator.InitializeAsync();

        await coordinator.LogoutAsync();

        Assert.AreEqual(LauncherScreen.Welcome, coordinator.Screen);
        Assert.IsNull(coordinator.Account);
        Assert.IsNull(accounts.SavedAccount);
        Assert.AreEqual(AppLanguage.Russian, coordinator.Language);
        Assert.AreEqual("ru", settings.Settings?.Language);
    }

    [TestMethod]
    public async Task AccountDeleteFailureDoesNotPretendToSignOut()
    {
        var account = TestData.Account();
        var accounts = new MemoryAccountStore(account) { FailClear = true };
        var coordinator = new AppCoordinator(new MemorySettingsStore(), accounts, new SequenceAuthService());
        await coordinator.InitializeAsync();
        coordinator.OpenSettings(SettingsSection.Profile);

        await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.LogoutAsync());

        Assert.AreEqual(LauncherScreen.Settings, coordinator.Screen);
        Assert.AreEqual(account, coordinator.Account);
        Assert.AreEqual(account, accounts.SavedAccount);
    }

    [TestMethod]
    public async Task BackInvalidatesSavePausedBeforeFinalCommit()
    {
        var accounts = new PausingAccountStore();
        var coordinator = new AppCoordinator(new MemorySettingsStore(), accounts, new SequenceAuthService());
        await coordinator.InitializeAsync();
        var attempt = coordinator.BeginSignIn();
        await accounts.FirstSaveReached.Task;

        coordinator.BackFromSignIn();
        accounts.ReleaseFirstSave.TrySetResult();
        await attempt;

        Assert.AreEqual(LauncherScreen.Welcome, coordinator.Screen);
        Assert.IsNull(coordinator.Account);
        Assert.IsNull(accounts.SavedAccount);
        Assert.IsNull(coordinator.LoginError);
    }

    [TestMethod]
    public async Task RetryMakesOldPausedSaveUnableToReplaceNewAccount()
    {
        var accounts = new PausingAccountStore();
        var auth = new SequenceAuthService(attempt => Task.FromResult(TestData.Account(attempt.ToString(), "player-" + attempt)));
        var coordinator = new AppCoordinator(new MemorySettingsStore(), accounts, auth);
        await coordinator.InitializeAsync();
        var firstAttempt = coordinator.BeginSignIn();
        await accounts.FirstSaveReached.Task;

        var retryAttempt = coordinator.RetrySignIn();
        await retryAttempt;
        accounts.ReleaseFirstSave.TrySetResult();
        await firstAttempt;

        Assert.AreEqual("2", coordinator.Account?.Xuid);
        Assert.AreEqual("2", accounts.SavedAccount?.Xuid);
        Assert.AreEqual(LauncherScreen.Builds, coordinator.Screen);
    }

    [TestMethod]
    public async Task LogoutInvalidatesSavePausedBeforeFinalCommit()
    {
        var saved = TestData.Account("old", "old-player");
        var accounts = new PausingAccountStore(saved);
        var coordinator = new AppCoordinator(new MemorySettingsStore(), accounts, new SequenceAuthService());
        await coordinator.InitializeAsync();
        var attempt = coordinator.BeginSignIn();
        await accounts.FirstSaveReached.Task;

        await coordinator.LogoutAsync();
        accounts.ReleaseFirstSave.TrySetResult();
        await attempt;

        Assert.AreEqual(LauncherScreen.Welcome, coordinator.Screen);
        Assert.IsNull(coordinator.Account);
        Assert.IsNull(accounts.SavedAccount);
    }

    [TestMethod]
    public async Task FailedAccountSaveLeavesUserOnLoginWithLocalizedFailureCode()
    {
        var accounts = new MemoryAccountStore { FailSave = true };
        var coordinator = new AppCoordinator(new MemorySettingsStore(), accounts, new SequenceAuthService());
        await coordinator.InitializeAsync();

        await coordinator.BeginSignIn();

        Assert.AreEqual(LauncherScreen.Login, coordinator.Screen);
        Assert.AreEqual(SignInPhase.Failed, coordinator.SignInPhase);
        Assert.AreEqual(AuthErrorKind.Persistence, coordinator.LoginError);
        Assert.IsNull(coordinator.Account);
        Assert.IsNull(accounts.SavedAccount);
    }

    [TestMethod]
    public async Task CorruptSavedAccountReturnsToWelcomeWithRecoveryNotice()
    {
        var accounts = new MemoryAccountStore { FailLoad = true };
        var coordinator = new AppCoordinator(new MemorySettingsStore(), accounts, new SequenceAuthService());

        await coordinator.InitializeAsync();

        Assert.AreEqual(LauncherScreen.Welcome, coordinator.Screen);
        Assert.AreEqual(StartupNotice.AccountUnavailable, coordinator.CurrentStartupNotice);
        Assert.IsNull(coordinator.Account);
    }

    [TestMethod]
    public async Task LanguageSavesRunInSelectionOrderAndLatestChoiceWins()
    {
        var settings = new PausingSettingsStore(new UserSettings("ru"));
        var coordinator = new AppCoordinator(settings, new MemoryAccountStore(), new SequenceAuthService());
        await coordinator.InitializeAsync();

        var english = coordinator.SetLanguageAsync(AppLanguage.English);
        await settings.FirstSaveReached.Task;
        var russian = coordinator.SetLanguageAsync(AppLanguage.Russian);
        settings.ReleaseFirstSave.TrySetResult();
        await Task.WhenAll(english, russian);

        Assert.AreEqual(AppLanguage.Russian, coordinator.Language);
        Assert.AreEqual("ru", settings.Settings?.Language);
    }

    [TestMethod]
    public async Task FailedOlderLanguageSaveDoesNotRevertRepeatedChoice()
    {
        var settings = new PausingSettingsStore(new UserSettings("ru")) { FailFirstSaves = 1 };
        var coordinator = new AppCoordinator(settings, new MemoryAccountStore(), new SequenceAuthService());
        await coordinator.InitializeAsync();

        var failed = coordinator.SetLanguageAsync(AppLanguage.English);
        await settings.FirstSaveReached.Task;
        var repeated = coordinator.SetLanguageAsync(AppLanguage.English);
        settings.ReleaseFirstSave.TrySetResult();

        await Assert.ThrowsExactlyAsync<IOException>(() => failed);
        await repeated;

        Assert.AreEqual(AppLanguage.English, coordinator.Language);
        Assert.AreEqual("en", settings.Settings?.Language);
    }

    [TestMethod]
    public async Task FailedLatestLanguageSaveRollsBackToLastSavedLanguage()
    {
        var settings = new PausingSettingsStore(new UserSettings("ru")) { FailFirstSaves = 2 };
        var coordinator = new AppCoordinator(settings, new MemoryAccountStore(), new SequenceAuthService());
        await coordinator.InitializeAsync();

        var failedOlder = coordinator.SetLanguageAsync(AppLanguage.English);
        await settings.FirstSaveReached.Task;
        var failedLatest = coordinator.SetLanguageAsync(AppLanguage.Russian);
        settings.ReleaseFirstSave.TrySetResult();

        await Assert.ThrowsExactlyAsync<IOException>(() => failedOlder);
        await Assert.ThrowsExactlyAsync<IOException>(() => failedLatest);

        Assert.AreEqual(AppLanguage.Russian, coordinator.Language);
        Assert.AreEqual("ru", settings.Settings?.Language);
    }

    [TestMethod]
    public async Task LaterLanguageSaveFailureRollsBackToTheActuallySavedLanguage()
    {
        var settings = new NthFailingSettingsStore(new UserSettings("ru"), failOnWrite: 2);
        var coordinator = new AppCoordinator(settings, new MemoryAccountStore(), new SequenceAuthService());
        await coordinator.InitializeAsync();

        var english = coordinator.SetLanguageAsync(AppLanguage.English);
        await settings.FirstSaveReached.Task;
        var russian = coordinator.SetLanguageAsync(AppLanguage.Russian);
        settings.ReleaseFirstSave.TrySetResult();

        await english;
        await Assert.ThrowsExactlyAsync<IOException>(() => russian);

        Assert.AreEqual(AppLanguage.English, coordinator.Language);
        Assert.AreEqual("en", settings.Settings?.Language);
    }

    private sealed class NthFailingSettingsStore(UserSettings? settings, int failOnWrite) : IUserSettingsStore
    {
        private int _writeCount;

        public UserSettings? Settings { get; private set; } = settings;
        public TaskCompletionSource FirstSaveReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);

        public async Task SaveAsync(UserSettings newSettings, CancellationToken cancellationToken = default)
        {
            var write = Interlocked.Increment(ref _writeCount);
            if (write == 1)
            {
                FirstSaveReached.TrySetResult();
                await ReleaseFirstSave.Task.WaitAsync(cancellationToken);
            }

            if (write == failOnWrite)
            {
                throw new IOException("Injected settings write failure.");
            }

            Settings = newSettings;
        }
    }
}

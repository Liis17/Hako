using Hako.Core;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Hako.Tests;

[TestClass]
public sealed class HakoAccountCoordinatorTests
{
    [TestMethod]
    public async Task ApprovalIsPolledEveryTwoSecondsThenStoredAsAnIndependentSession()
    {
        var time = new ManualTimeProvider();
        var auth = new FakeHakoAuth(time) { Status = HakoRequestStatus.Approved };
        var store = new MemoryHakoSessionStore();
        using var coordinator = new HakoAccountCoordinator(auth, store, time);

        var attempt = coordinator.BeginRegistrationAsync();
        Assert.AreEqual(HakoAttemptPhase.Pending, coordinator.AttemptPhase);
        Assert.IsNotNull(coordinator.BotLink);
        Assert.AreEqual(0, auth.StatusCount);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, auth.StatusCount);
        time.Advance(TimeSpan.FromSeconds(1));
        await attempt;

        Assert.AreEqual(1, auth.StatusCount);
        Assert.AreEqual(1, auth.ExchangeCount);
        Assert.AreEqual(HakoAttemptPhase.Idle, coordinator.AttemptPhase);
        Assert.AreEqual("Player_123", coordinator.Session?.Account.Username);
        Assert.AreEqual("session-secret", store.SavedSession?.Token);
        Assert.AreEqual(HakoSessionStatus.Verified, coordinator.SessionStatus);
    }

    [TestMethod]
    public async Task RequestExpiryStopsBeforePollingAndReportsExpired()
    {
        var time = new ManualTimeProvider();
        var auth = new FakeHakoAuth(time) { RequestLifetime = TimeSpan.FromSeconds(1) };
        using var coordinator = new HakoAccountCoordinator(auth, new MemoryHakoSessionStore(), time);

        var attempt = coordinator.BeginRegistrationAsync();
        time.Advance(TimeSpan.FromSeconds(1));
        await attempt;

        Assert.AreEqual(0, auth.StatusCount);
        Assert.AreEqual(HakoAuthErrorKind.RequestExpired, coordinator.AttemptError);
        Assert.AreEqual(HakoAttemptPhase.Failed, coordinator.AttemptPhase);
    }

    [TestMethod]
    public async Task DeniedRequestBecomesTerminalWithoutExchanging()
    {
        var time = new ManualTimeProvider();
        var auth = new FakeHakoAuth(time) { Status = HakoRequestStatus.Denied };
        using var coordinator = new HakoAccountCoordinator(auth, new MemoryHakoSessionStore(), time);

        var attempt = coordinator.BeginLoginAsync("@Player_123");
        time.Advance(TimeSpan.FromSeconds(2));
        await attempt;

        Assert.AreEqual(HakoAuthErrorKind.AuthorizationDenied, coordinator.AttemptError);
        Assert.AreEqual("Player_123", auth.LastUsername);
        Assert.AreEqual(0, auth.ExchangeCount);
    }

    [TestMethod]
    public async Task LostExchangeRequiresFreshLoginAndIsNeverRepeated()
    {
        var time = new ManualTimeProvider();
        var auth = new FakeHakoAuth(time) { Status = HakoRequestStatus.Approved };
        auth.Exchange = (_, _) => Task.FromException<HakoExchangeResult>(new HakoAuthException(HakoAuthErrorKind.Network));
        using var coordinator = new HakoAccountCoordinator(auth, new MemoryHakoSessionStore(), time);

        var attempt = coordinator.BeginRegistrationAsync();
        time.Advance(TimeSpan.FromSeconds(2));
        await attempt;

        Assert.AreEqual(1, auth.ExchangeCount);
        Assert.IsTrue(coordinator.RequiresFreshLogin);
        Assert.AreEqual(HakoAuthErrorKind.Network, coordinator.AttemptError);
        Assert.IsNull(coordinator.Session);
    }

    [TestMethod]
    public async Task TimedOutExchangeBodyRequiresFreshLoginWithoutPersistingSession()
    {
        var time = new ManualTimeProvider();
        var exchangeBody = new StallingReadStream();
        var exchangeCalls = 0;
        using var http = new HttpClient(new CallbackHttpHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/auth/register")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        "{\"request_id\":\"8aabf9c0-a4b4-4b4d-a0fc-a9e8e753bdf1\",\"bot_link\":\"https://t.me/hako_bot?start=abc\",\"exchange_secret\":\"exchange-secret\",\"expires_at\":\"2025-01-01T00:05:00Z\"}",
                        System.Text.Encoding.UTF8,
                        "application/json")
                });
            }

            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"status\":\"approved\",\"expires_at\":\"2025-01-01T00:05:00Z\"}",
                        System.Text.Encoding.UTF8,
                        "application/json")
                });
            }

            exchangeCalls++;
            var content = new StreamContent(exchangeBody);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var auth = new HakoAuthApiClient(http, new Uri("https://hako.liis17.com/"), TimeSpan.FromMilliseconds(50));
        var store = new MemoryHakoSessionStore();
        using var coordinator = new HakoAccountCoordinator(auth, store, time);

        var attempt = coordinator.BeginRegistrationAsync();
        Assert.AreEqual(HakoAttemptPhase.Pending, coordinator.AttemptPhase);
        time.Advance(TimeSpan.FromSeconds(2));
        await exchangeBody.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await attempt.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, exchangeCalls);
        Assert.AreEqual(HakoAuthErrorKind.Timeout, coordinator.AttemptError);
        Assert.IsTrue(coordinator.RequiresFreshLogin);
        Assert.IsNull(coordinator.Session);
        Assert.IsNull(store.SavedSession);
    }

    [TestMethod]
    public async Task ExpiredExchangeTokenIsRevokedAndNeverPersisted()
    {
        var time = new ManualTimeProvider();
        var auth = new FakeHakoAuth(time) { Status = HakoRequestStatus.Approved };
        auth.Exchange = (_, _) => Task.FromResult(new HakoExchangeResult(
            "expired-session-token",
            time.GetUtcNow().AddSeconds(-1),
            new HakoAccount("account-id", "Player_123", time.GetUtcNow())));
        var store = new MemoryHakoSessionStore();
        using var coordinator = new HakoAccountCoordinator(auth, store, time);

        var attempt = coordinator.BeginRegistrationAsync();
        time.Advance(TimeSpan.FromSeconds(2));
        await attempt;

        Assert.IsTrue(coordinator.RequiresFreshLogin);
        Assert.AreEqual(HakoAuthErrorKind.InvalidResponse, coordinator.AttemptError);
        Assert.AreEqual(1, auth.LogoutCount);
        Assert.IsNull(coordinator.Session);
        Assert.IsNull(store.SavedSession);
    }

    [TestMethod]
    public async Task CancelledAttemptCannotCommitLateExchangeAndRevokesReturnedSession()
    {
        var time = new ManualTimeProvider();
        var auth = new FakeHakoAuth(time) { Status = HakoRequestStatus.Approved };
        var store = new MemoryHakoSessionStore { PauseSave = true };
        using var coordinator = new HakoAccountCoordinator(auth, store, time);

        var attempt = coordinator.BeginRegistrationAsync();
        time.Advance(TimeSpan.FromSeconds(2));
        await store.SaveReached.Task;
        coordinator.CancelAttempt();
        store.ReleaseSave.TrySetResult();
        await attempt;

        Assert.IsNull(coordinator.Session);
        Assert.IsNull(store.SavedSession);
        Assert.AreEqual(1, auth.LogoutCount);
    }

    [TestMethod]
    public async Task LogoutCancelsPendingLoginBeforeItCanExchangeOrSave()
    {
        var time = new ManualTimeProvider();
        var session = CreateSession(time.GetUtcNow());
        var auth = new FakeHakoAuth(time) { Status = HakoRequestStatus.Approved };
        var store = new MemoryHakoSessionStore(session);
        using var coordinator = new HakoAccountCoordinator(auth, store, time);
        await coordinator.InitializeAsync();

        var login = coordinator.BeginRegistrationAsync();
        Assert.AreEqual(HakoAttemptPhase.Pending, coordinator.AttemptPhase);

        Assert.AreEqual(HakoLogoutResult.SignedOut, await coordinator.LogoutAsync());
        time.Advance(TimeSpan.FromSeconds(2));
        await login;

        Assert.AreEqual(0, auth.ExchangeCount);
        Assert.IsNull(coordinator.Session);
        Assert.IsNull(store.SavedSession);
    }

    [TestMethod]
    public async Task VerificationCoalescesAndPreservesCachedSessionOnRateLimit()
    {
        var time = new ManualTimeProvider();
        var session = CreateSession(time.GetUtcNow());
        var store = new MemoryHakoSessionStore(session);
        var auth = new FakeHakoAuth(time);
        var verification = new TaskCompletionSource<HakoMeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        auth.Me = (_, _) => verification.Task;
        using var coordinator = new HakoAccountCoordinator(auth, store, time);
        await coordinator.InitializeAsync();

        var first = coordinator.VerifyManuallyAsync();
        var second = coordinator.VerifyManuallyAsync();
        Assert.AreSame(first, second);
        Assert.AreEqual(1, auth.MeCount);
        verification.SetException(new HakoAuthException(HakoAuthErrorKind.RateLimited));
        await first;

        Assert.AreSame(session, coordinator.Session);
        Assert.AreSame(session, store.SavedSession);
        Assert.AreEqual(HakoSessionStatus.Unverified, coordinator.SessionStatus);
        Assert.AreEqual(HakoAuthErrorKind.RateLimited, coordinator.VerificationError);
        await coordinator.VerifyOnProfileEntryAsync();
        Assert.AreEqual(1, auth.MeCount);
    }

    [TestMethod]
    public async Task LogoutInvalidatesLateVerificationResponseBeforeItCanRestoreSession()
    {
        var time = new ManualTimeProvider();
        var session = CreateSession(time.GetUtcNow());
        var store = new MemoryHakoSessionStore(session);
        var auth = new FakeHakoAuth(time);
        var response = new TaskCompletionSource<HakoMeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        auth.Me = (_, _) => response.Task;
        using var coordinator = new HakoAccountCoordinator(auth, store, time);
        await coordinator.InitializeAsync();

        var verification = coordinator.VerifyManuallyAsync();
        Assert.AreEqual(1, auth.MeCount);
        Assert.AreEqual(HakoSessionStatus.Verifying, coordinator.SessionStatus);

        Assert.AreEqual(HakoLogoutResult.SignedOut, await coordinator.LogoutAsync());
        response.SetResult(new HakoMeResult(session.Account, session.DeviceName, time.GetUtcNow().AddDays(14)));
        await verification;

        Assert.IsNull(coordinator.Session);
        Assert.IsNull(store.SavedSession);
        Assert.AreEqual(HakoSessionStatus.SignedOut, coordinator.SessionStatus);
    }

    [TestMethod]
    public async Task VerificationCannotRestartWhileRemoteLogoutIsInProgress()
    {
        var time = new ManualTimeProvider();
        var session = CreateSession(time.GetUtcNow());
        var store = new MemoryHakoSessionStore(session);
        var auth = new FakeHakoAuth(time);
        var finishLogout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        auth.Logout = (_, _) => finishLogout.Task;
        using var coordinator = new HakoAccountCoordinator(auth, store, time);
        await coordinator.InitializeAsync();

        var logout = coordinator.LogoutAsync();
        await auth.LogoutReached.Task;
        await coordinator.VerifyManuallyAsync();
        await coordinator.VerifyOnProfileEntryAsync();

        Assert.AreEqual(0, auth.MeCount);
        Assert.AreSame(session, coordinator.Session);
        Assert.AreSame(session, store.SavedSession);

        finishLogout.TrySetResult();
        Assert.AreEqual(HakoLogoutResult.SignedOut, await logout);
        Assert.IsNull(coordinator.Session);
        Assert.IsNull(store.SavedSession);
    }

    [TestMethod]
    public async Task StartupAlwaysVerifiesRecentSavedSessionAndProfileEntryUsesOneMinuteThrottle()
    {
        var time = new ManualTimeProvider();
        var session = CreateSession(time.GetUtcNow()) with { VerifiedAt = time.GetUtcNow().AddMinutes(-5) };
        var auth = new FakeHakoAuth(time);
        using var hako = new HakoAccountCoordinator(auth, new MemoryHakoSessionStore(session), time);
        var app = new AppCoordinator(new MemorySettingsStore(), new MemoryAccountStore(), new SequenceAuthService(), hakoAccount: hako);

        await app.InitializeAsync();
        Assert.AreEqual(1, auth.MeCount);

        app.OpenSettings(SettingsSection.Profile);
        Assert.AreEqual(1, auth.MeCount);
        time.Advance(TimeSpan.FromSeconds(59));
        app.OpenSettings(SettingsSection.Profile);
        Assert.AreEqual(1, auth.MeCount);
        time.Advance(TimeSpan.FromSeconds(1));
        app.OpenSettings(SettingsSection.Profile);
        Assert.AreEqual(2, auth.MeCount);
    }

    [TestMethod]
    public async Task PeriodicVerificationUsesFakeClockAndStopsWhenCoordinatorIsDisposed()
    {
        var time = new ManualTimeProvider();
        var auth = new FakeHakoAuth(time);
        var coordinator = new HakoAccountCoordinator(auth, new MemoryHakoSessionStore(CreateSession(time.GetUtcNow())), time);
        await coordinator.InitializeAsync();
        await coordinator.VerifyOnStartupAsync();

        Assert.AreEqual(1, auth.MeCount);
        Assert.AreEqual(1, time.PendingTimerCount);
        time.Advance(TimeSpan.FromHours(24));
        Assert.AreEqual(2, auth.MeCount);

        coordinator.Dispose();
        Assert.AreEqual(0, time.PendingTimerCount);
        time.Advance(TimeSpan.FromHours(48));
        Assert.AreEqual(2, auth.MeCount);
    }

    [TestMethod]
    public async Task LogoutErrorBodyTimeoutCanRecoverWithLocalOnlyClear()
    {
        var time = new ManualTimeProvider();
        var errorBody = new StallingReadStream();
        using var http = new HttpClient(new CallbackHttpHandler((_, _) =>
        {
            var content = new StreamContent(errorBody);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = content });
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var auth = new HakoAuthApiClient(http, new Uri("https://hako.liis17.com/"), TimeSpan.FromMilliseconds(50));
        var session = CreateSession(time.GetUtcNow());
        var store = new MemoryHakoSessionStore(session);
        using var coordinator = new HakoAccountCoordinator(auth, store, time);
        await coordinator.InitializeAsync();

        var logout = coordinator.LogoutAsync();
        await errorBody.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(HakoLogoutResult.RemoteLogoutFailed, await logout.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(HakoAuthErrorKind.Timeout, coordinator.VerificationError);
        Assert.AreSame(session, store.SavedSession);

        Assert.IsTrue(await coordinator.ClearLocalSessionAsync());
        Assert.IsNull(coordinator.Session);
        Assert.IsNull(store.SavedSession);
    }

    [TestMethod]
    public async Task StalledBestEffortRevokeCannotHoldCancelledAttemptOpen()
    {
        var time = new ManualTimeProvider();
        var store = new MemoryHakoSessionStore { PauseSave = true };
        var auth = new FakeHakoAuth(time) { Status = HakoRequestStatus.Approved };
        auth.Logout = (_, _) => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        using var coordinator = new HakoAccountCoordinator(auth, store, time);

        var attempt = coordinator.BeginRegistrationAsync();
        time.Advance(TimeSpan.FromSeconds(2));
        await store.SaveReached.Task;
        coordinator.CancelAttempt();
        await auth.LogoutReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(5));
        await attempt.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, auth.LogoutCount);
        Assert.IsNull(coordinator.Session);
        Assert.IsNull(store.SavedSession);
    }

    [TestMethod]
    public async Task VerificationPersistsRenewedProfileAndExpiry()
    {
        var time = new ManualTimeProvider();
        var store = new MemoryHakoSessionStore(CreateSession(time.GetUtcNow()));
        var auth = new FakeHakoAuth(time)
        {
            Me = (_, _) => Task.FromResult(new HakoMeResult(
                new HakoAccount("account-id", "Updated_123", time.GetUtcNow()),
                "Gaming PC",
                time.GetUtcNow().AddDays(14)))
        };
        using var coordinator = new HakoAccountCoordinator(auth, store, time);
        await coordinator.InitializeAsync();

        await coordinator.VerifyManuallyAsync();

        Assert.AreEqual("Updated_123", coordinator.Session?.Account.Username);
        Assert.AreEqual("Gaming PC", store.SavedSession?.DeviceName);
        Assert.AreEqual(time.GetUtcNow().AddDays(14), store.SavedSession?.ExpiresAt);
        Assert.AreEqual(time.GetUtcNow(), store.SavedSession?.VerifiedAt);
        Assert.AreEqual(HakoSessionStatus.Verified, coordinator.SessionStatus);
    }

    [TestMethod]
    public async Task VerificationRejectsExpiredRenewalWithoutReplacingCachedSession()
    {
        var time = new ManualTimeProvider();
        var session = CreateSession(time.GetUtcNow());
        var store = new MemoryHakoSessionStore(session);
        var auth = new FakeHakoAuth(time)
        {
            Me = (_, _) => Task.FromResult(new HakoMeResult(
                new HakoAccount("account-id", "Updated_123", time.GetUtcNow()),
                "Gaming PC",
                time.GetUtcNow().AddSeconds(-1)))
        };
        using var coordinator = new HakoAccountCoordinator(auth, store, time);
        await coordinator.InitializeAsync();

        await coordinator.VerifyManuallyAsync();

        Assert.AreSame(session, coordinator.Session);
        Assert.AreSame(session, store.SavedSession);
        Assert.AreEqual(HakoAuthErrorKind.InvalidResponse, coordinator.VerificationError);
        Assert.AreEqual(HakoSessionStatus.Unverified, coordinator.SessionStatus);
    }

    [TestMethod]
    public async Task UnauthorizedVerificationClearsOnlyHakoSession()
    {
        var time = new ManualTimeProvider();
        var store = new MemoryHakoSessionStore(CreateSession(time.GetUtcNow()));
        var auth = new FakeHakoAuth(time)
        {
            Me = (_, _) => Task.FromException<HakoMeResult>(new HakoAuthException(HakoAuthErrorKind.Unauthorized))
        };
        using var coordinator = new HakoAccountCoordinator(auth, store, time);
        await coordinator.InitializeAsync();

        await coordinator.VerifyManuallyAsync();

        Assert.IsNull(coordinator.Session);
        Assert.IsNull(store.SavedSession);
        Assert.AreEqual(HakoSessionStatus.SignedOut, coordinator.SessionStatus);
    }

    [TestMethod]
    public async Task RemoteLogoutFailureKeepsSessionUntilUserChoosesLocalOnly()
    {
        var time = new ManualTimeProvider();
        var session = CreateSession(time.GetUtcNow());
        var store = new MemoryHakoSessionStore(session);
        var auth = new FakeHakoAuth(time)
        {
            Logout = (_, _) => Task.FromException(new HakoAuthException(HakoAuthErrorKind.Network))
        };
        using var coordinator = new HakoAccountCoordinator(auth, store, time);
        await coordinator.InitializeAsync();

        var result = await coordinator.LogoutAsync();

        Assert.AreEqual(HakoLogoutResult.RemoteLogoutFailed, result);
        Assert.AreSame(session, coordinator.Session);
        Assert.AreSame(session, store.SavedSession);
        Assert.AreEqual(HakoSessionStatus.LogoutFailed, coordinator.SessionStatus);
        Assert.IsTrue(await coordinator.ClearLocalSessionAsync());
        Assert.IsNull(coordinator.Session);
        Assert.IsNull(store.SavedSession);
    }

    [TestMethod]
    public async Task HakoOnlySignInLeavesMicrosoftAndGuestPlaytimeIdentityUntouched()
    {
        var time = new ManualTimeProvider();
        var auth = new FakeHakoAuth(time) { Status = HakoRequestStatus.Approved };
        using var hako = new HakoAccountCoordinator(auth, new MemoryHakoSessionStore(), time);
        var transfer = new RecordingGuestTransfer();
        var coordinator = new AppCoordinator(new MemorySettingsStore(), new MemoryAccountStore(),
            new SequenceAuthService(), playtimeTransfer: transfer, hakoAccount: hako);
        await coordinator.InitializeAsync();

        coordinator.OpenHakoSignIn();
        var attempt = coordinator.BeginHakoSignIn("Player_123");
        time.Advance(TimeSpan.FromSeconds(2));
        await attempt;

        Assert.IsNotNull(coordinator.HakoSession);
        Assert.IsNull(coordinator.Account);
        Assert.IsNull(coordinator.MinecraftSessions.CurrentAccountXuid);
        Assert.IsFalse(coordinator.IsGuest);
        Assert.AreEqual(0, transfer.PrepareCount);
        Assert.AreEqual(LauncherScreen.Builds, coordinator.Screen);
    }

    [TestMethod]
    public async Task MicrosoftSessionAndGuestStateRouteIndependentlyFromHako()
    {
        var time = new ManualTimeProvider();
        var savedMicrosoft = TestData.Account();
        var hakoStore = new MemoryHakoSessionStore(CreateSession(time.GetUtcNow()));
        using var hako = new HakoAccountCoordinator(new FakeHakoAuth(time), hakoStore, time);
        var coordinator = new AppCoordinator(new MemorySettingsStore(), new MemoryAccountStore(savedMicrosoft), new SequenceAuthService(),
            hakoAccount: hako);

        await coordinator.InitializeAsync();
        Assert.AreEqual(LauncherScreen.Builds, coordinator.Screen);
        Assert.IsFalse(coordinator.IsGuest);
        Assert.IsTrue(coordinator.HasProviderSession);

        coordinator.EnsureAvailableForRestoreActivation();
        Assert.IsFalse(coordinator.IsGuest);
        await coordinator.LogoutAsync();

        Assert.IsNull(coordinator.Account);
        Assert.IsNotNull(coordinator.HakoSession);
        Assert.AreEqual(LauncherScreen.Builds, coordinator.Screen);
        Assert.IsFalse(coordinator.IsGuest);
    }

    [TestMethod]
    public async Task RestoreActivationStartsGuestOnlyWhenNeitherProviderIsPresent()
    {
        var guest = new AppCoordinator(new MemorySettingsStore(), new MemoryAccountStore(), new SequenceAuthService());
        await guest.InitializeAsync();
        guest.EnsureAvailableForRestoreActivation();
        Assert.IsTrue(guest.IsGuest);

        var microsoft = new AppCoordinator(new MemorySettingsStore(), new MemoryAccountStore(TestData.Account()), new SequenceAuthService());
        await microsoft.InitializeAsync();
        microsoft.EnsureAvailableForRestoreActivation();
        Assert.IsFalse(microsoft.IsGuest);

        var hakoTime = new ManualTimeProvider();
        var hako = new HakoAccountCoordinator(new FakeHakoAuth(hakoTime),
            new MemoryHakoSessionStore(CreateSession(hakoTime.GetUtcNow())), hakoTime);
        var hakoOnly = new AppCoordinator(new MemorySettingsStore(), new MemoryAccountStore(), new SequenceAuthService(), hakoAccount: hako);
        await hakoOnly.InitializeAsync();
        hakoOnly.EnsureAvailableForRestoreActivation();
        Assert.IsFalse(hakoOnly.IsGuest);
        Assert.AreEqual(LauncherScreen.Builds, hakoOnly.Screen);

        var bothTime = new ManualTimeProvider();
        var bothHako = new HakoAccountCoordinator(new FakeHakoAuth(bothTime),
            new MemoryHakoSessionStore(CreateSession(bothTime.GetUtcNow())), bothTime);
        var both = new AppCoordinator(new MemorySettingsStore(), new MemoryAccountStore(TestData.Account()), new SequenceAuthService(), hakoAccount: bothHako);
        await both.InitializeAsync();
        Assert.IsTrue(both.HasProviderSession);
        both.EnsureAvailableForRestoreActivation();
        Assert.IsFalse(both.IsGuest);
    }

    private static HakoSession CreateSession(DateTimeOffset now) => new(
        "session-secret",
        new HakoAccount("account-id", "Player_123", now.AddDays(-1)),
        "Hako device",
        now.AddDays(14),
        null);

    private sealed class FakeHakoAuth(ManualTimeProvider time) : IHakoAuthService
    {
        public HakoRequestStatus Status { get; set; } = HakoRequestStatus.Approved;
        public TimeSpan RequestLifetime { get; set; } = TimeSpan.FromMinutes(5);
        public int StatusCount { get; private set; }
        public int ExchangeCount { get; private set; }
        public int MeCount { get; private set; }
        public int LogoutCount { get; private set; }
        public TaskCompletionSource LogoutReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? LastUsername { get; private set; }
        public Func<int, CancellationToken, Task<HakoAuthRequestStatus>>? Poll { get; set; }
        public Func<HakoPendingAuthRequest, CancellationToken, Task<HakoExchangeResult>>? Exchange { get; set; }
        public Func<string, CancellationToken, Task<HakoMeResult>>? Me { get; set; }
        public Func<string, CancellationToken, Task>? Logout { get; set; }

        public Task<HakoPendingAuthRequest> RegisterAsync(string deviceName, CancellationToken cancellationToken) =>
            Task.FromResult(new HakoPendingAuthRequest(Guid.NewGuid(), "exchange-secret", time.GetUtcNow() + RequestLifetime,
                new Uri("https://t.me/hako_bot?start=abc")));

        public Task<HakoPendingAuthRequest> LoginAsync(string username, string deviceName, CancellationToken cancellationToken)
        {
            LastUsername = username;
            return Task.FromResult(new HakoPendingAuthRequest(Guid.NewGuid(), "exchange-secret", time.GetUtcNow() + RequestLifetime, null));
        }

        public Task<HakoAuthRequestStatus> GetRequestStatusAsync(HakoPendingAuthRequest request, CancellationToken cancellationToken)
        {
            var count = ++StatusCount;
            return Poll?.Invoke(count, cancellationToken)
                ?? Task.FromResult(new HakoAuthRequestStatus(Status, request.ExpiresAt));
        }

        public Task<HakoExchangeResult> ExchangeAsync(HakoPendingAuthRequest request, CancellationToken cancellationToken)
        {
            ExchangeCount++;
            return Exchange?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new HakoExchangeResult("session-secret", new HakoAccount("account-id", "Player_123", time.GetUtcNow()).CreatedAt.AddDays(14),
                    new HakoAccount("account-id", "Player_123", time.GetUtcNow())));
        }

        public Task<HakoMeResult> GetMeAsync(string token, CancellationToken cancellationToken)
        {
            MeCount++;
            return Me?.Invoke(token, cancellationToken)
                ?? Task.FromResult(new HakoMeResult(new HakoAccount("account-id", "Player_123", time.GetUtcNow()),
                    "Hako device", time.GetUtcNow().AddDays(14)));
        }

        public Task LogoutAsync(string token, CancellationToken cancellationToken)
        {
            LogoutCount++;
            LogoutReached.TrySetResult();
            return Logout?.Invoke(token, cancellationToken) ?? Task.CompletedTask;
        }
    }

    private sealed class MemoryHakoSessionStore(HakoSession? initial = null) : IHakoSessionStore
    {
        public HakoSession? SavedSession { get; private set; } = initial;
        public bool PauseSave { get; set; }
        public TaskCompletionSource SaveReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<HakoSession?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(SavedSession);

        public async Task<bool> SaveAsync(
            HakoSession session,
            Func<Action, bool> commitIfCurrent,
            CancellationToken cancellationToken = default)
        {
            if (PauseSave)
            {
                SaveReached.TrySetResult();
                await ReleaseSave.Task.WaitAsync(cancellationToken);
            }

            return commitIfCurrent(() => SavedSession = session);
        }

        public Task<bool> ClearAsync(Func<Action, bool> commitIfCurrent, CancellationToken cancellationToken = default) =>
            Task.FromResult(commitIfCurrent(() => SavedSession = null));
    }

    private sealed class RecordingGuestTransfer : Hako.Core.Playtime.IAccountPlaytimeTransfer
    {
        public int PrepareCount { get; private set; }

        public Task<Guid?> PrepareGuestTransferAsync(string targetXuid, CancellationToken cancellationToken = default)
        {
            PrepareCount++;
            return Task.FromResult<Guid?>(null);
        }

        public Task CompleteGuestTransferAsync(Guid transferID, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CancelGuestTransferAsync(Guid transferID, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class HakoSessionStoreTests
{
    [TestMethod]
    public async Task HakoSessionUsesSeparateDpapiFileAndDoesNotChangeMicrosoftSession()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "Hako.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var microsoftStore = new AccountSessionStore(temporaryRoot);
            var microsoft = TestData.Account();
            Assert.IsTrue(await microsoftStore.SaveAsync(microsoft, replace =>
            {
                replace();
                return true;
            }));

            var hako = new HakoSession(
                "hako-private-token",
                new HakoAccount("hako-id", "Player_123", DateTimeOffset.Parse("2025-01-01T00:00:00Z")),
                "Gaming PC",
                DateTimeOffset.Parse("2025-01-15T00:00:00Z"),
                DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
            var hakoStore = new HakoSessionStore(temporaryRoot);
            Assert.IsTrue(await hakoStore.SaveAsync(hako, replace =>
            {
                replace();
                return true;
            }));

            var hakoPath = Path.Combine(temporaryRoot, "hako-session.dat");
            var protectedBytes = await File.ReadAllBytesAsync(hakoPath);
            Assert.IsFalse(Encoding.UTF8.GetString(protectedBytes).Contains("hako-private-token", StringComparison.Ordinal));
            Assert.IsFalse(hako.ToString().Contains("hako-private-token", StringComparison.Ordinal));
            Assert.AreEqual(hako, await hakoStore.LoadAsync());
            Assert.AreEqual(microsoft, await microsoftStore.LoadAsync());

            Assert.IsTrue(await hakoStore.ClearAsync(replace =>
            {
                replace();
                return true;
            }));
            Assert.IsNull(await hakoStore.LoadAsync());
            Assert.AreEqual(microsoft, await microsoftStore.LoadAsync());
            Assert.IsTrue(File.Exists(Path.Combine(temporaryRoot, "session.dat")));
        }
        finally
        {
            var normalizedTemporaryRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Hako.Tests")) + Path.DirectorySeparatorChar;
            var normalizedTarget = Path.GetFullPath(temporaryRoot);
            Assert.IsTrue(normalizedTarget.StartsWith(normalizedTemporaryRoot, StringComparison.OrdinalIgnoreCase));
            Directory.Delete(normalizedTarget, recursive: true);
        }
    }
}

using Hako.Core;

namespace Hako.Tests;

[TestClass]
public sealed class MinecraftSessionCoordinatorTests
{
    [TestMethod]
    public async Task RotatedRefreshTokenIsSavedEvenWhenMinecraftConnectionFails()
    {
        var accountStore = new MemoryAccountStore(TestData.Account());
        var auth = new RefreshAuthService(async (_, persist, cancellationToken) =>
        {
            await persist("rotated-refresh", cancellationToken);
            throw new HttpRequestException("simulated Xbox network failure");
        });
        var app = await CreateAppAsync(accountStore, auth, expireMinecraftToken: true);

        var error = await Assert.ThrowsExactlyAsync<AuthServiceException>(() => app.MinecraftSessions.ConnectForLaunchAsync());

        Assert.AreEqual(AuthErrorKind.Network, error.Kind);
        Assert.AreEqual("rotated-refresh", accountStore.SavedAccount!.MicrosoftRefreshToken);
        Assert.AreEqual("rotated-refresh", app.Account!.MicrosoftRefreshToken);
        Assert.AreEqual("minecraft-secret", app.Account.MinecraftAccessToken);
        Assert.AreEqual(MinecraftSessionStatus.Failed, app.MinecraftSessions.Status);
    }

    [TestMethod]
    public async Task ParallelLaunchesShareOneRefreshRequest()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reachedMinecraft = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var auth = new RefreshAuthService(async (_, persist, cancellationToken) =>
        {
            await persist("rotated", cancellationToken);
            reachedMinecraft.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return TestData.MinecraftConnection();
        });
        var app = await CreateAppAsync(new MemoryAccountStore(TestData.Account()), auth, expireMinecraftToken: true);

        var first = app.MinecraftSessions.ConnectForLaunchAsync();
        await reachedMinecraft.Task;
        var second = app.MinecraftSessions.ConnectForLaunchAsync();
        Assert.AreEqual(1, auth.ConnectionCount);
        release.TrySetResult();
        var accounts = await Task.WhenAll(first, second);

        Assert.AreEqual(accounts[0], accounts[1]);
        Assert.AreEqual(MinecraftSessionStatus.Connected, app.MinecraftSessions.Status);
    }

    [TestMethod]
    public async Task LogoutCancelsRefreshAndLateResultCannotRestoreAccountFile()
    {
        var reachedMinecraft = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var auth = new RefreshAuthService(async (_, persist, cancellationToken) =>
        {
            await persist("rotated-before-logout", cancellationToken);
            reachedMinecraft.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return TestData.MinecraftConnection();
        });
        var accountStore = new MemoryAccountStore(TestData.Account());
        var app = await CreateAppAsync(accountStore, auth, expireMinecraftToken: true);
        var refresh = app.MinecraftSessions.ConnectForLaunchAsync();
        await reachedMinecraft.Task;

        await app.LogoutAsync();

        try
        {
            await refresh;
            Assert.Fail("The refresh should have been cancelled by logout.");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.IsNull(accountStore.SavedAccount);
        Assert.IsNull(app.Account);
        Assert.AreEqual(MinecraftSessionStatus.Idle, app.MinecraftSessions.Status);
    }

    [TestMethod]
    public async Task AccountReplacedWithinGenerationRetriesRefreshInsteadOfReportingNetworkError()
    {
        AppCoordinator? app = null;
        var refreshTokens = new List<string>();
        var auth = new RefreshAuthService(async (refreshToken, persist, cancellationToken) =>
        {
            refreshTokens.Add(refreshToken);
            if (refreshTokens.Count == 1)
            {
                // Simulates a cancelled earlier refresh that committed its rotated token in the same generation.
                var state = app!.CaptureAccountState();
                Assert.IsTrue(app.TryCommitAccountUpdate(state.Account!, state.Generation,
                    state.Account! with { MicrosoftRefreshToken = "rotated-by-cancelled-refresh" }, () => { }));
            }

            await persist(refreshToken + "-rotated", cancellationToken);
            return TestData.MinecraftConnection();
        });
        var accountStore = new MemoryAccountStore(TestData.Account());
        app = await CreateAppAsync(accountStore, auth, expireMinecraftToken: true);

        var connected = await app.MinecraftSessions.ConnectForLaunchAsync();

        Assert.AreEqual(2, auth.ConnectionCount);
        Assert.AreEqual("rotated-by-cancelled-refresh", refreshTokens[1]);
        Assert.AreEqual("rotated-by-cancelled-refresh-rotated", connected.MicrosoftRefreshToken);
        Assert.AreEqual(connected, app.Account);
        Assert.AreEqual(MinecraftSessionStatus.Connected, app.MinecraftSessions.Status);
        Assert.IsNull(app.MinecraftSessions.Error);
    }

    [TestMethod]
    public async Task CurrentMinecraftSessionSkipsRefreshUntilNearExpiry()
    {
        var auth = new RefreshAuthService();
        var app = await CreateAppAsync(new MemoryAccountStore(TestData.Account()), auth);

        var connected = await app.MinecraftSessions.ConnectForLaunchAsync();

        Assert.AreEqual(TestData.Account(), connected);
        Assert.AreEqual(0, auth.ConnectionCount);
        Assert.AreEqual(MinecraftSessionStatus.Connected, app.MinecraftSessions.Status);
    }

    private static async Task<AppCoordinator> CreateAppAsync(
        MemoryAccountStore accountStore,
        IMicrosoftAuthService auth,
        bool expireMinecraftToken = false)
    {
        var time = new ManualTimeProvider();
        if (expireMinecraftToken)
        {
            time.Advance(TimeSpan.FromHours(2));
        }

        var app = new AppCoordinator(new UserSettingsService(new MemorySettingsStore()), accountStore, auth, time);
        await app.InitializeAsync();
        return app;
    }
}

using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;

namespace Hako.Tests;

[TestClass]
public sealed class GameLaunchCoordinatorTests
{
    [TestMethod]
    public async Task BuildThatIsNotReadyCannotLaunch()
    {
        await using var context = await LaunchContext.CreateAsync();
        var instance = context.CreateInstance(ready: false);

        var error = await Assert.ThrowsExactlyAsync<MinecraftLaunchException>(() => context.Coordinator.LaunchAsync(instance.Id));

        Assert.AreEqual(MinecraftLaunchError.BuildNotReady, error.Error);
        Assert.AreEqual(0, context.PrepareCount);
    }

    [TestMethod]
    public async Task OfflineLaunchUsesLegacyIdentityWithoutRefreshAndReportsSessionLifecycle()
    {
        await using var context = await LaunchContext.CreateAsync(expireMinecraftToken: true);
        var instance = context.CreateInstance(offline: true);
        GameSessionEventArgs? started = null;
        var ended = new TaskCompletionSource<GameSessionEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Coordinator.GameSessionStarted += (_, args) => started = args;
        context.Coordinator.GameSessionEnded += (_, args) => ended.TrySetResult(args);

        await context.Coordinator.LaunchAsync(instance.Id);

        Assert.AreEqual("legacy", context.LastRequest!.Identity.UserType);
        Assert.AreEqual("0", context.LastRequest.Identity.AccessToken);
        Assert.AreEqual(0, context.Auth.ConnectionCount);
        Assert.AreEqual(GameRunPhase.Running, context.Coordinator.GetState(instance.Id)!.Phase);
        Assert.IsNotNull(started);
        Assert.IsNotNull(started.Record.SessionID);
        context.Exit.TrySetResult(0);
        var end = await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, end.ExitCode);
        Assert.AreEqual(started.Record, end.Record);
        Assert.IsNull(context.Coordinator.GetState(instance.Id));
        Assert.IsFalse(context.Store.IsLaunchBusy(instance.Id));
    }

    [TestMethod]
    public async Task NonZeroExitCodeIsReportedWithLog()
    {
        await using var context = await LaunchContext.CreateAsync();
        var instance = context.CreateInstance(offline: true);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Coordinator.GameSessionEnded += (_, _) => ended.TrySetResult();

        await context.Coordinator.LaunchAsync(instance.Id);
        context.Exit.TrySetResult(3);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var state = context.Coordinator.GetState(instance.Id)!;
        Assert.AreEqual(GameRunPhase.Failed, state.Phase);
        Assert.AreEqual(3, state.ExitCode);
        Assert.AreEqual("LaunchGameExitCode", state.ErrorResourceKey);
        Assert.IsNotNull(state.LogPath);
    }

    [TestMethod]
    public async Task RunningBuildCannotBeLaunchedTwice()
    {
        await using var context = await LaunchContext.CreateAsync();
        var instance = context.CreateInstance(offline: true);
        await context.Coordinator.LaunchAsync(instance.Id);

        var error = await Assert.ThrowsExactlyAsync<MinecraftLaunchException>(() => context.Coordinator.LaunchAsync(instance.Id));

        Assert.AreEqual(MinecraftLaunchError.BuildAlreadyRunning, error.Error);
        Assert.AreEqual(1, context.StartCount);
        context.Exit.TrySetResult(0);
    }

    [TestMethod]
    public async Task GlobalJavaPathIsUsedOnlyForGlobalArgumentSource()
    {
        await using var context = await LaunchContext.CreateAsync(useGlobalJava: true);
        var global = context.CreateInstance(offline: true, source: LaunchArgumentSource.Global);
        var custom = context.CreateInstance(offline: true, source: LaunchArgumentSource.Custom, name: "Custom");

        await context.Coordinator.LaunchAsync(global.Id);
        Assert.AreEqual(context.GlobalJava, context.LastRequest!.Executable);
        await context.Coordinator.LaunchAsync(custom.Id);

        Assert.AreEqual(
            Path.Combine(context.Store.GetInstanceDirectory(custom.Id), "java", "bin", "javaw.exe"),
            context.LastRequest!.Executable);
        context.Exit.TrySetResult(0);
    }

    [TestMethod]
    public async Task MissingGlobalJavaFailsWithoutFallbackAndReleasesLease()
    {
        await using var context = await LaunchContext.CreateAsync(useGlobalJava: true);
        var instance = context.CreateInstance(offline: true, source: LaunchArgumentSource.Global);
        File.Delete(context.GlobalJava);

        var error = await Assert.ThrowsExactlyAsync<JavaLaunchValidationException>(() => context.Coordinator.LaunchAsync(instance.Id));

        Assert.AreEqual(JavaValidationError.MissingExecutable, error.Error);
        Assert.AreEqual(0, context.PrepareCount);
        var state = context.Coordinator.GetState(instance.Id)!;
        Assert.AreEqual(GameRunPhase.Failed, state.Phase);
        Assert.AreEqual("LaunchJavaMissing", state.ErrorResourceKey);
        Assert.IsFalse(context.Store.IsLaunchBusy(instance.Id));
    }

    [TestMethod]
    public async Task OnlineLaunchRefreshesExpiredSessionAndPassesMinecraftIdentity()
    {
        await using var context = await LaunchContext.CreateAsync(expireMinecraftToken: true);
        var instance = context.CreateInstance();

        await context.Coordinator.LaunchAsync(instance.Id);

        Assert.AreEqual(1, context.Auth.ConnectionCount);
        Assert.AreEqual("msa", context.LastRequest!.Identity.UserType);
        Assert.AreEqual("minecraft-connected-secret", context.LastRequest.Identity.AccessToken);
        Assert.AreEqual("profileid", context.LastRequest.Identity.Uuid);
        context.Exit.TrySetResult(0);
    }

    [TestMethod]
    public async Task SessionReplacedDuringPreparationStopsLaunch()
    {
        await using var context = await LaunchContext.CreateAsync();
        var instance = context.CreateInstance();
        context.OnPrepare = () =>
        {
            var (account, generation) = context.App.CaptureAccountState();
            Assert.IsTrue(context.App.TryCommitAccountUpdate(account!, generation,
                account! with { MinecraftAccessToken = "replaced-secret" }, () => { }));
        };

        var error = await Assert.ThrowsExactlyAsync<MinecraftLaunchException>(() => context.Coordinator.LaunchAsync(instance.Id));

        Assert.AreEqual(MinecraftLaunchError.SessionChanged, error.Error);
        Assert.AreEqual(0, context.StartCount);
        Assert.IsFalse(context.Store.IsLaunchBusy(instance.Id));
    }

    [TestMethod]
    public async Task DamagedRecordDoesNotStopRestoringOtherBuilds()
    {
        await using var context = await LaunchContext.CreateAsync();
        var damaged = context.CreateInstance(offline: true, name: "Damaged");
        var running = context.CreateInstance(offline: true, name: "Running");
        var damagedRoot = context.Store.GetInstanceDirectory(damaged.Id);
        File.WriteAllText(Path.Combine(damagedRoot, "minecraft", ".hako-running.json"), "not json");
        var runningRoot = context.Store.GetInstanceDirectory(running.Id);
        var launched = await new GameProcessRunner().StartAsync(new MinecraftLaunchPlan(
                Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 2"],
                Path.Combine(runningRoot, "minecraft"), 8),
            running.Id, runningRoot, Guid.NewGuid());
        var ended = new TaskCompletionSource<GameSessionEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Coordinator.GameSessionEnded += (_, args) => ended.TrySetResult(args);

        await context.Coordinator.RestoreRunningGamesAsync();

        Assert.AreEqual("LaunchProcessRecordInvalid", context.Coordinator.GetState(damaged.Id)!.ErrorResourceKey);
        Assert.IsFalse(context.Store.IsLaunchBusy(damaged.Id));
        Assert.AreEqual(GameRunPhase.Running, context.Coordinator.GetState(running.Id)!.Phase);
        var end = await ended.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(launched.Record, end.Record);
        await launched.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.IsNull(context.Coordinator.GetState(running.Id));
    }

    private sealed class LaunchContext : IAsyncDisposable
    {
        private readonly TemporaryDirectory _temporary = new();
        private int _prepareCount;
        private int _startCount;

        private LaunchContext()
        {
        }

        public AppCoordinator App { get; private set; } = null!;
        public RefreshAuthService Auth { get; } = new();
        public InstanceStore Store { get; private set; } = null!;
        public GameLaunchCoordinator Coordinator { get; private set; } = null!;
        public string GlobalJava { get; private set; } = "";
        public MinecraftLaunchRequest? LastRequest { get; private set; }
        public Action? OnPrepare { get; set; }
        public TaskCompletionSource<int?> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PrepareCount => Volatile.Read(ref _prepareCount);
        public int StartCount => Volatile.Read(ref _startCount);

        public static async Task<LaunchContext> CreateAsync(bool expireMinecraftToken = false, bool useGlobalJava = false)
        {
            var context = new LaunchContext();
            Directory.CreateDirectory(context._temporary.Root);
            context.GlobalJava = Path.Combine(context._temporary.Root, "Global Java", "javaw.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(context.GlobalJava)!);
            File.WriteAllText(context.GlobalJava, "");
            var time = new ManualTimeProvider();
            if (expireMinecraftToken)
            {
                time.Advance(TimeSpan.FromMinutes(90));
            }

            var settings = new UserSettingsService(new MemorySettingsStore(
                new UserSettings("en", JavaPath: useGlobalJava ? context.GlobalJava : null)));
            context.App = new AppCoordinator(settings, new MemoryAccountStore(TestData.Account()), context.Auth, time);
            await context.App.InitializeAsync();
            context.Store = new InstanceStore(context._temporary.Paths);
            await context.Store.LoadAsync();
            context.Coordinator = new GameLaunchCoordinator(
                context.Store,
                settings,
                context.App.MinecraftSessions,
                new GameProcessRunner(),
                javaValidation: null,
                prepare: context.PrepareAsync,
                start: context.StartAsync,
                timeProvider: time);
            return context;
        }

        public GameInstance CreateInstance(
            bool ready = true,
            bool offline = false,
            LaunchArgumentSource source = LaunchArgumentSource.Mojang,
            string name = "Build")
        {
            var prepared = new PreparedInstallation(
                new MojangVersionEntry { Id = "1.21", Type = "release", Url = "https://test.invalid/1.21.json", Sha1 = new string('a', 40) },
                GameArchitecture.X64,
                new MojangVersionMetadata
                {
                    Id = "1.21",
                    JavaVersion = new MojangJavaRequirement { Component = "java-runtime-delta", MajorVersion = 21 }
                },
                [],
                new MojangJavaRuntimePackage(),
                new MojangJavaRuntimeManifest(),
                [],
                []);
            var instance = Store.Create(new InstanceDraft { Name = name, OfflineMode = offline, ArgumentSource = source }, prepared);
            var root = Store.GetInstanceDirectory(instance.Id);
            Directory.CreateDirectory(Path.Combine(root, "java", "bin"));
            Directory.CreateDirectory(Path.Combine(root, "minecraft"));
            File.WriteAllText(Path.Combine(root, "java", "bin", "javaw.exe"), "");
            if (ready)
            {
                Store.CompleteInstallation(instance.Id, new InstallationResult(GameArchitecture.X64, "bin/javaw.exe", 21, false));
            }

            return Store.List().Single(candidate => candidate.Id == instance.Id);
        }

        private Task<MinecraftLaunchPlan> PrepareAsync(MinecraftLaunchRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _prepareCount);
            LastRequest = request;
            OnPrepare?.Invoke();
            return Task.FromResult(new MinecraftLaunchPlan(request.Executable, [], Path.Combine(request.InstanceRoot, "minecraft"), 21));
        }

        private Task<GameProcessLaunch> StartAsync(
            MinecraftLaunchPlan plan,
            Guid instanceId,
            string instanceRoot,
            Guid? sessionId,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startCount);
            var record = new GameProcessRecord(instanceId, 4242, 1_700_000_000, 0, sessionId);
            return Task.FromResult(new GameProcessLaunch(record, Path.Combine(instanceRoot, "minecraft", "logs", "hako-launch.log"), Exit.Task));
        }

        public async ValueTask DisposeAsync()
        {
            Exit.TrySetResult(0);
            await Store.DisposeAsync();
            await _temporary.DisposeAsync();
        }
    }
}

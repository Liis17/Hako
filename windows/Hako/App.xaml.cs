using System.Collections.Generic;
using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;
using Hako.Core.Playtime;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hako;

public partial class App : Application
{
    private static Window? _window;
    private static HakoRootLock? _previewRootLock;
    private static LauncherServices? _services;
    private static readonly BackupRestoreQueue RestoreQueue = new();
    private static int _restoreQueueReady;
    private static int _restoreQueueRunning;
    private readonly PlaytimeCoordinator? _playtime;
    private readonly AppCoordinator? _coordinator;
    private readonly MinecraftSkinPreviewService? _skinPreviewService;
    private readonly ProfilePreviewOptions? _previewOptions;
    private readonly HakoRootLock? _rootLock;
    private readonly bool _rootAlreadyOpen;
    private readonly string? _startupErrorResourceKey;

    internal static ProfilePreviewOptions? LaunchOptions { get; set; }
    internal static HakoPaths? LaunchPaths { get; set; }
    internal static string? PreviewRoot { get; set; }
    internal static string[] InitialRestorePaths { get; set; } = [];
    internal static UserSettingsService? UserSettingsService { get; private set; }
    internal static InstanceStore? InstanceStore { get; private set; }

    public App()
    {
        InitializeComponent();
        _previewOptions = LaunchOptions;
        try
        {
            LaunchPaths ??= HakoPaths.CreateProduction(AppDataDirectory.GetLocalPath());
            _rootLock = HakoRootLock.Acquire(LaunchPaths);
            if (PreviewRoot is not null)
            {
                _previewRootLock = _rootLock;
            }
        }
        catch (HakoRootInUseException)
        {
            _rootAlreadyOpen = true;
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _startupErrorResourceKey = "StartupStorageUnavailable";
            return;
        }

        _skinPreviewService = new MinecraftSkinPreviewService();
        _playtime = _previewOptions is null
            ? new PlaytimeCoordinator(LaunchPaths!.GameRoot, new PlaytimeHelperLauncher())
            : PlaytimeCoordinator.CreatePreview(LaunchPaths!.GameRoot);
        if (_previewOptions is not null)
        {
            var settingsService = new UserSettingsService(new InMemoryPreviewUserSettingsStore());
            UserSettingsService = settingsService;
            _coordinator = new AppCoordinator(
                settingsService,
                new InMemoryPreviewAccountStore(_previewOptions.CreateAccount()),
                new PreviewAuthService(),
                playtimeTransfer: _playtime);
        }
        else
        {
            var settingsService = new UserSettingsService(new JsonUserSettingsStore(LaunchPaths!.DataRoot));
            UserSettingsService = settingsService;
            _coordinator = new AppCoordinator(
                settingsService,
                new AccountSessionStore(LaunchPaths!.DataRoot),
                new MicrosoftDeviceAuth(new HttpClient(), TimeProvider.System),
                playtimeTransfer: _playtime);
        }

    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        if (_rootAlreadyOpen)
        {
            HakoStrings.SetLanguage(AppLanguages.Default, true);
            _window = new Window
            {
                Title = "Hako",
                Content = new TextBlock
                {
                    Text = HakoStrings.Get("HakoAlreadyOpen"),
                    HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
                    VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(32)
                }
            };
            _window.Activate();
            return;
        }

        if (_startupErrorResourceKey is not null)
        {
            ShowStartupError();
            return;
        }

        InstanceStore = new InstanceStore(LaunchPaths!);
        await InstanceStore.LoadAsync();

        var previewBuilds = _previewOptions?.Mode == ProfilePreviewMode.Builds;
        var previewModrinthHttpClient = previewBuilds ? PreviewBuildsFixture.CreateContentHttpClient() : null;
        var mojang = previewBuilds ? new MojangClient(previewModrinthHttpClient) : new MojangClient();
        var fabric = previewBuilds
            ? new FabricClient(previewModrinthHttpClient, paths: LaunchPaths,
                metaBase: PreviewBuildsFixture.FabricMetaBase, modrinthBase: PreviewBuildsFixture.ModrinthBase)
            : new FabricClient(paths: LaunchPaths);
        var modrinth = fabric.Modrinth;
        var modpacks = new ModpackInstaller(modrinth, mojang, fabric, LaunchPaths!);
        var modpackCatalog = new ModpackCatalog(modrinth);
        var installations = previewBuilds
            ? PreviewBuildsFixture.CreateInstallations(InstanceStore)
            : new InstallationCoordinator(InstanceStore, mojang, new MinecraftInstaller(mojang, fabric), modpacks);
        var previewInstance = previewBuilds && InstanceStore.LoadError is null
            ? PreviewBuildsFixture.Seed(InstanceStore, _previewOptions!.Page)
            : (Guid?)null;
        var games = new GameLaunchCoordinator(InstanceStore, UserSettingsService!, _coordinator!.MinecraftSessions,
            new GameProcessRunner(), playtime: _playtime);
        var content = new InstanceContent(InstanceStore);
        var contentController = new InstanceContentController(InstanceStore, content, modrinth, fabric,
            confirmSelection: HakoDialogs.ConfirmContentAsync);
        var worlds = new InstanceWorlds(InstanceStore);
        var screenshots = new InstanceScreenshots(InstanceStore);
        var backups = new InstanceBackup(InstanceStore, content);
        var worldBackups = new WorldBackup(InstanceStore, content, worlds);
        var restores = new RestoreArchive(InstanceStore);
        _services = new LauncherServices(_coordinator, UserSettingsService!, InstanceStore, installations, games, mojang, fabric,
            modrinth, modpacks, modpackCatalog, content, contentController, worlds, screenshots, backups,
            worldBackups, restores, previewModrinthHttpClient, _skinPreviewService!, _previewOptions, _playtime!);

        _window = new MainWindow(_services);
        _window.Activate();

        await _coordinator.InitializeAsync();
        await _playtime!.RecoverPendingTransferAsync(_coordinator.Account?.Xuid);
        if (InstanceStore.LoadError is null)
        {
            await StartBuildServicesAsync(installations, games);
        }
        await _playtime.RefreshAsync();
        _playtime.Start();

        if (_previewOptions is not null)
        {
            if (previewInstance is { } fixtureInstance) _playtime.SeedPreviewInstance(fixtureInstance);
            if (_previewOptions.Mode == ProfilePreviewMode.Guest)
            {
                _coordinator.ContinueWithoutAccount();
            }

            switch (_previewOptions.Page)
            {
                case PreviewPage.Builds:
                    _coordinator.OpenBuilds();
                    break;
                case PreviewPage.Create:
                    _coordinator.OpenInstanceCreation();
                    break;
                case PreviewPage.Modpacks:
                case PreviewPage.ModpackCreate:
                    _coordinator.OpenModpackCatalog();
                    break;
                case PreviewPage.Profile when previewInstance is { } id:
                    _coordinator.OpenInstance(id);
                    break;
                case PreviewPage.Content or PreviewPage.ResourcePacks or PreviewPage.Catalog
                    or PreviewPage.Worlds or PreviewPage.Screenshots or PreviewPage.Datapacks or PreviewPage.DatapackCatalog
                    when previewInstance is { } id:
                    _coordinator.OpenInstance(id);
                    break;
                case PreviewPage.SettingsGame:
                    _coordinator.OpenSettings(SettingsSection.Game);
                    break;
                case PreviewPage.Playtime:
                    _coordinator.OpenSettings(SettingsSection.Profile);
                    break;
                default:
                    if (_previewOptions.Mode != ProfilePreviewMode.Builds)
                    {
                        _coordinator.OpenSettings(SettingsSection.Profile);
                    }

                    break;
            }
        }

        if (InitialRestorePaths.Length > 0 && _coordinator.Account is null)
        {
            _coordinator.ContinueWithoutAccount();
        }

        Volatile.Write(ref _restoreQueueReady, 1);
        QueueRestoreFiles(InitialRestorePaths);
        InitialRestorePaths = [];
        StartRestoreQueue();
    }

    internal static void QueueRestoreFiles(IEnumerable<string> paths)
    {
        var added = false;
        foreach (var path in paths) added |= RestoreQueue.Enqueue(path);
        if (added) StartRestoreQueue();
    }

    internal static void RestoreWindowReady() => StartRestoreQueue();

    private static void StartRestoreQueue()
    {
        if (Volatile.Read(ref _restoreQueueReady) == 0
            || HakoDialogs.Root is null
            || Interlocked.CompareExchange(ref _restoreQueueRunning, 1, 0) != 0)
        {
            return;
        }

        _ = ProcessRestoreQueueAsync();
    }

    private static async Task ProcessRestoreQueueAsync()
    {
        RestoreNavigation? lastSuccess = null;
        var completedEntries = new List<BackupRestoreQueueEntry>();
        try
        {
            var store = _services?.Store;
            if (store is null || !store.IsLoaded || store.LoadError is not null)
            {
                var message = HakoStrings.Get("RestoreRegistryUnavailable");
                var loadError = store?.LoadError;
                if (loadError is not null) message += "\n" + HakoStrings.Get(loadError.ResourceKey);
                await HakoDialogs.ShowErrorAsync(message);
                while (RestoreQueue.TryDequeue(out var pending)) RestoreQueue.Complete(pending);
                return;
            }

            while (RestoreQueue.TryDequeue(out var entry))
            {
                completedEntries.Add(entry);
                try
                {
                    if (_services is not { } services) continue;
                    if (services.App.Account is null && !services.App.IsGuest)
                    {
                        services.App.ContinueWithoutAccount();
                    }

                    var success = await BackupRestoreDialog.RestoreAsync(services, entry.Path);
                    if (success is not null) lastSuccess = success;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or ArgumentException or InvalidOperationException)
                {
                    await HakoDialogs.ShowErrorAsync(HakoStrings.Get("RestoreFailureOperationFailed"));
                }
            }
        }
        finally
        {
            try
            {
                if (lastSuccess is not null && _window is MainWindow mainWindow)
                {
                    await mainWindow.NavigateToRestoreAsync(lastSuccess);
                }
            }
            finally
            {
                foreach (var entry in completedEntries) RestoreQueue.Complete(entry);
                Interlocked.Exchange(ref _restoreQueueRunning, 0);
                if (RestoreQueue.HasPending) StartRestoreQueue();
            }
        }
    }

    private static async Task StartBuildServicesAsync(InstallationCoordinator installations, GameLaunchCoordinator games)
    {
        try
        {
            await installations.StartAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Could not start the installation queue: {exception}");
        }

        try
        {
            await games.RestoreRunningGamesAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Could not restore running games: {exception}");
        }
    }

    internal static WindowId? MainWindowId => _window?.AppWindow.Id;

    internal static Window? CurrentWindow => _window;

    /// <summary>Stops app-lifetime services before the window closes. Exiting does not pause the installation queue.</summary>
    internal static async Task ShutdownServicesAsync()
    {
        if (_services is not { } services)
        {
            return;
        }

        _services = null;
        try { await services.Playtime.StopAsync(); } catch (Exception) { }
        services.ModpackCatalog.Dispose();
        try { await services.Installations.DisposeAsync(); } catch (Exception) { }
        try { await services.Store.DisposeAsync(); } catch (Exception) { }
        services.Mojang.Dispose();
        services.Fabric.Dispose();
        services.PreviewModrinthHttpClient?.Dispose();
    }

    internal static void ActivateMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        _window.AppWindow.Show();
        _window.Activate();
    }

    internal static void CleanupPreviewRoot()
    {
        if (PreviewRoot is null)
        {
            return;
        }

        _previewRootLock?.Dispose();
        _previewRootLock = null;
        try
        {
            new RecycleBin().PermanentDelete(PreviewRoot, Path.GetDirectoryName(PreviewRoot)!);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (ArgumentException)
        {
        }

        PreviewRoot = null;
    }

    private static void ShowStartupError()
    {
        HakoStrings.SetLanguage(AppLanguages.Default, true);
        _window = new Window
        {
            Title = "Hako",
            Content = new TextBlock
            {
                Text = HakoStrings.Get("StartupStorageUnavailable"),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(32)
            }
        };
        _window.Activate();
    }

}

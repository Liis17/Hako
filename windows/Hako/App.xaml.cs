using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;
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
    private readonly AppCoordinator? _coordinator;
    private readonly MinecraftSkinPreviewService? _skinPreviewService;
    private readonly ProfilePreviewOptions? _previewOptions;
    private readonly HakoRootLock? _rootLock;
    private readonly bool _rootAlreadyOpen;
    private readonly string? _startupErrorResourceKey;

    internal static ProfilePreviewOptions? LaunchOptions { get; set; }
    internal static HakoPaths? LaunchPaths { get; set; }
    internal static string? PreviewRoot { get; set; }
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
        if (_previewOptions is not null)
        {
            var settingsService = new UserSettingsService(new InMemoryPreviewUserSettingsStore());
            UserSettingsService = settingsService;
            _coordinator = new AppCoordinator(
                settingsService,
                new InMemoryPreviewAccountStore(_previewOptions.CreateAccount()),
                new PreviewAuthService());
        }
        else
        {
            var settingsService = new UserSettingsService(new JsonUserSettingsStore(LaunchPaths!.DataRoot));
            UserSettingsService = settingsService;
            _coordinator = new AppCoordinator(
                settingsService,
                new AccountSessionStore(LaunchPaths!.DataRoot),
                new MicrosoftDeviceAuth(new HttpClient(), TimeProvider.System));
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
        var mojang = previewBuilds ? PreviewBuildsFixture.CreateMojangClient() : new MojangClient();
        var previewModrinthHttpClient = previewBuilds ? PreviewBuildsFixture.CreateContentHttpClient() : null;
        var fabric = previewBuilds
            ? new FabricClient(previewModrinthHttpClient, paths: LaunchPaths,
                metaBase: PreviewBuildsFixture.FabricMetaBase, modrinthBase: PreviewBuildsFixture.ModrinthBase)
            : new FabricClient(paths: LaunchPaths);
        var modrinth = fabric.Modrinth;
        var installations = previewBuilds
            ? PreviewBuildsFixture.CreateInstallations(InstanceStore)
            : new InstallationCoordinator(InstanceStore, mojang, new MinecraftInstaller(mojang, fabric));
        var previewInstance = previewBuilds && InstanceStore.LoadError is null
            ? PreviewBuildsFixture.Seed(InstanceStore, _previewOptions!.Page)
            : (Guid?)null;
        var games = new GameLaunchCoordinator(InstanceStore, UserSettingsService!, _coordinator!.MinecraftSessions, new GameProcessRunner());
        var content = new InstanceContent(InstanceStore);
        var contentController = new InstanceContentController(InstanceStore, content, modrinth, fabric,
            HakoDialogs.ConfirmContentAsync);
        _services = new LauncherServices(_coordinator, UserSettingsService!, InstanceStore, installations, games, mojang, fabric,
            modrinth, content, contentController, previewModrinthHttpClient, _skinPreviewService!, _previewOptions);

        _window = new MainWindow(_services);
        _window.Activate();

        await _coordinator.InitializeAsync();
        if (InstanceStore.LoadError is null)
        {
            _ = StartBuildServicesAsync(installations, games);
        }

        if (_previewOptions is not null)
        {
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
                case PreviewPage.Profile when previewInstance is { } id:
                    _coordinator.OpenInstance(id);
                    break;
                case PreviewPage.SettingsGame:
                    _coordinator.OpenSettings(SettingsSection.Game);
                    break;
                default:
                    if (_previewOptions.Mode != ProfilePreviewMode.Builds)
                    {
                        _coordinator.OpenSettings(SettingsSection.Profile);
                    }

                    break;
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

using Hako.Core;
using Hako.Core.Instances;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hako;

public partial class App : Application
{
    private static Window? _window;
    private static HakoRootLock? _previewRootLock;
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

        _window = new MainWindow(_coordinator!, _skinPreviewService!, _previewOptions);
        _window.Activate();

        await _coordinator!.InitializeAsync();
        if (_previewOptions is not null)
        {
            if (_previewOptions.Mode == ProfilePreviewMode.Guest)
            {
                _coordinator.ContinueWithoutAccount();
                _coordinator.OpenSettings(SettingsSection.Profile);
            }
            else
            {
                _coordinator.OpenSettings(SettingsSection.Profile);
            }
        }
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

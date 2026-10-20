using Hako.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Hako;

public partial class App : Application
{
    private static Window? _window;
    private readonly AppCoordinator _coordinator;
    private readonly MinecraftSkinPreviewService _skinPreviewService;
    private readonly ProfilePreviewOptions? _previewOptions;

    internal static ProfilePreviewOptions? LaunchOptions { get; set; }

    public App()
    {
        InitializeComponent();
        _previewOptions = LaunchOptions;
        _skinPreviewService = new MinecraftSkinPreviewService();
        if (_previewOptions is not null)
        {
            _coordinator = new AppCoordinator(
                new InMemoryPreviewUserSettingsStore(),
                new InMemoryPreviewAccountStore(_previewOptions.CreateAccount()),
                new PreviewAuthService());
        }
        else
        {
            var dataDirectory = AppDataDirectory.GetLocalPath();
            _coordinator = new AppCoordinator(
                new JsonUserSettingsStore(dataDirectory),
                new AccountSessionStore(dataDirectory),
                new MicrosoftDeviceAuth(new HttpClient(), TimeProvider.System));
        }
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(_coordinator, _skinPreviewService, _previewOptions);
        _window.Activate();

        await _coordinator.InitializeAsync();
        if (_previewOptions is not null)
        {
            if (_previewOptions.Mode == ProfilePreviewMode.Guest)
            {
                _coordinator.ContinueWithoutAccount();
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
}

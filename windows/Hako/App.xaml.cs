using Hako.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Hako;

public partial class App : Application
{
    private static Window? _window;
    private readonly AppCoordinator _coordinator;

    public App()
    {
        InitializeComponent();
        var dataDirectory = AppDataDirectory.GetLocalPath();
        _coordinator = new AppCoordinator(
            new JsonUserSettingsStore(dataDirectory),
            new AccountSessionStore(dataDirectory),
            new MicrosoftDeviceAuth(new HttpClient(), TimeProvider.System));
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(_coordinator);
        _window.Activate();

        await _coordinator.InitializeAsync();
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

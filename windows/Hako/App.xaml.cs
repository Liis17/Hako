using Hako.Core;
using Microsoft.UI.Xaml;

namespace Hako;

public partial class App : Application
{
    private static Window? _window;
    private readonly AppCoordinator _coordinator;

    public App()
    {
        InitializeComponent();
        _coordinator = new AppCoordinator(new JsonUserSettingsStore(AppDataDirectory.GetLocalPath()));
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(_coordinator);
        _window.Activate();

        try
        {
            await _coordinator.InitializeAsync();
        }
        catch
        {
            _coordinator.ShowWelcomeAfterStartupFailure();
        }
    }

    internal static void ActivateMainWindow()
    {
        _window?.Activate();
    }
}

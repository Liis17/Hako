using Hako.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace Hako;

public sealed partial class MainWindow : Window
{
    private readonly AppCoordinator _coordinator;
    private bool _changingSelection;

    public MainWindow(AppCoordinator coordinator)
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SetInitialWindowSize();

        _coordinator = coordinator;
        _coordinator.PropertyChanged += Coordinator_PropertyChanged;
        Closed += (_, _) => _coordinator.CancelSignIn();
        HakoStrings.SetLanguage(_coordinator.Language, true);
        UpdateNavigationLabels();
        ShowScreen();
    }

    private void SetInitialWindowSize()
    {
        var scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96d;
        var initialWindowSize = AppWindow.Size;
        var initialClientSize = AppWindow.ClientSize;
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(1280 * scale) + initialWindowSize.Width - initialClientSize.Width,
            (int)Math.Round(720 * scale) + initialWindowSize.Height - initialClientSize.Height));

        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea;
        if (workArea is not { } area)
        {
            return;
        }

        var windowSize = AppWindow.Size;
        var width = Math.Min(windowSize.Width, area.Width);
        var height = Math.Min(windowSize.Height, area.Height);
        if (width != windowSize.Width || height != windowSize.Height)
        {
            AppWindow.Resize(new SizeInt32(width, height));
            windowSize = AppWindow.Size;
        }

        AppWindow.MoveAndResize(new RectInt32(
            area.X + (area.Width - windowSize.Width) / 2,
            area.Y + (area.Height - windowSize.Height) / 2,
            windowSize.Width,
            windowSize.Height));
    }

    private void Coordinator_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppCoordinator.Language))
        {
            HakoStrings.SetLanguage(_coordinator.Language);
            UpdateNavigationLabels();
        }

        if (e.PropertyName == nameof(AppCoordinator.Screen))
        {
            ShowScreen();
        }
        else if (e.PropertyName == nameof(AppCoordinator.SelectedSettingsSection)
                 && ShellFrame.Content is SettingsPage settingsPage)
        {
            settingsPage.SelectSection(_coordinator.SelectedSettingsSection);
        }
        else if (e.PropertyName == nameof(AppCoordinator.IsGuest)
                 && ShellFrame.Content is SettingsPage currentSettingsPage)
        {
            currentSettingsPage.RefreshProfile();
        }
        else if (e.PropertyName == nameof(AppCoordinator.Account)
                 && ShellFrame.Content is SettingsPage accountSettingsPage)
        {
            accountSettingsPage.RefreshProfile();
        }
        else if (e.PropertyName == nameof(AppCoordinator.CurrentStartupNotice)
                 && FlowFrame.Content is WelcomePage welcomePage)
        {
            welcomePage.RefreshStartupNotice();
        }
    }

    private void ShowScreen()
    {
        if (_coordinator.Screen is LauncherScreen.Builds or LauncherScreen.Settings)
        {
            FlowFrame.Visibility = Visibility.Collapsed;
            RootNavigation.Visibility = Visibility.Visible;
            _changingSelection = true;
            RootNavigation.SelectedItem = _coordinator.Screen == LauncherScreen.Builds
                ? BuildsNavigationItem
                : SettingsNavigationItem;
            _changingSelection = false;

            if (_coordinator.Screen == LauncherScreen.Builds)
            {
                ShellFrame.Navigate(typeof(BuildsPage), _coordinator);
            }
            else
            {
                ShellFrame.Navigate(typeof(SettingsPage), _coordinator);
            }

            return;
        }

        RootNavigation.Visibility = Visibility.Collapsed;
        FlowFrame.Visibility = Visibility.Visible;
        var page = _coordinator.Screen switch
        {
            LauncherScreen.Login => typeof(LoginPage),
            LauncherScreen.Welcome => typeof(WelcomePage),
            _ => typeof(LoadingPage)
        };
        FlowFrame.Navigate(page, _coordinator);
    }

    private void RootNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_changingSelection || args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        if (tag == "Builds")
        {
            _coordinator.OpenBuilds();
        }
        else if (tag == "Settings")
        {
            _coordinator.OpenSettings();
        }
    }

    private void UpdateNavigationLabels()
    {
        BuildsNavigationItem.Content = HakoStrings.Get("BuildsTitle");
        SettingsNavigationItem.Content = HakoStrings.Get("SettingsTitle");
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}

using Hako.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hako;

public sealed partial class MainWindow : Window
{
    private readonly AppCoordinator _coordinator;
    private bool _changingSelection;

    public MainWindow(AppCoordinator coordinator)
    {
        InitializeComponent();
        _coordinator = coordinator;
        _coordinator.PropertyChanged += Coordinator_PropertyChanged;
        HakoStrings.SetLanguage(_coordinator.Language, true);
        UpdateNavigationLabels();
        ShowScreen();
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
}

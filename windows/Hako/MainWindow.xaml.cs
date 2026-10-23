using System.Collections.Generic;
using System.Linq;
using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace Hako;

public sealed partial class MainWindow : Window
{
    private readonly LauncherServices _services;
    private readonly AppCoordinator _coordinator;
    private readonly MinecraftSkinPreviewService _skinPreviewService;
    private readonly ProfilePreviewOptions? _previewOptions;
    private readonly Dictionary<Guid, NavigationViewItem> _instanceItems = [];
    private readonly Dictionary<Guid, Guid> _instanceIconRevisions = [];
    private readonly CoalescedRefresh _instancesRefresh;
    private bool _changingSelection;
    private bool _windowActive;
    private bool _windowVisible;
    private bool _closed;
    private bool _closeConfirmed;
    private bool _closeRequested;
    private bool _navigating;
    private Guid? _shownInstanceId;

    internal MainWindow(LauncherServices services)
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _services = services;
        _coordinator = services.App;
        _skinPreviewService = services.SkinPreview;
        _previewOptions = services.PreviewOptions;
        _instancesRefresh = new CoalescedRefresh(DispatcherQueue, RefreshInstanceItems);
        SetInitialWindowSize();
        _coordinator.PropertyChanged += Coordinator_PropertyChanged;
        _services.Store.InstancesChanged += Store_InstancesChanged;
        RootGrid.Loaded += (_, _) =>
        {
            if (RootGrid.XamlRoot is { } root) HakoDialogs.AttachWindow(DispatcherQueue, root);
        };
        Activated += Window_Activated;
        VisibilityChanged += Window_VisibilityChanged;
        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += AppWindow_Closing;
        Closed += Window_Closed;
        HakoStrings.SetLanguage(_coordinator.Language, true);
        UpdateNavigationLabels();
        RefreshInstanceItems();
        ShowScreen();
        UpdateSkinAvailability();
    }

    private void SetInitialWindowSize()
    {
        var scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96d;
        var initialWindowSize = AppWindow.Size;
        var initialClientSize = AppWindow.ClientSize;
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round((_previewOptions?.ClientWidth ?? 1280) * scale) + initialWindowSize.Width - initialClientSize.Width,
            (int)Math.Round((_previewOptions?.ClientHeight ?? 720) * scale) + initialWindowSize.Height - initialClientSize.Height));

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
            RefreshInstanceItems();
        }

        if (e.PropertyName == nameof(AppCoordinator.Screen))
        {
            ShowScreen();
        }
        else if (e.PropertyName == nameof(AppCoordinator.SelectedInstanceId)
                 && _coordinator.Screen == LauncherScreen.Instance)
        {
            ShowScreen();
        }
        else if (e.PropertyName == nameof(AppCoordinator.SelectedSettingsSection)
                 && ShellFrame.Content is SettingsPage settingsPage)
        {
            settingsPage.SelectSection(_coordinator.SelectedSettingsSection);
            UpdateSelection();
            UpdateSkinAvailability();
        }
        else if (e.PropertyName is nameof(AppCoordinator.IsGuest) or nameof(AppCoordinator.Account))
        {
            UpdateNavigationLabels();
            if (ShellFrame.Content is SettingsPage currentSettingsPage)
            {
                currentSettingsPage.RefreshProfile();
                UpdateSkinAvailability();
            }
        }
        else if (e.PropertyName == nameof(AppCoordinator.CurrentStartupNotice)
                 && FlowFrame.Content is WelcomePage welcomePage)
        {
            welcomePage.RefreshStartupNotice();
        }
    }

    private void ShowScreen()
    {
        var screen = _coordinator.Screen;
        if (screen is LauncherScreen.Builds or LauncherScreen.Instance or LauncherScreen.InstanceCreation
            or LauncherScreen.ModpackCatalog or LauncherScreen.ModpackCreation or LauncherScreen.Settings)
        {
            FlowFrame.Visibility = Visibility.Collapsed;
            RootNavigation.Visibility = Visibility.Visible;
            UpdateSelection();

            switch (screen)
            {
                case LauncherScreen.Builds when ShellFrame.Content is not BuildsPage:
                    ShellFrame.Navigate(typeof(BuildsPage), _services);
                    break;
                case LauncherScreen.Instance when _coordinator.SelectedInstanceId is { } id
                                                  && (ShellFrame.Content is not InstanceProfilePage || _shownInstanceId != id):
                    _shownInstanceId = id;
                    ShellFrame.Navigate(typeof(InstanceProfilePage), new InstanceProfileContext(_services, id));
                    break;
                case LauncherScreen.InstanceCreation when ShellFrame.Content is not InstanceCreationPage:
                    ShellFrame.Navigate(typeof(InstanceCreationPage), _services);
                    break;
                case LauncherScreen.ModpackCatalog when ShellFrame.Content is not ModpackCatalogPage:
                    ShellFrame.Navigate(typeof(ModpackCatalogPage), _services);
                    break;
                case LauncherScreen.ModpackCreation when ShellFrame.Content is not ModpackCreationPage:
                    ShellFrame.Navigate(typeof(ModpackCreationPage), new ModpackCreationContext(_services, _pendingModpack));
                    break;
                case LauncherScreen.Settings when ShellFrame.Content is not SettingsPage:
                    ShellFrame.Navigate(
                        typeof(SettingsPage),
                        new SettingsPageContext(_coordinator, _skinPreviewService, _previewOptions, _services.Settings, _services.Playtime));
                    break;
            }

            if (screen != LauncherScreen.Instance)
            {
                _shownInstanceId = null;
            }

            UpdateSkinAvailability();
            return;
        }

        _shownInstanceId = null;
        RootNavigation.Visibility = Visibility.Collapsed;
        UpdateSkinAvailability();
        FlowFrame.Visibility = Visibility.Visible;
        var page = screen switch
        {
            LauncherScreen.Login => typeof(LoginPage),
            LauncherScreen.Welcome => typeof(WelcomePage),
            _ => typeof(LoadingPage)
        };
        FlowFrame.Navigate(page, _coordinator);
    }

    private void UpdateSelection()
    {
        _changingSelection = true;
        RootNavigation.SelectedItem = _coordinator.Screen switch
        {
            LauncherScreen.Builds => BuildsNavigationItem,
            LauncherScreen.Instance when _coordinator.SelectedInstanceId is { } id => _instanceItems.GetValueOrDefault(id),
            LauncherScreen.InstanceCreation or LauncherScreen.ModpackCatalog or LauncherScreen.ModpackCreation => CreateNavigationItem,
            LauncherScreen.Settings => _coordinator.SelectedSettingsSection == SettingsSection.Profile
                ? AccountNavigationItem
                : SettingsNavigationItem,
            _ => null
        };
        _changingSelection = false;
    }

    private void Store_InstancesChanged(object? sender, EventArgs e) => _instancesRefresh.Request();

    /// <summary>Updates rail items in place so selection and the shown page survive store notifications.</summary>
    private void RefreshInstanceItems()
    {
        if (_closed)
        {
            return;
        }

        var instances = _services.Store.Instances.OrderBy(instance => instance.CreatedAt).ThenBy(instance => instance.Id).ToList();
        var present = instances.Select(instance => instance.Id).ToHashSet();
        _changingSelection = true;
        try
        {
            foreach (var removed in _instanceItems.Keys.Where(id => !present.Contains(id)).ToList())
            {
                RootNavigation.MenuItems.Remove(_instanceItems[removed]);
                _instanceItems.Remove(removed);
                _instanceIconRevisions.Remove(removed);
            }

            for (var index = 0; index < instances.Count; index++)
            {
                var instance = instances[index];
                if (!_instanceItems.TryGetValue(instance.Id, out var item))
                {
                    item = new NavigationViewItem { Tag = instance.Id };
                    _instanceItems[instance.Id] = item;
                }

                item.Content = instance.Name;
                ToolTipService.SetToolTip(item, $"{instance.Name}\n{InstanceText.Details(instance)}");
                if (!_instanceIconRevisions.TryGetValue(instance.Id, out var revision) || revision != instance.IconRevision
                    || item.Icon is null)
                {
                    _instanceIconRevisions[instance.Id] = instance.IconRevision;
                    item.Icon = new FontIcon { Glyph = InstanceIconGlyphs.Glyph(instance.IconSymbol), FontFamily = new FontFamily("Segoe Fluent Icons") };
                    _ = LoadNavigationIconAsync(item, instance);
                }

                // Builds follow the "Сборки" item and its separator.
                var target = index + 2;
                var current = RootNavigation.MenuItems.IndexOf(item);
                if (current != target)
                {
                    if (current >= 0)
                    {
                        RootNavigation.MenuItems.RemoveAt(current);
                    }

                    RootNavigation.MenuItems.Insert(target, item);
                }
            }

            BuildsSeparator.Visibility = instances.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            CreateNavigationItem.IsEnabled = _services.Store.LoadError is null;
        }
        finally
        {
            _changingSelection = false;
        }

        if (_coordinator.SelectedInstanceId is { } selected && !present.Contains(selected))
        {
            _coordinator.CloseInstance(selected);
        }

        UpdateSelection();
    }

    private async Task LoadNavigationIconAsync(NavigationViewItem item, GameInstance instance)
    {
        var data = await InstanceIconSource.ReadAsync(_services.Store, instance);
        if (data is null || await InstanceIconSource.CreateAsync(data) is not { } image)
        {
            return;
        }

        if (_instanceIconRevisions.GetValueOrDefault(instance.Id) == instance.IconRevision)
        {
            item.Icon = new ImageIcon { Source = image };
        }
    }

    private void RootNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_changingSelection || args.SelectedItemContainer?.Tag is not { } tag)
        {
            return;
        }

        Action? action = tag switch
        {
            "Builds" => _coordinator.OpenBuilds,
            "Settings" => () => _coordinator.OpenSettings(),
            "Account" => () => _coordinator.OpenSettings(SettingsSection.Profile),
            Guid id => () => _coordinator.OpenInstance(id),
            _ => null
        };
        if (action is not null)
        {
            _ = NavigateAsync(action);
        }
    }

    private void RootNavigation_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (_changingSelection) return;
        if (args.InvokedItemContainer == CreateNavigationItem)
        {
            _ = NavigateAsync(null, chooseCreation: true);
        }
    }

    /// <summary>Asks the current page about unsaved input before leaving it; restores the rail selection when it stays.</summary>
    private async Task NavigateAsync(Action? action, bool chooseCreation = false)
    {
        if (_navigating)
        {
            UpdateSelection();
            return;
        }

        _navigating = true;
        try
        {
            UpdateSelection();
            if (chooseCreation && ShellFrame.Content is InstanceCreationPage)
            {
                return;
            }

            if (ShellFrame.Content is INavigationGuard guard && !await guard.ConfirmLeaveAsync())
            {
                return;
            }

            if (chooseCreation)
            {
                var choice = await InstanceCreationChoiceDialog.ShowAsync();
                if (choice == InstanceCreationChoice.Custom)
                {
                    _coordinator.OpenInstanceCreation();
                }
                else if (choice == InstanceCreationChoice.Modpack)
                {
                    _coordinator.OpenModpackCatalog();
                }

                return;
            }

            action?.Invoke();
        }
        finally
        {
            _navigating = false;
            UpdateSelection();
        }
    }

    /// <summary>Opens the build creation flow from a page (the empty state of Сборки) with the same guard as the rail.</summary>
    internal static void RequestCreation(Window? window)
    {
        if (window is MainWindow main)
        {
            _ = main.NavigateAsync(null, chooseCreation: true);
        }
    }

    private ModpackSelection? _pendingModpack;

    internal static void OpenModpackCreation(Window? window, ModrinthProject project, ModrinthVersion version, string? minecraftFilter)
    {
        if (window is MainWindow main)
        {
            main._pendingModpack = new ModpackSelection(project, version, minecraftFilter);
            main._coordinator.OpenModpackCreation();
        }
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed)
        {
            return;
        }

        args.Cancel = true;
        if (_closeRequested)
        {
            return;
        }

        _closeRequested = true;
        HakoDialogs.BeginWindowClose();
        try
        {
            if (RootNavigation.Visibility == Visibility.Visible
                && ShellFrame.Content is INavigationGuard guard
                && !await guard.ConfirmLeaveAsync())
            {
                return;
            }

            _closeConfirmed = true;
            await App.ShutdownServicesAsync();
            Close();
        }
        finally
        {
            _closeRequested = false;
            if (!_closeConfirmed) HakoDialogs.EndWindowClose();
        }
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs e)
    {
        var wasActive = _windowActive;
        _windowActive = e.WindowActivationState != WindowActivationState.Deactivated;
        if (!wasActive && _windowActive && ShellFrame.Content is InstanceProfilePage profilePage)
            profilePage.WindowReturned();
        UpdateSkinAvailability();
    }

    private void Window_VisibilityChanged(object sender, WindowVisibilityChangedEventArgs e)
    {
        _windowVisible = e.Visible;
        UpdateSkinAvailability();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs e)
    {
        if (e.DidVisibilityChange || e.DidPresenterChange)
        {
            _windowVisible = sender.IsVisible;
            UpdateSkinAvailability();
        }
    }

    private void Window_Closed(object sender, WindowEventArgs e)
    {
        _closed = true;
        HakoDialogs.CloseWindow();
        _windowActive = false;
        _windowVisible = false;
        _instancesRefresh.Stop();
        _coordinator.PropertyChanged -= Coordinator_PropertyChanged;
        _services.Store.InstancesChanged -= Store_InstancesChanged;
        AppWindow.Changed -= AppWindow_Changed;
        AppWindow.Closing -= AppWindow_Closing;
        _coordinator.CancelSignIn();
        UpdateSkinAvailability();
        _skinPreviewService.Dispose();
    }

    private void UpdateSkinAvailability()
    {
        if (ShellFrame.Content is not SettingsPage settingsPage)
        {
            return;
        }

        var settingsVisible = !_closed
            && _coordinator.Screen == LauncherScreen.Settings
            && RootNavigation.Visibility == Visibility.Visible;
        settingsPage.SetScreenVisible(settingsVisible);
        var windowVisible = !_closed && _windowVisible
            && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        settingsPage.SetWindowState(windowVisible, windowVisible && _windowActive);
    }

    private void UpdateNavigationLabels()
    {
        BuildsNavigationItem.Content = HakoStrings.Get("BuildsTitle");
        SettingsNavigationItem.Content = HakoStrings.Get("SettingsTitle");
        CreateNavigationItem.Content = HakoStrings.Get("CreateBuildButton");
        ToolTipService.SetToolTip(CreateNavigationItem, HakoStrings.Get("CreateBuildButton"));
        AccountNavigationItem.Content = _coordinator.Account?.MinecraftName
            ?? _coordinator.Account?.Gamertag
            ?? HakoStrings.Get(_coordinator.IsGuest ? "GuestProfileTitle" : "ProfileTitle");
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}

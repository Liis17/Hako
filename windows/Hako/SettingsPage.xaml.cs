using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;
using Hako.Core.Playtime;
using Hako.Core.Updates;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using Windows.UI;

namespace Hako;

internal sealed record SettingsPageContext(
    AppCoordinator Coordinator,
    MinecraftSkinPreviewService SkinPreviewService,
    ProfilePreviewOptions? PreviewOptions,
    UserSettingsService Settings,
    PlaytimeCoordinator Playtime,
    WindowsAppUpdateService Updates,
    InstanceStore Store);

public sealed partial class SettingsPage : Page, INavigationGuard
{
    private AppCoordinator? _coordinator;
    private MinecraftSkinPreviewService? _skinPreviewService;
    private ProfilePreviewOptions? _previewOptions;
    private bool _updatingSelection;
    private bool _updatingLanguage;
    private bool _windowVisible;
    private bool _windowActive;
    private bool _screenVisible;
    private bool _languageChangedSubscribed;
    private SettingsSection _selectedSection = SettingsSection.General;
    private string? _modelAccountId;
    private string? _modelProfileId;
    private Guid _profileRequestScope;
    private bool? _isWideProfileLayout;
    private UserSettingsService? _settings;
    private PlaytimeCoordinator? _playtime;
    private InstanceStore? _store;
    private InstanceStorageUsage? _storageUsage;
    private DiskSpaceInfo? _diskSpaceInfo;
    private CancellationTokenSource? _storageCancellation;
    private long _storageLoadGeneration;
    private bool _diskLoading;
    private bool _diskFailed;
    private bool _storageUsageLoading;
    private bool _storageUsageFailed;
    private DispatcherQueueTimer? _gameSaveTimer;
    private bool _loadingGame;
    private bool _pickingJava;
    private bool _editingCharacter;
    private WindowsAppUpdateService? _updates;
    private DispatcherQueueTimer? _appUpdateRefresh;
    private bool _refreshingAppUpdates;

    public SettingsPage()
    {
        InitializeComponent();
        SkinView.HeadImageReady += SkinView_HeadImageReady;
        CharacterEditor.StateChanged += (_, _) => SettingsSections.IsEnabled = !CharacterEditor.IsSaving;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var context = (SettingsPageContext)e.Parameter;
        _coordinator = context.Coordinator;
        _skinPreviewService = context.SkinPreviewService;
        _skinPreviewService.AppearanceChanged += SkinAppearanceChanged;
        _previewOptions = context.PreviewOptions;
        _settings = context.Settings;
        _playtime = context.Playtime;
        _updates = context.Updates;
        _updates.Coordinator.Changed += AppUpdates_Changed;
        _appUpdateRefresh = DispatcherQueue.CreateTimer();
        _appUpdateRefresh.Interval = TimeSpan.FromSeconds(2);
        _appUpdateRefresh.Tick += (_, _) => { if (_updates?.Coordinator.Phase == AppUpdatePhase.Ready) _ = RefreshAppUpdatesAsync(); };
        _appUpdateRefresh.Start();
        _store = context.Store;
        _playtime.PropertyChanged += Playtime_PropertyChanged;
        if (_playtime.Hako is { } sync)
        {
            sync.PropertyChanged += HakoPlaytime_PropertyChanged;
            _ = sync.RefreshAsync();
        }
        LoadGameSettings();
        _profileRequestScope = Guid.NewGuid();
        _screenVisible = true;
        SubscribeLanguageChanges();
        RefreshStrings();
        SelectSection(_coordinator.SelectedSettingsSection);
        if (_selectedSection == SettingsSection.Storage && _storageCancellation is null) StartStorageLoad();
        RefreshProfile();
        if (_previewOptions?.Page == PreviewPage.CharacterEditor)
            DispatcherQueue.TryEnqueue(() => EditCharacter_Click(this, new RoutedEventArgs()));
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        CancelStorageLoad();
        UnsubscribeLanguageChanges();
        CharacterEditor.Close();
        if (_skinPreviewService is not null) _skinPreviewService.AppearanceChanged -= SkinAppearanceChanged;
        _screenVisible = false;
        SkinView.SetEligible(false);
        SkinView.HeadImageReady -= SkinView_HeadImageReady;
        SkinView.DisposeView();
        _gameSaveTimer?.Stop();
        _appUpdateRefresh?.Stop();
        if (_updates is not null) _updates.Coordinator.Changed -= AppUpdates_Changed;
        _updates = null;
        if (_playtime is not null) _playtime.PropertyChanged -= Playtime_PropertyChanged;
        if (_playtime?.Hako is { } sync) sync.PropertyChanged -= HakoPlaytime_PropertyChanged;
        // Covers navigation without ConfirmLeaveAsync; after the guard saved, the draft equals the settings snapshot and nothing is written.
        _ = SaveGameSettingsAsync();
        _coordinator = null;
        _skinPreviewService = null;
        _playtime = null;
        _store = null;
        base.OnNavigatedFrom(e);
    }

    public void SetWindowState(bool visible, bool active)
    {
        active &= visible;
        if (_windowVisible == visible && _windowActive == active)
        {
            return;
        }

        _windowVisible = visible;
        _windowActive = active;
        UpdateSkinVisibility();
    }

    public void SetScreenVisible(bool visible)
    {
        if (_screenVisible == visible)
        {
            return;
        }

        _screenVisible = visible;
        if (!visible && _editingCharacter) CloseCharacterEditor();
        if (visible)
        {
            SubscribeLanguageChanges();
            RefreshStrings();
            RefreshProfile();
            if (_selectedSection == SettingsSection.Storage) StartStorageLoad();
        }
        else
        {
            CancelStorageLoad();
            UnsubscribeLanguageChanges();
        }

        UpdateSkinVisibility();
    }

    public void SelectSection(SettingsSection section)
    {
        if (_editingCharacter) CloseCharacterEditor();
        var enteringProfile = section == SettingsSection.Profile && _selectedSection != SettingsSection.Profile;
        var sectionChanged = _selectedSection != section;
        if (sectionChanged && _selectedSection == SettingsSection.Storage) CancelStorageLoad();
        _selectedSection = section;
        if (enteringProfile)
        {
            if (_playtime?.Hako is { } sync) _ = sync.RefreshAsync();
            _profileRequestScope = Guid.NewGuid();
        }

        _updatingSelection = true;
        SettingsSections.SelectedIndex = section switch
        {
            SettingsSection.Game => 1,
            SettingsSection.Storage => 2,
            SettingsSection.Profile => 3,
            _ => 0
        };
        _updatingSelection = false;
        GeneralPanel.Visibility = section == SettingsSection.General ? Visibility.Visible : Visibility.Collapsed;
        ProfilePanel.Visibility = section == SettingsSection.Profile ? Visibility.Visible : Visibility.Collapsed;
        GamePanel.Visibility = section == SettingsSection.Game ? Visibility.Visible : Visibility.Collapsed;
        StoragePanel.Visibility = section == SettingsSection.Storage ? Visibility.Visible : Visibility.Collapsed;
        if (sectionChanged && section == SettingsSection.Storage) StartStorageLoad();
        UpdateSkinVisibility();
    }

    public void RefreshProfile()
    {
        var account = _coordinator?.Account;
        var hakoSession = _coordinator?.HakoSession;
        if (_editingCharacter && (_modelAccountId != account?.Xuid || _modelProfileId != account?.MinecraftProfileId))
            CloseCharacterEditor();
        var isGuest = account is null && (_coordinator?.IsGuest == true || hakoSession is not null);
        var hasAccount = account is not null;

        ProfileCards.Visibility = Visibility.Visible;
        MicrosoftProfileCards.Visibility = hasAccount ? Visibility.Visible : Visibility.Collapsed;
        GuestProfilePanel.Visibility = isGuest ? Visibility.Visible : Visibility.Collapsed;
        SignedOutProfilePanel.Visibility = !hasAccount && !isGuest ? Visibility.Visible : Visibility.Collapsed;

        ProfileStatus.Text = account?.Gamertag ?? string.Empty;
        ProfileEmail.Text = account?.Email ?? string.Empty;
        ProfileEmail.Visibility = string.IsNullOrWhiteSpace(account?.Email) ? Visibility.Collapsed : Visibility.Visible;
        XboxBadge.Text = HakoStrings.Get("XboxLabel");

        AccountAvatar.Source = null;
        AccountAvatar.Visibility = Visibility.Collapsed;
        AvatarPlaceholder.Visibility = Visibility.Visible;
        if (Uri.TryCreate(account?.XboxAvatarUrl, UriKind.Absolute, out var avatarUri) && avatarUri.Scheme == Uri.UriSchemeHttps)
        {
            AccountAvatar.Source = new BitmapImage(avatarUri);
            AccountAvatar.Visibility = Visibility.Visible;
            AvatarPlaceholder.Visibility = Visibility.Collapsed;
        }

        MinecraftStatus.Text = account?.MinecraftName ?? HakoStrings.Get("MinecraftNotAvailableTitle");
        MinecraftBadge.Text = HakoStrings.Get("MinecraftEditionLabel");
        MinecraftDetails.Text = account?.MinecraftName is null
            ? HakoStrings.Get("MinecraftNotAvailableDetails")
            : HakoStrings.Get("MinecraftProfileDetails");
        MinecraftDetails.Visibility = account?.MinecraftName is null ? Visibility.Visible : Visibility.Collapsed;

        GuestProfileStatus.Text = HakoStrings.Get("GuestProfileTitle");
        GuestProfileDetails.Text = HakoStrings.Get("GuestProfileDetails");
        AccountPlaytimeTitle.Text = HakoStrings.Get("PlaytimeSummaryTitle");
        AccountPlaytimeValue.Text = PlaytimeFormatter.Format(_playtime?.TotalSeconds(account?.Xuid) ?? 0,
            _coordinator?.Language ?? AppLanguages.Default, HakoStrings.Get);
        GuestPlaytimeTitle.Text = HakoStrings.Get("PlaytimeSummaryTitle");
        GuestPlaytimeValue.Text = PlaytimeFormatter.Format(_playtime?.TotalSeconds(null) ?? 0,
            _coordinator?.Language ?? AppLanguages.Default, HakoStrings.Get);
        GuestPlaytimeTransfer.Text = HakoStrings.Get("PlaytimeGuestTransfer");
        AccountPlaytimeError.Text = PlaytimeErrorText(_playtime?.ErrorResourceKey, _playtime?.ErrorDetail);
        AccountPlaytimeError.Visibility = account is not null && _playtime?.ErrorResourceKey is not null ? Visibility.Visible : Visibility.Collapsed;
        GuestPlaytimeError.Text = PlaytimeErrorText(_playtime?.ErrorResourceKey, _playtime?.ErrorDetail);
        GuestPlaytimeError.Visibility = isGuest && _playtime?.ErrorResourceKey is not null ? Visibility.Visible : Visibility.Collapsed;
        SignInButton.Content = HakoStrings.Get("SignInButton");
        SignedOutProfileStatus.Text = HakoStrings.Get("ProfileSignedOutTitle");
        SignedOutProfileDetails.Text = HakoStrings.Get("ProfileSignedOutDetails");
        SignedOutSignInButton.Content = HakoStrings.Get("SignInButton");
        SignOutButton.Content = HakoStrings.Get("LogoutButton");
        SignOutButton.Visibility = hasAccount ? Visibility.Visible : Visibility.Collapsed;

        var cloud = _playtime?.Hako;
        HakoPlaytimePanel.Visibility = hakoSession is null ? Visibility.Collapsed : Visibility.Visible;
        HakoPlaytimeTitle.Text = HakoStrings.Get("PlaytimeSummaryTitle");
        HakoPlaytimeValue.Text = cloud?.TotalDurationMs is { } milliseconds
            ? PlaytimeFormatter.Format(milliseconds / 1000d, _coordinator?.Language ?? AppLanguages.Default, HakoStrings.Get)
            : HakoStrings.Get(cloud?.HasError == true ? "HakoPlaytimeUnavailable" : "HakoPlaytimeLoading");
        HakoPlaytimePending.Text = string.Format(HakoStrings.Get("HakoPlaytimePending"),
            PlaytimeFormatter.Format((cloud?.PendingDurationMs ?? 0) / 1000d, _coordinator?.Language ?? AppLanguages.Default, HakoStrings.Get));
        HakoPlaytimePending.Visibility = cloud?.PendingDurationMs > 0 ? Visibility.Visible : Visibility.Collapsed;
        HakoPlaytimeError.Text = HakoStrings.Get("HakoPlaytimeSyncError");
        HakoPlaytimeError.Visibility = cloud?.HasError == true ? Visibility.Visible : Visibility.Collapsed;
        HakoProviderTitle.Text = HakoStrings.Get("HakoProfileTitle");
        HakoProfileStatus.Text = hakoSession?.Account.Username ?? HakoStrings.Get("HakoProfileNotConnected");
        HakoProfileBadge.Text = HakoStrings.Get((_coordinator?.HakoSessionStatus ?? HakoSessionStatus.SignedOut).ResourceKey());
        HakoProfileDetails.Text = hakoSession is null
            ? HakoStrings.Get("HakoProfileSignedOutDetails")
            : string.Format(HakoStrings.Get("HakoProfileDeviceFormat"), hakoSession.DeviceName);
        HakoProfileError.Text = _coordinator?.HakoVerificationError is { } hakoError
            ? HakoStrings.Get(hakoError.ResourceKey())
            : string.Empty;
        HakoProfileError.Visibility = _coordinator?.HakoVerificationError is null ? Visibility.Collapsed : Visibility.Visible;
        HakoSignInButton.Content = HakoStrings.Get("HakoSignInButton");
        HakoSignInButton.Visibility = hakoSession is null ? Visibility.Visible : Visibility.Collapsed;
        HakoSignOutButton.Content = HakoStrings.Get("HakoProfileLogoutButton");
        HakoSignOutButton.Visibility = hakoSession is null ? Visibility.Collapsed : Visibility.Visible;

        var profileChanged = !string.Equals(_modelProfileId, account?.MinecraftProfileId, StringComparison.Ordinal);
        var accountChanged = !string.Equals(_modelAccountId, account?.Xuid, StringComparison.Ordinal);
        if (profileChanged || accountChanged)
        {
            _modelAccountId = account?.Xuid;
            _modelProfileId = account?.MinecraftProfileId;
            _profileRequestScope = Guid.NewGuid();
            if (profileChanged)
            {
                MinecraftHead.Source = null;
                MinecraftHead.Visibility = Visibility.Collapsed;
            }
        }

        UpdateSkinVisibility();
    }

    private static string PlaytimeErrorText(string? key, string? detail) => key is null
        ? string.Empty
        : string.IsNullOrWhiteSpace(detail)
            ? HakoStrings.Get(key)
            : HakoStrings.Get(key) + " " + string.Format(HakoStrings.Get("PlaytimeErrorDetails"), detail);

    private void HakoPlaytime_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(RefreshProfile);

    private void Playtime_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PlaytimeCoordinator.Revision) or nameof(PlaytimeCoordinator.ErrorResourceKey))) return;
        DispatcherQueue.TryEnqueue(RefreshProfile);
    }

    private void UpdateSkinVisibility()
    {
        var account = _coordinator?.Account;
        var showModel = _screenVisible
            && _windowVisible
            && _coordinator?.Screen == LauncherScreen.Settings
            && _selectedSection == SettingsSection.Profile
            && account is not null;

        CharacterPanel.Visibility = showModel ? Visibility.Visible : Visibility.Collapsed;
        CharacterEditor.SetWindowState(showModel && _editingCharacter, _windowActive);
        EditCharacterButton.Content = HakoStrings.Get("EditCharacter");
        EditCharacterButton.Visibility = account is { MinecraftProfileId.Length: > 0, MinecraftName.Length: > 0,
            MinecraftAccessToken.Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        showModel &= !_editingCharacter;
        if (!showModel || _skinPreviewService is null)
        {
            SkinView.SetEligible(false);
            return;
        }

        SkinView.SetProfile(
            _skinPreviewService,
            account!.MinecraftProfileId,
            _profileRequestScope,
            _previewOptions?.Mode == ProfilePreviewMode.Fixture);
        SkinView.SetWindowActive(_windowActive);
        SkinView.SetEligible(true);
    }

    private void ProfilePanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 800;
        if (_isWideProfileLayout == wide)
        {
            return;
        }

        _isWideProfileLayout = wide;
        ProfileLayout.ColumnDefinitions.Clear();
        ProfileLayout.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = wide ? new GridLength(360) : new GridLength(1, GridUnitType.Star)
        });
        if (wide)
        {
            ProfileLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        ProfileLayout.RowDefinitions.Clear();
        ProfileLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ProfileLayout.RowDefinitions.Add(new RowDefinition { Height = wide ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        if (!wide)
        {
            ProfileLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        Grid.SetColumnSpan(ProfileTitle, wide ? 2 : 1);
        Grid.SetColumn(ProfileCards, 0);
        Grid.SetRow(ProfileCards, 1);
        Grid.SetColumn(CharacterPanel, wide ? 1 : 0);
        Grid.SetRow(CharacterPanel, wide ? 1 : 2);
        ProfileLayout.ColumnSpacing = wide ? 24 : 0;
        SkinView.MinHeight = wide ? 400 : 360;
        SkinView.MinWidth = wide ? 240 : 0;
    }

    private void HakoStrings_LanguageChanged(object? sender, EventArgs e)
    {
        RefreshStrings();
        RefreshProfile();
    }

    private void SubscribeLanguageChanges()
    {
        if (_languageChangedSubscribed)
        {
            return;
        }

        HakoStrings.LanguageChanged += HakoStrings_LanguageChanged;
        _languageChangedSubscribed = true;
    }

    private void UnsubscribeLanguageChanges()
    {
        if (!_languageChangedSubscribed)
        {
            return;
        }

        HakoStrings.LanguageChanged -= HakoStrings_LanguageChanged;
        _languageChangedSubscribed = false;
    }

    private void RefreshStrings()
    {
        SettingsTitle.Text = HakoStrings.Get("SettingsTitle");
        GeneralSectionLabel.Text = HakoStrings.Get("GeneralSection");
        ProfileSectionLabel.Text = HakoStrings.Get("ProfileTitle");
        StorageSectionLabel.Text = HakoStrings.Get("StorageSection");
        GeneralTitle.Text = HakoStrings.Get("GeneralSection");
        ProfileTitle.Text = HakoStrings.Get("ProfileTitle");
        LanguageLabel.Text = HakoStrings.Get("LanguageLabel");
        RefreshGameStrings();
        _ = RefreshAppUpdatesAsync();
        RefreshStorageStrings();
        _updatingLanguage = true;
        LanguageBox.SelectedIndex = _coordinator?.Language == AppLanguage.Russian ? 0 : 1;
        _updatingLanguage = false;
    }

    private void SkinView_HeadImageReady(object? sender, byte[] rgba)
    {
        var bitmap = new WriteableBitmap(64, 64);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            var bgra = MinecraftSkinHeadBitmapConverter.ConvertStraightRgbaToPremultipliedBgra(rgba);
            stream.Write(bgra, 0, bgra.Length);
        }

        bitmap.Invalidate();
        MinecraftHead.Source = bitmap;
        MinecraftHead.Visibility = Visibility.Visible;
    }

    private void SettingsSections_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection || _coordinator is null || SettingsSections.SelectedItem is not ListViewItem item)
        {
            return;
        }

        var section = item.Tag switch
        {
            "Profile" => SettingsSection.Profile,
            "Game" => SettingsSection.Game,
            "Storage" => SettingsSection.Storage,
            _ => SettingsSection.General
        };
        _coordinator.OpenSettings(section);
        SelectSection(section);
    }

    private async void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingLanguage || _coordinator is null || LanguageBox.SelectedIndex < 0)
        {
            return;
        }

        var language = LanguageBox.SelectedIndex == 0 ? AppLanguage.Russian : AppLanguage.English;
        try
        {
            await _coordinator.SetLanguageAsync(language);
        }
        catch
        {
            await ShowErrorAsync("LanguageSaveError");
        }
    }

    private void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        _coordinator?.OpenSettings(SettingsSection.Profile);
        if (_coordinator is not null)
        {
            _ = _coordinator.BeginSignIn();
        }
    }

    private void HakoSignInButton_Click(object sender, RoutedEventArgs e) => _coordinator?.OpenHakoSignIn();

    private async void HakoSignOutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator is not { } coordinator || coordinator.HakoSession is null)
        {
            return;
        }

        var confirm = new ContentDialog
        {
            Title = HakoStrings.Get("HakoProfileLogoutTitle"),
            Content = HakoStrings.Get("HakoProfileLogoutMessage"),
            PrimaryButtonText = HakoStrings.Get("HakoProfileLogoutButton"),
            CloseButtonText = HakoStrings.Get("CancelButton"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await HakoDialogs.ShowAsync(confirm, current =>
        {
            current.Title = HakoStrings.Get("HakoProfileLogoutTitle");
            current.Content = HakoStrings.Get("HakoProfileLogoutMessage");
            current.PrimaryButtonText = HakoStrings.Get("HakoProfileLogoutButton");
            current.CloseButtonText = HakoStrings.Get("CancelButton");
        }) != ContentDialogResult.Primary || _coordinator != coordinator || !_screenVisible)
        {
            return;
        }

        var result = await coordinator.Hako.LogoutAsync();
        if (result == HakoLogoutResult.LocalClearFailed)
        {
            await ShowErrorAsync("HakoAuthPersistenceError");
            return;
        }

        if (result != HakoLogoutResult.RemoteLogoutFailed)
        {
            return;
        }

        var recovery = new ContentDialog
        {
            Title = HakoStrings.Get("HakoLogoutRetryTitle"),
            Content = HakoStrings.Get("HakoLogoutNetworkFailure"),
            PrimaryButtonText = HakoStrings.Get("HakoLogoutRetryButton"),
            SecondaryButtonText = HakoStrings.Get("HakoLogoutLocalOnlyButton"),
            CloseButtonText = HakoStrings.Get("CancelButton"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        var choice = await HakoDialogs.ShowAsync(recovery, current =>
        {
            current.Title = HakoStrings.Get("HakoLogoutRetryTitle");
            current.Content = HakoStrings.Get("HakoLogoutNetworkFailure");
            current.PrimaryButtonText = HakoStrings.Get("HakoLogoutRetryButton");
            current.SecondaryButtonText = HakoStrings.Get("HakoLogoutLocalOnlyButton");
            current.CloseButtonText = HakoStrings.Get("CancelButton");
        });
        if (_coordinator != coordinator || !_screenVisible)
        {
            return;
        }

        if (choice == ContentDialogResult.Secondary)
        {
            if (!await coordinator.Hako.ClearLocalSessionAsync())
            {
                await ShowErrorAsync("HakoAuthPersistenceError");
            }
        }
        else if (choice == ContentDialogResult.Primary
                 && await coordinator.Hako.LogoutAsync() != HakoLogoutResult.SignedOut)
        {
            await ShowErrorAsync("HakoLogoutRetryFailed");
        }
    }

    private async void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator is not { } coordinator)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = HakoStrings.Get("LogoutTitle"),
            Content = HakoStrings.Get("LogoutMessage"),
            PrimaryButtonText = HakoStrings.Get("LogoutConfirm"),
            CloseButtonText = HakoStrings.Get("CancelButton"),
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonStyle = (Style)Resources["LogoutConfirmButtonStyle"],
            CloseButtonStyle = (Style)Application.Current.Resources["DefaultButtonStyle"],
            XamlRoot = XamlRoot
        };
        // The dialog template accents its default button independently of CloseButtonStyle.
        dialog.Resources["AccentButtonStyle"] = Application.Current.Resources["DefaultButtonStyle"];
        if (await HakoDialogs.ShowAsync(dialog, current =>
        {
            current.Title = HakoStrings.Get("LogoutTitle");
            current.Content = HakoStrings.Get("LogoutMessage");
            current.PrimaryButtonText = HakoStrings.Get("LogoutConfirm");
            current.CloseButtonText = HakoStrings.Get("CancelButton");
        }) != ContentDialogResult.Primary || _coordinator != coordinator || !_screenVisible)
        {
            return;
        }

        try
        {
            await coordinator.LogoutAsync();
        }
        catch (Exception exception) when (exception is AccountStoreException or IOException or UnauthorizedAccessException)
        {
            await ShowErrorAsync("LogoutFailed");
        }
    }

    private async Task ShowErrorAsync(string resourceKey)
    {
        var dialog = new ContentDialog
        {
            Title = HakoStrings.Get("ErrorTitle"),
            Content = HakoStrings.Get(resourceKey),
            CloseButtonText = HakoStrings.Get("CloseButton"),
            XamlRoot = XamlRoot
        };
        await HakoDialogs.ShowAsync(dialog, current =>
        {
            current.Title = HakoStrings.Get("ErrorTitle");
            current.Content = HakoStrings.Get(resourceKey);
            current.CloseButtonText = HakoStrings.Get("CloseButton");
        });
    }

    private void AccountAvatar_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        AccountAvatar.Visibility = Visibility.Collapsed;
        AvatarPlaceholder.Visibility = Visibility.Visible;
    }

    public async Task<bool> ConfirmLeaveAsync()
    {
        if (CharacterEditor.IsSaving) return false;
        await SaveGameSettingsAsync();
        return _settings is null
            || (GameLaunchDefaults.WindowDimension(GlobalWidthBox.Text) is not null && GameLaunchDefaults.WindowDimension(GlobalHeightBox.Text) is not null)
            || await HakoDialogs.ConfirmLeaveInvalidDraftAsync();
    }

    private void SkinAppearanceChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _profileRequestScope = Guid.NewGuid();
            UpdateSkinVisibility();
        });
    }

    private async void EditCharacter_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator is null || _skinPreviewService is null || _editingCharacter) return;
        _editingCharacter = true;
        SettingsScrollViewer.Visibility = Visibility.Collapsed;
        ProfilePanel.Visibility = Visibility.Collapsed;
        CharacterEditor.Visibility = Visibility.Visible;
        UpdateSkinVisibility();
        await CharacterEditor.OpenAsync(_coordinator, _skinPreviewService, _previewOptions?.Mode == ProfilePreviewMode.Fixture);
        UpdateSkinVisibility();
    }

    private void CharacterEditor_BackRequested(object? sender, EventArgs e) => CloseCharacterEditor();

    private void CloseCharacterEditor()
    {
        CharacterEditor.Close();
        CharacterEditor.Visibility = Visibility.Collapsed;
        _editingCharacter = false;
        SettingsScrollViewer.Visibility = Visibility.Visible;
        ProfilePanel.Visibility = _selectedSection == SettingsSection.Profile ? Visibility.Visible : Visibility.Collapsed;
        _profileRequestScope = Guid.NewGuid();
        UpdateSkinVisibility();
    }

    private void LoadGameSettings()
    {
        if (_settings is null)
        {
            return;
        }

        _gameSaveTimer ??= CreateGameSaveTimer();
        var defaults = _settings.GetGameLaunchDefaults();
        _loadingGame = true;
        JavaPathBox.Text = defaults.JavaPath;
        GlobalJavaArgumentsBox.Text = defaults.JavaArguments;
        GlobalMinecraftArgumentsBox.Text = defaults.MinecraftArguments;
        GlobalFullscreenSwitch.IsOn = defaults.Fullscreen;
        GlobalWidthBox.Text = defaults.WindowWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        GlobalHeightBox.Text = defaults.WindowHeight.ToString(System.Globalization.CultureInfo.InvariantCulture);
        GlobalMemorySlider.Configure(GlobalArchitecture, defaults.MaximumMemoryMiB);
        _loadingGame = false;
        UpdateGameValidation();
    }

    private DispatcherQueueTimer CreateGameSaveTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => _ = SaveGameSettingsAsync();
        GlobalMemorySlider.ValueChanged += (_, _) => ScheduleGameSave();
        return timer;
    }

    // Global settings apply to builds of any architecture; the slider is limited only by this computer.
    private static GameArchitecture GlobalArchitecture => Environment.Is64BitOperatingSystem ? GameArchitecture.X64 : GameArchitecture.X86;

    private void RefreshGameStrings()
    {
        GameSectionLabel.Text = HakoStrings.Get("GameSection");
        GameTitle.Text = HakoStrings.Get("GameSection");
        GameDescription.Text = HakoStrings.Get("GameSettingsDescription");
        JavaPathBox.Header = FieldHeader.Create(HakoStrings.Get("JavaPathLabel"));
        JavaPathBox.PlaceholderText = HakoStrings.Get("JavaPathPlaceholder");
        ChooseJavaButton.Content = HakoStrings.Get("ChooseButton");
        JavaPathCaption.Text = HakoStrings.Get("JavaPathCaption");
        GlobalJavaArgumentsBox.Header = FieldHeader.Create(HakoStrings.Get("ParametersJavaArguments"));
        GlobalMinecraftArgumentsBox.Header = FieldHeader.Create(HakoStrings.Get("ParametersMinecraftArguments"));
        GlobalMemoryCaption.Text = HakoStrings.Get("ParametersMemoryCaption");
        WindowCardTitle.Text = HakoStrings.Get("WindowCardTitle");
        GlobalFullscreenSwitch.Header = HakoStrings.Get("ParametersFullscreen");
        GlobalWidthBox.Header = HakoStrings.Get("ParametersWidth");
        GlobalHeightBox.Header = HakoStrings.Get("ParametersHeight");
        GlobalWidthError.Text = GlobalHeightError.Text = HakoStrings.Get("DimensionError");
        WindowCaption.Text = HakoStrings.Get("WindowCaption");
        GameSaveError.Text = HakoStrings.Get("GameSettingsSaveError");
        GlobalMemorySlider.RefreshStrings();
    }

    private void RefreshStorageStrings()
    {
        DiskSpaceTitle.Text = HakoStrings.Get("DiskSpaceTitle");
        StorageUsedLabel.Text = HakoStrings.Get("StorageUsedLabel");
        StorageFreeLabel.Text = HakoStrings.Get("StorageFreeLabel");
        StorageTotalLabel.Text = HakoStrings.Get("StorageTotalLabel");
        DiskBuildsLabel.Text = HakoStrings.Get("StorageMinecraftBuilds");
        StorageUsageTitle.Text = HakoStrings.Get("StorageMinecraftBuilds");
        StorageOccupyLabel.Text = HakoStrings.Get("StorageOccupyLabel");
        DiskLoadingText.Text = HakoStrings.Get("StorageLoading");
        StorageUsageLoadingText.Text = HakoStrings.Get("StorageLoading");
        DiskErrorText.Text = HakoStrings.Get("StorageLoadError");
        StorageSizeErrorText.Text = HakoStrings.Get("StorageSizeError");
        StorageCategoryWorlds.Text = HakoStrings.Get("StorageCategoryWorlds");
        StorageCategoryMinecraft.Text = HakoStrings.Get("StorageCategoryMinecraft");
        StorageCategoryScreenshots.Text = HakoStrings.Get("StorageCategoryScreenshots");
        StorageCategoryJava.Text = HakoStrings.Get("StorageCategoryJava");
        StorageCategoryMods.Text = HakoStrings.Get("StorageCategoryMods");
        StorageCategoryDatapacks.Text = HakoStrings.Get("StorageCategoryDatapacks");
        StorageCategoryResourcePacks.Text = HakoStrings.Get("StorageCategoryResourcePacks");
        StorageCategoryBuildBackups.Text = HakoStrings.Get("StorageCategoryBuildBackups");
        StorageCategoryWorldBackups.Text = HakoStrings.Get("StorageCategoryWorldBackups");

        RefreshStorageUsageDisplay();
    }

    private void StartStorageLoad()
    {
        if (_selectedSection != SettingsSection.Storage || !_screenVisible || _store is not { } store) return;

        CancelStorageLoad();
        var cancellation = new CancellationTokenSource();
        var generation = _storageLoadGeneration;
        var cancellationToken = cancellation.Token;
        _storageCancellation = cancellation;
        _diskSpaceInfo = null;
        _storageUsage = null;
        _diskLoading = true;
        _diskFailed = false;
        _storageUsageLoading = true;
        _storageUsageFailed = false;
        RefreshDiskStorageDisplay();
        RefreshStorageUsageDisplay();

        var diskTask = LoadDiskSpaceAsync(store, generation, cancellationToken);
        var usageTask = LoadStorageUsageAsync(store, generation, cancellationToken);
        _ = FinishStorageLoadAsync(cancellation, diskTask, usageTask);
    }

    private void CancelStorageLoad()
    {
        _storageLoadGeneration++;
        var cancellation = _storageCancellation;
        _storageCancellation = null;
        cancellation?.Cancel();
    }

    private async Task LoadDiskSpaceAsync(InstanceStore store, long generation, CancellationToken cancellationToken)
    {
        try
        {
            var space = await Task.Run(store.GetDiskSpaceInfo, cancellationToken);
            if (!IsCurrentStorageLoad(store, generation, cancellationToken)) return;
            _diskSpaceInfo = space;
            _diskLoading = false;
            _diskFailed = false;
            RefreshDiskStorageDisplay();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!IsCurrentStorageLoad(store, generation, cancellationToken)) return;
            _diskSpaceInfo = null;
            _diskLoading = false;
            _diskFailed = true;
            RefreshDiskStorageDisplay();
        }
    }

    private async Task LoadStorageUsageAsync(InstanceStore store, long generation, CancellationToken cancellationToken)
    {
        try
        {
            var usage = await Task.Run(() => store.GetStorageUsage(cancellationToken), cancellationToken);
            if (!IsCurrentStorageLoad(store, generation, cancellationToken)) return;
            _storageUsage = usage;
            _storageUsageLoading = false;
            _storageUsageFailed = false;
            RefreshStorageUsageDisplay();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!IsCurrentStorageLoad(store, generation, cancellationToken)) return;
            _storageUsage = null;
            _storageUsageLoading = false;
            _storageUsageFailed = true;
            RefreshStorageUsageDisplay();
        }
    }

    private async Task FinishStorageLoadAsync(CancellationTokenSource cancellation, Task diskTask, Task usageTask)
    {
        try
        {
            await Task.WhenAll(diskTask, usageTask);
        }
        catch (Exception)
        {
        }
        finally
        {
            if (ReferenceEquals(_storageCancellation, cancellation)) _storageCancellation = null;
            cancellation.Dispose();
        }
    }

    private bool IsCurrentStorageLoad(InstanceStore store, long generation, CancellationToken cancellationToken) =>
        generation == _storageLoadGeneration
        && !cancellationToken.IsCancellationRequested
        && _screenVisible
        && _selectedSection == SettingsSection.Storage
        && ReferenceEquals(_store, store);

    private void RefreshDiskStorageDisplay()
    {
        DiskLoadingRing.IsActive = _diskLoading;
        DiskLoadingPanel.Visibility = _diskLoading ? Visibility.Visible : Visibility.Collapsed;
        DiskErrorText.Visibility = _diskFailed ? Visibility.Visible : Visibility.Collapsed;
        DiskDataPanel.Visibility = _diskSpaceInfo is not null ? Visibility.Visible : Visibility.Collapsed;
        var space = _diskSpaceInfo;
        if (space is null) return;

        var total = Math.Max(0, space.Total);
        var free = total == 0 ? 0 : Math.Clamp(space.Free, 0, total);
        var used = total - free;
        DiskVolumeName.Text = string.IsNullOrWhiteSpace(space.VolumeName)
            ? HakoStrings.Get("StorageDiskFallbackName")
            : space.VolumeName;
        DiskUsedValue.Text = FormatStorageSize(used);
        DiskFreeValue.Text = FormatStorageSize(free);
        DiskTotalValue.Text = FormatStorageSize(total);

        var hasUsage = _storageUsage is not null && !_storageUsageFailed;
        DiskBuildsLabel.Visibility = hasUsage ? Visibility.Visible : Visibility.Collapsed;
        DiskBuildsShare.Visibility = hasUsage ? Visibility.Visible : Visibility.Collapsed;
        if (hasUsage)
        {
            var instanceBytes = Math.Min(_storageUsage!.Total, used);
            var share = total == 0 ? 0 : (double)_storageUsage.Total * 100 / total;
            DiskBuildsShare.Text = WorldResourceKeys.FormatMessage("StorageBuildsShareFormat", HakoStrings.Get,
                FormatStorageSize(_storageUsage.Total), FormatPercentNumber(share));
            SetStorageBar(DiskSpaceSegments,
            [
                (instanceBytes, (uint?)0xE0607E),
                (used - instanceBytes, (uint?)0x9A9A9A),
                (free, null)
            ], total);
        }
        else
        {
            SetStorageBar(DiskSpaceSegments,
            [
                (used, (uint?)0x9A9A9A),
                (free, null)
            ], total);
        }
    }

    private void RefreshStorageUsageDisplay()
    {
        StorageUsageLoadingRing.IsActive = _storageUsageLoading;
        StorageUsageLoadingPanel.Visibility = _storageUsageLoading ? Visibility.Visible : Visibility.Collapsed;
        StorageSizeErrorText.Visibility = _storageUsageFailed ? Visibility.Visible : Visibility.Collapsed;
        StorageUsageContent.Visibility = _storageUsage is not null ? Visibility.Visible : Visibility.Collapsed;
        StorageTotalUsage.Text = _storageUsage is null ? "—" : FormatStorageSize(_storageUsage.Total);
        StorageBuildsFooter.Text = _store is { } store
            ? WorldResourceKeys.FormatMessage("StorageBuildsFooterFormat", HakoStrings.Get,
                store.Instances.Count.ToString(CultureInfo.CurrentCulture), store.GameRoot)
            : string.Empty;

        if (_storageUsage is { } usage)
        {
            var rows = new (InstanceStorageUsageCategory Category, TextBlock Percent, TextBlock Size)[]
            {
                (InstanceStorageUsageCategory.Worlds, StorageWorldsPercent, StorageWorldsSize),
                (InstanceStorageUsageCategory.Minecraft, StorageMinecraftPercent, StorageMinecraftSize),
                (InstanceStorageUsageCategory.Screenshots, StorageScreenshotsPercent, StorageScreenshotsSize),
                (InstanceStorageUsageCategory.Java, StorageJavaPercent, StorageJavaSize),
                (InstanceStorageUsageCategory.Mods, StorageModsPercent, StorageModsSize),
                (InstanceStorageUsageCategory.Datapacks, StorageDatapacksPercent, StorageDatapacksSize),
                (InstanceStorageUsageCategory.ResourcePacks, StorageResourcePacksPercent, StorageResourcePacksSize),
                (InstanceStorageUsageCategory.BuildBackups, StorageBuildBackupsPercent, StorageBuildBackupsSize),
                (InstanceStorageUsageCategory.WorldBackups, StorageWorldBackupsPercent, StorageWorldBackupsSize)
            };
            foreach (var row in rows)
            {
                var bytes = usage[row.Category];
                var fraction = usage.Total == 0 ? 0 : (double)bytes / usage.Total * 100;
                row.Percent.Text = FormatPercent(fraction);
                row.Size.Text = FormatStorageSize(bytes);
            }

            SetStorageBar(StorageCategoryBar, StorageCategories.Select(category =>
                (usage[category.Category], (uint?)category.Color)).ToArray(), usage.Total);
        }

        RefreshDiskStorageDisplay();
    }

    private static readonly (InstanceStorageUsageCategory Category, uint Color)[] StorageCategories =
    [
        (InstanceStorageUsageCategory.Worlds, 0xE0607E),
        (InstanceStorageUsageCategory.Minecraft, 0x7C8DB5),
        (InstanceStorageUsageCategory.Screenshots, 0xF2884B),
        (InstanceStorageUsageCategory.Java, 0xE5B93C),
        (InstanceStorageUsageCategory.Mods, 0x4FA37A),
        (InstanceStorageUsageCategory.Datapacks, 0x3FA7B8),
        (InstanceStorageUsageCategory.ResourcePacks, 0x8E6BBF),
        (InstanceStorageUsageCategory.BuildBackups, 0x9A9A9A),
        (InstanceStorageUsageCategory.WorldBackups, 0x6E6E6E)
    ];

    private static void SetStorageBar(Grid bar, IReadOnlyList<(long Bytes, uint? Color)> segments, long total)
    {
        bar.Children.Clear();
        bar.ColumnDefinitions.Clear();
        if (total <= 0) return;

        var visibleSegments = segments.Where(segment => segment.Bytes > 0).ToArray();
        for (var index = 0; index < visibleSegments.Length; index++)
        {
            var segment = visibleSegments[index];
            bar.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength((double)segment.Bytes / total, GridUnitType.Star)
            });
            var cornerRadius = visibleSegments.Length == 1
                ? new CornerRadius(6)
                : index == 0
                    ? new CornerRadius(6, 0, 0, 6)
                    : index == visibleSegments.Length - 1
                        ? new CornerRadius(0, 6, 6, 0)
                        : new CornerRadius(0);
            var border = new Border
            {
                Background = segment.Color is { } color ? CreateStorageBrush(color) : null,
                CornerRadius = cornerRadius
            };
            Grid.SetColumn(border, index);
            bar.Children.Add(border);
        }
    }

    private static SolidColorBrush CreateStorageBrush(uint color) => new(Color.FromArgb(255,
        (byte)(color >> 16), (byte)(color >> 8), (byte)color));

    private static string FormatStorageSize(long bytes) =>
        WorldResourceKeys.FormatSize(bytes, CultureInfo.CurrentCulture, HakoStrings.Get);

    private static string FormatPercent(double percent) =>
        percent.ToString("0.#", CultureInfo.CurrentCulture) + "%";

    private static string FormatPercentNumber(double percent) => percent.ToString("0.#", CultureInfo.CurrentCulture);

    private void UpdateGameValidation()
    {
        GlobalWidthBox.IsEnabled = GlobalHeightBox.IsEnabled = !GlobalFullscreenSwitch.IsOn;
        GlobalWidthError.Visibility = GameLaunchDefaults.WindowDimension(GlobalWidthBox.Text) is null ? Visibility.Visible : Visibility.Collapsed;
        GlobalHeightError.Visibility = GameLaunchDefaults.WindowDimension(GlobalHeightBox.Text) is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScheduleGameSave()
    {
        if (_loadingGame || _gameSaveTimer is null)
        {
            return;
        }

        _gameSaveTimer.Stop();
        _gameSaveTimer.Start();
    }

    /// <summary>An invalid window size keeps the saved one; the draft and its error stay on screen.</summary>
    private async Task SaveGameSettingsAsync()
    {
        _gameSaveTimer?.Stop();
        if (_settings is not { } settings)
        {
            return;
        }

        var saved = settings.GetGameLaunchDefaults();
        var defaults = new GameLaunchDefaults(
            JavaPathBox.Text.Trim(),
            GlobalJavaArgumentsBox.Text,
            GlobalMinecraftArgumentsBox.Text,
            GlobalFullscreenSwitch.IsOn,
            GameLaunchDefaults.WindowDimension(GlobalWidthBox.Text) ?? saved.WindowWidth,
            GameLaunchDefaults.WindowDimension(GlobalHeightBox.Text) ?? saved.WindowHeight,
            GlobalMemorySlider.Value);
        if (defaults == saved)
        {
            return;
        }

        try
        {
            await settings.SetGameLaunchDefaultsAsync(defaults);
            GameSaveError.Visibility = Visibility.Collapsed;
        }
        catch (Exception)
        {
            GameSaveError.Visibility = Visibility.Visible;
        }
    }

    private void GameText_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateGameValidation();
        ScheduleGameSave();
    }

    private void GlobalFullscreenSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateGameValidation();
        ScheduleGameSave();
    }

    private async void ChooseJavaButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pickingJava || App.MainWindowId is not { } windowId)
        {
            return;
        }

        _pickingJava = true;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(windowId)
            {
                CommitButtonText = HakoStrings.Get("ChooseButton"),
                FileTypeFilter = { ".exe" }
            };
            if (await picker.PickSingleFileAsync() is { } file)
            {
                JavaPathBox.Text = file.Path;
            }
        }
        catch (Exception)
        {
            GameSaveError.Visibility = Visibility.Visible;
        }
        finally
        {
            _pickingJava = false;
        }
    }
}

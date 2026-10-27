using Hako.Core;
using Hako.Core.Minecraft;
using Hako.Core.Playtime;
using Hako.Core.Updates;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Hako;

internal sealed record SettingsPageContext(
    AppCoordinator Coordinator,
    MinecraftSkinPreviewService SkinPreviewService,
    ProfilePreviewOptions? PreviewOptions,
    UserSettingsService Settings,
    PlaytimeCoordinator Playtime,
    WindowsAppUpdateService Updates);

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
        _playtime.PropertyChanged += Playtime_PropertyChanged;
        LoadGameSettings();
        _profileRequestScope = Guid.NewGuid();
        _screenVisible = true;
        SubscribeLanguageChanges();
        RefreshStrings();
        SelectSection(_coordinator.SelectedSettingsSection);
        RefreshProfile();
        if (_previewOptions?.Page == PreviewPage.CharacterEditor)
            DispatcherQueue.TryEnqueue(() => EditCharacter_Click(this, new RoutedEventArgs()));
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
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
        // Covers navigation without ConfirmLeaveAsync; after the guard saved, the draft equals the settings snapshot and nothing is written.
        _ = SaveGameSettingsAsync();
        _coordinator = null;
        _skinPreviewService = null;
        _playtime = null;
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
        }
        else
        {
            UnsubscribeLanguageChanges();
        }

        UpdateSkinVisibility();
    }

    public void SelectSection(SettingsSection section)
    {
        if (_editingCharacter) CloseCharacterEditor();
        var enteringProfile = section == SettingsSection.Profile && _selectedSection != SettingsSection.Profile;
        _selectedSection = section;
        if (enteringProfile)
        {
            _profileRequestScope = Guid.NewGuid();
        }

        _updatingSelection = true;
        SettingsSections.SelectedIndex = section switch { SettingsSection.Game => 1, SettingsSection.Profile => 2, _ => 0 };
        _updatingSelection = false;
        GeneralPanel.Visibility = section == SettingsSection.General ? Visibility.Visible : Visibility.Collapsed;
        ProfilePanel.Visibility = section == SettingsSection.Profile ? Visibility.Visible : Visibility.Collapsed;
        GamePanel.Visibility = section == SettingsSection.Game ? Visibility.Visible : Visibility.Collapsed;
        UpdateSkinVisibility();
    }

    public void RefreshProfile()
    {
        var account = _coordinator?.Account;
        if (_editingCharacter && (_modelAccountId != account?.Xuid || _modelProfileId != account?.MinecraftProfileId))
            CloseCharacterEditor();
        var isGuest = account is null && _coordinator?.IsGuest == true;
        var hasAccount = account is not null;

        ProfileCards.Visibility = hasAccount ? Visibility.Visible : Visibility.Collapsed;
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
        ProfileLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (!wide)
        {
            ProfileLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        Grid.SetColumnSpan(ProfileTitle, wide ? 2 : 1);
        Grid.SetColumn(ProfileCards, 0);
        Grid.SetRow(ProfileCards, 1);
        Grid.SetColumn(CharacterPanel, wide ? 1 : 0);
        Grid.SetRow(CharacterPanel, wide ? 1 : 2);
        Grid.SetColumnSpan(GuestProfilePanel, wide ? 2 : 1);
        Grid.SetRow(GuestProfilePanel, 1);
        Grid.SetColumnSpan(SignedOutProfilePanel, wide ? 2 : 1);
        Grid.SetRow(SignedOutProfilePanel, 1);
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
        GeneralTitle.Text = HakoStrings.Get("GeneralSection");
        ProfileTitle.Text = HakoStrings.Get("ProfileTitle");
        LanguageLabel.Text = HakoStrings.Get("LanguageLabel");
        RefreshGameStrings();
        _ = RefreshAppUpdatesAsync();
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

        var section = item.Tag switch { "Profile" => SettingsSection.Profile, "Game" => SettingsSection.Game, _ => SettingsSection.General };
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
            XamlRoot = XamlRoot
        };
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

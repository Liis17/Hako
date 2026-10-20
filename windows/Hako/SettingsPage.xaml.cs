using Hako.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Hako;

internal sealed record SettingsPageContext(
    AppCoordinator Coordinator,
    MinecraftSkinPreviewService SkinPreviewService,
    ProfilePreviewOptions? PreviewOptions);

public sealed partial class SettingsPage : Page
{
    private AppCoordinator? _coordinator;
    private MinecraftSkinPreviewService? _skinPreviewService;
    private ProfilePreviewOptions? _previewOptions;
    private bool _updatingSelection;
    private bool _updatingLanguage;
    private bool _windowAvailable;
    private bool _screenVisible;
    private bool _languageChangedSubscribed;
    private SettingsSection _selectedSection = SettingsSection.General;
    private string? _modelAccountId;
    private string? _modelProfileId;
    private Guid _profileRequestScope;
    private bool? _isWideProfileLayout;

    public SettingsPage()
    {
        InitializeComponent();
        SkinView.HeadImageReady += SkinView_HeadImageReady;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var context = (SettingsPageContext)e.Parameter;
        _coordinator = context.Coordinator;
        _skinPreviewService = context.SkinPreviewService;
        _previewOptions = context.PreviewOptions;
        _profileRequestScope = Guid.NewGuid();
        _screenVisible = true;
        SubscribeLanguageChanges();
        RefreshStrings();
        SelectSection(_coordinator.SelectedSettingsSection);
        RefreshProfile();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        UnsubscribeLanguageChanges();
        _screenVisible = false;
        SkinView.SetEligible(false);
        SkinView.HeadImageReady -= SkinView_HeadImageReady;
        SkinView.DisposeView();
        _coordinator = null;
        _skinPreviewService = null;
        base.OnNavigatedFrom(e);
    }

    public void SetWindowAvailable(bool available)
    {
        if (_windowAvailable == available)
        {
            return;
        }

        _windowAvailable = available;
        UpdateSkinVisibility();
    }

    public void SetScreenVisible(bool visible)
    {
        if (_screenVisible == visible)
        {
            return;
        }

        _screenVisible = visible;
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
        var enteringProfile = section == SettingsSection.Profile && _selectedSection != SettingsSection.Profile;
        _selectedSection = section;
        if (enteringProfile)
        {
            _profileRequestScope = Guid.NewGuid();
        }

        _updatingSelection = true;
        SettingsSections.SelectedIndex = section == SettingsSection.General ? 0 : 1;
        _updatingSelection = false;
        GeneralPanel.Visibility = section == SettingsSection.General ? Visibility.Visible : Visibility.Collapsed;
        ProfilePanel.Visibility = section == SettingsSection.Profile ? Visibility.Visible : Visibility.Collapsed;
        UpdateSkinVisibility();
    }

    public void RefreshProfile()
    {
        var account = _coordinator?.Account;
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

    private void UpdateSkinVisibility()
    {
        var account = _coordinator?.Account;
        var showModel = _screenVisible
            && _windowAvailable
            && _coordinator?.Screen == LauncherScreen.Settings
            && _selectedSection == SettingsSection.Profile
            && account is not null;

        SkinView.Visibility = showModel ? Visibility.Visible : Visibility.Collapsed;
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
        Grid.SetColumn(SkinView, wide ? 1 : 0);
        Grid.SetRow(SkinView, wide ? 1 : 2);
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
        _updatingLanguage = true;
        LanguageBox.SelectedIndex = _coordinator?.Language == AppLanguage.Russian ? 0 : 1;
        _updatingLanguage = false;
    }

    private void SkinView_HeadImageReady(object? sender, byte[] rgba)
    {
        var bitmap = new WriteableBitmap(64, 64);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(rgba, 0, rgba.Length);
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

        var section = Equals(item.Tag, "Profile") ? SettingsSection.Profile : SettingsSection.General;
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
        if (_coordinator is null)
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
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _coordinator.LogoutAsync();
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
        await dialog.ShowAsync();
    }

    private void AccountAvatar_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        AccountAvatar.Visibility = Visibility.Collapsed;
        AvatarPlaceholder.Visibility = Visibility.Visible;
    }
}

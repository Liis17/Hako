using Hako.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace Hako;

public sealed partial class SettingsPage : Page
{
    private AppCoordinator? _coordinator;
    private bool _updatingSelection;
    private bool _updatingLanguage;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _coordinator = (AppCoordinator)e.Parameter;
        HakoStrings.LanguageChanged += HakoStrings_LanguageChanged;
        RefreshStrings();
        SelectSection(_coordinator.SelectedSettingsSection);
        RefreshProfile();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        HakoStrings.LanguageChanged -= HakoStrings_LanguageChanged;
        base.OnNavigatedFrom(e);
    }

    public void SelectSection(SettingsSection section)
    {
        _updatingSelection = true;
        SettingsSections.SelectedIndex = section == SettingsSection.General ? 0 : 1;
        _updatingSelection = false;
        GeneralPanel.Visibility = section == SettingsSection.General ? Visibility.Visible : Visibility.Collapsed;
        ProfilePanel.Visibility = section == SettingsSection.Profile ? Visibility.Visible : Visibility.Collapsed;
    }

    public void RefreshProfile()
    {
        var account = _coordinator?.Account;
        var isGuest = account is null && _coordinator?.IsGuest == true;
        var hasAccount = account is not null;

        ProfileStatus.Text = account?.Gamertag ?? HakoStrings.Get(isGuest ? "GuestProfileTitle" : "ProfileSignedOutTitle");
        ProfileDetails.Text = isGuest
            ? HakoStrings.Get("GuestProfileDetails")
            : hasAccount ? string.Empty : HakoStrings.Get("ProfileSignedOutDetails");
        ProfileDetails.Visibility = string.IsNullOrEmpty(ProfileDetails.Text) ? Visibility.Collapsed : Visibility.Visible;

        ProfileEmail.Text = account?.Email ?? string.Empty;
        ProfileEmail.Visibility = string.IsNullOrWhiteSpace(account?.Email) ? Visibility.Collapsed : Visibility.Visible;
        XboxBadge.Text = HakoStrings.Get("XboxLabel");
        XboxBadge.Visibility = hasAccount ? Visibility.Visible : Visibility.Collapsed;

        AccountAvatar.Source = null;
        AccountAvatar.Visibility = Visibility.Collapsed;
        AvatarPlaceholder.Visibility = Visibility.Visible;
        if (Uri.TryCreate(account?.XboxAvatarUrl, UriKind.Absolute, out var avatarUri) && avatarUri.Scheme == Uri.UriSchemeHttps)
        {
            AccountAvatar.Source = new BitmapImage(avatarUri);
            AccountAvatar.Visibility = Visibility.Visible;
            AvatarPlaceholder.Visibility = Visibility.Collapsed;
        }

        MinecraftProfilePanel.Visibility = hasAccount ? Visibility.Visible : Visibility.Collapsed;
        if (account is not null)
        {
            MinecraftStatus.Text = account.MinecraftName ?? HakoStrings.Get("MinecraftNotAvailableTitle");
            MinecraftDetails.Text = account.MinecraftName is null
                ? HakoStrings.Get("MinecraftNotAvailableDetails")
                : HakoStrings.Get("MinecraftProfileDetails");
        }

        SignInButton.Content = HakoStrings.Get("SignInButton");
        SignInButton.Visibility = isGuest ? Visibility.Visible : Visibility.Collapsed;
        SignOutButton.Content = HakoStrings.Get("LogoutButton");
        SignOutButton.Visibility = hasAccount ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HakoStrings_LanguageChanged(object? sender, EventArgs e)
    {
        RefreshStrings();
        RefreshProfile();
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

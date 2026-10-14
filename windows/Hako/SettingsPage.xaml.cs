using Hako.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        var isGuest = _coordinator?.IsGuest == true;
        ProfileStatus.Text = HakoStrings.Get(isGuest ? "GuestProfileTitle" : "ProfileSignedOutTitle");
        ProfileDetails.Text = HakoStrings.Get(isGuest ? "GuestProfileDetails" : "ProfileSignedOutDetails");
        SignInButton.Content = HakoStrings.Get("SignInButton");
        SignInButton.Visibility = isGuest ? Visibility.Visible : Visibility.Collapsed;
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
            var dialog = new ContentDialog
            {
                Title = HakoStrings.Get("ErrorTitle"),
                Content = HakoStrings.Get("LanguageSaveError"),
                CloseButtonText = HakoStrings.Get("CloseButton"),
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
        }
    }

    private void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        _coordinator?.OpenSettings(SettingsSection.Profile);
        _coordinator?.BeginSignIn();
    }
}

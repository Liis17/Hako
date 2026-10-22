using Hako.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Hako;

public sealed partial class WelcomePage : Page
{
    private AppCoordinator? _coordinator;
    private bool _updatingLanguage;

    public WelcomePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _coordinator = (AppCoordinator)e.Parameter;
        HakoStrings.LanguageChanged += HakoStrings_LanguageChanged;
        RefreshStrings();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        HakoStrings.LanguageChanged -= HakoStrings_LanguageChanged;
        base.OnNavigatedFrom(e);
    }

    private void HakoStrings_LanguageChanged(object? sender, EventArgs e) => RefreshStrings();

    private void RefreshStrings()
    {
        WelcomeTitle.Text = HakoStrings.Get("WelcomeTitle");
        WelcomeSubtitle.Text = HakoStrings.Get("WelcomeSubtitle");
        StartButton.Content = HakoStrings.Get("StartButton");
        GuestButton.Content = HakoStrings.Get("GuestButton");
        Disclaimer.Text = HakoStrings.Get("Disclaimer");
        RefreshStartupNotice();
        _updatingLanguage = true;
        LanguageBox.SelectedIndex = _coordinator?.Language == AppLanguage.Russian ? 0 : 1;
        _updatingLanguage = false;
    }

    public void RefreshStartupNotice()
    {
        var key = _coordinator?.CurrentStartupNotice?.ResourceKey();
        StartupInfoBar.IsOpen = key is not null;
        if (key is not null)
        {
            StartupInfoBar.Message = HakoStrings.Get(key);
        }
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
            await ShowLanguageErrorAsync();
        }
    }

    private async Task ShowLanguageErrorAsync()
    {
        var dialog = new ContentDialog
        {
            Title = HakoStrings.Get("ErrorTitle"),
            Content = HakoStrings.Get("LanguageSaveError"),
            CloseButtonText = HakoStrings.Get("CloseButton"),
            XamlRoot = XamlRoot
        };
        await HakoDialogs.ShowAsync(dialog);
    }

    private void StartButton_Click(object sender, RoutedEventArgs e) => _coordinator?.BeginSignIn();

    private void GuestButton_Click(object sender, RoutedEventArgs e) => _coordinator?.ContinueWithoutAccount();
}

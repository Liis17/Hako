using Hako.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Hako;

public sealed partial class LoginPage : Page
{
    private AppCoordinator? _coordinator;

    public LoginPage()
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
        BackButton.Content = HakoStrings.Get("BackButton");
        LoginTitle.Text = HakoStrings.Get("LoginTitle");
        LoginStatus.Text = HakoStrings.Get("LoginPreparing");
    }

    private void BackButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => _coordinator?.BackFromSignIn();
}

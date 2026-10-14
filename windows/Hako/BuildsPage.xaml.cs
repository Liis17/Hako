using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Hako;

public sealed partial class BuildsPage : Page
{
    public BuildsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
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
        BuildsTitle.Text = HakoStrings.Get("BuildsTitle");
        BuildsEmptyTitle.Text = HakoStrings.Get("BuildsEmptyTitle");
        BuildsEmptyBody.Text = HakoStrings.Get("BuildsEmptyBody");
    }
}

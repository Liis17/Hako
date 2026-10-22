using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Hako;

internal enum InstanceCreationChoice
{
    None,
    Custom
}

/// <summary>«Своя сборка» or «Готовая с Modrinth» (InstanceCreationChoiceView on macOS). Modrinth modpacks arrive later.</summary>
internal static class InstanceCreationChoiceDialog
{
    public static async Task<InstanceCreationChoice> ShowAsync()
    {
        var choice = InstanceCreationChoice.None;
        var dialog = new ContentDialog
        {
            Title = HakoStrings.Get("CreationTitle"),
            CloseButtonText = HakoStrings.Get("CancelButton"),
            DefaultButton = ContentDialogButton.Close
        };

        var custom = Option("", HakoStrings.Get("CreationCustomTitle"), HakoStrings.Get("CreationCustomDetails"));
        custom.Click += (_, _) =>
        {
            choice = InstanceCreationChoice.Custom;
            dialog.Hide();
        };
        var modrinth = Option("", HakoStrings.Get("CreationModrinthTitle"), HakoStrings.Get("CreationModrinthUnavailable"));
        modrinth.IsEnabled = false;
        ToolTipService.SetToolTip(modrinth, HakoStrings.Get("CreationModrinthUnavailable"));

        dialog.Content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 420,
            Children =
            {
                new TextBlock
                {
                    Text = HakoStrings.Get("CreationChoiceSubtitle"),
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                },
                custom,
                modrinth
            }
        };
        await HakoDialogs.ShowAsync(dialog);
        return choice;
    }

    private static Button Option(string glyph, string title, string details)
    {
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Children.Add(new TextBlock
        {
            Text = details,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        var layout = new Grid
        {
            ColumnSpacing = 16,
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }
        };
        var icon = new FontIcon { Glyph = glyph, FontSize = 22, FontFamily = new FontFamily("Segoe Fluent Icons"), Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] };
        layout.Children.Add(icon);
        Grid.SetColumn(text, 1);
        layout.Children.Add(text);
        var chevron = new FontIcon { Glyph = "", FontSize = 12, FontFamily = new FontFamily("Segoe Fluent Icons") };
        Grid.SetColumn(chevron, 2);
        layout.Children.Add(chevron);
        var button = new Button
        {
            Content = layout,
            Padding = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, title);
        return button;
    }
}

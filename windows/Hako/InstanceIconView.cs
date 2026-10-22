using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Hako;

/// <summary>Build icon: a built-in glyph on the accent colour or the build's own picture, as InstanceIcon on macOS.</summary>
public sealed partial class InstanceIconView : UserControl
{
    private readonly Border _frame;
    private readonly FontIcon _glyph;
    private readonly Image _image;

    public InstanceIconView()
    {
        _glyph = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _image = new Image { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
        var grid = new Grid();
        grid.Children.Add(_glyph);
        grid.Children.Add(_image);
        _frame = new Border { Child = grid };
        _frame.SetValue(Border.BackgroundProperty, Application.Current.Resources["AccentFillColorDefaultBrush"]);
        Content = _frame;
        SizeChanged += (_, _) => UpdateSize();
        IsTabStop = false;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Show(null, null);
    }

    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(
        nameof(Symbol), typeof(string), typeof(InstanceIconView), new PropertyMetadata(null, OnIconChanged));

    public static readonly DependencyProperty PictureProperty = DependencyProperty.Register(
        nameof(Picture), typeof(ImageSource), typeof(InstanceIconView), new PropertyMetadata(null, OnIconChanged));

    public string? Symbol
    {
        get => (string?)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public ImageSource? Picture
    {
        get => (ImageSource?)GetValue(PictureProperty);
        set => SetValue(PictureProperty, value);
    }

    private static void OnIconChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (InstanceIconView)sender;
        view.Show(view.Symbol, view.Picture);
    }

    public void Show(string? symbol, ImageSource? image)
    {
        _glyph.Glyph = InstanceIconGlyphs.Glyph(symbol);
        _image.Source = image;
        _image.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        _glyph.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSize()
    {
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0)
        {
            return;
        }

        _frame.CornerRadius = new CornerRadius(side * 0.24);
        _glyph.FontSize = Math.Max(8, side * 0.43);
    }
}

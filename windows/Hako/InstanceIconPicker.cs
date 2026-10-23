using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;

namespace Hako;

/// <summary>Six built-in icons or a custom PNG/JPEG normalized to 256×256 (InstanceIconPicker on macOS).</summary>
public sealed partial class InstanceIconPicker : UserControl
{
    private readonly InstanceIconView _preview = new() { Width = 72, Height = 72, VerticalAlignment = VerticalAlignment.Top };
    private readonly List<(ToggleButton Button, string Symbol, string TitleKey)> _buttons = [];
    private readonly Button _customButton = new();
    private readonly TextBlock _error = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private ImageSource? _existingImage;
    private ImageSource? _customImage;
    private bool _picking;

    public InstanceIconPicker()
    {
        _error.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        var symbols = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (symbol, glyph, titleKey) in InstanceIconGlyphs.BuiltIn)
        {
            var button = new ToggleButton
            {
                Width = 40,
                Height = 36,
                Padding = new Thickness(0),
                Content = new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 16 }
            };
            button.Click += (_, _) => SelectSymbol(symbol);
            _buttons.Add((button, symbol, titleKey));
            symbols.Children.Add(button);
        }

        _customButton.Click += CustomButton_Click;
        var controls = new StackPanel { Spacing = 10, Children = { symbols, _customButton, _error } };
        var root = new Grid { ColumnSpacing = 18, ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() } };
        root.Children.Add(_preview);
        Grid.SetColumn(controls, 1);
        root.Children.Add(controls);
        Content = root;
        Update();
    }

    public event EventHandler? Changed;

    public string Symbol { get; private set; } = InstanceIconGlyphs.DefaultSymbol;

    /// <summary>New custom picture chosen in this editor, or null when unchanged or a symbol is selected.</summary>
    public byte[]? IconData { get; private set; }

    public void Load(string symbol, ImageSource? existingImage)
    {
        Symbol = symbol;
        IconData = null;
        _customImage = null;
        _existingImage = existingImage;
        _error.Visibility = Visibility.Collapsed;
        Update();
    }

    public void LoadCustom(byte[] data, ImageSource? image)
    {
        Symbol = string.Empty;
        IconData = data;
        _customImage = image;
        _existingImage = null;
        _error.Visibility = Visibility.Collapsed;
        Update();
    }

    public void RefreshStrings()
    {
        _customButton.Content = HakoStrings.Get("IconCustomButton");
        _error.Text = HakoStrings.Get("IconCustomError");
        foreach (var (button, _, titleKey) in _buttons)
        {
            var title = HakoStrings.Get(titleKey);
            ToolTipService.SetToolTip(button, title);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, string.Format(HakoStrings.Get("IconAccessibilityFormat"), title));
        }
    }

    public void SetEditable(bool editable)
    {
        foreach (var (button, _, _) in _buttons)
        {
            button.IsEnabled = editable;
        }

        _customButton.IsEnabled = editable;
    }

    private void SelectSymbol(string symbol)
    {
        Symbol = symbol;
        IconData = null;
        _customImage = null;
        _error.Visibility = Visibility.Collapsed;
        Update();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async void CustomButton_Click(object sender, RoutedEventArgs e)
    {
        if (_picking || App.MainWindowId is not { } windowId)
        {
            return;
        }

        _picking = true;
        try
        {
            var picker = new FileOpenPicker(windowId)
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                FileTypeFilter = { ".png", ".jpg", ".jpeg" }
            };
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                Update();
                return;
            }

            var data = await InstanceIconImage.NormalizeAsync(file.Path);
            var image = await InstanceIconSource.CreateAsync(data);
            IconData = data;
            Symbol = string.Empty;
            _customImage = image;
            _error.Visibility = Visibility.Collapsed;
            Update();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            _error.Visibility = Visibility.Visible;
            Update();
        }
        finally
        {
            _picking = false;
        }
    }

    private void Update()
    {
        _preview.Show(Symbol, Symbol.Length == 0 ? _customImage ?? _existingImage : null);
        foreach (var (button, symbol, _) in _buttons)
        {
            button.IsChecked = symbol == Symbol;
        }
    }
}

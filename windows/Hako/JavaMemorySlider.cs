using System.Globalization;
using Hako.Core.Minecraft;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Hako;

/// <summary>Java heap limit in 256 MiB steps with the 70% warning and the x86 limit, as JavaMemorySlider on macOS.</summary>
public sealed partial class JavaMemorySlider : UserControl
{
    private static readonly Lazy<JavaMemoryPolicy> SharedPolicy = new(() => JavaMemoryPolicy.Current);
    private readonly TextBlock _title = new() { FontWeight = Microsoft.UI.Text.FontWeights.Medium };
    private readonly TextBlock _value = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Slider _slider = new() { StepFrequency = 256, SmallChange = 256, LargeChange = 1024, TickPlacement = TickPlacement.None };
    private readonly TextBlock _caption = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _warning = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private GameArchitecture _architecture = GameArchitecture.X64;
    private bool _updating;

    public JavaMemorySlider()
    {
        _caption.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _warning.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        var header = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        header.Children.Add(_title);
        Grid.SetColumn(_value, 1);
        header.Children.Add(_value);
        Content = new StackPanel { Spacing = 6, Children = { header, _slider, _caption, _warning } };
        _slider.Minimum = 512;
        _slider.ValueChanged += Slider_ValueChanged;
        Configure(GameArchitecture.X64, Policy.InitialMiB(GameArchitecture.X64));
    }

    public event EventHandler? ValueChanged;

    public static JavaMemoryPolicy Policy => SharedPolicy.Value;

    public int Value => Policy.Normalize((int)_slider.Value, _architecture);

    public void Configure(GameArchitecture architecture, int value)
    {
        _architecture = architecture;
        _updating = true;
        _slider.Maximum = Policy.MaximumMiB(architecture);
        _slider.Value = Policy.Normalize(value, architecture);
        _updating = false;
        UpdateText();
    }

    public void RefreshStrings()
    {
        _title.Text = HakoStrings.Get("JavaMemoryTitle");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_slider, _title.Text);
        _warning.Text = HakoStrings.Get("LaunchMemoryWarning");
        UpdateText();
    }

    private void Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var normalized = Policy.Normalize((int)Math.Round(e.NewValue), _architecture);
        if (Math.Abs(_slider.Value - normalized) > 0.5)
        {
            _slider.Value = normalized;
            return;
        }

        UpdateText();
        if (!_updating)
        {
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateText()
    {
        _value.Text = string.Format(CultureInfo.CurrentCulture, HakoStrings.Get("JavaMemoryValue"), Value / 1024d);
        var physical = Policy.PhysicalBytes / (1024d * 1024 * 1024);
        _caption.Text = string.Format(CultureInfo.CurrentCulture, HakoStrings.Get(
            _architecture == GameArchitecture.X86 ? "JavaMemoryCaptionX86" : "JavaMemoryCaption"), physical);
        _warning.Visibility = Policy.Warns(Value) ? Visibility.Visible : Visibility.Collapsed;
    }
}

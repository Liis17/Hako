using System.Globalization;
using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hako;

/// <summary>Launch parameters of one build (InstanceParametersEditor on macOS): argument source, arguments, memory, window and offline-mode.</summary>
public sealed partial class InstanceParametersEditor : UserControl
{
    private InstanceDraft _draft = new();
    private MojangVersionMetadata? _manifest;
    private GameArchitecture _architecture = GameArchitecture.X64;
    private GameLaunchDefaults _global = GameLaunchDefaults.Standard;
    private bool _loading;
    private bool _editable = true;

    public InstanceParametersEditor()
    {
        InitializeComponent();
        MemorySlider.ValueChanged += MemorySlider_ValueChanged;
    }

    public event EventHandler? Changed;

    internal InstanceDraft Draft => _draft;

    public bool IsValid => (_draft.Parameters.Fullscreen
            || (GameLaunchDefaults.WindowDimension(WidthBox.Text) is not null && GameLaunchDefaults.WindowDimension(HeightBox.Text) is not null))
        && (!_draft.OfflineMode || OfflineUsername.IsValid(_draft.OfflineUsername));

    internal void Load(InstanceDraft draft, MojangVersionMetadata? manifest, GameArchitecture architecture, GameLaunchDefaults global)
    {
        _loading = true;
        _draft = draft;
        _manifest = manifest;
        _architecture = architecture;
        _global = global;
        FullscreenSwitch.IsOn = draft.Parameters.Fullscreen;
        WidthBox.Text = draft.Parameters.WindowWidth.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = draft.Parameters.WindowHeight.ToString(CultureInfo.InvariantCulture);
        OfflineSwitch.IsOn = draft.OfflineMode;
        OfflineNameBox.Text = draft.OfflineUsername;
        _loading = false;
        Update();
    }

    internal void SetManifest(MojangVersionMetadata? manifest, GameArchitecture architecture)
    {
        _manifest = manifest;
        _architecture = architecture;
        Update();
    }

    internal void SetEditable(bool editable)
    {
        _editable = editable;
        Update();
    }

    public void RefreshStrings()
    {
        GlobalSourceBox.Content = HakoStrings.Get("ParametersUseGlobal");
        MojangSourceBox.Content = HakoStrings.Get("ParametersUseMojang");
        JavaArgumentsBox.Header = FieldHeader.Create(HakoStrings.Get("ParametersJavaArguments"));
        MinecraftArgumentsBox.Header = FieldHeader.Create(HakoStrings.Get("ParametersMinecraftArguments"));
        MemoryCaption.Text = HakoStrings.Get("ParametersMemoryCaption");
        FullscreenSwitch.Header = HakoStrings.Get("ParametersFullscreen");
        WidthBox.Header = HakoStrings.Get("ParametersWidth");
        HeightBox.Header = HakoStrings.Get("ParametersHeight");
        DimensionError.Text = HakoStrings.Get("ParametersDimensionError");
        OfflineSwitch.Header = HakoStrings.Get("ParametersOfflineMode");
        OfflineNameBox.PlaceholderText = HakoStrings.Get("ParametersOfflineName");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(OfflineNameBox, HakoStrings.Get("ParametersOfflineName"));
        OfflineWarning.Text = HakoStrings.Get("ParametersOfflineWarning");
        OfflineNameError.Text = HakoStrings.Get("ParametersOfflineNameError");
        MemorySlider.RefreshStrings();
        Update();
    }

    private void Update()
    {
        var wasLoading = _loading;
        _loading = true;
        var source = _draft.ArgumentSource;
        GlobalSourceBox.IsChecked = source == LaunchArgumentSource.Global;
        MojangSourceBox.IsChecked = source == LaunchArgumentSource.Mojang;
        GlobalSourceBox.IsEnabled = MojangSourceBox.IsEnabled = _editable;

        SourceCaption.Text = source switch
        {
            LaunchArgumentSource.Global => HakoStrings.Get("ParametersGlobalCaption"),
            LaunchArgumentSource.Mojang => HakoStrings.Get("ParametersMojangCaption"),
            _ => string.Empty
        };
        SourceCaption.Visibility = Visible(SourceCaption.Text.Length > 0);
        SourceWarning.Text = HakoStrings.Get("ParametersGlobalEmptyWarning");
        SourceWarning.Visibility = Visible(source == LaunchArgumentSource.Global
            && string.IsNullOrWhiteSpace(_global.JavaArguments) && string.IsNullOrWhiteSpace(_global.MinecraftArguments));
        JavaPathWarning.Text = HakoStrings.Get("ParametersGlobalJavaWarning");
        JavaPathWarning.Visibility = Visible(source == LaunchArgumentSource.Global && !string.IsNullOrWhiteSpace(_global.JavaPath));

        var custom = source == LaunchArgumentSource.Custom;
        JavaArgumentsBox.IsEnabled = MinecraftArgumentsBox.IsEnabled = custom && _editable;
        var (java, minecraft) = DisplayedArguments(source);
        if (JavaArgumentsBox.Text != java)
        {
            JavaArgumentsBox.Text = java;
        }

        if (MinecraftArgumentsBox.Text != minecraft)
        {
            MinecraftArgumentsBox.Text = minecraft;
        }

        var memory = source == LaunchArgumentSource.Global ? _global.MaximumMemoryMiB : _draft.Parameters.MaximumMemoryMiB;
        MemorySlider.Configure(_architecture, memory);
        MemorySlider.IsEnabled = source != LaunchArgumentSource.Global && _editable;

        FullscreenSwitch.IsEnabled = OfflineSwitch.IsEnabled = _editable;
        WidthBox.IsEnabled = HeightBox.IsEnabled = !_draft.Parameters.Fullscreen && _editable;
        DimensionError.Visibility = Visible(!_draft.Parameters.Fullscreen
            && (GameLaunchDefaults.WindowDimension(WidthBox.Text) is null || GameLaunchDefaults.WindowDimension(HeightBox.Text) is null));
        OfflineNameBox.Visibility = Visible(_draft.OfflineMode);
        OfflineNameBox.IsEnabled = _editable;
        OfflineWarning.Visibility = Visible(_draft.OfflineMode);
        OfflineNameError.Visibility = Visible(_draft.OfflineMode && !OfflineUsername.IsValid(_draft.OfflineUsername));
        _loading = wasLoading;
    }

    private (string Java, string Minecraft) DisplayedArguments(LaunchArgumentSource source)
    {
        if (source == LaunchArgumentSource.Custom)
        {
            return (_draft.Parameters.JavaArguments, _draft.Parameters.MinecraftArguments);
        }

        var parameters = source == LaunchArgumentSource.Global
            ? _draft.Parameters with
            {
                JavaArguments = _global.JavaArguments,
                MinecraftArguments = _global.MinecraftArguments,
                MaximumMemoryMiB = _global.MaximumMemoryMiB
            }
            : _draft.Parameters;
        if (_manifest is not null)
        {
            try
            {
                var templates = MinecraftLaunchPlan.ArgumentTemplates(_manifest, source, parameters, _architecture, JavaMemorySlider.Policy);
                return (LaunchArguments.Format(templates.Java), LaunchArguments.Format(templates.Minecraft));
            }
            catch (Exception)
            {
                // Invalid global arguments are shown as typed; the launch reports the error.
            }
        }

        return source == LaunchArgumentSource.Global ? (_global.JavaArguments, _global.MinecraftArguments) : (string.Empty, string.Empty);
    }

    private void Commit(InstanceDraft draft)
    {
        _draft = draft;
        Update();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SourceBox_Click(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        var source = box == GlobalSourceBox ? LaunchArgumentSource.Global : LaunchArgumentSource.Mojang;
        Commit(_draft with { ArgumentSource = box.IsChecked == true ? source : LaunchArgumentSource.Custom });
    }

    private void ArgumentsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _draft.ArgumentSource != LaunchArgumentSource.Custom)
        {
            return;
        }

        Commit(_draft with
        {
            Parameters = _draft.Parameters with
            {
                JavaArguments = JavaArgumentsBox.Text,
                MinecraftArguments = MinecraftArgumentsBox.Text
            }
        });
    }

    private void MemorySlider_ValueChanged(object? sender, EventArgs e)
    {
        if (_loading || _draft.ArgumentSource == LaunchArgumentSource.Global)
        {
            return;
        }

        Commit(_draft with { Parameters = _draft.Parameters with { MaximumMemoryMiB = MemorySlider.Value } });
    }

    private void FullscreenSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            Commit(_draft with { Parameters = _draft.Parameters with { Fullscreen = FullscreenSwitch.IsOn } });
        }
    }

    private void DimensionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        // An invalid draft keeps the last valid size; the error stays visible until it is fixed.
        var parameters = _draft.Parameters with
        {
            WindowWidth = GameLaunchDefaults.WindowDimension(WidthBox.Text) ?? _draft.Parameters.WindowWidth,
            WindowHeight = GameLaunchDefaults.WindowDimension(HeightBox.Text) ?? _draft.Parameters.WindowHeight
        };
        Commit(_draft with { Parameters = parameters });
    }

    private void OfflineSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            Commit(_draft with { OfflineMode = OfflineSwitch.IsOn });
        }
    }

    private void OfflineNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading)
        {
            Commit(_draft with { OfflineUsername = OfflineNameBox.Text });
        }
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}

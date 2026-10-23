using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Hako;

internal sealed record ModpackSelection(ModrinthProject Project, ModrinthVersion Version, string? MinecraftFilter);
internal sealed record ModpackCreationContext(LauncherServices Services, ModpackSelection? Selection);

/// <summary>Creates a build from one pinned Modrinth modpack version.</summary>
public sealed partial class ModpackCreationPage : Page, INavigationGuard
{
    private LauncherServices? _services;
    private ModpackSelection? _selection;
    private PreparedModpack? _prepared;
    private Exception? _prepareError;
    private CancellationTokenSource? _prepareCancellation;
    private DispatcherQueueTimer? _nameTimer;
    private readonly NameValidation _nameValidation = new();
    private string? _nameToValidate;
    private string? _validatedName;
    private string? _nameErrorKey;
    private GameLaunchDefaults _global = GameLaunchDefaults.Standard;
    private bool _isLoaded;
    private bool _checking;
    private bool _saving;
    private bool _leaving;
    private bool _iconEdited;
    private long _iconGeneration;

    public ModpackCreationPage()
    {
        InitializeComponent();
        ParametersEditor.Changed += (_, _) => UpdateSaveState();
        IconPicker.Changed += (_, _) => _iconEdited = true;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var context = (ModpackCreationContext)e.Parameter;
        _services = context.Services;
        _selection = context.Selection;
        if (_selection is null)
        {
            _prepareError = new ModpackException(ModpackError.InvalidManifest);
            _isLoaded = true;
            RefreshStrings();
            ShowError(_prepareError);
            return;
        }

        _isLoaded = true;
        _services.Store.InstancesChanged += Store_InstancesChanged;
        _global = _services.Settings.GetGameLaunchDefaults();
        var defaultArchitecture = Environment.Is64BitOperatingSystem ? GameArchitecture.X64 : GameArchitecture.X86;
        var draft = new InstanceDraft
        {
            Parameters = new InstanceParameters
            {
                Fullscreen = _global.Fullscreen,
                WindowWidth = _global.WindowWidth,
                WindowHeight = _global.WindowHeight,
                MaximumMemoryMiB = JavaMemorySlider.Policy.InitialMiB(defaultArchitecture)
            }
        };
        ParametersEditor.Load(draft, null, defaultArchitecture, _global);
        IconPicker.Load(InstanceIconGlyphs.DefaultSymbol, null);
        _iconEdited = false;
        NameBox.Text = _selection.Project.Title.Length > 60 ? _selection.Project.Title[..60] : _selection.Project.Title;
        _nameTimer = DispatcherQueue.CreateTimer();
        _nameTimer.Interval = TimeSpan.FromMilliseconds(300);
        _nameTimer.IsRepeating = false;
        _nameTimer.Tick += (_, _) => _ = ValidateNameAsync();
        HakoStrings.LanguageChanged += HakoStrings_LanguageChanged;
        RefreshStrings();
        _ = PrepareAsync();
        _ = LoadIconAsync(_selection.Project);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _isLoaded = false;
        _iconGeneration++;
        if (_services is { } services) services.Store.InstancesChanged -= Store_InstancesChanged;
        HakoStrings.LanguageChanged -= HakoStrings_LanguageChanged;
        _nameTimer?.Stop();
        InvalidateNameValidation();
        _prepareCancellation?.Cancel();
        base.OnNavigatedFrom(e);
    }

    public async Task<bool> ConfirmLeaveAsync()
    {
        if (_leaving) return true;
        var answer = await HakoDialogs.ConfirmAsync(
            HakoStrings.Get("ModpackCancelTitle"),
            HakoStrings.Get("ModpackCancelMessage"),
            HakoStrings.Get("ModpackCancelConfirm"),
            HakoStrings.Get("CreationContinue"),
            destructive: true);
        _leaving = answer == ContentDialogResult.Primary;
        return _leaving;
    }

    private void HakoStrings_LanguageChanged(object? sender, EventArgs e) => RefreshStrings();

    private void RefreshStrings()
    {
        TitleText.Text = HakoStrings.Get("ModpackCreateTitle");
        NameLabel.Text = HakoStrings.Get("CreationNameLabel");
        NameBox.PlaceholderText = HakoStrings.Get("CreationNamePlaceholder");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NameBox, HakoStrings.Get("CreationNameLabel"));
        VersionTitle.Text = HakoStrings.Get("ModpackPinnedVersion");
        CheckingText.Text = HakoStrings.Get("ModpackPreparing");
        RetryButton.Content = HakoStrings.Get("ModpackRetryPrepare");
        FooterText.Text = HakoStrings.Get("ModpackCreateFooter");
        CancelButton.Content = HakoStrings.Get("CancelButton");
        SaveButton.Content = HakoStrings.Get("ModpackCreateSave");
        AdvancedExpander.Header = HakoStrings.Get("CreationAdvanced");
        IconPicker.RefreshStrings();
        ParametersEditor.RefreshStrings();
        UpdatePinnedInfo();
        UpdateName();
        UpdatePrepareState();
    }

    private void UpdatePinnedInfo()
    {
        if (_selection is not { } selection) return;
        VersionText.Text = string.Format(HakoStrings.Get("ModpackPinnedVersionValue"), selection.Project.Title,
            selection.Version.VersionNumber, selection.Version.DatePublished.ToLocalTime().ToString("d"));
        if (_prepared is { } prepared)
        {
            var loader = prepared.Plan.Manifest.LoaderVersion is { } version
                ? string.Format(HakoStrings.Get("ModpackPinnedFabric"), version)
                : HakoStrings.Get("ModpackPinnedVanilla");
            LoaderText.Text = string.Format(HakoStrings.Get("ModpackPinnedLoaderValue"), prepared.Plan.Manifest.Minecraft, loader);
        }
        else
        {
            LoaderText.Text = HakoStrings.Get("ModpackPinnedLoaderChecking");
        }
    }

    private async Task PrepareAsync()
    {
        if (_services is not { } services || _selection is not { } selection) return;
        _prepareCancellation?.Cancel();
        var cancellation = _prepareCancellation = new CancellationTokenSource();
        _checking = true;
        _prepareError = null;
        _prepared = null;
        UpdatePrepareState();
        try
        {
            var prepared = await services.Modpacks.PrepareAsync(selection.Project, selection.Version,
                selection.MinecraftFilter, cancellation.Token);
            if (!IsCurrent(cancellation)) return;
            _prepared = prepared;
            var draft = ParametersEditor.Draft with
            {
                ModLoader = prepared.Plan.Manifest.LoaderVersion is null ? InstanceModLoader.Vanilla : InstanceModLoader.Fabric,
                FabricConfiguration = prepared.Plan.Manifest.LoaderVersion is { } loader ? new FabricConfiguration(loader) : null,
                JavaMajorVersion = prepared.Minecraft.Manifest.Java.MajorVersion,
                LegacyTexturepacks = prepared.Minecraft.Manifest.LegacyTexturepacks
            };
            ParametersEditor.Load(draft, prepared.Minecraft.Manifest, prepared.Minecraft.Architecture, _global);
            ParametersEditor.SetFabricProfile(prepared.FabricProfile);
            AdvancedExpander.IsEnabled = true;
            PreparedText.Text = string.Format(HakoStrings.Get("ModpackPrepared"), prepared.Minecraft.Manifest.Java.MajorVersion);
            UpdatePinnedInfo();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrent(cancellation))
            {
                _prepareError = exception;
                ShowError(exception);
            }
        }
        finally
        {
            if (IsCurrent(cancellation))
            {
                _checking = false;
                UpdatePrepareState();
            }
        }
    }

    private bool IsCurrent(CancellationTokenSource cancellation) => _isLoaded && !cancellation.IsCancellationRequested
        && ReferenceEquals(_prepareCancellation, cancellation);

    private async Task LoadIconAsync(ModrinthProject project)
    {
        if (_services is not { } services) return;
        var generation = ++_iconGeneration;
        try
        {
            var bytes = await services.Modpacks.DownloadIconAsync(project);
            if (!CanApplyDefaultIcon(generation) || bytes is null) return;
            var normalized = await InstanceIconImage.NormalizeAsync(bytes);
            if (!CanApplyDefaultIcon(generation)) return;
            var image = await InstanceIconSource.CreateAsync(normalized);
            if (!CanApplyDefaultIcon(generation)) return;
            IconPicker.LoadCustom(normalized, image);
            UpdateSaveState();
        }
        catch (Exception)
        {
            // Invalid or unavailable project artwork leaves the standard editable icon selected.
        }
    }

    private bool CanApplyDefaultIcon(long generation) =>
        _isLoaded && generation == _iconGeneration && !_iconEdited;

    private void ShowError(Exception exception)
    {
        ErrorText.Text = InstanceText.Error(exception);
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void UpdatePrepareState()
    {
        CheckingPanel.Visibility = _checking ? Visibility.Visible : Visibility.Collapsed;
        PreparedText.Visibility = _prepared is null ? Visibility.Collapsed : Visibility.Visible;
        ErrorPanel.Visibility = _prepareError is null ? Visibility.Collapsed : Visibility.Visible;
        if (_prepareError is not null) ErrorText.Text = InstanceText.Error(_prepareError);
        UpdateSaveState();
    }

    private void UpdateName()
    {
        var name = NameBox.Text;
        NameCounter.Text = $"{name.Length}/60";
        if (_validatedName != name && _nameToValidate != name && _nameTimer is { } timer)
        {
            _nameValidation.Invalidate();
            _nameToValidate = name;
            timer.Stop();
            timer.Start();
        }

        var showError = _validatedName == name && _nameErrorKey is not null && name.Length > 0;
        NameHint.Text = showError ? HakoStrings.Get(_nameErrorKey!) : HakoStrings.Get("CreationNameHint");
        NameHint.Foreground = (Brush)Application.Current.Resources[showError ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];
        UpdateSaveState();
    }

    private async Task ValidateNameAsync()
    {
        if (!_isLoaded || _services is not { } services || _nameToValidate is not { } name) return;
        var result = await _nameValidation.ValidateAsync(name, input => Task.Run(() =>
        {
            try { services.Store.ValidateName(input); return null; }
            catch (Exception exception) { return InstallationResourceKeys.ErrorResourceKey(exception); }
        }));
        if (!_isLoaded || !result.IsCurrent || NameBox.Text != name || _nameToValidate != name) return;
        _nameToValidate = null;
        _validatedName = name;
        _nameErrorKey = result.ErrorKey;
        UpdateName();
    }

    private void UpdateSaveState()
    {
        SaveButton.IsEnabled = !_saving && _prepared is not null && !_checking
            && _validatedName == NameBox.Text && _nameErrorKey is null
            && ParametersEditor.IsValid && _services?.Store.LoadError is null;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateSaveState();
        if (!SaveButton.IsEnabled || _services is not { } services || _prepared is not { } prepared) return;
        _saving = true;
        UpdateSaveState();
        SaveErrorText.Visibility = Visibility.Collapsed;
        var draft = ParametersEditor.Draft with
        {
            Name = NameBox.Text,
            IconSymbol = IconPicker.Symbol,
            IconData = IconPicker.IconData
        };
        try
        {
            await services.Installations.CreateAndEnqueueModpackAsync(draft, prepared);
            _leaving = true;
            services.App.OpenBuilds();
        }
        catch (Exception exception)
        {
            SaveErrorText.Text = InstanceText.Error(exception);
            SaveErrorText.Visibility = Visibility.Visible;
            _saving = false;
            _validatedName = null;
            UpdateName();
        }
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateName();
    private void Store_InstancesChanged(object? sender, EventArgs e)
    {
        _nameValidation.Invalidate();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isLoaded) return;
            _validatedName = null;
            _nameToValidate = null;
            _nameErrorKey = null;
            _nameTimer?.Stop();
            UpdateName();
        });
    }

    private void InvalidateNameValidation()
    {
        _nameValidation.Invalidate();
        _validatedName = null;
        _nameToValidate = null;
        _nameErrorKey = null;
        _nameTimer?.Stop();
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e) => await PrepareAsync();
    private async void CancelButton_Click(object sender, RoutedEventArgs e) => await CancelAsync();
    private async void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await CancelAsync();
    }

    private async Task CancelAsync()
    {
        if (await ConfirmLeaveAsync()) _services?.App.OpenBuilds();
    }
}

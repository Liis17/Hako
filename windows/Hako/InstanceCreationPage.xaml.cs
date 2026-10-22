using System.Collections.Generic;
using System.Linq;
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

/// <summary>«Своя сборка» (InstanceCreationView on macOS): icon, name, Mojang version with a compatibility check, loader and parameters.</summary>
public sealed partial class InstanceCreationPage : Page, INavigationGuard
{
    private LauncherServices? _services;
    private MojangCatalog? _catalog;
    private List<MojangVersionEntry> _visible = [];
    private string _selectedId = "";
    private PreparedInstallation? _prepared;
    private FabricPreparation? _fabricPreparation;
    private FabricProfile? _fabricProfile;
    private string? _fabricVersionId;
    private int? _fabricJavaMajor;
    private string? _selectedLoaderVersion;
    private string? _validatedFabricSelectionKey;
    private Exception? _compatibilityError;
    private Exception? _catalogError;
    private CancellationTokenSource? _catalogCancellation;
    private CancellationTokenSource? _checkCancellation;
    private string? _nameErrorKey;
    private string? _nameToValidate;
    private string? _validatedName;
    private readonly NameValidation _nameValidation = new();
    private DispatcherQueueTimer? _nameTimer;
    private bool _checking;
    private bool _saving;
    private bool _leaving;
    private bool _updatingVersions;
    private bool _updatingLoaderVersions;
    private bool _isLoaded;

    public InstanceCreationPage()
    {
        InitializeComponent();
        ParametersEditor.Changed += (_, _) => UpdateSaveState();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = (LauncherServices)e.Parameter;
        _isLoaded = true;
        _services.Store.InstancesChanged += Store_InstancesChanged;
        var global = _services.Settings.GetGameLaunchDefaults();
        var draft = new InstanceDraft
        {
            Parameters = new InstanceParameters
            {
                Fullscreen = global.Fullscreen,
                WindowWidth = global.WindowWidth,
                WindowHeight = global.WindowHeight,
                MaximumMemoryMiB = JavaMemorySlider.Policy.InitialMiB(DefaultArchitecture)
            }
        };
        _nameTimer = DispatcherQueue.CreateTimer();
        _nameTimer.Interval = TimeSpan.FromMilliseconds(300);
        _nameTimer.IsRepeating = false;
        _nameTimer.Tick += (_, _) => _ = ValidateNameAsync();
        ParametersEditor.Load(draft, null, DefaultArchitecture, global);
        IconPicker.Load(InstanceIconGlyphs.DefaultSymbol, null);
        HakoStrings.LanguageChanged += HakoStrings_LanguageChanged;
        RefreshStrings();
        _ = LoadCatalogAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _isLoaded = false;
        if (_services is { } services) services.Store.InstancesChanged -= Store_InstancesChanged;
        HakoStrings.LanguageChanged -= HakoStrings_LanguageChanged;
        InvalidateNameValidation();
        _catalogCancellation?.Cancel();
        _checkCancellation?.Cancel();
        base.OnNavigatedFrom(e);
    }

    private static GameArchitecture DefaultArchitecture => Environment.Is64BitOperatingSystem ? GameArchitecture.X64 : GameArchitecture.X86;

    public async Task<bool> ConfirmLeaveAsync()
    {
        if (_leaving)
        {
            return true;
        }

        var answer = await HakoDialogs.ConfirmAsync(
            HakoStrings.Get("CreationCancelTitle"),
            HakoStrings.Get("CreationCancelMessage"),
            HakoStrings.Get("CreationCancelConfirm"),
            HakoStrings.Get("CreationContinue"),
            destructive: true);
        _leaving = answer == ContentDialogResult.Primary;
        return _leaving;
    }

    private void HakoStrings_LanguageChanged(object? sender, EventArgs e) => RefreshStrings();

    private void RefreshStrings()
    {
        TitleText.Text = HakoStrings.Get("CreationTitle");
        NameLabel.Text = HakoStrings.Get("CreationNameLabel");
        NameBox.PlaceholderText = HakoStrings.Get("CreationNamePlaceholder");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NameBox, HakoStrings.Get("CreationNameLabel"));
        VersionLabel.Text = HakoStrings.Get("CreationVersionLabel");
        CatalogLoadingText.Text = HakoStrings.Get("CreationCatalogLoading");
        CatalogRetryButton.Content = HakoStrings.Get("CreationCatalogRetry");
        ReleasesFilter.Text = HakoStrings.Get("CreationFilterReleases");
        SnapshotsFilter.Text = HakoStrings.Get("CreationFilterSnapshots");
        HistoricalFilter.Text = HakoStrings.Get("CreationFilterHistorical");
        AllFilter.Text = HakoStrings.Get("CreationFilterAll");
        SearchBox.PlaceholderText = HakoStrings.Get("CreationSearchPlaceholder");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(VersionBox, HakoStrings.Get("CreationVersionLabel"));
        NoVersionsText.Text = HakoStrings.Get("CreationNoVersions");
        CheckingText.Text = HakoStrings.Get("CreationChecking");
        CompatibilityRetryButton.Content = HakoStrings.Get("CreationCheckRetry");
        LoaderLabel.Text = HakoStrings.Get("CreationLoaderLabel");
        VanillaLoaderItem.Content = HakoStrings.Get("CreationLoaderVanilla");
        FabricLoaderItem.Content = HakoStrings.Get("CreationLoaderFabric");
        FabricLoaderLabel.Text = HakoStrings.Get("CreationFabricLoaderLabel");
        FabricVersionBox.PlaceholderText = HakoStrings.Get("CreationFabricLoaderPlaceholder");
        AdvancedExpander.Header = HakoStrings.Get("CreationAdvanced");
        FooterText.Text = HakoStrings.Get("CreationFooter");
        CancelButton.Content = HakoStrings.Get("CancelButton");
        SaveButton.Content = HakoStrings.Get("CreationSave");
        IconPicker.RefreshStrings();
        ParametersEditor.RefreshStrings();
        UpdateName();
        UpdateCatalogState();
    }

    private async Task LoadCatalogAsync()
    {
        if (_services is not { } services)
        {
            return;
        }

        _catalogCancellation?.Cancel();
        var cancellation = _catalogCancellation = new CancellationTokenSource();
        _catalogError = null;
        _catalog = null;
        UpdateCatalogState();
        try
        {
            var catalog = await services.Mojang.CatalogAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            _catalog = catalog;
            _selectedId = catalog.Latest.Release ?? catalog.Versions.FirstOrDefault()?.Id ?? "";
        }
        catch (Exception exception) when (!cancellation.IsCancellationRequested)
        {
            _catalogError = exception;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        UpdateCatalogState();
        UpdateVersions();
    }

    private void UpdateCatalogState()
    {
        CatalogLoading.Visibility = _catalog is null && _catalogError is null ? Visibility.Visible : Visibility.Collapsed;
        CatalogErrorPanel.Visibility = _catalogError is null ? Visibility.Collapsed : Visibility.Visible;
        CatalogErrorText.Text = _catalogError is null ? "" : InstanceText.Error(_catalogError);
        CatalogPanel.Visibility = _catalog is null ? Visibility.Collapsed : Visibility.Visible;
        FabricLoaderPanel.Visibility = IsFabric ? Visibility.Visible : Visibility.Collapsed;
        CheckingPanel.Visibility = _checking ? Visibility.Visible : Visibility.Collapsed;
        SupportedText.Visibility = _prepared is null ? Visibility.Collapsed : Visibility.Visible;
        SupportedText.Text = _prepared is null ? ""
            : IsFabric && _fabricPreparation is not null
                ? string.Format(HakoStrings.Get("CreationFabricSupported"), _fabricPreparation.Api.Version, _prepared.Manifest.Java.MajorVersion)
                : string.Format(HakoStrings.Get("CreationSupported"), _prepared.Manifest.Java.MajorVersion);
        CompatibilityErrorPanel.Visibility = _compatibilityError is null ? Visibility.Collapsed : Visibility.Visible;
        CompatibilityErrorText.Text = _compatibilityError is null ? "" : InstanceText.Error(_compatibilityError);
        CompatibilityRetryButton.Visibility = !IsFabric && _compatibilityError is MojangException { Error: MojangError.Unsupported }
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateSaveState();
    }

    private bool Includes(MojangVersionEntry version) => (FilterBar.SelectedItem?.Tag as string) switch
    {
        "snapshots" => version.Type == "snapshot",
        "historical" => version.Type is "old_alpha" or "old_beta",
        "all" => true,
        _ => version.Type == "release"
    };

    private void UpdateVersions()
    {
        if (_catalog is null)
        {
            return;
        }

        var search = SearchBox.Text.Trim();
        _visible = _catalog.Versions
            .Where(version => Includes(version) && (search.Length == 0 || version.Id.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var priorSelectedId = _selectedId;
        if (!_visible.Any(version => version.Id == _selectedId))
        {
            _selectedId = _visible.FirstOrDefault()?.Id ?? "";
        }

        if (_selectedId != priorSelectedId)
        {
            _prepared = null;
            _fabricPreparation = null;
            _fabricVersionId = null;
            _fabricJavaMajor = null;
            _fabricProfile = null;
            _selectedLoaderVersion = null;
            _validatedFabricSelectionKey = null;
        }

        _updatingVersions = true;
        VersionBox.ItemsSource = _visible.Select(version => version.Id).ToList();
        VersionBox.SelectedIndex = _visible.FindIndex(version => version.Id == _selectedId);
        _updatingVersions = false;
        VersionBox.Visibility = _visible.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoVersionsText.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _ = CheckCompatibilityAsync();
    }

    private bool IsFabric => (LoaderBox.SelectedItem as ComboBoxItem)?.Tag as string == "fabric";

    private string SelectionKey() => $"{_selectedId}\0{(IsFabric ? "fabric" : "vanilla")}\0{(IsFabric ? _selectedLoaderVersion : "")}";

    private async Task CheckCompatibilityAsync()
    {
        if (_services is not { } services) return;
        var version = _catalog?.Versions.FirstOrDefault(item => item.Id == _selectedId);
        if (version is null)
        {
            _checkCancellation?.Cancel();
            _prepared = null;
            _fabricPreparation = null;
            _fabricProfile = null;
            _checking = false;
            UpdateCatalogState();
            return;
        }

        var fabric = IsFabric;
        var selectionKey = SelectionKey();
        if (_prepared?.Version.Id == version.Id && _validatedFabricSelectionKey == selectionKey
            && (!fabric || _fabricProfile is not null)) return;

        _checkCancellation?.Cancel();
        var cancellation = _checkCancellation = new CancellationTokenSource();
        if (_prepared?.Version.Id != version.Id)
        {
            _prepared = null;
            _fabricPreparation = null;
            _fabricVersionId = null;
            _fabricJavaMajor = null;
            _fabricProfile = null;
            _selectedLoaderVersion = null;
            _validatedFabricSelectionKey = null;
            ParametersEditor.SetFabricProfile(null);
            _updatingLoaderVersions = true;
            FabricVersionBox.ItemsSource = null;
            FabricVersionBox.SelectedItem = null;
            _updatingLoaderVersions = false;
        }

        _compatibilityError = null;
        _checking = true;
        UpdateCatalogState();
        try
        {
            if (_prepared is null)
            {
                var prepared = await services.Mojang.PrepareAsync(version, cancellation.Token);
                if (!IsCurrent(cancellation, version.Id, fabric)) return;
                _prepared = prepared;
                ParametersEditor.SetManifest(prepared.Manifest, prepared.Architecture);
            }

            if (!fabric)
            {
                _fabricProfile = null;
                ParametersEditor.SetFabricProfile(null);
                _validatedFabricSelectionKey = SelectionKey();
                return;
            }

            var java = _prepared!.Manifest.Java.MajorVersion;
            if (_fabricPreparation is null || _fabricVersionId != version.Id || _fabricJavaMajor != java)
            {
                var currentPreparation = await FabricPreparationCommit.PrepareAndCommitIfCurrentAsync(
                    () => services.Fabric.PrepareAsync(version.Id, java, cancellation.Token),
                    () => IsCurrent(cancellation, version.Id, fabric),
                    preparation => _fabricPreparation = preparation);
                if (!currentPreparation) return;
                _fabricVersionId = version.Id;
                _fabricJavaMajor = java;
                PopulateLoaderVersions();
            }

            if (_selectedLoaderVersion is not { } loader
                || !_fabricPreparation!.Loaders.Any(item => item.Version == loader))
            {
                throw new MojangException(MojangError.Unsupported, "Choose a Fabric Loader version compatible with this Java version.");
            }

            selectionKey = SelectionKey();
            var loaded = await services.Fabric.GetProfileAsync(version.Id, loader, cancellation.Token);
            if (!IsCurrent(cancellation, version.Id, fabric: true) || SelectionKey() != selectionKey) return;
            _fabricProfile = loaded.Profile;
            ParametersEditor.SetFabricProfile(loaded.Profile);
            _validatedFabricSelectionKey = selectionKey;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrent(cancellation, version.Id, fabric))
            {
                _compatibilityError = exception;
                _validatedFabricSelectionKey = null;
                _fabricProfile = null;
                ParametersEditor.SetFabricProfile(null);
            }
        }
        finally
        {
            if (IsCurrent(cancellation, version.Id, fabric))
            {
                _checking = false;
                UpdateCatalogState();
            }
        }
    }

    private bool IsCurrent(CancellationTokenSource cancellation, string versionId, bool fabric) =>
        _isLoaded && !cancellation.IsCancellationRequested && _selectedId == versionId && IsFabric == fabric;

    private void PopulateLoaderVersions()
    {
        var loaders = _fabricPreparation?.Loaders ?? [];
        var selected = loaders.FirstOrDefault(loader => loader.Version == _selectedLoaderVersion)
            ?? loaders.FirstOrDefault(loader => loader.Stable)
            ?? loaders.FirstOrDefault();
        _updatingLoaderVersions = true;
        FabricVersionBox.ItemsSource = loaders.Select(loader => new FabricLoaderChoice(
            loader.Version + (loader.Stable ? HakoStrings.Get("CreationFabricLoaderStable") : ""), loader.Version, loader.Stable)).ToList();
        FabricVersionBox.SelectedItem = selected is null ? null : FabricVersionBox.Items.Cast<FabricLoaderChoice>()
            .FirstOrDefault(item => item.Version == selected.Version);
        _selectedLoaderVersion = selected?.Version;
        _updatingLoaderVersions = false;
    }

    private sealed record FabricLoaderChoice(string Display, string Version, bool Stable);

    private void UpdateName()
    {
        var name = NameBox.Text;
        NameCounter.Text = $"{name.Length}/60";
        if (_validatedName != name && _nameToValidate != name && _nameTimer is { } timer)
        {
            // ValidateName waits for the store gate and checks the disk, so it runs off the UI thread after typing pauses.
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
        if (!_isLoaded || _services is not { } services || _nameToValidate is not { } name)
        {
            return;
        }

        var result = await _nameValidation.ValidateAsync(name, input => Task.Run(() =>
        {
            try
            {
                services.Store.ValidateName(input);
                return null;
            }
            catch (Exception exception)
            {
                return InstallationResourceKeys.ErrorResourceKey(exception);
            }
        }));
        if (!_isLoaded || !result.IsCurrent || NameBox.Text != name || _nameToValidate != name)
        {
            return;
        }

        _nameToValidate = null;
        _validatedName = name;
        _nameErrorKey = result.ErrorKey;
        UpdateName();
    }

    private void UpdateSaveState()
    {
        var fabricReady = !IsFabric || (_fabricPreparation is not null
            && _fabricProfile is not null
            && _selectedLoaderVersion is not null
            && _fabricPreparation.Loaders.Any(loader => loader.Version == _selectedLoaderVersion)
            && _validatedFabricSelectionKey == SelectionKey());
        SaveButton.IsEnabled = !_saving
            && _prepared is not null
            && _prepared.Version.Id == _selectedId
            && !_checking
            && fabricReady
            && _validatedName == NameBox.Text
            && _nameErrorKey is null
            && ParametersEditor.IsValid
            && _services?.Store.LoadError is null;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateSaveState();
        if (!SaveButton.IsEnabled || _services is not { } services || _prepared is not { } prepared)
        {
            return;
        }

        _saving = true;
        UpdateSaveState();
        SaveErrorText.Visibility = Visibility.Collapsed;
        var draft = ParametersEditor.Draft with
        {
            Name = NameBox.Text,
            IconSymbol = IconPicker.Symbol,
            IconData = IconPicker.IconData,
            ModLoader = IsFabric ? InstanceModLoader.Fabric : InstanceModLoader.Vanilla,
            FabricConfiguration = IsFabric && _fabricPreparation is not null && _selectedLoaderVersion is not null
                ? new FabricConfiguration(_selectedLoaderVersion, _fabricPreparation.Api)
                : null
        };
        try
        {
            await services.Installations.CreateAndEnqueueAsync(draft, prepared, prepared.Manifest.LegacyTexturepacks);
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

    private async void CancelButton_Click(object sender, RoutedEventArgs e) => await CancelAsync();

    private async void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await CancelAsync();
    }

    private async Task CancelAsync()
    {
        if (await ConfirmLeaveAsync())
        {
            _services?.App.OpenBuilds();
        }
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateName();

    private void CatalogRetryButton_Click(object sender, RoutedEventArgs e) => _ = LoadCatalogAsync();

    private void CompatibilityRetryButton_Click(object sender, RoutedEventArgs e)
    {
        _prepared = null;
        _ = CheckCompatibilityAsync();
    }

    private void FilterBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => UpdateVersions();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateVersions();

    private void VersionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingVersions || VersionBox.SelectedItem is not string id || id == _selectedId)
        {
            return;
        }

        _selectedId = id;
        _ = CheckCompatibilityAsync();
    }

    private void LoaderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        _compatibilityError = null;
        _fabricProfile = null;
        _validatedFabricSelectionKey = null;
        ParametersEditor.SetFabricProfile(null);
        UpdateCatalogState();
        _ = CheckCompatibilityAsync();
    }

    private void FabricVersionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingLoaderVersions || !_isLoaded || FabricVersionBox.SelectedItem is not FabricLoaderChoice loader
            || loader.Version == _selectedLoaderVersion) return;
        _selectedLoaderVersion = loader.Version;
        _fabricProfile = null;
        _validatedFabricSelectionKey = null;
        _compatibilityError = null;
        ParametersEditor.SetFabricProfile(null);
        UpdateCatalogState();
        _ = CheckCompatibilityAsync();
    }

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
}

using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;

namespace Hako;

public sealed partial class InstanceWorldsView : UserControl
{
    private readonly ObservableCollection<InstanceWorldRow> _rows = [];
    private LauncherServices? _services;
    private GameInstance? _instance;
    private ProfilePreviewOptions? _preview;
    private CoalescedRefresh? _refresh;
    private CoalescedRefresh? _listRefresh;
    private CancellationTokenSource? _loadCancellation;
    private long _loadGeneration;
    private bool _active;
    private bool _loading;
    private bool _operationBusy;
    private bool _previewCatalogOpened;
    private string? _expandedWorldId;
    private string? _loadError;
    private string? _feedback;

    public InstanceWorldsView()
    {
        InitializeComponent();
        WorldItems.ItemsSource = _rows;
        Loaded += (_, _) => HakoStrings.LanguageChanged += HakoStrings_LanguageChanged;
        Unloaded += (_, _) => HakoStrings.LanguageChanged -= HakoStrings_LanguageChanged;
        RefreshStrings();
    }

    internal void Configure(LauncherServices services, GameInstance instance, ProfilePreviewOptions? preview)
    {
        var changed = _instance?.Id != instance.Id;
        _services = services;
        _instance = instance;
        _preview = preview;
        if (changed)
        {
            CancelListLoad();
            _rows.Clear();
            _expandedWorldId = null;
            _loadError = null;
            _feedback = null;
            _previewCatalogOpened = false;
        }

        RefreshStrings();
        if (changed && _active) LoadWorlds();
    }

    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (active)
        {
            if (_services is { } services)
            {
                _refresh = new CoalescedRefresh(DispatcherQueue, RefreshAvailability);
                _listRefresh = new CoalescedRefresh(DispatcherQueue, LoadWorlds);
                services.Store.BusyStateChanged += CoreStateChanged;
                services.Store.InstancesChanged += Store_InstancesChanged;
                services.Installations.PropertyChanged += CorePropertyChanged;
                services.Games.RunStateChanged += Games_RunStateChanged;
                services.Games.GameSessionEnded += Games_GameSessionEnded;
            }

            LoadWorlds();
            foreach (var row in _rows.Where(row => row.IsExpanded)) ConfigureDatapacksView(row);
        }
        else
        {
            if (_services is { } services)
            {
                services.Store.BusyStateChanged -= CoreStateChanged;
                services.Store.InstancesChanged -= Store_InstancesChanged;
                services.Installations.PropertyChanged -= CorePropertyChanged;
                services.Games.RunStateChanged -= Games_RunStateChanged;
                services.Games.GameSessionEnded -= Games_GameSessionEnded;
            }

            _refresh?.Stop();
            _refresh = null;
            _listRefresh?.Stop();
            _listRefresh = null;
            CancelListLoad();
            foreach (var row in _rows) row.ContentView?.SetActive(false);
        }

        RefreshAvailability();
    }

    public void RefreshWorldsIfActive()
    {
        if (_active) LoadWorlds();
    }

    private void HakoStrings_LanguageChanged(object? sender, EventArgs e) => RefreshStrings();

    internal void RefreshStrings()
    {
        WorldsTitle.Text = HakoStrings.Get("WorldsTitle");
        RefreshButton.Content = HakoStrings.Get("WorldsRefresh");
        OpenSavesButton.Content = HakoStrings.Get("WorldsOpenSaves");
        WorldLoadingText.Text = HakoStrings.Get("WorldsLoading");
        WorldEmptyText.Text = HakoStrings.Get("WorldsEmpty");
        WorldOperationText.Text = HakoStrings.Get("WorldsOperationInProgress");
        foreach (var row in _rows) row.UpdateLocalizedText(_services?.App.Language ?? AppLanguages.Default);
        RefreshAvailability();
    }

    private void CoreStateChanged(object? sender, EventArgs e) => _refresh?.Request();

    private void Store_InstancesChanged(object? sender, EventArgs e)
    {
        _refresh?.Request();
        _listRefresh?.Request();
    }

    private void CorePropertyChanged(object? sender, PropertyChangedEventArgs e) => _refresh?.Request();

    private void Games_RunStateChanged(object? sender, GameRunStateChangedEventArgs e)
    {
        if (_instance?.Id == e.InstanceId) _refresh?.Request();
    }

    private void Games_GameSessionEnded(object? sender, GameSessionEventArgs e)
    {
        if (_instance?.Id == e.InstanceId) _listRefresh?.Request();
    }

    private void RefreshAvailability()
    {
        var reason = BlockReason();
        WorldBlockReasonText.Text = reason ?? "";
        WorldBlockReasonText.Visibility = reason is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.IsEnabled = _active && !_loading;
        OpenSavesButton.IsEnabled = _instance?.State == InstanceInstallationState.Ready;
        WorldLoadingPanel.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
        WorldErrorText.Text = _loadError ?? "";
        WorldErrorText.Visibility = _loadError is null ? Visibility.Collapsed : Visibility.Visible;
        WorldEmptyText.Visibility = !_loading && _loadError is null && _rows.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        WorldOperationPanel.Visibility = _operationBusy ? Visibility.Visible : Visibility.Collapsed;
        WorldActionFeedbackText.Text = _feedback ?? "";
        WorldActionFeedbackText.Visibility = _feedback is null ? Visibility.Collapsed : Visibility.Visible;
        foreach (var row in _rows)
            row.UpdateAvailability(reason is null, reason);
    }

    private string? BlockReason()
    {
        if (_instance is not { } instance || _services is not { } services) return null;
        if (_operationBusy) return HakoStrings.Get("WorldsOperationBusy");
        if (instance.State is InstanceInstallationState.Queued or InstanceInstallationState.Installing)
            return HakoStrings.Get("ContentBlockedInstall");
        if (instance.State != InstanceInstallationState.Ready)
            return HakoStrings.Get("ContentBlockedBuild");
        if (services.Store.ManagementBlockedError(instance) is { } blocked)
            return HakoStrings.Get(blocked.ResourceKey());
        return services.Store.IsContentBusy(instance.Id) ? HakoStrings.Get("ContentBlockedFilesBusy") : null;
    }

    private void LoadWorlds()
    {
        if (!_active || _services is not { } services || _instance is not { } instance) return;
        CancelListLoad();
        _loadCancellation = new CancellationTokenSource();
        var cancellationToken = _loadCancellation.Token;
        var generation = ++_loadGeneration;
        _loading = true;
        _loadError = null;
        RefreshAvailability();
        _ = LoadWorldsAsync(services, instance, generation, cancellationToken);
    }

    private async Task LoadWorldsAsync(LauncherServices services, GameInstance instance, long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var worlds = await services.Worlds.ListAsync(instance.Id, cancellationToken);
            if (!IsCurrentLoad(instance.Id, generation, cancellationToken)) return;
            await ApplyWorldsAsync(worlds, generation, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (IsCurrentLoad(instance.Id, generation, cancellationToken))
                _loadError = FormatWorldException(exception);
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                _loading = false;
                RefreshAvailability();
            }
        }
    }

    private async Task ApplyWorldsAsync(IReadOnlyList<InstanceWorld> worlds, long generation,
        CancellationToken cancellationToken)
    {
        var existing = _rows.ToDictionary(row => row.WorldId, StringComparer.OrdinalIgnoreCase);
        var next = new List<InstanceWorldRow>(worlds.Count);
        foreach (var world in worlds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentLoad(_instance?.Id ?? Guid.Empty, generation, cancellationToken)) return;
            var icon = await InstanceIconSource.CreateAsync(world.IconData);
            if (cancellationToken.IsCancellationRequested || generation != _loadGeneration) return;
            if (!existing.TryGetValue(world.Id, out var row)) row = new InstanceWorldRow(world.Id);
            row.Update(world, icon, _services?.App.Language ?? AppLanguages.Default);
            next.Add(row);
        }

        foreach (var removed in _rows.Where(row => next.All(item => item.WorldId != row.WorldId)).ToArray())
        {
            removed.ContentView?.SetActive(false);
            _rows.Remove(removed);
        }

        for (var index = 0; index < next.Count; index++)
        {
            var row = next[index];
            row.IsExpanded = row.WorldId == _expandedWorldId;
            if (index < _rows.Count && ReferenceEquals(_rows[index], row)) continue;
            var oldIndex = _rows.IndexOf(row);
            if (oldIndex >= 0) _rows.Move(oldIndex, index);
            else _rows.Insert(index, row);
        }

        while (_rows.Count > next.Count) _rows.RemoveAt(_rows.Count - 1);
        if (_expandedWorldId is not null && next.All(row => row.WorldId != _expandedWorldId)) _expandedWorldId = null;
        if (_expandedWorldId is null && _preview?.Page is PreviewPage.Datapacks or PreviewPage.DatapackCatalog
            && next.Any(row => row.WorldId == "Survival World"))
            _expandedWorldId = "Survival World";
        foreach (var row in _rows) row.IsExpanded = row.WorldId == _expandedWorldId;
        foreach (var row in _rows.Where(row => row.IsExpanded)) ConfigureDatapacksView(row);
        RefreshAvailability();
    }

    private bool IsCurrentLoad(Guid instanceId, long generation, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && generation == _loadGeneration && _active
        && _instance?.Id == instanceId;

    private void CancelListLoad()
    {
        var cancellation = _loadCancellation;
        _loadCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        _loadGeneration++;
        _loading = false;
    }

    private void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not InstanceWorldRow row) return;
        var expanded = !row.IsExpanded;
        foreach (var other in _rows.Where(item => item.IsExpanded))
        {
            other.IsExpanded = false;
            other.ContentView?.SetActive(false);
        }

        _expandedWorldId = expanded ? row.WorldId : null;
        row.IsExpanded = expanded;
        if (expanded) ConfigureDatapacksView(row);
    }

    private void DatapacksView_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not InstanceContentView content || content.Tag is not InstanceWorldRow row) return;
        row.ContentView = content;
        if (row.IsExpanded) ConfigureDatapacksView(row);
    }

    private void DatapacksView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not InstanceContentView content || content.Tag is not InstanceWorldRow row) return;
        content.SetActive(false);
        if (ReferenceEquals(row.ContentView, content)) row.ContentView = null;
    }

    private void ConfigureDatapacksView(InstanceWorldRow row)
    {
        if (!_active || !row.IsExpanded || row.ContentView is not { } content
            || _services is not { } services || _instance is not { } instance) return;
        content.Configure(services, instance, InstanceContentTarget.WorldDatapacks(row.WorldId), row.Name);
        content.SetActive(true);
        if (_preview?.Page == PreviewPage.DatapackCatalog && !_previewCatalogOpened)
        {
            content.OpenCatalog();
            _previewCatalogOpened = true;
        }
    }

    private async void OpenSavesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is not { } services || _instance is not { } instance) return;
        try
        {
            var folder = await Task.Run(() =>
            {
                var root = services.Store.GetInstanceDirectory(instance.Id);
                var saves = InstanceStorage.ContainedPath("minecraft/saves", root, allowBaseReparsePoint: true);
                return Directory.Exists(saves) ? saves : null;
            });
            if (folder is null)
            {
                _feedback = HakoStrings.Get("WorldsSavesUnavailable");
                RefreshAvailability();
                return;
            }

            await InstanceContentView.OpenLocationAsync(folder);
        }
        catch (Exception exception)
        {
            _feedback = FormatWorldException(exception);
            RefreshAvailability();
        }
    }

    private async void DuplicateWorld_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not InstanceWorldRow row
            || _services is not { } services || _instance is not { } instance || BlockReason() is not null) return;
        _operationBusy = true;
        _feedback = null;
        RefreshAvailability();
        try
        {
            var answer = await HakoDialogs.ConfirmAsync(
                WorldResourceKeys.FormatMessage("WorldDuplicateTitle", HakoStrings.Get, row.Name, instance.Name),
                WorldResourceKeys.FormatMessage("WorldDuplicateMessage", HakoStrings.Get, row.Name, instance.Name),
                HakoStrings.Get("WorldDuplicateConfirm"), HakoStrings.Get("CancelButton"));
            if (answer != ContentDialogResult.Primary) return;
            if (CoreBlockReason() is { } blocked)
            {
                _feedback = blocked;
                return;
            }

            var result = await services.Worlds.DuplicateAsync(instance.Id, row.WorldId);
            if (!result.Succeeded)
            {
                _feedback = HakoStrings.Get(result.FailureReason is { } failure
                    ? WorldResourceKeys.Failure(failure) : "WorldActionFailed");
                return;
            }

            _feedback = WorldResourceKeys.FormatMessage("WorldDuplicateSucceeded", HakoStrings.Get,
                row.Name, result.WorldId!);
            LoadWorlds();
        }
        catch (Exception exception)
        {
            _feedback = FormatWorldException(exception);
        }
        finally
        {
            _operationBusy = false;
            RefreshAvailability();
        }
    }

    private async void TrashWorld_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not InstanceWorldRow row
            || _services is not { } services || _instance is not { } instance || BlockReason() is not null) return;
        _operationBusy = true;
        _feedback = null;
        RefreshAvailability();
        try
        {
            var answer = await HakoDialogs.ConfirmAsync(
                WorldResourceKeys.FormatMessage("WorldTrashTitle", HakoStrings.Get, row.Name),
                WorldResourceKeys.FormatMessage("WorldTrashMessage", HakoStrings.Get, row.Name, instance.Name),
                HakoStrings.Get("WorldTrashConfirm"), HakoStrings.Get("CancelButton"), destructive: true);
            if (answer != ContentDialogResult.Primary) return;
            if (CoreBlockReason() is { } blocked)
            {
                _feedback = blocked;
                return;
            }

            var result = await services.Worlds.TrashAsync(instance.Id, row.WorldId);
            if (!result.Succeeded)
            {
                _feedback = HakoStrings.Get(result.FailureReason is { } failure
                    ? WorldResourceKeys.Failure(failure) : "WorldActionFailed");
                return;
            }

            if (_expandedWorldId == row.WorldId) _expandedWorldId = null;
            _feedback = WorldResourceKeys.FormatMessage("WorldTrashSucceeded", HakoStrings.Get, row.Name);
            LoadWorlds();
        }
        catch (Exception exception)
        {
            _feedback = FormatWorldException(exception);
        }
        finally
        {
            _operationBusy = false;
            RefreshAvailability();
        }
    }

    private string? CoreBlockReason()
    {
        if (_instance is not { } instance || _services is not { } services) return null;
        if (instance.State is InstanceInstallationState.Queued or InstanceInstallationState.Installing)
            return HakoStrings.Get("ContentBlockedInstall");
        if (instance.State != InstanceInstallationState.Ready)
            return HakoStrings.Get("ContentBlockedBuild");
        if (services.Store.ManagementBlockedError(instance) is { } blocked)
            return HakoStrings.Get(blocked.ResourceKey());
        return services.Store.IsContentBusy(instance.Id) ? HakoStrings.Get("ContentBlockedFilesBusy") : null;
    }

    private string FormatWorldException(Exception exception)
    {
        var message = exception is InstanceWorldException world
            ? HakoStrings.Get(WorldResourceKeys.Failure(world.Error))
            : HakoStrings.Get("WorldActionFailed");
        return $"{message}\n{HakoStrings.Get("WorldDiagnosticLabel")} {exception.Message}";
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _feedback = null;
        LoadWorlds();
    }

}

public sealed class InstanceWorldRow : INotifyPropertyChanged
{
    private bool _isExpanded;
    private string? _blockReason;

    internal InstanceWorldRow(string worldId) => WorldId = worldId;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string WorldId { get; }
    public string Name { get; private set; } = "";
    public string VersionText { get; private set; } = "";
    public string ActivityText { get; private set; } = "";
    public string MetadataErrorText { get; private set; } = "";
    public Visibility MetadataErrorVisibility { get; private set; } = Visibility.Collapsed;
    public ImageSource? Icon { get; private set; }
    public Visibility IconVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PlaceholderVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
    public string PlaceholderGlyph => "\uE8B7";
    public string ExpandText { get; private set; } = "";
    public string DatapacksTitle { get; private set; } = "";
    public string ActionsText { get; private set; } = "";
    public string DuplicateText { get; private set; } = "";
    public string TrashText { get; private set; } = "";
    public bool CanManage { get; private set; }
    public string? BlockReason => _blockReason;
    public Visibility ExpandedVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    public InstanceContentView? ContentView { get; set; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExpandedVisibility));
            UpdateLocalizedText(AppLanguages.Default);
        }
    }

    internal void Update(InstanceWorld world, ImageSource? icon, AppLanguage language)
    {
        Icon = icon;
        Name = world.Name;
        var culture = CultureInfo.GetCultureInfo(language == AppLanguage.Russian ? "ru-RU" : "en-US");
        var version = world.Version ?? HakoStrings.Get("WorldVersionUnknown");
        VersionText = WorldResourceKeys.FormatMessage("WorldVersionFormat", HakoStrings.Get, version,
            HakoStrings.Get(WorldResourceKeys.GameType(world.GameType)));
        ActivityText = WorldResourceKeys.FormatMessage("WorldActivityFormat", HakoStrings.Get,
            WorldResourceKeys.FormatLastPlayed(world.LastPlayed, culture, HakoStrings.Get),
            WorldResourceKeys.FormatSize(world.Size, culture, HakoStrings.Get));
        MetadataErrorText = world.MetadataError is { } error
            ? WorldResourceKeys.FormatMessage("WorldMetadataUnavailable", HakoStrings.Get,
                HakoStrings.Get(WorldResourceKeys.MetadataError(error)))
            : "";
        MetadataErrorVisibility = world.MetadataError is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateLocalizedText(language);
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(IconVisibility));
        OnPropertyChanged(nameof(PlaceholderVisibility));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(ActivityText));
        OnPropertyChanged(nameof(MetadataErrorText));
        OnPropertyChanged(nameof(MetadataErrorVisibility));
    }

    internal void UpdateAvailability(bool canManage, string? reason)
    {
        CanManage = canManage;
        _blockReason = reason;
        OnPropertyChanged(nameof(CanManage));
        OnPropertyChanged(nameof(BlockReason));
    }

    internal void UpdateLocalizedText(AppLanguage language)
    {
        ExpandText = HakoStrings.Get(IsExpanded ? "WorldsHideDatapacks" : "WorldsShowDatapacks");
        DatapacksTitle = HakoStrings.Get("WorldsDatapacksTitle");
        ActionsText = HakoStrings.Get("WorldsActions");
        DuplicateText = HakoStrings.Get("WorldsDuplicate");
        TrashText = HakoStrings.Get("WorldsTrash");
        OnPropertyChanged(nameof(ExpandText));
        OnPropertyChanged(nameof(DatapacksTitle));
        OnPropertyChanged(nameof(ActionsText));
        OnPropertyChanged(nameof(DuplicateText));
        OnPropertyChanged(nameof(TrashText));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

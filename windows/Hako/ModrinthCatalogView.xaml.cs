using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using Hako.Core.Instances;
using Hako.Core.Minecraft;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Hako;

public sealed partial class ModrinthCatalogView : UserControl
{
    private readonly ObservableCollection<ModrinthCatalogRow> _rows = [];
    private LauncherServices? _services;
    private GameInstance? _instance;
    private ModrinthContentKind _kind;
    private CancellationTokenSource? _activeCancellation;
    private CancellationTokenSource? _searchCancellation;
    private DispatcherQueueTimer? _searchTimer;
    private InstanceContentState _contentState = new();
    private HashSet<string> _installed = new(StringComparer.Ordinal);
    private long _searchGeneration;
    private long _installedGeneration;
    private long _menuGeneration;
    private int _totalHits;
    private int _nextOffset;
    private int _failedOffset;
    private bool _hasMore;
    private bool _active;
    private bool _loadingSearch;
    private bool _loadingInstalled;
    private string _searchError = "";
    private string _actionFeedback = "";
    private string? _availabilityBlockReason;
    private MenuFlyout? _channelMenu;

    public ModrinthCatalogView()
    {
        InitializeComponent();
        ProjectsRepeater.ItemsSource = _rows;
        SortBox.SelectedIndex = 0;
        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(300);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => _ = SearchAsync(reset: true);
        Loaded += (_, _) => HakoStrings.LanguageChanged += HakoStrings_LanguageChanged;
        Unloaded += (_, _) => HakoStrings.LanguageChanged -= HakoStrings_LanguageChanged;
        RefreshStrings();
    }

    public event EventHandler? BackRequested;

    internal void Configure(LauncherServices services, GameInstance instance, ModrinthContentKind kind)
    {
        var previous = _instance;
        var changed = previous?.Id != instance.Id || _kind != kind;
        var contextChanged = previous is null || _kind != kind || previous.VersionID != instance.VersionID
            || previous.ModLoader != instance.ModLoader
            || previous.FabricConfiguration?.LoaderVersion != instance.FabricConfiguration?.LoaderVersion;
        var queryChanged = changed || contextChanged;
        var wasActive = _active;
        var contentLocationChanged = previous is not null
            && (previous.FolderName != instance.FolderName || previous.LegacyTexturepacks != instance.LegacyTexturepacks);
        var becameReady = previous is not null && previous.State != InstanceInstallationState.Ready
            && instance.State == InstanceInstallationState.Ready;
        if (queryChanged && _active) SetActive(false);
        _services = services;
        _instance = instance;
        _kind = kind;
        var blockReason = BlockReason();
        var availabilityChanged = previous is null || _availabilityBlockReason != blockReason || contentLocationChanged;
        _availabilityBlockReason = blockReason;
        if (availabilityChanged) CloseChannelMenu();
        if (queryChanged)
        {
            _installed.Clear();
            _rows.Clear();
            SearchBox.Text = "";
            _totalHits = 0;
            _nextOffset = 0;
            _hasMore = false;
            _searchError = "";
            _actionFeedback = "";
        }

        if (contextChanged) RefreshStrings();
        else if (availabilityChanged)
        {
            RefreshRows();
            RefreshState();
            if (_active && instance.State == InstanceInstallationState.Ready && (contentLocationChanged || becameReady))
                _ = RefreshInstalledProjectsAsync();
        }

        if (queryChanged && wasActive) SetActive(true);
    }

    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (active)
        {
            _activeCancellation = new CancellationTokenSource();
            _contentState = _services?.ContentController.GetState(_instance?.Id ?? Guid.Empty, _kind) ?? new InstanceContentState();
            RefreshRows();
            _ = RefreshInstalledProjectsAsync();
            _ = SearchAsync(reset: true);
        }
        else
        {
            CloseChannelMenu();
            _searchTimer?.Stop();
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = null;
            _activeCancellation?.Cancel();
            _activeCancellation?.Dispose();
            _activeCancellation = null;
            _loadingSearch = false;
            _loadingInstalled = false;
            RefreshState();
        }
    }

    public void RefreshStrings()
    {
        var mod = _kind == ModrinthContentKind.Mod;
        CatalogTitle.Text = HakoStrings.Get(mod ? "ContentCatalogTitleMods" : "ContentCatalogTitleResourcePacks");
        BackButton.Content = HakoStrings.Get("ContentCatalogBack");
        SearchBox.PlaceholderText = HakoStrings.Get("ContentCatalogSearchPlaceholder");
        SetSortLabel(0, "ContentCatalogSortRelevance");
        SetSortLabel(1, "ContentCatalogSortDownloads");
        SetSortLabel(2, "ContentCatalogSortNewest");
        SetSortLabel(3, "ContentCatalogSortUpdated");
        RetryButton.Content = HakoStrings.Get("ContentCatalogRetry");
        CatalogEmptyText.Text = HakoStrings.Get("ContentCatalogNoResults");
        LoadMoreButton.Content = HakoStrings.Get("ContentCatalogLoadMore");
        CatalogContextText.Text = _instance is { } instance
            ? string.Format(HakoStrings.Get("ContentCatalogContext"),
                instance.ModLoader == InstanceModLoader.Fabric ? $"Fabric {instance.FabricConfiguration?.LoaderVersion}" : HakoStrings.Get("CreationLoaderVanilla"),
                instance.VersionID)
            : "";
        RefreshRows();
        RefreshState();
    }

    public void ContentStateChanged(InstanceContentState state)
    {
        if (state.Revision < _contentState.Revision) return;
        var wasBusy = _contentState.IsBusy;
        _contentState = state;
        if (state.IsBusy || state.IsCheckingUpdates) CloseChannelMenu();
        RefreshRows();
        RefreshState();
        if (_active && wasBusy && !state.IsBusy) _ = RefreshInstalledProjectsAsync();
    }

    private void HakoStrings_LanguageChanged(object? sender, EventArgs e) => RefreshStrings();

    private void SetSortLabel(int index, string resourceKey)
    {
        if (index < SortBox.Items.Count && SortBox.Items[index] is ComboBoxItem item)
            item.Content = HakoStrings.Get(resourceKey);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_active) return;
        _searchTimer?.Stop();
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
        _searchGeneration++;
        _loadingSearch = false;
        _searchError = "";
        _actionFeedback = "";
        _failedOffset = 0;
        _hasMore = false;
        RefreshState();
        _searchTimer?.Start();
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_active) return;
        _searchTimer?.Stop();
        _ = SearchAsync(reset: true);
    }

    private async Task SearchAsync(bool reset, int? retryOffset = null)
    {
        if (!_active || _services is not { } services || _instance is not { } instance
            || _activeCancellation is not { } activeCancellation) return;
        _searchTimer?.Stop();
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(activeCancellation.Token);
        var cancellationToken = _searchCancellation.Token;
        var generation = ++_searchGeneration;
        var offset = retryOffset ?? (reset ? 0 : _nextOffset);
        _failedOffset = offset;
        _loadingSearch = true;
        _searchError = "";
        if (reset) _actionFeedback = "";
        RefreshState();
        try
        {
            var page = await services.Modrinth.SearchAsync(SearchBox.Text, _kind, instance.VersionID,
                SelectedSort(), offset, 20, cancellationToken);
            if (!IsCurrentSearch(generation, cancellationToken)) return;
            if (reset || offset == 0) _rows.Clear();
            foreach (var project in page.Hits)
            {
                if (_rows.All(row => !string.Equals(row.Project.Id, project.Id, StringComparison.Ordinal)))
                    _rows.Add(new ModrinthCatalogRow(project));
            }

            _totalHits = page.TotalHits;
            _nextOffset = offset + page.Hits.Count;
            _hasMore = _nextOffset < _totalHits;
            _failedOffset = -1;
            RefreshRows();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (IsCurrentSearch(generation, cancellationToken))
            {
                if (offset == 0)
                {
                    _rows.Clear();
                    _totalHits = 0;
                    _nextOffset = 0;
                    _hasMore = false;
                    RefreshRows();
                }
                _searchError = HakoStrings.Get("ContentFailureModrinthRequestFailed");
                _failedOffset = offset;
            }
        }
        finally
        {
            if (generation == _searchGeneration)
            {
                _loadingSearch = false;
                RefreshState();
            }
        }
    }

    private bool IsCurrentSearch(long generation, CancellationToken token) =>
        _active && generation == _searchGeneration && !token.IsCancellationRequested;

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        var offset = _failedOffset < 0 ? 0 : _failedOffset;
        await SearchAsync(reset: offset == 0, retryOffset: offset);
    }

    private async void LoadMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_hasMore && !_loadingSearch) await SearchAsync(reset: false);
    }

    private async Task RefreshInstalledProjectsAsync()
    {
        if (!_active || _services is not { } services || _instance is not { } instance
            || _activeCancellation is not { } activeCancellation || _contentState.IsBusy) return;
        var generation = ++_installedGeneration;
        _loadingInstalled = true;
        RefreshState();
        try
        {
            var installed = await services.ContentController.InstalledProjectsAsync(instance.Id, _kind, activeCancellation.Token);
            if (!_active || generation != _installedGeneration || activeCancellation.IsCancellationRequested) return;
            _installed = installed.Keys.ToHashSet(StringComparer.Ordinal);
            CatalogInstalledErrorText.Visibility = Visibility.Collapsed;
            RefreshRows();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (_active && generation == _installedGeneration)
                CatalogInstalledErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (generation == _installedGeneration)
            {
                _loadingInstalled = false;
                RefreshState();
            }
        }
    }

    private void RefreshState()
    {
        var searching = _loadingSearch;
        CatalogStatusPanel.Visibility = searching || _loadingInstalled ? Visibility.Visible : Visibility.Collapsed;
        CatalogProgress.IsActive = searching || _loadingInstalled;
        CatalogStatusText.Text = searching ? HakoStrings.Get("ContentCatalogLoading")
            : _loadingInstalled ? HakoStrings.Get("ContentLoading") : "";
        CatalogErrorText.Text = _searchError;
        CatalogErrorText.Visibility = _searchError.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CatalogActionFeedbackText.Text = _actionFeedback;
        CatalogActionFeedbackText.Visibility = _actionFeedback.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.Visibility = _searchError.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CatalogEmptyText.Visibility = !_loadingSearch && _searchError.Length == 0 && _rows.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreButton.Visibility = _hasMore && !_loadingSearch ? Visibility.Visible : Visibility.Collapsed;
        var blocked = BlockReason();
        CatalogBlockedText.Text = _contentState.IsBusy ? HakoStrings.Get("ContentCatalogBusy") : blocked ?? "";
        CatalogBlockedText.Visibility = _contentState.IsBusy || blocked is not null ? Visibility.Visible : Visibility.Collapsed;
        CatalogInstalledErrorText.Text = HakoStrings.Get("ContentCatalogInstalledLoadFailed");
    }

    private string? BlockReason()
    {
        if (_services is not { } services || _instance is not { } instance) return null;
        if (_kind == ModrinthContentKind.Mod && instance.ModLoader != InstanceModLoader.Fabric)
            return HakoStrings.Get("ContentBlockedFabric");
        if (instance.State is InstanceInstallationState.Queued or InstanceInstallationState.Installing)
            return HakoStrings.Get("ContentBlockedInstall");
        if (instance.State != InstanceInstallationState.Ready)
            return HakoStrings.Get("ContentBlockedBuild");
        if (services.Store.IsContentBusy(instance.Id))
            return HakoStrings.Get("ContentBlockedFilesBusy");
        if (services.Store.ManagementBlockedError(instance) is { } error)
            return HakoStrings.Get(error.ResourceKey());
        return null;
    }

    private ModrinthSort SelectedSort() => SortBox.SelectedItem is ComboBoxItem { Tag: string value }
        && Enum.TryParse<ModrinthSort>(value, out var sort) ? sort : ModrinthSort.Relevance;

    private void RefreshRows()
    {
        if (_rows.Count == 0) return;
        var canInstall = BlockReason() is null && !_contentState.IsBusy && !_contentState.IsCheckingUpdates;
        var projects = _rows.Select(row => row.Project).ToArray();
        for (var index = 0; index < projects.Length; index++)
        {
            var project = projects[index];
            _rows[index] = new ModrinthCatalogRow(project,
                installed: _installed.Contains(project.Id),
                isBusy: _contentState.IsBusy && string.Equals(_contentState.CurrentProjectId, project.Id, StringComparison.Ordinal),
                canInstall: canInstall, blockReason: BlockReason());
        }
    }

    private async void ProjectLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ModrinthProject project) return;
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(project.PageUrl))
                await HakoDialogs.ShowErrorAsync(HakoStrings.Get("ContentLinkFailed"));
        }
        catch (Exception) { await HakoDialogs.ShowErrorAsync(HakoStrings.Get("ContentLinkFailed")); }
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ModrinthProject project) return;
        await InstallProjectAsync(project, "release", _searchGeneration);
    }

    private async void ChannelMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_active || _services is not { } services || _instance is not { } instance
            || BlockReason() is not null || _contentState.IsBusy || _contentState.IsCheckingUpdates
            || (sender as FrameworkElement) is not Button button || button.Tag is not ModrinthProject project) return;
        var generation = _searchGeneration;
        var menuGeneration = ++_menuGeneration;
        var token = _activeCancellation?.Token ?? CancellationToken.None;
        var menu = new MenuFlyout();
        _channelMenu?.Hide();
        _channelMenu = menu;
        var beta = new MenuFlyoutItem { Text = HakoStrings.Get("ContentCatalogVersionsLoading"), IsEnabled = false };
        var alpha = new MenuFlyoutItem { Text = HakoStrings.Get("ContentCatalogVersionsLoading"), IsEnabled = false };
        menu.Items.Add(beta);
        menu.Items.Add(alpha);
        menu.ShowAt(button);
        try
        {
            var versions = await services.Modrinth.GetCompatibleVersionsAsync(project.Id, _kind, instance.VersionID, token);
            if (!_active || generation != _searchGeneration || menuGeneration != _menuGeneration || token.IsCancellationRequested) return;
            var betaVersion = versions.FirstOrDefault(version => version.VersionType == "beta");
            var alphaVersion = versions.FirstOrDefault(version => version.VersionType == "alpha");
            beta.Text = betaVersion is null ? HakoStrings.Get("ContentCatalogBetaUnavailable")
                : string.Format(HakoStrings.Get("ContentCatalogInstallBeta"), betaVersion.VersionNumber);
            alpha.Text = alphaVersion is null ? HakoStrings.Get("ContentCatalogAlphaUnavailable")
                : string.Format(HakoStrings.Get("ContentCatalogInstallAlpha"), alphaVersion.VersionNumber);
            var canInstall = BlockReason() is null && !_contentState.IsBusy
                && !_contentState.IsCheckingUpdates && !_installed.Contains(project.Id);
            beta.IsEnabled = canInstall && betaVersion is not null;
            alpha.IsEnabled = canInstall && alphaVersion is not null;
            beta.Click += async (_, _) => await InstallProjectAsync(project, "beta", generation);
            alpha.Click += async (_, _) => await InstallProjectAsync(project, "alpha", generation);
            var row = _rows.FirstOrDefault(item => item.Project.Id == project.Id);
            if (row is not null && versions.All(version => version.VersionType != "release"))
            {
                var index = _rows.IndexOf(row);
                _rows[index] = new ModrinthCatalogRow(project, row.Installed, row.IsBusy, row.AddEnabled,
                    HakoStrings.Get("ContentCatalogNoRelease"));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (_active && generation == _searchGeneration)
            {
                _actionFeedback = HakoStrings.Get("ContentFailureModrinthRequestFailed");
                RefreshState();
            }
        }
    }

    private void CloseChannelMenu()
    {
        _menuGeneration++;
        _channelMenu?.Hide();
        _channelMenu = null;
    }

    private async Task InstallProjectAsync(ModrinthProject project, string channel, long generation)
    {
        if (!_active || generation != _searchGeneration || _services is not { } services || _instance is not { } instance
            || BlockReason() is not null || _contentState.IsBusy || _contentState.IsCheckingUpdates
            || _installed.Contains(project.Id))
            return;
        try
        {
            var result = await services.ContentController.InstallAsync(instance.Id, _kind, project, channel);
            if (!_active || generation != _searchGeneration) return;
            if (result.Succeeded)
            {
                _actionFeedback = "";
                _installed.Add(project.Id);
                CatalogErrorText.Visibility = Visibility.Collapsed;
                CatalogErrorText.Text = "";
                RefreshRows();
                await RefreshInstalledProjectsAsync();
            }
            else if (result.Failure is { } failure)
            {
                _actionFeedback = InstanceContentView.FormatFailureDetails(failure);
                RefreshState();
            }
            else
            {
                _actionFeedback = HakoStrings.Get("ContentFailureCancelled");
                RefreshState();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (_active && generation == _searchGeneration)
            {
                _actionFeedback = HakoStrings.Get("ContentFailureOperationFailed");
                RefreshState();
            }
        }
        finally
        {
            if (_active && generation == _searchGeneration)
            {
                _contentState = services.ContentController.GetState(instance.Id, _kind);
                RefreshRows();
                RefreshState();
            }
        }
    }
}

public sealed class ModrinthCatalogRow
{
    public ModrinthCatalogRow(ModrinthProject project, bool installed = false, bool isBusy = false,
        bool canInstall = true, string? releaseHint = null, string? blockReason = null)
    {
        Project = project;
        Title = project.Title;
        Description = project.Description;
        Icon = project.IconUrl is { IsAbsoluteUri: true, Scheme: "https" } icon ? new BitmapImage(icon) : null;
        IconVisibility = Icon is null ? Visibility.Collapsed : Visibility.Visible;
        PlaceholderVisibility = Icon is null ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderGlyph = project.ProjectType == "mod" ? "\uE8A5" : "\uE8B7";
        Installed = installed;
        IsBusy = isBusy;
        AddText = installed ? HakoStrings.Get("ContentCatalogInstalled")
            : isBusy ? HakoStrings.Get("ContentCatalogAdding") : HakoStrings.Get("ContentCatalogAdd");
        AddEnabled = !installed && !isBusy && canInstall;
        AddHint = blockReason ?? (canInstall ? "" : HakoStrings.Get("ContentCatalogBusy"));
        MenuEnabled = !installed && !isBusy && canInstall;
        ChannelMenuHint = blockReason ?? HakoStrings.Get("ContentCatalogVersionsLoading");
        ReleaseHint = releaseHint ?? "";
        ReleaseHintVisibility = ReleaseHint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public ModrinthProject Project { get; }
    public string Title { get; }
    public string Description { get; }
    public ImageSource? Icon { get; }
    public Visibility IconVisibility { get; }
    public Visibility PlaceholderVisibility { get; }
    public string PlaceholderGlyph { get; }
    public bool Installed { get; }
    public bool IsBusy { get; }
    public string AddText { get; }
    public bool AddEnabled { get; }
    public string AddHint { get; }
    public bool MenuEnabled { get; }
    public string ChannelMenuHint { get; }
    public string ReleaseHint { get; }
    public Visibility ReleaseHintVisibility { get; }
}

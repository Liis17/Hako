using System.Collections.Generic;
using System.Linq;
using Hako.Core.Instances;
using Hako.Core.Minecraft;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Hako;

public sealed partial class ModpackCatalogPage : Page
{
    private LauncherServices? _services;
    private CancellationTokenSource? _catalogCancellation;
    private CancellationTokenSource? _versionsCancellation;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _searchTimer;
    private IReadOnlyList<ModrinthVersion> _versions = [];
    private ModrinthProject? _selectedProject;
    private long _searchGeneration;
    private long _versionsGeneration;
    private long _projectIconGeneration;
    private bool _isLoaded;
    private bool _updatingFilters;
    private bool _previewCreateStarted;

    public ModpackCatalogPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = (LauncherServices)e.Parameter;
        _isLoaded = true;
        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(300);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => _ = SearchAsync();
        HakoStrings.LanguageChanged += HakoStrings_LanguageChanged;
        RefreshStrings();
        _ = LoadFiltersAndSearchAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _isLoaded = false;
        _searchTimer?.Stop();
        _catalogCancellation?.Cancel();
        _versionsCancellation?.Cancel();
        HakoStrings.LanguageChanged -= HakoStrings_LanguageChanged;
        base.OnNavigatedFrom(e);
    }

    private void HakoStrings_LanguageChanged(object? sender, EventArgs e) => RefreshStrings();

    private void RefreshStrings()
    {
        TitleText.Text = HakoStrings.Get("ModpackCatalogTitle");
        SubtitleText.Text = HakoStrings.Get("ModpackCatalogSubtitle");
        SearchBox.PlaceholderText = HakoStrings.Get("ModpackSearchPlaceholder");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, HakoStrings.Get("ModpackSearchPlaceholder"));
        VersionLabel.Text = HakoStrings.Get("ModpackVersionLabel");
        RetryVersionsButton.Content = HakoStrings.Get("ModpackRetry");
        LoadingText.Text = HakoStrings.Get("ModpackLoading");
        RetryButton.Content = HakoStrings.Get("ModpackRetry");
        MoreButton.Content = HakoStrings.Get("ModpackLoadMore");
        ChooseButton.Content = HakoStrings.Get("ModpackChoose");
        ProjectLink.Content = HakoStrings.Get("ModpackOpenProject");
        if (_updatingFilters) return;
        var selectedSort = (SortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "relevance";
        _updatingFilters = true;
        SortBox.Items.Clear();
        SortBox.Items.Add(SortItem("relevance", "ModpackSortRelevance"));
        SortBox.Items.Add(SortItem("downloads", "ModpackSortDownloads"));
        SortBox.Items.Add(SortItem("newest", "ModpackSortNewest"));
        SortBox.Items.Add(SortItem("updated", "ModpackSortUpdated"));
        SortBox.SelectedItem = SortBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, selectedSort))
            ?? SortBox.Items[0];
        _updatingFilters = false;
        if (_isLoaded) _ = SearchAsync();
    }

    private ComboBoxItem SortItem(string tag, string key) => new() { Tag = tag, Content = HakoStrings.Get(key) };

    private async Task LoadFiltersAndSearchAsync()
    {
        if (_services is not { } services) return;
        using var cancellation = new CancellationTokenSource();
        try
        {
            var catalog = await services.Mojang.CatalogAsync(cancellation.Token);
            if (!_isLoaded) return;
            _updatingFilters = true;
            var selected = (MinecraftBox.SelectedItem as ComboBoxItem)?.Tag as string;
            MinecraftBox.Items.Clear();
            MinecraftBox.Items.Add(new ComboBoxItem { Tag = "", Content = HakoStrings.Get("ModpackAllMinecraftVersions") });
            foreach (var version in catalog.Versions)
                MinecraftBox.Items.Add(new ComboBoxItem { Tag = version.Id, Content = version.Id });
            MinecraftBox.SelectedItem = MinecraftBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, selected))
                ?? MinecraftBox.Items[0];
            _updatingFilters = false;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            _updatingFilters = true;
            MinecraftBox.Items.Clear();
            MinecraftBox.Items.Add(new ComboBoxItem { Tag = "", Content = HakoStrings.Get("ModpackAllMinecraftVersions") });
            MinecraftBox.SelectedIndex = 0;
            _updatingFilters = false;
        }

        await SearchAsync();
    }

    private async Task SearchAsync()
    {
        if (!_isLoaded || _services is not { } services) return;
        _catalogCancellation?.Cancel();
        var cancellation = _catalogCancellation = new CancellationTokenSource();
        var generation = ++_searchGeneration;
        SetLoading(true);
        try
        {
            await services.ModpackCatalog.SearchAsync(SearchBox.Text, SelectedMinecraft(), SelectedSort(), cancellation.Token);
            if (!IsCurrent(cancellation, generation)) return;
            _selectedProject = null;
            _versions = [];
            UpdateSelection();
            UpdateProjects();
            ErrorPanel.Visibility = services.ModpackCatalog.Error is null ? Visibility.Collapsed : Visibility.Visible;
            ErrorText.Text = services.ModpackCatalog.Error is null ? "" : InstanceText.Error(services.ModpackCatalog.Error);
            MoreButton.Visibility = services.ModpackCatalog.HasMore ? Visibility.Visible : Visibility.Collapsed;
            if (_services.PreviewOptions?.Page == PreviewPage.ModpackCreate && !_previewCreateStarted
                && services.ModpackCatalog.Projects.FirstOrDefault() is { } project)
            {
                _previewCreateStarted = true;
                await SelectProjectAsync(project, cancellation.Token);
                if (VersionBox.SelectedItem is ModpackVersionChoice choice)
                    OpenCreation(project, choice.Version);
            }
        }
        finally
        {
            if (IsCurrent(cancellation, generation)) SetLoading(false);
        }
    }

    private bool IsCurrent(CancellationTokenSource cancellation, long generation) =>
        _isLoaded && !cancellation.IsCancellationRequested && generation == _searchGeneration;

    private string? SelectedMinecraft() => (MinecraftBox.SelectedItem as ComboBoxItem)?.Tag as string is { Length: > 0 } id ? id : null;

    private ModrinthSort SelectedSort() => ((SortBox.SelectedItem as ComboBoxItem)?.Tag as string) switch
    {
        "downloads" => ModrinthSort.Downloads,
        "newest" => ModrinthSort.Newest,
        "updated" => ModrinthSort.Updated,
        _ => ModrinthSort.Relevance
    };

    private void SetLoading(bool loading)
    {
        LoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        if (loading)
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
            MoreButton.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateProjects()
    {
        ProjectList.Children.Clear();
        if (_services is not { } services) return;
        foreach (var project in services.ModpackCatalog.Projects)
        {
            var icon = new Image { Width = 42, Height = 42, Stretch = Stretch.UniformToFill };
            var iconBorder = new Border
            {
                Width = 42,
                Height = 42,
                CornerRadius = new CornerRadius(6),
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                Child = icon
            };
            var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = project.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = project.Description, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextTrimming = TextTrimming.CharacterEllipsis });
            var row = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() }, ColumnSpacing = 12, Children = { iconBorder } };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var button = new Button { Tag = project, Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10) };
            button.Click += ProjectButton_Click;
            ProjectList.Children.Add(button);
            _ = LoadProjectIconAsync(project, icon);
        }
    }

    private async Task LoadProjectIconAsync(ModrinthProject project, Image image)
    {
        if (_services is not { } services) return;
        var generation = _searchGeneration;
        try
        {
            var data = await services.Modpacks.DownloadIconAsync(project);
            if (!_isLoaded || generation != _searchGeneration || data is null) return;
            image.Source = await InstanceIconSource.CreateAsync(data);
        }
        catch (Exception)
        {
            // Project artwork is optional; the selectable project row remains usable.
        }
    }

    private async void ProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ModrinthProject project }) await SelectProjectAsync(project, CancellationToken.None);
    }

    private async Task SelectProjectAsync(ModrinthProject project, CancellationToken cancellationToken)
    {
        if (_services is not { } services) return;
        _versionsCancellation?.Cancel();
        var ownCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _versionsCancellation = ownCancellation;
        var generation = ++_versionsGeneration;
        _selectedProject = project;
        _versions = [];
        UpdateSelection();
        _ = LoadSelectedProjectIconAsync(project);
        VersionBox.ItemsSource = null;
        RetryVersionsButton.Visibility = Visibility.Collapsed;
        UnsupportedText.Visibility = Visibility.Collapsed;
        VersionDetails.Text = HakoStrings.Get("ModpackLoadingVersions");
        try
        {
            _versions = await services.ModpackCatalog.VersionsAsync(project, SelectedMinecraft(), ownCancellation.Token);
            if (!_isLoaded || ownCancellation.IsCancellationRequested || generation != _versionsGeneration) return;
            VersionBox.ItemsSource = _versions.Select(version => new ModpackVersionChoice(version, VersionDisplay(version))).ToList();
            VersionBox.SelectedIndex = _versions.Count == 0 ? -1 : 0;
            if (_versions.Count == 0)
            {
                VersionDetails.Text = "";
                UnsupportedText.Text = HakoStrings.Get("ModpackUnsupportedVersion");
                UnsupportedText.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateVersionDetails();
                if (_services.PreviewOptions?.Page == PreviewPage.ModpackCreate && !_previewCreateStarted)
                {
                    _previewCreateStarted = true;
                    OpenCreation(project, _versions[0]);
                }
            }
        }
        catch (OperationCanceledException) when (ownCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_isLoaded && generation == _versionsGeneration)
            {
                VersionDetails.Text = InstanceText.Error(exception);
                UnsupportedText.Text = HakoStrings.Get("ModpackUnsupportedVersion");
                UnsupportedText.Visibility = Visibility.Visible;
                RetryVersionsButton.Visibility = Visibility.Visible;
            }
        }
    }

    private string VersionDisplay(ModrinthVersion version) => string.Format(HakoStrings.Get("ModpackVersionDisplay"),
        version.VersionNumber, HakoStrings.Get(ChannelKey(version.VersionType)), version.DatePublished.ToLocalTime().ToString("d"));

    private static string ChannelKey(string channel) => channel switch
    {
        "release" => "ModpackChannelRelease",
        "beta" => "ModpackChannelBeta",
        "alpha" => "ModpackChannelAlpha",
        _ => "ModpackChannelUnknown"
    };

    private void UpdateSelection()
    {
        var project = _selectedProject;
        SelectedTitle.Text = project?.Title ?? "";
        SelectedDescription.Text = project?.Description ?? "";
        ProjectLink.NavigateUri = project?.PageUrl;
        ProjectLink.Visibility = project is null ? Visibility.Collapsed : Visibility.Visible;
        ProjectIcon.Source = null;
        ChooseButton.IsEnabled = project is not null && VersionBox.SelectedItem is ModpackVersionChoice;
        UnsupportedText.Visibility = Visibility.Collapsed;
    }

    private async Task LoadSelectedProjectIconAsync(ModrinthProject project)
    {
        if (_services is not { } services) return;
        var generation = ++_projectIconGeneration;
        try
        {
            var data = await services.Modpacks.DownloadIconAsync(project);
            if (!_isLoaded || generation != _projectIconGeneration || _selectedProject?.Id != project.Id || data is null) return;
            ProjectIcon.Source = await InstanceIconSource.CreateAsync(data);
        }
        catch (Exception)
        {
            // Project artwork is optional; version selection remains usable.
        }
    }

    private void UpdateVersionDetails()
    {
        if (VersionBox.SelectedItem is not ModpackVersionChoice choice) return;
        var versions = string.Join(", ", choice.Version.GameVersions);
        var loaders = string.Join(", ", choice.Version.Loaders);
        VersionDetails.Text = string.Format(HakoStrings.Get("ModpackVersionDetails"), versions, loaders);
        ChooseButton.IsEnabled = _selectedProject is not null;
    }

    private void OpenCreation(ModrinthProject project, ModrinthVersion version) =>
        MainWindow.OpenModpackCreation(App.CurrentWindow, project, version, SelectedMinecraft());

    private async void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is not { } services || services.ModpackCatalog.IsLoadingMore) return;
        var cancellation = _catalogCancellation;
        if (cancellation is null) return;
        SetLoading(true);
        await services.ModpackCatalog.LoadMoreAsync(cancellation.Token);
        if (!_isLoaded || cancellation.IsCancellationRequested) return;
        UpdateProjects();
        ErrorPanel.Visibility = services.ModpackCatalog.Error is null ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = services.ModpackCatalog.Error is null ? "" : InstanceText.Error(services.ModpackCatalog.Error);
        MoreButton.Visibility = services.ModpackCatalog.HasMore ? Visibility.Visible : Visibility.Collapsed;
        SetLoading(false);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoaded) { _searchTimer?.Stop(); _searchTimer?.Start(); }
    }

    private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoaded && !_updatingFilters) _ = SearchAsync();
    }

    private void VersionBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateVersionDetails();

    private void ChooseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is { } project && VersionBox.SelectedItem is ModpackVersionChoice choice)
            OpenCreation(project, choice.Version);
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

    private void RetryVersionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is { } project) _ = SelectProjectAsync(project, CancellationToken.None);
    }

    private sealed record ModpackVersionChoice(ModrinthVersion Version, string Display);
}

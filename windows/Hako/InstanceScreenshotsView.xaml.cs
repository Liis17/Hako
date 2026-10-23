using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Hako.Core;
using Hako.Core.Instances;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Hako;

public sealed partial class InstanceScreenshotsView : UserControl
{
    private readonly ObservableCollection<ScreenshotRow> _rows = [];
    private readonly ScreenshotThumbnails _thumbnails = new();
    private readonly Dictionary<ScreenshotRow, CancellationTokenSource> _thumbnailLoads = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private LauncherServices? _services;
    private GameInstance? _instance;
    private CoalescedRefresh? _refresh;
    private CancellationTokenSource? _listCancellation;
    private long _listGeneration;
    private bool _active;
    private bool _loading;
    private bool _operationBusy;
    private string? _loadError;
    private string? _feedback;

    public InstanceScreenshotsView()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue;
        Loaded += (_, _) => HakoStrings.LanguageChanged += HakoStrings_LanguageChanged;
        Unloaded += (_, _) => HakoStrings.LanguageChanged -= HakoStrings_LanguageChanged;
        RefreshStrings();
    }

    internal void Configure(LauncherServices services, GameInstance instance, ProfilePreviewOptions? preview)
    {
        _ = preview;
        var changed = _instance?.Id != instance.Id;
        _services = services;
        _instance = instance;
        if (changed)
        {
            CancelListLoad();
            CancelThumbnailLoads();
            _rows.Clear();
            _loadError = null;
            _feedback = null;
        }

        RefreshStrings();
        if (changed && _active) LoadScreenshots();
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
                services.Store.BusyStateChanged += CoreStateChanged;
                services.Store.InstancesChanged += Store_InstancesChanged;
                services.Games.GameSessionEnded += Games_GameSessionEnded;
            }
            LoadScreenshots();
        }
        else
        {
            if (_services is { } services)
            {
                services.Store.BusyStateChanged -= CoreStateChanged;
                services.Store.InstancesChanged -= Store_InstancesChanged;
                services.Games.GameSessionEnded -= Games_GameSessionEnded;
            }
            _refresh?.Stop();
            _refresh = null;
            CancelListLoad();
            CancelThumbnailLoads();
            foreach (var row in _rows) row.Thumbnail = null;
        }
        RefreshAvailability();
    }

    public void RefreshScreenshotsIfActive()
    {
        if (_active) LoadScreenshots();
    }

    internal void RefreshStrings()
    {
        ScreenshotsTitle.Text = HakoStrings.Get("ScreenshotsTitle");
        RefreshButton.Content = HakoStrings.Get("ScreenshotsRefresh");
        OpenFolderButton.Content = HakoStrings.Get("ScreenshotsOpenFolder");
        DeleteButton.Content = HakoStrings.Get("ScreenshotsDeleteSelected");
        LoadingText.Text = HakoStrings.Get("ScreenshotsLoading");
        OperationText.Text = HakoStrings.Get("ScreenshotsOperationInProgress");
        foreach (var row in _rows) row.UpdateLocalizedText(_services?.App.Language ?? AppLanguages.Default);
        RefreshSelectionText();
        RefreshAvailability();
    }

    private void HakoStrings_LanguageChanged(object? sender, EventArgs e) => RefreshStrings();
    private void CoreStateChanged(object? sender, EventArgs e) => _refresh?.Request();

    private void Store_InstancesChanged(object? sender, EventArgs e)
    {
        QueueScreenshotsRefresh();
    }

    private void Games_GameSessionEnded(object? sender, GameSessionEventArgs e)
    {
        QueueScreenshotsRefresh(e.InstanceId);
    }

    private void QueueScreenshotsRefresh(Guid? instanceID = null)
    {
        var generation = Interlocked.Read(ref _listGeneration);
        _dispatcher.TryEnqueue(() =>
        {
            if (!_active || generation != _listGeneration
                || (instanceID is { } id && _instance?.Id != id)) return;
            LoadScreenshots();
        });
    }

    private void RefreshAvailability()
    {
        var reason = BlockReason();
        ScreenshotsBlockReason.Text = reason ?? "";
        ScreenshotsBlockReason.Visibility = reason is null ? Visibility.Collapsed : Visibility.Visible;
        LoadingPanel.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
        OperationPanel.Visibility = _operationBusy ? Visibility.Visible : Visibility.Collapsed;
        if (LoadingPanel.Children.FirstOrDefault() is ProgressRing ring) ring.IsActive = _loading;
        ErrorText.Text = _loadError ?? "";
        ErrorText.Visibility = _loadError is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = HakoStrings.Get("ScreenshotsEmpty");
        EmptyText.Visibility = !_loading && _loadError is null && _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FeedbackText.Text = _feedback ?? "";
        FeedbackText.Visibility = _feedback is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.IsEnabled = _active && !_loading && !_operationBusy;
        OpenFolderButton.IsEnabled = !_operationBusy && _instance?.State == InstanceInstallationState.Ready;
        DeleteButton.IsEnabled = !_operationBusy && _instance?.State == InstanceInstallationState.Ready
            && !IsContentBusy() && ScreenshotItems.SelectedItems.Count > 0;
        ScreenshotItems.IsEnabled = !_operationBusy;
        ScreenshotsCount.Text = string.Format(HakoStrings.Get("ScreenshotsCountFormat"), _rows.Count);
        RefreshSelectionText();
    }

    private string? BlockReason()
    {
        if (_instance is not { } instance || _services is not { } services) return null;
        if (_operationBusy) return HakoStrings.Get("ScreenshotsOperationBusy");
        if (instance.State is InstanceInstallationState.Queued or InstanceInstallationState.Installing)
            return HakoStrings.Get("ContentBlockedInstall");
        if (instance.State != InstanceInstallationState.Ready)
            return HakoStrings.Get("ContentBlockedBuild");
        if (IsContentBusy()) return HakoStrings.Get("ContentBlockedFilesBusy");
        return null;
    }

    private bool IsContentBusy() => _instance is { } instance && _services is { } services
        && services.Store.IsContentBusy(instance.Id);

    private void LoadScreenshots()
    {
        if (!_active || _services is not { } services || _instance is not { } instance) return;
        CancelListLoad();
        _listCancellation = new CancellationTokenSource();
        var token = _listCancellation.Token;
        var generation = ++_listGeneration;
        _loading = true;
        _loadError = null;
        RefreshAvailability();
        _ = LoadScreenshotsAsync(services, instance, generation, token);
    }

    private async Task LoadScreenshotsAsync(LauncherServices services, GameInstance instance, long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var screenshots = await services.Screenshots.ListAsync(instance.Id, cancellationToken);
            if (!IsCurrentLoad(instance.Id, generation, cancellationToken)) return;
            var existing = _rows.ToDictionary(row => row.Path, StringComparer.OrdinalIgnoreCase);
            var next = new List<ScreenshotRow>(screenshots.Count);
            foreach (var screenshot in screenshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentLoad(instance.Id, generation, cancellationToken)) return;
                if (!existing.TryGetValue(screenshot.Path, out var row)) row = new ScreenshotRow(screenshot);
                else
                {
                    CancelThumbnailLoad(row);
                    row.Update(screenshot);
                }
                row.UpdateLocalizedText(services.App.Language);
                next.Add(row);
            }

            foreach (var removed in _rows.Where(row => next.All(item => !ReferenceEquals(item, row))).ToArray())
            {
                CancelThumbnailLoad(removed);
                _rows.Remove(removed);
            }
            for (var index = 0; index < next.Count; index++)
            {
                var row = next[index];
                if (index < _rows.Count && ReferenceEquals(_rows[index], row)) continue;
                var oldIndex = _rows.IndexOf(row);
                if (oldIndex >= 0) _rows.Move(oldIndex, index);
                else _rows.Insert(index, row);
            }
            while (_rows.Count > next.Count) _rows.RemoveAt(_rows.Count - 1);
            _feedback = null;
            foreach (var row in _rows) if (row.IsVisible) StartThumbnailLoad(row);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (IsCurrentLoad(instance.Id, generation, cancellationToken))
                _loadError = FormatException(exception);
        }
        finally
        {
            if (generation == _listGeneration)
            {
                _loading = false;
                RefreshAvailability();
            }
        }
    }

    private bool IsCurrentLoad(Guid id, long generation, CancellationToken token) =>
        !token.IsCancellationRequested && generation == _listGeneration && _active && _instance?.Id == id;

    private void CancelListLoad()
    {
        var cancellation = _listCancellation;
        _listCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        _listGeneration++;
        _loading = false;
    }

    private void StartThumbnailLoad(ScreenshotRow row)
    {
        if (!_active || !_rows.Contains(row) || row.Thumbnail is not null || _thumbnailLoads.ContainsKey(row)) return;
        var cancellation = new CancellationTokenSource();
        _thumbnailLoads.Add(row, cancellation);
        _ = LoadThumbnailAsync(row, cancellation);
    }

    private async Task LoadThumbnailAsync(ScreenshotRow row, CancellationTokenSource cancellation)
    {
        try
        {
            var thumbnail = await _thumbnails.LoadAsync(row.Path, cancellation.Token);
            if (cancellation.IsCancellationRequested || !_active
                || !_thumbnailLoads.TryGetValue(row, out var current) || !ReferenceEquals(current, cancellation)
                || !_rows.Contains(row)) return;
            row.Thumbnail = thumbnail;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (_rows.Contains(row)) row.Thumbnail = null; }
        finally
        {
            if (_thumbnailLoads.TryGetValue(row, out var current) && ReferenceEquals(current, cancellation))
                _thumbnailLoads.Remove(row);
            cancellation.Dispose();
        }
    }

    private void CancelThumbnailLoad(ScreenshotRow row)
    {
        if (!_thumbnailLoads.Remove(row, out var cancellation)) return;
        cancellation.Cancel();
    }

    private void CancelThumbnailLoads()
    {
        foreach (var cancellation in _thumbnailLoads.Values) cancellation.Cancel();
        _thumbnailLoads.Clear();
    }

    private void ScreenshotTile_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ScreenshotRow row }) return;
        row.IsVisible = true;
        StartThumbnailLoad(row);
    }

    private void ScreenshotTile_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ScreenshotRow row }) return;
        row.IsVisible = false;
        CancelThumbnailLoad(row);
    }

    private async void ScreenshotTile_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ScreenshotRow row) await OpenScreenshotAsync(row);
    }

    private async void OpenScreenshot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ScreenshotRow row) await OpenScreenshotAsync(row);
    }

    private async Task OpenScreenshotAsync(ScreenshotRow row)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(row.Path);
            if (!await Windows.System.Launcher.LaunchFileAsync(file))
                await HakoDialogs.ShowErrorAsync(HakoStrings.Get("ScreenshotsOpenFailed"));
        }
        catch (Exception) { await HakoDialogs.ShowErrorAsync(HakoStrings.Get("ScreenshotsOpenFailed")); }
    }

    private async void OpenScreenshotFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ScreenshotRow row) return;
        await InstanceContentView.OpenLocationAsync(Path.GetDirectoryName(row.Path));
    }

    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is not { } services || _instance is not { } instance) return;
        try
        {
            var folder = await services.Screenshots.GetOrCreateFolderAsync(instance.Id);
            await InstanceContentView.OpenLocationAsync(folder);
        }
        catch (Exception exception) { await HakoDialogs.ShowErrorAsync(FormatException(exception)); }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _feedback = null;
        LoadScreenshots();
    }

    private void ScreenshotItems_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshAvailability();

    private void RefreshSelectionText() => ScreenshotsSelectionCount.Text = ScreenshotItems.SelectedItems.Count == 0
        ? "" : string.Format(HakoStrings.Get("ScreenshotsSelectionFormat"), ScreenshotItems.SelectedItems.Count);

    private async void DeleteButton_Click(object sender, RoutedEventArgs e) => await DeleteSelectedAsync();

    private async void DeleteScreenshot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ScreenshotRow row) return;
        if (!ScreenshotItems.SelectedItems.Contains(row)) ScreenshotItems.SelectedItems.Clear();
        if (!ScreenshotItems.SelectedItems.Contains(row)) ScreenshotItems.SelectedItems.Add(row);
        await DeleteSelectedAsync();
    }

    private async Task DeleteSelectedAsync()
    {
        var selected = ScreenshotItems.SelectedItems.OfType<ScreenshotRow>().ToArray();
        if (selected.Length == 0 || _services is not { } services || _instance is not { } instance || _operationBusy) return;
        _operationBusy = true;
        _feedback = null;
        RefreshAvailability();
        try
        {
            var answer = await HakoDialogs.ConfirmAsync(
                HakoStrings.Get("ScreenshotsDeleteTitle"),
                string.Format(HakoStrings.Get("ScreenshotsDeleteMessage"), selected.Length),
                HakoStrings.Get("ScreenshotsDeleteConfirm"), HakoStrings.Get("CancelButton"), destructive: true);
            if (answer != ContentDialogResult.Primary) return;

            var result = await services.Screenshots.TrashAsync(instance.Id, selected.Select(row => row.Screenshot));
            var recycled = result.Results.Items.Count(item => item.Status == RecycleBinItemStatus.Recycled);
            var failures = result.Results.Items.Count - recycled;
            _feedback = failures == 0
                ? string.Format(HakoStrings.Get("ScreenshotsDeletedFormat"), recycled)
                : string.Format(HakoStrings.Get("ScreenshotsDeletePartialFormat"), recycled, failures);
            ScreenshotItems.SelectedItems.Clear();
            LoadScreenshots();
        }
        catch (Exception exception) { _feedback = FormatException(exception); }
        finally
        {
            _operationBusy = false;
            RefreshAvailability();
        }
    }

    private async void ScreenshotTile_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not ScreenshotRow row) return;
        var rows = ScreenshotItems.SelectedItems.Contains(row)
            ? ScreenshotItems.SelectedItems.OfType<ScreenshotRow>().ToArray() : [row];
        var deferral = args.GetDeferral();
        try
        {
            var files = new List<IStorageItem>(rows.Length);
            foreach (var selected in rows)
                files.Add(await StorageFile.GetFileFromPathAsync(selected.Path));
            args.Data.SetStorageItems(files, readOnly: true);
            args.Data.RequestedOperation = DataPackageOperation.Copy;
            args.AllowedOperations = DataPackageOperation.Copy;
        }
        catch (Exception) { args.Cancel = true; }
        finally { deferral.Complete(); }
    }

    private void ScreenshotItems_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete && ScreenshotItems.SelectedItems.Count > 0)
        {
            e.Handled = true;
            _ = DeleteSelectedAsync();
        }
    }

    private string FormatException(Exception exception)
    {
        if (exception is InstanceStorageException storage)
            return $"{HakoStrings.Get(storage.Error.ResourceKey())}\n{string.Format(HakoStrings.Get("BackupDiagnosticFormat"), storage.Message)}";
        return HakoStrings.Get("ScreenshotsActionFailed") + "\n" + exception.Message;
    }

}

public sealed class ScreenshotRow : INotifyPropertyChanged
{
    private InstanceScreenshot _screenshot;
    private BitmapImage? _thumbnail;
    private CultureInfo _culture = CultureInfo.CurrentCulture;

    internal ScreenshotRow(InstanceScreenshot screenshot) => _screenshot = screenshot;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal InstanceScreenshot Screenshot => _screenshot;
    internal bool IsVisible { get; set; }
    internal string Path => _screenshot.Path;
    public string Name => _screenshot.Name;
    public string CreatedText => _screenshot.CreatedAt?.ToLocalTime().ToString("g", _culture) ?? "";
    public string ActionsText { get; private set; } = "";
    public string OpenText { get; private set; } = "";
    public string OpenFolderText { get; private set; } = "";
    public string DeleteText { get; private set; } = "";
    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThumbnailVisibility));
            OnPropertyChanged(nameof(PlaceholderVisibility));
        }
    }
    public Visibility ThumbnailVisibility => Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PlaceholderVisibility => Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    internal void Update(InstanceScreenshot screenshot)
    {
        Thumbnail = null;
        _screenshot = screenshot;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(CreatedText));
    }

    internal void UpdateLocalizedText(AppLanguage language)
    {
        _culture = CultureInfo.GetCultureInfo(language == AppLanguage.Russian ? "ru-RU" : "en-US");
        OnPropertyChanged(nameof(CreatedText));
        ActionsText = HakoStrings.Get("ScreenshotsActions");
        OpenText = HakoStrings.Get("ScreenshotsOpen");
        OpenFolderText = HakoStrings.Get("ScreenshotsOpenFolder");
        DeleteText = HakoStrings.Get("ScreenshotsDelete");
        OnPropertyChanged(nameof(ActionsText));
        OnPropertyChanged(nameof(OpenText));
        OnPropertyChanged(nameof(OpenFolderText));
        OnPropertyChanged(nameof(DeleteText));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

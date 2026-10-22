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
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Hako;

public sealed partial class InstanceContentView : UserControl
{
    private readonly ObservableCollection<InstanceContentRow> _rows = [];
    private LauncherServices? _services;
    private GameInstance? _instance;
    private ModrinthContentKind _kind;
    private InstanceContentState _state = new();
    private CoalescedRefresh? _stateRefresh;
    private CancellationTokenSource? _readCancellation;
    private long _appliedRevision = -1;
    private bool _active;
    private bool _catalogVisible;
    private bool _importing;
    private string? _availabilityBlockReason;
    private MenuFlyout? _itemMenu;

    public InstanceContentView()
    {
        InitializeComponent();
        ContentItems.ItemsSource = _rows;
        CatalogPane.BackRequested += CatalogPane_BackRequested;
        Loaded += (_, _) => HakoStrings.LanguageChanged += HakoStrings_LanguageChanged;
        Unloaded += (_, _) => HakoStrings.LanguageChanged -= HakoStrings_LanguageChanged;
    }

    internal void Configure(LauncherServices services, GameInstance instance, ModrinthContentKind kind)
    {
        var previous = _instance;
        var changed = previous?.Id != instance.Id || _kind != kind;
        var stringsChanged = previous is null || _kind != kind;
        var instanceStateChanged = previous is null || previous.State != instance.State
            || previous.FolderName != instance.FolderName || previous.LegacyTexturepacks != instance.LegacyTexturepacks;
        var contentLocationChanged = previous is not null
            && (previous.FolderName != instance.FolderName || previous.LegacyTexturepacks != instance.LegacyTexturepacks);
        var becameReady = previous is not null && previous.State != InstanceInstallationState.Ready
            && instance.State == InstanceInstallationState.Ready;
        if (changed && _active)
        {
            CancelRead();
            CatalogPane.SetActive(false);
        }

        _services = services;
        _instance = instance;
        _kind = kind;
        // Store leases change independently of the immutable GameInstance snapshot.
        var blockReason = BlockReason(services, instance, kind, _state);
        var availabilityChanged = _availabilityBlockReason != blockReason;
        _availabilityBlockReason = blockReason;
        CatalogPane.Configure(services, instance, kind);
        if (changed)
        {
            _state = services.ContentController.GetState(instance.Id, kind);
            _appliedRevision = _state.Revision;
            RefreshState();
            if (_active)
            {
                if (_catalogVisible) CatalogPane.SetActive(true);
                else StartLocalReadAndCheck();
            }
        }

        if (stringsChanged) RefreshStrings();
        else if (instanceStateChanged || availabilityChanged) RefreshState();

        if (!changed && _active && !_catalogVisible && (contentLocationChanged || becameReady))
            StartLocalReadAndCheck();
    }

    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (active)
        {
            if (_services is { } services)
            {
                _stateRefresh = new CoalescedRefresh(DispatcherQueue, RefreshFromController);
                services.ContentController.StateChanged += ContentController_StateChanged;
                if (_instance is { } instance)
                {
                    _state = services.ContentController.GetState(instance.Id, _kind);
                    _appliedRevision = _state.Revision;
                }
            }

            if (_catalogVisible) CatalogPane.SetActive(true);
            else StartLocalReadAndCheck();
        }
        else
        {
            _itemMenu?.Hide();
            if (_services is { } services) services.ContentController.StateChanged -= ContentController_StateChanged;
            _stateRefresh?.Stop();
            _stateRefresh = null;
            CancelRead();
            CatalogPane.SetActive(false);
        }

        RefreshState();
    }

    public void RefreshStrings()
    {
        ContentTitle.Text = HakoStrings.Get(_kind == ModrinthContentKind.Mod
            ? "ContentSectionTitleMods" : "ContentSectionTitleResourcePacks");
        ImportFilesButton.Content = HakoStrings.Get("ContentImportFiles");
        ImportFolderButton.Content = HakoStrings.Get("ContentImportFolder");
        ImportFolderButton.Visibility = _kind == ModrinthContentKind.ResourcePack ? Visibility.Visible : Visibility.Collapsed;
        CatalogButton.Content = HakoStrings.Get("ContentOpenModrinth");
        CheckUpdatesButton.Content = HakoStrings.Get("ContentCheckUpdates");
        UpdateAllButton.Content = HakoStrings.Get("ContentUpdateAll");
        OpenFolderButton.Content = HakoStrings.Get("ContentOpenFolder");
        DropHintText.Text = HakoStrings.Get(_kind == ModrinthContentKind.Mod
            ? "ContentDropHint" : "ContentDropHintResourcePacks");
        ContentEmptyText.Text = HakoStrings.Get(_kind == ModrinthContentKind.Mod
            ? "ContentEmptyMods" : "ContentEmptyResourcePacks");
        CatalogPane.RefreshStrings();
        RefreshRows();
        RefreshState();
    }

    public void OpenCatalog()
    {
        _catalogVisible = true;
        CancelRead();
        LocalContentPane.Visibility = Visibility.Collapsed;
        CatalogPane.Visibility = Visibility.Visible;
        CatalogPane.SetActive(_active);
    }

    private void CatalogPane_BackRequested(object? sender, EventArgs e)
    {
        if (_services is null || _instance is null) return;
        _catalogVisible = false;
        CatalogPane.SetActive(false);
        CatalogPane.Visibility = Visibility.Collapsed;
        LocalContentPane.Visibility = Visibility.Visible;
        if (_active) StartLocalReadAndCheck();
        RefreshState();
    }

    private void HakoStrings_LanguageChanged(object? sender, EventArgs e) => RefreshStrings();

    private void ContentController_StateChanged(object? sender, InstanceContentStateChangedEventArgs e)
    {
        if (_instance?.Id == e.Key.InstanceId) _stateRefresh?.Request();
    }

    private void RefreshFromController()
    {
        if (!_active || _services is not { } services || _instance is not { } instance) return;
        var state = services.ContentController.GetState(instance.Id, _kind);
        if (state.Revision < _appliedRevision) return;
        var wasBusy = _state.IsBusy;
        _appliedRevision = state.Revision;
        _state = state;
        RefreshState();
        CatalogPane.ContentStateChanged(state);
        if (wasBusy && !state.IsBusy && !_catalogVisible && !_importing)
            StartLocalReadAndCheck();
    }

    private void StartLocalReadAndCheck()
    {
        if (!_active || _catalogVisible || _services is not { } services || _instance is not { } instance) return;
        CancelRead();
        _readCancellation = new CancellationTokenSource();
        _ = ReadAndCheckAsync(services, instance.Id, _kind, _readCancellation.Token);
    }

    private async Task ReadAndCheckAsync(LauncherServices services, Guid instanceId,
        ModrinthContentKind kind, CancellationToken cancellationToken)
    {
        try
        {
            if (!services.ContentController.GetState(instanceId, kind).IsBusy)
                await services.ContentController.ReloadAsync(instanceId, kind, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!services.ContentController.GetState(instanceId, kind).IsBusy)
                await services.ContentController.CheckUpdatesAsync(instanceId, kind, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Content refresh failed: {exception.GetType().Name}");
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) _stateRefresh?.Request();
        }
    }

    private void CancelRead()
    {
        var cancellation = _readCancellation;
        _readCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void RefreshState()
    {
        if (_services is not { } services || _instance is not { } instance) return;
        var blocked = BlockReason(services, instance, _kind, _state);
        BlockReasonText.Text = blocked ?? "";
        BlockReasonText.Visibility = blocked is null ? Visibility.Collapsed : Visibility.Visible;
        var canMutate = blocked is null && !_state.IsBusy && !_state.IsCheckingUpdates && !_importing;
        if (!canMutate) _itemMenu?.Hide();
        ImportFilesButton.IsEnabled = canMutate;
        ImportFolderButton.IsEnabled = canMutate;
        CatalogButton.IsEnabled = canMutate;
        OpenFolderButton.IsEnabled = instance.State == InstanceInstallationState.Ready;
        CheckUpdatesButton.IsEnabled = BlockReason(services, instance, _kind, _state) is null
            && !_state.IsBusy && !_state.IsCheckingUpdates && !_importing;
        ToolTipService.SetToolTip(CheckUpdatesButton, blocked);
        ToolTipService.SetToolTip(ImportFilesButton, blocked);
        ToolTipService.SetToolTip(ImportFolderButton, blocked);
        ToolTipService.SetToolTip(CatalogButton, blocked);
        ToolTipService.SetToolTip(UpdateAllButton, blocked);
        UpdateAllButton.IsEnabled = canMutate;
        UpdateAllButton.Visibility = _state.HasUpdates ? Visibility.Visible : Visibility.Collapsed;

        var loading = _state.IsLoading || _state.IsCheckingUpdates || _importing;
        ContentProgress.IsActive = loading;
        ContentProgressPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        ContentProgressText.Text = _importing
            ? HakoStrings.Get("ContentActionBusy")
            : HakoStrings.Get(_state.IsCheckingUpdates ? "ContentCheckingUpdates" : "ContentLoading");
        ContentCount.Text = string.Format(HakoStrings.Get("ContentActiveCount"),
            _state.Items.Count(item => item.Enabled), _state.Items.Count);
        ContentEmptyText.Visibility = _state.Items.Count == 0 && !_state.IsLoading && !_state.IsBusy
            ? Visibility.Visible : Visibility.Collapsed;
        ContentUpdateMessageText.Visibility = _state.UpdateMessage is null ? Visibility.Collapsed : Visibility.Visible;
        ContentUpdateMessageText.Text = _state.UpdateMessage is { } message ? FormatFailure(message) : "";
        ContentErrorText.Visibility = _state.Error is null ? Visibility.Collapsed : Visibility.Visible;
        ContentErrorText.Text = _state.Error is { } error ? FormatFailure(error) : "";
        ContentDiagnosticText.Visibility = _state.Error?.Arguments.ContainsKey("diagnostic") == true
            ? Visibility.Visible : Visibility.Collapsed;
        ContentDiagnosticText.Text = _state.Error?.Arguments.TryGetValue("diagnostic", out var diagnostic) == true
            ? $"{HakoStrings.Get("ContentDiagnosticLabel")} {diagnostic}" : "";
        RefreshRows(canMutate);
        CatalogPane.ContentStateChanged(_state);
    }

    private string? BlockReason(LauncherServices services, GameInstance instance,
        ModrinthContentKind kind, InstanceContentState state)
    {
        if (kind == ModrinthContentKind.Mod && instance.ModLoader != InstanceModLoader.Fabric)
            return HakoStrings.Get("ContentBlockedFabric");
        if (instance.State is InstanceInstallationState.Queued or InstanceInstallationState.Installing)
            return HakoStrings.Get("ContentBlockedInstall");
        if (instance.State != InstanceInstallationState.Ready)
            return HakoStrings.Get("ContentBlockedBuild");
        if (state.IsBusy || services.Store.IsContentBusy(instance.Id))
            return HakoStrings.Get("ContentBlockedFilesBusy");
        if (services.Store.ManagementBlockedError(instance) is { } blocked)
            return HakoStrings.Get(blocked.ResourceKey());
        return null;
    }

    private void RefreshRows(bool? canMutateOverride = null)
    {
        if (_services is not { } services || _instance is not { } instance) return;
        var canMutate = canMutateOverride ?? (BlockReason(services, instance, _kind, _state) is null
            && !_state.IsBusy && !_state.IsCheckingUpdates && !_importing);
        _rows.Clear();
        foreach (var item in _state.Items)
        {
            _state.Updates.TryGetValue(item.LogicalName, out var update);
            _rows.Add(new InstanceContentRow(item, update, canMutate, BlockReason(services, instance, _kind, _state)));
        }
    }

    private async void ImportFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindowId is not { } windowId || _instance is not { } instance || !CanMutate()) return;
        var kind = _kind;
        try
        {
            var picker = new FileOpenPicker(windowId)
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                FileTypeFilter = { kind == ModrinthContentKind.Mod ? ".jar" : ".zip" }
            };
            var files = await picker.PickMultipleFilesAsync();
            if (files is not null) await ImportPathsAsync(files.Select(file => file.Path), instance, kind);
        }
        catch (Exception exception)
        {
            ShowActionFailure(ContentFailureCode.ContentOperationFailed, exception.Message);
        }
    }

    private async void ImportFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindowId is not { } windowId || _instance is not { } instance
            || _kind != ModrinthContentKind.ResourcePack || !CanMutate()) return;
        var kind = _kind;
        try
        {
            var picker = new FolderPicker(windowId) { SuggestedStartLocation = PickerLocationId.Downloads };
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) await ImportPathsAsync([folder.Path], instance, kind);
        }
        catch (Exception exception)
        {
            ShowActionFailure(ContentFailureCode.ContentOperationFailed, exception.Message);
        }
    }

    private async Task ImportPathsAsync(IEnumerable<string> paths, GameInstance instance, ModrinthContentKind kind)
    {
        if (_services is not { } services || !CanMutate()) return;
        _importing = true;
        RefreshState();
        var imported = 0;
        var feedback = new List<string>();
        try
        {
            foreach (var path in paths)
            {
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                try
                {
                    var (isSupported, invalidKey) = await Task.Run(() => ValidateImportPath(path, kind));
                    if (!isSupported)
                    {
                        feedback.Add(string.Format(HakoStrings.Get(invalidKey), name));
                        continue;
                    }

                    var result = await services.ContentController.ImportLocalAsync(instance.Id, kind, path);
                    if (result.Succeeded) imported++;
                    else if (result.Failure is { } failure)
                        feedback.Add(string.Format(HakoStrings.Get("ContentImportFailureLine"), name, FormatFailureDetails(failure)));
                    else
                        feedback.Add(string.Format(HakoStrings.Get("ContentImportCancelled"), name));
                }
                catch (Exception exception)
                {
                    var failure = new ContentFailure(ContentFailureCode.ContentOperationFailed,
                        new Dictionary<string, string> { ["diagnostic"] = exception.Message });
                    feedback.Add(string.Format(HakoStrings.Get("ContentImportFailureLine"), name, FormatFailureDetails(failure)));
                }
            }
        }
        finally
        {
            _importing = false;
            _stateRefresh?.Request();
            RefreshState();
        }

        if (imported > 0) feedback.Insert(0, string.Format(HakoStrings.Get("ContentImportSuccess"), imported));
        ContentActionFeedbackText.Text = string.Join(Environment.NewLine, feedback);
        ContentActionFeedbackText.Visibility = feedback.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static (bool IsSupported, string FailureKey) ValidateImportPath(string path, ModrinthContentKind kind)
    {
        var failureKey = kind == ModrinthContentKind.Mod ? "ContentInvalidModFile" : "ContentInvalidPackFile";
        if (Directory.Exists(path))
        {
            if (kind != ModrinthContentKind.ResourcePack) return (false, failureKey);
            if (!File.Exists(Path.Combine(path, "pack.mcmeta")))
            {
                failureKey = "ContentInvalidPackFolder";
                return (false, failureKey);
            }

            return (true, failureKey);
        }

        if (!File.Exists(path)) return (false, failureKey);
        var extension = Path.GetExtension(path);
        var supported = kind == ModrinthContentKind.Mod
            ? extension.Equals(".jar", StringComparison.OrdinalIgnoreCase)
            : extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
        return (supported, failureKey);
    }

    private void DropTarget_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems) && CanMutate()
            ? DataPackageOperation.Copy : DataPackageOperation.None;
        e.DragUIOverride.Caption = HakoStrings.Get(_kind == ModrinthContentKind.Mod
            ? "ContentDropHint" : "ContentDropHintResourcePacks");
    }

    private async void DropTarget_Drop(object sender, DragEventArgs e)
    {
        if (!CanMutate() || _instance is not { } instance) return;
        var kind = _kind;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Where(item => item is StorageFile or StorageFolder).Select(item => item.Path).ToArray();
            if (paths.Length == 0)
            {
                ShowActionFailure(ContentFailureCode.ContentOperationFailed, HakoStrings.Get("ContentInvalidDrop"));
                return;
            }

            await ImportPathsAsync(paths, instance, kind);
        }
        catch (Exception exception)
        {
            ShowActionFailure(ContentFailureCode.ContentOperationFailed, exception.Message);
        }
    }

    private void CatalogButton_Click(object sender, RoutedEventArgs e)
    {
        if (CanMutate()) OpenCatalog();
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is not { } services || _instance is not { } instance || !CanMutate()) return;
        CancelRead();
        _readCancellation = new CancellationTokenSource();
        try { await services.ContentController.CheckUpdatesAsync(instance.Id, _kind, _readCancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ShowActionFailure(ContentFailureCode.ModrinthRequestFailed, exception.Message); }
        finally { _stateRefresh?.Request(); }
    }

    private async void UpdateAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is not { } services || _instance is not { } instance || !CanMutate()) return;
        try
        {
            var result = await services.ContentController.UpdateAllAsync(instance.Id, _kind);
            var succeeded = result.Items.Count(item => item.Succeeded);
            var failed = result.Items.Where(item => !item.Succeeded).ToArray();
            var lines = new List<string>
            {
                string.Format(HakoStrings.Get("ContentUpdateAllSummary"), succeeded, failed.Length)
            };
            lines.AddRange(failed.Select(item => string.Format(HakoStrings.Get("ContentBulkFailureLine"),
                item.Filename, item.Failure is null ? HakoStrings.Get("ContentFailureOperationFailed") : FormatFailureDetails(item.Failure))));
            ContentActionFeedbackText.Text = string.Join(Environment.NewLine, lines);
            ContentActionFeedbackText.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            ShowActionFailure(ContentFailureCode.ContentOperationFailed, exception.Message);
        }
        finally { _stateRefresh?.Request(); }
    }

    private async void UpdateItem_Click(object sender, RoutedEventArgs e)
    {
        if (_services is not { } services || _instance is not { } instance
            || (sender as FrameworkElement)?.Tag is not InstanceContentItem item || !CanMutate()) return;
        try
        {
            var result = await services.ContentController.UpdateAsync(instance.Id, _kind, item);
            if (!result.Succeeded && result.Failure is { } failure) ShowActionFailure(failure);
        }
        catch (Exception exception) { ShowActionFailure(ContentFailureCode.ContentOperationFailed, exception.Message); }
        finally { _stateRefresh?.Request(); }
    }

    private async void EnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_services is not { } services || _instance is not { } instance || sender is not ToggleSwitch toggle
            || toggle.Tag is not InstanceContentItem item || toggle.IsOn == item.Enabled) return;
        if (!CanMutate())
        {
            toggle.IsOn = item.Enabled;
            return;
        }

        try
        {
            var result = await services.ContentController.SetEnabledAsync(instance.Id, _kind, item, toggle.IsOn);
            if (!result.Succeeded && result.Failure is { } failure) ShowActionFailure(failure);
        }
        catch (Exception exception) { ShowActionFailure(ContentFailureCode.ContentOperationFailed, exception.Message); }
        finally { _stateRefresh?.Request(); }
    }

    private async void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if (_services is not { } services || _instance is not { } instance
            || (sender as FrameworkElement)?.Tag is not InstanceContentItem item || !CanMutate()) return;
        try
        {
            var result = await services.ContentController.DeleteAsync(instance.Id, _kind, item);
            if (!result.Succeeded && result.Failure is { } failure) ShowActionFailure(failure);
        }
        catch (Exception exception) { ShowActionFailure(ContentFailureCode.ContentOperationFailed, exception.Message); }
        finally { _stateRefresh?.Request(); }
    }

    private void ItemMenu_Opened(object? sender, object e) => _itemMenu = sender as MenuFlyout;

    private async void RevealItem_Click(object sender, RoutedEventArgs e)
    {
        if (_services is not { } services || _instance is not { } instance
            || (sender as FrameworkElement)?.Tag is not InstanceContentItem item) return;
        try
        {
            var folder = await Task.Run(() =>
            {
                var root = services.Store.GetInstanceDirectory(instance.Id);
                var relative = Path.GetRelativePath(root, item.Path);
                var safePath = InstanceStorage.ContainedPath(relative, root, allowBaseReparsePoint: true);
                return item.IsDirectory ? safePath : Path.GetDirectoryName(safePath);
            });
            await OpenLocationAsync(folder);
        }
        catch (Exception) { await HakoDialogs.ShowErrorAsync(HakoStrings.Get("ContentOpenFailed")); }
    }

    private async void ContentSource_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Uri { IsAbsoluteUri: true, Scheme: "https" } uri) return;
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(uri))
                await HakoDialogs.ShowErrorAsync(HakoStrings.Get("ContentLinkFailed"));
        }
        catch (Exception)
        {
            await HakoDialogs.ShowErrorAsync(HakoStrings.Get("ContentLinkFailed"));
        }
    }

    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is not { } services || _instance is not { } instance) return;
        try
        {
            var folder = await Task.Run(() =>
            {
                var root = services.Store.GetInstanceDirectory(instance.Id);
                var relative = _kind == ModrinthContentKind.Mod ? "minecraft/mods"
                    : instance.LegacyTexturepacks ? "minecraft/texturepacks" : "minecraft/resourcepacks";
                var path = InstanceStorage.ContainedPath(relative, root, allowBaseReparsePoint: true);
                Directory.CreateDirectory(path);
                return path;
            });
            await OpenLocationAsync(folder);
        }
        catch (Exception) { await HakoDialogs.ShowErrorAsync(HakoStrings.Get("ContentOpenFailed")); }
    }

    private async Task OpenLocationAsync(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            if (!await Windows.System.Launcher.LaunchFolderAsync(folder))
                await HakoDialogs.ShowErrorAsync(HakoStrings.Get("ContentOpenFailed"));
        }
        catch (Exception) { await HakoDialogs.ShowErrorAsync(HakoStrings.Get("ContentOpenFailed")); }
    }

    private bool CanMutate() => _services is { } services && _instance is { } instance
        && BlockReason(services, instance, _kind, _state) is null
        && !_state.IsBusy && !_state.IsCheckingUpdates && !_importing;

    private void ShowActionFailure(ContentFailureCode code, string? diagnostic = null)
    {
        var message = HakoStrings.Get(ContentResourceKeys.Failure(code));
        if (code == ContentFailureCode.ContentOperationFailed && !string.IsNullOrWhiteSpace(diagnostic))
            message += $"\n{HakoStrings.Get("ContentDiagnosticLabel")} {diagnostic}";
        ContentActionFeedbackText.Text = message;
        ContentActionFeedbackText.Visibility = Visibility.Visible;
    }

    private void ShowActionFailure(ContentFailure failure)
    {
        ContentActionFeedbackText.Text = FormatFailureDetails(failure);
        ContentActionFeedbackText.Visibility = Visibility.Visible;
    }

    internal static string FormatFailure(ContentFailure failure) => ContentResourceKeys.FormatFailure(failure, HakoStrings.Get);

    internal static string FormatFailureDetails(ContentFailure failure) => ContentResourceKeys.FormatFailureDetails(failure, HakoStrings.Get);
}

public sealed class InstanceContentRow
{
    public InstanceContentRow(InstanceContentItem item, InstanceContentUpdate? update, bool canMutate, string? blockReason)
    {
        BlockReason = blockReason;
        Item = item;
        Name = item.Name;
        SourceText = HakoStrings.Get(item.Origin?.Source switch
        {
            ModSource.Modrinth => "ContentSourceModrinth",
            ModSource.CurseForge => "ContentSourceCurseForge",
            _ => "ContentSourceLocal"
        });
        VersionText = item.Origin?.VersionNumber ?? "";
        PageUrl = item.Origin?.PageURL;
        HasPageUrl = PageUrl is { IsAbsoluteUri: true, Scheme: "https" };
        Enabled = item.Enabled;
        EnabledOnText = HakoStrings.Get("ContentEnabledOn");
        EnabledOffText = HakoStrings.Get("ContentEnabledOff");
        RevealText = HakoStrings.Get("ContentRevealItem");
        DeleteText = HakoStrings.Get("ContentDeleteItem");
        CanToggle = canMutate;
        UpdateVisibility = update is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateText = update is null ? HakoStrings.Get("ContentUpdateButton")
            : string.Format(HakoStrings.Get("ContentUpdateItem"), update.Version?.VersionNumber ?? update.Api?.Version ?? "");
        CanUpdate = canMutate && update is not null;
        PlaceholderGlyph = item.IsDirectory ? "\uE8B7" : "\uE8A5";
        Icon = PageUrl is { IsAbsoluteUri: true, Scheme: "https" } && item.Origin?.IconURL is { IsAbsoluteUri: true, Scheme: "https" } icon
            ? new BitmapImage(icon) : null;
        IconVisibility = Icon is null ? Visibility.Collapsed : Visibility.Visible;
        PlaceholderVisibility = Icon is null ? Visibility.Visible : Visibility.Collapsed;
    }

    public InstanceContentItem Item { get; }
    public string? BlockReason { get; }
    public string Name { get; }
    public string SourceText { get; }
    public string VersionText { get; }
    public Uri? PageUrl { get; }
    public bool HasPageUrl { get; }
    public bool Enabled { get; }
    public bool CanToggle { get; }
    public string EnabledOnText { get; }
    public string EnabledOffText { get; }
    public string RevealText { get; }
    public string DeleteText { get; }
    public string UpdateText { get; }
    public bool CanUpdate { get; }
    public Visibility UpdateVisibility { get; }
    public string PlaceholderGlyph { get; }
    public ImageSource? Icon { get; }
    public Visibility IconVisibility { get; }
    public Visibility PlaceholderVisibility { get; }
}

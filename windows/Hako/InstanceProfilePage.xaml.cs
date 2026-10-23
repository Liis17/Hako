using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Hako;

/// <summary>Build profile (InstanceProfileView on macOS): header, «Играть», installation controls, «⋯» and the «Настройки» section.</summary>
public sealed partial class InstanceProfilePage : Page, INavigationGuard
{
    private enum ProfileSection { Mods, ResourcePacks, Worlds, Settings }

    private LauncherServices? _services;
    private Guid _id;
    private GameInstance? _instance;
    private CoalescedRefresh? _refresh;
    private DispatcherQueueTimer? _saveTimer;
    private InstanceDraft? _pendingSave;
    private byte[]? _iconDataToSave;
    private Task _saving = Task.CompletedTask;
    private Guid? _iconRevision;
    private InstanceInstallationState? _manifestState;
    private bool _loadingSettings;
    private bool _busyOperation;
    private bool _deleted;
    private DispatcherQueueTimer? _nameTimer;
    private string? _nameToValidate;
    private string? _validatedName;
    private string? _nameErrorKey;
    private readonly NameValidation _nameValidation = new();
    private int _instancesChangedPending;
    private ProfileSection _selectedSection = ProfileSection.Settings;
    private bool _updatingSectionTabs;

    public InstanceProfilePage()
    {
        InitializeComponent();
        ParametersEditor.Changed += (_, _) => ScheduleSave(immediate: false);
        IconPicker.Changed += (_, _) =>
        {
            _iconDataToSave = IconPicker.IconData;
            ScheduleSave(immediate: true);
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var context = (InstanceProfileContext)e.Parameter;
        _services = context.Services;
        _id = context.InstanceId;
        _refresh = new CoalescedRefresh(DispatcherQueue, Refresh);
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(500);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => _ = FlushSettingsAsync();
        _nameTimer = DispatcherQueue.CreateTimer();
        _nameTimer.Interval = TimeSpan.FromMilliseconds(300);
        _nameTimer.IsRepeating = false;
        _nameTimer.Tick += (_, _) => _ = ValidateNameAsync();
        _services.Store.InstancesChanged += Store_InstancesChanged;
        _services.Store.BusyStateChanged += Changed;
        _services.Installations.PropertyChanged += Changed;
        _services.Games.RunStateChanged += Changed;
        _services.App.MinecraftSessions.PropertyChanged += Changed;
        _services.App.PropertyChanged += Changed;
        HakoStrings.LanguageChanged += HakoStrings_LanguageChanged;

        _instance = Current();
        if (_instance is { } instance)
        {
            _selectedSection = instance.ModLoader == InstanceModLoader.Fabric
                ? ProfileSection.Mods : ProfileSection.ResourcePacks;
            if (_services.PreviewOptions is { Mode: ProfilePreviewMode.Builds } preview)
            {
                _selectedSection = preview.Page switch
                {
                    PreviewPage.Content or PreviewPage.Catalog => ProfileSection.Mods,
                    PreviewPage.ResourcePacks => ProfileSection.ResourcePacks,
                    PreviewPage.Worlds or PreviewPage.Datapacks or PreviewPage.DatapackCatalog => ProfileSection.Worlds,
                    _ => _selectedSection
                };
            }

            NameBox.Text = instance.Name;
            LoadSettings(instance);
        }

        RefreshStrings();
        if (_services.PreviewOptions is { Mode: ProfilePreviewMode.Builds, Page: PreviewPage.Catalog })
            ContentSection.OpenCatalog();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _refresh?.Stop();
        _saveTimer?.Stop();
        ContentSection.SetActive(false);
        WorldsSection.SetActive(false);
        InvalidateNameValidation();
        _ = FlushSettingsAsync();
        if (_services is { } services)
        {
            services.Store.InstancesChanged -= Store_InstancesChanged;
            services.Store.BusyStateChanged -= Changed;
            services.Installations.PropertyChanged -= Changed;
            services.Games.RunStateChanged -= Changed;
            services.App.MinecraftSessions.PropertyChanged -= Changed;
            services.App.PropertyChanged -= Changed;
        }

        HakoStrings.LanguageChanged -= HakoStrings_LanguageChanged;
        base.OnNavigatedFrom(e);
    }

    private GameInstance? Current() => _services?.Store.Instances.FirstOrDefault(instance => instance.Id == _id);

    private void Changed(object? sender, EventArgs e) => _refresh?.Request();

    private void Store_InstancesChanged(object? sender, EventArgs e)
    {
        _nameValidation.Invalidate();
        Interlocked.Exchange(ref _instancesChangedPending, 1);
        _refresh?.Request();
    }

    private void HakoStrings_LanguageChanged(object? sender, EventArgs e) => RefreshStrings();

    private void RefreshStrings()
    {
        PlayButtonText.Text = HakoStrings.Get("PlayButton");
        PreparingText.Text = HakoStrings.Get("PlayPreparing");
        OpenLogButton.Content = HakoStrings.Get("OpenLaunchLog");
        RetrySessionButton.Content = HakoStrings.Get("RetrySessionButton");
        ToolTipService.SetToolTip(ActionsButton, HakoStrings.Get("InstanceActions"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ActionsButton, HakoStrings.Get("InstanceActions"));
        DeleteMenuItem.Text = HakoStrings.Get("DeleteBuildMenu");
        SettingsTitle.Text = HakoStrings.Get("SettingsTitle");
        ModsSectionButton.Content = HakoStrings.Get("InstanceSectionMods");
        ResourcePacksSectionButton.Content = HakoStrings.Get("InstanceSectionResourcePacks");
        WorldsSectionButton.Content = HakoStrings.Get("InstanceSectionWorlds");
        SettingsSectionButton.Content = HakoStrings.Get("SettingsTitle");
        ProfileSectionTitle.Text = HakoStrings.Get("InstanceProfileSection");
        NameLabel.Text = HakoStrings.Get("CreationNameLabel");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NameBox, HakoStrings.Get("CreationNameLabel"));
        RenameBlockedText.Text = HakoStrings.Get("RenameBlocked");
        SaveNameButton.Content = HakoStrings.Get("SaveNameButton");
        ParametersTitle.Text = HakoStrings.Get("LaunchParametersTitle");
        SettingsBlockedText.Text = HakoStrings.Get("SettingsBlockedInstalling");
        ManifestMissingText.Text = HakoStrings.Get("ManifestMissing");
        AutosaveText.Text = HakoStrings.Get("SettingsAutosave");
        NextLaunchText.Text = HakoStrings.Get("SettingsNextLaunch");
        IconPicker.RefreshStrings();
        ParametersEditor.RefreshStrings();
        WorldsSection.RefreshStrings();
        Refresh();
    }

    private void LoadSettings(GameInstance instance)
    {
        _loadingSettings = true;
        ParametersEditor.Load(DraftFrom(instance), null, Architecture(instance), _services!.Settings.GetGameLaunchDefaults());
        IconPicker.Load(instance.IconSymbol, null);
        _loadingSettings = false;
    }

    private static InstanceDraft DraftFrom(GameInstance instance) => new()
    {
        Name = instance.Name,
        IconSymbol = instance.IconSymbol,
        ArgumentSource = instance.ArgumentSource,
        OfflineMode = instance.OfflineMode,
        OfflineUsername = instance.OfflineUsername,
        Parameters = instance.Parameters,
        ModLoader = instance.ModLoader,
        FabricConfiguration = instance.FabricConfiguration,
        JavaMajorVersion = instance.JavaMajorVersion,
        LegacyTexturepacks = instance.LegacyTexturepacks,
        Architecture = instance.Architecture
    };

    private static GameArchitecture Architecture(GameInstance instance) =>
        Enum.TryParse<GameArchitecture>(instance.Architecture, ignoreCase: false, out var architecture) && Enum.IsDefined(architecture)
            ? architecture
            : Environment.Is64BitOperatingSystem ? GameArchitecture.X64 : GameArchitecture.X86;

    private void Refresh()
    {
        if (_services is not { } services || Current() is not { } instance)
        {
            return;
        }

        var instancesChanged = Interlocked.Exchange(ref _instancesChangedPending, 0) != 0;
        var previous = _instance;
        _instance = instance;
        NameText.Text = instance.Name;
        DetailsText.Text = instance.JavaMajorVersion > 0
            ? string.Format(HakoStrings.Get("InstanceDetailsJavaFormat"), instance.VersionID, InstanceText.LoaderTitle(instance), instance.JavaMajorVersion)
            : InstanceText.Details(instance);
        if (_iconRevision != instance.IconRevision)
        {
            _iconRevision = instance.IconRevision;
            _ = LoadIconAsync(instance);
        }

        if (previous is not null && previous.Name != instance.Name)
        {
            if (NameBox.Text == previous.Name) NameBox.Text = instance.Name;
            InvalidateNameValidation();
        }
        else if (instancesChanged && NameBox.Text != instance.Name)
        {
            InvalidateNameValidation();
        }

        if (_manifestState != instance.State)
        {
            _manifestState = instance.State;
            _ = LoadManifestAsync(instance);
        }

        RefreshPlayControls(services, instance);
        RefreshInstallation(services, instance);
        RefreshSections(services, instance);

        var installing = instance.State is InstanceInstallationState.Queued or InstanceInstallationState.Installing;
        var settingsEditable = !installing && !services.Store.IsContentBusy(instance.Id);
        ParametersEditor.SetEditable(settingsEditable);
        IconPicker.SetEditable(settingsEditable);
        SettingsBlockedText.Visibility = settingsEditable ? Visibility.Collapsed : Visibility.Visible;
        var run = services.Games.GetState(instance.Id)?.Phase;
        NextLaunchText.Visibility = run is GameRunPhase.Running or GameRunPhase.Preparing ? Visibility.Visible : Visibility.Collapsed;
        ManifestMissingText.Visibility = ParametersEditor.Draft.ArgumentSource == LaunchArgumentSource.Mojang
            && instance.State != InstanceInstallationState.Ready
            ? Visibility.Visible
            : Visibility.Collapsed;

        VersionInfoText.Text = InstanceText.Details(instance);
        FolderText.Text = string.Format(HakoStrings.Get("InstanceFolderFormat"), FolderDisplayPath(services, instance));
        UpdateName();
    }

    private void RefreshSections(LauncherServices services, GameInstance instance)
    {
        var hasMods = instance.ModLoader == InstanceModLoader.Fabric;
        ModsSectionButton.Visibility = hasMods ? Visibility.Visible : Visibility.Collapsed;
        if (!hasMods && _selectedSection == ProfileSection.Mods)
            _selectedSection = ProfileSection.ResourcePacks;

        var showContent = _selectedSection is ProfileSection.Mods or ProfileSection.ResourcePacks;
        var showWorlds = _selectedSection == ProfileSection.Worlds;
        SettingsSection.Visibility = _selectedSection == ProfileSection.Settings ? Visibility.Visible : Visibility.Collapsed;
        ContentSection.Visibility = showContent ? Visibility.Visible : Visibility.Collapsed;
        WorldsSection.Visibility = showWorlds ? Visibility.Visible : Visibility.Collapsed;
        SectionTabs.Visibility = Visibility.Visible;

        _updatingSectionTabs = true;
        SectionTabs.SelectedIndex = (int)_selectedSection;
        _updatingSectionTabs = false;
        if (showContent)
        {
            var kind = _selectedSection == ProfileSection.Mods
                ? ModrinthContentKind.Mod : ModrinthContentKind.ResourcePack;
            ContentSection.Configure(services, instance, kind);
        }

        ContentSection.SetActive(showContent);
        WorldsSection.Configure(services, instance, services.PreviewOptions);
        WorldsSection.SetActive(showWorlds);
    }

    private void SelectSection(ProfileSection section)
    {
        if (_instance is not { } instance || _services is not { } services) return;
        if (section == ProfileSection.Mods && instance.ModLoader != InstanceModLoader.Fabric) return;
        _selectedSection = section;
        RefreshSections(services, instance);
    }

    private void SectionTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSectionTabs || SectionTabs.SelectedItem is not RadioButton { Tag: string tag }) return;
        if (Enum.TryParse<ProfileSection>(tag, out var section)) SelectSection(section);
    }

    internal void WindowReturned() => WorldsSection.RefreshWorldsIfActive();

    private static string FolderDisplayPath(LauncherServices services, GameInstance instance) =>
        DisplayPath.Format(Path.Combine(services.Store.GameRoot, instance.FolderName));

    private void RefreshPlayControls(LauncherServices services, GameInstance instance)
    {
        var ready = instance.State == InstanceInstallationState.Ready;
        PlayPanel.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        if (!ready)
        {
            return;
        }

        var availability = InstancePlayLogic.Evaluate(services, instance);
        var run = services.Games.GetState(instance.Id);
        PlayButton.IsEnabled = availability.Enabled;
        ToolTipService.SetToolTip(PlayButton, availability.Reason ?? HakoStrings.Get("PlayHint"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlayButton, string.Format(HakoStrings.Get("PlayAccessibilityFormat"), instance.Name));
        OfflineNameText.Text = instance.OfflineMode ? string.Format(HakoStrings.Get("OfflineNameFormat"), instance.OfflineUsername) : "";
        OfflineNameText.Visibility = instance.OfflineMode ? Visibility.Visible : Visibility.Collapsed;
        PreparingPanel.Visibility = run?.Phase == GameRunPhase.Preparing ? Visibility.Visible : Visibility.Collapsed;

        string status;
        var critical = false;
        switch (run?.Phase)
        {
            case GameRunPhase.Preparing:
                status = "";
                break;
            case GameRunPhase.Running:
                status = HakoStrings.Get("GameRunning");
                break;
            case GameRunPhase.Failed:
                status = BuildCardModel.LaunchErrorText(run);
                critical = true;
                break;
            default:
                status = availability.Reason ?? "";
                critical = availability.CanRetrySession;
                break;
        }

        PlayStatusText.Text = status;
        PlayStatusText.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PlayStatusText.Foreground = (Brush)Application.Current.Resources[critical ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];
        OpenLogButton.Visibility = run?.Phase == GameRunPhase.Failed && File.Exists(run.LogPath) ? Visibility.Visible : Visibility.Collapsed;
        OpenLogButton.Tag = run?.LogPath;
        RetrySessionButton.Visibility = run is null && availability.CanRetrySession ? Visibility.Visible : Visibility.Collapsed;
        RetrySessionButton.IsEnabled = services.App.MinecraftSessions.Status != MinecraftSessionStatus.Connecting;
    }

    private void RefreshInstallation(LauncherServices services, GameInstance instance)
    {
        var ready = instance.State == InstanceInstallationState.Ready;
        InstallationPanel.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        if (ready)
        {
            return;
        }

        var progress = services.Installations.Progress.GetValueOrDefault(instance.Id);
        InstallationStateText.Text = HakoStrings.Get(instance.State.ResourceKey());
        InstallationActionButton.Content = HakoStrings.Get(instance.State switch
        {
            InstanceInstallationState.Paused => "InstallationResume",
            InstanceInstallationState.Failed => "InstallationRetry",
            _ => "InstallationPause"
        });
        InstallationActionButton.IsEnabled = !(instance.State == InstanceInstallationState.Installing && instance.PauseRequested);

        var installing = instance.State == InstanceInstallationState.Installing && progress is not null;
        var fraction = InstanceText.Fraction(progress);
        InstallationProgress.Visibility = installing ? Visibility.Visible : Visibility.Collapsed;
        InstallationProgress.IsIndeterminate = installing && fraction is null;
        InstallationProgress.Value = fraction ?? 0;
        InstallationProgressText.Text = instance.State == InstanceInstallationState.Queued
            ? HakoStrings.Get("InstallationQueuedHint")
            : installing ? InstanceText.Status(instance, progress) : "";
        InstallationProgressText.Visibility = InstallationProgressText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        InstallationErrorText.Text = instance.State == InstanceInstallationState.Failed ? InstanceText.InstallationError(instance) : "";
        InstallationErrorText.Visibility = InstallationErrorText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateName()
    {
        if (_services is not { } services || _instance is not { } instance)
        {
            return;
        }

        var blocked = services.Store.ManagementBlockedError(instance) is not null || _busyOperation;
        NameBox.IsEnabled = !blocked;
        RenameBlockedText.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        NameCounter.Text = $"{NameBox.Text.Length}/60";
        var name = NameBox.Text;
        if (name == instance.Name)
        {
            if (_nameToValidate is not null)
            {
                _nameValidation.Invalidate();
            }
            _nameTimer?.Stop();
            _nameToValidate = null;
            _validatedName = name;
            _nameErrorKey = null;
        }
        else if (_validatedName != name && _nameToValidate != name && _nameTimer is { } timer)
        {
            // ValidateName waits for the store gate and checks the disk, so it runs off the UI thread after typing pauses.
            _nameValidation.Invalidate();
            _nameToValidate = name;
            timer.Stop();
            timer.Start();
        }

        var errorKey = _validatedName == name ? _nameErrorKey : null;
        NameErrorText.Text = errorKey is null ? "" : HakoStrings.Get(errorKey);
        NameErrorText.Visibility = errorKey is null ? Visibility.Collapsed : Visibility.Visible;
        SaveNameButton.IsEnabled = !blocked && name != instance.Name && _validatedName == name && errorKey is null;
    }

    private async Task ValidateNameAsync()
    {
        if (_services is not { } services || _nameToValidate is not { } name)
        {
            return;
        }

        var id = _id;
        var result = await _nameValidation.ValidateAsync(name, input => Task.Run(() =>
        {
            try
            {
                services.Store.ValidateName(input, id);
                return null;
            }
            catch (Exception exception)
            {
                return InstallationResourceKeys.ErrorResourceKey(exception);
            }
        }));
        if (!result.IsCurrent || NameBox.Text != name || _nameToValidate != name)
        {
            return;
        }

        _nameToValidate = null;
        _validatedName = name;
        _nameErrorKey = result.ErrorKey;
        UpdateName();
    }

    private void InvalidateNameValidation()
    {
        _nameValidation.Invalidate();
        _nameToValidate = null;
        _validatedName = null;
        _nameErrorKey = null;
        _nameTimer?.Stop();
    }

    private async Task LoadIconAsync(GameInstance instance)
    {
        if (_services is not { } services)
        {
            return;
        }

        var image = await InstanceIconSource.CreateAsync(await InstanceIconSource.ReadAsync(services.Store, instance));
        if (_iconRevision != instance.IconRevision)
        {
            return;
        }

        HeaderIcon.Show(instance.IconSymbol, image);
        if (IconPicker.IconData is null && string.IsNullOrEmpty(instance.IconSymbol))
        {
            IconPicker.Load(instance.IconSymbol, image);
        }
    }

    private async Task LoadManifestAsync(GameInstance instance)
    {
        if (_services is not { } services || instance.State != InstanceInstallationState.Ready)
        {
            return;
        }

        MojangVersionMetadata? manifest;
        try
        {
            manifest = await Task.Run(() => MojangClient.ReadInstalledManifest(services.Store.GetInstanceDirectory(instance.Id), instance));
        }
        catch (Exception)
        {
            manifest = null;
        }

        if (_instance?.Id == instance.Id)
        {
            ParametersEditor.SetManifest(manifest, Architecture(instance));
            ParametersEditor.SetFabricProfile(null);
            if (manifest is not null && instance.ModLoader == InstanceModLoader.Fabric
                && instance.FabricConfiguration is { LoaderVersion: { Length: > 0 } loaderVersion })
            {
                _ = LoadFabricProfileAsync(instance, loaderVersion);
            }
        }
    }

    private async Task LoadFabricProfileAsync(GameInstance instance, string loaderVersion)
    {
        if (_services is not { } services || instance.FabricProfileSHA1 is not { } sha1) return;
        try
        {
            var root = await Task.Run(() => services.Store.GetInstanceDirectory(instance.Id));
            var profile = await FabricClient.ReadInstalledProfileAsync(root, instance.VersionID, loaderVersion, sha1);
            if (_instance?.Id == instance.Id && Current()?.FabricConfiguration?.LoaderVersion == loaderVersion)
            {
                ParametersEditor.SetFabricProfile(profile);
            }
        }
        catch (Exception)
        {
            if (_instance?.Id == instance.Id) ParametersEditor.SetFabricProfile(null);
        }
    }

    private void ScheduleSave(bool immediate)
    {
        if (_loadingSettings || _saveTimer is null)
        {
            return;
        }

        _saveTimer.Stop();
        if (immediate)
        {
            _ = FlushSettingsAsync();
        }
        else
        {
            _saveTimer.Start();
        }
    }

    /// <summary>Saves the latest valid draft; saves run one at a time so an older draft never lands last.</summary>
    private Task FlushSettingsAsync()
    {
        _saveTimer?.Stop();
        if (_deleted || _services is not { } services || !ParametersEditor.IsValid || Current() is not { } current
            || current.State is InstanceInstallationState.Queued or InstanceInstallationState.Installing)
        {
            return _saving;
        }

        var draft = ParametersEditor.Draft with
        {
            IconSymbol = IconPicker.Symbol,
            IconData = _iconDataToSave
        };
        // Loading the page raises TextChanged later; an unchanged draft must not rewrite instances.json.
        if (draft.IconData is null && draft.IconSymbol == current.IconSymbol && !ParametersChanged(current))
        {
            return _saving;
        }

        _pendingSave = draft;
        _iconDataToSave = null;
        if (!_saving.IsCompleted)
        {
            return _saving;
        }

        _saving = SaveLoopAsync(services);
        return _saving;
    }

    private async Task SaveLoopAsync(LauncherServices services)
    {
        while (_pendingSave is { } draft)
        {
            _pendingSave = null;
            try
            {
                await Task.Run(() => services.Store.UpdateSettings(_id, draft));
                SettingsErrorText.Visibility = Visibility.Collapsed;
            }
            catch (Exception exception)
            {
                SettingsErrorText.Text = InstanceText.Error(exception);
                SettingsErrorText.Visibility = Visibility.Visible;
            }
        }
    }

    private bool ParametersChanged(GameInstance current)
    {
        var draft = ParametersEditor.Draft;
        return draft.ArgumentSource != current.ArgumentSource || draft.OfflineMode != current.OfflineMode
            || draft.OfflineUsername != current.OfflineUsername || draft.Parameters != current.Parameters;
    }

    public async Task<bool> ConfirmLeaveAsync()
    {
        await FlushSettingsAsync();
        await _saving;
        if (!_deleted && !ParametersEditor.IsValid && Current() is { } current
            && (!ParametersEditor.WindowSizeValid || ParametersChanged(current))
            && !await HakoDialogs.ConfirmLeaveInvalidDraftAsync())
        {
            return false;
        }

        if (_instance is not { } instance || Current() is null || NameBox.Text == instance.Name)
        {
            return true;
        }

        var answer = await HakoDialogs.ConfirmAsync(
            HakoStrings.Get("RenameTitle"),
            HakoStrings.Get("RenameMessage"),
            HakoStrings.Get("RenameConfirm"),
            HakoStrings.Get("CancelButton"),
            HakoStrings.Get("RenameSkip"));
        if (answer == ContentDialogResult.Secondary)
        {
            NameBox.Text = instance.Name;
            return true;
        }

        return answer == ContentDialogResult.Primary && await RenameAsync();
    }

    private async Task<bool> RenameAsync()
    {
        if (_services is not { } services)
        {
            return false;
        }

        var name = NameBox.Text;
        _busyOperation = true;
        UpdateName();
        try
        {
            await Task.Run(() => services.Store.Rename(_id, name));
            if (Current() is { } renamed)
            {
                NameBox.Text = renamed.Name;
            }

            return true;
        }
        catch (Exception exception)
        {
            await HakoDialogs.ShowErrorAsync(InstanceText.Error(exception));
            return false;
        }
        finally
        {
            _busyOperation = false;
            Refresh();
        }
    }

    private async void SaveNameButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = await HakoDialogs.ConfirmAsync(
            HakoStrings.Get("RenameTitle"),
            HakoStrings.Get("RenameMessage"),
            HakoStrings.Get("RenameConfirm"),
            HakoStrings.Get("CancelButton"));
        if (answer == ContentDialogResult.Primary)
        {
            await RenameAsync();
        }
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateName();

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is { } services)
        {
            InstancePlayLogic.Launch(services, _id);
        }
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e) => InstancePlayLogic.OpenLog(OpenLogButton.Tag as string);

    private void RetrySessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is { } services)
        {
            RetrySessionButton.IsEnabled = false;
            InstancePlayLogic.RetrySession(services);
        }
    }

    private async void InstallationActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is not { } services || Current() is not { } instance)
        {
            return;
        }

        InstallationActionButton.IsEnabled = false;
        try
        {
            await (instance.State switch
            {
                InstanceInstallationState.Paused => services.Installations.ResumeAsync(_id),
                InstanceInstallationState.Failed => services.Installations.RetryAsync(_id),
                _ => services.Installations.PauseAsync(_id)
            });
        }
        catch (Exception exception)
        {
            await HakoDialogs.ShowErrorAsync(InstanceText.Error(exception));
        }
        finally
        {
            Refresh();
        }
    }

    private void ActionsMenu_Opening(object sender, object e)
    {
        var blocked = _instance is { } instance && _services is { } services
            ? services.Store.ManagementBlockedError(instance)
            : null;
        DeleteMenuItem.IsEnabled = blocked is null && !_busyOperation;
        ToolTipService.SetToolTip(DeleteMenuItem, blocked is { } error ? HakoStrings.Get(error.ResourceKey()) : null);
    }

    private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_services is not { } services || Current() is not { } instance)
        {
            return;
        }

        var answer = await HakoDialogs.ConfirmAsync(
            string.Format(HakoStrings.Get("DeleteBuildTitle"), instance.Name),
            string.Format(HakoStrings.Get("DeleteBuildMessage"), FolderDisplayPath(services, instance)),
            HakoStrings.Get("DeleteBuildConfirm"),
            HakoStrings.Get("CancelButton"),
            destructive: true);
        if (answer != ContentDialogResult.Primary)
        {
            return;
        }

        _busyOperation = true;
        OperationText.Text = HakoStrings.Get("DeletingBuild");
        OperationPanel.Visibility = Visibility.Visible;
        try
        {
            await Task.Run(() => services.Store.Delete(_id));
            _saveTimer?.Stop();
            _pendingSave = null;
            _deleted = true;
            services.App.CloseInstance(_id);
        }
        catch (Exception exception)
        {
            await HakoDialogs.ShowErrorAsync(InstanceText.Error(exception));
        }
        finally
        {
            _busyOperation = false;
            OperationPanel.Visibility = Visibility.Collapsed;
        }
    }
}

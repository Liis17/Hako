using Hako.Core.Updates;
using Microsoft.UI.Xaml;

namespace Hako;

public sealed partial class SettingsPage
{
    private void AppUpdates_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => _ = RefreshAppUpdatesAsync());

    private async Task RefreshAppUpdatesAsync()
    {
        if (_updates is not { } service || _refreshingAppUpdates) return;
        _refreshingAppUpdates = true;
        try
        {
            var updates = service.Coordinator;
            var reason = updates.Phase == AppUpdatePhase.Ready ? await updates.GetBlockReasonAsync() : null;
            if (_updates != service) return;
            AppUpdatesTitle.Text = HakoStrings.Get("AppUpdatesTitle");
            AppVersionText.Text = string.Format(HakoStrings.Get("AppUpdateCurrentVersion"), updates.CurrentVersion.ToString(3));
            AutomaticAppUpdatesBox.Content = HakoStrings.Get("AppUpdateAutomatic");
            AutomaticAppUpdatesBox.IsChecked = service.AutomaticChecks;
            AutomaticAppUpdatesBox.IsEnabled = updates.Supported && !updates.IsBusy;
            CheckAppUpdateButton.Content = HakoStrings.Get("AppUpdateCheck");
            DownloadAppUpdateButton.Content = HakoStrings.Get("AppUpdateDownload");
            InstallAppUpdateButton.Content = HakoStrings.Get("AppUpdateInstall");
            CancelAppUpdateButton.Content = HakoStrings.Get("AppUpdateCancel");
            AppReleasesLink.Content = HakoStrings.Get("AppUpdateReleases");
            AppReleasesLink.NavigateUri = updates.Release is { } release
                ? new Uri(AppReleaseClient.ReleasesPage + "/tag/" + release.Tag) : AppReleaseClient.ReleasesPage;
            CheckAppUpdateButton.IsEnabled = updates.Supported && !updates.IsBusy && updates.Downloaded is null;
            DownloadAppUpdateButton.Visibility = updates.Phase == AppUpdatePhase.Available ? Visibility.Visible : Visibility.Collapsed;
            InstallAppUpdateButton.Visibility = updates.Phase == AppUpdatePhase.Ready ? Visibility.Visible : Visibility.Collapsed;
            InstallAppUpdateButton.IsEnabled = !updates.IsBusy && reason is null;
            CancelAppUpdateButton.Visibility = updates.Phase == AppUpdatePhase.Downloading ? Visibility.Visible : Visibility.Collapsed;
            AppUpdateProgress.Visibility = updates.Phase is AppUpdatePhase.Downloading or AppUpdatePhase.Installing or AppUpdatePhase.Checking
                ? Visibility.Visible : Visibility.Collapsed;
            AppUpdateProgress.IsIndeterminate = updates.Phase is AppUpdatePhase.Installing or AppUpdatePhase.Checking;
            AppUpdateProgress.Value = updates.Progress;
            var error = updates.ErrorResourceKey is "AppUpdateWaitGames" or "AppUpdateWaitOperations" ? reason : updates.ErrorResourceKey;
            var key = !updates.Supported ? "AppUpdateUnsupported" : error ?? updates.Phase switch
            {
                AppUpdatePhase.Checking => "AppUpdateChecking",
                AppUpdatePhase.Downloading => "AppUpdateDownloading",
                AppUpdatePhase.Installing => "AppUpdateInstalling",
                AppUpdatePhase.Ready => reason ?? "AppUpdateReady",
                AppUpdatePhase.Available => "AppUpdateAvailable",
                _ => updates.HasChecked ? "AppUpdateUpToDate" : "AppUpdateNotChecked"
            };
            AppUpdateStatus.Text = key == "AppUpdateAvailable"
                ? string.Format(HakoStrings.Get(key), updates.Release!.GetVersion()!.ToString(3))
                : key == "AppUpdateDownloading" ? string.Format(HakoStrings.Get(key), (int)updates.Progress)
                : HakoStrings.Get(key);
        }
        catch (Exception) { AppUpdateStatus.Text = HakoStrings.Get("AppUpdateFailed"); }
        finally { _refreshingAppUpdates = false; }
    }

    private async void AutomaticAppUpdatesBox_Click(object sender, RoutedEventArgs e)
    {
        try { if (_updates is { } updates) updates.AutomaticChecks = AutomaticAppUpdatesBox.IsChecked == true; }
        catch (Exception) { await HakoDialogs.ShowErrorAsync(HakoStrings.Get("AppUpdateFailed")); }
        await RefreshAppUpdatesAsync();
    }

    private async void CheckAppUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updates is { } service) await service.Coordinator.CheckAsync();
    }

    private async void DownloadAppUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updates is not { } service) return;
        string? initialBlock;
        try { initialBlock = await service.Coordinator.GetBlockReasonAsync(); }
        catch (Exception) { await RefreshAppUpdatesAsync(); return; }
        await service.Coordinator.DownloadAsync();
        if (service.Coordinator.Phase == AppUpdatePhase.Ready && initialBlock is null
            && _updates == service && _screenVisible && _selectedSection == Hako.Core.SettingsSection.General)
        {
            try
            {
                // The original click authorizes immediate installation only while the launcher remained idle.
                if (await service.Coordinator.GetBlockReasonAsync() is null) await service.Coordinator.InstallAsync();
            }
            catch (Exception) { await RefreshAppUpdatesAsync(); }
        }
    }

    private async void InstallAppUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updates is { } service) await service.Coordinator.InstallAsync();
    }

    private void CancelAppUpdateButton_Click(object sender, RoutedEventArgs e) => _updates?.Coordinator.CancelDownload();
}

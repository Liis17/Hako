using System.Collections.Generic;
using System.Linq;
using Hako.Core.Instances;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hako;

internal sealed record RestoreNavigation(Guid InstanceId, InstanceProfileSection Section);

internal static class BackupRestoreDialog
{
    public static async Task<RestoreNavigation?> RestoreAsync(LauncherServices services, string path)
    {
        if (!services.Store.IsLoaded || services.Store.LoadError is not null)
        {
            await HakoDialogs.ShowErrorAsync(HakoStrings.Get("RestoreRegistryUnavailable"));
            return null;
        }

        var progressRing = new ProgressRing { IsActive = true, Width = 24, Height = 24 };
        var progressText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var progressContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        progressContent.Children.Add(progressRing);
        progressContent.Children.Add(progressText);
        var progress = new ContentDialog
        {
            Content = progressContent,
            CloseButtonText = HakoStrings.Get("RestoreCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        void RefreshProgress(ContentDialog dialog)
        {
            dialog.Title = HakoStrings.Get("RestoreTitle");
            dialog.CloseButtonText = HakoStrings.Get("RestoreCancel");
            progressText.Text = HakoStrings.Get("RestoreChecking");
        }

        RestorePreparationResult? preparation;
        bool cancelled;
        try
        {
            (cancelled, preparation) = await HakoDialogs.ShowProgressAsync(
                progress,
                cancellationToken => services.Restores.PrepareAsync(path, cancellationToken),
                RefreshProgress);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                          or ArgumentException or InvalidOperationException)
        {
            await ShowFailureAsync(new RestoreArchiveFailure(RestoreArchiveFailureReason.OperationFailed, ""));
            return null;
        }

        if (cancelled && preparation?.Staged is { } cancelledStage)
        {
            await services.Restores.DiscardAsync(cancelledStage);
            return null;
        }

        if (preparation is null || !preparation.Succeeded)
        {
            if (!cancelled && preparation?.Failure?.Reason is not (null or RestoreArchiveFailureReason.Cancelled))
            {
                await ShowFailureAsync(preparation.Failure);
            }

            return null;
        }

        var staged = preparation.Staged!;
        try
        {
            return staged.Kind == RestoreArchiveKind.Instance
                ? await RestoreInstanceAsync(services, staged)
                : await RestoreWorldAsync(services, staged);
        }
        finally
        {
            if (!staged.IsCompleted) await services.Restores.DiscardAsync(staged);
        }
    }

    private static async Task<RestoreNavigation?> RestoreInstanceAsync(
        LauncherServices services,
        StagedRestoreArchive staged)
    {
        var manifest = staged.InstanceManifest!;
        var matching = staged.MatchingInstance is { State: InstanceInstallationState.Ready } instance
            ? instance
            : null;
        var details = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var createNew = new RadioButton { GroupName = "InstanceRestoreAction", IsChecked = true };
        var replaceExisting = new RadioButton { GroupName = "InstanceRestoreAction", IsEnabled = matching is not null };
        var target = new ComboBox { IsEnabled = false, MinWidth = 280 };
        if (matching is not null)
        {
            target.Items.Add(new ComboBoxItem
            {
                Content = BuildTargetName(matching),
                Tag = matching
            });
            target.SelectedIndex = 0;
        }
        else
        {
            target.Items.Add(new ComboBoxItem { Content = HakoStrings.Get("RestoreNoMatchingBuild") });
        }

        var name = new TextBox { Text = manifest.Name, MinWidth = 280 };
        var layout = new StackPanel { Spacing = 10, MaxWidth = 560 };
        layout.Children.Add(details);
        layout.Children.Add(createNew);
        layout.Children.Add(replaceExisting);
        layout.Children.Add(target);
        layout.Children.Add(name);
        if (staged.NeedsJavaInstallation)
        {
            layout.Children.Add(new TextBlock
            {
                Text = HakoStrings.Get("RestoreJavaRequired"),
                TextWrapping = TextWrapping.Wrap
            });
        }

        void UpdateAction()
        {
            var replacing = replaceExisting.IsChecked == true;
            target.IsEnabled = replacing && matching is not null;
            name.IsEnabled = !replacing;
        }

        createNew.Checked += (_, _) => UpdateAction();
        replaceExisting.Checked += (_, _) => UpdateAction();
        var dialog = new ContentDialog
        {
            Content = layout,
            PrimaryButtonText = HakoStrings.Get("RestoreApply"),
            CloseButtonText = HakoStrings.Get("RestoreCancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        void Refresh(ContentDialog current)
        {
            current.Title = HakoStrings.Get("RestoreBuildTitle");
            current.PrimaryButtonText = HakoStrings.Get("RestoreApply");
            current.CloseButtonText = HakoStrings.Get("RestoreCancel");
            details.Text = string.Format(HakoStrings.Get("RestoreBuildDetails"), manifest.Name,
                manifest.MinecraftVersion, LoaderName(manifest.ModLoader, manifest.LoaderVersion), manifest.ModCount);
            createNew.Content = HakoStrings.Get("RestoreCreateNew");
            replaceExisting.Content = HakoStrings.Get("RestoreReplaceBuild");
            name.Header = HakoStrings.Get("RestoreBuildName");
        }
        var choice = await HakoDialogs.ShowAsync(dialog, Refresh);
        if (choice != ContentDialogResult.Primary) return null;

        var replacing = replaceExisting.IsChecked == true && matching is not null;
        if (replacing)
        {
            var confirmation = await HakoDialogs.ConfirmAsync(
                HakoStrings.Get("RestoreReplaceBuildTitle"),
                string.Format(HakoStrings.Get("RestoreReplaceBuildMessage"), matching!.Name),
                HakoStrings.Get("RestoreReplace"), HakoStrings.Get("RestoreCancel"), destructive: true);
            if (confirmation != ContentDialogResult.Primary) return null;
        }

        if (staged.NeedsJavaInstallation)
        {
            var confirmation = await HakoDialogs.ConfirmAsync(
                HakoStrings.Get("RestoreJavaTitle"), HakoStrings.Get("RestoreJavaConfirmation"),
                HakoStrings.Get("RestoreContinue"), HakoStrings.Get("RestoreCancel"));
            if (confirmation != ContentDialogResult.Primary) return null;
        }

        var restore = await services.Restores.CommitInstanceAsync(staged,
            replacing ? matching!.Name : name.Text,
            replacing ? matching!.Id : null);
        if (!restore.Succeeded)
        {
            await ShowFailureAsync(restore.Failure!);
            return null;
        }

        var restored = restore.Instance!;
        if (restored.State == InstanceInstallationState.Queued)
        {
            try
            {
                await services.Installations.EnqueueAsync(restored.Id);
            }
            catch (InvalidOperationException)
            {
                await HakoDialogs.ShowErrorAsync(HakoStrings.Get("RestoreQueueStartFailed"));
            }
        }

        if (restore.CleanupWarning) await ShowCleanupWarningAsync();
        return new RestoreNavigation(restored.Id, InstanceProfileSection.Settings);
    }

    private static async Task<RestoreNavigation?> RestoreWorldAsync(
        LauncherServices services,
        StagedRestoreArchive staged)
    {
        var manifest = staged.WorldManifest!;
        var targets = services.Store.Instances
            .Where(instance => instance.State == InstanceInstallationState.Ready)
            .OrderBy(instance => instance.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (targets.Length == 0)
        {
            await HakoDialogs.ShowErrorAsync(HakoStrings.Get("RestoreNoReadyBuild"));
            return null;
        }

        var details = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var target = new ComboBox { MinWidth = 300 };
        foreach (var instance in targets)
        {
            target.Items.Add(new ComboBoxItem { Content = BuildTargetName(instance), Tag = instance });
        }

        var preferred = staged.MatchingInstance is { State: InstanceInstallationState.Ready } match
            ? Array.FindIndex(targets, instance => instance.Id == match.Id)
            : 0;
        target.SelectedIndex = Math.Max(preferred, 0);
        var name = new TextBox { Text = manifest.Name, MinWidth = 300 };
        var folderName = new TextBox { Text = manifest.FolderName, MinWidth = 300 };
        var copyWorld = new RadioButton { GroupName = "WorldRestoreAction", IsChecked = true };
        var replaceWorld = new RadioButton { GroupName = "WorldRestoreAction", IsEnabled = false };
        IReadOnlyList<InstanceWorld> existingWorlds = [];
        InstanceWorld? selectedExistingWorld = null;
        var worldLoadGeneration = 0;
        ContentDialog? dialog = null;
        var layout = new StackPanel { Spacing = 10, MaxWidth = 600 };
        layout.Children.Add(details);
        layout.Children.Add(target);
        layout.Children.Add(name);
        layout.Children.Add(folderName);
        layout.Children.Add(copyWorld);
        layout.Children.Add(replaceWorld);

        void RefreshWorldChoice()
        {
            selectedExistingWorld = existingWorlds.FirstOrDefault(world =>
                string.Equals(world.Id, folderName.Text, StringComparison.OrdinalIgnoreCase));
            replaceWorld.IsEnabled = selectedExistingWorld is not null;
            if (selectedExistingWorld is null && replaceWorld.IsChecked == true) copyWorld.IsChecked = true;
            replaceWorld.Content = selectedExistingWorld is null
                ? HakoStrings.Get("RestoreReplaceWorld")
                : string.Format(HakoStrings.Get("RestoreReplaceWorldWith"), selectedExistingWorld.Name);
        }

        async Task LoadWorldsAsync(GameInstance instance)
        {
            var generation = ++worldLoadGeneration;
            if (dialog is not null) dialog.IsPrimaryButtonEnabled = false;
            existingWorlds = [];
            if (replaceWorld.IsChecked == true) copyWorld.IsChecked = true;
            RefreshWorldChoice();
            try
            {
                var worlds = await services.Worlds.ListAsync(instance.Id);
                if (generation != worldLoadGeneration) return;
                existingWorlds = worlds;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (generation != worldLoadGeneration) return;
                existingWorlds = [];
            }

            RefreshWorldChoice();
            if (generation == worldLoadGeneration && dialog is not null) dialog.IsPrimaryButtonEnabled = true;
        }

        await LoadWorldsAsync(targets[target.SelectedIndex]);
        target.SelectionChanged += async (_, _) =>
        {
            if (target.SelectedItem is ComboBoxItem { Tag: GameInstance instance })
            {
                await LoadWorldsAsync(instance);
            }
        };
        folderName.TextChanged += (_, _) => RefreshWorldChoice();

        dialog = new ContentDialog
        {
            Content = layout,
            PrimaryButtonText = HakoStrings.Get("RestoreApply"),
            CloseButtonText = HakoStrings.Get("RestoreCancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        void Refresh(ContentDialog current)
        {
            current.Title = HakoStrings.Get("RestoreWorldTitle");
            current.PrimaryButtonText = HakoStrings.Get("RestoreApply");
            current.CloseButtonText = HakoStrings.Get("RestoreCancel");
            details.Text = string.Format(HakoStrings.Get("RestoreWorldDetails"), manifest.Name,
                manifest.SaveVersion ?? HakoStrings.Get("RestoreUnknownVersion"),
                HakoStrings.Get(WorldResourceKeys.GameType(manifest.GameType)),
                manifest.SourceInstance.MinecraftVersion,
                LoaderName(manifest.SourceInstance.ModLoader, manifest.SourceInstance.LoaderVersion));
            target.Header = HakoStrings.Get("RestoreWorldBuild");
            name.Header = HakoStrings.Get("RestoreWorldName");
            folderName.Header = HakoStrings.Get("RestoreWorldFolder");
            copyWorld.Content = HakoStrings.Get("RestoreCopyWorld");
            RefreshWorldChoice();
        }
        var choice = await HakoDialogs.ShowAsync(dialog, Refresh);
        if (choice != ContentDialogResult.Primary
            || target.SelectedItem is not ComboBoxItem { Tag: GameInstance selectedTarget }) return null;

        var source = manifest.SourceInstance;
        var targetLoader = selectedTarget.ModLoader == InstanceModLoader.Fabric ? "fabric" : "vanilla";
        var sourceDiffers = !string.Equals(selectedTarget.VersionID, source.MinecraftVersion, StringComparison.Ordinal)
            || !string.Equals(targetLoader, source.ModLoader, StringComparison.Ordinal)
            || targetLoader == "fabric" && !string.Equals(selectedTarget.FabricConfiguration?.LoaderVersion,
                source.LoaderVersion, StringComparison.Ordinal);
        if (sourceDiffers)
        {
            var confirmation = await HakoDialogs.ConfirmAsync(
                HakoStrings.Get("RestoreWorldBuildMismatchTitle"),
                string.Format(HakoStrings.Get("RestoreWorldBuildMismatchMessage"), BuildTargetName(selectedTarget)),
                HakoStrings.Get("RestoreContinue"), HakoStrings.Get("RestoreCancel"));
            if (confirmation != ContentDialogResult.Primary) return null;
        }

        var replacing = replaceWorld.IsChecked == true && selectedExistingWorld is not null;
        var destinationFolder = replacing ? selectedExistingWorld!.Id : folderName.Text;
        if (replacing)
        {
            var confirmation = await HakoDialogs.ConfirmAsync(
                HakoStrings.Get("RestoreReplaceWorldTitle"),
                string.Format(HakoStrings.Get("RestoreReplaceWorldMessage"), selectedExistingWorld!.Name),
                HakoStrings.Get("RestoreReplace"), HakoStrings.Get("RestoreCancel"), destructive: true);
            if (confirmation != ContentDialogResult.Primary) return null;
        }

        var restore = await services.Restores.CommitWorldAsync(staged, selectedTarget.Id,
            name.Text, destinationFolder, replacing);
        if (!restore.Succeeded)
        {
            await ShowFailureAsync(restore.Failure!);
            return null;
        }

        if (restore.CleanupWarning) await ShowCleanupWarningAsync();
        return new RestoreNavigation(selectedTarget.Id, InstanceProfileSection.Worlds);
    }

    private static string BuildTargetName(GameInstance instance) =>
        $"{instance.Name} · Minecraft {instance.VersionID} · {LoaderName(instance.ModLoader == InstanceModLoader.Fabric ? "fabric" : "vanilla", instance.FabricConfiguration?.LoaderVersion)}";

    private static string LoaderName(string loader, string? version) => loader == "fabric"
        ? $"{HakoStrings.Get("RestoreFabric")} {version}"
        : HakoStrings.Get("RestoreVanilla");

    private static Task ShowFailureAsync(RestoreArchiveFailure failure)
    {
        var message = HakoStrings.Get(RestoreResourceKeys.Key(failure.Reason));
        if (failure.Reason == RestoreArchiveFailureReason.RollbackFailed && !string.IsNullOrWhiteSpace(failure.Diagnostic))
        {
            message += "\n" + string.Format(HakoStrings.Get("RestoreRecoveryDetails"), failure.Diagnostic);
        }

        return HakoDialogs.ShowErrorAsync(message);
    }

    private static Task ShowCleanupWarningAsync() => HakoDialogs.ShowAsync(new ContentDialog
    {
        Title = HakoStrings.Get("RestoreCleanupWarningTitle"),
        Content = new TextBlock { Text = HakoStrings.Get("RestoreCleanupWarning"), TextWrapping = TextWrapping.Wrap },
        CloseButtonText = HakoStrings.Get("CloseButton"),
        DefaultButton = ContentDialogButton.Close
    });
}

internal static class RestoreResourceKeys
{
    public static string Key(RestoreArchiveFailureReason reason) => reason switch
    {
        RestoreArchiveFailureReason.UnsupportedFileType => "RestoreFailureUnsupportedFileType",
        RestoreArchiveFailureReason.FileUnavailable => "RestoreFailureFileUnavailable",
        RestoreArchiveFailureReason.InvalidArchive => "RestoreFailureInvalidArchive",
        RestoreArchiveFailureReason.UnsafePath => "RestoreFailureUnsafePath",
        RestoreArchiveFailureReason.InvalidManifest => "RestoreFailureInvalidManifest",
        RestoreArchiveFailureReason.InvalidInstance => "RestoreFailureInvalidBuild",
        RestoreArchiveFailureReason.InvalidWorld => "RestoreFailureInvalidWorld",
        RestoreArchiveFailureReason.InvalidName => "RestoreFailureInvalidName",
        RestoreArchiveFailureReason.InsufficientSpace => "RestoreFailureInsufficientSpace",
        RestoreArchiveFailureReason.RegistryUnavailable => "RestoreFailureRegistryUnavailable",
        RestoreArchiveFailureReason.BuildUnavailable => "RestoreFailureBuildUnavailable",
        RestoreArchiveFailureReason.BuildBusy => "RestoreFailureBuildBusy",
        RestoreArchiveFailureReason.DestinationConflict => "RestoreFailureDestinationConflict",
        RestoreArchiveFailureReason.RollbackFailed => "RestoreFailureRollbackFailed",
        RestoreArchiveFailureReason.Cancelled => "RestoreFailureCancelled",
        RestoreArchiveFailureReason.OperationFailed => "RestoreFailureOperationFailed",
        _ => "RestoreFailureOperationFailed"
    };
}

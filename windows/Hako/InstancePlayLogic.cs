using Hako.Core;
using Hako.Core.Instances;

namespace Hako;

/// <summary>Why «Играть» is unavailable, in the order of GameLaunchCoordinator.disabledReason on macOS.</summary>
internal sealed record PlayAvailability(bool Enabled, string? ReasonKey, bool CanRetrySession = false)
{
    public string? Reason => ReasonKey is null ? null : HakoStrings.Get(ReasonKey);
}

internal static class InstancePlayLogic
{
    public static PlayAvailability Evaluate(LauncherServices services, GameInstance instance)
    {
        if (services.Store.IsContentBusy(instance.Id))
        {
            return new(false, "PlayBlockedFilesBusy");
        }

        if (instance.State != InstanceInstallationState.Ready)
        {
            return new(false, "PlayBlockedNotReady");
        }

        var run = services.Games.GetState(instance.Id);
        if (run?.Phase == GameRunPhase.Preparing)
        {
            return new(false, "PlayPreparing");
        }

        if (run?.Phase == GameRunPhase.Running || services.Store.IsLaunchBusy(instance.Id))
        {
            return new(false, "PlayBlockedRunning");
        }

        if (instance.OfflineMode)
        {
            return OfflineUsername.IsValid(instance.OfflineUsername) ? new(true, null) : new(false, "PlayBlockedOfflineName");
        }

        // The Minecraft token itself is refreshed when the launch starts, so only a missing profile blocks here.
        var account = services.App.Account;
        if (account is null || string.IsNullOrWhiteSpace(account.MinecraftProfileId) || string.IsNullOrWhiteSpace(account.MicrosoftRefreshToken))
        {
            return new(false, "PlayBlockedNoSession");
        }

        var sessions = services.App.MinecraftSessions;
        if (sessions.Status == MinecraftSessionStatus.Failed && sessions.Error is { } error)
        {
            return new(false, error.ResourceKey(), CanRetrySession: true);
        }

        return new(true, null);
    }

    /// <summary>Starts the launch off the UI thread; the result is shown from GameLaunchCoordinator.RunStateChanged.</summary>
    public static void Launch(LauncherServices services, Guid instanceId) => _ = Task.Run(async () =>
    {
        try
        {
            await services.Games.LaunchAsync(instanceId);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Launch failed: {exception.GetType().Name}");
        }
    });

    public static void RetrySession(LauncherServices services) => _ = Task.Run(async () =>
    {
        try
        {
            await services.App.MinecraftSessions.ConnectForLaunchAsync();
        }
        catch (Exception)
        {
            // MinecraftSessions.Status and Error describe the failure.
        }
    });

    public static void OpenLog(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception)
        {
        }
    }
}

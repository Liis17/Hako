using System.Reflection;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Hako.Core.Updates;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.Storage;

namespace Hako;

/// <summary>Package identity, preferences, scheduling and Windows deployment stay outside Hako.Core.</summary>
internal sealed class WindowsAppUpdateService : IDisposable
{
    private const string AutomaticKey = "checkUpdatesAutomatically";
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly DispatcherQueue _dispatcher;
    private readonly LauncherServices _services;
    private readonly DispatcherQueueTimer _timer;
    private bool _started;
    public AppUpdateCoordinator Coordinator { get; }

    internal WindowsAppUpdateService(LauncherServices services, DispatcherQueue dispatcher)
    {
        _services = services;
        _dispatcher = dispatcher;
        var identity = new AppPackageIdentity("", "", Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0), "");
        var supported = false;
        try
        {
            var package = Package.Current;
            var id = package.Id;
            var version = id.Version;
            identity = new AppPackageIdentity(id.Name, id.Publisher,
                new Version(version.Major, version.Minor, version.Build, version.Revision), id.Architecture.ToString().ToLowerInvariant());
            supported = !package.IsDevelopmentMode && identity.Architecture is "x64" or "arm64"
                && services.PreviewOptions is null && App.LaunchPaths is { Isolated: false };
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException) { }
        Coordinator = new AppUpdateCoordinator(new AppReleaseClient(_http),
            new AppUpdateDownload(_http, App.LaunchPaths!.CacheRoot, identity), identity, supported, GetBlockReasonAsync, InstallAsync);
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromHours(6);
        _timer.Tick += (_, _) => { if (AutomaticChecks) _ = Coordinator.CheckAsync(automatic: true); };
    }

    public bool AutomaticChecks
    {
        get => Coordinator.Supported && ApplicationData.Current.LocalSettings.Values[AutomaticKey] is not false;
        set
        {
            if (!Coordinator.Supported) return;
            ApplicationData.Current.LocalSettings.Values[AutomaticKey] = value;
            if (_started && value) _ = Coordinator.CheckAsync(automatic: true);
        }
    }

    public void Start()
    {
        _started = true;
        if (!Coordinator.Supported) return;
        _ = Task.Run(() => AppUpdateDownload.CleanupStaleDownloads(App.LaunchPaths!.CacheRoot));
        _timer.Start();
        if (AutomaticChecks) _ = Coordinator.CheckAsync(automatic: true);
    }

    private async Task<string?> GetBlockReasonAsync(CancellationToken cancellationToken)
    {
        if (_services.Games.States.Values.Any(state => state.Phase is Hako.Core.Instances.GameRunPhase.Running or Hako.Core.Instances.GameRunPhase.Preparing)
            || await _services.Playtime.HasActiveProcessesAsync(cancellationToken).ConfigureAwait(false))
            return "AppUpdateWaitGames";
        if (_services.Store.HasAppUpdateBlockers || _services.Installations.ActiveInstanceId is not null || App.HasPendingRestore)
            return "AppUpdateWaitOperations";
        return null;
    }

    private Task InstallAsync(DownloadedAppUpdate update, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try
            {
                if (App.CurrentWindow is not MainWindow window) throw new AppUpdateException("AppUpdateFailed");
                await window.ApplyAppUpdateAsync(update, cancellationToken);
                completion.TrySetResult();
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception exception) { completion.TrySetException(exception); }
        })) completion.TrySetException(new AppUpdateException("AppUpdateFailed"));
        return completion.Task;
    }

    internal static async Task DeployAsync(DownloadedAppUpdate update, CancellationToken cancellationToken)
    {
        // Restart Manager refuses to relaunch a process younger than 60 seconds. Use monotonic process
        // lifetime so updating immediately after launch still gets an automatic restart.
        var restartDelay = TimeSpan.FromSeconds(61) - Stopwatch.GetElapsedTime(Program.StartedAt);
        if (restartDelay > TimeSpan.Zero) await Task.Delay(restartDelay, cancellationToken);
        if (App.HasPendingRestore) throw new AppUpdateException("AppUpdateWaitOperations");
        // Only Restart Manager's update path should relaunch Hako, not crashes or hangs.
        var restartResult = RegisterApplicationRestart(null, 1 | 2 | 8);
        if (restartResult != 0) throw new AppUpdateException("AppUpdateInstallFailed", Marshal.GetExceptionForHR(restartResult));
        try
        {
            var result = await new PackageManager().AddPackageAsync(new Uri(update.PackagePath),
                update.Dependencies.Select(path => new Uri(path)), DeploymentOptions.ForceTargetApplicationShutdown);
            if (result.ExtendedErrorCode is { } error) throw error;
        }
        catch (Exception exception)
        {
            UnregisterApplicationRestart();
            var key = unchecked((uint)exception.HResult) is 0x800B0109 or 0x800B0100 or 0x800B0101 or 0x800B010A
                ? "AppUpdateCertificateError" : "AppUpdateInstallFailed";
            throw new AppUpdateException(key, exception);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        Coordinator.Dispose();
        _http.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, uint flags);
    [DllImport("kernel32.dll")]
    private static extern int UnregisterApplicationRestart();
}

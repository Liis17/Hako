namespace Hako.Core.Updates;

public enum AppUpdatePhase { Idle, Checking, Available, Downloading, Ready, Installing }

/// <summary>One operation at a time. UI consumers marshal Changed to their dispatcher.</summary>
public sealed class AppUpdateCoordinator(
    AppReleaseClient releases, AppUpdateDownload downloads, AppPackageIdentity installed,
    bool supported, Func<CancellationToken, Task<string?>> blockReason,
    Func<DownloadedAppUpdate, CancellationToken, Task> install) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _downloadCancellation;
    private int _operation;
    private bool _disposed;
    public event EventHandler? Changed;
    public AppUpdatePhase Phase { get; private set; }
    public AppRelease? Release { get; private set; }
    public DownloadedAppUpdate? Downloaded { get; private set; }
    public string? ErrorResourceKey { get; private set; }
    public double Progress { get; private set; }
    public bool Supported => supported;
    public Version CurrentVersion => installed.Version;
    public bool IsBusy => Volatile.Read(ref _operation) != 0;

    public async Task CheckAsync(bool automatic = false)
    {
        if (!supported || _disposed || Interlocked.CompareExchange(ref _operation, 1, 0) != 0) return;
        if (Downloaded is not null) { Interlocked.Exchange(ref _operation, 0); return; }
        ErrorResourceKey = null;
        Phase = AppUpdatePhase.Checking;
        Notify();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(1));
            var release = await releases.LatestAsync(installed.Architecture, timeout.Token).ConfigureAwait(false);
            Release = release?.GetVersion() > installed.Version ? release : null;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!automatic) ErrorResourceKey = ErrorKey(exception);
        }
        finally
        {
            Phase = Release is null ? AppUpdatePhase.Idle : AppUpdatePhase.Available;
            Interlocked.Exchange(ref _operation, 0);
            Notify();
        }
    }

    public async Task DownloadAsync()
    {
        if (!supported || _disposed || Release is null || Downloaded is not null
            || Interlocked.CompareExchange(ref _operation, 1, 0) != 0) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromMinutes(30));
        _downloadCancellation = cancellation;
        Phase = AppUpdatePhase.Downloading;
        ErrorResourceKey = null;
        Progress = 0;
        Notify();
        try
        {
            Downloaded = await downloads.DownloadAsync(Release, new DownloadProgress(this), cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) { ErrorResourceKey = ErrorKey(exception); }
        finally
        {
            _downloadCancellation = null;
            Phase = Downloaded is null ? AppUpdatePhase.Available : AppUpdatePhase.Ready;
            Interlocked.Exchange(ref _operation, 0);
            Notify();
        }
    }

    public void CancelDownload()
    {
        try { _downloadCancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public Task<string?> GetBlockReasonAsync(CancellationToken cancellationToken = default) => blockReason(cancellationToken);

    public async Task InstallAsync()
    {
        if (!supported || _disposed || Downloaded is null || Interlocked.CompareExchange(ref _operation, 1, 0) != 0) return;
        ErrorResourceKey = null;
        Phase = AppUpdatePhase.Installing;
        Notify();
        try
        {
            var reason = await blockReason(_lifetime.Token).ConfigureAwait(false);
            if (reason is not null) throw new AppUpdateException(reason);
            await downloads.VerifyAsync(Downloaded, _lifetime.Token).ConfigureAwait(false);
            await install(Downloaded, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ErrorResourceKey = ErrorKey(exception);
            if (exception is AppUpdateException { ResourceKey: "AppUpdateInvalidPackage" })
            {
                AppUpdateDownload.Delete(Downloaded);
                Downloaded = null;
            }
        }
        finally
        {
            Phase = Downloaded is null ? AppUpdatePhase.Available : AppUpdatePhase.Ready;
            Interlocked.Exchange(ref _operation, 0);
            Notify();
        }
    }

    private static string ErrorKey(Exception exception) => exception is AppUpdateException update
        ? update.ResourceKey : "AppUpdateFailed";

    private void Notify() { if (!_disposed) Changed?.Invoke(this, EventArgs.Empty); }

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        // A completed operation's files belong only to this coordinator. A running download removes its own partial files.
        if (!IsBusy && Downloaded is not null) AppUpdateDownload.Delete(Downloaded);
    }

    private sealed class DownloadProgress(AppUpdateCoordinator owner) : IProgress<double>
    {
        private int _lastPercent = -1;
        public void Report(double value)
        {
            if ((int)value == _lastPercent) return;
            _lastPercent = (int)value;
            owner.Progress = value;
            owner.Notify();
        }
    }
}

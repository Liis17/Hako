using System.Collections.ObjectModel;
using System.ComponentModel;
using Hako.Core.Minecraft;

namespace Hako.Core.Instances;

/// <summary>
/// App-lifetime FIFO installer. PropertyChanged is raised on the calling thread, including worker
/// threads; UI subscribers must marshal updates to their DispatcherQueue. Handlers can run while an
/// internal lock is held and must not block or synchronously wait for the UI thread.
/// </summary>
public sealed class InstallationCoordinator : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly InstanceStore _store;
    private readonly Func<MojangVersionEntry, GameArchitecture?, CancellationToken, Task<PreparedInstallation>> _prepare;
    private readonly Func<PreparedInstallation, string, FabricConfiguration?, IProgress<InstallationProgress>?, CancellationToken, Task<InstallationResult>> _installFabric;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _sync = new();
    private readonly object _disposeSync = new();
    private readonly Dictionary<Guid, PreparedInstallation> _prepared = [];
    private readonly Dictionary<Guid, InstallationProgress> _progress = [];
    private Guid? _activeInstanceId;
    private CancellationTokenSource? _activeCancellation;
    private Task? _activeTask;
    private string? _queueError;
    private long _lastProgressTimestamp;
    private bool _hasProgressTimestamp;
    private int _pendingCreates;
    private bool _started;
    private bool _pumping;
    private bool _disposing;
    private bool _disposed;
    private Task? _disposeTask;

    public InstallationCoordinator(InstanceStore store, MojangClient client, MinecraftInstaller installer)
        : this(
            store,
            (version, pinned, cancellationToken) => pinned is { } architecture
                ? client.PrepareAsync(version, architecture, cancellationToken)
                : client.PrepareAsync(version, cancellationToken),
            (prepared, root, progress, cancellationToken) => installer.InstallAsync(prepared, root, progress, cancellationToken),
            installFabric: (prepared, root, fabric, progress, cancellationToken) =>
                installer.InstallFabricAsync(prepared, root, fabric, progress, cancellationToken))
    {
    }

    /// <summary>Uses custom preparation and installation steps (tests and the offline UI preview).</summary>
    public InstallationCoordinator(
        InstanceStore store,
        Func<MojangVersionEntry, GameArchitecture?, CancellationToken, Task<PreparedInstallation>> prepare,
        Func<PreparedInstallation, string, IProgress<InstallationProgress>?, CancellationToken, Task<InstallationResult>> install,
        TimeProvider? timeProvider = null,
        Func<PreparedInstallation, string, FabricConfiguration?, IProgress<InstallationProgress>?, CancellationToken, Task<InstallationResult>>? installFabric = null)
    {
        _store = store;
        _prepare = prepare;
        _installFabric = installFabric ?? ((prepared, root, _, progress, cancellationToken) =>
            install(prepared, root, progress, cancellationToken));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _store.BusyStateChanged += OnBusyStateChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        Task? activeTask;
        CancellationTokenSource? cancellation;
        try
        {
            lock (_sync)
            {
                _disposing = true;
                _disposed = true;
                activeTask = _activeTask;
                cancellation = _activeCancellation;
            }
        }
        finally
        {
            _startGate.Release();
        }

        _store.BusyStateChanged -= OnBusyStateChanged;
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The active installation may have completed after its task was captured.
        }
        finally
        {
            if (activeTask is not null)
            {
                await activeTask.ConfigureAwait(false);
            }
        }
    }

    public IReadOnlyDictionary<Guid, InstallationProgress> Progress
    {
        get
        {
            lock (_sync)
            {
                return new ReadOnlyDictionary<Guid, InstallationProgress>(new Dictionary<Guid, InstallationProgress>(_progress));
            }
        }
    }

    public Guid? ActiveInstanceId
    {
        get { lock (_sync) return _activeInstanceId; }
    }

    public string? QueueError
    {
        get { lock (_sync) return _queueError; }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(InstallationCoordinator));
                }

                if (_started)
                {
                    return;
                }
            }

            if (!_store.IsLoaded)
            {
                await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_store.LoadError is { } loadError)
            {
                SetQueueError(loadError.Message);
                return;
            }

            await Task.Run(() =>
            {
                lock (_sync)
                {
                    if (_started)
                    {
                        return;
                    }

                    foreach (var instance in _store.Instances)
                    {
                        if (instance.PauseRequested && instance.State != InstanceInstallationState.Paused)
                        {
                            _store.SetInstallationStatus(instance.Id, InstanceInstallationState.Paused, pauseRequested: true);
                        }
                        else if (!instance.PauseRequested && instance.State == InstanceInstallationState.Installing)
                        {
                            _store.SetInstallationStatus(instance.Id, InstanceInstallationState.Queued, pauseRequested: false);
                        }
                    }

                    _started = true;
                    _queueError = null;
                    NotifyPropertyChanged(nameof(QueueError));
                    PumpLocked();
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Queues a build created in the queued state. Pass its verified preparation on first install.</summary>
    public Task EnqueueAsync(Guid id, PreparedInstallation? prepared = null) => Task.Run(() =>
    {
        lock (_sync)
        {
            EnqueueLocked(id, prepared);
        }
    });

    /// <summary>Creates and queues a build without allowing the pump to start it before its verified preparation is registered.</summary>
    public Task<GameInstance> CreateAndEnqueueAsync(InstanceDraft draft, PreparedInstallation prepared, bool legacyTexturepacks = false) => Task.Run(() =>
    {
        lock (_sync)
        {
            EnsureStarted();
            _pendingCreates++;
        }

        var pending = true;
        try
        {
            var instance = _store.Create(draft, prepared, legacyTexturepacks);
            lock (_sync)
            {
                _pendingCreates--;
                pending = false;
                var current = FindInstance(instance.Id);
                if (current.State == InstanceInstallationState.Paused && current.PauseRequested)
                {
                    // Resume re-prepares this build using the architecture already pinned at creation.
                    _prepared.Remove(instance.Id);
                    PumpLocked();
                }
                else
                {
                    EnqueueLocked(instance.Id, prepared);
                }

                return FindInstance(instance.Id);
            }
        }
        catch
        {
            lock (_sync)
            {
                if (pending)
                {
                    _pendingCreates--;
                }
                PumpLocked();
            }

            throw;
        }
    });

    public Task PauseAsync(Guid id) => Task.Run(() =>
    {
        CancellationTokenSource? cancellation = null;
        lock (_sync)
        {
            EnsureStarted();
            var instance = FindInstance(id);
            if (instance.State is not (InstanceInstallationState.Queued or InstanceInstallationState.Installing))
            {
                throw new InvalidOperationException("Only a queued or installing build can be paused.");
            }

            var active = _activeTask is not null
                && _activeInstanceId == id
                && instance.State == InstanceInstallationState.Installing;
            _store.SetInstallationStatus(
                id,
                active ? InstanceInstallationState.Installing : InstanceInstallationState.Paused,
                pauseRequested: true);

            if (active)
            {
                SetProgress(id, new InstallationProgress(InstallationProgressStage.Pausing));
                cancellation = _activeCancellation;
            }
            else
            {
                _prepared.Remove(id);
                PumpLocked();
            }
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The installation may have completed after pauseRequested was persisted.
        }
    });

    public Task ResumeAsync(Guid id) => Task.Run(() =>
    {
        lock (_sync)
        {
            EnsureStarted();
            var instance = FindInstance(id);
            if (instance.State != InstanceInstallationState.Paused)
            {
                throw new InvalidOperationException("Only a paused build can be resumed.");
            }

            _store.SetInstallationStatus(id, InstanceInstallationState.Queued, pauseRequested: false);
            SetQueueError(null);
            PumpLocked();
        }
    });

    public Task RetryAsync(Guid id) => Task.Run(() =>
    {
        lock (_sync)
        {
            EnsureStarted();
            var instance = FindInstance(id);
            var orphanedInstallation = instance.State == InstanceInstallationState.Installing
                && instance.Id != _activeInstanceId
                && !instance.PauseRequested;
            if (instance.State != InstanceInstallationState.Failed && !orphanedInstallation)
            {
                throw new InvalidOperationException("Only a failed or abandoned installation can be retried.");
            }

            _store.SetInstallationStatus(id, InstanceInstallationState.Queued, pauseRequested: false);
            SetQueueError(null);
            PumpLocked();
        }
    });

    private void EnqueueLocked(Guid id, PreparedInstallation? prepared)
    {
        EnsureStarted();
        var instance = FindInstance(id);
        if (_activeInstanceId == id)
        {
            return;
        }

        if (instance.State != InstanceInstallationState.Queued)
        {
            throw new InvalidOperationException("Only a queued build can be enqueued.");
        }

        if (prepared is not null)
        {
            ValidatePreparedForInstance(instance, prepared);
            _store.PinArchitecture(id, prepared.Architecture);
            _prepared[id] = prepared;
        }

        _store.SetInstallationStatus(id, InstanceInstallationState.Queued, pauseRequested: false);
        SetQueueError(null);
        PumpLocked();
    }

    private void PumpLocked()
    {
        if (_disposed || !_started || _activeTask is not null || _pumping || _pendingCreates > 0)
        {
            return;
        }

        _pumping = true;
        try
        {
            foreach (var instance in _store.Instances
                         .Where(item => item.State == InstanceInstallationState.Queued)
                         .OrderBy(item => item.CreatedAt)
                         .ThenBy(item => item.Id))
            {
                if (instance.PauseRequested)
                {
                    _store.SetInstallationStatus(instance.Id, InstanceInstallationState.Paused, pauseRequested: true);
                    continue;
                }

                if (!_store.TryBeginInstallation(instance.Id, out var lease, out var reason))
                {
                    if (reason is InstanceStorageError.InstanceRegistryNotLoaded or InstanceStorageError.InstanceRegistryUnavailable)
                    {
                        SetQueueError(reason.Value.ToString());
                        return;
                    }

                    continue;
                }

                var cancellation = new CancellationTokenSource();
                try
                {
                    _store.SetInstallationStatus(instance.Id, InstanceInstallationState.Installing, pauseRequested: false);
                    _activeInstanceId = instance.Id;
                    _activeCancellation = cancellation;
                    _prepared.Remove(instance.Id, out var prepared);
                    _activeTask = Task.Run(() => RunInstallationAsync(instance.Id, prepared, lease!, cancellation));
                    SetQueueError(null);
                    NotifyPropertyChanged(nameof(ActiveInstanceId));
                    return;
                }
                catch
                {
                    lease?.Dispose();
                    cancellation.Dispose();
                    throw;
                }
            }
        }
        finally
        {
            _pumping = false;
        }
    }

    private async Task RunInstallationAsync(
        Guid id,
        PreparedInstallation? suppliedPreparation,
        IDisposable? lease,
        CancellationTokenSource cancellation)
    {
        InstallationResult? result = null;
        Exception? installationFailure = null;
        try
        {
            var instance = FindInstance(id);
            var version = new MojangVersionEntry
            {
                Id = instance.VersionID,
                Type = "release",
                Url = instance.MetadataURL,
                Sha1 = instance.MetadataSHA1
            };
            var prepared = suppliedPreparation;
            if (prepared is null)
            {
                SetProgress(id, new InstallationProgress(InstallationProgressStage.PreparingMetadata));
                var pinned = ParsePinnedArchitecture(instance.Architecture);
                prepared = await _prepare(version, pinned, cancellation.Token).ConfigureAwait(false);
            }

            cancellation.Token.ThrowIfCancellationRequested();
            ValidatePreparedForInstance(instance, prepared);
            _store.PinArchitecture(id, prepared.Architecture);

            var progress = new InlineProgress<InstallationProgress>(value => SetProgress(id, value));
            result = await _installFabric(prepared, _store.GetInstanceDirectory(id), instance.FabricConfiguration, progress,
                cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
        }
        catch (Exception exception)
        {
            installationFailure = exception;
        }

        lease?.Dispose();
        lease = null;
        try
        {
            lock (_sync)
            {
                try
                {
                    var instance = _store.Instances.FirstOrDefault(item => item.Id == id);
                    var userPauseRequested = instance?.PauseRequested == true;
                    if (_disposing && !userPauseRequested && installationFailure is not null)
                    {
                        // Keep Installing so the next coordinator can restore the build to the queue.
                    }
                    else if (userPauseRequested || (!_disposing && cancellation.IsCancellationRequested))
                    {
                        _store.SetInstallationStatus(id, InstanceInstallationState.Paused, pauseRequested: true);
                    }
                    else if (installationFailure is not null)
                    {
                        var failure = DescribeFailure(installationFailure);
                        _store.SetInstallationStatus(id, InstanceInstallationState.Failed, pauseRequested: false,
                            failure.Code, failure.Message);
                    }
                    else
                    {
                        _store.CompleteInstallation(id, result!);
                        SetQueueError(null);
                    }
                }
                catch (Exception persistenceError)
                {
                    SetQueueError(persistenceError.Message);
                    try
                    {
                        var failure = DescribeFailure(persistenceError);
                        _store.SetInstallationStatus(id, InstanceInstallationState.Failed, pauseRequested: false,
                            failure.Code, failure.Message);
                    }
                    catch (Exception fallbackError)
                    {
                        SetQueueError(fallbackError.Message);
                    }
                }
                finally
                {
                    RemoveProgress(id);
                    _activeInstanceId = null;
                    _activeCancellation = null;
                    _activeTask = null;
                    cancellation.Dispose();
                    NotifyPropertyChanged(nameof(ActiveInstanceId));
                    try
                    {
                        PumpLocked();
                    }
                    catch (Exception exception)
                    {
                        SetQueueError(exception.Message);
                    }
                }
            }
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private void OnBusyStateChanged(object? sender, EventArgs eventArgs)
    {
        lock (_sync)
        {
            try
            {
                PumpLocked();
            }
            catch (Exception exception)
            {
                SetQueueError(exception.Message);
            }
        }
    }

    private GameInstance FindInstance(Guid id) =>
        _store.Instances.FirstOrDefault(instance => instance.Id == id)
        ?? throw new KeyNotFoundException("The build does not exist.");

    private void EnsureStarted()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(InstallationCoordinator));
        }

        if (!_started)
        {
            throw new InvalidOperationException("StartAsync must complete before using the installation queue.");
        }
    }

    private static GameArchitecture? ParsePinnedArchitecture(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (Enum.TryParse<GameArchitecture>(value, ignoreCase: false, out var architecture)
            && Enum.IsDefined(architecture))
        {
            return architecture;
        }

        throw new MojangException(MojangError.Invalid, "The build's pinned architecture is invalid.");
    }

    private static void ValidatePreparedForInstance(GameInstance instance, PreparedInstallation prepared)
    {
        if (!Enum.IsDefined(prepared.Architecture)
            || !string.Equals(instance.VersionID, prepared.Version.Id, StringComparison.Ordinal)
            || !string.Equals(prepared.Version.Id, prepared.Manifest.Id, StringComparison.Ordinal))
        {
            throw new MojangException(MojangError.Invalid, "The prepared Minecraft version does not match this build.");
        }

        if (instance.Architecture is { } pinned
            && !string.Equals(pinned, prepared.Architecture.ToString(), StringComparison.Ordinal))
        {
            throw new MojangException(MojangError.Invalid, "The prepared architecture does not match this build's pinned architecture.");
        }
    }

    private static (string Code, string Message) DescribeFailure(Exception exception) => exception switch
    {
        MojangException mojang => ($"mojang.{mojang.Error.ToString().ToLowerInvariant()}", mojang.Message),
        InstanceStorageException storage => ($"storage.{storage.Error.ToString().ToLowerInvariant()}", storage.Message),
        OperationCanceledException => ("installation.cancelled", exception.Message),
        _ => ("installation.unexpected", exception.Message)
    };

    private void SetProgress(Guid id, InstallationProgress value)
    {
        var changed = false;
        lock (_sync)
        {
            if (value.Stage != InstallationProgressStage.Pausing
                && (_activeCancellation?.IsCancellationRequested == true
                    || _store.Instances.FirstOrDefault(instance => instance.Id == id)?.PauseRequested == true))
            {
                return;
            }

            if (_progress.TryGetValue(id, out var previous))
            {
                if (previous.Stage == value.Stage
                    && value.Stage != InstallationProgressStage.Complete
                    && Math.Abs(ProgressFraction(value) - ProgressFraction(previous)) < 0.002
                    && _hasProgressTimestamp
                    && _timeProvider.GetElapsedTime(_lastProgressTimestamp) < TimeSpan.FromMilliseconds(150))
                {
                    return;
                }

                changed = previous != value;
            }
            else
            {
                changed = true;
            }

            _progress[id] = value;
            _lastProgressTimestamp = _timeProvider.GetTimestamp();
            _hasProgressTimestamp = true;
        }

        if (changed)
        {
            NotifyPropertyChanged(nameof(Progress));
        }
    }

    private static double ProgressFraction(InstallationProgress value) => value.TotalBytes > 0
        ? (double)value.CompletedBytes / value.TotalBytes
        : value.TotalFiles > 0 ? (double)value.CompletedFiles / value.TotalFiles : 0;

    private void RemoveProgress(Guid id)
    {
        if (_progress.Remove(id))
        {
            NotifyPropertyChanged(nameof(Progress));
        }
    }

    private void SetQueueError(string? value)
    {
        lock (_sync)
        {
            if (_queueError == value)
            {
                return;
            }

            _queueError = value;
            NotifyPropertyChanged(nameof(QueueError));
        }
    }

    private void NotifyPropertyChanged(string propertyName)
    {
        var handlers = PropertyChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (PropertyChangedEventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
            catch (Exception)
            {
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public static class InstallationResourceKeys
{
    public static string ErrorResourceKey(string errorCode) => errorCode switch
    {
        "mojang.http" => "InstallationErrorMojangHttp",
        "mojang.unsupported" => "InstallationErrorMojangUnsupported",
        "mojang.integrity" => "InstallationErrorMojangIntegrity",
        "mojang.invalid" => "InstallationErrorMojangInvalid",
        "storage.operationfailed" => "InstallationErrorStorageOperationFailed",
        "storage.invalidpath" => "InstallationErrorStorageInvalidPath",
        "storage.pathtraversal" => "InstallationErrorStoragePathTraversal",
        "storage.reparsepointnotallowed" => "InstallationErrorStorageReparsePoint",
        "storage.invalidbuildname" => "InstallationErrorStorageInvalidBuildName",
        "storage.reservedbuildname" => "InstallationErrorStorageReservedBuildName",
        "storage.duplicatebuildname" => "InstallationErrorStorageDuplicateBuildName",
        "storage.buildfolderexists" => "InstallationErrorStorageBuildFolderExists",
        "storage.buildinstalling" => "InstallationErrorStorageBuildInstalling",
        "storage.buildrunning" => "InstallationErrorStorageBuildRunning",
        "storage.buildfilesbusy" => "InstallationErrorStorageBuildFilesBusy",
        "storage.instanceregistrynotloaded" => "InstallationErrorStorageRegistryNotLoaded",
        "storage.instanceregistryunavailable" => "InstallationErrorStorageRegistryUnavailable",
        "storage.invalidofflineusername" => "InstallationErrorStorageInvalidOfflineUsername",
        "storage.missingfabricloaderversion" => "InstallationErrorStorageMissingFabricLoaderVersion",
        "installation.cancelled" => "InstallationErrorCancelled",
        "installation.unexpected" => "InstallationErrorUnexpected",
        _ => "InstallationErrorUnexpected"
    };

    public static string ResourceKey(this InstanceStorageError error) =>
        ErrorResourceKey($"storage.{error.ToString().ToLowerInvariant()}");

    public static string ResourceKey(this MojangError error) =>
        ErrorResourceKey($"mojang.{error.ToString().ToLowerInvariant()}");

    /// <summary>Localized text key for an exception from build creation, settings or the Mojang catalog.</summary>
    public static string ErrorResourceKey(Exception exception) => exception switch
    {
        InstanceStorageException storage => storage.Error.ResourceKey(),
        InstanceRegistryException registry => registry.ResourceKey,
        MojangException mojang => mojang.Error.ResourceKey(),
        HttpRequestException or TimeoutException or TaskCanceledException => "InstallationErrorMojangHttp",
        IOException or UnauthorizedAccessException => "InstallationErrorStorageOperationFailed",
        _ => "InstallationErrorUnexpected"
    };

    public static string ResourceKey(this InstanceInstallationState state) => state switch
    {
        InstanceInstallationState.Queued => "InstallationStateQueued",
        InstanceInstallationState.Installing => "InstallationStateInstalling",
        InstanceInstallationState.Paused => "InstallationStatePaused",
        InstanceInstallationState.Ready => "InstallationStateReady",
        InstanceInstallationState.Failed => "InstallationStateFailed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    public static string ResourceKey(this InstallationProgressStage stage) => stage switch
    {
        InstallationProgressStage.PreparingMetadata => "InstallationStagePreparingMetadata",
        InstallationProgressStage.PreparingFiles => "InstallationStagePreparingFiles",
        InstallationProgressStage.Downloading => "InstallationStageDownloading",
        InstallationProgressStage.PreparingLibraries => "InstallationStagePreparingLibraries",
        InstallationProgressStage.PreparingAssets => "InstallationStagePreparingAssets",
        InstallationProgressStage.Complete => "InstallationStageComplete",
        InstallationProgressStage.Pausing => "InstallationStagePausing",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
    };
}

using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Hako.Core;
using Hako.Core.Minecraft;

namespace Hako.Core.Instances;

public enum InstanceRegistryError
{
    Corrupt,
    UnsupportedVersion,
    ReadFailure
}

public sealed class InstanceRegistryException : IOException
{
    public InstanceRegistryException(string message) : this(InstanceRegistryError.Corrupt, message) { }

    public InstanceRegistryException(string message, Exception innerException)
        : this(InstanceRegistryError.Corrupt, message, innerException) { }

    public InstanceRegistryException(InstanceRegistryError error, string message, Exception? innerException = null)
        : base(message, innerException) => Error = error;

    public InstanceRegistryError Error { get; }
    public string ResourceKey => Error switch
    {
        InstanceRegistryError.Corrupt or InstanceRegistryError.UnsupportedVersion => "InstanceRegistryDamaged",
        InstanceRegistryError.ReadFailure => "InstanceRegistryUnavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(Error), Error, null)
    };
}

public sealed record InstanceRegistryFile(int FormatVersion, List<GameInstance> Instances);

public sealed class InstanceStore : INotifyPropertyChanged, IAsyncDisposable
{
    private static readonly HashSet<string> ReservedNames = CreateReservedNames();
    private readonly InstanceStorage _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action? _beforeRegistryCommit;
    private readonly Action? _beforeDeleteStagingCleanup;
    private readonly Action<string, string>? _beforeRestoreMove;
    private readonly Action<string>? _beforeRetiredDelete;
    private readonly object _deleteStagingSync = new();
    private readonly object _deleteStagingTasksSync = new();
    private readonly object _snapshotSync = new();
    private IReadOnlyList<GameInstance> _instances = Array.AsReadOnly(Array.Empty<GameInstance>());
    private InstanceRegistryException? _loadError;
    private Task? _deleteStagingCleanupTask;
    private Task? _disposeTask;
    private bool _isLoaded;
    // Written under _gate, read lock-free so UI-thread checks never wait for a long operation holding _gate.
    private ImmutableHashSet<Guid> _launchBusy = ImmutableHashSet<Guid>.Empty;
    private ImmutableHashSet<Guid> _contentBusy = ImmutableHashSet<Guid>.Empty;

    public InstanceStore(HakoPaths paths, Action? beforeRegistryCommit = null)
        : this(paths, beforeRegistryCommit, null, null, null)
    {
    }

    internal InstanceStore(
        HakoPaths paths,
        Action? beforeRegistryCommit,
        Action? beforeDeleteStagingCleanup,
        Action<string, string>? beforeRestoreMove = null,
        Action<string>? beforeRetiredDelete = null)
    {
        _storage = new InstanceStorage(paths);
        _beforeRegistryCommit = beforeRegistryCommit;
        _beforeDeleteStagingCleanup = beforeDeleteStagingCleanup;
        _beforeRestoreMove = beforeRestoreMove;
        _beforeRetiredDelete = beforeRetiredDelete;
    }

    internal Task? DeleteStagingCleanupTask => Volatile.Read(ref _deleteStagingCleanupTask);

    public ValueTask DisposeAsync()
    {
        lock (_deleteStagingTasksSync)
        {
            _disposeTask ??= _deleteStagingCleanupTask ?? Task.CompletedTask;
            return new ValueTask(_disposeTask);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? InstancesChanged;
    public event EventHandler? BusyStateChanged;

    public IReadOnlyList<GameInstance> Instances
    {
        get
        {
            lock (_snapshotSync)
            {
                return _instances;
            }
        }
    }

    public InstanceRegistryException? LoadError
    {
        get
        {
            lock (_snapshotSync)
            {
                return _loadError;
            }
        }
    }

    public bool IsLoaded
    {
        get
        {
            lock (_snapshotSync)
            {
                return _isLoaded;
            }
        }
    }

    public IReadOnlyList<GameInstance> List() => Instances;

    /// <summary>Absolute folder that contains the build folders.</summary>
    public string GameRoot => _storage.Root;

    public string GetInstanceDirectory(Guid id)
    {
        _gate.Wait();
        try
        {
            return _storage.Directory(Find(GetRegistrySnapshot(), id).FolderName);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool IsLaunchBusy(Guid id) => Volatile.Read(ref _launchBusy).Contains(id);

    public bool IsContentBusy(Guid id) => Volatile.Read(ref _contentBusy).Contains(id);

    public bool TryBeginLaunch(Guid id, out IDisposable? lease, out InstanceStorageError? reason)
    {
        bool changed;
        _gate.Wait();
        try
        {
            if (!TryGetRegistrySnapshot(out var registry, out reason))
            {
                lease = null;
                return false;
            }

            var instance = Find(registry, id);
            reason = GetManagementBlock(instance);
            if (reason is not null)
            {
                lease = null;
                return false;
            }

            changed = AddBusy(ref _launchBusy, id);
            lease = new BusyLease(() => EndBusy(id, launch: true));
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            NotifyBusyStateChanged();
        }

        reason = null;
        return true;
    }

    public bool TryBeginContent(Guid id, out IDisposable? lease, out InstanceStorageError? reason)
    {
        bool changed;
        _gate.Wait();
        try
        {
            if (!TryGetRegistrySnapshot(out var registry, out reason))
            {
                lease = null;
                return false;
            }

            var instance = Find(registry, id);
            reason = GetManagementBlock(instance);
            if (reason is not null)
            {
                lease = null;
                return false;
            }

            changed = AddBusy(ref _contentBusy, id);
            lease = new BusyLease(() => EndBusy(id, launch: false));
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            NotifyBusyStateChanged();
        }

        reason = null;
        return true;
    }

    /// <summary>Locks screenshots against other file operations while preserving macOS behavior during gameplay.</summary>
    internal bool TryBeginScreenshotMutation(Guid id, out IDisposable? lease, out InstanceStorageError? reason)
    {
        bool changed;
        _gate.Wait();
        try
        {
            if (!TryGetRegistrySnapshot(out var registry, out reason))
            {
                lease = null;
                return false;
            }

            var instance = Find(registry, id);
            if (instance.State is InstanceInstallationState.Queued or InstanceInstallationState.Installing)
            {
                lease = null;
                reason = InstanceStorageError.BuildInstalling;
                return false;
            }

            if (_contentBusy.Contains(id))
            {
                lease = null;
                reason = InstanceStorageError.BuildFilesBusy;
                return false;
            }

            changed = AddBusy(ref _contentBusy, id);
            lease = new BusyLease(() => EndBusy(id, launch: false));
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            NotifyBusyStateChanged();
        }

        reason = null;
        return true;
    }

    internal bool TryBeginInstallation(Guid id, out IDisposable? lease, out InstanceStorageError? reason)
    {
        bool changed;
        _gate.Wait();
        try
        {
            if (!TryGetRegistrySnapshot(out var registry, out reason))
            {
                lease = null;
                return false;
            }

            var instance = Find(registry, id);
            if (instance.State != InstanceInstallationState.Queued)
            {
                lease = null;
                reason = InstanceStorageError.BuildInstalling;
                return false;
            }

            if (_launchBusy.Contains(id))
            {
                lease = null;
                reason = InstanceStorageError.BuildRunning;
                return false;
            }

            if (_contentBusy.Contains(id))
            {
                lease = null;
                reason = InstanceStorageError.BuildFilesBusy;
                return false;
            }

            changed = AddBusy(ref _contentBusy, id);
            lease = new BusyLease(() => EndBusy(id, launch: false));
            reason = null;
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            NotifyBusyStateChanged();
        }

        return true;
    }

    private static bool AddBusy(ref ImmutableHashSet<Guid> busy, Guid id)
    {
        var updated = busy.Add(id);
        var changed = !ReferenceEquals(updated, busy);
        Volatile.Write(ref busy, updated);
        return changed;
    }

    private void EndBusy(Guid id, bool launch)
    {
        _gate.Wait();
        bool changed;
        try
        {
            ref var busy = ref launch ? ref _launchBusy : ref _contentBusy;
            var updated = busy.Remove(id);
            changed = !ReferenceEquals(updated, busy);
            Volatile.Write(ref busy, updated);
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            NotifyBusyStateChanged();
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var cleanupStaging = false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                var registry = await ReadRegistryAsync(cancellationToken);
                lock (_snapshotSync)
                {
                    _instances = Array.AsReadOnly(registry.Instances.ToArray());
                    _loadError = null;
                    _isLoaded = true;
                }
                cleanupStaging = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InstanceRegistryException exception)
            {
                lock (_snapshotSync)
                {
                    _instances = Array.AsReadOnly(Array.Empty<GameInstance>());
                    _loadError = exception;
                    _isLoaded = true;
                }
            }

            NotifyInstancesChanged();
        }
        finally
        {
            _gate.Release();
        }

        if (cleanupStaging)
        {
            ScheduleDeleteStagingCleanup(() =>
            {
                _beforeDeleteStagingCleanup?.Invoke();
                CleanupDeleteStaging();
            });
        }
    }

    private void ScheduleDeleteStagingCleanup(Action cleanup)
    {
        lock (_deleteStagingTasksSync)
        {
            if (_disposeTask is not null)
            {
                return;
            }

            var previous = _deleteStagingCleanupTask ?? Task.CompletedTask;
            var cleanupTask = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    cleanup();
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Could not clean instance delete staging; it will be retried on the next load: {exception}");
                }
            });
            Volatile.Write(ref _deleteStagingCleanupTask, cleanupTask);
        }
    }

    public string ValidateName(string input, Guid? excluding = null)
    {
        _gate.Wait();
        try
        {
            var (name, folderName) = ParseName(input);
            ValidateReservedName(folderName);
            var instances = GetRegistrySnapshot().Instances;
            _storage.EnsureAvailable(folderName, instances, excluding);
            return folderName;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string ValidateArchiveName(string input)
    {
        var (name, folderName) = ParseName(input);
        if (!string.Equals(name, input, StringComparison.Ordinal))
        {
            throw new InstanceStorageException(InstanceStorageError.InvalidBuildName,
                "The archived build name is not in canonical form.");
        }

        ValidateReservedName(folderName);
        return folderName;
    }

    public GameInstance Create(
        InstanceDraft draft,
        string versionID,
        string metadataURL = "",
        string metadataSHA1 = "",
        bool legacyTexturepacks = false,
        int javaMajorVersion = 0,
        ModpackPlan? modpackPlan = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (modpackPlan is not null)
        {
            modpackPlan.Validate();
            var loader = modpackPlan.Manifest.LoaderVersion;
            var matchesLoader = loader is null
                ? draft.ModLoader == InstanceModLoader.Vanilla && draft.FabricConfiguration is null
                : draft.ModLoader == InstanceModLoader.Fabric
                    && draft.FabricConfiguration is { Api: null } fabric
                    && string.Equals(fabric.LoaderVersion, loader, StringComparison.Ordinal);
            if (!string.Equals(versionID, modpackPlan.Manifest.Minecraft, StringComparison.Ordinal) || !matchesLoader)
            {
                throw new InstanceStorageException(InstanceStorageError.OperationFailed,
                    "The modpack plan does not match the selected Minecraft version and loader.");
            }
        }

        _gate.Wait();
        try
        {
            ValidateOffline(draft);
            var (name, folderName) = ParseName(draft.Name);
            ValidateReservedName(folderName);
            var registry = GetRegistrySnapshot();
            _storage.EnsureAvailable(folderName, registry.Instances);

            System.IO.Directory.CreateDirectory(_storage.Root);
            _ = InstanceStorage.ContainedPath(".hako-lock", _storage.Root, allowBaseReparsePoint: true);
            var directory = _storage.Directory(folderName);
            _storage.CreateDirectoryExclusive(directory);

            GameInstance instance;
            try
            {
                System.IO.Directory.CreateDirectory(Path.Combine(directory, "java"));
                System.IO.Directory.CreateDirectory(Path.Combine(directory, "minecraft"));
                WriteIconIfProvided(directory, draft.IconData);

                instance = new GameInstance
                {
                    Name = name,
                    FolderName = folderName,
                    VersionID = versionID,
                    MetadataURL = metadataURL,
                    MetadataSHA1 = metadataSHA1,
                    IconSymbol = draft.IconSymbol,
                    ArgumentSource = draft.ArgumentSource,
                    OfflineMode = draft.OfflineMode,
                    OfflineUsername = draft.OfflineUsername,
                    Parameters = NormalizeParameters(draft.Parameters),
                    JavaMajorVersion = javaMajorVersion > 0 ? javaMajorVersion : draft.JavaMajorVersion,
                    LegacyTexturepacks = legacyTexturepacks || draft.LegacyTexturepacks,
                    ModLoader = draft.ModLoader,
                    FabricConfiguration = draft.ModLoader == InstanceModLoader.Fabric ? draft.FabricConfiguration : null,
                    ModpackVersionID = modpackPlan?.VersionId,
                    Architecture = draft.Architecture
                };
                if (instance.ModLoader == InstanceModLoader.Fabric && string.IsNullOrWhiteSpace(instance.FabricConfiguration?.LoaderVersion))
                {
                    throw new InstanceStorageException(InstanceStorageError.MissingFabricLoaderVersion,
                        "Choose a Fabric Loader version before creating the build.");
                }

                modpackPlan?.Write(directory);
                var updated = registry.Instances.Append(instance).ToList();
                WriteRegistry(new InstanceRegistryFile(1, updated));
            }
            catch (Exception operationFailure)
            {
                RollbackAndThrow(operationFailure, () => InstanceStorage.DeleteCreatedDirectory(directory));
                throw;
            }
            NotifyInstancesChanged();
            return instance;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Creates a queued build from a version that was already verified by MojangClient.PrepareAsync.</summary>
    public GameInstance Create(InstanceDraft draft, PreparedInstallation prepared, bool legacyTexturepacks = false,
        ModpackPlan? modpackPlan = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!string.Equals(prepared.Version.Id, prepared.Manifest.Id, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(prepared.Version.Url)
            || string.IsNullOrWhiteSpace(prepared.Version.Sha1)
            || !Enum.IsDefined(prepared.Architecture))
        {
            throw new InstanceStorageException(InstanceStorageError.OperationFailed,
                "The prepared Minecraft version is incomplete.");
        }

        return Create(
            draft with { Architecture = prepared.Architecture.ToString() },
            prepared.Version.Id,
            prepared.Version.Url,
            prepared.Version.Sha1,
            legacyTexturepacks,
            prepared.Manifest.Java.MajorVersion,
            modpackPlan);
    }

    public void SetInstallationStatus(
        Guid id,
        InstanceInstallationState state,
        bool pauseRequested,
        string? errorCode = null,
        string? errorMessage = null)
    {
        _gate.Wait();
        try
        {
            var registry = GetRegistrySnapshot();
            var current = Find(registry, id);
            var updated = current with
            {
                State = state,
                PauseRequested = pauseRequested,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage
            };
            WriteRegistry(registry with
            {
                Instances = registry.Instances.Select(instance => instance.Id == id ? updated : instance).ToList()
            });
            NotifyInstancesChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void PinArchitecture(Guid id, GameArchitecture architecture)
    {
        if (!Enum.IsDefined(architecture))
        {
            throw new ArgumentOutOfRangeException(nameof(architecture));
        }

        _gate.Wait();
        try
        {
            var registry = GetRegistrySnapshot();
            var current = Find(registry, id);
            var architectureName = architecture.ToString();
            if (current.Architecture is { } pinned
                && !string.Equals(pinned, architectureName, StringComparison.Ordinal))
            {
                throw new InstanceStorageException(InstanceStorageError.OperationFailed,
                    "The build architecture has already been pinned.");
            }

            if (current.Architecture is not null)
            {
                return;
            }

            var updated = current with { Architecture = architectureName };
            WriteRegistry(registry with
            {
                Instances = registry.Instances.Select(instance => instance.Id == id ? updated : instance).ToList()
            });
            NotifyInstancesChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void CompleteInstallation(Guid id, InstallationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _gate.Wait();
        try
        {
            var registry = GetRegistrySnapshot();
            var current = Find(registry, id);
            if (current.PauseRequested)
            {
                throw new OperationCanceledException("The installation was paused.");
            }

            var expectedArchitecture = current.Architecture;
            if (!string.Equals(expectedArchitecture, result.Architecture.ToString(), StringComparison.Ordinal))
            {
                throw new InstanceStorageException(InstanceStorageError.OperationFailed,
                    "The installer returned a different architecture than the build's pinned architecture.");
            }

            var updated = current with
            {
                State = InstanceInstallationState.Ready,
                PauseRequested = false,
                ErrorCode = null,
                ErrorMessage = null,
                JavaExecutable = result.JavaExecutable,
                JavaMajorVersion = result.JavaMajorVersion,
                LegacyTexturepacks = result.LegacyTexturepacks,
                FabricProfileSHA1 = result.FabricProfileSHA1
            };
            WriteRegistry(registry with
            {
                Instances = registry.Instances.Select(instance => instance.Id == id ? updated : instance).ToList()
            });
            NotifyInstancesChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Rename(Guid id, string input)
    {
        _gate.Wait();
        try
        {
            var registry = GetRegistrySnapshot();
            var current = Find(registry, id);
            ThrowIfManagementBlocked(current);

            var (name, folderName) = ParseName(input);
            ValidateReservedName(folderName);
            _storage.EnsureAvailable(folderName, registry.Instances, id);
            var updated = current with { Name = name, FolderName = folderName };
            UpdateCore(registry, current, updated, null, preserveIcon: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void UpdateSettings(Guid id, InstanceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        _gate.Wait();
        try
        {
            var registry = GetRegistrySnapshot();
            var current = Find(registry, id);
            // Like macOS, a running game doesn't block profile settings: they apply on the next launch.
            if (GetManagementBlock(current) is { } error and not InstanceStorageError.BuildRunning)
            {
                throw new InstanceStorageException(error, ManagementMessage(error));
            }

            ValidateOffline(draft);
            var updated = ApplyDraft(current, draft, includeName: false);
            UpdateCore(registry, current, updated, draft.IconData);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Update(Guid id, InstanceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        _gate.Wait();
        try
        {
            var registry = GetRegistrySnapshot();
            var current = Find(registry, id);
            ThrowIfManagementBlocked(current);
            ValidateOffline(draft);
            var (name, folderName) = ParseName(draft.Name);
            ValidateReservedName(folderName);
            _storage.EnsureAvailable(folderName, registry.Instances, id);
            var updated = ApplyDraft(current, draft, includeName: false) with
            {
                Name = name,
                FolderName = folderName,
                IconRevision = Guid.NewGuid()
            };
            UpdateCore(registry, current, updated, draft.IconData);
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? ManagementBlockedReason(GameInstance instance) =>
        GetManagementBlock(instance) is { } error ? ManagementMessage(error) : null;

    /// <summary>The reason rename and delete are blocked, for localized UI text; does not wait for running store operations.</summary>
    public InstanceStorageError? ManagementBlockedError(GameInstance instance) => GetManagementBlock(instance);

    /// <summary>Copies a ready build into an independently registered folder while holding its content lease.</summary>
    public async Task<GameInstance> DuplicateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var leaseResult = await Task.Run(() => TryBeginContent(id, out var lease, out var reason)
                ? (lease, reason)
                : (null, reason), cancellationToken)
            .ConfigureAwait(false);
        using var lease = leaseResult.lease
            ?? throw new InstanceStorageException(leaseResult.reason ?? InstanceStorageError.BuildFilesBusy,
                "The build cannot be duplicated while it is installing, running, or busy.");

        var sourceInstance = Instances.FirstOrDefault(instance => instance.Id == id)
            ?? throw new InstanceStorageException(InstanceStorageError.InstanceRegistryUnavailable,
                "The build no longer exists.");
        if (sourceInstance.State != InstanceInstallationState.Ready)
            throw new InstanceStorageException(InstanceStorageError.BuildInstalling,
                "Wait for the build installation to finish before duplicating it.");

        var source = await Task.Run(() => GetInstanceDirectory(id), cancellationToken).ConfigureAwait(false);
        var stageName = ".hako-duplicate-" + Guid.NewGuid().ToString("N");
        var stage = InstanceStorage.ContainedPath(stageName, _storage.Root, allowBaseReparsePoint: true);
        var ownsStage = false;
        var published = false;
        try
        {
            InstanceStorage.CreateDirectoryExclusiveAt(_storage.Root, stageName);
            ownsStage = true;
            File.SetAttributes(stage, FileAttributes.Directory | FileAttributes.Hidden);
            var sourceEntries = await Task.Run(async () => await InstanceFileTree.CopyAsync(source, stage,
                IsTransientCopyPath, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var registry = GetRegistrySnapshot();
                var current = Find(registry, id);
                if (current != sourceInstance || current.State != InstanceInstallationState.Ready)
                    throw new InstanceStorageException(InstanceStorageError.BuildFilesBusy,
                        "The build profile changed while it was being duplicated.");

                InstanceFileTree.EnsureUnchanged(_storage.Directory(current.FolderName), sourceEntries,
                    IsTransientCopyPath, cancellationToken);
                for (var number = 2; number <= 999; number++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var suffix = " " + number;
                    var name = current.Name[..Math.Min(current.Name.Length, 60 - suffix.Length)].TrimEnd() + suffix;
                    var (validatedName, folderName) = ParseName(name);
                    try
                    {
                        ValidateReservedName(folderName);
                        _storage.EnsureAvailable(folderName, registry.Instances);
                    }
                    catch (InstanceStorageException exception) when (exception.Error is
                        InstanceStorageError.DuplicateBuildName or InstanceStorageError.BuildFolderExists)
                    {
                        continue;
                    }

                    var destination = _storage.Directory(folderName);
                    try
                    {
                        Directory.Move(stage, destination);
                    }
                    catch (IOException) when (EntryExists(destination))
                    {
                        continue;
                    }

                    try
                    {
                        File.SetAttributes(destination, FileAttributes.Directory);
                    }
                    catch (Exception operationFailure)
                    {
                        RollbackAndThrow(operationFailure, () => InstanceFileTree.DeleteOwnedDirectory(destination));
                        throw;
                    }

                    published = true;
                    var duplicate = current with
                    {
                        Id = Guid.NewGuid(),
                        Name = validatedName,
                        FolderName = folderName,
                        CreatedAt = DateTimeOffset.UtcNow,
                        IconRevision = Guid.NewGuid(),
                        State = InstanceInstallationState.Ready,
                        PauseRequested = false,
                        ErrorCode = null,
                        ErrorMessage = null
                    };
                    try
                    {
                        WriteRegistry(new InstanceRegistryFile(1, registry.Instances.Append(duplicate).ToList()));
                    }
                    catch (Exception operationFailure)
                    {
                        RollbackAndThrow(operationFailure, () => InstanceFileTree.DeleteOwnedDirectory(destination));
                        throw;
                    }

                    NotifyInstancesChanged();
                    return duplicate;
                }

                throw new InstanceStorageException(InstanceStorageError.DuplicateBuildName,
                    "No available name could be chosen for the duplicate build.");
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (InstanceFileTreeReparseException exception)
        {
            throw new InstanceStorageException(InstanceStorageError.ReparsePointNotAllowed,
                exception.Message, exception);
        }
        finally
        {
            if (ownsStage && !published && Directory.Exists(stage))
            {
                try { InstanceFileTree.DeleteOwnedDirectory(stage); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"Could not remove duplicate staging folder '{stage}': {exception.Message}");
                }
            }
        }
    }

    internal RestoreInstanceCommitOutcome CommitRestoredInstance(
        StagedRestoreArchive staged,
        GameInstance candidate,
        string inputName,
        Guid? replaceInstanceID,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsLoaded || LoadError is not null)
        {
            throw new RestoreArchiveException(RestoreArchiveFailureReason.RegistryUnavailable,
                "The build registry is unavailable.");
        }

        using var contentLease = replaceInstanceID is { } contentTargetID
            ? AcquireRestoreContentLease(contentTargetID)
            : null;

        _gate.Wait(cancellationToken);
        try
        {
            var registry = GetRegistrySnapshot();
            var manifest = staged.InstanceManifest
                ?? throw new RestoreArchiveException(RestoreArchiveFailureReason.InvalidArchive,
                    "The staged archive is not a build backup.");
            ValidateRestoreStage(staged.DirectoryPath);

            GameInstance? target = null;
            if (replaceInstanceID is { } targetID)
            {
                target = registry.Instances.FirstOrDefault(item => item.Id == targetID);
                if (target is null || staged.MatchingInstance?.Id != targetID || !MatchesBackup(target, manifest))
                {
                    throw new RestoreArchiveException(RestoreArchiveFailureReason.BuildUnavailable,
                        "The selected build no longer matches this backup.");
                }

                if (target.State != InstanceInstallationState.Ready)
                {
                    throw new RestoreArchiveException(RestoreArchiveFailureReason.BuildUnavailable,
                        "The selected build is not ready to be replaced.");
                }
            }

            var (name, folderName) = target is null
                ? FindAvailableRestoreName(inputName, registry.Instances)
                : (target.Name, target.FolderName);
            ValidateReservedName(folderName);
            if (target is not null) _storage.EnsureAvailable(folderName, registry.Instances, target.Id);
            ValidateOffline(new InstanceDraft
            {
                OfflineMode = candidate.OfflineMode,
                OfflineUsername = candidate.OfflineUsername
            });

            var destination = _storage.Directory(folderName);
            if (File.Exists(destination))
            {
                throw new RestoreArchiveException(RestoreArchiveFailureReason.DestinationConflict,
                    "The build destination is occupied.");
            }

            var oldDirectory = target is null ? null : _storage.Directory(target.FolderName);
            var oldExists = oldDirectory is not null && Directory.Exists(oldDirectory);
            if (target is null && Directory.Exists(destination))
            {
                throw new RestoreArchiveException(RestoreArchiveFailureReason.DestinationConflict,
                    "The build destination is occupied.");
            }

            var restored = candidate with
            {
                Id = target?.Id ?? candidate.Id,
                Name = name,
                FolderName = folderName,
                CreatedAt = target?.CreatedAt ?? candidate.CreatedAt
            };
            var stageAttributes = File.GetAttributes(staged.DirectoryPath);
            _ = InstanceFileTree.Snapshot(staged.DirectoryPath, null, cancellationToken);
            var retired = Path.Combine(_storage.Root, ".hako-retired-" + Guid.NewGuid().ToString("N"));
            var oldMoved = false;
            var newMoved = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (oldExists)
                {
                    MoveRestoreDirectory(oldDirectory!, retired);
                    oldMoved = true;
                }

                try { MoveRestoreDirectory(staged.DirectoryPath, destination); }
                catch (IOException) when (EntryExists(destination))
                {
                    throw new RestoreArchiveException(RestoreArchiveFailureReason.DestinationConflict,
                        "The build destination became occupied.");
                }
                newMoved = true;
                File.SetAttributes(destination, FileAttributes.Directory);
                cancellationToken.ThrowIfCancellationRequested();
                var instances = target is null
                    ? registry.Instances.Append(restored).ToList()
                    : registry.Instances.Select(item => item.Id == target.Id ? restored : item).ToList();
                WriteRegistry(new InstanceRegistryFile(1, instances));
            }
            catch (Exception operationFailure)
            {
                List<Exception>? rollbackFailures = null;
                if (newMoved)
                {
                    try
                    {
                        MoveRestoreDirectory(destination, staged.DirectoryPath);
                        File.SetAttributes(staged.DirectoryPath, stageAttributes);
                    }
                    catch (Exception rollbackFailure)
                    {
                        rollbackFailures = [rollbackFailure];
                    }
                }

                if (oldMoved)
                {
                    try { MoveRestoreDirectory(retired, oldDirectory!); }
                    catch (Exception rollbackFailure)
                    {
                        rollbackFailures ??= [];
                        rollbackFailures.Add(rollbackFailure);
                    }
                }

                if (rollbackFailures is not null)
                {
                    rollbackFailures.Insert(0, operationFailure);
                    var recoveryPaths = new[] { staged.DirectoryPath, destination, retired }
                        .Where(path => Directory.Exists(path) || File.Exists(path));
                    throw new RestoreArchiveException(RestoreArchiveFailureReason.RollbackFailed,
                        $"The restore failed and rollback was incomplete. Recovery data remains at: {string.Join(", ", recoveryPaths)}.",
                        new AggregateException(rollbackFailures));
                }

                throw;
            }

            NotifyInstancesChanged();
            var cleanupWarning = false;
            if (oldMoved)
            {
                var cleanupPath = retired;
                try { cleanupPath = MarkRetiredRestoreDirectory(retired); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"Could not mark retired restore folder: {exception.GetType().Name}");
                }
                try { DeleteRetiredRestoreDirectory(cleanupPath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    cleanupWarning = true;
                    Debug.WriteLine($"Could not remove retired restore folder: {exception.GetType().Name}");
                }
            }

            return new RestoreInstanceCommitOutcome(restored, cleanupWarning);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal void MoveRestoreDirectory(string from, string to)
    {
        _beforeRestoreMove?.Invoke(from, to);
        InstanceStorage.MoveDirectory(from, to);
    }

    internal void DeleteRetiredRestoreDirectory(string path)
    {
        _beforeRetiredDelete?.Invoke(path);
        InstanceStorage.DeleteCreatedDirectory(path);
    }

    private IDisposable AcquireRestoreContentLease(Guid id)
    {
        IDisposable? lease;
        InstanceStorageError? reason;
        try
        {
            if (!TryBeginContent(id, out lease, out reason))
            {
                var failure = reason is InstanceStorageError.InstanceRegistryNotLoaded or InstanceStorageError.InstanceRegistryUnavailable
                    ? RestoreArchiveFailureReason.RegistryUnavailable
                    : reason is InstanceStorageError.BuildInstalling or InstanceStorageError.BuildRunning or InstanceStorageError.BuildFilesBusy
                        ? RestoreArchiveFailureReason.BuildBusy
                        : RestoreArchiveFailureReason.BuildUnavailable;
                throw new RestoreArchiveException(failure, "The selected build is unavailable or busy.");
            }
        }
        catch (KeyNotFoundException exception)
        {
            throw new RestoreArchiveException(RestoreArchiveFailureReason.BuildUnavailable,
                "The selected build no longer exists.", exception);
        }

        try
        {
            if (Instances.FirstOrDefault(instance => instance.Id == id)?.State != InstanceInstallationState.Ready)
            {
                throw new RestoreArchiveException(RestoreArchiveFailureReason.BuildUnavailable,
                    "The selected build is not ready to be replaced.");
            }

            return lease!;
        }
        catch
        {
            lease?.Dispose();
            throw;
        }
    }

    internal string MarkRetiredRestoreDirectory(string path)
    {
        var name = Path.GetFileName(path);
        if (!TryGetRestoreTemporaryName(name, retired: true, committed: false))
        {
            throw new RestoreArchiveException(RestoreArchiveFailureReason.UnsafePath,
                "The retired restore folder is invalid.");
        }

        var committedPath = path + ".committed";
        InstanceStorage.MoveDirectory(path, committedPath);
        return committedPath;
    }

    private (string Name, string FolderName) FindAvailableRestoreName(
        string inputName,
        IReadOnlyCollection<GameInstance> instances)
    {
        var initial = ParseName(inputName);
        ValidateReservedName(initial.FolderName);
        try
        {
            _storage.EnsureAvailable(initial.FolderName, instances);
            return initial;
        }
        catch (InstanceStorageException exception) when (exception.Error is
            InstanceStorageError.DuplicateBuildName or InstanceStorageError.BuildFolderExists)
        {
        }

        for (var number = 2; number <= 999; number++)
        {
            var suffix = " " + number;
            var candidate = ParseName(initial.Name[..Math.Min(initial.Name.Length, 60 - suffix.Length)].TrimEnd() + suffix);
            ValidateReservedName(candidate.FolderName);
            try
            {
                _storage.EnsureAvailable(candidate.FolderName, instances);
                return candidate;
            }
            catch (InstanceStorageException exception) when (exception.Error is
                InstanceStorageError.DuplicateBuildName or InstanceStorageError.BuildFolderExists)
            {
            }
        }

        throw new InstanceStorageException(InstanceStorageError.DuplicateBuildName,
            "No unique build name is available for this backup.");
    }

    private void ValidateRestoreStage(string stagePath)
    {
        var fullPath = Path.GetFullPath(stagePath);
        var name = Path.GetFileName(fullPath);
        if (!string.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(_storage.Root), StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(".hako-restore-", StringComparison.Ordinal)
            || !Guid.TryParseExact(name[".hako-restore-".Length..], "N", out _))
        {
            throw new RestoreArchiveException(RestoreArchiveFailureReason.UnsafePath,
                "The restore staging folder is invalid.");
        }

        var attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device))
            != FileAttributes.Directory)
        {
            throw new RestoreArchiveException(RestoreArchiveFailureReason.UnsafePath,
                "The restore staging folder is unsafe.");
        }

        _ = InstanceStorage.ContainedPath(name, _storage.Root, allowBaseReparsePoint: true);
    }

    private static bool MatchesBackup(GameInstance instance, InstanceBackupManifest manifest) =>
        manifest.InstanceID is { } id
            ? instance.Id == id
            : string.Equals(instance.FolderName, manifest.FolderName, StringComparison.OrdinalIgnoreCase)
              && string.Equals(instance.VersionID, manifest.MinecraftVersion, StringComparison.Ordinal)
              && string.Equals(instance.ModLoader == InstanceModLoader.Fabric ? "fabric" : "vanilla",
                  manifest.ModLoader, StringComparison.Ordinal)
              && string.Equals(instance.FabricConfiguration?.LoaderVersion, manifest.LoaderVersion,
                  StringComparison.Ordinal);

    private static bool IsTransientCopyPath(string path) =>
        path.Equals(".hako-game", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(".hako-game/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("minecraft/.hako-running.json", StringComparison.OrdinalIgnoreCase)
        || path.Equals("minecraft/.hako-launch", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("minecraft/.hako-launch/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("minecraft/logs/hako-launch.log", StringComparison.OrdinalIgnoreCase);

    private static bool EntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public void Delete(Guid id)
    {
        _gate.Wait();
        try
        {
            var registry = GetRegistrySnapshot();
            var current = Find(registry, id);
            ThrowIfManagementBlocked(current);

            var directory = _storage.Directory(current.FolderName);
            if (File.Exists(directory))
            {
                throw new InstanceStorageException(InstanceStorageError.BuildFolderExists, "The build folder is not a directory.");
            }

            var removed = InstanceStorage.ContainedPath($".delete-{id:N}-{Guid.NewGuid():N}", _storage.Root, allowBaseReparsePoint: true);
            var exists = System.IO.Directory.Exists(directory);
            if (exists)
            {
                if (System.IO.Directory.Exists(removed) || File.Exists(removed))
                {
                    throw new InstanceStorageException(InstanceStorageError.BuildFolderExists,
                        "The temporary delete folder already exists.");
                }

                InstanceStorage.MoveDirectory(directory, removed);
            }

            try
            {
                WriteRegistry(new InstanceRegistryFile(1, registry.Instances.Where(item => item.Id != id).ToList()));
            }
            catch (Exception operationFailure)
            {
                RollbackAndThrow(operationFailure, () =>
                {
                    if (exists && System.IO.Directory.Exists(removed))
                    {
                        InstanceStorage.MoveDirectory(removed, directory);
                    }
                });
            }

            if (exists)
            {
                ScheduleDeleteStagingCleanup(() => TryDeleteStaging(removed));
            }

            NotifyInstancesChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void UpdateCore(InstanceRegistryFile registry, GameInstance current, GameInstance updated, byte[]? iconData, bool preserveIcon = false)
    {
        if (current.FolderName != updated.FolderName && GetManagementBlock(current) is { } error)
        {
            throw new InstanceStorageException(error, ManagementMessage(error));
        }

        var oldDirectory = _storage.Directory(current.FolderName);
        var newDirectory = _storage.Directory(updated.FolderName);
        var oldIcon = preserveIcon ? null : ReadIcon(oldDirectory);
        var moved = false;
        try
        {
            if (current.FolderName != updated.FolderName)
            {
                MoveDirectoryForRename(oldDirectory, newDirectory);
                moved = true;
            }

            if (!preserveIcon && iconData is not null)
            {
                WriteIcon(newDirectory, iconData);
            }
            else if (!preserveIcon && !string.IsNullOrEmpty(updated.IconSymbol))
            {
                File.Delete(InstanceStorage.ContainedPath("icon.png", newDirectory));
            }

            var instances = registry.Instances.Select(item => item.Id == current.Id ? updated : item).ToList();
            WriteRegistry(new InstanceRegistryFile(1, instances));
        }
        catch (Exception operationFailure)
        {
            var rollbackActions = new List<Action>();
            if (moved)
            {
                rollbackActions.Add(() =>
                {
                    if (System.IO.Directory.Exists(newDirectory))
                    {
                        MoveDirectoryForRename(newDirectory, oldDirectory);
                    }
                });
            }

            if (!preserveIcon)
            {
                rollbackActions.Add(() => RestoreIcon(oldDirectory, oldIcon));
            }

            RollbackAndThrow(operationFailure, rollbackActions.ToArray());
        }

        NotifyInstancesChanged();
    }

    private InstanceRegistryFile GetRegistrySnapshot()
    {
        lock (_snapshotSync)
        {
            if (!_isLoaded)
            {
                throw new InvalidOperationException("LoadAsync must complete before using the instance registry.");
            }

            if (_loadError is not null)
            {
                throw _loadError;
            }

            return new InstanceRegistryFile(1, _instances.ToList());
        }
    }

    private bool TryGetRegistrySnapshot(out InstanceRegistryFile registry, out InstanceStorageError? reason)
    {
        lock (_snapshotSync)
        {
            if (!_isLoaded)
            {
                registry = new InstanceRegistryFile(1, []);
                reason = InstanceStorageError.InstanceRegistryNotLoaded;
                return false;
            }

            if (_loadError is not null)
            {
                registry = new InstanceRegistryFile(1, []);
                reason = InstanceStorageError.InstanceRegistryUnavailable;
                return false;
            }

            registry = new InstanceRegistryFile(1, _instances.ToList());
            reason = null;
            return true;
        }
    }

    private async Task<InstanceRegistryFile> ReadRegistryAsync(CancellationToken cancellationToken)
    {
        string registryPath;
        try
        {
            registryPath = InstanceStorage.ContainedPath("instances.json", _storage.Root, allowBaseReparsePoint: true);
        }
        catch (InstanceStorageException exception)
        {
            throw new InstanceRegistryException(InstanceRegistryError.ReadFailure,
                "The instances registry path is not safe to read.", exception);
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(registryPath);
        }
        catch (FileNotFoundException)
        {
            return new InstanceRegistryFile(1, []);
        }
        catch (DirectoryNotFoundException)
        {
            return new InstanceRegistryFile(1, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstanceRegistryException(InstanceRegistryError.ReadFailure,
                "The instances registry could not be read. It was left unchanged.", exception);
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new InstanceRegistryException(InstanceRegistryError.Corrupt, "The instances registry path is a directory.");
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(registryPath, cancellationToken);
            var registry = JsonSerializer.Deserialize(bytes, HakoJsonContext.Default.InstanceRegistryFile);
            if (registry is null)
            {
                throw new JsonException("The registry is empty.");
            }

            if (registry.FormatVersion != 1)
            {
                throw new InstanceRegistryException(InstanceRegistryError.UnsupportedVersion,
                    $"Unsupported instances registry format version: {registry.FormatVersion}.");
            }

            if (registry.Instances is null)
            {
                throw new JsonException("The registry contains an invalid instance.");
            }

            var identifiers = new HashSet<Guid>();
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var instance in registry.Instances)
            {
                if (instance is null || instance.Id == Guid.Empty || !identifiers.Add(instance.Id)
                    || string.IsNullOrWhiteSpace(instance.Name) || string.IsNullOrWhiteSpace(instance.FolderName)
                    || instance.VersionID is null || instance.MetadataURL is null || instance.MetadataSHA1 is null
                    || instance.IconSymbol is null || instance.OfflineUsername is null || instance.JavaExecutable is null
                    || !InstanceStorage.IsSinglePathSegment(instance.FolderName)
                    || !folders.Add(instance.FolderName) || instance.Parameters is null
                    || instance.Parameters.JavaArguments is null || instance.Parameters.MinecraftArguments is null
                    || instance.OfflineMode && !OfflineUsername.IsValid(instance.OfflineUsername))
                {
                    throw new JsonException("The registry contains an invalid instance.");
                }

                if (instance.ModLoader == InstanceModLoader.Fabric
                    && string.IsNullOrWhiteSpace(instance.FabricConfiguration?.LoaderVersion))
                {
                    throw new JsonException("The Fabric configuration is missing.");
                }

                try
                {
                    var (name, folderName) = ParseName(instance.Name);
                    if (!string.Equals(name, instance.Name, StringComparison.Ordinal)
                        || !string.Equals(folderName, instance.FolderName, StringComparison.Ordinal))
                    {
                        throw new JsonException("The registry contains an invalid instance name.");
                    }

                    ValidateReservedName(folderName);
                }
                catch (InstanceStorageException exception)
                {
                    throw new JsonException("The registry contains an invalid instance name.", exception);
                }
            }

            return registry;
        }
        catch (InstanceRegistryException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new InstanceRegistryException(InstanceRegistryError.Corrupt,
                "The instances registry is damaged. It was left unchanged.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstanceRegistryException(InstanceRegistryError.ReadFailure,
                "The instances registry could not be read. It was left unchanged.", exception);
        }
    }

    private void WriteRegistry(InstanceRegistryFile registry)
    {
        System.IO.Directory.CreateDirectory(_storage.Root);
        var registryPath = InstanceStorage.ContainedPath("instances.json", _storage.Root, allowBaseReparsePoint: true);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(registry, HakoJsonContext.Default.InstanceRegistryFile);
        var temporaryPath = registryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            _beforeRegistryCommit?.Invoke();
            File.Move(temporaryPath, registryPath, overwrite: true);
            lock (_snapshotSync)
            {
                _instances = Array.AsReadOnly(registry.Instances.ToArray());
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static GameInstance Find(InstanceRegistryFile registry, Guid id) => registry.Instances.FirstOrDefault(instance => instance.Id == id)
        ?? throw new KeyNotFoundException("The build does not exist.");

    private static (string Name, string FolderName) ParseName(string input)
    {
        var name = input.Trim();
        if (name.Length is < 1 or > 60 || name.Any(character => character is not (>= 'A' and <= 'Z')
            and not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not ' '))
        {
            throw new InstanceStorageException(InstanceStorageError.InvalidBuildName,
                "Enter 1 to 60 Latin letters, digits, or spaces.");
        }

        return (name, name.Replace(' ', '_'));
    }

    private static void ValidateReservedName(string folderName)
    {
        if (ReservedNames.Contains(folderName))
        {
            throw new InstanceStorageException(InstanceStorageError.ReservedBuildName,
                $"The build name '{folderName}' is reserved.");
        }
    }

    private static HashSet<string> CreateReservedNames()
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { InstanceStorage.BackupsFolder, InstanceStorage.WorldsFolder, "CON", "PRN", "AUX", "NUL" };
        for (var index = 1; index <= 9; index++)
        {
            values.Add("COM" + index);
            values.Add("LPT" + index);
        }

        return values;
    }

    private static void ValidateOffline(InstanceDraft draft)
    {
        if (draft.OfflineMode && !OfflineUsername.IsValid(draft.OfflineUsername))
        {
            throw new InstanceStorageException(InstanceStorageError.InvalidOfflineUsername,
                "Offline username must contain 3 to 16 ASCII letters, digits, or underscores.");
        }
    }

    private void CleanupDeleteStaging()
    {
        if (!System.IO.Directory.Exists(_storage.Root))
        {
            return;
        }

        try
        {
            foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(_storage.Root))
            {
                var name = Path.GetFileName(entry);
                if (TryGetRestoreTemporaryName(name, retired: false, committed: false)
                    || TryGetRestoreTemporaryName(name, retired: true, committed: true))
                {
                    TryDeleteStaging(entry);
                    continue;
                }

                if (!TryParseDeleteStagingName(name, out var instanceId))
                {
                    continue;
                }

                if (instanceId is { } id && !ReconcileInterruptedDelete(id))
                {
                    continue;
                }

                TryDeleteStaging(entry);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Could not inspect instance delete staging; it will be retried on the next load: {exception}");
        }
    }

    private bool ReconcileInterruptedDelete(Guid instanceId)
    {
        _gate.Wait();
        try
        {
            var registry = GetRegistrySnapshot();
            var instance = registry.Instances.FirstOrDefault(item => item.Id == instanceId);
            if (instance is null)
            {
                return true;
            }

            var buildDirectory = _storage.Directory(instance.FolderName);
            if (System.IO.Directory.Exists(buildDirectory) || File.Exists(buildDirectory))
            {
                return true;
            }

            var remaining = registry.Instances.Where(item => item.Id != instanceId).ToList();
            WriteRegistry(new InstanceRegistryFile(1, remaining));
            NotifyInstancesChanged();
            return true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Could not reconcile instance delete staging; it will be retried on the next load: {exception}");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool TryParseDeleteStagingName(string name, out Guid? instanceId)
    {
        instanceId = null;
        const string prefix = ".delete-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var identifier = name[prefix.Length..];
        if (Guid.TryParseExact(identifier, "N", out _) || Guid.TryParseExact(identifier, "D", out _))
        {
            return true;
        }

        if (identifier.Length != 65 || identifier[32] != '-'
            || !Guid.TryParseExact(identifier[..32], "N", out var parsedInstanceId)
            || !Guid.TryParseExact(identifier[33..], "N", out _))
        {
            return false;
        }

        instanceId = parsedInstanceId;
        return true;
    }

    private static bool TryGetRestoreTemporaryName(string name, bool retired, bool committed)
    {
        var prefix = retired ? ".hako-retired-" : ".hako-restore-";
        var suffix = committed ? ".committed" : string.Empty;
        if (!name.StartsWith(prefix, StringComparison.Ordinal)
            || !name.EndsWith(suffix, StringComparison.Ordinal)
            || name.Length <= prefix.Length + suffix.Length)
        {
            return false;
        }

        var identifierLength = name.Length - prefix.Length - suffix.Length;
        return Guid.TryParseExact(name.AsSpan(prefix.Length, identifierLength), "N", out _);
    }

    private void TryDeleteStaging(string path)
    {
        lock (_deleteStagingSync)
        {
            try
            {
                InstanceStorage.DeleteCreatedDirectory(path);
            }
            catch (DirectoryNotFoundException)
            {
                // Another cleanup pass or the delete operation already removed this staging path.
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Could not remove instance delete staging; it will be retried on the next load: {exception}");
            }
        }
    }

    private static InstanceParameters NormalizeParameters(InstanceParameters parameters) => parameters with
    {
        WindowWidth = parameters.WindowWidth > 0 ? parameters.WindowWidth : 1280,
        WindowHeight = parameters.WindowHeight > 0 ? parameters.WindowHeight : 720,
        MaximumMemoryMiB = GameLaunchDefaults.NormalizeMemory(parameters.MaximumMemoryMiB)
    };

    private static GameInstance ApplyDraft(GameInstance current, InstanceDraft draft, bool includeName) => current with
    {
        Name = includeName ? ParseName(draft.Name).Name : current.Name,
        IconSymbol = draft.IconSymbol,
        IconRevision = draft.IconData is not null || !string.Equals(draft.IconSymbol, current.IconSymbol, StringComparison.Ordinal)
            ? Guid.NewGuid()
            : current.IconRevision,
        ArgumentSource = draft.ArgumentSource,
        OfflineMode = draft.OfflineMode,
        OfflineUsername = draft.OfflineUsername,
        Parameters = NormalizeParameters(draft.Parameters)
    };

    private static void WriteIconIfProvided(string directory, byte[]? data)
    {
        if (data is not null)
        {
            WriteIcon(directory, data);
        }
    }

    private static void WriteIcon(string directory, byte[] data)
    {
        var iconPath = InstanceStorage.ContainedPath("icon.png", directory);
        var temporaryPath = iconPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, iconPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static byte[]? ReadIcon(string directory)
    {
        var iconPath = InstanceStorage.ContainedPath("icon.png", directory);
        return File.Exists(iconPath) ? File.ReadAllBytes(iconPath) : null;
    }

    private static void RestoreIcon(string directory, byte[]? oldIcon)
    {
        var iconPath = InstanceStorage.ContainedPath("icon.png", directory);
        if (oldIcon is null)
        {
            File.Delete(iconPath);
        }
        else
        {
            WriteIcon(directory, oldIcon);
        }
    }

    private static void MoveDirectoryForRename(string from, string to)
    {
        if (!string.Equals(Path.GetFileName(from), Path.GetFileName(to), StringComparison.OrdinalIgnoreCase))
        {
            InstanceStorage.MoveDirectory(from, to);
            return;
        }

        var root = Path.GetDirectoryName(from)!;
        var temporary = Path.Combine(root, ".rename-" + Guid.NewGuid().ToString("N"));
        InstanceStorage.MoveDirectory(from, temporary);
        try
        {
            InstanceStorage.MoveDirectory(temporary, to);
        }
        catch (Exception operationFailure)
        {
            RollbackAndThrow(operationFailure, () =>
            {
                if (System.IO.Directory.Exists(temporary))
                {
                    InstanceStorage.MoveDirectory(temporary, from);
                }
            });
        }
    }

    private InstanceStorageError? GetManagementBlock(GameInstance instance)
    {
        if (instance.State is InstanceInstallationState.Queued or InstanceInstallationState.Installing)
        {
            return InstanceStorageError.BuildInstalling;
        }

        if (Volatile.Read(ref _launchBusy).Contains(instance.Id))
        {
            return InstanceStorageError.BuildRunning;
        }

        if (Volatile.Read(ref _contentBusy).Contains(instance.Id))
        {
            return InstanceStorageError.BuildFilesBusy;
        }

        return null;
    }

    private void ThrowIfManagementBlocked(GameInstance instance)
    {
        if (GetManagementBlock(instance) is { } error)
        {
            throw new InstanceStorageException(error, ManagementMessage(error));
        }
    }

    private static string ManagementMessage(InstanceStorageError error) => error switch
    {
        InstanceStorageError.BuildInstalling => "The build is installing.",
        InstanceStorageError.BuildRunning => "Minecraft is running for this build.",
        InstanceStorageError.BuildFilesBusy => "Build files are busy.",
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, null)
    };

    [DoesNotReturn]
    private static void RollbackAndThrow(Exception operationFailure, params Action[] rollbackActions)
    {
        List<Exception>? failures = null;
        foreach (var rollback in rollbackActions)
        {
            try
            {
                rollback();
            }
            catch (Exception rollbackFailure)
            {
                failures ??= [operationFailure];
                failures.Add(rollbackFailure);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("The operation and its rollback both failed.", failures);
        }

        ExceptionDispatchInfo.Capture(operationFailure).Throw();
    }

    private void NotifyInstancesChanged()
    {
        _gate.Release();
        try
        {
            InvokePropertyChanged(nameof(Instances));
            InvokePropertyChanged(nameof(LoadError));
            InvokePropertyChanged(nameof(IsLoaded));
            InvokeHandlers(InstancesChanged, this, EventArgs.Empty);
        }
        finally
        {
            _gate.Wait();
        }
    }

    private void NotifyBusyStateChanged()
    {
        InvokePropertyChanged("BusyState");
        InvokeHandlers(BusyStateChanged, this, EventArgs.Empty);
    }

    private void InvokePropertyChanged(string propertyName)
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

    private static void InvokeHandlers(EventHandler? handlers, object sender, EventArgs args)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(sender, args);
            }
            catch (Exception)
            {
            }
        }
    }

    private sealed class BusyLease(Action end) : IDisposable
    {
        private Action? _end = end;

        public void Dispose() => Interlocked.Exchange(ref _end, null)?.Invoke();
    }
}

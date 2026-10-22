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
    private readonly object _deleteStagingSync = new();
    private readonly object _deleteStagingTasksSync = new();
    private readonly object _snapshotSync = new();
    private IReadOnlyList<GameInstance> _instances = Array.AsReadOnly(Array.Empty<GameInstance>());
    private InstanceRegistryException? _loadError;
    private Task? _deleteStagingCleanupTask;
    private Task? _disposeTask;
    private bool _isLoaded;
    private readonly HashSet<Guid> _launchBusy = [];
    private readonly HashSet<Guid> _contentBusy = [];

    public InstanceStore(HakoPaths paths, Action? beforeRegistryCommit = null)
        : this(paths, beforeRegistryCommit, null)
    {
    }

    internal InstanceStore(HakoPaths paths, Action? beforeRegistryCommit, Action? beforeDeleteStagingCleanup)
    {
        _storage = new InstanceStorage(paths);
        _beforeRegistryCommit = beforeRegistryCommit;
        _beforeDeleteStagingCleanup = beforeDeleteStagingCleanup;
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

    public bool IsLaunchBusy(Guid id)
    {
        _gate.Wait();
        try
        {
            return _launchBusy.Contains(id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool IsContentBusy(Guid id)
    {
        _gate.Wait();
        try
        {
            return _contentBusy.Contains(id);
        }
        finally
        {
            _gate.Release();
        }
    }

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

            changed = _launchBusy.Add(id);
            lease = new BusyLease(() => EndBusy(id, _launchBusy));
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

            changed = _contentBusy.Add(id);
            lease = new BusyLease(() => EndBusy(id, _contentBusy));
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

            changed = _contentBusy.Add(id);
            lease = new BusyLease(() => EndBusy(id, _contentBusy));
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

    private void EndBusy(Guid id, HashSet<Guid> busy)
    {
        _gate.Wait();
        bool changed;
        try
        {
            changed = busy.Remove(id);
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

    public GameInstance Create(
        InstanceDraft draft,
        string versionID,
        string metadataURL = "",
        string metadataSHA1 = "",
        bool legacyTexturepacks = false,
        int javaMajorVersion = 0)
    {
        ArgumentNullException.ThrowIfNull(draft);
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
                    Architecture = draft.Architecture
                };
                if (instance.ModLoader == InstanceModLoader.Fabric && string.IsNullOrWhiteSpace(instance.FabricConfiguration?.LoaderVersion))
                {
                    throw new InstanceStorageException(InstanceStorageError.MissingFabricLoaderVersion,
                        "Choose a Fabric Loader version before creating the build.");
                }

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
    public GameInstance Create(InstanceDraft draft, PreparedInstallation prepared, bool legacyTexturepacks = false)
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
            prepared.Manifest.Java.MajorVersion);
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
                LegacyTexturepacks = result.LegacyTexturepacks
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

    public string? ManagementBlockedReason(GameInstance instance)
    {
        _gate.Wait();
        try
        {
            return GetManagementBlock(instance) is { } error ? ManagementMessage(error) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The reason rename and delete are blocked, for localized UI text.</summary>
    public InstanceStorageError? ManagementBlockedError(GameInstance instance)
    {
        _gate.Wait();
        try
        {
            return GetManagementBlock(instance);
        }
        finally
        {
            _gate.Release();
        }
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
                if (!TryParseDeleteStagingName(Path.GetFileName(entry), out var instanceId))
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

        if (_launchBusy.Contains(instance.Id))
        {
            return InstanceStorageError.BuildRunning;
        }

        if (_contentBusy.Contains(instance.Id))
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

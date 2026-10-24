using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hako.Core.Minecraft;

namespace Hako.Core.Instances;

public enum RestoreArchiveKind
{
    Instance,
    World
}

public enum RestoreArchiveFailureReason
{
    UnsupportedFileType,
    FileUnavailable,
    InvalidArchive,
    UnsafePath,
    InvalidManifest,
    InvalidInstance,
    InvalidWorld,
    InvalidName,
    InsufficientSpace,
    RegistryUnavailable,
    BuildUnavailable,
    BuildBusy,
    DestinationConflict,
    RollbackFailed,
    Cancelled,
    OperationFailed
}

public sealed record RestoreArchiveFailure(RestoreArchiveFailureReason Reason, string Diagnostic);

public sealed record RestorePreparationResult(StagedRestoreArchive? Staged, RestoreArchiveFailure? Failure)
{
    public bool Succeeded => Staged is not null && Failure is null;

    internal static RestorePreparationResult Success(StagedRestoreArchive staged) => new(staged, null);
    internal static RestorePreparationResult Failed(RestoreArchiveFailureReason reason, string diagnostic) =>
        new(null, new RestoreArchiveFailure(reason, diagnostic));
}

public sealed record RestoreArchiveCommitResult(
    GameInstance? Instance,
    string? WorldFolderName,
    RestoreArchiveFailure? Failure,
    bool CleanupWarning)
{
    public bool Succeeded => Failure is null && (Instance is not null || WorldFolderName is not null);

    internal static RestoreArchiveCommitResult InstanceSuccess(GameInstance instance, bool cleanupWarning) =>
        new(instance, null, null, cleanupWarning);

    internal static RestoreArchiveCommitResult WorldSuccess(string worldFolderName, bool cleanupWarning) =>
        new(null, worldFolderName, null, cleanupWarning);

    internal static RestoreArchiveCommitResult Failed(RestoreArchiveFailureReason reason, string diagnostic) =>
        new(null, null, new RestoreArchiveFailure(reason, diagnostic), false);
}

/// <summary>Validated archive data extracted into an isolated Hako staging folder.</summary>
public sealed class StagedRestoreArchive
{
    private int _operationState;

    internal StagedRestoreArchive(
        string directory,
        RestoreArchiveKind kind,
        InstanceBackupManifest? instanceManifest,
        WorldBackupManifest? worldManifest,
        bool needsJavaInstallation,
        GameArchitecture? architecture,
        int? javaMajorVersion,
        GameInstance? matchingInstance,
        string? modpackVersionID)
    {
        DirectoryPath = directory;
        Kind = kind;
        InstanceManifest = instanceManifest;
        WorldManifest = worldManifest;
        NeedsJavaInstallation = needsJavaInstallation;
        Architecture = architecture;
        JavaMajorVersion = javaMajorVersion;
        MatchingInstance = matchingInstance;
        ModpackVersionID = modpackVersionID;
    }

    public RestoreArchiveKind Kind { get; }
    public InstanceBackupManifest? InstanceManifest { get; }
    public WorldBackupManifest? WorldManifest { get; }
    public bool NeedsJavaInstallation { get; }
    public GameArchitecture? Architecture { get; }
    public int? JavaMajorVersion { get; }
    public GameInstance? MatchingInstance { get; }
    public string? ModpackVersionID { get; }
    public bool IsCompleted => Volatile.Read(ref _operationState) == 2;

    internal string DirectoryPath { get; }
    internal bool TryBeginOperation() => Interlocked.CompareExchange(ref _operationState, 1, 0) == 0;
    internal void CompleteOperation() => Volatile.Write(ref _operationState, 2);
    internal void ReleaseOperation() => Interlocked.CompareExchange(ref _operationState, 0, 1);
}

/// <summary>Validates and extracts Swift-compatible .hakobackup and .hakoworld files.</summary>
[SupportedOSPlatform("windows")]
public sealed class RestoreArchive
{
    private const long MaximumArchiveSize = 20L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 100_000;
    private const long MaximumManifestSize = 4L * 1024 * 1024;
    private const long MaximumFileSize = 8L * 1024 * 1024 * 1024;
    private const long MaximumExpandedSize = 100L * 1024 * 1024 * 1024;
    private const long MaximumCentralDirectorySize = 64L * 1024 * 1024;
    private const long MaximumMetadataSize = 16L * 1024 * 1024;
    private const long MaximumIconSize = 4L * 1024 * 1024;
    private const string StagePrefix = ".hako-restore-";
    private static readonly Regex VersionIdPattern = new("^[A-Za-z0-9._+\\-]{1,128}$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha1Pattern = new("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha512Pattern = new("^[0-9a-fA-F]{128}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ReservedDeviceNames = CreateReservedDeviceNames();

    private readonly InstanceStore _store;
    private readonly IHostArchitectureProvider _hostArchitecture;
    private readonly Func<string, long> _availableSpace;

    public RestoreArchive(InstanceStore store)
        : this(store, new WindowsHostArchitectureProvider(), GetAvailableSpace)
    {
    }

    internal RestoreArchive(
        InstanceStore store,
        IHostArchitectureProvider hostArchitecture,
        Func<string, long> availableSpace)
    {
        _store = store;
        _hostArchitecture = hostArchitecture;
        _availableSpace = availableSpace;
    }

    public async Task<RestorePreparationResult> PrepareAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        string? stagingDirectory = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_store.IsLoaded || _store.LoadError is not null)
            {
                return RestorePreparationResult.Failed(RestoreArchiveFailureReason.RegistryUnavailable,
                    "The build registry is unavailable.");
            }

            if (!Path.GetExtension(archivePath).Equals(".hakobackup", StringComparison.OrdinalIgnoreCase)
                && !Path.GetExtension(archivePath).Equals(".hakoworld", StringComparison.OrdinalIgnoreCase))
            {
                return RestorePreparationResult.Failed(RestoreArchiveFailureReason.UnsupportedFileType,
                    "Choose a .hakobackup or .hakoworld file.");
            }

            var fullArchivePath = Path.GetFullPath(archivePath);
            if (!File.Exists(fullArchivePath))
            {
                return RestorePreparationResult.Failed(RestoreArchiveFailureReason.FileUnavailable,
                    "The backup file could not be opened.");
            }

            var limits = new ZipArchiveValidationLimits(MaximumEntries, MaximumArchiveSize, MaximumFileSize,
                MaximumExpandedSize, MaximumCentralDirectorySize);
            var scan = await Task.Run(() => ZipArchiveSupport.ScanCentralDirectory(fullArchivePath, limits), cancellationToken)
                .ConfigureAwait(false);
            var kind = Path.GetExtension(fullArchivePath).Equals(".hakobackup", StringComparison.OrdinalIgnoreCase)
                ? RestoreArchiveKind.Instance
                : RestoreArchiveKind.World;
            using var archiveStream = new FileStream(fullArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count != scan.Entries.Count)
            {
                throw Failure(RestoreArchiveFailureReason.InvalidArchive, "The backup archive is damaged.");
            }

            var entries = ValidateArchiveEntries(archive, scan, kind);
            var manifestEntry = entries.SingleOrDefault(entry => string.Equals(entry.Record.Name, "data.json", StringComparison.Ordinal))
                ?? throw Failure(RestoreArchiveFailureReason.InvalidManifest, "The backup manifest is missing.");
            if (manifestEntry.Record.IsDirectory || manifestEntry.Record.IsSymbolicLink
                || manifestEntry.Record.Length > MaximumManifestSize)
            {
                throw Failure(RestoreArchiveFailureReason.InvalidManifest, "The backup manifest is invalid.");
            }

            var manifestBytes = await ReadEntryAsync(manifestEntry.Entry, manifestEntry.Record,
                MaximumManifestSize, cancellationToken).ConfigureAwait(false);
            InstanceBackupManifest? instanceManifest = null;
            WorldBackupManifest? worldManifest = null;
            bool needsJavaInstallation = false;
            GameInstance? matchingInstance;
            string? modpackVersionID = null;
            GameArchitecture? architecture = null;
            int? javaMajorVersion = null;
            if (kind == RestoreArchiveKind.Instance)
            {
                instanceManifest = Deserialize(manifestBytes, HakoJsonContext.Default.InstanceBackupManifest,
                    RestoreArchiveFailureReason.InvalidManifest);
                ValidateInstanceManifest(instanceManifest);
                needsJavaInstallation = !IsWindowsExecutable(instanceManifest.Profile.JavaExecutable);
                matchingInstance = FindMatchingInstance(instanceManifest);
            }
            else
            {
                worldManifest = Deserialize(manifestBytes, HakoJsonContext.Default.WorldBackupManifest,
                    RestoreArchiveFailureReason.InvalidManifest);
                ValidateWorldManifest(worldManifest);
                matchingInstance = FindMatchingWorldTarget(worldManifest);
            }

            var expandedSize = entries.Where(entry =>
                    !string.Equals(entry.Record.Name, "data.json", StringComparison.Ordinal)
                    && !entry.Record.IsDirectory
                    && !(kind == RestoreArchiveKind.Instance && needsJavaInstallation && IsUnderJava(entry.Record.Name)))
                .Aggregate(0L, (sum, entry) => checked(sum + entry.Record.Length));
            if (_availableSpace(_store.GameRoot) < expandedSize)
            {
                throw Failure(RestoreArchiveFailureReason.InsufficientSpace,
                    "There is not enough free space to prepare this backup.");
            }

            Directory.CreateDirectory(_store.GameRoot);
            var stageName = StagePrefix + Guid.NewGuid().ToString("N");
            stagingDirectory = InstanceStorage.ContainedPath(stageName, _store.GameRoot, allowBaseReparsePoint: true);
            InstanceStorage.CreateDirectoryExclusiveAt(_store.GameRoot, stageName);
            File.SetAttributes(stagingDirectory, FileAttributes.Directory | FileAttributes.Hidden);

            foreach (var item in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(item.Record.Name, "data.json", StringComparison.Ordinal)) continue;
                var discardJava = kind == RestoreArchiveKind.Instance && needsJavaInstallation && IsUnderJava(item.Record.Name);
                if (item.Record.IsDirectory)
                {
                    if (item.Record.Length != 0)
                    {
                        throw Failure(RestoreArchiveFailureReason.InvalidArchive, "The backup archive contains an invalid directory entry.");
                    }

                    _ = await ReadEntryAsync(item.Entry, item.Record, 0, cancellationToken).ConfigureAwait(false);

                    if (!discardJava && !item.Record.IsSymbolicLink)
                    {
                        Directory.CreateDirectory(InstanceStorage.ContainedPath(item.Record.Name, stagingDirectory));
                    }

                    continue;
                }

                var outputPath = discardJava || item.Record.IsSymbolicLink
                    ? null
                    : InstanceStorage.ContainedPath(item.Record.Name, stagingDirectory);
                if (outputPath is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                }

                var linkTarget = item.Record.IsSymbolicLink
                    ? await CopyEntryAsync(item.Entry, item.Record, null, MaximumFileSize, cancellationToken).ConfigureAwait(false)
                    : null;
                if (linkTarget is not null)
                {
                    ValidateJavaLink(item.Record.Name, linkTarget);
                }
                else if (!item.Record.IsSymbolicLink)
                {
                    if (outputPath is null)
                    {
                        await CopyEntryAsync(item.Entry, item.Record, null, MaximumFileSize, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        try
                        {
                            await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write,
                                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            await CopyEntryAsync(item.Entry, item.Record, output, MaximumFileSize, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            try { File.Delete(outputPath); }
                            catch (FileNotFoundException) { }
                            throw;
                        }
                    }
                }
            }

            if (instanceManifest is not null)
            {
                if (!needsJavaInstallation)
                {
                    var java = InspectJava(stagingDirectory, instanceManifest);
                    if (java is null || !IsSupportedByHost(java.Architecture, java.Host))
                    {
                        needsJavaInstallation = true;
                    }
                    else if (entries.Any(entry => entry.Record.IsSymbolicLink))
                    {
                        throw Failure(RestoreArchiveFailureReason.UnsafePath,
                            "A Windows Java runtime cannot contain symbolic links.");
                    }
                    else
                    {
                        architecture = java.Architecture;
                        javaMajorVersion = java.JavaMajorVersion;
                        modpackVersionID = await ValidateExtractedInstanceAsync(stagingDirectory, instanceManifest,
                            java.Architecture, java.JavaMajorVersion, cancellationToken).ConfigureAwait(false);
                    }

                    if (needsJavaInstallation)
                    {
                        DeleteJavaDirectory(stagingDirectory);
                    }
                }

                if (needsJavaInstallation)
                {
                    architecture = null;
                    javaMajorVersion = null;
                    modpackVersionID = await ValidateExtractedInstanceAsync(stagingDirectory, instanceManifest,
                        null, null, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (worldManifest is not null)
            {
                await ValidateExtractedWorldAsync(stagingDirectory, worldManifest, cancellationToken).ConfigureAwait(false);
            }

            var staged = new StagedRestoreArchive(stagingDirectory, kind, instanceManifest, worldManifest,
                needsJavaInstallation, architecture, javaMajorVersion, matchingInstance, modpackVersionID);
            stagingDirectory = null;
            return RestorePreparationResult.Success(staged);
        }
        catch (OperationCanceledException)
        {
            return RestorePreparationResult.Failed(RestoreArchiveFailureReason.Cancelled, "Backup preparation was cancelled.");
        }
        catch (RestoreArchiveException exception)
        {
            return RestorePreparationResult.Failed(exception.Reason, exception.Message);
        }
        catch (ZipArchiveValidationException exception)
        {
            return RestorePreparationResult.Failed(exception.Failure == ZipArchiveValidationFailure.UnsafeEntry
                    ? RestoreArchiveFailureReason.UnsafePath
                    : RestoreArchiveFailureReason.InvalidArchive,
                exception.Failure == ZipArchiveValidationFailure.UnsafeEntry
                    ? "The archive contains an unsafe entry."
                    : "The backup archive is damaged or unsupported.");
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or OverflowException)
        {
            return RestorePreparationResult.Failed(RestoreArchiveFailureReason.InvalidArchive,
                "The backup archive is damaged or unsupported.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                          or JsonException or ArgumentException
                                          or NotSupportedException)
        {
            return RestorePreparationResult.Failed(RestoreArchiveFailureReason.OperationFailed,
                "The backup could not be prepared.");
        }
        finally
        {
            if (stagingDirectory is not null)
            {
                try { InstanceStorage.DeleteCreatedDirectory(stagingDirectory); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Debug.WriteLine($"Could not remove restore staging folder: {exception.GetType().Name}");
                }
            }
        }
    }

    public Task DiscardAsync(StagedRestoreArchive staged, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staged);
        cancellationToken.ThrowIfCancellationRequested();
        if (!staged.TryBeginOperation())
        {
            if (staged.IsCompleted) return Task.CompletedTask;
            throw new IOException("The restore staging directory is already in use.");
        }

        try
        {
            if (!Directory.Exists(staged.DirectoryPath) && !File.Exists(staged.DirectoryPath))
            {
                staged.CompleteOperation();
                return Task.CompletedTask;
            }

            ValidateStageDirectory(staged.DirectoryPath);
            InstanceStorage.DeleteCreatedDirectory(staged.DirectoryPath);
            staged.CompleteOperation();
        }
        catch
        {
            staged.ReleaseOperation();
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task<RestoreArchiveCommitResult> CommitInstanceAsync(
        StagedRestoreArchive staged,
        string name,
        Guid? replaceInstanceID = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staged);
        if (staged.Kind != RestoreArchiveKind.Instance || staged.InstanceManifest is null)
        {
            return RestoreArchiveCommitResult.Failed(RestoreArchiveFailureReason.InvalidArchive,
                "The staged archive is not a build backup.");
        }
        if (!staged.TryBeginOperation())
        {
            return RestoreArchiveCommitResult.Failed(RestoreArchiveFailureReason.BuildBusy,
                "The staged backup is already being used or has been completed.");
        }

        var completed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_store.IsLoaded || _store.LoadError is not null)
            {
                return RestoreArchiveCommitResult.Failed(RestoreArchiveFailureReason.RegistryUnavailable,
                    "The build registry is unavailable.");
            }

            var manifest = staged.InstanceManifest;
            var instance = new GameInstance
            {
                Id = replaceInstanceID ?? Guid.NewGuid(),
                CreatedAt = manifest.InstanceCreatedAt,
                VersionID = manifest.MinecraftVersion,
                MetadataURL = manifest.Profile.MetadataURL,
                MetadataSHA1 = manifest.Profile.MetadataSHA1,
                IconSymbol = manifest.IconSymbol,
                ArgumentSource = manifest.Profile.ArgumentSource,
                OfflineMode = manifest.Profile.OfflineMode,
                OfflineUsername = manifest.Profile.OfflineUsername,
                Parameters = manifest.Profile.Parameters,
                State = staged.NeedsJavaInstallation ? InstanceInstallationState.Queued : InstanceInstallationState.Ready,
                JavaExecutable = staged.NeedsJavaInstallation ? "bin/javaw.exe" : manifest.Profile.JavaExecutable,
                JavaMajorVersion = staged.JavaMajorVersion ?? manifest.JavaMajorVersion,
                LegacyTexturepacks = manifest.Profile.LegacyTexturepacks,
                ModLoader = manifest.ModLoader == "fabric" ? InstanceModLoader.Fabric : InstanceModLoader.Vanilla,
                FabricConfiguration = manifest.Profile.FabricConfiguration is { } fabric
                    ? new FabricConfiguration(fabric.LoaderVersion, staged.ModpackVersionID is null ? fabric.Api : null)
                    : null,
                FabricProfileSHA1 = manifest.Profile.FabricProfileSHA1,
                ModpackVersionID = staged.ModpackVersionID,
                Architecture = staged.Architecture?.ToString(),
                PauseRequested = false,
                ErrorCode = null,
                ErrorMessage = null,
                IconRevision = Guid.NewGuid()
            };

            var outcome = await Task.Run(
                () => _store.CommitRestoredInstance(staged, instance, name, replaceInstanceID, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            staged.CompleteOperation();
            completed = true;
            return RestoreArchiveCommitResult.InstanceSuccess(outcome.Instance, outcome.CleanupWarning);
        }
        catch (OperationCanceledException)
        {
            return RestoreArchiveCommitResult.Failed(RestoreArchiveFailureReason.Cancelled,
                "Restoring the build was cancelled.");
        }
        catch (RestoreArchiveException exception)
        {
            return RestoreArchiveCommitResult.Failed(exception.Reason, exception.Message);
        }
        catch (InstanceStorageException exception)
        {
            return RestoreArchiveCommitResult.Failed(MapStoreFailure(exception), "The build could not be restored.");
        }
        catch (InstanceFileTreeReparseException)
        {
            return RestoreArchiveCommitResult.Failed(RestoreArchiveFailureReason.UnsafePath,
                "The staged build contains an unsafe file system entry.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return RestoreArchiveCommitResult.Failed(RestoreArchiveFailureReason.OperationFailed,
                "The build could not be restored.");
        }
        finally
        {
            if (!completed) staged.ReleaseOperation();
        }
    }

    public async Task<RestoreArchiveCommitResult> CommitWorldAsync(
        StagedRestoreArchive staged,
        Guid targetInstanceID,
        string name,
        string folderName,
        bool replace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staged);
        if (staged.Kind != RestoreArchiveKind.World || staged.WorldManifest is null)
        {
            return RestoreArchiveCommitResult.Failed(RestoreArchiveFailureReason.InvalidArchive,
                "The staged archive is not a world backup.");
        }
        if (!staged.TryBeginOperation())
        {
            return RestoreArchiveCommitResult.Failed(RestoreArchiveFailureReason.BuildBusy,
                "The staged backup is already being used or has been completed.");
        }

        var completed = false;
        try
        {
            var result = await Task.Run(
                () => CommitWorld(staged, targetInstanceID, name, folderName, replace, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            completed = result.Succeeded;
            if (completed) staged.CompleteOperation();
            return result;
        }
        catch (OperationCanceledException)
        {
            return RestoreArchiveCommitResult.Failed(RestoreArchiveFailureReason.Cancelled,
                "Restoring the world was cancelled.");
        }
        catch (RestoreArchiveException exception)
        {
            return RestoreArchiveCommitResult.Failed(exception.Reason, exception.Message);
        }
        catch (InstanceStorageException exception)
        {
            return RestoreArchiveCommitResult.Failed(MapStoreFailure(exception), "The world could not be restored.");
        }
        catch (InstanceWorldException exception)
        {
            return RestoreArchiveCommitResult.Failed(exception.Error == InstanceWorldFailureReason.ReparsePointNotAllowed
                ? RestoreArchiveFailureReason.UnsafePath
                : RestoreArchiveFailureReason.DestinationConflict, "The world destination is unavailable.");
        }
        catch (InstanceFileTreeReparseException)
        {
            return RestoreArchiveCommitResult.Failed(RestoreArchiveFailureReason.UnsafePath,
                "The staged world contains an unsafe file system entry.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return RestoreArchiveCommitResult.Failed(RestoreArchiveFailureReason.OperationFailed,
                "The world could not be restored.");
        }
        finally
        {
            if (!completed) staged.ReleaseOperation();
        }
    }

    private RestoreArchiveCommitResult CommitWorld(
        StagedRestoreArchive staged,
        Guid targetInstanceID,
        string name,
        string folderName,
        bool replace,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_store.IsLoaded || _store.LoadError is not null)
        {
            return RestoreArchiveCommitResult.Failed(RestoreArchiveFailureReason.RegistryUnavailable,
                "The build registry is unavailable.");
        }

        if (!_store.TryBeginContent(targetInstanceID, out var lease, out var blockedReason))
        {
            return RestoreArchiveCommitResult.Failed(blockedReason is InstanceStorageError.InstanceRegistryNotLoaded
                    or InstanceStorageError.InstanceRegistryUnavailable
                    ? RestoreArchiveFailureReason.RegistryUnavailable
                    : blockedReason is InstanceStorageError.BuildInstalling or InstanceStorageError.BuildRunning
                        or InstanceStorageError.BuildFilesBusy
                        ? RestoreArchiveFailureReason.BuildBusy
                        : RestoreArchiveFailureReason.BuildUnavailable,
                "The selected build is unavailable or busy.");
        }

        using (lease)
        {
            var target = _store.Instances.FirstOrDefault(instance => instance.Id == targetInstanceID);
            if (target is null || target.State != InstanceInstallationState.Ready)
            {
                return RestoreArchiveCommitResult.Failed(RestoreArchiveFailureReason.BuildUnavailable,
                    "Choose a ready build for the world.");
            }

            ValidateWorldName(name);
            ValidateWorldFolderName(folderName);
            ValidateStageDirectory(staged.DirectoryPath);
            var instanceRoot = _store.GetInstanceDirectory(targetInstanceID);
            var minecraftPath = InstanceStorage.ContainedPath("minecraft", instanceRoot);
            Directory.CreateDirectory(minecraftPath);
            var savesPath = InstanceStorage.ContainedPath("minecraft/saves", instanceRoot);
            Directory.CreateDirectory(savesPath);
            var stageWorld = InstanceStorage.ContainedPath("world", staged.DirectoryPath);
            var stageAttributes = File.GetAttributes(stageWorld);
            if ((stageAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device))
                != FileAttributes.Directory)
            {
                throw Failure(RestoreArchiveFailureReason.InvalidWorld, "The staged world folder is invalid.");
            }

            _ = InstanceFileTree.Snapshot(stageWorld, null, cancellationToken);
            RenameWorldMetadata(stageWorld, name, cancellationToken);

            var requestedFolderName = replace ? folderName : FindAvailableWorldFolderName(savesPath, folderName);
            var folderKey = WorldFolderKey(requestedFolderName);
            var existingName = replace
                ? Directory.EnumerateFileSystemEntries(savesPath)
                    .Select(Path.GetFileName)
                    .FirstOrDefault(item => string.Equals(WorldFolderKey(item!), folderKey, StringComparison.Ordinal))
                : null;
            if (existingName is null && replace)
            {
                return RestoreArchiveCommitResult.Failed(RestoreArchiveFailureReason.DestinationConflict,
                    "There is no world folder to replace.");
            }

            var destinationName = existingName ?? requestedFolderName;
            var destination = InstanceStorage.ContainedPath(destinationName, savesPath);
            if (existingName is not null)
            {
                try { _ = InstanceWorlds.WorldFolder(existingName, instanceRoot); }
                catch (InstanceWorldException exception)
                {
                    throw Failure(exception.Error == InstanceWorldFailureReason.ReparsePointNotAllowed
                        ? RestoreArchiveFailureReason.UnsafePath
                        : RestoreArchiveFailureReason.DestinationConflict,
                        "The existing world folder cannot be replaced.", exception);
                }
            }

            var retired = Path.Combine(_store.GameRoot, ".hako-retired-" + Guid.NewGuid().ToString("N"));
            var oldMoved = false;
            var newMoved = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (existingName is not null)
                {
                    _store.MoveRestoreDirectory(destination, retired);
                    oldMoved = true;
                }

                try { _store.MoveRestoreDirectory(stageWorld, destination); }
                catch (IOException) when (Directory.Exists(destination) || File.Exists(destination))
                {
                    throw Failure(RestoreArchiveFailureReason.DestinationConflict,
                        "The world destination became occupied.");
                }
                newMoved = true;
                _ = InstanceWorlds.WorldFolder(destinationName, instanceRoot);
            }
            catch (Exception operationFailure)
            {
                List<Exception>? rollbackFailures = null;
                if (newMoved)
                {
                    try { _store.MoveRestoreDirectory(destination, stageWorld); }
                    catch (Exception rollbackFailure) { rollbackFailures = [rollbackFailure]; }
                }
                if (oldMoved)
                {
                    try { _store.MoveRestoreDirectory(retired, destination); }
                    catch (Exception rollbackFailure)
                    {
                        rollbackFailures ??= [];
                        rollbackFailures.Add(rollbackFailure);
                    }
                }

                if (rollbackFailures is not null)
                {
                    rollbackFailures.Insert(0, operationFailure);
                    var recoveryPaths = new[] { stageWorld, destination, retired }
                        .Where(path => Directory.Exists(path) || File.Exists(path));
                    return RestoreArchiveCommitResult.Failed(RestoreArchiveFailureReason.RollbackFailed,
                        $"The world restore failed and rollback was incomplete. Recovery data remains at: {string.Join(", ", recoveryPaths)}.");
                }

                return RestoreArchiveCommitResult.Failed(MapWorldOperationFailure(operationFailure),
                    "The world could not be restored.");
            }

            var cleanupWarning = false;
            if (oldMoved)
            {
                var cleanupPath = retired;
                try { cleanupPath = _store.MarkRetiredRestoreDirectory(retired); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Debug.WriteLine($"Could not mark retired world folder: {exception.GetType().Name}");
                }
                try { _store.DeleteRetiredRestoreDirectory(cleanupPath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    cleanupWarning = true;
                }
            }
            try { InstanceStorage.DeleteCreatedDirectory(staged.DirectoryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                cleanupWarning = true;
            }

            return RestoreArchiveCommitResult.WorldSuccess(destinationName, cleanupWarning);
        }
    }

    private static void ValidateWorldName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl))
        {
            throw Failure(RestoreArchiveFailureReason.InvalidName, "Enter a valid world name.");
        }
    }

    private static void ValidateWorldFolderName(string folderName)
    {
        if (folderName.Length is < 1 or > 240 || !InstanceStorage.IsSinglePathSegment(folderName))
        {
            throw Failure(RestoreArchiveFailureReason.InvalidName, "Enter a valid world folder name.");
        }
    }

    private static string WorldFolderKey(string folderName) =>
        folderName.Normalize(NormalizationForm.FormC).ToUpperInvariant();

    private static string FindAvailableWorldFolderName(string savesPath, string requestedName)
    {
        var existingNames = Directory.EnumerateFileSystemEntries(savesPath)
            .Select(Path.GetFileName)
            .Select(name => WorldFolderKey(name!))
            .ToHashSet(StringComparer.Ordinal);

        if (!existingNames.Contains(WorldFolderKey(requestedName))) return requestedName;
        for (var number = 2; number <= 999; number++)
        {
            var suffix = " " + number;
            var candidate = requestedName[..Math.Min(requestedName.Length, 240 - suffix.Length)].TrimEnd() + suffix;
            if (InstanceStorage.IsSinglePathSegment(candidate) && !existingNames.Contains(WorldFolderKey(candidate))) return candidate;
        }

        throw Failure(RestoreArchiveFailureReason.DestinationConflict,
            "No unique world folder name is available.");
    }

    private static void RenameWorldMetadata(string worldDirectory, string name, CancellationToken cancellationToken)
    {
        var renamedCount = 0;
        foreach (var filename in new[] { "level.dat", "level.dat_old" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = InstanceStorage.ContainedPath(filename, worldDirectory);
            if (!File.Exists(path)) continue;
            if (!IsOrdinaryFile(path) || new FileInfo(path).Length > MaximumMetadataSize)
            {
                throw Failure(RestoreArchiveFailureReason.InvalidWorld, "World metadata is unsafe or too large.");
            }

            try
            {
                var compressed = File.ReadAllBytes(path);
                var renamed = WorldMetadata.Renamed(compressed, name, cancellationToken);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        output.Write(renamed);
                        output.Flush(flushToDisk: true);
                    }
                    File.Move(temporary, path, overwrite: true);
                }
                finally
                {
                    try { File.Delete(temporary); }
                    catch (FileNotFoundException) { }
                }

                renamedCount++;
            }
            catch (WorldMetadataException exception)
            {
                throw Failure(RestoreArchiveFailureReason.InvalidWorld, "World metadata could not be renamed.", exception);
            }
        }

        if (renamedCount == 0)
        {
            throw Failure(RestoreArchiveFailureReason.InvalidWorld, "World metadata is missing.");
        }
    }

    private static RestoreArchiveFailureReason MapWorldOperationFailure(Exception exception) => exception switch
    {
        RestoreArchiveException restore => restore.Reason,
        InstanceStorageException storage => MapStoreFailure(storage),
        InstanceWorldException world when world.Error == InstanceWorldFailureReason.ReparsePointNotAllowed =>
            RestoreArchiveFailureReason.UnsafePath,
        InstanceWorldException => RestoreArchiveFailureReason.DestinationConflict,
        InstanceFileTreeReparseException => RestoreArchiveFailureReason.UnsafePath,
        OperationCanceledException => RestoreArchiveFailureReason.Cancelled,
        _ => RestoreArchiveFailureReason.OperationFailed
    };

    private static RestoreArchiveFailureReason MapStoreFailure(InstanceStorageException exception) => exception.Error switch
    {
        InstanceStorageError.InvalidBuildName or InstanceStorageError.ReservedBuildName => RestoreArchiveFailureReason.InvalidName,
        InstanceStorageError.DuplicateBuildName or InstanceStorageError.BuildFolderExists => RestoreArchiveFailureReason.DestinationConflict,
        InstanceStorageError.BuildInstalling or InstanceStorageError.BuildRunning or InstanceStorageError.BuildFilesBusy => RestoreArchiveFailureReason.BuildBusy,
        InstanceStorageError.InstanceRegistryNotLoaded or InstanceStorageError.InstanceRegistryUnavailable => RestoreArchiveFailureReason.RegistryUnavailable,
        InstanceStorageError.InvalidPath or InstanceStorageError.PathTraversal or InstanceStorageError.ReparsePointNotAllowed => RestoreArchiveFailureReason.UnsafePath,
        _ => RestoreArchiveFailureReason.OperationFailed
    };

    private void ValidateStageDirectory(string stagePath)
    {
        var fullPath = Path.GetFullPath(stagePath);
        var name = Path.GetFileName(fullPath);
        if (!string.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(_store.GameRoot), StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(StagePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name[StagePrefix.Length..], "N", out _))
        {
            throw new IOException("The restore staging directory is invalid.");
        }

        var attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device))
            != FileAttributes.Directory)
        {
            throw new IOException("The restore staging directory is unsafe.");
        }
    }

    private static List<ArchiveEntry> ValidateArchiveEntries(
        ZipArchive archive,
        ZipArchiveScanResult scan,
        RestoreArchiveKind kind)
    {
        var paths = new RestoreArchivePathRegistry();
        var entries = new List<ArchiveEntry>(archive.Entries.Count);
        var manifests = 0;
        for (var index = 0; index < archive.Entries.Count; index++)
        {
            var entry = archive.Entries[index];
            var record = scan.Entries[index];
            if (entry.Length != record.Length || entry.CompressedLength != record.CompressedLength)
            {
                throw Failure(RestoreArchiveFailureReason.InvalidArchive, "The backup archive is damaged.");
            }

            var normalized = ValidateArchivePath(record.Name, record.IsDirectory);
            if (string.Equals(normalized, "data.json", StringComparison.Ordinal)
                && !record.IsDirectory && !record.IsSymbolicLink) manifests++;
            if (kind == RestoreArchiveKind.World
                && !string.Equals(normalized, "data.json", StringComparison.Ordinal)
                && !normalized.StartsWith("world/", StringComparison.Ordinal)
                && !string.Equals(normalized, "world", StringComparison.Ordinal))
            {
                throw Failure(RestoreArchiveFailureReason.UnsafePath, "World backups may contain only the world folder and its manifest.");
            }

            if (record.IsSymbolicLink && (kind != RestoreArchiveKind.Instance || !IsUnderJava(normalized)))
            {
                throw Failure(RestoreArchiveFailureReason.UnsafePath, "The archive contains a symbolic link outside Java.");
            }

            if (record.IsDirectory)
            {
                if (record.Length != 0) throw Failure(RestoreArchiveFailureReason.InvalidArchive, "The backup archive is damaged.");
                paths.AddDirectory(normalized, explicitEntry: true);
            }
            else
            {
                paths.AddFile(normalized);
            }

            entries.Add(new ArchiveEntry(entry, record with { Name = normalized }));
        }

        if (manifests != 1)
        {
            throw Failure(RestoreArchiveFailureReason.InvalidManifest, "The backup must contain exactly one root data.json manifest.");
        }

        return entries;
    }

    private static void ValidateInstanceManifest(InstanceBackupManifest manifest)
    {
        if (manifest.Profile is null || manifest.Mods is null
            || manifest.FormatVersion != InstanceBackupManifest.CurrentFormat
            || string.IsNullOrWhiteSpace(manifest.Name)
            || string.IsNullOrWhiteSpace(manifest.FolderName)
            || string.IsNullOrWhiteSpace(manifest.MinecraftVersion)
            || !VersionIdPattern.IsMatch(manifest.MinecraftVersion)
            || manifest.ModLoader is not ("vanilla" or "fabric")
            || (manifest.ModLoader == "fabric") != (manifest.LoaderVersion is not null)
            || manifest.ModLoader == "fabric" && string.IsNullOrWhiteSpace(manifest.LoaderVersion)
            || manifest.JavaMajorVersion is < 1 or > 99
            || string.IsNullOrWhiteSpace(manifest.Profile.MetadataSHA1)
            || !Sha1Pattern.IsMatch(manifest.Profile.MetadataSHA1)
            || !IsHttps(manifest.Profile.MetadataURL)
            || string.IsNullOrWhiteSpace(manifest.Profile.JavaExecutable)
            || !IsSafeRelativePath(manifest.Profile.JavaExecutable)
            || manifest.Profile.OfflineUsername is null
            || manifest.Profile.Parameters is null
            || manifest.Profile.Parameters.JavaArguments is null
            || manifest.Profile.Parameters.MinecraftArguments is null
            || manifest.Profile.Parameters.WindowWidth < 1 || manifest.Profile.Parameters.WindowHeight < 1
            || manifest.Profile.Parameters.MaximumMemoryMiB < 128
            || (manifest.Profile.OfflineMode && !OfflineUsername.IsValid(manifest.Profile.OfflineUsername))
            || manifest.ModCount != manifest.Mods.Count)
        {
            throw Failure(RestoreArchiveFailureReason.InvalidManifest, "The instance backup manifest is invalid.");
        }

        string expectedFolder;
        try { expectedFolder = InstanceStore.ValidateArchiveName(manifest.Name); }
        catch (InstanceStorageException exception)
        {
            throw Failure(RestoreArchiveFailureReason.InvalidManifest, "The instance name in the backup manifest is invalid.", exception);
        }
        if (!string.Equals(expectedFolder, manifest.FolderName, StringComparison.Ordinal))
        {
            throw Failure(RestoreArchiveFailureReason.InvalidManifest, "The instance folder does not match its name.");
        }

        var profileFabric = manifest.Profile.FabricConfiguration;
        if (manifest.ModLoader == "fabric")
        {
            if (profileFabric is null
                || !string.Equals(profileFabric.LoaderVersion, manifest.LoaderVersion, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(manifest.Profile.FabricProfileSHA1)
                || !Sha1Pattern.IsMatch(manifest.Profile.FabricProfileSHA1 ?? ""))
            {
                throw Failure(RestoreArchiveFailureReason.InvalidManifest, "The Fabric profile does not match the backup manifest.");
            }

            if (profileFabric.Api is { } api
                && (api.ProjectID != "P7dR8mSH"
                    || !IsSafeFilename(api.Filename, mustEndWithJar: true)
                    || api.Size <= 0
                    || string.IsNullOrWhiteSpace(api.Sha1)
                    || !Sha1Pattern.IsMatch(api.Sha1)
                    || string.IsNullOrWhiteSpace(api.Sha512)
                    || !Sha512Pattern.IsMatch(api.Sha512)
                    || string.IsNullOrWhiteSpace(api.Url)
                    || !IsHttps(api.Url)))
            {
                throw Failure(RestoreArchiveFailureReason.InvalidManifest, "The Fabric API description is invalid.");
            }
        }
        else if (profileFabric is not null || manifest.Profile.FabricProfileSHA1 is not null)
        {
            throw Failure(RestoreArchiveFailureReason.InvalidManifest, "The Vanilla backup contains Fabric data.");
        }

        var modNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in manifest.Mods)
        {
            if (mod is null || string.IsNullOrWhiteSpace(mod.File))
            {
                throw Failure(RestoreArchiveFailureReason.InvalidManifest, "The mod list in the backup manifest is invalid.");
            }

            var name = mod.File.Normalize(NormalizationForm.FormC);
            if (!IsSafeFilename(name, mustEndWithJar: true) || !modNames.Add(name)
                || mod.Source is not ("local" or "modrinth" or "curseForge")
                || mod.Source == "modrinth" && (string.IsNullOrWhiteSpace(mod.ProjectID) || string.IsNullOrWhiteSpace(mod.VersionID)))
            {
                throw Failure(RestoreArchiveFailureReason.InvalidManifest, "The mod list in the backup manifest is invalid.");
            }
        }
    }

    private static void ValidateWorldManifest(WorldBackupManifest manifest)
    {
        if (manifest.SourceInstance is null || manifest.Datapacks is null
            || manifest.FormatVersion != WorldBackupManifest.CurrentFormat
            || manifest.WorldPath != "world"
            || string.IsNullOrWhiteSpace(manifest.Name)
            || !IsValidWorldFolder(manifest.FolderName)
            || manifest.SourceInstance.Id == Guid.Empty
            || string.IsNullOrWhiteSpace(manifest.SourceInstance.Name)
            || !IsValidWorldFolder(manifest.SourceInstance.FolderName)
            || !VersionIdPattern.IsMatch(manifest.SourceInstance.MinecraftVersion)
            || manifest.SourceInstance.ModLoader is not ("vanilla" or "fabric")
            || (manifest.SourceInstance.ModLoader == "fabric") != !string.IsNullOrWhiteSpace(manifest.SourceInstance.LoaderVersion))
        {
            throw Failure(RestoreArchiveFailureReason.InvalidManifest, "The world backup manifest is invalid.");
        }

        var datapackPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var datapack in manifest.Datapacks)
        {
            if (datapack is null || string.IsNullOrWhiteSpace(datapack.Path) || string.IsNullOrWhiteSpace(datapack.File))
            {
                throw Failure(RestoreArchiveFailureReason.InvalidManifest, "The datapack list in the world manifest is invalid.");
            }

            var path = datapack.Path.Normalize(NormalizationForm.FormC);
            if (!(path.StartsWith("datapacks/", StringComparison.Ordinal)
                  || path.StartsWith(".hako-disabled-datapacks/", StringComparison.Ordinal))
                || path.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..")
                || !string.Equals(Path.GetFileName(path), datapack.File.Normalize(NormalizationForm.FormC), StringComparison.Ordinal)
                || !datapackPaths.Add(path)
                || (datapack.IsDirectory && datapack.Sha512 is not null)
                || (!datapack.IsDirectory && datapack.Sha512 is not null && !Sha512Pattern.IsMatch(datapack.Sha512)))
            {
                throw Failure(RestoreArchiveFailureReason.InvalidManifest, "The datapack list in the world manifest is invalid.");
            }
        }
    }

    private static T Deserialize<T>(byte[] data, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        RestoreArchiveFailureReason reason) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(data, typeInfo)
                ?? throw Failure(reason, "The backup manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw Failure(reason, "The backup manifest is invalid.", exception);
        }
    }

    private static async Task<byte[]> ReadEntryAsync(
        ZipArchiveEntry entry,
        ZipArchiveEntryRecord record,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream((int)Math.Min(record.Length, maximumBytes));
        await CopyEntryAsync(entry, record, output, maximumBytes, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    private static async Task<byte[]?> CopyEntryAsync(
        ZipArchiveEntry entry,
        ZipArchiveEntryRecord record,
        Stream? output,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        MemoryStream? linkTarget = null;
        if (record.IsSymbolicLink)
        {
            if (record.Length > 32_760) throw Failure(RestoreArchiveFailureReason.UnsafePath, "A Java link target is too long.");
            linkTarget = new MemoryStream((int)record.Length);
        }

        try
        {
            await using var source = entry.Open();
            var buffer = new byte[81920];
            long total = 0;
            var crc = uint.MaxValue;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total = checked(total + read);
                if (total > maximumBytes || total > record.Length)
                {
                    throw Failure(RestoreArchiveFailureReason.InvalidArchive, "An archive entry exceeds its declared size.");
                }

                crc = ZipArchiveSupport.UpdateCrc32(crc, buffer.AsSpan(0, read));
                if (output is not null)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                else if (linkTarget is not null)
                {
                    await linkTarget.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            if (total != record.Length || ZipArchiveSupport.FinishCrc32(crc) != record.Crc32)
            {
                throw Failure(RestoreArchiveFailureReason.InvalidArchive, "An archive entry failed its size or CRC check.");
            }

            if (output is not null) await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return linkTarget?.ToArray();
        }
        finally
        {
            linkTarget?.Dispose();
        }
    }

    private async Task<string?> ValidateExtractedInstanceAsync(
        string root,
        InstanceBackupManifest manifest,
        GameArchitecture? architecture,
        int? javaMajorVersion,
        CancellationToken cancellationToken)
    {
        ValidateBuildIcon(root, manifest);
        await ValidateModsAsync(root, manifest, cancellationToken).ConfigureAwait(false);
        var fabricProfile = await ReadAndValidateFabricProfileAsync(root, manifest, cancellationToken).ConfigureAwait(false);
        await ValidateFabricApiAsync(root, manifest, cancellationToken).ConfigureAwait(false);
        var modpackVersionID = await ReadAndValidateModpackPlanAsync(root, manifest, cancellationToken).ConfigureAwait(false);

        var versionPath = InstanceStorage.ContainedPath(
            $"minecraft/versions/{manifest.MinecraftVersion}/{manifest.MinecraftVersion}.json", root);
        var jarPath = InstanceStorage.ContainedPath(
            $"minecraft/versions/{manifest.MinecraftVersion}/{manifest.MinecraftVersion}.jar", root);
        var versionInfo = new FileInfo(versionPath);
        if (!IsOrdinaryFile(versionPath) || versionInfo.Length is <= 0 or > MaximumMetadataSize
            || !IsOrdinaryFile(jarPath)
            || !await MinecraftInstaller.IsValidFileAsync(versionPath,
                new MojangDownload { Sha1 = manifest.Profile.MetadataSHA1, Size = versionInfo.Length }, cancellationToken)
                .ConfigureAwait(false))
        {
            throw Failure(RestoreArchiveFailureReason.InvalidInstance,
                "Minecraft version metadata is missing or does not match the backup.");
        }

        MojangVersionMetadata minecraft;
        try
        {
            await using var input = new FileStream(versionPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            minecraft = await JsonSerializer.DeserializeAsync(input, MojangJsonContext.Default.MojangVersionMetadata,
                cancellationToken).ConfigureAwait(false)
                ?? throw Failure(RestoreArchiveFailureReason.InvalidInstance, "Minecraft version metadata is invalid.");
        }
        catch (JsonException exception)
        {
            throw Failure(RestoreArchiveFailureReason.InvalidInstance, "Minecraft version metadata is invalid.", exception);
        }

        if (minecraft.Id != manifest.MinecraftVersion
            || minecraft.LegacyTexturepacks != manifest.Profile.LegacyTexturepacks
            || !minecraft.Downloads.TryGetValue("client", out var client)
            || !await MinecraftInstaller.IsValidFileAsync(jarPath, client, cancellationToken).ConfigureAwait(false))
        {
            throw Failure(RestoreArchiveFailureReason.InvalidInstance,
                "Minecraft version metadata or client JAR does not match the backup.");
        }

        if (architecture is not null && javaMajorVersion < Math.Max(manifest.JavaMajorVersion, minecraft.Java.MajorVersion))
        {
            throw Failure(RestoreArchiveFailureReason.InvalidInstance, "The Windows Java runtime is older than the required version.");
        }

        if (architecture is null) return modpackVersionID;

        var fabricOverrides = new HashSet<string>(fabricProfile?.Libraries.Select(library => LibraryKey(library.Name))
            ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var library in minecraft.Libraries.Where(library => library.IsAllowed(architecture.Value)))
        {
            if (library.Downloads?.Artifact is { Path: { Length: > 0 } artifactPath } artifact
                && !fabricOverrides.Contains(LibraryKey(library.Name)))
            {
                await ValidateLibraryAsync(root, artifactPath, artifact, cancellationToken).ConfigureAwait(false);
            }

            if (library.Natives?.TryGetValue("windows", out var classifierPattern) == true
                && library.Downloads?.Classifiers is { } classifiers)
            {
                var classifier = classifierPattern.Replace("${arch}", architecture.Value.NativeArchSubstitution(), StringComparison.Ordinal);
                if (classifiers.TryGetValue(classifier, out var native) && native.Path is { Length: > 0 } nativePath)
                {
                    await ValidateLibraryAsync(root, nativePath, native, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (fabricProfile is not null)
        {
            foreach (var library in fabricProfile.Libraries)
            {
                if (!string.IsNullOrWhiteSpace(library.Sha1))
                {
                    await ValidateLibraryAsync(root, library.MavenPath,
                        new MojangDownload { Sha1 = library.Sha1, Size = library.Size }, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return modpackVersionID;
    }

    private static void ValidateBuildIcon(string root, InstanceBackupManifest manifest)
    {
        if (manifest.IconPNG is { } expected)
        {
            if (expected.Length > MaximumIconSize) throw Failure(RestoreArchiveFailureReason.InvalidManifest, "The build icon is too large.");
            var iconPath = InstanceStorage.ContainedPath("icon.png", root);
            if (!IsOrdinaryFile(iconPath) || new FileInfo(iconPath).Length != expected.Length
                || !File.ReadAllBytes(iconPath).AsSpan().SequenceEqual(expected))
            {
                throw Failure(RestoreArchiveFailureReason.InvalidInstance, "The build icon does not match its manifest.");
            }
        }
        else if (string.IsNullOrWhiteSpace(manifest.IconSymbol))
        {
            throw Failure(RestoreArchiveFailureReason.InvalidManifest, "The backup has no build icon.");
        }
    }

    private static async Task ValidateModsAsync(
        string root,
        InstanceBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        foreach (var mod in manifest.Mods)
        {
            var name = mod.File.Normalize(NormalizationForm.FormC) + (mod.Enabled ? "" : ".disabled");
            var path = InstanceStorage.ContainedPath("minecraft/mods/" + name, root);
            if (!IsOrdinaryFile(path))
            {
                throw Failure(RestoreArchiveFailureReason.InvalidInstance, "A mod listed in the backup manifest is missing or unsafe.");
            }

            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var header = new byte[4];
            if (await input.ReadAsync(header, cancellationToken).ConfigureAwait(false) != header.Length
                || !header.AsSpan().SequenceEqual("PK\u0003\u0004"u8))
            {
                throw Failure(RestoreArchiveFailureReason.InvalidInstance, "A mod listed in the backup manifest is not a JAR archive.");
            }
        }
    }

    private static async Task<FabricProfile?> ReadAndValidateFabricProfileAsync(
        string root,
        InstanceBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        if (manifest.ModLoader != "fabric") return null;
        var path = InstanceStorage.ContainedPath("minecraft/.hako-fabric.json", root);
        var size = IsOrdinaryFile(path) ? new FileInfo(path).Length : 0;
        if (size is <= 0 or > MaximumMetadataSize
            || !await MinecraftInstaller.IsValidFileAsync(path,
                new MojangDownload { Sha1 = manifest.Profile.FabricProfileSHA1 ?? "", Size = size }, cancellationToken)
                .ConfigureAwait(false))
        {
            throw Failure(RestoreArchiveFailureReason.InvalidInstance, "The Fabric profile is missing or damaged.");
        }

        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var profile = await JsonSerializer.DeserializeAsync(input, HakoJsonContext.Default.FabricProfile,
                cancellationToken).ConfigureAwait(false)
                ?? throw Failure(RestoreArchiveFailureReason.InvalidInstance, "The Fabric profile is invalid.");
            profile.Validate(manifest.MinecraftVersion, manifest.LoaderVersion!);
            return profile;
        }
        catch (JsonException exception)
        {
            throw Failure(RestoreArchiveFailureReason.InvalidInstance, "The Fabric profile is invalid.", exception);
        }
        catch (MojangException exception)
        {
            throw Failure(RestoreArchiveFailureReason.InvalidInstance, "The Fabric profile does not match the backup.", exception);
        }
    }

    private static async Task ValidateFabricApiAsync(
        string root,
        InstanceBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        if (manifest.Profile.FabricConfiguration?.Api is not { } api) return;
        var mod = manifest.Mods.FirstOrDefault(item => string.Equals(item.File, api.Filename, StringComparison.OrdinalIgnoreCase))
            ?? throw Failure(RestoreArchiveFailureReason.InvalidInstance, "Fabric API is missing from the mod list.");
        var modFile = InstanceStorage.ContainedPath(
            "minecraft/mods/" + mod.File.Normalize(NormalizationForm.FormC) + (mod.Enabled ? "" : ".disabled"), root);
        if (!IsOrdinaryFile(modFile)
            || !await MinecraftInstaller.IsValidFileAsync(modFile,
                new MojangDownload { Sha1 = api.Sha1, Size = api.Size }, cancellationToken).ConfigureAwait(false))
        {
            throw Failure(RestoreArchiveFailureReason.InvalidInstance, "Fabric API does not match the backup manifest.");
        }

        try { await FabricClient.ValidateApiFileAsync(modFile, api, cancellationToken).ConfigureAwait(false); }
        catch (MojangException exception)
        {
            throw Failure(RestoreArchiveFailureReason.InvalidInstance, "Fabric API does not match the backup manifest.", exception);
        }
    }

    private static async Task<string?> ReadAndValidateModpackPlanAsync(
        string root,
        InstanceBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        var path = InstanceStorage.ContainedPath(ModpackPlan.Filename, root);
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) throw Failure(RestoreArchiveFailureReason.InvalidInstance, "The modpack plan is invalid.");
            return null;
        }

        var size = new FileInfo(path).Length;
        if (!IsOrdinaryFile(path) || size is <= 0 or > MaximumMetadataSize)
        {
            throw Failure(RestoreArchiveFailureReason.InvalidInstance, "The modpack plan is missing or too large.");
        }

        ModpackPlan plan;
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            plan = await JsonSerializer.DeserializeAsync(input, HakoJsonContext.Default.ModpackPlan,
                cancellationToken).ConfigureAwait(false)
                ?? throw Failure(RestoreArchiveFailureReason.InvalidInstance, "The modpack plan is invalid.");
            plan.Validate();
        }
        catch (JsonException exception)
        {
            throw Failure(RestoreArchiveFailureReason.InvalidInstance, "The modpack plan is invalid.", exception);
        }
        catch (ModpackException exception)
        {
            throw Failure(RestoreArchiveFailureReason.InvalidInstance, "The modpack plan is invalid.", exception);
        }

        if (!plan.Completed || plan.Manifest.Minecraft != manifest.MinecraftVersion
            || !string.Equals(plan.Manifest.LoaderVersion, manifest.LoaderVersion, StringComparison.Ordinal)
            || (plan.Manifest.LoaderVersion is null) != (manifest.ModLoader == "vanilla"))
        {
            throw Failure(RestoreArchiveFailureReason.InvalidInstance, "The completed modpack plan does not match the build profile.");
        }

        return plan.VersionId;
    }

    private static async Task ValidateLibraryAsync(
        string root,
        string relativePath,
        MojangDownload download,
        CancellationToken cancellationToken)
    {
        if (!IsSafeRelativePath(relativePath))
        {
            throw Failure(RestoreArchiveFailureReason.InvalidInstance, "A Minecraft library path is invalid.");
        }

        var path = InstanceStorage.ContainedPath("minecraft/libraries/" + relativePath, root);
        if (!IsOrdinaryFile(path)
            || !await MinecraftInstaller.IsValidFileAsync(path, download, cancellationToken).ConfigureAwait(false))
        {
            throw Failure(RestoreArchiveFailureReason.InvalidInstance, "A Minecraft or Fabric library is missing or damaged.");
        }
    }

    private static async Task ValidateExtractedWorldAsync(
        string root,
        WorldBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        var world = InstanceStorage.ContainedPath(manifest.WorldPath, root);
        WorldMetadata? authoritative = null;
        foreach (var file in new[] { "level.dat", "level.dat_old" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = InstanceStorage.ContainedPath(file, world);
            if (!File.Exists(path)) continue;
            if (!IsOrdinaryFile(path) || new FileInfo(path).Length > MaximumMetadataSize)
            {
                throw Failure(RestoreArchiveFailureReason.InvalidWorld, "World metadata is missing or too large.");
            }

            WorldMetadata metadata;
            try
            {
                metadata = await Task.Run(() => WorldMetadata.ReadFile(path, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WorldMetadataException exception)
            {
                throw Failure(RestoreArchiveFailureReason.InvalidWorld, "World metadata is invalid.", exception);
            }
            if (!string.Equals(metadata.Name, manifest.Name, StringComparison.Ordinal))
            {
                throw Failure(RestoreArchiveFailureReason.InvalidWorld, "The world name does not match its manifest.");
            }

            authoritative ??= metadata;
            if (file == "level.dat") authoritative = metadata;
        }

        if (authoritative is null
            || !string.Equals(authoritative.Version, manifest.SaveVersion, StringComparison.Ordinal)
            || authoritative.GameType != manifest.GameType
            || !SameDate(authoritative.LastPlayed, manifest.LastPlayed))
        {
            throw Failure(RestoreArchiveFailureReason.InvalidWorld, "World metadata does not match its manifest.");
        }

        foreach (var datapack in manifest.Datapacks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = InstanceStorage.ContainedPath(datapack.Path.Normalize(NormalizationForm.FormC), world);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                throw Failure(RestoreArchiveFailureReason.InvalidWorld, "A datapack listed in the world manifest is missing.", exception);
            }
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                || datapack.IsDirectory != ((attributes & FileAttributes.Directory) != 0))
            {
                throw Failure(RestoreArchiveFailureReason.InvalidWorld, "A datapack is missing or has the wrong file type.");
            }

            if (!datapack.IsDirectory && datapack.Sha512 is { } expectedHash)
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = await SHA512.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(Convert.ToHexString(hash), expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw Failure(RestoreArchiveFailureReason.InvalidWorld, "A datapack does not match its manifest.");
                }
            }
        }

        if (manifest.IconPNG is { } icon)
        {
            var iconPath = InstanceStorage.ContainedPath("icon.png", world);
            if (icon.Length > MaximumIconSize || !IsOrdinaryFile(iconPath)
                || new FileInfo(iconPath).Length != icon.Length
                || !File.ReadAllBytes(iconPath).AsSpan().SequenceEqual(icon))
            {
                throw Failure(RestoreArchiveFailureReason.InvalidWorld, "The world icon does not match its manifest.");
            }
        }
    }

    private JavaRuntimeInfo? InspectJava(string root, InstanceBackupManifest manifest)
    {
        if (!IsWindowsExecutable(manifest.Profile.JavaExecutable)) return null;
        var executablePath = InstanceStorage.ContainedPath("java/" + manifest.Profile.JavaExecutable, root);
        if (!IsOrdinaryFile(executablePath) || ReadPeArchitecture(executablePath) is not { } architecture)
        {
            return null;
        }

        var host = _hostArchitecture.GetHostArchitecture();
        if (!IsSupportedByHost(architecture, host)) return null;

        var releasePath = InstanceStorage.ContainedPath("java/release", root);
        if (!IsOrdinaryFile(releasePath) || new FileInfo(releasePath).Length > 128 * 1024)
        {
            throw Failure(RestoreArchiveFailureReason.InvalidInstance, "The Windows Java runtime release file is missing or invalid.");
        }

        var releaseText = File.ReadAllText(releasePath);
        var versionLine = Regex.Match(releaseText, "(?m)^JAVA_VERSION=\"([^\"]+)\"$");
        if (!versionLine.Success || !TryJavaMajor(versionLine.Groups[1].Value, out var major)
            || major < manifest.JavaMajorVersion)
        {
            throw Failure(RestoreArchiveFailureReason.InvalidInstance, "The Windows Java runtime version is invalid or too old.");
        }

        return new JavaRuntimeInfo(architecture, host, major);
    }

    private static GameArchitecture? ReadPeArchitecture(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64, FileOptions.SequentialScan);
        if (stream.Length < 64) return null;
        Span<byte> dosHeader = stackalloc byte[64];
        stream.ReadExactly(dosHeader);
        if (dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z') return null;
        var peOffset = BinaryPrimitives.ReadUInt32LittleEndian(dosHeader[0x3c..]);
        if (peOffset > stream.Length - 6) return null;
        stream.Position = peOffset;
        Span<byte> header = stackalloc byte[6];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("PE\0\0"u8)) return null;
        return BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) switch
        {
            0x8664 => GameArchitecture.X64,
            0xAA64 => GameArchitecture.Arm64,
            0x014C => GameArchitecture.X86,
            _ => null
        };
    }

    private static bool IsSupportedByHost(GameArchitecture architecture, HostArchitecture host) => host.Machine switch
    {
        HostMachineArchitecture.X86 => architecture == GameArchitecture.X86,
        HostMachineArchitecture.X64 => architecture is GameArchitecture.X64 or GameArchitecture.X86,
        HostMachineArchitecture.Arm64 => architecture is GameArchitecture.Arm64 or GameArchitecture.X86
            || architecture == GameArchitecture.X64 && host.Amd64UserEnabled,
        _ => false
    };

    private static bool TryJavaMajor(string version, out int major)
    {
        var match = Regex.Match(version, "^(?:1\\.)?(?<major>[0-9]+)");
        return int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out major);
    }

    private static void DeleteJavaDirectory(string root)
    {
        var javaDirectory = Path.Combine(root, "java");
        if (Directory.Exists(javaDirectory)) InstanceStorage.DeleteCreatedDirectory(javaDirectory);
        else if (File.Exists(javaDirectory)) File.Delete(javaDirectory);
    }

    private GameInstance? FindMatchingInstance(InstanceBackupManifest manifest)
    {
        if (manifest.InstanceID is { } id)
        {
            return _store.Instances.FirstOrDefault(instance => instance.Id == id);
        }

        var matches = _store.Instances.Where(instance =>
            string.Equals(instance.FolderName, manifest.FolderName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(instance.VersionID, manifest.MinecraftVersion, StringComparison.Ordinal)
            && string.Equals(instance.ModLoader == InstanceModLoader.Fabric ? "fabric" : "vanilla",
                manifest.ModLoader, StringComparison.Ordinal)
            && string.Equals(instance.FabricConfiguration?.LoaderVersion, manifest.LoaderVersion, StringComparison.Ordinal))
            .Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private GameInstance? FindMatchingWorldTarget(WorldBackupManifest manifest)
    {
        var ready = _store.Instances.Where(instance => instance.State == InstanceInstallationState.Ready).ToArray();
        var exact = ready.FirstOrDefault(instance => instance.Id == manifest.SourceInstance.Id);
        if (exact is not null) return exact;

        var matches = ready.Where(instance =>
            string.Equals(instance.FolderName, manifest.SourceInstance.FolderName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(instance.VersionID, manifest.SourceInstance.MinecraftVersion, StringComparison.Ordinal)
            && string.Equals(instance.ModLoader == InstanceModLoader.Fabric ? "fabric" : "vanilla",
                manifest.SourceInstance.ModLoader, StringComparison.Ordinal)
            && string.Equals(instance.FabricConfiguration?.LoaderVersion, manifest.SourceInstance.LoaderVersion,
                StringComparison.Ordinal)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static void ValidateJavaLink(string archivePath, byte[] linkData)
    {
        string target;
        try { target = new UTF8Encoding(false, true).GetString(linkData); }
        catch (DecoderFallbackException exception)
        {
            throw Failure(RestoreArchiveFailureReason.UnsafePath, "A Java symbolic link target is invalid.", exception);
        }

        if (target.Length == 0 || target.StartsWith('/') || target.StartsWith('\\')
            || target.Contains('\\') || target.Contains(':') || target.Contains('\0')
            || target.Any(char.IsControl))
        {
            throw Failure(RestoreArchiveFailureReason.UnsafePath, "A Java symbolic link target is unsafe.");
        }

        var parent = archivePath.TrimEnd('/').Split('/').Skip(1).SkipLast(1).ToList();
        foreach (var segment in target.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (parent.Count == 0) throw Failure(RestoreArchiveFailureReason.UnsafePath, "A Java symbolic link escapes its folder.");
                parent.RemoveAt(parent.Count - 1);
                continue;
            }

            parent.Add(segment);
        }
    }

    private static string ValidateArchivePath(string path, bool directory)
    {
        if (directory && path.EndsWith("/", StringComparison.Ordinal)) path = path[..^1];
        var normalized = path.Normalize(NormalizationForm.FormC);
        if (normalized.Length is 0 or > 32_760
            || normalized.StartsWith('/') || normalized.StartsWith('\\')
            || normalized.Contains('\\') || normalized.Contains(':') || normalized.Contains('\0')
            || normalized.Any(character => char.IsControl(character) || "<>\"|?*".Contains(character)))
        {
            throw Failure(RestoreArchiveFailureReason.UnsafePath, "The archive contains an unsafe path.");
        }

        var segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length is 0 or > 255
                                    || segment is "." or ".."
                                    || segment.EndsWith('.') || segment.EndsWith(' ')
                                    || ReservedDeviceNames.Contains(DeviceBaseName(segment))))
        {
            throw Failure(RestoreArchiveFailureReason.UnsafePath, "The archive contains an unsafe path.");
        }

        return normalized;
    }

    private static bool IsSafeRelativePath(string path)
    {
        try
        {
            _ = ValidateArchivePath(path, directory: false);
            return true;
        }
        catch (RestoreArchiveException)
        {
            return false;
        }
    }

    private static bool IsSafeFilename(string name, bool mustEndWithJar)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name.Contains(':')
            || name.Contains('\0') || name.Any(char.IsControl) || name.EndsWith('.') || name.EndsWith(' ')
            || name.IndexOfAny("<>\"|?*".ToCharArray()) >= 0 || ReservedDeviceNames.Contains(DeviceBaseName(name)))
        {
            return false;
        }

        return !mustEndWithJar || name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidWorldFolder(string folderName) =>
        !string.IsNullOrWhiteSpace(folderName)
        && folderName.Length <= 240
        && folderName is not ("." or "..")
        && !folderName.Contains('/') && !folderName.Contains('\\') && !folderName.Contains(':')
        && !folderName.Contains('\0') && !folderName.Any(char.IsControl)
        && !folderName.EndsWith('.') && !folderName.EndsWith(' ')
        && !ReservedDeviceNames.Contains(DeviceBaseName(folderName));

    private static bool IsWindowsExecutable(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderJava(string path) =>
        path.Equals("java", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("java/", StringComparison.OrdinalIgnoreCase);

    private static bool IsOrdinaryFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool IsHttps(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo);

    private static bool SameDate(DateTimeOffset? first, DateTimeOffset? second) =>
        first is { } left && second is { } right
            ? Math.Abs((left - right).TotalSeconds) < 1
            : first is null && second is null;

    private static string LibraryKey(string name)
    {
        try { return FabricMavenLibrary.Parse(name).Key; }
        catch (ArgumentException) { return name; }
    }

    private static string DeviceBaseName(string segment)
    {
        var dot = segment.IndexOf('.');
        return (dot < 0 ? segment : segment[..dot]).ToUpperInvariant();
    }

    private static HashSet<string> CreateReservedDeviceNames()
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
        foreach (var number in Enumerable.Range(1, 9))
        {
            values.Add($"COM{number}");
            values.Add($"LPT{number}");
        }

        foreach (var number in new[] { "¹", "²", "³" })
        {
            values.Add("COM" + number);
            values.Add("LPT" + number);
        }

        return values;
    }

    private static long GetAvailableSpace(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root)) return 0;
        return new DriveInfo(root).AvailableFreeSpace;
    }

    private static RestoreArchiveException Failure(
        RestoreArchiveFailureReason reason,
        string diagnostic,
        Exception? innerException = null) => new(reason, diagnostic, innerException);

    private sealed record ArchiveEntry(ZipArchiveEntry Entry, ZipArchiveEntryRecord Record);
    private sealed record JavaRuntimeInfo(GameArchitecture Architecture, HostArchitecture Host, int JavaMajorVersion);
}

internal sealed class RestoreArchiveException(
    RestoreArchiveFailureReason reason,
    string message,
    Exception? innerException = null) : IOException(message, innerException)
{
    public RestoreArchiveFailureReason Reason { get; } = reason;
}

internal sealed record RestoreInstanceCommitOutcome(GameInstance Instance, bool CleanupWarning);

internal sealed class RestoreArchivePathRegistry
{
    private readonly Dictionary<string, PathEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public void AddFile(string path)
    {
        var segments = path.Split('/');
        for (var index = 1; index < segments.Length; index++)
        {
            AddDirectory(string.Join('/', segments.Take(index)), explicitEntry: false);
        }

        if (!_entries.TryAdd(path, new PathEntry(IsDirectory: false, Explicit: true)))
        {
            throw new RestoreArchiveException(RestoreArchiveFailureReason.UnsafePath,
                "The archive contains duplicate or conflicting paths.");
        }
    }

    public void AddDirectory(string path, bool explicitEntry)
    {
        var segments = path.Split('/');
        for (var index = 1; index < segments.Length; index++)
        {
            AddDirectory(string.Join('/', segments.Take(index)), explicitEntry: false);
        }

        if (!_entries.TryGetValue(path, out var previous))
        {
            _entries.Add(path, new PathEntry(IsDirectory: true, Explicit: explicitEntry));
            return;
        }

        if (!previous.IsDirectory || explicitEntry && previous.Explicit)
        {
            throw new RestoreArchiveException(RestoreArchiveFailureReason.UnsafePath,
                "The archive contains duplicate or conflicting paths.");
        }

        if (explicitEntry) _entries[path] = previous with { Explicit = true };
    }

    private sealed record PathEntry(bool IsDirectory, bool Explicit);
}

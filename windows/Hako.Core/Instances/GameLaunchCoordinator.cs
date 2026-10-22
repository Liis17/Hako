using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Hako.Core.Minecraft;

namespace Hako.Core.Instances;

public enum GameRunPhase
{
    Preparing,
    Running,
    Failed
}

public sealed record GameRunState(
    GameRunPhase Phase,
    int? ExitCode = null,
    string? ErrorResourceKey = null,
    string? LogPath = null);

public sealed class GameRunStateChangedEventArgs(Guid instanceId) : EventArgs
{
    public Guid InstanceId { get; } = instanceId;
}

/// <summary>Extension point for playtime tracking: a game session started or ended (including restored games).</summary>
public sealed class GameSessionEventArgs(Guid instanceId, GameProcessRecord record, int? exitCode = null) : EventArgs
{
    public Guid InstanceId { get; } = instanceId;
    public GameProcessRecord Record { get; } = record;
    public int? ExitCode { get; } = exitCode;
}

public sealed record MinecraftLaunchRequest(
    GameInstance Instance,
    string InstanceRoot,
    string Executable,
    MinecraftLaunchIdentity Identity,
    LaunchArgumentSource ArgumentSource,
    InstanceParameters Parameters,
    GameArchitecture Architecture);

/// <summary>
/// Coordinates one build launch at a time. State notifications are raised from worker threads as
/// well as the caller thread; UI subscribers must marshal them to their DispatcherQueue.
/// </summary>
public sealed class GameLaunchCoordinator : INotifyPropertyChanged
{
    private readonly InstanceStore _store;
    private readonly UserSettingsService _settings;
    private readonly MinecraftSessionCoordinator _sessions;
    private readonly GameProcessRunner _runner;
    private readonly JavaLaunchValidation _javaValidation;
    private readonly Func<MinecraftLaunchRequest, CancellationToken, Task<MinecraftLaunchPlan>> _prepare;
    private readonly Func<MinecraftLaunchPlan, Guid, string, Guid?, CancellationToken, Task<GameProcessLaunch>> _start;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, GameRunState> _states = [];
    private readonly Dictionary<Guid, IDisposable> _launchLeases = [];

    public GameLaunchCoordinator(
        InstanceStore store,
        UserSettingsService settings,
        MinecraftSessionCoordinator sessions,
        GameProcessRunner runner,
        JavaLaunchValidation? javaValidation = null,
        Func<MinecraftLaunchRequest, CancellationToken, Task<MinecraftLaunchPlan>>? prepare = null,
        TimeProvider? timeProvider = null)
        : this(store, settings, sessions, runner, javaValidation, prepare, null, timeProvider)
    {
    }

    internal GameLaunchCoordinator(
        InstanceStore store,
        UserSettingsService settings,
        MinecraftSessionCoordinator sessions,
        GameProcessRunner runner,
        JavaLaunchValidation? javaValidation,
        Func<MinecraftLaunchRequest, CancellationToken, Task<MinecraftLaunchPlan>>? prepare,
        Func<MinecraftLaunchPlan, Guid, string, Guid?, CancellationToken, Task<GameProcessLaunch>>? start,
        TimeProvider? timeProvider)
    {
        _store = store;
        _settings = settings;
        _sessions = sessions;
        _runner = runner;
        _javaValidation = javaValidation ?? new JavaLaunchValidation();
        _prepare = prepare ?? PrepareAsync;
        _start = start ?? ((plan, id, root, sessionId, token) => runner.StartAsync(plan, id, root, sessionId, token));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<GameRunStateChangedEventArgs>? RunStateChanged;
    public event EventHandler<GameSessionEventArgs>? GameSessionStarted;
    public event EventHandler<GameSessionEventArgs>? GameSessionEnded;

    public IReadOnlyDictionary<Guid, GameRunState> States
    {
        get
        {
            lock (_sync)
            {
                return new Dictionary<Guid, GameRunState>(_states);
            }
        }
    }

    public GameRunState? GetState(Guid instanceId)
    {
        lock (_sync)
        {
            return _states.GetValueOrDefault(instanceId);
        }
    }

    public async Task LaunchAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = _store.List().FirstOrDefault(candidate => candidate.Id == instanceId)
            ?? throw new KeyNotFoundException("The build does not exist.");
        if (instance.State != InstanceInstallationState.Ready)
        {
            throw new MinecraftLaunchException(MinecraftLaunchError.BuildNotReady);
        }

        if (!_store.TryBeginLaunch(instanceId, out var lease, out _ ) || lease is null)
        {
            throw new MinecraftLaunchException(MinecraftLaunchError.BuildAlreadyRunning);
        }

        var leaseTransferred = false;
        try
        {
            var root = _store.GetInstanceDirectory(instanceId);
            var settingsSnapshot = _settings.Current;
            var globalDefaults = GameLaunchDefaults.From(settingsSnapshot);
            var parameters = instance.ArgumentSource == LaunchArgumentSource.Global
                ? new InstanceParameters
                {
                    JavaArguments = globalDefaults.JavaArguments,
                    MinecraftArguments = globalDefaults.MinecraftArguments,
                    Fullscreen = globalDefaults.Fullscreen,
                    WindowWidth = globalDefaults.WindowWidth,
                    WindowHeight = globalDefaults.WindowHeight,
                    MaximumMemoryMiB = globalDefaults.MaximumMemoryMiB
                }
                : instance.Parameters;
            var architecture = ParsePinnedArchitecture(instance.Architecture);
            var memoryPolicy = JavaMemoryPolicy.Current;
            parameters = parameters with { MaximumMemoryMiB = memoryPolicy.Normalize(parameters.MaximumMemoryMiB, architecture) };

            var executable = ResolveJavaExecutable(instance, root, settingsSnapshot);
            if (!File.Exists(executable))
            {
                // The selected Java is never silently replaced by another runtime.
                throw new JavaLaunchValidationException(JavaValidationError.MissingExecutable);
            }

            SetState(instanceId, new GameRunState(GameRunPhase.Preparing));

            MinecraftLaunchIdentity identity;
            AccountSession? session = null;
            if (instance.OfflineMode)
            {
                identity = MinecraftLaunchIdentity.Offline(instance.OfflineUsername);
            }
            else
            {
                session = await _sessions.ConnectForLaunchAsync(cancellationToken).ConfigureAwait(false);
                identity = OnlineIdentity(session);
            }

            var request = new MinecraftLaunchRequest(instance, root, executable, identity, instance.ArgumentSource, parameters, architecture);
            var plan = await _prepare(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (!instance.OfflineMode)
            {
                var currentSession = await _sessions.ConnectForLaunchAsync(cancellationToken).ConfigureAwait(false);
                if (session is null || !SameMinecraftSession(session, currentSession))
                {
                    throw new MinecraftLaunchException(MinecraftLaunchError.SessionChanged);
                }
            }

            var process = await _start(plan, instanceId, root, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _launchLeases[instanceId] = lease;
                leaseTransferred = true;
            }

            SetState(instanceId, new GameRunState(GameRunPhase.Running, LogPath: process.LogPath));
            _ = ObserveCompletionAsync(instanceId, process, lease);
        }
        catch (OperationCanceledException)
        {
            ClearState(instanceId);
            throw;
        }
        catch (Exception exception)
        {
            var logPath = TryGetLogPath(instanceId);
            SetState(instanceId, new GameRunState(GameRunPhase.Failed, ErrorResourceKey: ResourceKey(exception), LogPath: logPath));
            throw;
        }
        finally
        {
            if (!leaseTransferred)
            {
                lease.Dispose();
            }
        }
    }

    public async Task RestoreRunningGamesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var instance in _store.List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_store.TryBeginLaunch(instance.Id, out var lease, out _) || lease is null)
            {
                continue;
            }

            GameProcessLaunch? restored;
            try
            {
                var root = _store.GetInstanceDirectory(instance.Id);
                restored = await _runner.RestoreAsync(instance.Id, root, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lease.Dispose();
                throw;
            }
            catch (Exception)
            {
                // One damaged record must not prevent the remaining builds from being restored.
                lease.Dispose();
                SetState(instance.Id, new GameRunState(GameRunPhase.Failed,
                    ErrorResourceKey: "LaunchProcessRecordInvalid", LogPath: TryGetLogPath(instance.Id)));
                continue;
            }

            if (restored is null)
            {
                lease.Dispose();
                continue;
            }

            lock (_sync)
            {
                _launchLeases[instance.Id] = lease;
            }

            SetState(instance.Id, new GameRunState(GameRunPhase.Running, LogPath: restored.LogPath));
            _ = ObserveCompletionAsync(instance.Id, restored, lease);
        }
    }

    public string GetLaunchLogPath(Guid instanceId)
    {
        var root = _store.GetInstanceDirectory(instanceId);
        return InstanceStorage.ContainedPath("minecraft/logs/hako-launch.log", root, allowBaseReparsePoint: true);
    }

    private async Task ObserveCompletionAsync(Guid instanceId, GameProcessLaunch process, IDisposable lease)
    {
        Raise(GameSessionStarted, new GameSessionEventArgs(instanceId, process.Record));
        int? exitCode = null;
        try
        {
            exitCode = await process.Completion.ConfigureAwait(false);
            if (exitCode is { } code && code != 0)
            {
                SetState(instanceId, new GameRunState(GameRunPhase.Failed, code, "LaunchGameExitCode", process.LogPath));
            }
            else
            {
                ClearState(instanceId);
            }
        }
        catch
        {
            SetState(instanceId, new GameRunState(GameRunPhase.Failed,
                ErrorResourceKey: "LaunchProcessRecordInvalid", LogPath: process.LogPath));
        }
        finally
        {
            lock (_sync)
            {
                _launchLeases.Remove(instanceId);
            }

            lease.Dispose();
            Raise(GameSessionEnded, new GameSessionEventArgs(instanceId, process.Record, exitCode));
        }
    }

    private void Raise(EventHandler<GameSessionEventArgs>? handlers, GameSessionEventArgs args)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<GameSessionEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception) { }
        }
    }

    private async Task<MinecraftLaunchPlan> PrepareAsync(MinecraftLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manifestPath = InstanceStorage.ContainedPath(
            $"minecraft/versions/{request.Instance.VersionID}/{request.Instance.VersionID}.json",
            request.InstanceRoot,
            allowBaseReparsePoint: true);
        var manifestData = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Convert.ToHexString(SHA1.HashData(manifestData)), request.Instance.MetadataSHA1, StringComparison.OrdinalIgnoreCase))
        {
            throw new MinecraftLaunchException(MinecraftLaunchError.MetadataInvalid);
        }

        MojangVersionMetadata manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(manifestData, MojangJsonContext.Default.MojangVersionMetadata)
                ?? throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new MinecraftLaunchException(MinecraftLaunchError.MetadataInvalid, innerException: exception);
        }

        if (!string.Equals(manifest.Id, request.Instance.VersionID, StringComparison.Ordinal))
        {
            throw new MinecraftLaunchException(MinecraftLaunchError.MetadataInvalid);
        }

        MojangAssetIndex? assetIndex = null;
        if (manifest.AssetIndex is { } assetReference)
        {
            var assetIndexPath = InstanceStorage.ContainedPath(
                $"minecraft/assets/indexes/{assetReference.Id}.json", request.InstanceRoot, allowBaseReparsePoint: true);
            var assetIndexData = await File.ReadAllBytesAsync(assetIndexPath, cancellationToken).ConfigureAwait(false);
            try
            {
                MojangClient.Verify(assetIndexData, assetReference.Download);
                assetIndex = JsonSerializer.Deserialize(assetIndexData, MojangJsonContext.Default.MojangAssetIndex)
                    ?? throw new JsonException();
            }
            catch (Exception exception) when (exception is JsonException or MojangException)
            {
                throw new MinecraftLaunchException(MinecraftLaunchError.AssetIndexInvalid, innerException: exception);
            }
        }

        if (!manifest.Downloads.TryGetValue("client", out var clientDownload) || clientDownload.Sha1.Length != 40)
        {
            throw new MinecraftLaunchException(MinecraftLaunchError.ClientMissing);
        }

        var clientPath = InstanceStorage.ContainedPath(
            $"minecraft/versions/{manifest.Id}/{manifest.Id}.jar", request.InstanceRoot, allowBaseReparsePoint: true);
        if (!File.Exists(clientPath))
        {
            throw new MinecraftLaunchException(MinecraftLaunchError.ClientMissing);
        }

        await using (var stream = new FileStream(clientPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (clientDownload.Size is { } expectedSize && stream.Length != expectedSize)
            {
                throw new MinecraftLaunchException(MinecraftLaunchError.ClientMissing);
            }

            var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(Convert.ToHexString(hash), clientDownload.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                throw new MinecraftLaunchException(MinecraftLaunchError.ClientMissing);
            }
        }

        var major = await _javaValidation.ValidateAsync(request.Executable, manifest.Java.MajorVersion,
            request.Architecture, cancellationToken: cancellationToken).ConfigureAwait(false);
        MinecraftLaunchProfile? profile = null;
        if (request.Instance.ModLoader == InstanceModLoader.Fabric)
        {
            profile = await LoadFabricProfileAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return MinecraftLaunchPlan.Build(manifest, request.InstanceRoot, request.Executable, request.Identity,
            request.ArgumentSource, request.Parameters, request.Architecture, assetIndex,
            javaMajorVersion: major, profile: profile);
    }

    private static Task<MinecraftLaunchProfile> LoadFabricProfileAsync(MinecraftLaunchRequest request, CancellationToken cancellationToken) =>
        request.Instance.FabricConfiguration is { } configuration
            ? MinecraftLaunchPlan.LoadInstalledFabricProfileAsync(request.InstanceRoot, request.Instance.VersionID,
                configuration.LoaderVersion, request.Instance.FabricProfileSHA1, cancellationToken)
            : throw new MinecraftLaunchException(MinecraftLaunchError.FabricProfileMissing);

    private static string ResolveJavaExecutable(
        GameInstance instance,
        string root,
        UserSettings settings)
    {
        if (instance.ArgumentSource == LaunchArgumentSource.Global && !string.IsNullOrWhiteSpace(settings.JavaPath))
        {
            var globalPath = Environment.ExpandEnvironmentVariables(settings.JavaPath.Trim());
            return Path.GetFullPath(globalPath);
        }

        return InstanceStorage.ContainedPath("java/" + instance.JavaExecutable, root, allowBaseReparsePoint: true);
    }

    private static GameArchitecture ParsePinnedArchitecture(string? architecture)
    {
        if (architecture is not null && Enum.TryParse<GameArchitecture>(architecture, ignoreCase: false, out var parsed)
            && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new MinecraftLaunchException(MinecraftLaunchError.InvalidManifest, "The build has no valid pinned architecture.");
    }

    private MinecraftLaunchIdentity OnlineIdentity(AccountSession account)
    {
        if (string.IsNullOrWhiteSpace(account.MinecraftName) || string.IsNullOrWhiteSpace(account.MinecraftProfileId)
            || string.IsNullOrWhiteSpace(account.MinecraftAccessToken)
            || account.MinecraftTokenExpiresAt is not { } expiresAt || expiresAt <= _timeProvider.GetUtcNow())
        {
            throw new AuthServiceException(AuthErrorKind.MinecraftProfileMissing);
        }

        return new MinecraftLaunchIdentity(account.MinecraftName, account.MinecraftProfileId.Replace("-", "", StringComparison.Ordinal),
            account.MinecraftAccessToken, account.Xuid, "msa");
    }

    private bool SameMinecraftSession(AccountSession first, AccountSession second) =>
        first.Xuid == second.Xuid
        && first.MinecraftProfileId == second.MinecraftProfileId
        && first.MinecraftAccessToken == second.MinecraftAccessToken
        && first.MinecraftTokenExpiresAt is { } expiresAt
        && expiresAt > _timeProvider.GetUtcNow();

    private string? TryGetLogPath(Guid instanceId)
    {
        try { return GetLaunchLogPath(instanceId); }
        catch (Exception) { return null; }
    }

    private static string ResourceKey(Exception exception) => exception switch
    {
        AuthServiceException auth => auth.Kind.ResourceKey(),
        MinecraftLaunchException launch => launch.ResourceKey,
        JavaLaunchValidationException java => java.ResourceKey,
        _ => "LaunchProcessStartFailed"
    };

    private void SetState(Guid instanceId, GameRunState state)
    {
        lock (_sync)
        {
            _states[instanceId] = state;
        }

        NotifyState(instanceId);
    }

    private void ClearState(Guid instanceId)
    {
        bool removed;
        lock (_sync)
        {
            removed = _states.Remove(instanceId);
        }

        if (removed)
        {
            NotifyState(instanceId);
        }
    }

    private void NotifyState(Guid instanceId)
    {
        var handlers = RunStateChanged;
        if (handlers is not null)
        {
            foreach (EventHandler<GameRunStateChangedEventArgs> handler in handlers.GetInvocationList())
            {
                try { handler(this, new GameRunStateChangedEventArgs(instanceId)); }
                catch (Exception) { }
            }
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(States)));
    }
}

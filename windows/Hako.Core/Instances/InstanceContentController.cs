using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using System.Text.Json;
using Hako.Core.Minecraft;

namespace Hako.Core.Instances;

public enum InstanceContentOperation
{
    Reload,
    Import,
    Enable,
    Delete,
    Install,
    CheckUpdates,
    Update,
    UpdateAll
}

public enum ContentConfirmationCode
{
    Dependencies,
    IncompatibleContent,
    ReplaceExisting,
    DeleteContent
}

public enum ContentConfirmationChoice
{
    Cancel,
    Primary,
    Alternative
}

public enum ContentConfirmationNoteCode
{
    DisabledWillBeEnabled,
    Unsupported,
    RequiresFabric,
    NoCompatibleVersion,
    ProjectLimitReached,
    ProjectUnavailable,
    FabricLoaderRequirement,
    JavaRequirement,
    IncompatibleWith
}

public enum ContentFailureCode
{
    BuildUnavailable,
    BuildBusy,
    BuildInstalling,
    ModRequiresFabric,
    InvalidChannel,
    NoCompatibleVersion,
    RegistryCorrupt,
    RegistryUnavailable,
    StaleContent,
    ConfirmationRequired,
    ConfirmationDeclined,
    FabricApiIncompatible,
    ModrinthRequestFailed,
    ContentOperationFailed,
    BulkUpdateFailed,
    Cancelled
}

public readonly record struct InstanceContentStateKey(Guid InstanceId, ModrinthContentKind Kind);

public sealed record ContentFailure(ContentFailureCode Code, IReadOnlyDictionary<string, string> Arguments);

public sealed record ContentConfirmationProject(
    string Id,
    string Title,
    Uri? IconUrl,
    ContentConfirmationNoteCode? NoteCode = null,
    IReadOnlyDictionary<string, string>? NoteArguments = null);

/// <summary>UI-neutral confirmation data. The UI maps codes and arguments to localized text.</summary>
public sealed record ContentConfirmationRequest(
    ContentConfirmationCode Code,
    IReadOnlyDictionary<string, string> Arguments,
    bool PrimaryActionAvailable,
    bool AlternativeActionAvailable,
    bool Destructive,
    IReadOnlyList<ContentConfirmationProject> Projects);

public delegate Task<ContentConfirmationChoice> ContentConfirmationCallback(
    ContentConfirmationRequest request,
    CancellationToken cancellationToken);

public sealed record InstanceContentUpdate(
    InstanceContentItem Item,
    ModrinthProject Project,
    ModrinthVersion? Version,
    ModrinthFile? File,
    string CurrentSha512,
    ModrinthContentKind? VersionKind = null,
    FabricApiDescriptor? Api = null);

public sealed record InstanceContentState
{
    public IReadOnlyList<InstanceContentItem> Items { get; init; } = [];
    public IReadOnlyDictionary<string, InstanceContentUpdate> Updates { get; init; }
        = new ReadOnlyDictionary<string, InstanceContentUpdate>(new Dictionary<string, InstanceContentUpdate>(StringComparer.OrdinalIgnoreCase));
    public bool IsLoading { get; init; }
    public bool IsBusy { get; init; }
    public bool IsCheckingUpdates { get; init; }
    public InstanceContentOperation? BusyOperation { get; init; }
    public string? CurrentProjectId { get; init; }
    public ContentFailure? Error { get; init; }
    public ContentFailure? UpdateMessage { get; init; }
    public long Revision { get; init; }
    public bool HasUpdates => Updates.Count != 0;
}

public sealed class InstanceContentStateChangedEventArgs(InstanceContentStateKey key, InstanceContentState state) : EventArgs
{
    public InstanceContentStateKey Key { get; } = key;
    public InstanceContentState State { get; } = state;
}

public sealed record ContentActionResult(bool Succeeded, ContentFailure? Failure = null);

public sealed record ContentUpdateItemResult(string Filename, bool Succeeded, ContentFailure? Failure = null);

public sealed record ContentBulkUpdateResult(IReadOnlyList<ContentUpdateItemResult> Items)
{
    public bool HasFailures => Items.Any(item => !item.Succeeded);
}

/// <summary>
/// Core-only coordinator for local content, Modrinth installs and updates. Events are raised on the
/// thread that changed the snapshot; UI consumers marshal them to their dispatcher.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstanceContentController
{
    private const int MaximumDependencyProjects = 30;
    private const int MaximumFabricMetadataBytes = 1024 * 1024;
    private readonly InstanceStore _store;
    private readonly InstanceContent _content;
    private readonly ModrinthClient _modrinth;
    private readonly FabricClient _fabric;
    private readonly ContentConfirmationCallback? _confirm;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _operationGates = new();
    private readonly SemaphoreSlim _confirmationGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<InstanceContentStateKey, InstanceContentState> _states = [];
    private readonly Dictionary<InstanceContentStateKey, long> _revisions = [];

    public InstanceContentController(InstanceStore store, InstanceContent content, ModrinthClient modrinth,
        FabricClient fabric, ContentConfirmationCallback? confirm = null)
    {
        _store = store;
        _content = content;
        _modrinth = modrinth;
        _fabric = fabric;
        _confirm = confirm;
    }

    public event EventHandler<InstanceContentStateChangedEventArgs>? StateChanged;

    public InstanceContentState GetState(Guid instanceId, ModrinthContentKind kind)
    {
        ValidateKind(kind);
        var key = new InstanceContentStateKey(instanceId, kind);
        lock (_sync) return Snapshot(_states.GetValueOrDefault(key) ?? new InstanceContentState());
    }

    public async Task ReloadAsync(Guid instanceId, ModrinthContentKind kind, CancellationToken cancellationToken = default)
    {
        ValidateKind(kind);
        var key = new InstanceContentStateKey(instanceId, kind);
        var initial = TryBeginRead(key, checkingUpdates: false);
        if (initial is null) return;
        var revision = initial.Revision;
        RaiseStateChanged(key, initial);
        try
        {
            var instance = FindInstance(instanceId);
            var root = _store.GetInstanceDirectory(instanceId);
            var (items, registryFailure) = await ListWithRegistryFallbackAsync(instance, kind, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent(key, revision, instance, root)) return;
            ChangeState(key, revision, state => state with
            {
                Items = items,
                Updates = RetainUpdates(state.Updates, items),
                Error = registryFailure
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ChangeState(key, revision, state => state with { Error = MapFailure(exception) });
        }
        finally
        {
            ChangeState(key, revision, state => state with { IsLoading = false });
        }
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<InstanceContentItem>>> InstalledProjectsAsync(
        Guid instanceId, ModrinthContentKind kind, CancellationToken cancellationToken = default)
    {
        ValidateKind(kind);
        var readRevision = GetState(instanceId, kind).Revision;
        var instance = FindInstance(instanceId);
        var (items, registryFailure) = await ListWithRegistryFallbackAsync(instance, kind, cancellationToken).ConfigureAwait(false);
        if (registryFailure is not null)
            SetReadError(instanceId, kind, readRevision, registryFailure);
        return await GroupInstalledProjectsAsync(items, cancellationToken).ConfigureAwait(false);
    }

    public Task<ContentActionResult> ImportLocalAsync(Guid instanceId, ModrinthContentKind kind, string sourcePath,
        CancellationToken cancellationToken = default) => RunMutationAsync(instanceId, kind, InstanceContentOperation.Import,
        async (operation, root, instance, token) =>
        {
            var items = await _content.ListUnderLeaseAsync(root, instance, kind, includeDisabled: true, token).ConfigureAwait(false);
            var incoming = Path.GetFileName(Path.GetFullPath(sourcePath));
            var collision = items.FirstOrDefault(item => LogicalName(kind, item).Equals(incoming, StringComparison.OrdinalIgnoreCase));
            var replace = false;
            if (collision is not null)
            {
                var choice = await ConfirmAsync(new ContentConfirmationRequest(ContentConfirmationCode.ReplaceExisting,
                    Args(("filename", incoming), ("instanceName", instance.Name)), true, false, true,
                    [new ContentConfirmationProject(incoming, incoming, null)]), token).ConfigureAwait(false);
                if (choice != ContentConfirmationChoice.Primary)
                    return new ContentActionResult(false, choice == ContentConfirmationChoice.Cancel ? null : Failure(ContentFailureCode.ConfirmationDeclined));
                replace = true;
            }

            ValidatePathSnapshot(instance, root);
            operation.Touch(kind);
            await _content.ImportUnderLeaseAsync(root, instance, kind, sourcePath, replace, origin: null, token).ConfigureAwait(false);
            return new ContentActionResult(true);
        }, cancellationToken);

    public Task<ContentActionResult> SetEnabledAsync(Guid instanceId, ModrinthContentKind kind,
        InstanceContentItem item, bool enabled, CancellationToken cancellationToken = default) =>
        RunMutationAsync(instanceId, kind, InstanceContentOperation.Enable, async (operation, root, instance, token) =>
        {
            var items = await _content.ListUnderLeaseAsync(root, instance, kind, includeDisabled: true, token).ConfigureAwait(false);
            var current = items.FirstOrDefault(candidate => PathEquals(candidate.Path, item.Path));
            if (current is null) throw ControllerException.With(ContentFailureCode.StaleContent);
            ValidatePathSnapshot(instance, root);
            operation.Touch(kind);
            await Task.Run(() => _content.SetEnabledUnderLease(root, instance, current, kind, enabled), token).ConfigureAwait(false);
            return new ContentActionResult(true);
        }, cancellationToken);

    public Task<ContentActionResult> DeleteAsync(Guid instanceId, ModrinthContentKind kind,
        InstanceContentItem item, CancellationToken cancellationToken = default) =>
        RunMutationAsync(instanceId, kind, InstanceContentOperation.Delete, async (operation, root, instance, token) =>
        {
            var choice = await ConfirmAsync(new ContentConfirmationRequest(ContentConfirmationCode.DeleteContent,
                Args(("filename", item.Name), ("instanceName", instance.Name)), true, false, true,
                [new ContentConfirmationProject(item.Name, item.Name, item.Origin?.IconURL)]), token).ConfigureAwait(false);
            if (choice != ContentConfirmationChoice.Primary) return new ContentActionResult(false);
            var items = await _content.ListUnderLeaseAsync(root, instance, kind, includeDisabled: true, token).ConfigureAwait(false);
            var current = items.FirstOrDefault(candidate => PathEquals(candidate.Path, item.Path));
            if (current is null) throw ControllerException.With(ContentFailureCode.StaleContent);
            ValidatePathSnapshot(instance, root);
            operation.Touch(kind);
            var status = await _content.TrashUnderLeaseAsync(root, instance, current, kind, token).ConfigureAwait(false);
            return status == RecycleBinItemStatus.Recycled
                ? new ContentActionResult(true)
                : new ContentActionResult(false, Failure(ContentFailureCode.ContentOperationFailed));
        }, cancellationToken);

    public Task<ContentActionResult> InstallAsync(Guid instanceId, ModrinthContentKind kind,
        ModrinthProject project, string channel = "release", CancellationToken cancellationToken = default) =>
        RunMutationAsync(instanceId, kind, InstanceContentOperation.Install,
            (operation, root, instance, token) => InstallUnderLeaseAsync(operation, root, instance, kind, project,
                channel, requestedVersion: null, requestedFile: null, replacing: null, interactive: true, token),
            cancellationToken, project.Id);

    public Task CheckUpdatesAsync(Guid instanceId, ModrinthContentKind kind, CancellationToken cancellationToken = default) =>
        CheckUpdatesCoreAsync(instanceId, kind, cancellationToken);

    public Task<ContentActionResult> UpdateAsync(Guid instanceId, ModrinthContentKind kind,
        InstanceContentItem item, CancellationToken cancellationToken = default)
    {
        ValidateKind(kind);
        var update = GetState(instanceId, kind).Updates.GetValueOrDefault(item.LogicalName);
        if (update is null || !PathEquals(update.Item.Path, item.Path))
            return Task.FromResult(new ContentActionResult(false, Failure(ContentFailureCode.StaleContent)));
        return RunMutationAsync(instanceId, kind, InstanceContentOperation.Update, async (operation, root, instance, token) =>
        {
            operation.Touch(kind);
            operation.SetCurrentProject(update.Project.Id);
            if (update.Api is not null)
            {
                return await ApplyApiUpdateUnderLeaseAsync(root, instance, update, interactive: true, token).ConfigureAwait(false);
            }

            return await InstallUnderLeaseAsync(operation, root, instance, kind, update.Project, channel: null,
                update.Version, update.File, (update.Item, update.CurrentSha512), interactive: true, token,
                requestedVersionKind: update.VersionKind).ConfigureAwait(false);
        }, cancellationToken, update.Project.Id);
    }

    public Task<ContentBulkUpdateResult> UpdateAllAsync(Guid instanceId, ModrinthContentKind kind,
        CancellationToken cancellationToken = default)
    {
        ValidateKind(kind);
        var updates = GetState(instanceId, kind).Updates.Values
            .OrderBy(update => update.Api is null ? 1 : 0)
            .ThenBy(update => update.Item.LogicalName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (updates.Length == 0) return Task.FromResult(new ContentBulkUpdateResult([]));

        return RunBulkUpdateAsync(instanceId, kind, updates, cancellationToken);
    }

    private async Task<ContentBulkUpdateResult> RunBulkUpdateAsync(Guid instanceId, ModrinthContentKind kind,
        IReadOnlyList<InstanceContentUpdate> updates, CancellationToken cancellationToken)
    {
        var results = new List<ContentUpdateItemResult>(updates.Count);
        var outcome = await RunMutationAsync(instanceId, kind, InstanceContentOperation.UpdateAll,
            async (operation, root, instance, token) =>
            {
                operation.Touch(kind);
                foreach (var update in updates.OrderBy(item => item.Api is null ? 1 : 0)
                             .ThenBy(item => item.Item.LogicalName, StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    operation.SetCurrentProject(update.Project.Id);
                    try
                    {
                        if (update.Api is not null)
                            await ApplyApiUpdateUnderLeaseAsync(root, instance, update, interactive: false, token).ConfigureAwait(false);
                        else
                        {
                            var result = await InstallUnderLeaseAsync(operation, root, instance, kind, update.Project,
                                channel: null, update.Version, update.File, (update.Item, update.CurrentSha512),
                                interactive: false, token, requestedVersionKind: update.VersionKind).ConfigureAwait(false);
                            if (!result.Succeeded)
                                throw ControllerException.With(ContentFailureCode.ConfirmationRequired);
                        }

                        results.Add(new ContentUpdateItemResult(update.Item.LogicalName, true));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception)
                    {
                        results.Add(new ContentUpdateItemResult(update.Item.LogicalName, false, MapFailure(exception)));
                    }
                }

                var failure = results.Any(result => !result.Succeeded)
                    ? Failure(ContentFailureCode.BulkUpdateFailed, ("failureCount", results.Count(result => !result.Succeeded).ToString()))
                    : null;
                return new ContentActionResult(failure is null, failure);
            }, cancellationToken, updates[0].Project.Id).ConfigureAwait(false);

        if (!outcome.Succeeded && results.Count == 0 && outcome.Failure is not null)
            results.Add(new ContentUpdateItemResult("", false, outcome.Failure));
        return new ContentBulkUpdateResult(results);
    }

    private async Task<ContentActionResult> RunMutationAsync(Guid instanceId, ModrinthContentKind kind,
        InstanceContentOperation operationKind,
        Func<OperationContext, string, GameInstance, CancellationToken, Task<ContentActionResult>> action,
        CancellationToken cancellationToken, string? initialProjectId = null)
    {
        ValidateKind(kind);
        var gate = _operationGates.GetOrAdd(instanceId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var operation = new OperationContext(this, instanceId, operationKind, cancellationToken);
        var key = new InstanceContentStateKey(instanceId, kind);
        try
        {
            operation.Touch(kind);
            var instance = FindInstance(instanceId);
            if (kind == ModrinthContentKind.Mod && instance.ModLoader != InstanceModLoader.Fabric)
                throw ControllerException.With(ContentFailureCode.ModRequiresFabric);
            if (kind == ModrinthContentKind.Mod
                && instance.State is InstanceInstallationState.Queued or InstanceInstallationState.Installing)
                throw ControllerException.With(ContentFailureCode.BuildInstalling);
            var root = _store.GetInstanceDirectory(instanceId);
            operation.SetSnapshot(instance, root);
            operation.SetCurrentProject(initialProjectId);
            try
            {
                var result = await _content.WithContentLeaseAsync(instanceId, async (leasedRoot, leasedInstance, token) =>
                {
                    operation.CallbackEntered = true;
                    ValidatePathSnapshot(leasedInstance, leasedRoot);
                    operation.SetSnapshot(leasedInstance, leasedRoot);
                    try
                    {
                        return await action(operation, leasedRoot, leasedInstance, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        await RefreshTouchedUnderLeaseAsync(operation).ConfigureAwait(false);
                    }
                }, cancellationToken).ConfigureAwait(false);

                if (result.Failure is not null)
                    foreach (var stateKey in operation.Keys) ChangeState(stateKey, operation.Revision(stateKey), state => state with { Error = result.Failure });
                else
                    foreach (var stateKey in operation.Keys) ChangeState(stateKey, operation.Revision(stateKey), state => state with { Error = null });
                return result;
            }
            catch (OperationCanceledException)
            {
                if (!operation.CallbackEntered)
                    await RefreshTouchedOutsideLeaseAsync(operation).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                var failure = MapFailure(exception);
                if (!operation.CallbackEntered)
                    await RefreshTouchedOutsideLeaseAsync(operation).ConfigureAwait(false);
                foreach (var stateKey in operation.Keys) ChangeState(stateKey, operation.Revision(stateKey), state => state with { Error = failure });
                return new ContentActionResult(false, failure);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var stateKey in operation.Keys) ChangeState(stateKey, operation.Revision(stateKey), state => state with { Error = Failure(ContentFailureCode.Cancelled) });
            throw;
        }
        catch (Exception exception)
        {
            var failure = MapFailure(exception);
            ChangeState(key, operation.Revision(key), state => state with { Error = failure });
            return new ContentActionResult(false, failure);
        }
        finally
        {
            foreach (var stateKey in operation.Keys)
                ClearBusyUnderOperationGate(stateKey);
            gate.Release();
        }
    }

    private async Task<ContentActionResult> InstallUnderLeaseAsync(OperationContext operation, string root,
        GameInstance instance, ModrinthContentKind targetKind, ModrinthProject project, string? channel,
        ModrinthVersion? requestedVersion, ModrinthFile? requestedFile,
        (InstanceContentItem Item, string Sha512)? replacing, bool interactive, CancellationToken cancellationToken,
        ModrinthContentKind? requestedVersionKind = null)
    {
        if (targetKind == ModrinthContentKind.Mod && instance.ModLoader != InstanceModLoader.Fabric)
            throw ControllerException.With(ContentFailureCode.ModRequiresFabric);
        if (channel is not null && channel is not ("release" or "beta" or "alpha"))
            throw ControllerException.With(ContentFailureCode.InvalidChannel);

        operation.SetCurrentProject(project.Id);
        var version = requestedVersion ?? await _modrinth.GetLatestVersionAsync(project.Id, targetKind,
            instance.VersionID, channel, cancellationToken).ConfigureAwait(false);
        var file = requestedFile ?? version?.InstallableFile(targetKind);
        var versionKind = requestedVersionKind ?? targetKind;
        if (version is null || file is null || !file.IsInstallable(targetKind)
            || !version.GameVersions.Contains(instance.VersionID, StringComparer.Ordinal)
            || !version.Loaders.Contains(Loader(versionKind), StringComparer.Ordinal))
            throw ControllerException.With(ContentFailureCode.NoCompatibleVersion,
                ("projectId", project.Id), ("channel", channel ?? "latest"));

        var plan = await BuildDependencyPlanAsync(root, instance, project, version, cancellationToken).ConfigureAwait(false);
        foreach (var dependency in plan.Downloads) operation.Touch(dependency.Kind);
        foreach (var enable in plan.Enable) operation.Touch(enable.Kind);
        if (plan.Listed.Count > 0)
        {
            if (!interactive) throw ControllerException.With(ContentFailureCode.ConfirmationRequired, ("projectId", project.Id));
            var choice = await ConfirmAsync(new ContentConfirmationRequest(ContentConfirmationCode.Dependencies,
                Args(("projectId", project.Id), ("projectTitle", project.Title), ("instanceName", instance.Name)),
                plan.Downloads.Count > 0 || plan.Enable.Count > 0, true, false, plan.Listed), cancellationToken).ConfigureAwait(false);
            if (choice == ContentConfirmationChoice.Cancel) return new ContentActionResult(false);
            if (choice == ContentConfirmationChoice.Alternative)
            {
                plan.Downloads.Clear();
                plan.Enable.Clear();
            }
            else if (!plan.Downloads.Any() && !plan.Enable.Any())
            {
                return new ContentActionResult(false);
            }
        }

        plan.Downloads.Add(new PlannedDownload(project, version, file, targetKind, IsRoot: true));
        var plannedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var download in plan.Downloads)
            if (!plannedTargets.Add($"{download.Kind}:{download.File.Filename}"))
                throw ControllerException.With(ContentFailureCode.ContentOperationFailed,
                    ("filename", download.File.Filename));

        var staging = Path.Combine(Path.GetTempPath(), "Hako-Modrinth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var staged = new List<(PlannedDownload Download, string Path)>();
            var warnings = new Dictionary<string, ContentConfirmationProject>(StringComparer.Ordinal);
            var configuration = instance.FabricConfiguration;
            foreach (var download in plan.Downloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                operation.SetCurrentProject(download.Project.Id);
                var path = Path.Combine(staging, Guid.NewGuid().ToString("N"), download.File.Filename);
                await _modrinth.DownloadFileAsync(download.File, path, cancellationToken).ConfigureAwait(false);
                if (download.Kind == ModrinthContentKind.Mod && configuration is not null)
                {
                    foreach (var note in await CompatibilityNotesAsync(path, configuration.LoaderVersion,
                                 instance.JavaMajorVersion, cancellationToken).ConfigureAwait(false))
                        warnings.TryAdd(download.Project.Id + ":" + note.NoteCode,
                            new ContentConfirmationProject(download.Project.Id, download.Project.Title,
                                download.Project.IconUrl, note.NoteCode, note.NoteArguments));
                }

                staged.Add((download, path));
            }

            foreach (var download in plan.Downloads)
            {
                foreach (var conflictId in await IncompatibleProjectIdsAsync(download.Version, cancellationToken).ConfigureAwait(false))
                {
                    foreach (var kind in new[] { ModrinthContentKind.Mod, ModrinthContentKind.ResourcePack })
                    {
                        var installed = await InstalledProjectsUnderLeaseAsync(root, instance, kind, cancellationToken).ConfigureAwait(false);
                        if (!installed.TryGetValue(conflictId, out var files) || !files.Any(item => item.Enabled)) continue;
                        var conflict = (await _modrinth.GetProjectsAsync([conflictId], cancellationToken).ConfigureAwait(false))
                            .FirstOrDefault();
                        warnings.TryAdd("conflict:" + conflictId + ":" + download.Project.Id,
                            new ContentConfirmationProject(conflictId, conflict?.Title ?? conflictId,
                                conflict?.IconUrl, ContentConfirmationNoteCode.IncompatibleWith,
                                Args(("projectTitle", download.Project.Title))));
                    }
                }
            }

            if (warnings.Count > 0)
            {
                if (!interactive) throw ControllerException.With(ContentFailureCode.ConfirmationRequired, ("projectId", project.Id));
                var choice = await ConfirmAsync(new ContentConfirmationRequest(ContentConfirmationCode.IncompatibleContent,
                    Args(("projectId", project.Id), ("projectTitle", project.Title), ("instanceName", instance.Name)),
                    true, false, true, warnings.Values.ToArray()), cancellationToken).ConfigureAwait(false);
                if (choice != ContentConfirmationChoice.Primary) return new ContentActionResult(false);
            }

            if (replacing is { } expected)
                await _content.ValidateExpectedHashUnderLeaseAsync(root, instance, expected.Item, targetKind,
                    expected.Sha512, cancellationToken).ConfigureAwait(false);

            var collisionPlan = new List<(PlannedDownload Download, string Path, InstanceContentItem? Collision)>();
            foreach (var entry in staged)
            {
                var collision = await PlanCollisionUnderLeaseAsync(root, instance, entry.Download.Kind,
                    entry.Download.File.Filename, entry.Download.Project,
                    entry.Download.IsRoot ? replacing?.Item : null, interactive, cancellationToken).ConfigureAwait(false);
                if (!collision.Proceed) return new ContentActionResult(false, collision.Failure);

                collisionPlan.Add((entry.Download, entry.Path, collision.Item));
            }

            ValidatePathSnapshot(instance, root);
            foreach (var (download, path, collision) in collisionPlan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateStagedFile(staging, path);
                operation.Touch(download.Kind);
                var origin = OriginFor(download);
                if (download.IsRoot && replacing is { } replacement)
                {
                    await _content.ReplaceUnderLeaseAsync(root, instance, replacement.Item, download.Kind, path,
                        download.File.Filename, replacement.Sha512, origin, cancellationToken, collision).ConfigureAwait(false);
                }
                else
                {
                    await _content.ImportUnderLeaseAsync(root, instance, download.Kind, path,
                        replace: collision is not null, origin, cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var enable in plan.Enable)
            {
                var kind = enable.Kind;
                var currentItems = await _content.ListUnderLeaseAsync(root, instance, kind, true, cancellationToken).ConfigureAwait(false);
                var current = currentItems.FirstOrDefault(item => PathEquals(item.Path, enable.Item.Path));
                if (current is null) throw ControllerException.With(ContentFailureCode.StaleContent);
                operation.Touch(kind);
                await Task.Run(() => _content.SetEnabledUnderLease(root, instance, current, kind, true), cancellationToken)
                    .ConfigureAwait(false);
            }

            return new ContentActionResult(true);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private async Task<DependencyPlan> BuildDependencyPlanAsync(string root, GameInstance instance,
        ModrinthProject rootProject, ModrinthVersion rootVersion,
        CancellationToken cancellationToken)
    {
        var plan = new DependencyPlan();
        var installed = new Dictionary<ModrinthContentKind, IReadOnlyDictionary<string, IReadOnlyList<InstanceContentItem>>>();
        var pending = new Queue<(ModrinthProject Project, ModrinthVersion Version)>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { rootProject.Id };
        pending.Enqueue((rootProject, rootVersion));

        while (pending.TryDequeue(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var required = (current.Version.Dependencies ?? []).Where(dependency => dependency.Type == "required").ToArray();
            var resolved = await ResolveDependencyProjectsAsync(required, cancellationToken).ConfigureAwait(false);
            foreach (var entry in resolved)
            {
                if (entry.ProjectId is null)
                {
                    plan.Listed.Add(new ContentConfirmationProject(entry.Dependency.VersionId ?? "missing",
                        entry.Dependency.VersionId ?? "", null, ContentConfirmationNoteCode.ProjectUnavailable));
                    continue;
                }
                if (!seen.Add(entry.ProjectId)) continue;
                if (seen.Count > MaximumDependencyProjects)
                {
                    seen.Remove(entry.ProjectId);
                    plan.Listed.Add(new ContentConfirmationProject(entry.ProjectId, entry.ProjectId, null,
                        ContentConfirmationNoteCode.ProjectLimitReached,
                        Args(("limit", MaximumDependencyProjects.ToString()))));
                    continue;
                }

                var dependency = entry.Project;
                if (dependency is null)
                {
                    dependency = (await _modrinth.GetProjectsAsync([entry.ProjectId], cancellationToken).ConfigureAwait(false))
                        .FirstOrDefault();
                }
                if (dependency is null)
                {
                    plan.Listed.Add(new ContentConfirmationProject(entry.ProjectId, entry.ProjectId, null,
                        ContentConfirmationNoteCode.ProjectUnavailable));
                    continue;
                }

                var dependencyKind = dependency.ProjectType switch
                {
                    "mod" => ModrinthContentKind.Mod,
                    "resourcepack" => ModrinthContentKind.ResourcePack,
                    _ => (ModrinthContentKind?)null
                };
                if (dependencyKind is null)
                {
                    plan.Listed.Add(ProjectNote(dependency, ContentConfirmationNoteCode.Unsupported));
                    continue;
                }

                if (dependencyKind == ModrinthContentKind.Mod
                    && (instance.ModLoader != InstanceModLoader.Fabric
                        || instance.State is InstanceInstallationState.Queued or InstanceInstallationState.Installing))
                {
                    plan.Listed.Add(ProjectNote(dependency, ContentConfirmationNoteCode.RequiresFabric));
                    continue;
                }

                var kind = dependencyKind.Value;
                if (!installed.TryGetValue(kind, out var installedProjects))
                {
                    installedProjects = await InstalledProjectsUnderLeaseAsync(root, instance, kind, cancellationToken)
                        .ConfigureAwait(false);
                    installed[kind] = installedProjects;
                }
                if (installedProjects.TryGetValue(dependency.Id, out var existing))
                {
                    if (existing.Any(item => item.Enabled)) continue;
                    var disabled = existing.FirstOrDefault();
                    if (disabled is not null)
                    {
                        plan.Enable.Add(new PlannedEnable(disabled, kind));
                        plan.Listed.Add(ProjectNote(dependency, ContentConfirmationNoteCode.DisabledWillBeEnabled));
                        continue;
                    }
                }

                var next = await _modrinth.GetLatestVersionAsync(dependency.Id, kind, instance.VersionID,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var file = next?.InstallableFile(kind);
                if (next is null || file is null)
                {
                    plan.Listed.Add(ProjectNote(dependency, ContentConfirmationNoteCode.NoCompatibleVersion));
                    continue;
                }

                plan.Downloads.Add(new PlannedDownload(dependency, next, file, kind, IsRoot: false));
                plan.Listed.Add(ProjectNote(dependency, null));
                pending.Enqueue((dependency, next));
            }
        }

        return plan;
    }

    private async Task<IReadOnlyList<ResolvedDependency>> ResolveDependencyProjectsAsync(
        IReadOnlyList<ModrinthDependency> dependencies, CancellationToken cancellationToken)
    {
        var byProject = new Dictionary<string, ModrinthProject?>(StringComparer.Ordinal);
        foreach (var projectId in dependencies.Where(dependency => dependency.ProjectId is not null)
                     .Select(dependency => dependency.ProjectId!).Distinct(StringComparer.Ordinal))
            byProject[projectId] = null;

        var versionIds = dependencies.Where(dependency => dependency.ProjectId is null && dependency.VersionId is not null)
            .Select(dependency => dependency.VersionId!).Distinct(StringComparer.Ordinal).ToArray();
        var versions = versionIds.Length == 0
            ? Array.Empty<ModrinthVersion>()
            : await _modrinth.GetVersionsAsync(versionIds, cancellationToken).ConfigureAwait(false);
        var projectByVersion = versions.ToDictionary(version => version.Id, version => version.ProjectId, StringComparer.Ordinal);
        foreach (var projectId in projectByVersion.Values.Distinct(StringComparer.Ordinal)) byProject[projectId] = null;

        var projects = byProject.Count == 0
            ? Array.Empty<ModrinthProject>()
            : (await _modrinth.GetProjectsAsync(byProject.Keys.ToArray(), cancellationToken).ConfigureAwait(false)).ToArray();
        foreach (var project in projects) byProject[project.Id] = project;

        return dependencies.Select(dependency =>
        {
            var id = dependency.ProjectId;
            if (id is null && dependency.VersionId is not null) projectByVersion.TryGetValue(dependency.VersionId, out id);
            return new ResolvedDependency(dependency, id, id is null ? null : byProject.GetValueOrDefault(id));
        }).ToArray();
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<InstanceContentItem>>> InstalledProjectsUnderLeaseAsync(
        string root, GameInstance instance, ModrinthContentKind kind, CancellationToken cancellationToken)
    {
        var items = await _content.ListUnderLeaseAsync(root, instance, kind, includeDisabled: true, cancellationToken)
            .ConfigureAwait(false);
        return await GroupInstalledProjectsAsync(items, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<InstanceContentItem>>> GroupInstalledProjectsAsync(
        IReadOnlyList<InstanceContentItem> items, CancellationToken cancellationToken)
    {
        var grouped = new Dictionary<string, List<InstanceContentItem>>(StringComparer.Ordinal);
        var unknown = new List<InstanceContentItem>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Origin is { Source: ModSource.Modrinth } origin)
                Add(origin.ProjectID, item);
            else if (!item.IsDirectory)
                unknown.Add(item);
        }

        var matches = await _modrinth.VersionsOfAsync(unknown.Select(item => item.Path).ToArray(), cancellationToken)
            .ConfigureAwait(false);
        foreach (var item in unknown)
            if (matches.TryGetValue(item.Path, out var match)) Add(match.ProjectId, item);

        return new ReadOnlyDictionary<string, IReadOnlyList<InstanceContentItem>>(
            grouped.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<InstanceContentItem>)pair.Value.ToArray(), StringComparer.Ordinal));

        void Add(string projectId, InstanceContentItem item)
        {
            if (!grouped.TryGetValue(projectId, out var files)) grouped[projectId] = files = [];
            files.Add(item);
        }
    }

    private async Task CheckUpdatesCoreAsync(Guid instanceId, ModrinthContentKind kind, CancellationToken cancellationToken)
    {
        ValidateKind(kind);
        var key = new InstanceContentStateKey(instanceId, kind);
        var initial = TryBeginRead(key, checkingUpdates: true);
        if (initial is null) return;
        var revision = initial.Revision;
        RaiseStateChanged(key, initial);
        GameInstance instance;
        string root;
        try
        {
            instance = FindInstance(instanceId);
            root = _store.GetInstanceDirectory(instanceId);
        }
        catch (Exception exception)
        {
            ChangeState(key, revision, state => state with { IsCheckingUpdates = false, Error = MapFailure(exception) });
            return;
        }

        if (kind == ModrinthContentKind.Mod && instance.ModLoader != InstanceModLoader.Fabric)
        {
            ChangeState(key, revision, state => state with
            {
                IsCheckingUpdates = false,
                Updates = EmptyUpdates(),
                UpdateMessage = Failure(ContentFailureCode.ModRequiresFabric)
            });
            return;
        }

        if (kind == ModrinthContentKind.Mod
            && instance.State is InstanceInstallationState.Queued or InstanceInstallationState.Installing)
        {
            ChangeState(key, revision, state => state with
            {
                IsCheckingUpdates = false,
                Updates = EmptyUpdates(),
                UpdateMessage = Failure(ContentFailureCode.BuildInstalling)
            });
            return;
        }

        try
        {
            var (items, registryFailure) = await ListWithRegistryFallbackAsync(instance, kind, cancellationToken).ConfigureAwait(false);
            var available = new Dictionary<string, InstanceContentUpdate>(StringComparer.OrdinalIgnoreCase);
            ContentFailure? updateMessage = registryFailure;

            if (kind == ModrinthContentKind.Mod && instance.ModLoader == InstanceModLoader.Fabric)
            {
                foreach (var item in items.Where(item => item.Origin?.Api is not null))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ChangeState(key, revision, state => state with { CurrentProjectId = FabricClient.FabricApiProjectId });
                    try
                    {
                        var next = await _fabric.GetLatestApiAsync(instance.VersionID, cancellationToken).ConfigureAwait(false);
                        var current = item.Origin!.Api!;
                        if (next.VersionID == current.VersionID || next.Sha512.Equals(current.Sha512, StringComparison.OrdinalIgnoreCase)) continue;
                        var cached = await _fabric.DownloadApiAsync(next, cancellationToken).ConfigureAwait(false);
                        var metadata = await FabricClient.ReadModMetadataAsync(cached, cancellationToken).ConfigureAwait(false);
                        var configuration = instance.FabricConfiguration;
                        if (configuration is null || !metadata.Supports(configuration.LoaderVersion, instance.JavaMajorVersion))
                        {
                            updateMessage = Failure(ContentFailureCode.FabricApiIncompatible,
                                ("version", next.Version), ("loader", configuration?.LoaderVersion ?? ""),
                                ("java", instance.JavaMajorVersion.ToString()));
                            continue;
                        }

                        var project = ApiProject();
                        available[item.LogicalName] = new InstanceContentUpdate(item, project, null, null,
                            item.Origin.Sha512, Api: next);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception)
                    {
                        updateMessage ??= MapFailure(exception);
                    }
                }
            }

            var ordinary = items.Where(item => !item.IsDirectory && item.Origin?.Api is null).ToArray();
            if (ordinary.Length > 0)
            {
                try
                {
                    var matches = await _modrinth.VersionsOfAsync(ordinary.Select(item => item.Path).ToArray(), cancellationToken)
                        .ConfigureAwait(false);
                    var matched = ordinary.Where(item => matches.ContainsKey(item.Path)).ToArray();
                    var matchValues = matched.Select(item => matches[item.Path]).ToArray();
                    var versions = await TryGetVersionsAsync(matchValues.Select(match => match.VersionId), cancellationToken).ConfigureAwait(false);
                    var projects = await TryGetProjectsAsync(matchValues.Select(match => match.ProjectId), cancellationToken).ConfigureAwait(false);
                    var versionsById = versions.ToDictionary(version => version.Id, StringComparer.Ordinal);
                    var projectsById = projects.ToDictionary(project => project.Id, StringComparer.Ordinal);
                    var fileTypes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in matched)
                    {
                        var match = matches[item.Path];
                        var oldVersion = versionsById.GetValueOrDefault(match.VersionId);
                        var oldFileType = item.Origin?.FileType
                            ?? oldVersion?.Files.FirstOrDefault(file => file.Sha512.Equals(match.Sha512, StringComparison.OrdinalIgnoreCase))?.FileType;
                        fileTypes[match.Sha512] = oldFileType;
                    }

                    var attached = kind == ModrinthContentKind.ResourcePack
                        ? matchValues.Where(match => fileTypes.GetValueOrDefault(match.Sha512) is "required-resource-pack" or "optional-resource-pack")
                            .Select(match => match.Sha512).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var latest = new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);
                    var publishedAfter = matched.GroupBy(item => matches[item.Path].Sha512, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.Max(item => matches[item.Path].DatePublished),
                            StringComparer.OrdinalIgnoreCase);
                    var regularHashes = matchValues.Select(match => match.Sha512).Where(hash => !attached.Contains(hash))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (regularHashes.Length > 0)
                        foreach (var pair in await _modrinth.LatestVersionsAfterAsync(regularHashes, kind, instance.VersionID,
                                     publishedAfter, cancellationToken)
                                     .ConfigureAwait(false)) latest[pair.Key] = pair.Value;
                    if (attached.Count > 0)
                        foreach (var pair in await _modrinth.LatestVersionsAfterAsync(attached.ToArray(), ModrinthContentKind.Datapack,
                                     instance.VersionID, publishedAfter, cancellationToken).ConfigureAwait(false)) latest[pair.Key] = pair.Value;

                    var origins = new List<(InstanceContentItem Item, string Hash, ModOrigin Origin)>();
                    foreach (var item in matched)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var match = matches[item.Path];
                        ChangeState(key, revision, state => state with { CurrentProjectId = match.ProjectId });
                        var oldVersion = versionsById.GetValueOrDefault(match.VersionId);
                        var project = projectsById.GetValueOrDefault(match.ProjectId);
                        var oldFileType = item.Origin?.FileType
                            ?? oldVersion?.Files.FirstOrDefault(file => file.Sha512.Equals(match.Sha512, StringComparison.OrdinalIgnoreCase))?.FileType;
                        if (oldVersion is not null && project is not null)
                        {
                            var origin = new ModOrigin
                            {
                                Source = ModSource.Modrinth,
                                ProjectID = match.ProjectId,
                                VersionID = oldVersion.Id,
                                PageURL = project.PageUrl,
                                Sha512 = match.Sha512,
                                VersionNumber = oldVersion.VersionNumber,
                                IconURL = project.IconUrl,
                                FileType = oldFileType
                            };
                            origins.Add((item, match.Sha512, origin));
                        }

                        if (!latest.TryGetValue(match.Sha512, out var next)
                            || next.ProjectId != match.ProjectId || next.DatePublished <= match.DatePublished) continue;
                        var updateKind = attached.Contains(match.Sha512) ? ModrinthContentKind.ResourcePack : kind;
                        var file = next.UpdateFile(updateKind, oldFileType, item.LogicalName);
                        if (file is null || file.Sha512.Equals(match.Sha512, StringComparison.OrdinalIgnoreCase)) continue;
                        var updateProject = project ?? ProjectFallback(match.ProjectId, updateKind);
                        available[item.LogicalName] = new InstanceContentUpdate(item, updateProject, next, file,
                            match.Sha512, attached.Contains(match.Sha512) ? ModrinthContentKind.Datapack : kind);
                    }

                    if (origins.Count > 0)
                    {
                        try
                        {
                            await _content.WithContentLeaseAsync(instanceId, async (leasedRoot, leasedInstance, token) =>
                            {
                                ValidatePathSnapshot(leasedInstance, leasedRoot);
                                foreach (var origin in origins)
                                {
                                    token.ThrowIfCancellationRequested();
                                    await _content.RecordOriginUnderLeaseAsync(leasedRoot, leasedInstance, origin.Item,
                                        kind, origin.Hash, origin.Origin, token).ConfigureAwait(false);
                                }

                                return true;
                            }, cancellationToken).ConfigureAwait(false);
                            items = await _content.ListAsync(instanceId, kind, cancellationToken: cancellationToken).ConfigureAwait(false);
                            foreach (var name in available.Keys.ToArray())
                            {
                                var updatedItem = items.FirstOrDefault(item => item.LogicalName.Equals(name, StringComparison.OrdinalIgnoreCase));
                                if (updatedItem is not null) available[name] = available[name] with { Item = updatedItem };
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception exception)
                        {
                            updateMessage ??= MapFailure(exception);
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    updateMessage ??= MapFailure(exception);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent(key, revision, instance, root)) return;
            ChangeState(key, revision, state => state with
            {
                Items = items,
                Updates = new ReadOnlyDictionary<string, InstanceContentUpdate>(available),
                UpdateMessage = updateMessage,
                Error = registryFailure
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ChangeState(key, revision, state => state with { UpdateMessage = MapFailure(exception) });
        }
        finally
        {
            ChangeState(key, revision, state => state with { IsCheckingUpdates = false, CurrentProjectId = null });
        }
    }

    private async Task<(bool Proceed, InstanceContentItem? Item, ContentFailure? Failure)> PlanCollisionUnderLeaseAsync(
        string root, GameInstance instance, ModrinthContentKind kind, string filename, ModrinthProject project,
        InstanceContentItem? replacing, bool interactive, CancellationToken cancellationToken)
    {
        var items = await _content.ListUnderLeaseAsync(root, instance, kind, true, cancellationToken).ConfigureAwait(false);
        var collision = items.FirstOrDefault(item => LogicalName(kind, item).Equals(filename, StringComparison.OrdinalIgnoreCase));
        if (collision is null || (replacing is not null && PathEquals(collision.Path, replacing.Path)))
            return (true, null, null);
        if (!interactive) throw ControllerException.With(ContentFailureCode.ConfirmationRequired, ("filename", filename));
        var choice = await ConfirmAsync(new ContentConfirmationRequest(ContentConfirmationCode.ReplaceExisting,
            Args(("filename", filename), ("instanceName", instance.Name)), true, false, true,
            [new ContentConfirmationProject(project.Id, project.Title, project.IconUrl)]), cancellationToken).ConfigureAwait(false);
        return choice == ContentConfirmationChoice.Primary
            ? (true, collision, null)
            : (false, null, choice == ContentConfirmationChoice.Cancel ? null
                : Failure(ContentFailureCode.ConfirmationDeclined, ("filename", filename)));
    }

    private async Task<ContentActionResult> ApplyApiUpdateUnderLeaseAsync(string root, GameInstance instance,
        InstanceContentUpdate update, bool interactive, CancellationToken cancellationToken)
    {
        var api = update.Api ?? throw ControllerException.With(ContentFailureCode.StaleContent);
        var configuration = instance.FabricConfiguration;
        if (configuration is null) throw ControllerException.With(ContentFailureCode.FabricApiIncompatible);
        var cached = await _fabric.DownloadApiAsync(api, cancellationToken).ConfigureAwait(false);
        var metadata = await FabricClient.ReadModMetadataAsync(cached, cancellationToken).ConfigureAwait(false);
        if (!metadata.Supports(configuration.LoaderVersion, instance.JavaMajorVersion))
            throw ControllerException.With(ContentFailureCode.FabricApiIncompatible,
                ("version", api.Version), ("loader", configuration.LoaderVersion), ("java", instance.JavaMajorVersion.ToString()));
        var staging = Path.Combine(Path.GetTempPath(), "Hako-Modrinth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var staged = Path.Combine(staging, api.Filename);
            await using (var input = new FileStream(cached, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            await _content.ValidateExpectedHashUnderLeaseAsync(root, instance, update.Item, ModrinthContentKind.Mod,
                update.CurrentSha512, cancellationToken).ConfigureAwait(false);
            var collision = await PlanCollisionUnderLeaseAsync(root, instance, ModrinthContentKind.Mod,
                api.Filename, update.Project, update.Item, interactive, cancellationToken).ConfigureAwait(false);
            if (!collision.Proceed) return new ContentActionResult(false, collision.Failure);
            ValidatePathSnapshot(instance, root);
            await _content.ReplaceUnderLeaseAsync(root, instance, update.Item, ModrinthContentKind.Mod, staged,
                api.Filename, update.CurrentSha512, ModOrigin.FromApi(api), cancellationToken, collision.Item).ConfigureAwait(false);
            return new ContentActionResult(true);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private async Task<(IReadOnlyList<InstanceContentItem> Items, ContentFailure? Failure)> ListWithRegistryFallbackAsync(
        GameInstance instance, ModrinthContentKind kind, CancellationToken cancellationToken)
    {
        try
        {
            return (await _content.ListAsync(instance.Id, kind, cancellationToken: cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception exception) when (IsRegistryCorruption(exception))
        {
            var items = await _content.ListAsync(instance.Id, kind, includeDisabled: true, readOrigins: false, cancellationToken)
                .ConfigureAwait(false);
            return (items, Failure(ContentFailureCode.RegistryCorrupt));
        }
    }

    private async Task RefreshTouchedUnderLeaseAsync(OperationContext operation)
    {
        foreach (var kind in operation.TouchedKinds)
        {
            var key = new InstanceContentStateKey(operation.InstanceId, kind);
            try
            {
                ValidatePathSnapshot(operation.Instance!, operation.Root!);
                var (items, failure) = await ListWithRegistryFallbackAsync(operation.Root!, operation.Instance!, kind,
                    CancellationToken.None, underLease: true).ConfigureAwait(false);
                ChangeState(key, operation.Revision(key), state => state with
                {
                    Items = items,
                    Updates = RetainUpdates(state.Updates, items),
                    Error = failure
                });
            }
            catch (Exception exception)
            {
                ChangeState(key, operation.Revision(key), state => state with { Error = MapFailure(exception) });
            }
        }
    }

    private async Task RefreshTouchedOutsideLeaseAsync(OperationContext operation)
    {
        if (operation.Instance is null) return;
        foreach (var kind in operation.TouchedKinds)
        {
            var key = new InstanceContentStateKey(operation.InstanceId, kind);
            try
            {
                var (items, failure) = await ListWithRegistryFallbackAsync(operation.Instance, kind,
                    CancellationToken.None).ConfigureAwait(false);
                if (!IsCurrent(key, operation.Revision(key), operation.Instance, operation.Root!)) continue;
                ChangeState(key, operation.Revision(key), state => state with
                {
                    Items = items,
                    Updates = RetainUpdates(state.Updates, items),
                    Error = failure
                });
            }
            catch (Exception exception)
            {
                ChangeState(key, operation.Revision(key), state => state with { Error = MapFailure(exception) });
            }
        }
    }

    private async Task<(IReadOnlyList<InstanceContentItem> Items, ContentFailure? Failure)> ListWithRegistryFallbackAsync(
        string root, GameInstance instance, ModrinthContentKind kind, CancellationToken cancellationToken,
        bool underLease = false)
    {
        try
        {
            var items = underLease
                ? await _content.ListUnderLeaseAsync(root, instance, kind, true, cancellationToken).ConfigureAwait(false)
                : await _content.ListAsync(instance.Id, kind, cancellationToken: cancellationToken).ConfigureAwait(false);
            return (items, null);
        }
        catch (Exception exception) when (IsRegistryCorruption(exception))
        {
            var items = underLease
                ? await _content.ListUnderLeaseAsync(root, instance, kind, true, cancellationToken, readOrigins: false).ConfigureAwait(false)
                : await _content.ListAsync(instance.Id, kind, true, readOrigins: false, cancellationToken).ConfigureAwait(false);
            return (items, Failure(ContentFailureCode.RegistryCorrupt));
        }
    }

    private async Task<IReadOnlyList<ModrinthVersion>> TryGetVersionsAsync(IEnumerable<string> ids,
        CancellationToken cancellationToken)
    {
        var values = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (values.Length == 0) return [];
        try { return await _modrinth.GetVersionsAsync(values, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    private async Task<IReadOnlyList<ModrinthProject>> TryGetProjectsAsync(IEnumerable<string> ids,
        CancellationToken cancellationToken)
    {
        var values = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (values.Length == 0) return [];
        try { return await _modrinth.GetProjectsAsync(values, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    private async Task<IReadOnlyList<ContentConfirmationProject>> CompatibilityNotesAsync(string jarPath,
        string loader, int java, CancellationToken cancellationToken)
    {
        FabricModMetadata metadata;
        try
        {
            var fileInfo = new FileInfo(jarPath);
            if (!fileInfo.Exists || fileInfo.Length == 0) return [];
            metadata = await FabricClient.ReadModMetadataAsync(jarPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return []; }

        var notes = new List<ContentConfirmationProject>();
        if (metadata.Depends?.TryGetValue("fabricloader", out var loaderPredicate) == true
            && TryMatches(loaderPredicate, loader, out var loaderMatches) && !loaderMatches)
        {
            notes.Add(new ContentConfirmationProject(metadata.Id, metadata.Id, null,
                ContentConfirmationNoteCode.FabricLoaderRequirement,
                Args(("required", string.Join(" | ", loaderPredicate.Alternatives)), ("current", loader))));
        }

        if (java > 0 && metadata.Depends?.TryGetValue("java", out var javaPredicate) == true
            && TryMatches(javaPredicate, java.ToString(System.Globalization.CultureInfo.InvariantCulture), out var javaMatches)
            && !javaMatches)
        {
            notes.Add(new ContentConfirmationProject(metadata.Id, metadata.Id, null,
                ContentConfirmationNoteCode.JavaRequirement,
                Args(("required", string.Join(" | ", javaPredicate.Alternatives)), ("current", java.ToString()))));
        }

        return notes;
    }

    private static bool TryMatches(FabricVersionPredicate predicate, string version, out bool matches)
    {
        try { matches = predicate.Matches(version); return true; }
        catch { matches = false; return false; }
    }

    private async Task<IReadOnlyList<string>> IncompatibleProjectIdsAsync(ModrinthVersion version,
        CancellationToken cancellationToken)
    {
        var incompatible = (version.Dependencies ?? []).Where(dependency => dependency.Type == "incompatible").ToArray();
        var resolved = await ResolveDependencyProjectsAsync(incompatible, cancellationToken).ConfigureAwait(false);
        return resolved.Where(item => item.ProjectId is not null).Select(item => item.ProjectId!).Distinct(StringComparer.Ordinal).ToArray();
    }

    private async Task<ContentConfirmationChoice> ConfirmAsync(ContentConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        if (_confirm is null) return ContentConfirmationChoice.Cancel;
        await _confirmationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _confirm(request, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _confirmationGate.Release();
        }
    }

    private InstanceContentState? TryBeginRead(InstanceContentStateKey key, bool checkingUpdates)
    {
        lock (_sync)
        {
            var previous = _states.GetValueOrDefault(key) ?? new InstanceContentState();
            if (previous.IsBusy || (checkingUpdates && previous.IsCheckingUpdates)) return null;
            var revision = _revisions.GetValueOrDefault(key) + 1;
            _revisions[key] = revision;
            var initial = checkingUpdates
                ? previous with { Revision = revision, IsCheckingUpdates = true, IsLoading = false, CurrentProjectId = null, UpdateMessage = null }
                : previous with { Revision = revision, IsLoading = true, IsCheckingUpdates = false, CurrentProjectId = null, Error = null };
            _states[key] = initial;
            return Snapshot(initial);
        }
    }

    private void ChangeState(InstanceContentStateKey key, long revision, Func<InstanceContentState, InstanceContentState> change)
    {
        InstanceContentState? snapshot = null;
        lock (_sync)
        {
            if (_revisions.GetValueOrDefault(key) != revision) return;
            var previous = _states.GetValueOrDefault(key) ?? new InstanceContentState();
            _states[key] = change(previous) with { Revision = revision };
            snapshot = Snapshot(_states[key]);
        }

        RaiseStateChanged(key, snapshot);
    }

    private void ClearBusyUnderOperationGate(InstanceContentStateKey key)
    {
        InstanceContentState snapshot;
        lock (_sync)
        {
            // The per-instance operation gate is still held: no other mutation can own busy.
            // Read revisions govern publication of read results, never mutation cleanup.
            var previous = _states[key];
            _states[key] = previous with { IsBusy = false, BusyOperation = null, CurrentProjectId = null };
            snapshot = Snapshot(_states[key]);
        }

        RaiseStateChanged(key, snapshot);
    }

    private void SetReadError(Guid instanceId, ModrinthContentKind kind, long revision, ContentFailure failure)
    {
        // A read result belongs to its starting revision and cannot supersede a mutation.
        var key = new InstanceContentStateKey(instanceId, kind);
        ChangeState(key, revision, state => state with { Error = failure });
    }

    private void RaiseStateChanged(InstanceContentStateKey key, InstanceContentState snapshot)
    {
        var handlers = StateChanged;
        if (handlers is null) return;
        foreach (EventHandler<InstanceContentStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, new InstanceContentStateChangedEventArgs(key, snapshot)); }
            catch { }
        }
    }

    private bool IsCurrent(InstanceContentStateKey key, long revision, GameInstance instance, string root)
    {
        lock (_sync)
            if (_revisions.GetValueOrDefault(key) != revision) return false;
        try
        {
            var current = _store.List().FirstOrDefault(candidate => candidate.Id == instance.Id);
            return current is not null && current.FolderName == instance.FolderName
                && PathEquals(_store.GetInstanceDirectory(instance.Id), root);
        }
        catch { return false; }
    }

    private GameInstance FindInstance(Guid instanceId) => _store.List().FirstOrDefault(instance => instance.Id == instanceId)
        ?? throw ControllerException.With(ContentFailureCode.BuildUnavailable);

    private void ValidatePathSnapshot(GameInstance instance, string root)
    {
        var current = _store.List().FirstOrDefault(candidate => candidate.Id == instance.Id);
        if (current is null || !string.Equals(current.FolderName, instance.FolderName, StringComparison.Ordinal)
            || !PathEquals(_store.GetInstanceDirectory(instance.Id), root))
            throw ControllerException.With(ContentFailureCode.StaleContent);
        _ = InstanceStorage.ContainedPath("minecraft/mods", root, allowBaseReparsePoint: true);
        _ = InstanceStorage.ContainedPath(instance.LegacyTexturepacks ? "minecraft/texturepacks" : "minecraft/resourcepacks",
            root, allowBaseReparsePoint: true);
    }

    private static IReadOnlyDictionary<string, InstanceContentUpdate> RetainUpdates(
        IReadOnlyDictionary<string, InstanceContentUpdate> updates, IReadOnlyList<InstanceContentItem> items)
    {
        var retained = new Dictionary<string, InstanceContentUpdate>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, update) in updates)
        {
            var current = items.FirstOrDefault(item => !item.IsDirectory && item.LogicalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (current is null || !PathEquals(current.Path, update.Item.Path)) continue;
            retained[name] = update with { Item = current };
        }

        return new ReadOnlyDictionary<string, InstanceContentUpdate>(retained);
    }

    private static InstanceContentState Snapshot(InstanceContentState state) => state with
    {
        Items = state.Items.ToArray(),
        Updates = new ReadOnlyDictionary<string, InstanceContentUpdate>(
            state.Updates.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase))
    };

    private static ContentFailure MapFailure(Exception exception)
    {
        if (exception is ControllerException controller) return controller.Failure;
        if (IsRegistryCorruption(exception)) return Failure(ContentFailureCode.RegistryCorrupt);
        if (exception is InstanceRegistryException registry)
            return Failure(registry.Error == InstanceRegistryError.ReadFailure
                ? ContentFailureCode.RegistryUnavailable : ContentFailureCode.RegistryCorrupt);
        if (exception is InstanceStorageException storage)
            return Failure(storage.Error switch
            {
                InstanceStorageError.BuildRunning or InstanceStorageError.BuildFilesBusy => ContentFailureCode.BuildBusy,
                InstanceStorageError.BuildInstalling or InstanceStorageError.InstanceRegistryNotLoaded => ContentFailureCode.BuildInstalling,
                InstanceStorageError.InstanceRegistryUnavailable => ContentFailureCode.RegistryUnavailable,
                _ => ContentFailureCode.ContentOperationFailed
            });
        if (exception is MojangException) return Failure(ContentFailureCode.ModrinthRequestFailed);
        if (exception is HttpRequestException or JsonException) return Failure(ContentFailureCode.ModrinthRequestFailed);
        if (exception is IOException) return Failure(ContentFailureCode.ContentOperationFailed, ("diagnostic", exception.Message));
        return Failure(ContentFailureCode.ContentOperationFailed);
    }

    private static bool IsRegistryCorruption(Exception exception) => exception is InvalidDataException or JsonException;

    private static void ValidateKind(ModrinthContentKind kind)
    {
        if (kind is not (ModrinthContentKind.Mod or ModrinthContentKind.ResourcePack))
            throw new NotSupportedException("World datapacks are handled in a later stage.");
    }

    private static string Loader(ModrinthContentKind kind) => kind switch
    {
        ModrinthContentKind.Mod => "fabric",
        ModrinthContentKind.ResourcePack => "minecraft",
        ModrinthContentKind.Datapack => "datapack",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string LogicalName(ModrinthContentKind kind, InstanceContentItem item) => kind == ModrinthContentKind.Mod
        ? item.LogicalName
        : item.Name;

    private static string LogicalName(ModrinthContentKind kind, string name) =>
        kind == ModrinthContentKind.Mod && name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
            ? name[..^9] : name;

    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left),
        Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static ContentConfirmationProject ProjectNote(ModrinthProject project, ContentConfirmationNoteCode? code) =>
        new(project.Id, project.Title, project.IconUrl, code);

    private static ModOrigin OriginFor(PlannedDownload download)
    {
        if (download.Project.Id == FabricClient.FabricApiProjectId)
        {
            var api = new FabricApiDescriptor(download.Project.Id, download.Version.Id, download.Version.VersionNumber,
                download.Version.VersionType, download.File.Filename, download.File.Url.AbsoluteUri,
                download.File.Size, download.File.Sha1, download.File.Sha512);
            return ModOrigin.FromApi(api);
        }

        return new ModOrigin
        {
            Source = ModSource.Modrinth,
            ProjectID = download.Project.Id,
            VersionID = download.Version.Id,
            PageURL = download.Project.PageUrl,
            Sha512 = download.File.Sha512,
            VersionNumber = download.Version.VersionNumber,
            IconURL = download.Project.IconUrl,
            FileType = download.File.FileType
        };
    }

    private static ModrinthProject ApiProject() => new()
    {
        Id = FabricClient.FabricApiProjectId,
        Title = "Fabric API",
        ProjectType = "mod"
    };

    private static ModrinthProject ProjectFallback(string projectId, ModrinthContentKind kind) => new()
    {
        Id = projectId,
        Title = projectId,
        ProjectType = kind == ModrinthContentKind.Mod ? "mod" : "resourcepack"
    };

    private static void ValidateStagedFile(string staging, string path)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(staging, full);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
            throw ControllerException.With(ContentFailureCode.StaleContent);
        var attributes = File.GetAttributes(full);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw ControllerException.With(ContentFailureCode.StaleContent);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }

    private static ContentFailure Failure(ContentFailureCode code, params (string Key, string Value)[] values) =>
        new(code, Args(values.Select(value => (value.Key, (string?)value.Value)).ToArray()));

    private static IReadOnlyDictionary<string, InstanceContentUpdate> EmptyUpdates() =>
        new ReadOnlyDictionary<string, InstanceContentUpdate>(new Dictionary<string, InstanceContentUpdate>(StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<string, string> Args(params (string Key, string? Value)[] values) =>
        new ReadOnlyDictionary<string, string>(values.Where(value => value.Value is not null)
            .ToDictionary(value => value.Key, value => value.Value!, StringComparer.Ordinal));

    private sealed class OperationContext(InstanceContentController owner, Guid instanceId,
        InstanceContentOperation operation, CancellationToken cancellationToken)
    {
        private readonly Dictionary<InstanceContentStateKey, long> _revisions = [];
        private readonly HashSet<ModrinthContentKind> _touched = [];
        public Guid InstanceId { get; } = instanceId;
        public InstanceContentOperation Operation { get; } = operation;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public GameInstance? Instance { get; private set; }
        public string? Root { get; private set; }
        public bool CallbackEntered { get; set; }
        public IEnumerable<ModrinthContentKind> TouchedKinds => _touched;
        public IEnumerable<InstanceContentStateKey> Keys => _revisions.Keys.ToArray();

        public void SetSnapshot(GameInstance instance, string root)
        {
            Instance = instance;
            Root = root;
        }

        public void Touch(ModrinthContentKind kind)
        {
            if (!_touched.Add(kind)) return;
            var key = new InstanceContentStateKey(InstanceId, kind);
            InstanceContentState snapshot;
            lock (owner._sync)
            {
                var revision = owner._revisions.GetValueOrDefault(key) + 1;
                owner._revisions[key] = revision;
                _revisions[key] = revision;
                var state = owner._states.GetValueOrDefault(key) ?? new InstanceContentState();
                owner._states[key] = state with
                {
                    Revision = revision,
                    IsBusy = true,
                    BusyOperation = Operation,
                    IsCheckingUpdates = false,
                    IsLoading = false,
                    Updates = EmptyUpdates(),
                    Error = null,
                    UpdateMessage = null
                };
                snapshot = Snapshot(owner._states[key]);
            }

            owner.RaiseStateChanged(key, snapshot);
        }

        public long Revision(InstanceContentStateKey key) => _revisions.GetValueOrDefault(key);

        public void SetCurrentProject(string? projectId)
        {
            foreach (var key in _revisions.Keys.ToArray())
                owner.ChangeState(key, Revision(key), state => state with { CurrentProjectId = projectId });
        }
    }

    private sealed class DependencyPlan
    {
        public List<PlannedDownload> Downloads { get; } = [];
        public List<PlannedEnable> Enable { get; } = [];
        public List<ContentConfirmationProject> Listed { get; } = [];
    }

    private sealed record PlannedDownload(ModrinthProject Project, ModrinthVersion Version,
        ModrinthFile File, ModrinthContentKind Kind, bool IsRoot);
    private sealed record PlannedEnable(InstanceContentItem Item, ModrinthContentKind Kind);
    private sealed record ResolvedDependency(ModrinthDependency Dependency, string? ProjectId, ModrinthProject? Project);

    private sealed class ControllerException(ContentFailure failure) : Exception
    {
        public ContentFailure Failure { get; } = failure;
        public static ControllerException With(ContentFailureCode code, params (string Key, string Value)[] arguments) =>
            new(InstanceContentController.Failure(code, arguments));
    }
}

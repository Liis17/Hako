using System.Runtime.Versioning;
using Hako.Core;

namespace Hako.Core.Instances;

public sealed record InstanceScreenshot(string Path, DateTimeOffset? CreatedAt)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public sealed record ScreenshotTrashResult(RecycleBinResult Results)
{
    public bool Succeeded => Results.Items.All(item => item.Status == RecycleBinItemStatus.Recycled);
    public bool HasFailures => Results.Items.Any(item => item.Status != RecycleBinItemStatus.Recycled);
}

/// <summary>Enumerates and trashes PNG screenshots owned by one registered build.</summary>
[SupportedOSPlatform("windows")]
public sealed class InstanceScreenshots
{
    private readonly InstanceStore _store;
    private readonly RecycleBin _recycleBin;

    public InstanceScreenshots(InstanceStore store, RecycleBin? recycleBin = null)
    {
        _store = store;
        _recycleBin = recycleBin ?? new RecycleBin();
    }

    public async Task<IReadOnlyList<InstanceScreenshot>> ListAsync(Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var root = await Task.Run(() => _store.GetInstanceDirectory(instanceId), cancellationToken)
            .ConfigureAwait(false);
        return await Task.Run(() => ListAt(root, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates the screenshots folder only after an explicit Open Folder request.</summary>
    public async Task<string> GetOrCreateFolderAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        using var lease = await BeginMutationAsync(instanceId, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var root = _store.GetInstanceDirectory(instanceId);
            var folder = ScreenshotFolder(root, create: true);
            return folder;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScreenshotTrashResult> TrashAsync(Guid instanceId, IEnumerable<InstanceScreenshot> screenshots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(screenshots);
        using var lease = await BeginMutationAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var root = await Task.Run(() => _store.GetInstanceDirectory(instanceId), cancellationToken)
            .ConfigureAwait(false);
        var folder = await Task.Run(() => ScreenshotFolder(root, create: false), cancellationToken)
            .ConfigureAwait(false);
        if (!Directory.Exists(folder))
            return new ScreenshotTrashResult(new RecycleBinResult([]));

        var items = screenshots.ToArray();
        var failures = new Dictionary<int, RecycleBinItemResult>();
        var paths = new List<(int Index, string Path)>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < items.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[index];
            try
            {
                var name = System.IO.Path.GetFileName(item.Path);
                if (!InstanceStorage.IsSinglePathSegment(name)
                    || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The selected file is not a PNG screenshot.");

                var ownedPath = InstanceStorage.ContainedPath(Path.Combine("minecraft", "screenshots", name), root,
                    allowBaseReparsePoint: true);
                if (!PathEquals(item.Path, ownedPath))
                    throw new IOException("The selected file does not belong to this build's screenshots folder.");
                var attributes = File.GetAttributes(ownedPath);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Hidden)) != 0)
                    throw new IOException("The selected screenshot is not a visible regular file.");
                if (!unique.Add(ownedPath)) continue;
                paths.Add((index, ownedPath));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                failures[index] = new RecycleBinItemResult(item.Path, RecycleBinItemStatus.Failed, exception.Message);
            }
        }

        var recycled = paths.Count == 0
            ? new RecycleBinResult([])
            : await _recycleBin.DeleteAsync(paths.Select(item => item.Path), cancellationToken).ConfigureAwait(false);
        var recycleResults = recycled.Items.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var results = new List<RecycleBinItemResult>(items.Length);
        for (var index = 0; index < items.Length; index++)
        {
            if (failures.TryGetValue(index, out var failure))
            {
                results.Add(failure);
                continue;
            }

            var name = System.IO.Path.GetFileName(items[index].Path);
            var path = Path.Combine(folder, name);
            if (recycleResults.TryGetValue(Path.GetFullPath(path), out var result)) results.Add(result);
        }
        return new ScreenshotTrashResult(new RecycleBinResult(results));
    }

    private async Task<IDisposable> BeginMutationAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var result = await Task.Run(() => _store.TryBeginScreenshotMutation(instanceId, out var lease, out var reason)
                ? (lease, reason)
                : (null, reason), cancellationToken)
            .ConfigureAwait(false);
        return result.lease ?? throw new InstanceStorageException(result.reason ?? InstanceStorageError.BuildFilesBusy,
            "Screenshots cannot be changed while the build is installing or another file operation is active.");
    }

    private static IReadOnlyList<InstanceScreenshot> ListAt(string root, CancellationToken cancellationToken)
    {
        var folder = ScreenshotFolder(root, create: false);
        if (!Directory.Exists(folder)) return Array.Empty<InstanceScreenshot>();
        var results = new List<InstanceScreenshot>();
        foreach (var path in Directory.EnumerateFileSystemEntries(folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!System.IO.Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)) continue;
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Hidden)) != 0)
                continue;

            var created = File.GetCreationTimeUtc(path);
            var modified = File.GetLastWriteTimeUtc(path);
            var date = created.Year <= 1601 ? modified : created;
            results.Add(new InstanceScreenshot(Path.GetFullPath(path), new DateTimeOffset(date, TimeSpan.Zero)));
        }

        return results.OrderByDescending(item => item.CreatedAt)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ScreenshotFolder(string instanceRoot, bool create)
    {
        var minecraft = InstanceStorage.ContainedPath("minecraft", instanceRoot, allowBaseReparsePoint: true);
        if (File.Exists(minecraft)) throw new IOException("The minecraft path is not a directory.");
        if (!Directory.Exists(minecraft)) return InstanceStorage.ContainedPath("minecraft/screenshots", instanceRoot, allowBaseReparsePoint: true);

        var folder = InstanceStorage.ContainedPath("minecraft/screenshots", instanceRoot, allowBaseReparsePoint: true);
        if (File.Exists(folder)) throw new IOException("The screenshots path is not a directory.");
        if (Directory.Exists(folder))
        {
            var attributes = File.GetAttributes(folder);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != FileAttributes.Directory)
                throw new IOException("The screenshots folder is not a regular directory.");
            return folder;
        }

        if (create) InstanceStorage.CreateDirectoryExclusiveAt(minecraft, "screenshots");
        return folder;
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}

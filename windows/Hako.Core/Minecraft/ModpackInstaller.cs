using System.Collections.Concurrent;
using Hako.Core.Instances;

namespace Hako.Core.Minecraft;

public sealed class ModpackInstaller
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CacheGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AllowedManifestHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.modrinth.com",
        "github.com",
        "raw.githubusercontent.com",
        "gitlab.com"
    };
    private static readonly HashSet<string> AllowedArchiveHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.modrinth.com"
    };
    private readonly ModrinthClient _modrinth;
    private readonly MojangClient _minecraft;
    private readonly FabricClient _fabric;
    private readonly string _cacheRoot;

    public ModpackInstaller(ModrinthClient modrinth, MojangClient minecraft, FabricClient fabric, HakoPaths paths)
    {
        _modrinth = modrinth;
        _minecraft = minecraft;
        _fabric = fabric;
        _cacheRoot = Path.Combine(Path.GetFullPath(paths.CacheRoot), "Modpacks");
    }

    public Task<byte[]?> DownloadIconAsync(ModrinthProject project, CancellationToken cancellationToken = default) =>
        _modrinth.DownloadImageAsync(project.IconUrl, cancellationToken);

    public async Task<PreparedModpack> PrepareAsync(
        ModrinthProject project,
        ModrinthVersion version,
        string? minecraftFilter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(version);
        var archive = SelectArchive(version);
        var pinnedArchive = ModpackFile.From(archive);
        if (project.ProjectType != "modpack"
            || version.ProjectId != project.Id
            || version.Loaders.Count == 0
            || version.Loaders.Any(loader => loader is not ("fabric" or "minecraft")))
        {
            throw new ModpackException(ModpackError.InvalidManifest);
        }

        var archivePath = await GetCachedArchiveAsync(pinnedArchive, cancellationToken).ConfigureAwait(false);
        var contents = await Task.Run(() => ModpackArchive.Inspect(archivePath), cancellationToken).ConfigureAwait(false);
        var manifest = contents.Manifest;
        var expectedLoader = manifest.LoaderVersion is null ? "minecraft" : "fabric";
        if (!version.GameVersions.Contains(manifest.Minecraft, StringComparer.Ordinal)
            || !version.Loaders.Contains(expectedLoader, StringComparer.Ordinal)
            || minecraftFilter is not null && manifest.Minecraft != minecraftFilter)
        {
            throw new ModpackException(ModpackError.InvalidManifest);
        }

        var catalog = await _minecraft.CatalogAsync(cancellationToken).ConfigureAwait(false);
        var gameVersion = catalog.Versions.FirstOrDefault(item =>
            string.Equals(item.Id, manifest.Minecraft, StringComparison.Ordinal))
            ?? throw new ModpackException(ModpackError.InvalidManifest);
        var preparedMinecraft = await _minecraft.PrepareAsync(gameVersion, cancellationToken).ConfigureAwait(false);
        FabricProfile? fabricProfile = null;
        if (manifest.LoaderVersion is { } loader)
        {
            var loaders = await _fabric.GetLoaderVersionsAsync(manifest.Minecraft, cancellationToken).ConfigureAwait(false);
            if (!loaders.Any(item => string.Equals(item.Version, loader, StringComparison.Ordinal)))
            {
                throw new ModpackException(ModpackError.InvalidManifest);
            }

            fabricProfile = (await _fabric.GetProfileAsync(manifest.Minecraft, loader, cancellationToken).ConfigureAwait(false)).Profile;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var plan = new ModpackPlan
        {
            ProjectId = project.Id,
            VersionId = version.Id,
            VersionNumber = version.VersionNumber,
            PageUrl = project.PageUrl,
            Archive = pinnedArchive,
            Manifest = manifest
        };
        plan.Validate();
        return new PreparedModpack(plan, preparedMinecraft, fabricProfile);
    }

    public ModpackPlan? Validate(string instanceRoot, GameInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var plan = ModpackPlan.Read(instanceRoot);
        if (instance.ModpackVersionID is null)
        {
            if (plan is not null) throw new ModpackException(ModpackError.InvalidManifest);
            return null;
        }

        if (plan is null
            || !string.Equals(plan.VersionId, instance.ModpackVersionID, StringComparison.Ordinal)
            || !string.Equals(plan.Manifest.Minecraft, instance.VersionID, StringComparison.Ordinal)
            || plan.Manifest.LoaderVersion is null
                && (instance.ModLoader != InstanceModLoader.Vanilla || instance.FabricConfiguration is not null)
            || plan.Manifest.LoaderVersion is { } expectedLoader
                && (instance.ModLoader != InstanceModLoader.Fabric
                    || instance.FabricConfiguration is not { Api: null } configuration
                    || !string.Equals(configuration.LoaderVersion, expectedLoader, StringComparison.Ordinal)))
        {
            throw new ModpackException(ModpackError.InvalidManifest);
        }

        return plan;
    }

    public async Task InstallAsync(string instanceRoot, IProgress<InstallationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var plan = ModpackPlan.Read(instanceRoot)
            ?? throw new ModpackException(ModpackError.InvalidManifest);
        if (plan.Completed) return;

        plan.Validate();
        progress?.Report(new InstallationProgress(InstallationProgressStage.PreparingModpack));
        var archivePath = await GetCachedArchiveAsync(plan.Archive, cancellationToken).ConfigureAwait(false);
        var contents = await Task.Run(() => ModpackArchive.Inspect(archivePath), cancellationToken).ConfigureAwait(false);
        if (!contents.Manifest.EquivalentTo(plan.Manifest))
        {
            throw new ModpackException(ModpackError.InvalidManifest);
        }

        var files = plan.Manifest.Files.Where(file => file.InstallOnClient).ToArray();
        var totalFileBytes = files.Aggregate(0L, (total, file) => checked(total + file.FileSize));
        var totalOverrideBytes = contents.Overrides.Aggregate(0L, (total, file) => checked(total + file.Size));
        if (totalFileBytes > ModpackArchiveLimits.Default.MaximumExpandedSize - totalOverrideBytes)
        {
            throw new ModpackException(ModpackError.InvalidManifest);
        }

        var minecraftRoot = InstanceStorage.ContainedPath("minecraft", instanceRoot);
        if (!Directory.Exists(minecraftRoot) || IsReparsePoint(minecraftRoot))
        {
            throw new ModpackException(ModpackError.UnsafePath);
        }

        var staging = InstanceStorage.ContainedPath(".hako-modpack-" + Guid.NewGuid().ToString("N"), instanceRoot);
        if (File.Exists(staging) || Directory.Exists(staging))
        {
            throw new ModpackException(ModpackError.UnsafePath);
        }

        Directory.CreateDirectory(staging);
        try
        {
            var cacheFiles = Path.Combine(_cacheRoot, "files");
            Directory.CreateDirectory(cacheFiles);
            var downloadedBytes = 0L;
            var completedFiles = 0;
            progress?.Report(new InstallationProgress(InstallationProgressStage.DownloadingModpack,
                TotalBytes: totalFileBytes, TotalFiles: files.Length));
            using (var limiter = new SemaphoreSlim(4))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var tasks = files.Select(async file =>
                {
                    await limiter.WaitAsync(linked.Token).ConfigureAwait(false);
                    try
                    {
                        var cached = await GetCachedFileAsync(file, cacheFiles, linked.Token).ConfigureAwait(false);
                        var destination = InstanceStorage.ContainedPath(file.Path, staging);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        await CopyFileAsync(cached, destination, linked.Token).ConfigureAwait(false);
                        var bytes = Interlocked.Add(ref downloadedBytes, file.FileSize);
                        var count = Interlocked.Increment(ref completedFiles);
                        progress?.Report(new InstallationProgress(InstallationProgressStage.DownloadingModpack,
                            bytes, totalFileBytes, count, files.Length));
                    }
                    catch
                    {
                        linked.Cancel();
                        throw;
                    }
                    finally
                    {
                        limiter.Release();
                    }
                }).ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            var extractedBytes = 0L;
            foreach (var item in contents.Overrides)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = InstanceStorage.ContainedPath(item.Path, staging);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination)) File.Delete(destination);
                var remaining = ModpackArchiveLimits.Default.MaximumExpandedSize - totalFileBytes - extractedBytes;
                await ModpackArchive.ExtractAsync(archivePath, item, destination,
                    Math.Min(ModpackArchiveLimits.Default.MaximumFileSize, remaining), cancellationToken).ConfigureAwait(false);
                extractedBytes = checked(extractedBytes + item.Size);
            }

            var stagedPaths = files.Select(file => file.Path)
                .Concat(contents.Overrides.Select(item => item.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            foreach (var path in stagedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = DestinationPath(path, minecraftRoot);
            }

            progress?.Report(new InstallationProgress(InstallationProgressStage.InstallingModpackContents,
                totalFileBytes, totalFileBytes, files.Length, files.Length));
            foreach (var path in stagedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = InstanceStorage.ContainedPath(path, staging);
                var destination = DestinationPath(path, minecraftRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                _ = DestinationPath(path, minecraftRoot);
                File.Move(source, destination, overwrite: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await (plan with { Completed = true }).WriteAsync(instanceRoot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteOwnedStaging(staging);
        }
    }

    private async Task<string> GetCachedArchiveAsync(ModpackFile file, CancellationToken cancellationToken)
    {
        if (!file.IsInstallable(ModpackArchiveLimits.Default.MaximumFileSize))
            throw new ModpackException(ModpackError.InvalidManifest);
        var directory = Path.Combine(_cacheRoot, "archives");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, file.Sha512.ToLowerInvariant() + ".mrpack");
        return await GetCachedAsync(destination, file.Size, file.Sha1, file.Sha512,
            [file.Url], AllowedArchiveHosts, cancellationToken).ConfigureAwait(false);
    }

    private Task<string> GetCachedFileAsync(ModpackManifestFile file, string cacheDirectory, CancellationToken cancellationToken)
    {
        var sha1 = file.Hashes["sha1"];
        var sha512 = file.Hashes["sha512"];
        var destination = Path.Combine(cacheDirectory, sha512.ToLowerInvariant());
        return GetCachedAsync(destination, file.FileSize, sha1, sha512, file.Downloads,
            AllowedManifestHosts, cancellationToken, allowEmpty: true);
    }

    private async Task<string> GetCachedAsync(string destination, long size, string sha1, string sha512,
        IReadOnlyList<Uri> urls, IReadOnlySet<string>? allowedHosts, CancellationToken cancellationToken,
        bool allowEmpty = false)
    {
        var gate = CacheGates.GetOrAdd(destination, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureRegularCacheFileOrMissing(destination);
            if (File.Exists(destination)
                && await ModrinthClient.IsVerifiedFileAsync(destination, size, sha1, sha512, cancellationToken)
                    .ConfigureAwait(false))
            {
                return destination;
            }

            if (File.Exists(destination)) File.Delete(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".hako-modpack-" + Guid.NewGuid().ToString("N"));
            try
            {
                try
                {
                    await _modrinth.DownloadVerifiedFileAsync(urls, temporary, size, sha1, sha512, allowedHosts,
                        cancellationToken, allowEmpty).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new ModpackException(ModpackError.DamagedFile, exception);
                }

                EnsureRegularCacheFileOrMissing(destination);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (FileNotFoundException) { }
            }

            return destination;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string DestinationPath(string relativePath, string minecraftRoot)
    {
        ModpackPaths.Validate(relativePath);
        var destination = InstanceStorage.ContainedPath(relativePath, minecraftRoot);
        var current = minecraftRoot;
        if (IsReparsePoint(current)) throw new ModpackException(ModpackError.UnsafePath);
        var segments = relativePath.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                try
                {
                    _ = File.GetAttributes(current);
                }
                catch (FileNotFoundException)
                {
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }
            }

            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0
                || index < segments.Length - 1 && (attributes & FileAttributes.Directory) == 0
                || index == segments.Length - 1 && (attributes & FileAttributes.Directory) != 0)
            {
                throw new ModpackException(ModpackError.UnsafePath);
            }
        }

        return destination;
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void EnsureRegularCacheFileOrMissing(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
                throw new ModpackException(ModpackError.UnsafePath);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void DeleteOwnedStaging(string path)
    {
        if (!Directory.Exists(path) || IsReparsePoint(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static ModrinthFile SelectArchive(ModrinthVersion version)
    {
        var candidates = version.Files.Where(file => file.IsInstallable(".mrpack")).ToArray();
        return candidates.FirstOrDefault(file => file.Primary) ?? candidates.FirstOrDefault()
            ?? throw new ModpackException(ModpackError.InvalidManifest);
    }
}

using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Hako.Core.Instances;

namespace Hako.Core.Minecraft;

public enum InstallationProgressStage
{
    PreparingMetadata,
    PreparingFiles,
    Downloading,
    PreparingLibraries,
    PreparingAssets,
    PreparingModpack,
    DownloadingModpack,
    InstallingModpackContents,
    Complete,
    Pausing
}

public sealed record InstallationProgress(
    InstallationProgressStage Stage,
    long CompletedBytes = 0,
    long TotalBytes = 0,
    int CompletedFiles = 0,
    int TotalFiles = 0);

public sealed record InstallationResult(
    GameArchitecture Architecture,
    string JavaExecutable,
    int JavaMajorVersion,
    bool LegacyTexturepacks,
    string? FabricProfileSHA1 = null);

public sealed class MinecraftInstaller(MojangClient client, FabricClient? fabricClient = null)
{
    private const int MaximumConcurrentDownloads = 4;
    private const int MaximumNativeArchiveEntries = 100_000;
    private const long MaximumNativeArchiveSize = 1024L * 1024 * 1024;
    private const int BufferSize = 81920;
    private static readonly HashSet<string> ReservedDeviceNames = BuildReservedDeviceNames();
    private readonly FabricClient _fabricClient = fabricClient ?? new FabricClient();

    private sealed record DownloadJob(MojangDownload Download, string RelativePath);
    private sealed record AssetCopy(string Name, MojangDownload Download);
    private sealed record NativeArchive(string RelativePath, IReadOnlyList<string> Excludes);

    public Task<InstallationResult> InstallAsync(
        PreparedInstallation prepared,
        string instanceRoot,
        IProgress<InstallationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => InstallCoreAsync(prepared, instanceRoot, [], null, progress, cancellationToken), cancellationToken);

    public Task<InstallationResult> InstallAsync(
        PreparedInstallation prepared,
        string instanceRoot,
        IReadOnlyCollection<LibraryInstallation> additionalLibraries,
        IProgress<InstallationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => InstallCoreAsync(prepared, instanceRoot, additionalLibraries, null, progress, cancellationToken), cancellationToken);

    public Task<InstallationResult> InstallFabricAsync(
        PreparedInstallation prepared,
        string instanceRoot,
        FabricConfiguration? fabric,
        IProgress<InstallationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => InstallCoreAsync(prepared, instanceRoot, [], fabric, progress, cancellationToken), cancellationToken);

    private async Task<InstallationResult> InstallCoreAsync(
        PreparedInstallation prepared,
        string instanceRoot,
        IReadOnlyCollection<LibraryInstallation> additionalLibraries,
        FabricConfiguration? fabric,
        IProgress<InstallationProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(prepared.Version.Id, prepared.Manifest.Id, StringComparison.Ordinal))
        {
            throw new MojangException(MojangError.Invalid, "The prepared version ID does not match its version description.");
        }

        MojangClient.Verify(prepared.ManifestData, new MojangDownload
        {
            Url = prepared.Version.Url,
            Sha1 = prepared.Version.Sha1
        });
        MojangClient.Verify(prepared.RuntimeManifestData, prepared.Runtime.Manifest);
        progress?.Report(new InstallationProgress(InstallationProgressStage.PreparingFiles));
        var root = Path.GetFullPath(instanceRoot);
        Directory.CreateDirectory(root);
        _ = InstanceStorage.ContainedPath("installation-check", root);
        CleanStaleTemporaryFiles(root, cancellationToken);

        var manifest = prepared.Manifest;
        FabricProfile? fabricProfile = null;
        byte[]? fabricProfileData = null;
        string? fabricProfileSha1 = null;
        IReadOnlyList<LibraryInstallation> fabricLibraries = [];
        if (fabric is not null)
        {
            if (string.IsNullOrWhiteSpace(fabric.LoaderVersion))
            {
                throw new MojangException(MojangError.Unsupported, "A Fabric Loader version is required.");
            }

            (fabricProfile, fabricProfileData) = await _fabricClient.GetProfileAsync(manifest.Id, fabric.LoaderVersion, cancellationToken)
                .ConfigureAwait(false);
            fabricLibraries = await _fabricClient.ResolveLibrariesAsync(fabricProfile, cancellationToken).ConfigureAwait(false);
            fabricProfileSha1 = Convert.ToHexString(SHA1.HashData(fabricProfileData)).ToLowerInvariant();
        }

        var versionDirectory = $"minecraft/versions/{manifest.Id}";
        var jobsByPath = new Dictionary<string, DownloadJob>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nativeArchives = new List<NativeArchive>();
        var assetCopies = new List<AssetCopy>();
        var assetCopyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalBytes = 0L;

        void AddJob(MojangDownload download, string relativePath)
        {
            relativePath = NormalizeRelativePath(relativePath);
            MojangClient.ValidateDownload(download);
            ValidateRelativeWindowsPath(relativePath);
            _ = InstanceStorage.ContainedPath(relativePath, root);
            if (jobsByPath.TryGetValue(relativePath, out var existing))
            {
                if (!string.Equals(existing.Download.Sha1, download.Sha1, StringComparison.OrdinalIgnoreCase)
                    || existing.Download.Size is { } oldSize && download.Size is { } newSize && oldSize != newSize)
                {
                    throw new MojangException(MojangError.Invalid, "The version description maps different files to the same path.");
                }

                if (existing.Download.Size is null && download.Size is { } knownSize)
                {
                    jobsByPath[relativePath] = existing with { Download = existing.Download with { Size = knownSize } };
                    totalBytes += knownSize;
                }

                return;
            }

            jobsByPath.Add(relativePath, new DownloadJob(download, relativePath));
            totalBytes += download.Size ?? 0;
        }

        void AddAssetCopy(string relativePath, string hash, long size)
        {
            relativePath = NormalizeRelativePath(relativePath);
            ValidateRelativeWindowsPath(relativePath);
            _ = InstanceStorage.ContainedPath(relativePath, root);
            if (!assetCopyPaths.Add(relativePath))
            {
                throw new MojangException(MojangError.Invalid, "The asset index maps multiple assets to the same Windows path.");
            }

            assetCopies.Add(new AssetCopy(relativePath, new MojangDownload
            {
                Url = MojangClient.AssetBaseUrl,
                Sha1 = hash,
                Size = size
            }));
        }

        foreach (var (relativePath, entry) in prepared.RuntimeManifest.Files)
        {
            ValidateRelativeWindowsPath(relativePath);
            _ = InstanceStorage.ContainedPath(Path.Combine("java", relativePath), root);
            switch (entry.Type)
            {
                case "directory":
                    directories.Add(Path.Combine("java", relativePath));
                    break;
                case "file":
                    if (entry.Downloads?.GetValueOrDefault("raw") is not { } raw)
                    {
                        throw new MojangException(MojangError.Invalid, $"Mojang omitted the raw Java file for {relativePath}.");
                    }

                    AddJob(raw, Path.Combine("java", relativePath));
                    break;
                case "link":
                    ValidateRuntimeLinkTarget(relativePath, entry.Target, root);
                    throw new MojangException(MojangError.Invalid,
                        $"The Java runtime contains a link at '{relativePath}'. Hako does not create Windows reparse points.");
                default:
                    throw new MojangException(MojangError.Invalid, $"Mojang returned an unknown Java runtime entry type for {relativePath}.");
            }
        }

        if (!prepared.RuntimeManifest.Files.ContainsKey("bin/javaw.exe"))
        {
            throw new MojangException(MojangError.Invalid, "The selected Java runtime does not contain bin/javaw.exe.");
        }

        if (!manifest.Downloads.TryGetValue("client", out var clientDownload))
        {
            throw new MojangException(MojangError.Unsupported, "Mojang does not provide a client for this version.");
        }

        AddJob(clientDownload, $"{versionDirectory}/{manifest.Id}.jar");
        var allLibraries = prepared.Libraries.Concat(additionalLibraries).ToArray();
        if (fabricProfile is not null)
        {
            var fabricKeys = fabricProfile.Libraries.Select(library => FabricMavenLibrary.Parse(library.Name).Key)
                .ToHashSet(StringComparer.Ordinal);
            allLibraries = fabricLibraries.Concat(allLibraries.Where(library => FabricMavenLibrary.KeyFromPath(library.Path) is not { } key
                || !fabricKeys.Contains(key))).ToArray();
        }

        foreach (var library in allLibraries)
        {
            var libraryPath = NormalizeRelativePath(Path.Combine("minecraft/libraries", library.Path));
            AddJob(library.Download, libraryPath);
            if (library.ExtractNatives)
            {
                nativeArchives.Add(new NativeArchive(
                    libraryPath,
                    library.ExtractionExcludes ?? []));
            }
        }

        foreach (var logging in manifest.Logging?.Values ?? Enumerable.Empty<MojangLoggingConfiguration>())
        {
            var fileName = logging.File.Id ?? Path.GetFileName(new Uri(logging.File.Url).AbsolutePath);
            ValidateRelativeWindowsPath(fileName);
            AddJob(logging.File, Path.Combine("minecraft/assets/log_configs", fileName));
        }

        byte[]? assetIndexData = null;
        MojangAssetIndex? assetIndex = null;
        if (manifest.AssetIndex is { } assetReference)
        {
            ValidateRelativeWindowsPath(assetReference.Id);
            assetIndexData = await client.GetVerifiedBytesAsync(assetReference.Download, cancellationToken).ConfigureAwait(false);
            assetIndex = System.Text.Json.JsonSerializer.Deserialize(assetIndexData, MojangJsonContext.Default.MojangAssetIndex)
                ?? throw new MojangException(MojangError.Invalid, "Mojang returned an empty asset index.");

            foreach (var (name, asset) in assetIndex.Objects)
            {
                if (asset.Hash.Length != 40 || !asset.Hash.All(Uri.IsHexDigit) || asset.Size < 0)
                {
                    throw new MojangException(MojangError.Invalid, "Mojang returned an invalid asset entry.");
                }

                var assetHash = asset.Hash.ToLowerInvariant();
                var hashPath = $"{assetHash[..2]}/{assetHash}";
                AddJob(new MojangDownload
                {
                    Url = MojangClient.AssetBaseUrl + hashPath,
                    Sha1 = assetHash,
                    Size = asset.Size
                }, $"minecraft/assets/objects/{hashPath}");
                if (assetIndex.Virtual)
                {
                    AddAssetCopy($"minecraft/assets/virtual/{assetReference.Id}/{name}", assetHash, asset.Size);
                }

                if (assetIndex.MapToResources)
                {
                    AddAssetCopy($"minecraft/resources/{name}", assetHash, asset.Size);
                }
            }

            await WriteAtomicallyAsync($"minecraft/assets/indexes/{assetReference.Id}.json", assetIndexData, root, cancellationToken)
                .ConfigureAwait(false);
        }

        await WriteAtomicallyAsync($"{versionDirectory}/{manifest.Id}.json", prepared.ManifestData, root, cancellationToken)
            .ConfigureAwait(false);
        if (fabricProfileData is not null)
        {
            await WriteAtomicallyAsync("minecraft/.hako-fabric.json", fabricProfileData, root, cancellationToken).ConfigureAwait(false);
        }
        await WriteAtomicallyAsync("java/runtime-manifest.json", prepared.RuntimeManifestData, root, cancellationToken)
            .ConfigureAwait(false);

        foreach (var directory in directories)
        {
            CreateDirectory(directory, root);
        }

        var jobs = jobsByPath.Values.OrderBy(job => job.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        progress?.Report(new InstallationProgress(InstallationProgressStage.Downloading, 0, totalBytes, 0, jobs.Length));
        var progressState = new ProgressState(progress, totalBytes, jobs.Length);
        using (var semaphore = new SemaphoreSlim(MaximumConcurrentDownloads))
        using (var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            Exception? firstNonCancellationFailure = null;
            async Task DownloadAndCancelOnErrorAsync(DownloadJob job)
            {
                try
                {
                    await DownloadAsync(job, root, semaphore, progressState, linkedCancellation.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Interlocked.CompareExchange(ref firstNonCancellationFailure, exception, null);
                    linkedCancellation.Cancel();
                    throw;
                }
            }

            var downloads = jobs.Select(DownloadAndCancelOnErrorAsync).ToArray();
            try
            {
                await Task.WhenAll(downloads).ConfigureAwait(false);
            }
            catch
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (firstNonCancellationFailure is not null)
                {
                    ExceptionDispatchInfo.Capture(firstNonCancellationFailure).Throw();
                }

                throw;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new InstallationProgress(InstallationProgressStage.PreparingLibraries, totalBytes, totalBytes, jobs.Length, jobs.Length));
        if (nativeArchives.Count > 0)
        {
            await ExtractNativesAsync(nativeArchives.DistinctBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(), root, cancellationToken)
                .ConfigureAwait(false);
        }

        if (assetIndex is not null)
        {
            progress?.Report(new InstallationProgress(InstallationProgressStage.PreparingAssets, totalBytes, totalBytes, jobs.Length, jobs.Length));
            foreach (var assetCopy in assetCopies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var assetHash = assetCopy.Download.Sha1.ToLowerInvariant();
                var source = $"minecraft/assets/objects/{assetHash[..2]}/{assetHash}";
                await CopyAtomicallyAsync(source, assetCopy.Name, assetCopy.Download, root, cancellationToken).ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        CreateDirectory("minecraft/mods", root);
        CreateDirectory(manifest.LegacyTexturepacks ? "minecraft/texturepacks" : "minecraft/resourcepacks", root);
        if (fabricProfile is not null && fabric is not null && fabric.Api is { } api)
        {
            if (!InstanceContent.ApiWasProvisioned(root))
            {
                var cachedApi = await _fabricClient.DownloadApiAsync(api, cancellationToken).ConfigureAwait(false);
                var metadata = await FabricClient.ReadModMetadataAsync(cachedApi, cancellationToken).ConfigureAwait(false);
                if (!metadata.Supports(fabric.LoaderVersion, manifest.Java.MajorVersion))
                {
                    throw new MojangException(MojangError.Unsupported, "Fabric API is incompatible with the selected Loader or Java version.");
                }

                await InstanceContent.ProvisionAPIAsync(api, cachedApi, root, cancellationToken).ConfigureAwait(false);
            }
        }

        progress?.Report(new InstallationProgress(InstallationProgressStage.Complete, totalBytes, totalBytes, jobs.Length, jobs.Length));
        return new InstallationResult(
            prepared.Architecture,
            "bin/javaw.exe",
            manifest.Java.MajorVersion,
            manifest.LegacyTexturepacks,
            fabricProfileSha1);
    }

    private async Task DownloadAsync(
        DownloadJob job,
        string root,
        SemaphoreSlim semaphore,
        ProgressState progress,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var destination = InstanceStorage.ContainedPath(job.RelativePath, root);
            if (await IsValidFileAsync(destination, job.Download, cancellationToken).ConfigureAwait(false))
            {
                progress.FileCompleted(job.Download.Size ?? 0);
                return;
            }

            CreateDirectory(Path.GetDirectoryName(job.RelativePath)!, root);
            var temporary = TemporaryPathFor(destination);
            try
            {
                using var response = await client.SendAsync(job.Download.Url, cancellationToken).ConfigureAwait(false);
                if (job.Download.Size is { } expectedContentLength && response.Content.Headers.ContentLength is { } contentLength
                    && contentLength > expectedContentLength)
                {
                    throw new MojangException(MojangError.Integrity, $"The downloaded file is larger than declared: {job.RelativePath}.");
                }

                Stream source;
                try
                {
                    source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new MojangException(MojangError.Http, "The Mojang response stream timed out.", innerException: exception);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new MojangException(MojangError.Http, "The Mojang response stream could not be opened.", innerException: exception);
                }

                await using var responseStream = source;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                var buffer = new byte[BufferSize];
                long size = 0;
                await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    while (true)
                    {
                        var readLength = BufferSize;
                        if (job.Download.Size is { } declaredSizeLimit)
                        {
                            var remaining = declaredSizeLimit - size;
                            if (remaining < BufferSize)
                            {
                                readLength = (int)remaining + 1;
                            }
                        }

                        var read = await client.ReadWithIdleTimeoutAsync(source, buffer.AsMemory(0, readLength), cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        if (job.Download.Size is { } maximumSize && read > maximumSize - size)
                        {
                            throw new MojangException(MojangError.Integrity, $"The downloaded file is larger than declared: {job.RelativePath}.");
                        }

                        size += read;
                        hash.AppendData(buffer, 0, read);
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        progress.BytesDownloaded(read);
                    }

                    await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(actualHash, job.Download.Sha1, StringComparison.OrdinalIgnoreCase)
                    || job.Download.Size is { } expectedSize && size != expectedSize)
                {
                    throw new MojangException(MojangError.Integrity, $"The downloaded file failed its SHA-1 or size check: {job.RelativePath}.");
                }

                File.Move(temporary, destination, overwrite: true);
                progress.FileCompleted(0);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    internal static async Task<bool> IsValidFileAsync(string path, MojangDownload download, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var info = new FileInfo(path);
        if (download.Size is { } expectedSize && info.Length != expectedSize)
        {
            return false;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[BufferSize];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        return string.Equals(Convert.ToHexString(hash.GetHashAndReset()), download.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteAtomicallyAsync(string relativePath, byte[] data, string root, CancellationToken cancellationToken)
    {
        ValidateRelativeWindowsPath(relativePath);
        var destination = InstanceStorage.ContainedPath(relativePath, root);
        CreateDirectory(Path.GetDirectoryName(relativePath)!, root);
        var temporary = TemporaryPathFor(destination);
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task CopyAtomicallyAsync(
        string sourceRelativePath,
        string destinationRelativePath,
        MojangDownload download,
        string root,
        CancellationToken cancellationToken)
    {
        ValidateRelativeWindowsPath(destinationRelativePath);
        var source = InstanceStorage.ContainedPath(sourceRelativePath, root);
        var destination = InstanceStorage.ContainedPath(destinationRelativePath, root);
        CreateDirectory(Path.GetDirectoryName(destinationRelativePath)!, root);
        if (!await IsValidFileAsync(source, download, cancellationToken).ConfigureAwait(false))
        {
            throw new MojangException(MojangError.Integrity, "An asset object changed before it could be copied.");
        }

        if (await IsValidFileAsync(destination, download, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var temporary = TemporaryPathFor(destination);
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsValidFileAsync(temporary, download, cancellationToken).ConfigureAwait(false))
            {
                throw new MojangException(MojangError.Integrity, "An asset copy failed its SHA-1 or size check.");
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task ExtractNativesAsync(
        IReadOnlyList<NativeArchive> archives,
        string root,
        CancellationToken cancellationToken)
    {
        var stagingRelative = $"minecraft/.hako-natives-{Guid.NewGuid():N}.tmp";
        var stagingPath = InstanceStorage.ContainedPath(stagingRelative, root);
        CreateDirectory(stagingRelative, root);
        try
        {
            foreach (var archive in archives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExtractNativeArchiveAsync(archive, stagingRelative, root, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            PublishNativeStaging(stagingPath, root, Directory.Move);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingPath))
                {
                    InstanceStorage.DeleteEntryWithoutFollowingReparsePoints(stagingPath);
                }
            }
            catch (Exception exception) when (IsBestEffortFileSystemFailure(exception))
            {
            }
        }
    }

    private static async Task ExtractNativeArchiveAsync(
        NativeArchive nativeArchive,
        string stagingRelative,
        string root,
        CancellationToken cancellationToken)
    {
        var archivePath = InstanceStorage.ContainedPath(nativeArchive.RelativePath, root);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumNativeArchiveEntries)
        {
            throw new MojangException(MojangError.Invalid, "A native library archive contains too many entries.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(ZipArchiveEntry Entry, string RelativePath, bool IsDirectory, bool Excluded)>();
        long expandedSize = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var path = entry.FullName.Replace('\\', '/').TrimEnd('/');
            if (path.Length == 0)
            {
                continue;
            }

            ValidateRelativeWindowsPath(path);
            if (!paths.Add(path))
            {
                throw new MojangException(MojangError.Invalid, "A native library archive contains duplicate paths.");
            }

            var destinationRelative = $"{stagingRelative}/{path}";
            _ = InstanceStorage.ContainedPath(destinationRelative, root);
            expandedSize = checked(expandedSize + (isDirectory ? 0 : entry.Length));
            if (expandedSize > MaximumNativeArchiveSize)
            {
                throw new MojangException(MojangError.Invalid, "A native library archive expands beyond the supported size limit.");
            }

            var normalized = path + (isDirectory ? "/" : "");
            var excluded = nativeArchive.Excludes.Any(exclude => IsExcluded(normalized, exclude));
            entries.Add((entry, destinationRelative, isDirectory, excluded));
        }

        foreach (var (entry, destinationRelative, isDirectory, excluded) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (excluded)
            {
                continue;
            }

            if (isDirectory)
            {
                CreateDirectory(destinationRelative, root);
                continue;
            }

            var destination = InstanceStorage.ContainedPath(destinationRelative, root);
            CreateDirectory(Path.GetDirectoryName(destinationRelative)!, root);
            await using var input = entry.Open();
            await ExtractNativeEntryAsync(input, destination, entry.Length, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task ExtractNativeEntryAsync(
        Stream input,
        string destination,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        if (expectedLength < 0)
        {
            throw new MojangException(MojangError.Invalid, "A native library entry has an invalid declared size.");
        }

        var temporary = TemporaryPathFor(destination);
        try
        {
            long size = 0;
            var buffer = new byte[BufferSize];
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var remaining = expectedLength - size;
                    var readLength = remaining < BufferSize ? (int)remaining + 1 : BufferSize;
                    var read = await input.ReadAsync(buffer.AsMemory(0, readLength), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (read > remaining)
                    {
                        throw new MojangException(MojangError.Invalid, "A native library entry exceeds its declared size.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    size += read;
                }

                if (size != expectedLength)
                {
                    throw new MojangException(MojangError.Invalid, "A native library entry was truncated while extracting.");
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal static void PublishNativeStaging(string stagingPath, string root, Action<string, string> moveDirectory)
    {
        var pending = new Stack<string>();
        pending.Push(stagingPath);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new MojangException(MojangError.Invalid, "A reparse point appeared in the native library staging folder.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }

        var destination = InstanceStorage.ContainedPath("minecraft/natives", root);
        var backup = InstanceStorage.ContainedPath($"minecraft/.hako-natives-backup-{Guid.NewGuid():N}.tmp", root);
        var destinationMoved = false;
        try
        {
            if (Directory.Exists(destination))
            {
                moveDirectory(destination, backup);
                destinationMoved = true;
            }

            moveDirectory(stagingPath, destination);
        }
        catch (Exception publicationFailure)
        {
            try
            {
                if (destinationMoved)
                {
                    if (Directory.Exists(destination) && !Directory.Exists(stagingPath))
                    {
                        moveDirectory(destination, stagingPath);
                    }

                    moveDirectory(backup, destination);
                }
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException("Native library publication failed and the previous natives could not be restored.",
                    publicationFailure, rollbackFailure);
            }

            throw;
        }

        if (destinationMoved)
        {
            try
            {
                InstanceStorage.DeleteEntryWithoutFollowingReparsePoints(backup);
            }
            catch (Exception exception) when (IsBestEffortFileSystemFailure(exception))
            {
            }
        }
    }

    private static bool IsExcluded(string path, string exclude)
    {
        var normalized = exclude.Replace('\\', '/');
        if (!normalized.EndsWith('/'))
        {
            return string.Equals(path.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase);
        }

        return path.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static string TemporaryPathFor(string destination) =>
        Path.Combine(Path.GetDirectoryName(destination)!, $".hako-{Guid.NewGuid():N}.tmp");

    internal static void CleanStaleTemporaryFiles(string root, CancellationToken cancellationToken)
    {
        // InstanceStore prevents concurrent installs for one root; only installer-owned folders are cleaned here.
        foreach (var relativeDirectory in new[]
        {
            "java",
            "minecraft/versions",
            "minecraft/libraries",
            "minecraft/assets",
            "minecraft/natives",
            "minecraft/resources"
        })
        {
            cancellationToken.ThrowIfCancellationRequested();
            CleanStaleTemporaryEntries(root, Path.Combine(root, relativeDirectory), recurse: true, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        CleanStaleTemporaryEntries(root, Path.Combine(root, "minecraft"), recurse: false, cancellationToken);
    }

    private static void CleanStaleTemporaryEntries(
        string root,
        string directory,
        bool recurse,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var currentDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var safeDirectory = InstanceStorage.ContainedPath(Path.GetRelativePath(root, currentDirectory), root);
                if (!Directory.Exists(safeDirectory))
                {
                    continue;
                }

                foreach (var entry in Directory.EnumerateFileSystemEntries(safeDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var safeEntry = InstanceStorage.ContainedPath(Path.GetRelativePath(root, entry), root);
                        var attributes = File.GetAttributes(safeEntry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        var name = Path.GetFileName(safeEntry);
                        if (name.StartsWith(".hako-", StringComparison.OrdinalIgnoreCase)
                            && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                InstanceStorage.DeleteEntryWithoutFollowingReparsePoints(safeEntry);
                            }
                            catch (Exception exception) when (IsBestEffortFileSystemFailure(exception))
                            {
                            }
                        }
                        else if (recurse && (attributes & FileAttributes.Directory) != 0)
                        {
                            pending.Push(safeEntry);
                        }
                    }
                    catch (Exception exception) when (IsBestEffortFileSystemFailure(exception))
                    {
                    }
                }
            }
            catch (Exception exception) when (IsBestEffortFileSystemFailure(exception))
            {
            }
        }
    }

    private static bool IsBestEffortFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or InstanceStorageException;

    private static void ValidateRuntimeLinkTarget(string relativePath, string? target, string root)
    {
        if (string.IsNullOrWhiteSpace(target) || Path.IsPathRooted(target) || target.Contains(':') || target.Contains('\0'))
        {
            throw new MojangException(MojangError.Invalid, $"The Java runtime link at '{relativePath}' has an invalid target.");
        }

        var javaRoot = Path.GetFullPath(Path.Combine(root, "java"));
        var linkPath = Path.GetFullPath(Path.Combine(javaRoot, relativePath));
        var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(linkPath)!, target));
        var prefix = Path.EndsInDirectorySeparator(javaRoot) ? javaRoot : javaRoot + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new MojangException(MojangError.Invalid, $"The Java runtime link at '{relativePath}' escapes the java directory.");
        }
    }

    private static void CreateDirectory(string relativePath, string root)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        ValidateRelativeWindowsPath(relativePath);
        var path = InstanceStorage.ContainedPath(relativePath, root);
        Directory.CreateDirectory(path);
        _ = InstanceStorage.ContainedPath(relativePath, root);
    }

    private static void ValidateRelativeWindowsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.StartsWith('/') || path.StartsWith('\\')
            || path.Contains(':') || path.Contains('\0'))
        {
            throw new MojangException(MojangError.Invalid, "An installation path is absolute or invalid.");
        }

        var parts = path.Split(['/', '\\'], StringSplitOptions.None);
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new MojangException(MojangError.Invalid, "An installation path contains an invalid Windows name.");
            }

            var baseName = part.Split('.')[0].TrimEnd(' ', '.');
            if (ReservedDeviceNames.Contains(baseName))
            {
                throw new MojangException(MojangError.Invalid, "An installation path contains a reserved Windows device name.");
            }
        }
    }

    private static HashSet<string> BuildReservedDeviceNames()
    {
        var names = new HashSet<string>(["CON", "PRN", "AUX", "NUL"], StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index <= 9; index++)
        {
            names.Add($"COM{index}");
            names.Add($"LPT{index}");
        }

        return names;
    }

    private sealed class ProgressState(IProgress<InstallationProgress>? progress, long totalBytes, int totalFiles)
    {
        private readonly object _gate = new();
        private long _completedBytes;
        private int _completedFiles;

        public void BytesDownloaded(int count)
        {
            lock (_gate)
            {
                _completedBytes += count;
                progress?.Report(new InstallationProgress(InstallationProgressStage.Downloading, _completedBytes, totalBytes,
                    _completedFiles, totalFiles));
            }
        }

        public void FileCompleted(long reusedBytes)
        {
            lock (_gate)
            {
                _completedBytes += reusedBytes;
                _completedFiles++;
                progress?.Report(new InstallationProgress(InstallationProgressStage.Downloading, _completedBytes, totalBytes,
                    _completedFiles, totalFiles));
            }
        }
    }
}

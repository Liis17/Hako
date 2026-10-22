using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hako.Core.Minecraft;

public sealed class MojangClient : IDisposable
{
    public const string CatalogUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    public const string RuntimeIndexUrl = "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";
    public const string AssetBaseUrl = "https://resources.download.minecraft.net/";
    private const int MaximumMetadataBytes = 128 * 1024 * 1024;
    private static readonly TimeSpan DefaultReadIdleTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IHostArchitectureProvider _hostArchitectureProvider;
    private readonly TimeSpan _readIdleTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _runtimeIndexGate = new(1, 1);
    private Dictionary<string, Dictionary<string, List<MojangJavaRuntimePackage>>>? _runtimeIndex;

    public MojangClient(
        HttpClient? httpClient = null,
        IHostArchitectureProvider? hostArchitectureProvider = null,
        TimeSpan? readIdleTimeout = null,
        TimeProvider? timeProvider = null)
    {
        _readIdleTimeout = readIdleTimeout ?? DefaultReadIdleTimeout;
        if (_readIdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(readIdleTimeout));
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _hostArchitectureProvider = hostArchitectureProvider ?? new WindowsHostArchitectureProvider();
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            var version = typeof(MojangClient).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Hako", version));
        }
    }

    public async Task<MojangCatalog> CatalogAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await GetBytesAsync(CatalogUrl, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, MojangJsonContext.Default.MojangCatalog)
            ?? throw Invalid("Mojang returned an empty version catalog.");
    }

    public Task<PreparedInstallation> PrepareAsync(MojangVersionEntry version, CancellationToken cancellationToken = default) =>
        PrepareCoreAsync(version, _hostArchitectureProvider.GetHostArchitecture(), null, cancellationToken);

    public Task<PreparedInstallation> PrepareAsync(
        MojangVersionEntry version,
        HostArchitecture host,
        CancellationToken cancellationToken = default) =>
        PrepareCoreAsync(version, host, null, cancellationToken);

    public Task<PreparedInstallation> PrepareAsync(
        MojangVersionEntry version,
        HostArchitecture host,
        GameArchitecture pinned,
        CancellationToken cancellationToken = default) =>
        PrepareCoreAsync(version, host, pinned, cancellationToken);

    public Task<PreparedInstallation> PrepareAsync(
        MojangVersionEntry version,
        GameArchitecture pinned,
        CancellationToken cancellationToken = default) =>
        PrepareCoreAsync(version, _hostArchitectureProvider.GetHostArchitecture(), pinned, cancellationToken);

    internal async Task<HttpResponseMessage> SendAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw Invalid("Mojang download URLs must use HTTPS.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MojangException(MojangError.Http, "The Mojang request timed out.", innerException: exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new MojangException(MojangError.Http, "The Mojang request failed.", innerException: exception);
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            response.Dispose();
            throw new MojangException(MojangError.Http, $"The Mojang server returned HTTP {statusCode}.", statusCode);
        }

        return response;
    }

    internal async Task<byte[]> GetBytesAsync(string url, CancellationToken cancellationToken, long? expectedSize = null)
    {
        if (expectedSize is < 0)
        {
            throw Invalid("Mojang returned an invalid file size.");
        }

        using var response = await SendAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is > MaximumMetadataBytes)
        {
            throw Invalid("Mojang metadata exceeds the supported size limit.");
        }

        if (expectedSize is { } expectedContentLength && response.Content.Headers.ContentLength is { } contentLength
            && contentLength > expectedContentLength)
        {
            throw new MojangException(MojangError.Integrity, "A Mojang response is larger than its declared size.");
        }

        Stream stream;
        try
        {
            stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
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

        await using var responseStream = stream;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var sizeLimit = expectedSize is { } size ? Math.Min(size, MaximumMetadataBytes) : MaximumMetadataBytes;
            var remaining = sizeLimit - buffer.Length;
            var readLength = remaining < chunk.Length ? (int)remaining + 1 : chunk.Length;
            var read = await ReadWithIdleTimeoutAsync(responseStream, chunk.AsMemory(0, readLength), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumMetadataBytes
                || expectedSize is { } expectedSizeLimit && buffer.Length + read > expectedSizeLimit)
            {
                if (expectedSize is { } declaredSize && buffer.Length + read > declaredSize)
                {
                    throw new MojangException(MojangError.Integrity, "A Mojang response is larger than its declared size.");
                }

                throw Invalid("Mojang metadata exceeds the supported size limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    internal async Task<byte[]> GetVerifiedBytesAsync(MojangDownload download, CancellationToken cancellationToken)
    {
        var bytes = await GetBytesAsync(download.Url, cancellationToken, download.Size).ConfigureAwait(false);
        Verify(bytes, download);
        return bytes;
    }

    internal async ValueTask<int> ReadWithIdleTimeoutAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var idleTimeout = new CancellationTokenSource(_readIdleTimeout, _timeProvider);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idleTimeout.Token);
        try
        {
            return await stream.ReadAsync(buffer, linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && idleTimeout.IsCancellationRequested)
        {
            throw new MojangException(MojangError.Http, "The Mojang response stopped sending data.", innerException: exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MojangException(MojangError.Http, "The Mojang response timed out while being read.", innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new MojangException(MojangError.Http, "The Mojang response could not be read.", innerException: exception);
        }
        catch (IOException exception)
        {
            throw new MojangException(MojangError.Http, "The Mojang response could not be read.", innerException: exception);
        }
    }

    public void Dispose()
    {
        _runtimeIndexGate.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>
    /// Reads the version description installed in a build, verified against the SHA-1 pinned in its profile.
    /// Returns null when the build has no matching installed description yet.
    /// </summary>
    public static MojangVersionMetadata? ReadInstalledManifest(string instanceRoot, Instances.GameInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var path = Instances.InstanceStorage.ContainedPath(
            $"minecraft/versions/{instance.VersionID}/{instance.VersionID}.json", instanceRoot, allowBaseReparsePoint: true);
        if (!File.Exists(path))
        {
            return null;
        }

        var data = File.ReadAllBytes(path);
        if (!string.Equals(Convert.ToHexString(SHA1.HashData(data)), instance.MetadataSHA1, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var manifest = JsonSerializer.Deserialize(data, MojangJsonContext.Default.MojangVersionMetadata);
        return manifest is not null && string.Equals(manifest.Id, instance.VersionID, StringComparison.Ordinal) ? manifest : null;
    }

    internal static void Verify(ReadOnlySpan<byte> bytes, MojangDownload download)
    {
        ValidateDownload(download);
        var hash = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(hash, download.Sha1, StringComparison.OrdinalIgnoreCase)
            || download.Size is { } expectedSize && bytes.Length != expectedSize)
        {
            throw new MojangException(MojangError.Integrity, "A downloaded Mojang file failed its SHA-1 or size check.");
        }
    }

    internal static void ValidateDownload(MojangDownload download)
    {
        if (!Uri.TryCreate(download.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw Invalid("Mojang download URLs must use HTTPS.");
        }

        if (download.Sha1.Length != 40 || !download.Sha1.All(Uri.IsHexDigit) || download.Size is < 0)
        {
            throw Invalid("Mojang returned invalid file integrity metadata.");
        }
    }

    private async Task<PreparedInstallation> PrepareCoreAsync(
        MojangVersionEntry version,
        HostArchitecture host,
        GameArchitecture? pinned,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(version.Id))
        {
            throw Invalid("The Mojang version catalog entry has no ID.");
        }

        var manifestData = await GetVerifiedBytesAsync(new MojangDownload
        {
            Url = version.Url,
            Sha1 = version.Sha1
        }, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize(manifestData, MojangJsonContext.Default.MojangVersionMetadata)
            ?? throw Invalid("Mojang returned an empty version description.");

        if (manifest.Downloads is null || manifest.Libraries is null
            || string.IsNullOrWhiteSpace(manifest.Java.Component) || manifest.Java.MajorVersion < 1)
        {
            throw Invalid("Mojang returned an incomplete version description.");
        }

        if (!string.Equals(version.Id, manifest.Id, StringComparison.Ordinal)
            || !manifest.Downloads.TryGetValue("client", out var clientDownload))
        {
            throw Unsupported("Mojang does not provide a client for this version.");
        }

        if (clientDownload is null)
        {
            throw Invalid("Mojang returned an empty client download entry.");
        }

        ValidateDownload(clientDownload);

        var runtimeIndex = await GetRuntimeIndexAsync(cancellationToken).ConfigureAwait(false);
        var candidates = CandidateArchitectures(host, pinned);
        MojangException? unsupported = null;
        foreach (var architecture in candidates)
        {
            try
            {
                var runtime = FindRuntime(runtimeIndex, manifest.Java, architecture);
                var libraries = MojangCompatibility.Libraries(manifest, architecture);
                foreach (var library in libraries)
                {
                    ValidateDownload(library.Download);
                }

                ValidateDownload(runtime.Manifest);
                var runtimeManifestData = await GetVerifiedBytesAsync(runtime.Manifest, cancellationToken).ConfigureAwait(false);
                var runtimeManifest = JsonSerializer.Deserialize(runtimeManifestData, MojangJsonContext.Default.MojangJavaRuntimeManifest)
                    ?? throw Invalid("Mojang returned an empty Java runtime manifest.");
                if (runtimeManifest.Files is null)
                {
                    throw Invalid("Mojang returned an incomplete Java runtime manifest.");
                }

                var linkEntry = runtimeManifest.Files.FirstOrDefault(file => file.Value.Type == "link");
                if (linkEntry.Value is not null)
                {
                    throw Invalid($"The Java runtime contains a link at '{linkEntry.Key}'. Hako does not create Windows reparse points.");
                }

                if (!runtimeManifest.Files.TryGetValue("bin/javaw.exe", out var javaw) || javaw.Type != "file")
                {
                    throw Unsupported($"Mojang's {architecture.MojangRuntimeKey()} Java runtime has no Windows javaw.exe.");
                }

                foreach (var file in runtimeManifest.Files.Values.Where(file => file.Type == "file"))
                {
                    if (file.Downloads?.GetValueOrDefault("raw") is not { } raw)
                    {
                        throw Unsupported($"Mojang's {architecture.MojangRuntimeKey()} Java runtime is incomplete.");
                    }

                    ValidateDownload(raw);
                }

                return new PreparedInstallation(
                    version,
                    architecture,
                    manifest,
                    manifestData,
                    runtime,
                    runtimeManifest,
                    runtimeManifestData,
                    libraries);
            }
            catch (MojangException exception) when (exception.Error == MojangError.Unsupported)
            {
                unsupported = exception;
            }
        }

        throw unsupported ?? Unsupported("This Windows version has no compatible Minecraft architecture.");
    }

    private async Task<Dictionary<string, Dictionary<string, List<MojangJavaRuntimePackage>>>> GetRuntimeIndexAsync(
        CancellationToken cancellationToken)
    {
        if (_runtimeIndex is not null)
        {
            return _runtimeIndex;
        }

        await _runtimeIndexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runtimeIndex is null)
            {
                var bytes = await GetBytesAsync(RuntimeIndexUrl, cancellationToken).ConfigureAwait(false);
                _runtimeIndex = JsonSerializer.Deserialize(bytes, MojangJsonContext.Default.DictionaryStringDictionaryStringListMojangJavaRuntimePackage)
                    ?? throw Invalid("Mojang returned an empty Java runtime catalog.");
            }

            return _runtimeIndex;
        }
        finally
        {
            _runtimeIndexGate.Release();
        }
    }

    private static IReadOnlyList<GameArchitecture> CandidateArchitectures(HostArchitecture host, GameArchitecture? pinned)
    {
        if (pinned is { } selected)
        {
            var supported = host.Machine switch
            {
                HostMachineArchitecture.X64 => selected == GameArchitecture.X64,
                HostMachineArchitecture.X86 => selected == GameArchitecture.X86,
                HostMachineArchitecture.Arm64 => selected == GameArchitecture.Arm64
                    || selected == GameArchitecture.X64 && host.Amd64UserEnabled,
                _ => false
            };
            return supported ? [selected] : throw Unsupported("The pinned game architecture cannot run on this Windows system.");
        }

        return host.Machine switch
        {
            HostMachineArchitecture.X64 => [GameArchitecture.X64],
            HostMachineArchitecture.X86 => [GameArchitecture.X86],
            HostMachineArchitecture.Arm64 when host.Amd64UserEnabled => [GameArchitecture.Arm64, GameArchitecture.X64],
            HostMachineArchitecture.Arm64 => [GameArchitecture.Arm64],
            _ => throw Unsupported("The Windows machine architecture could not be determined.")
        };
    }

    private static MojangJavaRuntimePackage FindRuntime(
        Dictionary<string, Dictionary<string, List<MojangJavaRuntimePackage>>> index,
        MojangJavaRequirement requirement,
        GameArchitecture architecture)
    {
        if (index.TryGetValue(architecture.MojangRuntimeKey(), out var components)
            && components.TryGetValue(requirement.Component, out var packages))
        {
            var matching = packages
                .Where(package => package is not null && package.MajorVersion == requirement.MajorVersion)
                .OrderByDescending(package => package.Version.Released, StringComparer.Ordinal)
                .FirstOrDefault();
            if (matching is not null)
            {
                return matching;
            }
        }

        throw Unsupported($"Mojang does not provide Java {requirement.MajorVersion} for {architecture.MojangRuntimeKey()}.");
    }

    private static MojangException Invalid(string message) => new(MojangError.Invalid, message);
    private static MojangException Unsupported(string message) => new(MojangError.Unsupported, message);
}

public static class MojangCompatibility
{
    public static IReadOnlyList<LibraryInstallation> Libraries(
        MojangVersionMetadata manifest,
        GameArchitecture architecture,
        IReadOnlyDictionary<string, bool>? features = null)
    {
        var libraries = manifest.Libraries;
        var selectedByIndex = Enumerable.Range(0, libraries.Count).Select(_ => new List<LibraryInstallation>()).ToArray();
        var mappedGroups = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < libraries.Count; index++)
        {
            var library = libraries[index];
            if (library.Natives?.ContainsKey("windows") != true)
            {
                continue;
            }

            if (!library.CouldApplyToWindows(features))
            {
                continue;
            }

            if (!library.IsAllowed(architecture, features))
            {
                continue;
            }

            var classifier = library.Natives["windows"]
                .Replace("${arch}", architecture.NativeArchSubstitution(), StringComparison.Ordinal);
            if (!NativeClassifierMatches(classifier, architecture))
            {
                throw new MojangException(MojangError.Unsupported, $"The {architecture} native library set is incomplete.");
            }

            var native = library.Downloads?.Classifiers?.GetValueOrDefault(classifier);
            if (native?.Path is null)
            {
                throw new MojangException(MojangError.Unsupported, $"Mojang does not provide {classifier} for {library.Name}.");
            }

            if (library.Downloads?.Artifact is { } artifact)
            {
                selectedByIndex[index].Add(ToInstallation(artifact));
            }

            selectedByIndex[index].Add(new LibraryInstallation(
                native,
                native.Path,
                IsNative: true,
                ExtractNatives: library.Extract is not null,
                ExtractionExcludes: library.Extract?.Exclude ?? []));
            mappedGroups.Add(library.NativeGroup);
        }

        var nativeGroups = libraries
            .Select((library, index) => (Library: library, Index: index))
            .Where(item => IsWindowsNative(item.Library) && item.Library.CouldApplyToWindows(features))
            .GroupBy(item => item.Library.NativeGroup, StringComparer.Ordinal);

        foreach (var group in nativeGroups)
        {
            if (mappedGroups.Contains(group.Key))
            {
                continue;
            }

            var candidate = group
                .Where(item => item.Library.IsAllowed(architecture, features) && NativeMatches(item.Library, architecture))
                .OrderByDescending(item => NativeSpecificity(item.Library.Classifier, architecture))
                .ThenBy(item => item.Index)
                .FirstOrDefault();
            var download = candidate.Library?.Downloads?.Artifact;
            if (candidate.Library is null || download?.Path is null)
            {
                throw new MojangException(MojangError.Unsupported, $"Mojang does not provide a complete {architecture} native library set.");
            }

            selectedByIndex[candidate.Index].Add(new LibraryInstallation(
                download,
                download.Path,
                IsNative: true,
                ExtractNatives: candidate.Library.Extract is not null,
                ExtractionExcludes: candidate.Library.Extract?.Exclude ?? []));
            mappedGroups.Add(group.Key);
        }

        var selected = new List<LibraryInstallation>();
        for (var index = 0; index < libraries.Count; index++)
        {
            selected.AddRange(selectedByIndex[index]);
            var library = libraries[index];
            if (!library.IsAllowed(architecture, features))
            {
                continue;
            }

            if (IsAnyNative(library) || library.Natives?.ContainsKey("windows") == true)
            {
                continue;
            }

            if (library.Downloads?.Artifact is null && library.Natives is { Count: > 0 })
            {
                continue;
            }

            if (library.Downloads?.Artifact is not { } artifact)
            {
                throw new MojangException(MojangError.Unsupported, $"Mojang does not provide an artifact for {library.Name}.");
            }

            selected.Add(ToInstallation(artifact));
        }

        return selected;
    }

    private static LibraryInstallation ToInstallation(MojangDownload download)
    {
        if (download.Path is null)
        {
            throw new MojangException(MojangError.Invalid, "A Mojang library is missing its relative path.");
        }

        return new LibraryInstallation(download, download.Path);
    }

    private static bool IsAnyNative(MojangLibrary library) => library.Classifier?.StartsWith("natives-", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsWindowsNative(MojangLibrary library) =>
        library.Classifier?.StartsWith("natives-windows", StringComparison.OrdinalIgnoreCase) == true;

    private static bool NativeMatches(MojangLibrary library, GameArchitecture architecture) =>
        library.Classifier is { } classifier && NativeClassifierMatches(classifier, architecture);

    private static bool NativeClassifierMatches(string classifier, GameArchitecture architecture)
    {
        if (!classifier.StartsWith("natives-windows", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var arm = classifier.Contains("arm64", StringComparison.OrdinalIgnoreCase)
            || classifier.Contains("aarch64", StringComparison.OrdinalIgnoreCase);
        var x86 = IsX86Only(classifier);
        var generic = string.Equals(classifier, "natives-windows", StringComparison.OrdinalIgnoreCase);
        var expected = architecture.MojangNativeClassifier();
        return architecture switch
        {
            GameArchitecture.Arm64 => arm && classifier.StartsWith("natives-windows", StringComparison.OrdinalIgnoreCase)
                && (classifier.Contains(expected, StringComparison.OrdinalIgnoreCase)
                    || classifier.Contains("natives-windows-aarch64", StringComparison.OrdinalIgnoreCase)),
            GameArchitecture.X64 => !arm && !x86 && classifier.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            GameArchitecture.X86 => !arm && (x86 || generic)
                && (classifier.StartsWith(expected, StringComparison.OrdinalIgnoreCase)
                    || classifier.EndsWith("-32", StringComparison.OrdinalIgnoreCase)
                    || generic),
            _ => false
        };
    }

    private static bool IsX86Only(string? classifier) => classifier is not null
        && (classifier.Contains("-x86", StringComparison.OrdinalIgnoreCase)
            && !classifier.Contains("-x86_64", StringComparison.OrdinalIgnoreCase)
            || classifier.EndsWith("-32", StringComparison.OrdinalIgnoreCase));

    private static int NativeSpecificity(string? classifier, GameArchitecture architecture) => architecture switch
    {
        GameArchitecture.Arm64 => classifier?.Contains("arm64", StringComparison.OrdinalIgnoreCase) == true
            || classifier?.Contains("aarch64", StringComparison.OrdinalIgnoreCase) == true ? 2 : 0,
        GameArchitecture.X64 => classifier?.EndsWith("-64", StringComparison.OrdinalIgnoreCase) == true ? 2 : 1,
        GameArchitecture.X86 => IsX86Only(classifier) ? 2 : 1,
        _ => 0
    };
}

using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace Hako.Core.Updates;

public sealed record AppPackageIdentity(string Name, string Publisher, Version Version, string Architecture);
public sealed record DownloadedAppUpdate(AppRelease Release, string PackagePath, string[] Dependencies);

public sealed class AppUpdateDownload(HttpClient http, string cacheRoot, AppPackageIdentity installed)
{
    public async Task<DownloadedAppUpdate> DownloadAsync(AppRelease release, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var package = release.GetPackage(installed.Architecture) ?? throw new AppUpdateException("AppUpdateInvalidRelease");
        var assets = new[] { package }.Concat(release.GetDependencies(installed.Architecture)).ToArray();
        if (assets.Select(asset => asset.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != assets.Length)
            throw new AppUpdateException("AppUpdateInvalidRelease");
        var root = Path.Combine(cacheRoot, "AppUpdates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new List<string>();
            var total = assets.Sum(asset => asset.Size);
            long completed = 0;
            foreach (var asset in assets)
            {
                var path = Path.Combine(root, Path.GetFileName(asset.Name));
                await DownloadAssetAsync(asset, path, bytes => progress?.Report(total > 0 ? (completed + bytes) * 100d / total : 0), cancellationToken).ConfigureAwait(false);
                paths.Add(path);
                completed += asset.Size;
            }
            var identity = ReadIdentity(paths[0]);
            if (identity.Name != installed.Name || identity.Publisher != installed.Publisher
                || identity.Architecture != installed.Architecture || identity.Version != release.GetVersion()
                || identity.Version <= installed.Version)
                throw new AppUpdateException("AppUpdateInvalidPackage");
            // Framework identities and trust are ultimately checked by Windows deployment.
            foreach (var dependency in paths.Skip(1))
            {
                var dependencyIdentity = ReadIdentity(dependency);
                if (dependencyIdentity.Architecture != installed.Architecture && dependencyIdentity.Architecture != "neutral")
                    throw new AppUpdateException("AppUpdateInvalidPackage");
            }
            return new DownloadedAppUpdate(release, paths[0], paths.Skip(1).ToArray());
        }
        catch
        {
            DeleteDirectory(root);
            throw;
        }
    }

    public static void Delete(DownloadedAppUpdate update) => DeleteDirectory(Path.GetDirectoryName(update.PackagePath)!);

    private static void DeleteDirectory(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async Task VerifyAsync(DownloadedAppUpdate update, CancellationToken cancellationToken)
    {
        var assets = new[] { update.Release.GetPackage(installed.Architecture)! }
            .Concat(update.Release.GetDependencies(installed.Architecture)).ToArray();
        var paths = new[] { update.PackagePath }.Concat(update.Dependencies).ToArray();
        if (assets.Length != paths.Length) throw new AppUpdateException("AppUpdateInvalidPackage");
        for (var index = 0; index < paths.Length; index++)
        {
            await using var stream = File.OpenRead(paths[index]);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            if (stream.Length != assets[index].Size || !DigestMatches(assets[index].Digest, hash))
                throw new AppUpdateException("AppUpdateInvalidPackage");
        }
    }

    private async Task DownloadAssetAsync(AppReleaseAsset asset, string path, Action<long> progress, CancellationToken cancellationToken)
    {
        if (asset.Size <= 0 || !IsDigest(asset.Digest)
            || asset.Name != Path.GetFileName(asset.Name) || asset.Name.Contains('\\')
            || !Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var url) || url.Scheme != "https"
            || url.Host != "github.com" || !url.IsDefaultPort || url.UserInfo.Length != 0
            || !url.AbsolutePath.StartsWith("/Liis17/Hako/releases/download/", StringComparison.Ordinal))
            throw new AppUpdateException("AppUpdateInvalidRelease");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Hako/1.0 (+https://github.com/Liis17/Hako)");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
            throw new AppUpdateException("AppUpdateInvalidPackage");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long received = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            received += count;
            if (received > asset.Size) throw new AppUpdateException("AppUpdateInvalidPackage");
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            progress(received);
        }
        if (received != asset.Size || !DigestMatches(asset.Digest, hash.GetHashAndReset()))
            throw new AppUpdateException("AppUpdateInvalidPackage");
    }

    private static bool IsDigest(string? digest) => digest is { Length: 71 }
        && digest.StartsWith("sha256:", StringComparison.Ordinal) && digest[7..].All(Uri.IsHexDigit);

    private static bool DigestMatches(string? digest, byte[] hash) => IsDigest(digest)
        && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(digest![7..]), hash);

    public static AppPackageIdentity ReadIdentity(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry("AppxManifest.xml") ?? throw new InvalidDataException();
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024
            });
            var document = XDocument.Load(reader);
            XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            var identity = document.Element(ns + "Package")?.Element(ns + "Identity") ?? throw new InvalidDataException();
            return new AppPackageIdentity(
                (string?)identity.Attribute("Name") ?? throw new InvalidDataException(),
                (string?)identity.Attribute("Publisher") ?? throw new InvalidDataException(),
                Version.Parse((string?)identity.Attribute("Version") ?? ""),
                (string?)identity.Attribute("ProcessorArchitecture") ?? throw new InvalidDataException());
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or ArgumentException or FormatException or OverflowException)
        { throw new AppUpdateException("AppUpdateInvalidPackage", exception); }
    }
}

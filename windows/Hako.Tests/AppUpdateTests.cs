using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hako.Core;
using Hako.Core.Updates;

namespace Hako.Tests;

[TestClass]
public sealed class AppUpdateTests
{
    private static readonly AppPackageIdentity Installed = new("hako", "CN=daske", new Version(1, 1, 1, 0), "x64");

    [TestMethod]
    public async Task LatestTraversesPagesAndSelectsNumericWindowsVersionForInstalledArchitecture()
    {
        var requests = new List<string>();
        using var http = Http(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            if (requests.Count == 1)
            {
                var response = Json(Release("1.9.0"), Release("99.0.0") with { Tag = "macos-v99.0.0" },
                    Release("9.0.0") with { Prerelease = true }, Release("8.0.0") with { Draft = true });
                response.Headers.Add("Link", "<https://api.github.com/repositories/123/releases?per_page=100&page=2>; rel=\"next\"");
                return response;
            }
            return Json(Release("1.10.0"), Release("2.0.0", "arm64"), Release("7.0.0") with { PublishedAt = null },
                Release("6.0.0") with { Tag = "windows-v6.0.0-beta" });
        });
        var latest = await new AppReleaseClient(http).LatestAsync("x64");
        Assert.AreEqual("windows-v1.10.0", latest!.Tag);
        Assert.AreEqual(2, requests.Count);
        using var armHttp = Http(_ => Json(Release("1.10.0"), Release("2.0.0", "arm64")));
        Assert.AreEqual("windows-v2.0.0", (await new AppReleaseClient(armHttp).LatestAsync("arm64"))!.Tag);
    }

    [TestMethod]
    public async Task RejectsUnsafeAndCyclicPaginationAndRateLimits()
    {
        foreach (var next in new[] { "https://example.com/releases", "https://api.github.com/users/123", AppReleaseClient.ReleasesUri + "?per_page=100&page=1" })
        {
            using var http = Http(_ =>
            {
                var response = Json(Release("1.2.0"));
                response.Headers.Add("Link", $"<{next}>; rel=\"next\"");
                return response;
            });
            await Assert.ThrowsExactlyAsync<AppUpdateException>(() => new AppReleaseClient(http).LatestAsync("x64"));
        }
        using var limited = Http(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var error = await Assert.ThrowsExactlyAsync<AppUpdateException>(() => new AppReleaseClient(limited).LatestAsync("x64"));
        Assert.AreEqual("AppUpdateRateLimited", error.ResourceKey);
        using var malformed = Http(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{") });
        await Assert.ThrowsExactlyAsync<AppUpdateException>(() => new AppReleaseClient(malformed).LatestAsync("x64"));
    }

    [TestMethod]
    public void StrictVersionsRejectMalformedTags()
    {
        foreach (var tag in new[] { "nightly", "windows-v1.2", "windows-v1.2.0.0", "windows-v01.2.0", "windows-v65536.0.0", "windows-v1.2.-1" })
            Assert.IsNull((Release("1.2.0") with { Tag = tag }).GetVersion(), tag);
        Assert.AreEqual(new Version(1, 10, 0, 0), Release("1.10.0").GetVersion());
    }

    [TestMethod]
    public async Task DownloadsAndVerifiesPackageAndDependencyThenDetectsTampering()
    {
        using var directory = new TemporaryDirectory();
        var main = Package();
        var dependency = Package(name: "framework", architecture: "neutral");
        var release = Release("1.2.0", bytes: main);
        release = release with { Assets = [.. release.Assets, Asset("Hako-1.2.0-windows-x64-dependency-framework.appx", dependency)] };
        using var http = Http(request => Bytes(request.RequestUri!.AbsolutePath.EndsWith(".appx") ? dependency : main));
        var download = new AppUpdateDownload(http, directory.Root, Installed);
        var update = await download.DownloadAsync(release, null, CancellationToken.None);
        Assert.AreEqual(1, update.Dependencies.Length);
        await download.VerifyAsync(update, CancellationToken.None);
        await File.WriteAllTextAsync(update.PackagePath, "tampered");
        await Assert.ThrowsExactlyAsync<AppUpdateException>(() => download.VerifyAsync(update, CancellationToken.None));
        AppUpdateDownload.Delete(update);
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(update.PackagePath)));
    }

    [TestMethod]
    public async Task RejectsWrongSizeDigestIdentityAndVersionAndCleansPartialFiles()
    {
        using var directory = new TemporaryDirectory();
        var bytes = Package();
        var original = Release("1.2.0", bytes: bytes);
        (AppRelease Release, byte[] Bytes)[] variants =
        [
            (original with { Assets = [original.Assets[0] with { Size = bytes.Length + 1 }] }, bytes),
            (original with { Assets = [original.Assets[0] with { Digest = "sha256:" + new string('0', 64) }] }, bytes),
            (original with { Assets = [original.Assets[0] with { Digest = null }] }, bytes),
            .. new[] { Package(name: "other"), Package(publisher: "CN=other"), Package(architecture: "arm64"), Package(version: "1.3.0.0") }
                .Select(data => (Release("1.2.0", bytes: data), data))
        ];
        foreach (var (release, payload) in variants)
        {
            using var http = Http(_ => Bytes(payload));
            await Assert.ThrowsExactlyAsync<AppUpdateException>(() => new AppUpdateDownload(http, directory.Root, Installed)
                .DownloadAsync(release, null, CancellationToken.None));
            Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(directory.Root, "AppUpdates")).Length);
        }
    }

    [TestMethod]
    public async Task CoordinatorSerializesRequestsAndDoesNotInstallWhileBlocked()
    {
        using var directory = new TemporaryDirectory();
        var bytes = Package();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        using var http = new HttpClient(new AsyncHandler(async (request, token) =>
        {
            if (request.RequestUri!.Host != "api.github.com") return Bytes(bytes);
            Interlocked.Increment(ref checks);
            reached.TrySetResult();
            await resume.Task.WaitAsync(token);
            return Json(Release("1.2.0", bytes: bytes));
        }));
        var installs = 0;
        string? blocked = "AppUpdateWaitGames";
        using var coordinator = new AppUpdateCoordinator(new AppReleaseClient(http), new AppUpdateDownload(http, directory.Root, Installed),
            Installed, true, _ => Task.FromResult<string?>(blocked), (_, _) => { installs++; return Task.CompletedTask; });
        var first = coordinator.CheckAsync();
        await reached.Task;
        await coordinator.CheckAsync();
        Assert.AreEqual(1, checks);
        resume.TrySetResult();
        await first;
        await coordinator.DownloadAsync();
        Assert.AreEqual(AppUpdatePhase.Ready, coordinator.Phase);
        await coordinator.InstallAsync();
        Assert.AreEqual("AppUpdateWaitGames", coordinator.ErrorResourceKey);
        Assert.AreEqual(0, installs);
        blocked = null;
        await coordinator.InstallAsync();
        Assert.AreEqual(1, installs);
    }

    [TestMethod]
    public async Task CancellationLeavesAvailableUpdateAndAutomaticErrorsStaySilent()
    {
        using var directory = new TemporaryDirectory();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new AsyncHandler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "api.github.com") return Json(Release("1.2.0", bytes: Package()));
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        }));
        using var coordinator = new AppUpdateCoordinator(new AppReleaseClient(http), new AppUpdateDownload(http, directory.Root, Installed),
            Installed, true, _ => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);
        await coordinator.CheckAsync();
        var download = coordinator.DownloadAsync();
        await reached.Task;
        coordinator.CancelDownload();
        await download;
        Assert.AreEqual(AppUpdatePhase.Available, coordinator.Phase);
        Assert.IsNull(coordinator.Downloaded);
        Assert.IsNull(coordinator.ErrorResourceKey);
        using var failedHttp = Http(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var failed = new AppUpdateCoordinator(new AppReleaseClient(failedHttp), new AppUpdateDownload(failedHttp, directory.Root, Installed),
            Installed, true, _ => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);
        await failed.CheckAsync(automatic: true);
        Assert.IsNull(failed.ErrorResourceKey);
        await failed.CheckAsync();
        Assert.AreEqual("AppUpdateFailed", failed.ErrorResourceKey);
    }

    private static AppRelease Release(string version, string architecture = "x64", byte[]? bytes = null) => new(
        "windows-v" + version, false, false, DateTimeOffset.UtcNow,
        [Asset($"Hako-{version}-windows-{architecture}.msix", bytes ?? [1, 2, 3])]);

    private static AppReleaseAsset Asset(string name, byte[] bytes) => new(name,
        "https://github.com/Liis17/Hako/releases/download/windows-v1.2.0/" + name, bytes.Length,
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    private static byte[] Package(string name = "hako", string publisher = "CN=daske", string architecture = "x64", string version = "1.2.0.0")
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("AppxManifest.xml");
            entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write($"<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\"><Identity Name=\"{name}\" Publisher=\"{publisher}\" ProcessorArchitecture=\"{architecture}\" Version=\"{version}\" /></Package>");
        }
        return stream.ToArray();
    }

    private static HttpResponseMessage Json(params AppRelease[] releases) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(releases, HakoJsonContext.Default.AppReleaseArray)) };
    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> handler) => new(new AsyncHandler((request, _) => Task.FromResult(handler(request))));
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}

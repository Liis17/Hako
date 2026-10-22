using System.Net;
using System.Text;
using Hako.Core.Instances;
using Hako.Core.Minecraft;

namespace Hako;

/// <summary>
/// `--profile-preview builds`: fake builds in every installation state inside the temporary preview root.
/// Nothing is downloaded: the Mojang catalog is a canned response and installation only reports progress.
/// </summary>
internal static class PreviewBuildsFixture
{
    private const string CatalogJson = """
        {"latest":{"release":"1.21.4","snapshot":"25w02a"},"versions":[
        {"id":"25w02a","type":"snapshot","url":"https://piston-meta.mojang.com/v1/packages/0000000000000000000000000000000000000001/25w02a.json","sha1":"0000000000000000000000000000000000000001"},
        {"id":"1.21.4","type":"release","url":"https://piston-meta.mojang.com/v1/packages/0000000000000000000000000000000000000002/1.21.4.json","sha1":"0000000000000000000000000000000000000002"},
        {"id":"1.20.1","type":"release","url":"https://piston-meta.mojang.com/v1/packages/0000000000000000000000000000000000000003/1.20.1.json","sha1":"0000000000000000000000000000000000000003"},
        {"id":"b1.7.3","type":"old_beta","url":"https://piston-meta.mojang.com/v1/packages/0000000000000000000000000000000000000004/b1.7.3.json","sha1":"0000000000000000000000000000000000000004"}]}
        """;

    public static MojangClient CreateMojangClient() => new(new HttpClient(new CatalogOnlyHandler()));

    public static InstallationCoordinator CreateInstallations(InstanceStore store) => new(
        store,
        (version, pinned, _) => Task.FromResult(new PreparedInstallation(
            version,
            pinned ?? GameArchitecture.X64,
            new MojangVersionMetadata { Id = version.Id },
            [],
            new MojangJavaRuntimePackage(),
            new MojangJavaRuntimeManifest(),
            [],
            [])),
        async (_, _, progress, cancellationToken) =>
        {
            progress?.Report(new InstallationProgress(InstallationProgressStage.Downloading, 420L << 20, 1000L << 20, 1200, 3100));
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        });

    /// <summary>Creates the fixture builds in the preview store; returns the ready build shown by the profile page.</summary>
    public static Guid Seed(InstanceStore store)
    {
        GameInstance Create(string name, string symbol, bool offline = false) => store.Create(
            new InstanceDraft { Name = name, IconSymbol = symbol, OfflineMode = offline, OfflineUsername = "PreviewPlayer" },
            "1.21.4",
            "https://piston-meta.mojang.com/v1/packages/0000000000000000000000000000000000000002/1.21.4.json",
            "0000000000000000000000000000000000000002",
            javaMajorVersion: 21);

        var ready = Create("Survival", "leaf.fill", offline: true);
        store.PinArchitecture(ready.Id, GameArchitecture.X64);
        store.CompleteInstallation(ready.Id, new InstallationResult(GameArchitecture.X64, "bin/javaw.exe", 21, false));

        Create("Creative", "cube.fill");
        Create("Skyblock", "moon.stars.fill");

        var paused = Create("Adventure", "bolt.fill");
        store.SetInstallationStatus(paused.Id, InstanceInstallationState.Paused, pauseRequested: true);

        var failed = Create("Hardcore", "flame.fill");
        store.SetInstallationStatus(failed.Id, InstanceInstallationState.Failed, pauseRequested: false, "mojang.http", "Preview failure");
        return ready.Id;
    }

    private sealed class CatalogOnlyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri?.AbsoluteUri == MojangClient.CatalogUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CatalogJson, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;

namespace Hako.Tests;

[TestClass]
public sealed class ModpackTests
{
    private static readonly Uri ApiBase = new("https://api.test/v2/");
    private static readonly Uri FabricBase = new("https://meta.test/v2/");
    private const string Minecraft = "1.21.4";
    private const string Loader = "0.16.10";

    [TestMethod]
    public void SwiftPlanFixtureUsesExactKeysAndMissingCompletedDefaultsFalse()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Modpack", "plan.json");
        var json = File.ReadAllText(fixturePath);
        var plan = JsonSerializer.Deserialize(json, HakoJsonContext.Default.ModpackPlan)!;

        Assert.AreEqual("fixtureproject", plan.ProjectId);
        Assert.AreEqual("fixtureversion", plan.VersionId);
        Assert.IsFalse(plan.Completed);
        Assert.AreEqual("1.21.4", plan.Manifest.Minecraft);
        Assert.AreEqual("0.16.10", plan.Manifest.LoaderVersion);
        Assert.IsTrue(plan.Manifest.Files[0].InstallOnClient);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(plan, HakoJsonContext.Default.ModpackPlan));
        var root = document.RootElement;
        Assert.IsTrue(root.TryGetProperty("projectID", out _));
        Assert.IsTrue(root.TryGetProperty("versionID", out _));
        Assert.IsTrue(root.TryGetProperty("versionNumber", out _));
        Assert.IsTrue(root.TryGetProperty("pageURL", out _));
        Assert.IsFalse(root.TryGetProperty("projectId", out _));
        Assert.AreEqual("https://modrinth.com/modpack/fixture", root.GetProperty("pageURL").GetString());
        Assert.AreEqual("optional", root.GetProperty("manifest").GetProperty("files")[0].GetProperty("env").GetProperty("client").GetString());
    }

    [TestMethod]
    public void ManifestRequiresSupportedDependenciesSafePathsAndExactDownloadHosts()
    {
        var manifest = Manifest([ManifestFile("mods/optional.jar", Bytes("optional"), client: "optional"),
            ManifestFile("mods/server.jar", Bytes("server"), client: "unsupported")]);
        manifest.Validate();
        Assert.IsTrue(manifest.Files[0].InstallOnClient);
        Assert.IsFalse(manifest.Files[1].InstallOnClient);

        Assert.ThrowsExactly<ModpackException>(() => (manifest with
        {
            Dependencies = new Dictionary<string, string> { ["minecraft"] = Minecraft, ["forge"] = "47" }
        }).Validate());
        Assert.ThrowsExactly<ModpackException>(() => (manifest with
        {
            Files = [ManifestFile("mods/bad.jar", Bytes("bad"), url: "https://evil.example/bad.jar")]
        }).Validate());
        foreach (var url in new[]
        {
            "http://cdn.modrinth.com/files/mod.jar",
            "https://cdn.modrinth.com.evil.example/files/mod.jar",
            "https://cdn.modrinth.com:444/files/mod.jar",
            "https://user@cdn.modrinth.com/files/mod.jar"
        })
        {
            Assert.ThrowsExactly<ModpackException>(() => (manifest with
            {
                Files = [ManifestFile("mods/bad.jar", Bytes("bad"), url: url)]
            }).Validate(), url);
        }
        var plan = MakePlan(Bytes("archive"), manifest);
        Assert.ThrowsExactly<ModpackException>(() => (plan with
        {
            Archive = plan.Archive with { Url = new Uri("https://cdn.modrinth.com:444/files/pack.mrpack") }
        }).Validate());
        Assert.ThrowsExactly<ModpackException>(() => (manifest with
        {
            Files = [ManifestFile("mods/case.jar", Bytes("a")), ManifestFile("mods/CASE.jar", Bytes("b"))]
        }).Validate());
        Assert.ThrowsExactly<ModpackException>(() => (manifest with
        {
            Files = [ManifestFile("mods/file", Bytes("a")), ManifestFile("mods/file/child", Bytes("b"))]
        }).Validate());
    }

    [TestMethod]
    [DataRow("../escape.jar")]
    [DataRow("mods/../../escape.jar")]
    [DataRow("C:/outside.jar")]
    [DataRow("C:outside.jar")]
    [DataRow("//server/share/file.jar")]
    [DataRow(@"\\server\share\file.jar")]
    [DataRow("mods/file.jar:stream")]
    [DataRow("mods//file.jar")]
    [DataRow("mods/")]
    [DataRow("mods/file.jar.")]
    [DataRow("mods/file.jar ")]
    [DataRow("mods/CON.txt")]
    [DataRow("mods/LPT1.jar")]
    [DataRow(".hako-modpack.json")]
    [DataRow("minecraft/options.txt")]
    [DataRow("libraries/client.jar")]
    [DataRow("assets/index.json")]
    public void WindowsUnsafePathsAreRejected(string path) =>
        Assert.ThrowsExactly<ModpackException>(() => ModpackPaths.Validate(path));

    [TestMethod]
    public void ArchiveAppliesCommonThenClientOverridesAndIgnoresServerOverrides()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = MakeArchive(temporary.Root, Manifest(), new Dictionary<string, byte[]>
        {
            ["overrides/config/settings.txt"] = Bytes("common"),
            ["client-overrides/config/settings.txt"] = Bytes("client"),
            ["server-overrides/config/settings.txt"] = Bytes("server")
        });

        var contents = ModpackArchive.Inspect(archivePath);

        Assert.AreEqual(1, contents.Overrides.Count);
        Assert.AreEqual("config/settings.txt", contents.Overrides[0].Path);
        Assert.AreEqual("client-overrides/config/settings.txt", contents.Overrides[0].EntryPath);
        Assert.AreEqual(Bytes("client").Length, contents.Overrides[0].Size);
    }

    [TestMethod]
    public void ArchiveRejectsSymlinksAndBoundedMetadataAndExpandedSize()
    {
        using var temporary = new TemporaryDirectory();
        var symlink = MakeArchive(temporary.Root, Manifest(), new Dictionary<string, byte[]> { ["overrides/link"] = Bytes("target") },
            attributes: new Dictionary<string, int> { ["overrides/link"] = unchecked((int)0xA0000000) });
        Assert.ThrowsExactly<ModpackException>(() => ModpackArchive.Inspect(symlink));

        var manyEntries = MakeArchive(temporary.Root, Manifest(), new Dictionary<string, byte[]> { ["overrides/a"] = [1] });
        Assert.ThrowsExactly<ModpackException>(() => ModpackArchive.Inspect(manyEntries,
            ModpackArchiveLimits.Default with { MaximumEntries = 1 }));
        Assert.ThrowsExactly<ModpackException>(() => ModpackArchive.Inspect(manyEntries,
            ModpackArchiveLimits.Default with { MaximumIndexSize = 1 }));
        Assert.ThrowsExactly<ModpackException>(() => ModpackArchive.Inspect(manyEntries,
            ModpackArchiveLimits.Default with { MaximumExpandedSize = 1 }));
        Assert.ThrowsExactly<ModpackException>(() => ModpackArchive.Inspect(manyEntries,
            ModpackArchiveLimits.Default with { MaximumArchiveSize = 1 }));
    }

    [TestMethod]
    public async Task ModrinthModpackSearchUsesOptionalExactMinecraftFacetAndVersionMetadata()
    {
        using var temporary = new TemporaryDirectory();
        var requests = new List<Uri>();
        var versionBytes = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            Version("release", "release", "2026-01-01T00:00:00Z", [Minecraft], ["fabric"], "pack.mrpack"),
            Version("beta", "beta", "2026-02-01T00:00:00Z", [Minecraft], ["minecraft"], "pack-beta.mrpack"),
            Version("forge", "release", "2026-03-01T00:00:00Z", [Minecraft], ["forge"], "forge.mrpack"),
            Version("wrong-game", "release", "2026-04-01T00:00:00Z", ["1.20.1"], ["minecraft"], "old.mrpack"),
            Version("wrong-file", "release", "2026-05-01T00:00:00Z", [Minecraft], ["minecraft"], "pack.jar")
        });
        using var client = new ModrinthClient(new HttpClient(new CallbackHttpHandler((request, _) =>
        {
            requests.Add(request.RequestUri!);
            var body = request.RequestUri!.AbsolutePath == "/v2/search"
                ? "{\"hits\":[],\"total_hits\":0}"
                : Encoding.UTF8.GetString(versionBytes);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        })), ApiBase, cacheRoot: temporary.Root);

        await client.SearchModpacksAsync("c++", null, ModrinthSort.Downloads, offset: 20);
        await client.SearchModpacksAsync("", Minecraft, ModrinthSort.Updated);
        var versions = await client.GetModpackVersionsAsync("project123", Minecraft);

        var searchWithoutVersion = Uri.UnescapeDataString(requests[0].Query);
        var searchWithVersion = Uri.UnescapeDataString(requests[1].Query);
        StringAssert.Contains(searchWithoutVersion, "project_type:modpack");
        StringAssert.Contains(searchWithoutVersion, "categories:fabric");
        StringAssert.Contains(searchWithoutVersion, "categories:minecraft");
        StringAssert.Contains(searchWithoutVersion, "environment!=dedicated_server_only");
        Assert.IsFalse(searchWithoutVersion.Contains("versions:", StringComparison.Ordinal));
        StringAssert.Contains(searchWithVersion, "versions:1.21.4");
        StringAssert.Contains(requests[0].Query, "offset=20");
        StringAssert.Contains(requests[0].Query, "index=downloads");
        StringAssert.Contains(requests[1].Query, "index=updated");
        CollectionAssert.AreEqual(new[] { "release", "beta" }, versions.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public async Task PreparePinsCatalogMinecraftAndFabricProfileWithoutRequestingFabricApi()
    {
        using var temporary = new TemporaryDirectory();
        var manifest = Manifest([], new Dictionary<string, string> { ["minecraft"] = Minecraft, ["fabric-loader"] = Loader });
        var archiveBytes = MakeArchiveBytes(manifest, new Dictionary<string, byte[]>());
        var handler = new PackHttpHandler(archiveBytes);
        using var http = new HttpClient(handler);
        using var mojang = new MojangClient(http, new FixedHostArchitectureProvider());
        using var fabric = new FabricClient(http, temporary.Paths, metaBase: FabricBase, modrinthBase: ApiBase);
        var installer = new ModpackInstaller(fabric.Modrinth, mojang, fabric, temporary.Paths);
        var project = new ModrinthProject { Id = "project123", Slug = "pack", ProjectType = "modpack", Title = "Pack" };
        var version = ModpackVersion("version123", archiveBytes, [Minecraft], ["fabric"]);

        var prepared = await installer.PrepareAsync(project, version);

        Assert.AreEqual(Minecraft, prepared.Plan.Manifest.Minecraft);
        Assert.AreEqual(Loader, prepared.Plan.Manifest.LoaderVersion);
        Assert.AreEqual(Minecraft, prepared.Minecraft.Version.Id);
        Assert.AreEqual("fabric-loader-0.16.10-1.21.4", prepared.FabricProfile!.Id);
        Assert.AreEqual(0, handler.Requests.Count(uri => uri.AbsolutePath.Contains(FabricClient.FabricApiProjectId, StringComparison.Ordinal)));
        Assert.AreEqual(0, handler.Requests.Count(uri => uri.AbsolutePath.Contains("api", StringComparison.OrdinalIgnoreCase)));

        await Assert.ThrowsExactlyAsync<ModpackException>(() => installer.PrepareAsync(project, version, "1.20.1"));
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("sha1")]
    [DataRow("sha512")]
    [DataRow("body")]
    public async Task PrepareAndInstallEmptyRequiredFileVerifiesBothHashesAndActualBody(string scenario)
    {
        using var temporary = new TemporaryDirectory();
        var file = ManifestFile("config/empty.txt", [], url: "https://cdn.modrinth.com/files/empty.txt");
        if (scenario is "sha1" or "sha512")
            file.Hashes[scenario] = scenario == "sha1" ? Sha1(Bytes("wrong")) : Sha512(Bytes("wrong"));
        var manifest = Manifest([file]);
        var archiveBytes = MakeArchiveBytes(manifest, new Dictionary<string, byte[]>());
        var handler = new PackHttpHandler(archiveBytes,
            new Dictionary<string, byte[]> { ["/files/empty.txt"] = scenario == "body" ? Bytes("not empty") : [] })
        {
            DeclaredFileLength = 0
        };
        using var http = new HttpClient(handler);
        using var mojang = new MojangClient(http, new FixedHostArchitectureProvider());
        using var fabric = new FabricClient(http, temporary.Paths, modrinthBase: ApiBase);
        var installer = new ModpackInstaller(fabric.Modrinth, mojang, fabric, temporary.Paths);
        var project = new ModrinthProject { Id = "project123", Slug = "pack", ProjectType = "modpack", Title = "Pack" };
        var prepared = await installer.PrepareAsync(project, ModpackVersion("version123", archiveBytes, [Minecraft], ["minecraft"]));
        var root = Path.Combine(temporary.Root, "Instance");
        Directory.CreateDirectory(Path.Combine(root, "minecraft"));
        prepared.Plan.Write(root);
        var destination = Path.Combine(root, "minecraft", "config", "empty.txt");

        if (scenario != "valid")
        {
            var error = await Assert.ThrowsExactlyAsync<ModpackException>(() => installer.InstallAsync(root, null, CancellationToken.None));
            Assert.AreEqual(ModpackError.DamagedFile, error.Error);
            Assert.IsFalse(File.Exists(destination));
            Assert.IsFalse(ModpackPlan.Read(root)!.Completed);
            Assert.AreEqual(1, handler.Requests.Count(uri => uri.AbsolutePath == "/files/empty.txt"));
            return;
        }

        await installer.InstallAsync(root, null, CancellationToken.None);
        Assert.IsTrue(File.Exists(destination));
        Assert.AreEqual(0L, new FileInfo(destination).Length);
        Assert.IsTrue(ModpackPlan.Read(root)!.Completed);
        Assert.AreEqual(1, handler.Requests.Count(uri => uri.AbsolutePath == "/files/empty.txt"));
        var requests = handler.Requests.Count;
        await (prepared.Plan with { Completed = false }).WriteAsync(root);
        await installer.InstallAsync(root, null, CancellationToken.None);
        Assert.AreEqual(requests, handler.Requests.Count, "Retry reuses the verified empty cache file.");
        File.Delete(destination);
        await installer.InstallAsync(root, null, CancellationToken.None);
        Assert.IsFalse(File.Exists(destination), "A completed import must not replay a deleted empty file.");
        Assert.AreEqual(requests, handler.Requests.Count);
    }

    [TestMethod]
    public async Task InstallStagesAllFilesThenPublishesOverridesAndCompletedReinstallDoesNotReplay()
    {
        using var temporary = new TemporaryDirectory();
        var required = Bytes("required mod");
        var optional = Bytes("optional mod");
        var unsupported = Bytes("server-only mod");
        var manifest = Manifest(
        [
            ManifestFile("mods/required.jar", required, "required", "https://cdn.modrinth.com/files/required.jar"),
            ManifestFile("mods/optional.jar", optional, "optional", "https://cdn.modrinth.com/files/optional.jar"),
            ManifestFile("mods/server-only.jar", unsupported, "unsupported", "https://cdn.modrinth.com/files/server-only.jar")
        ]);
        var archiveBytes = MakeArchiveBytes(manifest, new Dictionary<string, byte[]>
        {
            ["overrides/config/settings.txt"] = Bytes("common"),
            ["client-overrides/config/settings.txt"] = Bytes("client"),
            ["server-overrides/config/server.txt"] = Bytes("server")
        });
        var handler = new PackHttpHandler(archiveBytes, new Dictionary<string, byte[]>
        {
            ["/files/required.jar"] = required,
            ["/files/optional.jar"] = optional,
            ["/files/server-only.jar"] = unsupported
        });
        using var http = new HttpClient(handler);
        using var mojang = new MojangClient(http, new FixedHostArchitectureProvider());
        using var fabric = new FabricClient(http, temporary.Paths, modrinthBase: ApiBase);
        var installer = new ModpackInstaller(fabric.Modrinth, mojang, fabric, temporary.Paths);
        var plan = MakePlan(archiveBytes, manifest);
        var root = Path.Combine(temporary.Root, "Instance");
        Directory.CreateDirectory(Path.Combine(root, "minecraft"));
        plan.Write(root);

        await installer.InstallAsync(root, null, CancellationToken.None);

        Assert.AreEqual("required mod", await File.ReadAllTextAsync(Path.Combine(root, "minecraft", "mods", "required.jar")));
        Assert.AreEqual("optional mod", await File.ReadAllTextAsync(Path.Combine(root, "minecraft", "mods", "optional.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "minecraft", "mods", "server-only.jar")));
        Assert.AreEqual("client", await File.ReadAllTextAsync(Path.Combine(root, "minecraft", "config", "settings.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "minecraft", "config", "server.txt")));
        Assert.IsTrue(ModpackPlan.Read(root)!.Completed);

        await File.WriteAllTextAsync(Path.Combine(root, "minecraft", "mods", "required.jar"), "user edit");
        var requestsBeforeReplay = handler.Requests.Count;
        await installer.InstallAsync(root, null, CancellationToken.None);
        Assert.AreEqual("user edit", await File.ReadAllTextAsync(Path.Combine(root, "minecraft", "mods", "required.jar")));
        Assert.AreEqual(requestsBeforeReplay, handler.Requests.Count);
    }

    [TestMethod]
    public async Task FileDownloadFallsBackAfterWrongHashAndReusesVerifiedSha512Cache()
    {
        using var temporary = new TemporaryDirectory();
        var payload = Bytes("verified payload");
        var manifest = Manifest([ManifestFile("mods/verified.jar", payload,
            url: "https://cdn.modrinth.com/files/first.jar") with
        {
            Downloads =
            [
                new Uri("https://cdn.modrinth.com/files/first.jar"),
                new Uri("https://raw.githubusercontent.com/hako-fixtures/pack/main/verified.jar")
            ]
        }]);
        var archiveBytes = MakeArchiveBytes(manifest, new Dictionary<string, byte[]>());
        var handler = new PackHttpHandler(archiveBytes,
            new Dictionary<string, byte[]> { ["/files/first.jar"] = payload, ["/hako-fixtures/pack/main/verified.jar"] = payload })
        {
            FailFirstFileRequest = true
        };
        using var http = new HttpClient(handler);
        using var mojang = new MojangClient(http, new FixedHostArchitectureProvider());
        using var fabric = new FabricClient(http, temporary.Paths, modrinthBase: ApiBase);
        var installer = new ModpackInstaller(fabric.Modrinth, mojang, fabric, temporary.Paths);
        var plan = MakePlan(archiveBytes, manifest);
        var root = Path.Combine(temporary.Root, "First Instance");
        Directory.CreateDirectory(Path.Combine(root, "minecraft"));
        plan.Write(root);

        await installer.InstallAsync(root, null, CancellationToken.None);

        Assert.AreEqual("verified payload", await File.ReadAllTextAsync(Path.Combine(root, "minecraft", "mods", "verified.jar")));
        var fileRequests = handler.Requests.Where(uri => uri.AbsolutePath.EndsWith("verified.jar", StringComparison.Ordinal)
            || uri.AbsolutePath.EndsWith("first.jar", StringComparison.Ordinal)).ToArray();
        CollectionAssert.AreEqual(new[] { "/files/first.jar", "/hako-fixtures/pack/main/verified.jar" },
            fileRequests.Select(uri => uri.AbsolutePath).ToArray());

        var requestsAfterFirstInstall = handler.Requests.Count;
        await (plan with { Completed = false }).WriteAsync(root);
        await installer.InstallAsync(root, null, CancellationToken.None);

        Assert.AreEqual(requestsAfterFirstInstall, handler.Requests.Count, "The second import should use the verified SHA-512 cache.");
        Assert.IsTrue(ModpackPlan.Read(root)!.Completed);
    }

    [TestMethod]
    public async Task CancellingModpackDownloadRemovesStagingAndDoesNotMarkPlanComplete()
    {
        using var temporary = new TemporaryDirectory();
        var payload = Bytes("cancelled payload");
        var manifest = Manifest([ManifestFile("mods/cancelled.jar", payload,
            url: "https://cdn.modrinth.com/files/cancelled.jar")]);
        var archiveBytes = MakeArchiveBytes(manifest, new Dictionary<string, byte[]>());
        var handler = new PackHttpHandler(archiveBytes, new Dictionary<string, byte[]> { ["/files/cancelled.jar"] = payload })
        {
            BlockFileRequests = true
        };
        using var http = new HttpClient(handler);
        using var mojang = new MojangClient(http, new FixedHostArchitectureProvider());
        using var fabric = new FabricClient(http, temporary.Paths, modrinthBase: ApiBase);
        var installer = new ModpackInstaller(fabric.Modrinth, mojang, fabric, temporary.Paths);
        var root = Path.Combine(temporary.Root, "Cancelled Instance");
        Directory.CreateDirectory(Path.Combine(root, "minecraft"));
        MakePlan(archiveBytes, manifest).Write(root);
        using var cancellation = new CancellationTokenSource();

        var install = installer.InstallAsync(root, null, cancellation.Token);
        await handler.FileRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        try
        {
            await install;
            Assert.Fail("The canceled download must not complete the import.");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.IsFalse(ModpackPlan.Read(root)!.Completed);
        Assert.IsFalse(File.Exists(Path.Combine(root, "minecraft", "mods", "cancelled.jar")));
        Assert.AreEqual(0, Directory.GetDirectories(root, ".hako-modpack-*", SearchOption.TopDirectoryOnly).Length);
    }

    [TestMethod]
    public async Task QueueWritesPlanBeforeWorkWaitsForContentAndRetriesDamagedDownload()
    {
        await using var temporary = new TemporaryDirectory();
        var payload = Bytes("queued mod");
        var manifest = Manifest([ManifestFile("mods/queued.jar", payload, url: "https://cdn.modrinth.com/files/queued.jar")]);
        var archiveBytes = MakeArchiveBytes(manifest, new Dictionary<string, byte[]>());
        var handler = new PackHttpHandler(archiveBytes, new Dictionary<string, byte[]> { ["/files/queued.jar"] = payload })
        {
            FailFirstFileRequest = true,
            BlockFileRequests = true
        };
        using var http = new HttpClient(handler);
        using var mojang = new MojangClient(http, new FixedHostArchitectureProvider());
        using var fabric = new FabricClient(http, temporary.Paths, modrinthBase: ApiBase);
        var modpacks = new ModpackInstaller(fabric.Modrinth, mojang, fabric, temporary.Paths);
        var store = temporary.CreateStore();
        await store.LoadAsync();
        var minecraft = PreparedMinecraft();
        var coordinator = new InstallationCoordinator(store,
            (_, _, _) => Task.FromResult(minecraft),
            (prepared, root, _, _) =>
            {
                Assert.IsTrue(File.Exists(Path.Combine(root, ModpackPlan.Filename)), "The pinned plan must be durable before install work starts.");
                return Task.FromResult(new InstallationResult(prepared.Architecture, "bin/javaw.exe", 21, false));
            },
            modpackInstaller: modpacks);
        await coordinator.StartAsync();

        var instance = await coordinator.CreateAndEnqueueModpackAsync(
            new InstanceDraft { Name = "Queued Pack", ModLoader = InstanceModLoader.Vanilla },
            new PreparedModpack(MakePlan(archiveBytes, manifest), minecraft));
        Assert.IsNotNull(ModpackPlan.Read(store.GetInstanceDirectory(instance.Id)));
        var failed = await WaitForTerminalStateAsync(store, instance.Id);
        Assert.AreEqual(InstanceInstallationState.Failed, failed.State);
        Assert.AreEqual("modpack.damagedfile", failed.ErrorCode);
        Assert.IsFalse(ModpackPlan.Read(store.GetInstanceDirectory(instance.Id))!.Completed);
        Assert.IsFalse(File.Exists(Path.Combine(store.GetInstanceDirectory(instance.Id), "minecraft", "mods", "queued.jar")));

        await coordinator.RetryAsync(instance.Id);
        await handler.FileRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var installing = store.Instances.Single(item => item.Id == instance.Id);
        Assert.AreEqual(InstanceInstallationState.Installing, installing.State);
        Assert.IsFalse(ModpackPlan.Read(store.GetInstanceDirectory(instance.Id))!.Completed);
        handler.ReleaseFileRequest.TrySetResult();
        var ready = await WaitForTerminalStateAsync(store, instance.Id);
        Assert.AreEqual(InstanceInstallationState.Ready, ready.State);
        Assert.IsTrue(ModpackPlan.Read(store.GetInstanceDirectory(instance.Id))!.Completed);
        Assert.AreEqual("queued mod", await File.ReadAllTextAsync(Path.Combine(store.GetInstanceDirectory(instance.Id), "minecraft", "mods", "queued.jar")));

        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task PausedQueuedPlanResumesAndMissingPlanFailsInsteadOfInstallingVanilla()
    {
        await using var temporary = new TemporaryDirectory();
        var manifest = Manifest([]);
        var archiveBytes = MakeArchiveBytes(manifest, new Dictionary<string, byte[]>());
        var handler = new PackHttpHandler(archiveBytes);
        using var http = new HttpClient(handler);
        using var mojang = new MojangClient(http, new FixedHostArchitectureProvider());
        using var fabric = new FabricClient(http, temporary.Paths, modrinthBase: ApiBase);
        var modpacks = new ModpackInstaller(fabric.Modrinth, mojang, fabric, temporary.Paths);
        var store = temporary.CreateStore();
        await store.LoadAsync();
        var minecraft = PreparedMinecraft();
        var instance = store.Create(new InstanceDraft { Name = "Paused Pack" }, minecraft, modpackPlan: MakePlan(archiveBytes, manifest));
        store.SetInstallationStatus(instance.Id, InstanceInstallationState.Paused, pauseRequested: true);
        var coordinator = new InstallationCoordinator(store,
            (_, _, _) => Task.FromResult(minecraft),
            (prepared, _, _, _) => Task.FromResult(new InstallationResult(prepared.Architecture, "bin/javaw.exe", 21, false)),
            modpackInstaller: modpacks);
        await coordinator.StartAsync();
        Assert.AreEqual(InstanceInstallationState.Paused, store.Instances.Single(item => item.Id == instance.Id).State);
        await coordinator.ResumeAsync(instance.Id);
        Assert.AreEqual(InstanceInstallationState.Ready, (await WaitForTerminalStateAsync(store, instance.Id)).State);
        await coordinator.DisposeAsync();

        var missingStore = temporary.CreateStore();
        await missingStore.LoadAsync();
        var missing = missingStore.Create(new InstanceDraft { Name = "Missing Plan" }, minecraft, modpackPlan: MakePlan(archiveBytes, manifest));
        File.Delete(Path.Combine(missingStore.GetInstanceDirectory(missing.Id), ModpackPlan.Filename));
        var missingCoordinator = new InstallationCoordinator(missingStore,
            (_, _, _) => Task.FromResult(minecraft),
            (prepared, _, _, _) => Task.FromResult(new InstallationResult(prepared.Architecture, "bin/javaw.exe", 21, false)),
            modpackInstaller: modpacks);
        await missingCoordinator.StartAsync();
        var failed = await WaitForTerminalStateAsync(missingStore, missing.Id);
        Assert.AreEqual(InstanceInstallationState.Failed, failed.State);
        Assert.AreEqual("modpack.invalidmanifest", failed.ErrorCode);
        await missingCoordinator.DisposeAsync();
    }

    private static ModpackManifest Manifest(IReadOnlyList<ModpackManifestFile>? files = null,
        Dictionary<string, string>? dependencies = null) => new()
    {
        FormatVersion = 1,
        Game = "minecraft",
        VersionId = "pack-v1",
        Name = "Fixture Pack",
        Files = files?.ToList() ?? [],
        Dependencies = dependencies ?? new Dictionary<string, string> { ["minecraft"] = Minecraft }
    };

    private static ModpackManifestFile ManifestFile(string path, byte[] bytes, string client = "required",
        string url = "https://cdn.modrinth.com/files/content.jar") => new()
    {
        Path = path,
        Hashes = new Dictionary<string, string> { ["sha1"] = Sha1(bytes), ["sha512"] = Sha512(bytes) },
        Environment = new ModpackEnvironment { Client = client, Server = "optional" },
        Downloads = [new Uri(url)],
        FileSize = bytes.Length
    };

    private static ModpackPlan MakePlan(byte[] archive, ModpackManifest manifest) => new()
    {
        ProjectId = "project123",
        VersionId = "version123",
        VersionNumber = "1.0.0",
        PageUrl = new Uri("https://modrinth.com/modpack/fixture"),
        Archive = new ModpackFile
        {
            Filename = "fixture.mrpack",
            Url = new Uri("https://cdn.modrinth.com/packs/fixture.mrpack"),
            Size = archive.Length,
            Hashes = new Dictionary<string, string> { ["sha1"] = Sha1(archive), ["sha512"] = Sha512(archive) },
            Primary = true
        },
        Manifest = manifest
    };

    private static string MakeArchive(string directory, ModpackManifest manifest, IReadOnlyDictionary<string, byte[]> entries,
        IReadOnlyDictionary<string, int>? attributes = null)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".mrpack");
        File.WriteAllBytes(path, MakeArchiveBytes(manifest, entries, attributes));
        return path;
    }

    private static byte[] MakeArchiveBytes(ModpackManifest manifest, IReadOnlyDictionary<string, byte[]> entries,
        IReadOnlyDictionary<string, int>? attributes = null)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var index = archive.CreateEntry("modrinth.index.json", CompressionLevel.Fastest);
            using (var stream = index.Open())
                JsonSerializer.Serialize(stream, manifest, HakoJsonContext.Default.ModpackManifest);
            foreach (var (name, data) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                if (attributes?.TryGetValue(name, out var entryAttributes) == true) entry.ExternalAttributes = entryAttributes;
                using var stream = entry.Open();
                stream.Write(data);
            }
        }

        return output.ToArray();
    }

    private static ModrinthVersion Version(string id, string channel, string date, List<string> games, List<string> loaders, string filename)
    {
        var bytes = Bytes("archive");
        var file = new ModrinthFile
        {
            Filename = filename,
            Url = new Uri("https://cdn.modrinth.com/" + filename),
            Size = bytes.Length,
            Hashes = new Dictionary<string, string> { ["sha1"] = Sha1(bytes), ["sha512"] = Sha512(bytes) },
            Primary = true
        };
        return new ModrinthVersion
        {
            Id = id,
            ProjectId = "project123",
            VersionNumber = id,
            VersionType = channel,
            DatePublished = DateTimeOffset.Parse(date),
            GameVersions = games,
            Loaders = loaders,
            Files = [file]
        };
    }

    private static ModrinthVersion ModpackVersion(string id, byte[] archive, List<string> games, List<string> loaders) => new()
    {
        Id = id,
        ProjectId = "project123",
        VersionNumber = "1.0.0",
        VersionType = "release",
        DatePublished = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        GameVersions = games,
        Loaders = loaders,
        Files = [new ModrinthFile
        {
            Filename = "fixture.mrpack",
            Url = new Uri("https://cdn.modrinth.com/packs/fixture.mrpack"),
            Size = archive.Length,
            Hashes = new Dictionary<string, string> { ["sha1"] = Sha1(archive), ["sha512"] = Sha512(archive) },
            Primary = true
        }]
    };

    private static PreparedInstallation PreparedMinecraft()
    {
        var version = new MojangVersionEntry
        {
            Id = Minecraft,
            Type = "release",
            Url = "https://piston-meta.mojang.com/metadata.json",
            Sha1 = new string('a', 40)
        };
        return new PreparedInstallation(version, GameArchitecture.X64,
            new MojangVersionMetadata
            {
                Id = Minecraft,
                Downloads = new Dictionary<string, MojangDownload> { ["client"] = new() { Url = "https://piston-meta.mojang.com/client.jar", Sha1 = new string('0', 40), Size = 0 } },
                Libraries = [],
                JavaVersion = new MojangJavaRequirement { Component = "java-runtime", MajorVersion = 21 }
            }, [], new MojangJavaRuntimePackage(), new MojangJavaRuntimeManifest(), [], []);
    }

    private static async Task<GameInstance> WaitForTerminalStateAsync(InstanceStore store, Guid id)
    {
        var completion = new TaskCompletionSource<GameInstance>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? _, EventArgs __)
        {
            var current = store.Instances.FirstOrDefault(item => item.Id == id);
            if (current?.State is InstanceInstallationState.Ready or InstanceInstallationState.Failed)
                completion.TrySetResult(current);
        }

        store.InstancesChanged += Changed;
        try
        {
            Changed(null, EventArgs.Empty);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            store.InstancesChanged -= Changed;
        }
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static string Sha1(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
    private static string Sha512(byte[] bytes) => Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();

    private sealed class FixedHostArchitectureProvider : IHostArchitectureProvider
    {
        public HostArchitecture GetHostArchitecture() => new(HostMachineArchitecture.X64, false);
    }

    private sealed class PackHttpHandler : HttpMessageHandler
    {
        private readonly byte[] _archive;
        private readonly IReadOnlyDictionary<string, byte[]> _files;
        private int _fileRequests;

        public PackHttpHandler(byte[] archive, IReadOnlyDictionary<string, byte[]>? files = null)
        {
            _archive = archive;
            _files = files ?? new Dictionary<string, byte[]>();
        }

        public List<Uri> Requests { get; } = [];
        public bool FailFirstFileRequest { get; init; }
        public bool BlockFileRequests { get; init; }
        public long? DeclaredFileLength { get; init; }
        public TaskCompletionSource FileRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFileRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Requests) Requests.Add(uri);
            if (uri.AbsoluteUri == MojangClient.CatalogUrl) return Json(MojangCatalogJson());
            if (uri.AbsoluteUri == MojangClient.RuntimeIndexUrl) return Json(MojangRuntimeIndexJson());
            if (uri.AbsolutePath == "/metadata.json") return BytesResponse(MojangMetadataBytes(Minecraft));
            if (uri.AbsolutePath == "/runtime.json") return BytesResponse(MojangRuntimeBytes());
            if (uri.AbsolutePath == "/packs/fixture.mrpack") return BytesResponse(_archive);
            if (uri.AbsolutePath == "/v2/versions/loader/1.21.4")
                return Json("[{\"loader\":{\"version\":\"0.16.10\",\"stable\":true}}]");
            if (uri.AbsolutePath == "/v2/versions/loader/1.21.4/0.16.10/profile/json")
                return Json("{\"id\":\"fabric-loader-0.16.10-1.21.4\",\"inheritsFrom\":\"1.21.4\",\"mainClass\":\"net.fabricmc.loader.impl.launch.knot.KnotClient\",\"libraries\":[]}");
            if (uri.AbsolutePath.Contains(FabricClient.FabricApiProjectId, StringComparison.Ordinal))
                return Json("[]");
            if (_files.TryGetValue(uri.AbsolutePath, out var bytes))
            {
                var requestNumber = Interlocked.Increment(ref _fileRequests);
                if (BlockFileRequests && (!FailFirstFileRequest || requestNumber > 1))
                {
                    FileRequestStarted.TrySetResult();
                    await ReleaseFileRequest.Task.WaitAsync(cancellationToken);
                }

                if (FailFirstFileRequest && requestNumber == 1) return BytesResponse(Bytes("wrong first payload"));
                var response = BytesResponse(bytes);
                if (DeclaredFileLength is { } length) response.Content.Headers.ContentLength = length;
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        private static HttpResponseMessage BytesResponse(byte[] bytes) => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
    }

    private static string MojangCatalogJson()
    {
        var metadata = MojangMetadataBytes(Minecraft);
        return JsonSerializer.Serialize(new
        {
            latest = new { release = Minecraft },
            versions = new[] { new { id = Minecraft, type = "release", url = "https://piston-meta.mojang.com/metadata.json", sha1 = Sha1(metadata) } }
        });
    }

    private static byte[] MojangMetadataBytes(string id) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        id,
        downloads = new { client = new { url = "https://piston-meta.mojang.com/client.jar", sha1 = new string('0', 40), size = 0 } },
        libraries = Array.Empty<object>(),
        javaVersion = new { component = "java-runtime", majorVersion = 21 },
        mainClass = "net.minecraft.client.main.Main",
        arguments = new { game = Array.Empty<object>(), jvm = Array.Empty<object>() }
    });

    private static byte[] MojangRuntimeBytes() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        files = new Dictionary<string, object>
        {
            ["bin/javaw.exe"] = new { type = "file", executable = true,
                downloads = new { raw = new { url = "https://piston-meta.mojang.com/javaw.exe", sha1 = new string('0', 40), size = 0 } } }
        }
    });

    private static string MojangRuntimeIndexJson()
    {
        var runtime = MojangRuntimeBytes();
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["windows-x64"] = new Dictionary<string, object>
            {
                ["java-runtime"] = new[]
                {
                    new { manifest = new { url = "https://piston-meta.mojang.com/runtime.json", sha1 = Sha1(runtime), size = runtime.Length },
                        version = new { name = "21.0.1", released = "2026-01-01T00:00:00Z" } }
                }
            }
        });
    }
}

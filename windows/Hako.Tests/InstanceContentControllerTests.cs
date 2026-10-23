using System.IO.Compression;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;

namespace Hako.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class InstanceContentControllerTests
{
    [TestMethod]
    public async Task InstallWalksRequiredDependenciesBreadthFirstDeduplicatesCyclesAndResolvesVersionOnly()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary);
        using var server = new ModrinthServer();
        server.ObserveContentDirectory(Path.Combine(root, "minecraft", "mods"));
        server.AddProject("rootmod1", "Root", "mod");
        server.AddProject("depmod01", "Dependency mod", "mod");
        server.AddProject("packdep1", "Dependency pack", "resourcepack");
        var rootFile = server.File("root.jar", Encoding.UTF8.GetBytes("root"));
        var modFile = server.File("dep.jar", Encoding.UTF8.GetBytes("dependency"));
        var packFile = server.File("dep.zip", Encoding.UTF8.GetBytes("resourcepack"));
        server.AddVersion("rootmod1", "rootver01", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Mod, [rootFile],
            [Dependency(projectId: "depmod01"), Dependency(projectId: "depmod01"), Dependency(versionId: "packver01"), Dependency(projectId: "packdep1", type: "optional")]);
        server.AddVersion("depmod01", "depver001", "1.0", "release", "2025-01-02T00:00:00Z",
            ModrinthContentKind.Mod, [modFile], [Dependency(projectId: "rootmod1")]);
        server.AddVersion("packdep1", "packver01", "1.0", "release", "2025-01-03T00:00:00Z",
            ModrinthContentKind.ResourcePack, [packFile]);

        var requests = new List<ContentConfirmationRequest>();
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric,
            (request, _) => { requests.Add(request); return Task.FromResult(ContentConfirmationChoice.Primary); });

        InstanceContentState? packInitial = null;
        Task? packRead = null;
        Task? packCheck = null;
        controller.StateChanged += (_, args) =>
        {
            if (args.Key.Kind != ModrinthContentKind.ResourcePack || packInitial is not null) return;
            packInitial = args.State;
            packRead = controller.ReloadAsync(instance.Id, ModrinthContentKind.ResourcePack);
            packCheck = controller.CheckUpdatesAsync(instance.Id, ModrinthContentKind.ResourcePack);
        };
        var result = await controller.InstallAsync(instance.Id, ModrinthContentKind.Mod,
            server.Project("rootmod1"));
        await Task.WhenAll(packRead!, packCheck!);
        Assert.IsTrue(packInitial!.IsBusy);
        var packState = controller.GetState(instance.Id, ModrinthContentKind.ResourcePack);
        Assert.AreEqual(packInitial.Revision, packState.Revision);
        Assert.IsFalse(packState.IsBusy);
        Assert.AreEqual("dep.zip", packState.Items.Single().Name);

        Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
        Assert.AreEqual(1, requests.Count);
        Assert.AreEqual(ContentConfirmationCode.Dependencies, requests[0].Code);
        Assert.IsTrue(requests[0].PrimaryActionAvailable);
        CollectionAssert.AreEquivalent(new[] { "depmod01", "packdep1" }, requests[0].Projects.Select(project => project.Id).ToArray());
        Assert.AreEqual(3, server.DownloadRequests);
        Assert.IsTrue(server.AllDownloadsSawEmptyContentDirectory, "No file may be published before every planned download finishes.");
        var mods = await content.ListAsync(instance.Id, ModrinthContentKind.Mod);
        var packs = await content.ListAsync(instance.Id, ModrinthContentKind.ResourcePack);
        Assert.AreEqual("depmod01", mods.Single(item => item.Name == "dep.jar").Origin?.ProjectID);
        Assert.AreEqual("rootmod1", mods.Single(item => item.Name == "root.jar").Origin?.ProjectID);
        Assert.AreEqual("packdep1", packs.Single().Origin?.ProjectID);
        Assert.AreEqual(root, store.GetInstanceDirectory(instance.Id));
    }

    [TestMethod]
    public async Task DisabledDependencyIsOfferedForEnableAndUnsupportedOrMissingDependenciesAreExplained()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary);
        using var server = new ModrinthServer();
        server.AddProject("rootmod1", "Root", "mod");
        server.AddProject("depmod01", "Disabled dependency", "mod");
        server.AddProject("missing01", "Missing compatible version", "mod");
        server.AddProject("datapack1", "Unsupported dependency", "datapack");
        var disabledBytes = Encoding.UTF8.GetBytes("installed dependency");
        var source = Path.Combine(temporary.Root, "dep.jar");
        await File.WriteAllBytesAsync(source, disabledBytes);
        await content.ImportAsync(instance.Id, ModrinthContentKind.Mod, source);
        var modFolder = Path.Combine(root, "minecraft", "mods");
        InstanceContent.WriteRegistry(root, new ModRegistry
        {
            ApiProvisioned = false,
            Files = new Dictionary<string, ModOrigin>(StringComparer.Ordinal)
            {
                ["dep.jar"] = Origin("depmod01", "depver001", disabledBytes)
            }
        });
        var disabled = (await content.ListAsync(instance.Id, ModrinthContentKind.Mod)).Single();
        await content.SetEnabledAsync(instance.Id, disabled, ModrinthContentKind.Mod, enabled: false);
        var rootFile = server.File("root.jar", Encoding.UTF8.GetBytes("root"));
        server.AddVersion("rootmod1", "rootver01", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Mod, [rootFile],
            [Dependency(projectId: "depmod01"), Dependency(projectId: "missing01"), Dependency(projectId: "datapack1")]);
        server.AddVersion("missing01", "missingold", "old", "release", "2024-12-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("missing-old.jar", [1])], gameVersions: ["1.20.3"]);

        ContentConfirmationRequest? request = null;
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric,
            (value, _) => { request = value; return Task.FromResult(ContentConfirmationChoice.Primary); });
        var result = await controller.InstallAsync(instance.Id, ModrinthContentKind.Mod, server.Project("rootmod1"));

        Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
        Assert.IsNotNull(request);
        Assert.IsTrue(request!.Projects.Any(project => project.Id == "depmod01"
            && project.NoteCode == ContentConfirmationNoteCode.DisabledWillBeEnabled));
        Assert.IsTrue(request.Projects.Any(project => project.Id == "missing01"
            && project.NoteCode == ContentConfirmationNoteCode.NoCompatibleVersion));
        Assert.IsTrue(request.Projects.Any(project => project.Id == "datapack1"
            && project.NoteCode == ContentConfirmationNoteCode.Unsupported));
        Assert.IsTrue(File.Exists(Path.Combine(modFolder, "dep.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(modFolder, "dep.jar.disabled")));
        Assert.IsTrue((await content.ListAsync(instance.Id, ModrinthContentKind.Mod))
            .Single(item => item.Name == "dep.jar").Enabled);
        Assert.IsFalse(File.Exists(Path.Combine(modFolder, "missing-old.jar")));
    }

    [TestMethod]
    public async Task CorruptOriginRegistryKeepsFilesVisibleAndBlocksMutationsWithoutOverwritingBytes()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary);
        var modFolder = Path.Combine(root, "minecraft", "mods");
        Directory.CreateDirectory(modFolder);
        await File.WriteAllBytesAsync(Path.Combine(modFolder, "existing.jar"), [1, 2, 3]);
        var registryPath = Path.Combine(root, "minecraft", ".hako-mods.json");
        var corruptRegistry = Encoding.UTF8.GetBytes("{ broken registry");
        await File.WriteAllBytesAsync(registryPath, corruptRegistry);
        var source = Path.Combine(temporary.Root, "new.jar");
        await File.WriteAllBytesAsync(source, [4, 5, 6]);
        using var server = new ModrinthServer();
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric);

        await controller.ReloadAsync(instance.Id, ModrinthContentKind.Mod);
        var state = controller.GetState(instance.Id, ModrinthContentKind.Mod);
        Assert.AreEqual(ContentFailureCode.RegistryCorrupt, state.Error?.Code);
        Assert.AreEqual("existing.jar", state.Items.Single().Name);
        Assert.IsNull(state.Items.Single().Origin);

        var import = await controller.ImportLocalAsync(instance.Id, ModrinthContentKind.Mod, source);

        Assert.IsFalse(import.Succeeded);
        Assert.AreEqual(ContentFailureCode.RegistryCorrupt, import.Failure?.Code);
        CollectionAssert.AreEqual(corruptRegistry, await File.ReadAllBytesAsync(registryPath));
        Assert.IsFalse(File.Exists(Path.Combine(modFolder, "new.jar")));
    }

    [TestMethod]
    public async Task DependencyLimitIsShownAndOnlyFirstTwentyNineDependenciesArePlanned()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, _, content) = await CreateReadyInstanceAsync(temporary);
        using var server = new ModrinthServer();
        server.AddProject("rootmod1", "Root", "mod");
        var dependencies = new List<ModrinthDependency>();
        var rootFile = server.File("root.jar", [0]);
        for (var index = 1; index <= 30; index++)
        {
            var id = $"dep{index:0000}";
            server.AddProject(id, id, "mod");
            server.AddVersion(id, $"ver{index:0000}", "1.0", "release", "2025-01-02T00:00:00Z",
                ModrinthContentKind.Mod, [server.File($"{id}.jar", [(byte)index])]);
            dependencies.Add(Dependency(projectId: id));
        }

        server.AddVersion("rootmod1", "rootver01", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Mod, [rootFile], dependencies);
        ContentConfirmationRequest? request = null;
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric,
            (value, _) => { request = value; return Task.FromResult(ContentConfirmationChoice.Primary); });

        var result = await controller.InstallAsync(instance.Id, ModrinthContentKind.Mod, server.Project("rootmod1"));

        Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
        Assert.IsNotNull(request);
        Assert.IsTrue(request!.Projects.Any(project => project.NoteCode == ContentConfirmationNoteCode.ProjectLimitReached));
        Assert.AreEqual(30, (await content.ListAsync(instance.Id, ModrinthContentKind.Mod)).Count);
    }

    [TestMethod]
    public async Task DependencyPlanWithoutUsableEntriesOffersRootOnlyAlternative()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, _, content) = await CreateReadyInstanceAsync(temporary);
        using var server = new ModrinthServer();
        server.AddProject("rootmod1", "Root", "mod");
        server.AddProject("badpack01", "Not handled here", "datapack");
        server.AddProject("missing01", "No matching version", "resourcepack");
        var rootFile = server.File("root.jar", [1, 2]);
        server.AddVersion("rootmod1", "rootver01", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Mod, [rootFile], [Dependency(projectId: "badpack01"), Dependency(projectId: "missing01")]);
        server.AddVersion("missing01", "missingold", "old", "release", "2024-12-01T00:00:00Z",
            ModrinthContentKind.ResourcePack, [server.File("old.zip", [5])], gameVersions: ["1.20.3"]);
        ContentConfirmationRequest? request = null;
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric,
            (value, _) => { request = value; return Task.FromResult(ContentConfirmationChoice.Alternative); });

        var result = await controller.InstallAsync(instance.Id, ModrinthContentKind.Mod, server.Project("rootmod1"));

        Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
        Assert.IsNotNull(request);
        Assert.IsFalse(request!.PrimaryActionAvailable);
        Assert.IsTrue(request.AlternativeActionAvailable);
        Assert.AreEqual(1, (await content.ListAsync(instance.Id, ModrinthContentKind.Mod)).Count);
        Assert.AreEqual(0, (await content.ListAsync(instance.Id, ModrinthContentKind.ResourcePack)).Count);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task CancellationDuringConfirmationOrDownloadLeavesFilesUntouchedAndReleasesLease()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary);
        using var server = new ModrinthServer();
        server.AddProject("rootmod1", "Root", "mod");
        server.AddProject("depmod01", "Dependency", "mod");
        var rootFile = server.File("root.jar", [1]);
        var depFile = server.File("dep.jar", [2]);
        server.AddVersion("rootmod1", "rootver01", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Mod, [rootFile], [Dependency(projectId: "depmod01")]);
        server.AddVersion("depmod01", "depver001", "1.0", "release", "2025-01-02T00:00:00Z",
            ModrinthContentKind.Mod, [depFile]);
        var confirmationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric, async (_, token) =>
        {
            confirmationStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ContentConfirmationChoice.Primary;
        });
        using var cancellation = new CancellationTokenSource();
        var install = controller.InstallAsync(instance.Id, ModrinthContentKind.Mod, server.Project("rootmod1"),
            cancellationToken: cancellation.Token);
        await confirmationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(store.TryBeginLaunch(instance.Id, out _, out var launchReason));
        Assert.AreEqual(InstanceStorageError.BuildFilesBusy, launchReason);
        store.SetInstallationStatus(instance.Id, InstanceInstallationState.Queued, pauseRequested: false);
        Assert.IsFalse(store.TryBeginInstallation(instance.Id, out _, out var installReason));
        Assert.AreEqual(InstanceStorageError.BuildFilesBusy, installReason);
        store.SetInstallationStatus(instance.Id, InstanceInstallationState.Ready, pauseRequested: false);
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => install);
        Assert.AreEqual(0, Directory.Exists(Path.Combine(root, "minecraft", "mods"))
            ? Directory.EnumerateFileSystemEntries(Path.Combine(root, "minecraft", "mods")).Count() : 0);
        Assert.IsFalse(controller.GetState(instance.Id, ModrinthContentKind.Mod).IsBusy);
        Assert.IsTrue(store.TryBeginLaunch(instance.Id, out var launchLease, out _));
        launchLease!.Dispose();

        server.OnDownload = async token => await Task.Delay(Timeout.InfiniteTimeSpan, token);
        var single = server.WithProjectVersion("rootmod1", "rootver01", "1.0", "release", [rootFile]);
        server.ReplaceVersions("rootmod1", [single]);
        var stagingBeforeCancellation = Directory.GetDirectories(Path.GetTempPath(), "Hako-Modrinth-*")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var nextCancellation = new CancellationTokenSource();
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.DownloadStarted = downloadStarted;
        var downloadTask = controller.InstallAsync(instance.Id, ModrinthContentKind.Mod, server.Project("rootmod1"),
            cancellationToken: nextCancellation.Token);
        await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        nextCancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => downloadTask);
        Assert.AreEqual(0, Directory.Exists(Path.Combine(root, "minecraft", "mods"))
            ? Directory.EnumerateFileSystemEntries(Path.Combine(root, "minecraft", "mods")).Count() : 0);
        CollectionAssert.AreEqual(stagingBeforeCancellation.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            Directory.GetDirectories(Path.GetTempPath(), "Hako-Modrinth-*").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [TestMethod]
    public async Task DecliningRootCollisionDoesNotReportInstallSuccessOrChangeExistingFile()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary);
        using var server = new ModrinthServer();
        server.AddProject("rootmod1", "Root", "mod");
        var existingBytes = Encoding.UTF8.GetBytes("user file");
        var folder = Path.Combine(root, "minecraft", "mods");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "root.jar"), existingBytes);
        var file = server.File("root.jar", Encoding.UTF8.GetBytes("downloaded"));
        server.AddVersion("rootmod1", "rootver01", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Mod, [file]);
        var prompts = 0;
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric,
            (request, _) =>
            {
                prompts++;
                Assert.AreEqual(ContentConfirmationCode.ReplaceExisting, request.Code);
                return Task.FromResult(ContentConfirmationChoice.Cancel);
            });

        var result = await controller.InstallAsync(instance.Id, ModrinthContentKind.Mod, server.Project("rootmod1"));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(1, prompts);
        CollectionAssert.AreEqual(existingBytes, await File.ReadAllBytesAsync(Path.Combine(folder, "root.jar")));
        Assert.IsNull((await content.ListAsync(instance.Id, ModrinthContentKind.Mod)).Single().Origin);
    }

    [TestMethod]
    public async Task FabricLoaderAndJavaPredicatesRequireDestructiveConfirmationAndCancellationChangesNothing()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary, loader: "0.16.9", java: 17);
        using var server = new ModrinthServer();
        server.AddProject("rootmod1", "Root", "mod");
        var jar = FabricJar("root", "{\"fabricloader\":\">=0.20.0\",\"java\":\">=21\"}");
        server.AddVersion("rootmod1", "rootver01", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("root.jar", jar)]);
        ContentConfirmationRequest? prompt = null;
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric,
            (request, _) => { prompt = request; return Task.FromResult(ContentConfirmationChoice.Cancel); });

        var result = await controller.InstallAsync(instance.Id, ModrinthContentKind.Mod, server.Project("rootmod1"));

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(prompt);
        Assert.AreEqual(ContentConfirmationCode.IncompatibleContent, prompt!.Code);
        Assert.IsTrue(prompt.Destructive);
        Assert.IsTrue(prompt.Projects.Any(project => project.NoteCode == ContentConfirmationNoteCode.FabricLoaderRequirement));
        Assert.IsTrue(prompt.Projects.Any(project => project.NoteCode == ContentConfirmationNoteCode.JavaRequirement));
        Assert.AreEqual(0, Directory.Exists(Path.Combine(root, "minecraft", "mods"))
            ? Directory.EnumerateFileSystemEntries(Path.Combine(root, "minecraft", "mods")).Count() : 0);
    }

    [TestMethod]
    public async Task CheckRecognizesManualDisabledFileAndUpdatePreservesDisabledStateAndOrigin()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary);
        using var server = new ModrinthServer();
        server.AddProject("project01", "Recognized mod", "mod", iconUrl: "https://cdn.modrinth.com/icon.png");
        var oldBytes = Encoding.UTF8.GetBytes("old bytes");
        var oldVersion = server.AddVersion("project01", "oldver001", "1.0", "beta", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("old.jar", oldBytes)]);
        var newVersion = server.AddVersion("project01", "newver001", "2.0", "release", "2025-02-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("new.jar", Encoding.UTF8.GetBytes("new bytes"))]);
        server.Recognize(Hash(oldBytes), oldVersion);
        server.AddUpdate(Hash(oldBytes), newVersion);
        var source = Path.Combine(temporary.Root, "old.jar");
        await File.WriteAllBytesAsync(source, oldBytes);
        await content.ImportAsync(instance.Id, ModrinthContentKind.Mod, source);
        var enabled = (await content.ListAsync(instance.Id, ModrinthContentKind.Mod)).Single();
        await content.SetEnabledAsync(instance.Id, enabled, ModrinthContentKind.Mod, enabled: false);
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric);

        await controller.CheckUpdatesAsync(instance.Id, ModrinthContentKind.Mod);
        var state = controller.GetState(instance.Id, ModrinthContentKind.Mod);
        Assert.IsFalse(state.IsCheckingUpdates);
        Assert.AreEqual("project01", state.Items.Single().Origin?.ProjectID);
        Assert.AreEqual("oldver001", state.Items.Single().Origin?.VersionID);
        Assert.AreEqual("https://modrinth.com/mod/project01", state.Items.Single().Origin?.PageURL.AbsoluteUri);
        Assert.IsTrue(state.HasUpdates, state.UpdateMessage?.ToString());

        var updateResult = await controller.UpdateAsync(instance.Id, ModrinthContentKind.Mod, state.Items.Single());

        Assert.IsTrue(updateResult.Succeeded, updateResult.Failure?.ToString());
        var updated = (await content.ListAsync(instance.Id, ModrinthContentKind.Mod)).Single();
        Assert.IsFalse(updated.Enabled);
        Assert.AreEqual("new.jar.disabled", updated.Name);
        Assert.AreEqual("newver001", updated.Origin?.VersionID);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("new bytes"), await File.ReadAllBytesAsync(updated.Path));
    }

    [TestMethod]
    public async Task UpdateExpectedHashProtectsChangedFileAndOlderReleaseIsNotOfferedToNewerBeta()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, _, content) = await CreateReadyInstanceAsync(temporary);
        using var server = new ModrinthServer();
        server.AddProject("project01", "Mod", "mod");
        var oldBytes = Encoding.UTF8.GetBytes("beta file");
        var oldVersion = server.AddVersion("project01", "betaver01", "1.0-beta", "beta", "2025-03-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("mod.jar", oldBytes)]);
        var olderRelease = server.AddVersion("project01", "release01", "1.0", "release", "2025-02-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("mod.jar", Encoding.UTF8.GetBytes("older release"))]);
        server.Recognize(Hash(oldBytes), oldVersion);
        server.AddUpdate(Hash(oldBytes), olderRelease);
        var source = Path.Combine(temporary.Root, "mod.jar");
        await File.WriteAllBytesAsync(source, oldBytes);
        await content.ImportAsync(instance.Id, ModrinthContentKind.Mod, source);
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric);

        await controller.CheckUpdatesAsync(instance.Id, ModrinthContentKind.Mod);
        Assert.IsFalse(controller.GetState(instance.Id, ModrinthContentKind.Mod).HasUpdates);

        var newer = server.AddVersion("project01", "newver001", "2.0", "release", "2025-04-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("new.jar", Encoding.UTF8.GetBytes("new bytes"))]);
        server.AddUpdate(Hash(oldBytes), newer);
        await controller.CheckUpdatesAsync(instance.Id, ModrinthContentKind.Mod);
        var state = controller.GetState(instance.Id, ModrinthContentKind.Mod);
        Assert.IsTrue(state.HasUpdates, state.UpdateMessage?.ToString());
        await File.WriteAllBytesAsync(state.Items.Single().Path, Encoding.UTF8.GetBytes("changed by user"));
        var result = await controller.UpdateAsync(instance.Id, ModrinthContentKind.Mod, state.Items.Single());

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ContentFailureCode.ContentOperationFailed, result.Failure?.Code);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("changed by user"), await File.ReadAllBytesAsync(state.Items.Single().Path));
    }

    [TestMethod]
    public async Task LinkedResourcePackUpdateUsesDatapackVersionsAndRetainsFileType()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary);
        using var server = new ModrinthServer();
        server.AddProject("datapack1", "Datapack", "mod");
        var oldPackBytes = Encoding.UTF8.GetBytes("old linked pack");
        var oldPack = server.File("linked.zip", oldPackBytes, "required-resource-pack");
        var oldMain = server.File("data.zip", [1]);
        var oldVersion = server.AddVersion("datapack1", "oldpack01", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Datapack, [oldMain, oldPack]);
        var nextPack = server.File("linked.zip", Encoding.UTF8.GetBytes("updated linked pack"), "required-resource-pack");
        var nextMain = server.File("data.zip", [2]);
        var newVersion = server.AddVersion("datapack1", "newpack01", "2.0", "release", "2025-02-01T00:00:00Z",
            ModrinthContentKind.Datapack, [nextMain, nextPack]);
        server.Recognize(Hash(oldPackBytes), oldVersion);
        server.AddUpdate(Hash(oldPackBytes), newVersion);
        var source = Path.Combine(temporary.Root, "linked.zip");
        await File.WriteAllBytesAsync(source, oldPackBytes);
        await content.ImportAsync(instance.Id, ModrinthContentKind.ResourcePack, source);
        InstanceContent.WriteRegistry(root, ".hako-resourcepacks.json", new ModRegistry
        {
            ApiProvisioned = false,
            Files = new Dictionary<string, ModOrigin>(StringComparer.Ordinal)
            {
                ["linked.zip"] = new ModOrigin
                {
                    Source = ModSource.Modrinth,
                    ProjectID = "datapack1",
                    VersionID = oldVersion.Id,
                    PageURL = new Uri("https://modrinth.com/mod/datapack1"),
                    Sha512 = Hash(oldPackBytes),
                    FileType = "required-resource-pack"
                }
            }
        });
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric);

        await controller.CheckUpdatesAsync(instance.Id, ModrinthContentKind.ResourcePack);
        var state = controller.GetState(instance.Id, ModrinthContentKind.ResourcePack);
        Assert.IsTrue(state.HasUpdates, state.UpdateMessage?.ToString());
        var result = await controller.UpdateAsync(instance.Id, ModrinthContentKind.ResourcePack, state.Items.Single());

        Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
        var updated = (await content.ListAsync(instance.Id, ModrinthContentKind.ResourcePack)).Single();
        Assert.AreEqual("newpack01", updated.Origin?.VersionID);
        Assert.AreEqual("required-resource-pack", updated.Origin?.FileType);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("updated linked pack"), await File.ReadAllBytesAsync(updated.Path));
    }

    [TestMethod]
    public async Task FabricApiUpdateIsNotOfferedWhenLatestFileRejectsPinnedLoader()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary, loader: "0.16.9");
        using var server = new ModrinthServer();
        var currentFile = server.File("fabric-api-current.jar", FabricJar("fabric_api", "{}"));
        var current = new FabricApiDescriptor(FabricClient.FabricApiProjectId, "apivers01", "1.0.0", "release",
            currentFile.Filename, currentFile.Url.AbsoluteUri, currentFile.Bytes.Length, currentFile.Sha1, currentFile.Sha512);
        var incompatibleFile = server.File("fabric-api-new.jar", FabricJar("fabric_api", "{\"fabricloader\":\">=0.17.0\"}"));
        server.AddVersion(FabricClient.FabricApiProjectId, "apivers02", "2.0.0", "release", "2025-02-01T00:00:00Z",
            ModrinthContentKind.Mod, [incompatibleFile]);
        var modFolder = Path.Combine(root, "minecraft", "mods");
        Directory.CreateDirectory(modFolder);
        await File.WriteAllBytesAsync(Path.Combine(modFolder, currentFile.Filename), currentFile.Bytes);
        InstanceContent.WriteRegistry(root, new ModRegistry
        {
            ApiProvisioned = true,
            Files = new Dictionary<string, ModOrigin>(StringComparer.Ordinal)
            {
                [currentFile.Filename.ToLowerInvariant()] = ModOrigin.FromApi(current)
            }
        });
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric);

        await controller.CheckUpdatesAsync(instance.Id, ModrinthContentKind.Mod);

        var state = controller.GetState(instance.Id, ModrinthContentKind.Mod);
        Assert.IsFalse(state.HasUpdates);
        Assert.AreEqual(ContentFailureCode.FabricApiIncompatible, state.UpdateMessage?.Code);
        CollectionAssert.AreEqual(currentFile.Bytes, await File.ReadAllBytesAsync(Path.Combine(modFolder, currentFile.Filename)));
    }

    [TestMethod]
    [DataRow("none", false)]
    [DataRow("accept", false)]
    [DataRow("accept", true)]
    [DataRow("decline", false)]
    [DataRow("cancel", true)]
    [DataRow("cancel-token", false)]
    [DataRow("bulk", true)]
    public async Task CompatibleFabricApiUpdateReplacesFileAndKeepsApiDescriptor(string scenario, bool disabled)
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary, loader: "0.16.9");
        using var server = new ModrinthServer();
        var currentFile = server.File("fabric-api-old.jar", FabricJar("fabric_api", "{\"fabricloader\":\">=0.16.0\"}"));
        var current = new FabricApiDescriptor(FabricClient.FabricApiProjectId, "apivers01", "1.0.0", "release",
            currentFile.Filename, currentFile.Url.AbsoluteUri, currentFile.Bytes.Length, currentFile.Sha1, currentFile.Sha512);
        var nextFile = server.File("fabric-api-new.jar", FabricJar("fabric_api_new", "{\"fabricloader\":\">=0.16.0\"}"));
        server.AddVersion(FabricClient.FabricApiProjectId, "apivers02", "2.0.0", "release", "2025-02-01T00:00:00Z",
            ModrinthContentKind.Mod, [nextFile]);
        var modFolder = Path.Combine(root, "minecraft", "mods");
        Directory.CreateDirectory(modFolder);
        var oldPath = Path.Combine(modFolder, currentFile.Filename + (disabled ? ".disabled" : ""));
        await File.WriteAllBytesAsync(oldPath, currentFile.Bytes);
        var collisionPath = Path.Combine(modFolder, nextFile.Filename);
        var foreign = Encoding.UTF8.GetBytes("foreign content");
        if (scenario != "none") await File.WriteAllBytesAsync(collisionPath, foreign);
        // A separate update proves bulk continues after the API confirmation requirement.
        var otherBytes = Encoding.UTF8.GetBytes("other old");
        var otherNext = Encoding.UTF8.GetBytes("other next");
        if (scenario == "bulk")
        {
            server.AddProject("othermod1", "Other mod", "mod");
            var old = server.AddVersion("othermod1", "otherold1", "1", "release", "2025-01-01T00:00:00Z",
                ModrinthContentKind.Mod, [server.File("other.jar", otherBytes)]);
            var next = server.AddVersion("othermod1", "othernew1", "2", "release", "2025-02-01T00:00:00Z",
                ModrinthContentKind.Mod, [server.File("other.jar", otherNext)]);
            server.Recognize(Hash(otherBytes), old);
            server.AddUpdate(Hash(otherBytes), next);
            await File.WriteAllBytesAsync(Path.Combine(modFolder, "other.jar"), otherBytes);
        }
        InstanceContent.WriteRegistry(root, new ModRegistry
        {
            ApiProvisioned = true,
            Files = new Dictionary<string, ModOrigin>(StringComparer.Ordinal)
            {
                [currentFile.Filename.ToLowerInvariant()] = ModOrigin.FromApi(current)
            }
        });
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        using var cancellation = new CancellationTokenSource();
        var confirmations = 0;
        var controller = new InstanceContentController(store, content, modrinth, fabric, (request, _) =>
        {
            confirmations++;
            Assert.AreEqual(ContentConfirmationCode.ReplaceExisting, request.Code);
            Assert.AreEqual(nextFile.Filename, request.Arguments["filename"]);
            if (scenario == "cancel-token")
            {
                cancellation.Cancel();
                return new TaskCompletionSource<ContentConfirmationChoice>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            }
            return Task.FromResult(scenario == "decline" ? ContentConfirmationChoice.Alternative
                : scenario is "cancel" or "cancel-token" ? ContentConfirmationChoice.Cancel : ContentConfirmationChoice.Primary);
        });

        await controller.CheckUpdatesAsync(instance.Id, ModrinthContentKind.Mod);
        var state = controller.GetState(instance.Id, ModrinthContentKind.Mod);
        Assert.IsTrue(state.HasUpdates, state.UpdateMessage?.ToString());
        var oldItem = state.Items.Single(item => item.Path == oldPath);
        var registryPath = Path.Combine(root, "minecraft", ".hako-mods.json");
        var registryBefore = await File.ReadAllBytesAsync(registryPath);
        if (scenario == "bulk")
        {
            var bulk = await controller.UpdateAllAsync(instance.Id, ModrinthContentKind.Mod);
            Assert.AreEqual(ContentFailureCode.ConfirmationRequired, bulk.Items.First().Failure?.Code);
            Assert.IsTrue(bulk.Items.Last().Succeeded);
            Assert.AreEqual(0, confirmations);
            CollectionAssert.AreEqual(otherNext, await File.ReadAllBytesAsync(Path.Combine(modFolder, "other.jar")));
            CollectionAssert.AreEqual(currentFile.Bytes, await File.ReadAllBytesAsync(oldPath));
            CollectionAssert.AreEqual(foreign, await File.ReadAllBytesAsync(collisionPath));
            Assert.AreEqual(current.VersionID, InstanceContent.ReadRegistry(root).Files[currentFile.Filename].Api?.VersionID);
            Assert.IsFalse(controller.GetState(instance.Id, ModrinthContentKind.Mod).IsBusy);
            return;
        }
        var updateTask = controller.UpdateAsync(instance.Id, ModrinthContentKind.Mod, oldItem, cancellation.Token);
        if (scenario == "cancel-token")
            await Assert.ThrowsAsync<OperationCanceledException>(() => updateTask);
        var result = scenario == "cancel-token" ? null : await updateTask;
        Assert.AreEqual(scenario == "none" ? 0 : 1, confirmations);
        Assert.IsFalse(controller.GetState(instance.Id, ModrinthContentKind.Mod).IsBusy);
        if (scenario is "decline" or "cancel" or "cancel-token")
        {
            if (result is not null) Assert.IsFalse(result.Succeeded);
            CollectionAssert.AreEqual(currentFile.Bytes, await File.ReadAllBytesAsync(oldPath));
            CollectionAssert.AreEqual(foreign, await File.ReadAllBytesAsync(collisionPath));
            CollectionAssert.AreEqual(registryBefore, await File.ReadAllBytesAsync(registryPath));
            return;
        }

        Assert.IsTrue(result!.Succeeded, result.Failure?.ToString());
        var updated = (await content.ListAsync(instance.Id, ModrinthContentKind.Mod)).Single();
        Assert.AreEqual(nextFile.Filename + (disabled ? ".disabled" : ""), updated.Name);
        Assert.AreEqual(!disabled, updated.Enabled);
        Assert.IsFalse(File.Exists(oldPath));
        Assert.AreEqual("0.16.9", store.List().Single().FabricConfiguration?.LoaderVersion);
        Assert.AreEqual(21, store.List().Single().JavaMajorVersion);
        Assert.AreEqual("apivers02", updated.Origin?.Api?.VersionID);
        Assert.AreEqual(nextFile.Sha512, updated.Origin?.Api?.Sha512);
        CollectionAssert.AreEqual(nextFile.Bytes, await File.ReadAllBytesAsync(updated.Path));
    }

    [TestMethod]
    public async Task BulkUpdateRequiresNoDialogsContinuesAfterFailureAndUpdatesRemainingFiles()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, _, content) = await CreateReadyInstanceAsync(temporary);
        using var server = new ModrinthServer();
        server.AddProject("firstmod", "First", "mod");
        server.AddProject("secondmod", "Second", "mod");
        var oldFirst = Encoding.UTF8.GetBytes("first-old");
        var oldSecond = Encoding.UTF8.GetBytes("second-old");
        var firstVersion = server.AddVersion("firstmod", "firstold", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("a.jar", oldFirst)]);
        var secondVersion = server.AddVersion("secondmod", "secondold", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("b.jar", oldSecond)]);
        var firstNext = server.AddVersion("firstmod", "firstnew", "2.0", "release", "2025-02-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("a.jar", [10])], [Dependency(projectId: "missingdep")]);
        var secondNext = server.AddVersion("secondmod", "secondnew", "2.0", "release", "2025-02-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("b.jar", [20])]);
        server.AddProject("missingdep", "Missing dependency", "mod");
        server.AddVersion("missingdep", "olddep01", "old", "release", "2024-12-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("olddep.jar", [30])], gameVersions: ["1.20.3"]);
        server.Recognize(Hash(oldFirst), firstVersion);
        server.Recognize(Hash(oldSecond), secondVersion);
        server.AddUpdate(Hash(oldFirst), firstNext);
        server.AddUpdate(Hash(oldSecond), secondNext);
        foreach (var (filename, bytes) in new[] { ("a.jar", oldFirst), ("b.jar", oldSecond) })
        {
            var source = Path.Combine(temporary.Root, filename);
            await File.WriteAllBytesAsync(source, bytes);
            await content.ImportAsync(instance.Id, ModrinthContentKind.Mod, source);
        }
        var dialogs = 0;
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric,
            (_, _) => { dialogs++; return Task.FromResult(ContentConfirmationChoice.Primary); });
        await controller.CheckUpdatesAsync(instance.Id, ModrinthContentKind.Mod);

        var result = await controller.UpdateAllAsync(instance.Id, ModrinthContentKind.Mod);

        Assert.AreEqual(0, dialogs);
        Assert.IsTrue(result.HasFailures, result.Items.FirstOrDefault(item => !item.Succeeded)?.Failure?.ToString());
        Assert.AreEqual(2, result.Items.Count);
        Assert.IsFalse(result.Items.Single(item => item.Filename == "a.jar").Succeeded);
        Assert.AreEqual(ContentFailureCode.ConfirmationRequired, result.Items.Single(item => item.Filename == "a.jar").Failure?.Code);
        Assert.IsTrue(result.Items.Single(item => item.Filename == "b.jar").Succeeded);
        var files = await content.ListAsync(instance.Id, ModrinthContentKind.Mod);
        CollectionAssert.AreEqual(new byte[] { 20 }, await File.ReadAllBytesAsync(files.Single(item => item.Name == "b.jar").Path));
        CollectionAssert.AreEqual(oldFirst, await File.ReadAllBytesAsync(files.Single(item => item.Name == "a.jar").Path));
    }

    [TestMethod]
    public async Task StaleUpdateResponseCannotReplaceRenamedInstanceState()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, _, content) = await CreateReadyInstanceAsync(temporary);
        using var server = new ModrinthServer();
        server.AddProject("project01", "Mod", "mod");
        var oldBytes = Encoding.UTF8.GetBytes("old");
        var old = server.AddVersion("project01", "oldver001", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("mod.jar", oldBytes)]);
        var next = server.AddVersion("project01", "newver001", "2.0", "release", "2025-02-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("mod.jar", [2])]);
        server.Recognize(Hash(oldBytes), old);
        server.AddUpdate(Hash(oldBytes), next);
        var source = Path.Combine(temporary.Root, "mod.jar");
        await File.WriteAllBytesAsync(source, oldBytes);
        await content.ImportAsync(instance.Id, ModrinthContentKind.Mod, source);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.BeforeVersionUpdate = async (hashes, token) =>
        {
            held.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric);
        await controller.ReloadAsync(instance.Id, ModrinthContentKind.Mod);
        var itemPathBeforeCheck = controller.GetState(instance.Id, ModrinthContentKind.Mod).Items.Single().Path;

        var check = controller.CheckUpdatesAsync(instance.Id, ModrinthContentKind.Mod);
        await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        store.Rename(instance.Id, "Renamed build");
        release.TrySetResult();
        await check;

        var state = controller.GetState(instance.Id, ModrinthContentKind.Mod);
        Assert.IsFalse(state.IsCheckingUpdates);
        Assert.IsFalse(state.HasUpdates);
        Assert.AreEqual(itemPathBeforeCheck, state.Items.Single().Path);
    }

    [TestMethod]
    public async Task ReloadStartedWhenUpdateCheckPublishesProjectClearsProjectFromIdleSnapshot()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary);
        using var server = new ModrinthServer();
        server.AddProject("project01", "Mod", "mod");
        var oldBytes = Encoding.UTF8.GetBytes("old");
        var old = server.AddVersion("project01", "oldver001", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("mod.jar", oldBytes)]);
        server.AddUpdate(Hash(oldBytes), server.AddVersion("project01", "newver001", "2.0", "release",
            "2025-02-01T00:00:00Z", ModrinthContentKind.Mod, [server.File("new.jar", [2])]));
        server.Recognize(Hash(oldBytes), old);
        var source = Path.Combine(temporary.Root, "mod.jar");
        await File.WriteAllBytesAsync(source, oldBytes);
        await content.ImportAsync(instance.Id, ModrinthContentKind.Mod, source);
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric);
        var reloadStarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reloadTriggered = false;
        string? observedProjectId = null;
        controller.StateChanged += (_, args) =>
        {
            if (reloadTriggered || args.Key.InstanceId != instance.Id
                || args.Key.Kind != ModrinthContentKind.Mod || args.State.CurrentProjectId is null) return;
            reloadTriggered = true;
            observedProjectId = args.State.CurrentProjectId;
            reloadStarted.TrySetResult(controller.ReloadAsync(instance.Id, ModrinthContentKind.Mod));
        };

        var check = controller.CheckUpdatesAsync(instance.Id, ModrinthContentKind.Mod);
        var reload = await reloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(check, reload);

        var state = controller.GetState(instance.Id, ModrinthContentKind.Mod);
        Assert.AreEqual("project01", observedProjectId);
        Assert.IsFalse(state.IsBusy);
        Assert.IsFalse(state.IsLoading);
        Assert.IsFalse(state.IsCheckingUpdates);
        Assert.IsNull(state.CurrentProjectId);
        Assert.AreEqual("mod.jar", state.Items.Single().Name);
        Assert.AreEqual(Path.Combine(root, "minecraft", "mods", "mod.jar"), state.Items.Single().Path);
    }

    [TestMethod]
    public async Task LateUpdateCheckCannotReplaceNewerReloadSnapshot()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, _, content) = await CreateReadyInstanceAsync(temporary);
        using var server = new ModrinthServer();
        server.AddProject("project01", "Mod", "mod");
        var oldBytes = Encoding.UTF8.GetBytes("old");
        var old = server.AddVersion("project01", "oldver001", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("mod.jar", oldBytes)]);
        var next = server.AddVersion("project01", "newver001", "2.0", "release", "2025-02-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("mod-new.jar", [2])]);
        server.Recognize(Hash(oldBytes), old);
        server.AddUpdate(Hash(oldBytes), next);
        var source = Path.Combine(temporary.Root, "mod.jar");
        await File.WriteAllBytesAsync(source, oldBytes);
        await content.ImportAsync(instance.Id, ModrinthContentKind.Mod, source);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.BeforeVersionUpdate = async (_, token) =>
        {
            held.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric);
        await controller.ReloadAsync(instance.Id, ModrinthContentKind.Mod);

        var check = controller.CheckUpdatesAsync(instance.Id, ModrinthContentKind.Mod);
        await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller.ReloadAsync(instance.Id, ModrinthContentKind.Mod);
        var reloadState = controller.GetState(instance.Id, ModrinthContentKind.Mod);
        release.TrySetResult();
        await check;

        var final = controller.GetState(instance.Id, ModrinthContentKind.Mod);
        Assert.IsFalse(final.IsCheckingUpdates);
        Assert.IsFalse(final.HasUpdates);
        Assert.AreEqual(reloadState.Items.Single().Path, final.Items.Single().Path);
        Assert.IsNull(final.Items.Single().Origin);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RegistryReadErrorDuringDeleteConfirmationDoesNotStrandBusyOrClearAnotherMutation(bool cancelToken)
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary);
        var folder = Path.Combine(root, "minecraft", "mods");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "existing.jar");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var registryPath = Path.Combine(root, "minecraft", ".hako-mods.json");
        var corrupt = Encoding.UTF8.GetBytes("{ broken registry");
        await File.WriteAllBytesAsync(registryPath, corrupt);
        using var server = new ModrinthServer();
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var choice = new TaskCompletionSource<ContentConfirmationChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var controller = new InstanceContentController(store, content, modrinth, fabric, (_, _) =>
        {
            entered.TrySetResult();
            return choice.Task;
        });
        await controller.ReloadAsync(instance.Id, ModrinthContentKind.Mod);
        var item = controller.GetState(instance.Id, ModrinthContentKind.Mod).Items.Single();
        var delete = controller.DeleteAsync(instance.Id, ModrinthContentKind.Mod, item, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var busy = controller.GetState(instance.Id, ModrinthContentKind.Mod);
        await controller.InstalledProjectsAsync(instance.Id, ModrinthContentKind.Mod);
        var afterRead = controller.GetState(instance.Id, ModrinthContentKind.Mod);
        Assert.IsTrue(afterRead.IsBusy);
        Assert.AreEqual(busy.Revision, afterRead.Revision);
        Assert.AreEqual(ContentFailureCode.RegistryCorrupt, afterRead.Error?.Code);
        if (cancelToken)
        {
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => delete);
        }
        else
        {
            choice.SetResult(ContentConfirmationChoice.Cancel);
            Assert.IsFalse((await delete).Succeeded);
        }
        Assert.IsFalse(controller.GetState(instance.Id, ModrinthContentKind.Mod).IsBusy);
        CollectionAssert.AreEqual(corrupt, await File.ReadAllBytesAsync(registryPath));

        // Repair only this isolated fixture, then verify both reads and a new mutation execute.
        InstanceContent.WriteRegistry(root, new ModRegistry());
        await controller.ReloadAsync(instance.Id, ModrinthContentKind.Mod);
        var reloaded = controller.GetState(instance.Id, ModrinthContentKind.Mod);
        Assert.IsNull(reloaded.Error);
        var result = await controller.SetEnabledAsync(instance.Id, ModrinthContentKind.Mod, reloaded.Items.Single(), false);
        Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
        Assert.IsFalse(controller.GetState(instance.Id, ModrinthContentKind.Mod).Items.Single().Enabled);
    }

    [TestMethod]
    [DataRow("cancel", false)]
    [DataRow("blocked-old", false)]
    [DataRow("registry", false)]
    [DataRow("registry", true)]
    [DataRow("success", false)]
    [DataRow("success", true)]
    [DataRow("decline", false)]
    [DataRow("recycle-failed", false)]
    [DataRow("recycle-cancelled", true)]
    [DataRow("rollback-locked", false)]
    [DataRow("rollback-locked", true)]
    [DataRow("cancel-after-commit", false)]
    public async Task UpdateCollisionPublishesReversiblyAndRecyclesOnlyAfterRegistryCommit(string scenario, bool disabled)
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, _) = await CreateReadyInstanceAsync(temporary);
        using var server = new ModrinthServer();
        server.AddProject("project01", "Updated mod", "mod");
        server.AddProject("foreign01", "Foreign mod", "mod");
        var oldBytes = Encoding.UTF8.GetBytes("original old file");
        var collisionBytes = Encoding.UTF8.GetBytes("user collision file");
        var updatedBytes = Encoding.UTF8.GetBytes("updated file");
        var oldVersion = server.AddVersion("project01", "oldver001", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("old.jar", oldBytes)]);
        var nextVersion = server.AddVersion("project01", "newver001", "2.0", "release", "2025-02-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("new.jar", updatedBytes)]);
        var foreignVersion = server.AddVersion("foreign01", "foreignv1", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("new.jar", collisionBytes)]);
        server.Recognize(Hash(oldBytes), oldVersion);
        server.Recognize(Hash(collisionBytes), foreignVersion);
        server.AddUpdate(Hash(oldBytes), nextVersion);
        var folder = Path.Combine(root, "minecraft", "mods");
        Directory.CreateDirectory(folder);
        var oldPath = Path.Combine(folder, disabled ? "old.jar.disabled" : "old.jar");
        var collisionPath = Path.Combine(folder, "new.jar");
        await File.WriteAllBytesAsync(oldPath, oldBytes);
        await File.WriteAllBytesAsync(collisionPath, collisionBytes);
        InstanceContent.WriteRegistry(root, new ModRegistry
        {
            ApiProvisioned = true,
            Files = new Dictionary<string, ModOrigin>(StringComparer.Ordinal)
            {
                ["old.jar"] = Origin("project01", "oldver001", oldBytes),
                ["new.jar"] = Origin("foreign01", "foreignv1", collisionBytes)
            }
        });
        var registryPath = Path.Combine(root, "minecraft", ".hako-mods.json");
        var registryBytes = await File.ReadAllBytesAsync(registryPath);
        using var cancellation = new CancellationTokenSource();
        var confirmed = false;
        var stagedCollision = false;
        var recycled = new List<byte[]>();
        FileStream? targetLock = null;
        var recycler = new CallbackRecycleOperation(async (path, token) =>
        {
            Assert.IsFalse(token.IsCancellationRequested);
            Assert.AreEqual("newver001", InstanceContent.ReadRegistry(root).Files["new.jar"].VersionID);
            recycled.Add(await File.ReadAllBytesAsync(path));
            if (scenario == "cancel-after-commit") cancellation.Cancel();
            if (scenario == "recycle-failed") throw new IOException("Injected recycle failure");
            if (scenario == "recycle-cancelled") throw new OperationCanceledException("Injected shell cancellation");
            File.Delete(path);
            return RecycleBinItemStatus.Recycled;
        });
        var content = new InstanceContent(store, new RecycleBin(recycler),
            beforeRegistryCommit: () =>
            {
                if (confirmed && scenario == "registry") throw new IOException("Injected registry failure");
                if (confirmed && scenario == "rollback-locked")
                {
                    targetLock = new FileStream(Path.Combine(folder, disabled ? "new.jar.disabled" : "new.jar"),
                        FileMode.Open, FileAccess.Read, FileShare.Read);
                    throw new IOException("Injected registry failure with locked publication");
                }
            },
            beforeReplacementPublish: () =>
            {
                Assert.IsTrue(confirmed);
                Assert.IsFalse(File.Exists(collisionPath));
                Assert.IsTrue(File.Exists(oldPath));
                stagedCollision = true;
                if (scenario == "cancel") cancellation.Cancel();
            });
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        FileStream? oldLock = null;
        var controller = new InstanceContentController(store, content, modrinth, fabric, (request, _) =>
        {
            Assert.AreEqual(ContentConfirmationCode.ReplaceExisting, request.Code);
            confirmed = true;
            if (scenario == "blocked-old")
                oldLock = new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.FromResult(scenario == "decline" ? ContentConfirmationChoice.Cancel : ContentConfirmationChoice.Primary);
        });
        await controller.CheckUpdatesAsync(instance.Id, ModrinthContentKind.Mod);
        var oldItem = controller.GetState(instance.Id, ModrinthContentKind.Mod).Items.Single(item => item.Path == oldPath);
        Assert.IsTrue(controller.GetState(instance.Id, ModrinthContentKind.Mod).HasUpdates);
        registryBytes = await File.ReadAllBytesAsync(registryPath);
        ContentActionResult? result = null;
        try
        {
            var task = controller.UpdateAsync(instance.Id, ModrinthContentKind.Mod, oldItem, cancellation.Token);
            if (scenario == "cancel") await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => task);
            else result = await task;
        }
        finally
        {
            oldLock?.Dispose();
            targetLock?.Dispose();
        }

        Assert.IsTrue(confirmed);
        Assert.AreEqual(scenario != "decline", stagedCollision);
        Assert.IsFalse(controller.GetState(instance.Id, ModrinthContentKind.Mod).IsBusy);
        if (scenario is "success" or "recycle-failed" or "recycle-cancelled" or "cancel-after-commit")
        {
            Assert.AreEqual(scenario is "success" or "cancel-after-commit", result!.Succeeded, result.Failure?.ToString());
            Assert.IsFalse(File.Exists(oldPath));
            var target = Path.Combine(folder, disabled ? "new.jar.disabled" : "new.jar");
            CollectionAssert.AreEqual(updatedBytes, await File.ReadAllBytesAsync(target));
            var registry = InstanceContent.ReadRegistry(root);
            Assert.IsTrue(registry.ApiProvisioned);
            Assert.IsFalse(registry.Files.ContainsKey("old.jar"));
            Assert.AreEqual("newver001", registry.Files["new.jar"].VersionID);
            CollectionAssert.AreEqual(collisionBytes, recycled.Single());
            var backups = Directory.GetFiles(folder, "*.hako-backup-*");
            if (scenario is "success" or "cancel-after-commit") Assert.AreEqual(0, backups.Length);
            else
            {
                CollectionAssert.AreEqual(collisionBytes, await File.ReadAllBytesAsync(backups.Single()));
                StringAssert.Contains(result.Failure!.Arguments["diagnostic"], backups.Single());
            }
        }
        else
        {
            if (result is not null) Assert.IsFalse(result.Succeeded);
            CollectionAssert.AreEqual(oldBytes, await File.ReadAllBytesAsync(oldPath));
            var backups = Directory.GetFiles(folder, "*.hako-backup-*");
            if (scenario == "rollback-locked")
            {
                StringAssert.Contains(result!.Failure!.Arguments["diagnostic"], "Could not remove published entry");
                CollectionAssert.AreEqual(updatedBytes,
                    await File.ReadAllBytesAsync(Path.Combine(folder, disabled ? "new.jar.disabled" : "new.jar")));
                if (!disabled)
                {
                    CollectionAssert.AreEqual(collisionBytes, await File.ReadAllBytesAsync(backups.Single()));
                    StringAssert.Contains(result.Failure.Arguments["diagnostic"], backups.Single());
                }
            }
            if (scenario != "rollback-locked" || disabled)
                CollectionAssert.AreEqual(collisionBytes, await File.ReadAllBytesAsync(collisionPath));
            CollectionAssert.AreEqual(registryBytes, await File.ReadAllBytesAsync(registryPath));
            Assert.AreEqual("oldver001", InstanceContent.ReadRegistry(root).Files["old.jar"].VersionID);
            Assert.AreEqual("foreignv1", InstanceContent.ReadRegistry(root).Files["new.jar"].VersionID);
            Assert.AreEqual(0, recycled.Count);
            Assert.AreEqual(scenario == "rollback-locked" && !disabled ? 1 : 0, backups.Length);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReadInitialEventCanStartMutationWithoutPublishingStaleRead(bool checkUpdates)
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary);
        using var server = new ModrinthServer();
        var path = Path.Combine(root, "minecraft", "mods", "local.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "local mod");
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric);
        await controller.ReloadAsync(instance.Id, ModrinthContentKind.Mod);
        var item = controller.GetState(instance.Id, ModrinthContentKind.Mod).Items.Single();
        Task<ContentActionResult>? mutation = null;
        InstanceContentState? initial = null;
        controller.StateChanged += (_, args) =>
        {
            if (initial is not null) return;
            initial = args.State;
            mutation = controller.SetEnabledAsync(instance.Id, ModrinthContentKind.Mod, item, false);
        };
        if (checkUpdates) await controller.CheckUpdatesAsync(instance.Id, ModrinthContentKind.Mod);
        else await controller.ReloadAsync(instance.Id, ModrinthContentKind.Mod);
        var mutationResult = await mutation!.WaitAsync(TimeSpan.FromSeconds(5));
        var failure = mutationResult.Failure is { } contentFailure
            ? $"{contentFailure.Code}: {string.Join(", ", contentFailure.Arguments.Select(argument => $"{argument.Key}={argument.Value}"))}"
            : "Mutation did not succeed.";
        Assert.IsTrue(mutationResult.Succeeded, failure);
        Assert.AreEqual(checkUpdates, initial!.IsCheckingUpdates);
        Assert.AreEqual(!checkUpdates, initial.IsLoading);
        var after = controller.GetState(instance.Id, ModrinthContentKind.Mod);
        Assert.IsTrue(after.Revision > initial.Revision);
        Assert.IsFalse(after.IsBusy);
        Assert.IsFalse(after.IsLoading);
        Assert.IsFalse(after.IsCheckingUpdates);
        Assert.IsFalse(after.Items.Single().Enabled);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task MutationStartRejectsReentrantAndConcurrentReads(bool checkUpdates, bool concurrent)
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary);
        using var server = new ModrinthServer();
        var path = Path.Combine(root, "minecraft", "mods", "local.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "local mod");
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric);
        await controller.ReloadAsync(instance.Id, ModrinthContentKind.Mod);
        var before = controller.GetState(instance.Id, ModrinthContentKind.Mod);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        Task? read = null;
        InstanceContentState? initial = null;
        controller.StateChanged += (_, args) =>
        {
            if (args.State.Revision <= before.Revision || initial is not null) return;
            initial = args.State;
            if (concurrent)
            {
                entered.SetResult();
                release.Wait(TimeSpan.FromSeconds(5));
            }
            else read = checkUpdates
                ? controller.CheckUpdatesAsync(instance.Id, ModrinthContentKind.Mod)
                : controller.ReloadAsync(instance.Id, ModrinthContentKind.Mod);
        };
        var mutation = Task.Run(() => controller.SetEnabledAsync(instance.Id, ModrinthContentKind.Mod, before.Items.Single(), false));
        if (concurrent)
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                read = checkUpdates ? controller.CheckUpdatesAsync(instance.Id, ModrinthContentKind.Mod)
                    : controller.ReloadAsync(instance.Id, ModrinthContentKind.Mod);
                await read.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { release.Set(); }
        }
        Assert.IsTrue((await mutation.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
        await read!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(initial!.IsBusy);
        var after = controller.GetState(instance.Id, ModrinthContentKind.Mod);
        Assert.AreEqual(initial.Revision, after.Revision);
        Assert.IsFalse(after.IsBusy);
        Assert.IsFalse(after.IsLoading);
        Assert.IsFalse(after.IsCheckingUpdates);
        Assert.IsFalse(after.Items.Single().Enabled);
        Assert.IsTrue(after.Items.Single().Path.EndsWith(".disabled", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task WorldDatapackControllerScopesStateAndRejectsWorldRemovedDuringDownload()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary);
        var worldA = CreateWorldFolder(root, "WorldA");
        var worldB = CreateWorldFolder(root, "WorldB");
        using var server = new ModrinthServer();
        server.AddProject("datapack1", "World pack", "datapack");
        var bytes = DatapackZip("world A");
        var file = server.File("world.zip", bytes);
        server.AddVersion("datapack1", "worldver1", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Datapack, [file]);
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric);
        var changedKeys = new HashSet<InstanceContentStateKey>();
        controller.StateChanged += (_, args) => changedKeys.Add(args.Key);
        var targetA = InstanceContentTarget.WorldDatapacks("WorldA");
        var targetB = InstanceContentTarget.WorldDatapacks("WorldB");

        await controller.ReloadAsync(instance.Id, targetA);
        await controller.ReloadAsync(instance.Id, targetB);
        var revisionB = controller.GetState(instance.Id, targetB).Revision;
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.DownloadStarted = downloadStarted;
        server.OnDownload = async token => await releaseDownload.Task.WaitAsync(token);
        var installA = controller.InstallAsync(instance.Id, targetA, server.Project("datapack1"));
        await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(controller.GetState(instance.Id, targetA).IsBusy);
        Assert.IsFalse(controller.GetState(instance.Id, targetB).IsBusy);
        Assert.AreEqual(revisionB, controller.GetState(instance.Id, targetB).Revision);
        releaseDownload.SetResult();
        Assert.IsTrue((await installA).Succeeded);
        server.OnDownload = null;
        server.DownloadStarted = null;

        Assert.AreEqual(1, controller.GetState(instance.Id, targetA).Items.Count);
        Assert.AreEqual(0, controller.GetState(instance.Id, targetB).Items.Count);
        Assert.IsTrue(changedKeys.Contains(new InstanceContentStateKey(instance.Id, ModrinthContentKind.Datapack, "WorldA")));
        Assert.IsTrue(changedKeys.Contains(new InstanceContentStateKey(instance.Id, ModrinthContentKind.Datapack, "WorldB")));
        Assert.IsTrue(File.Exists(Path.Combine(worldA, "datapacks", "world.zip")));
        Assert.IsFalse(Directory.Exists(Path.Combine(worldB, "datapacks")));

        var disappearingWorld = CreateWorldFolder(root, "Disappearing");
        server.AddProject("gonepack1", "Disappearing pack", "datapack");
        server.AddVersion("gonepack1", "gonever01", "1.0", "release", "2025-01-02T00:00:00Z",
            ModrinthContentKind.Datapack, [server.File("gone.zip", DatapackZip("gone"))]);
        server.OnDownload = _ =>
        {
            Directory.Delete(disappearingWorld, recursive: true);
            return Task.CompletedTask;
        };

        var failed = await controller.InstallAsync(instance.Id, InstanceContentTarget.WorldDatapacks("Disappearing"),
            server.Project("gonepack1"));
        Assert.IsFalse(failed.Succeeded);
        Assert.AreEqual(ContentFailureCode.WorldUnavailable, failed.Failure?.Code);
        Assert.IsFalse(Directory.Exists(disappearingWorld));
    }

    [TestMethod]
    public async Task DatapackDependenciesPreferDatapackVersionsAndSelectionRoutesOnlyChosenPack()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary,
            modLoader: InstanceModLoader.Vanilla);
        var world = CreateWorldFolder(root, "Selected");
        var target = InstanceContentTarget.WorldDatapacks("Selected");
        using var server = new ModrinthServer();
        server.ObserveContentDirectories(Path.Combine(world, "datapacks"), Path.Combine(root, "minecraft", "resourcepacks"));
        server.AddProject("rootpack", "Root pack", "datapack");
        server.AddProject("dependency", "Multi-type dependency", "mod", allProjectTypes: ["mod", "datapack"]);
        var rootFile = server.File("root.zip", DatapackZip("root"));
        server.AddVersion("rootpack", "rootver01", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Datapack, [rootFile], [Dependency(projectId: "dependency")]);
        var dependencyFile = server.File("dependency.zip", DatapackZip("dependency"));
        var requiredPack = server.File("required.zip", DatapackZip("required"), "required-resource-pack", primary: false);
        var optionalPack = server.File("optional.zip", DatapackZip("optional"), "optional-resource-pack", primary: false);
        server.AddVersion("dependency", "depver001", "2.0", "release", "2025-01-02T00:00:00Z",
            ModrinthContentKind.Datapack, [dependencyFile, requiredPack, optionalPack]);
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var requests = new List<ContentConfirmationRequest>();
        var controller = new InstanceContentController(store, content, modrinth, fabric,
            confirmSelection: (request, _) =>
            {
                requests.Add(request);
                var result = request.Code == ContentConfirmationCode.DatapackResourcePacks
                    ? new ContentConfirmationResult(ContentConfirmationChoice.Primary, "optional.zip")
                    : new ContentConfirmationResult(ContentConfirmationChoice.Primary);
                return Task.FromResult(result);
            });

        var result = await controller.InstallAsync(instance.Id, target, server.Project("rootpack"));

        Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
        var resourceSelection = requests.Single(request => request.Code == ContentConfirmationCode.DatapackResourcePacks);
        Assert.AreEqual(target, resourceSelection.Target);
        var selectedFiles = resourceSelection.Files!;
        CollectionAssert.AreEqual(new[] { "required.zip", "optional.zip" }, selectedFiles.Select(file => file.Filename).ToArray());
        Assert.IsTrue(selectedFiles[0].Required);
        Assert.IsFalse(selectedFiles[1].Required);
        Assert.IsTrue(server.AllDownloadsSawEmptyContentDirectories);
        Assert.IsTrue(File.Exists(Path.Combine(world, "datapacks", "root.zip")));
        Assert.IsTrue(File.Exists(Path.Combine(world, "datapacks", "dependency.zip")));
        Assert.AreEqual("dependency", (await content.ListAsync(instance.Id, target))
            .Single(item => item.Name == "dependency.zip").Origin?.ProjectID);
        Assert.IsTrue(File.Exists(Path.Combine(root, "minecraft", "resourcepacks", "optional.zip")));
        var selectedPackOrigin = (await content.ListAsync(instance.Id, InstanceContentTarget.Packs)).Single().Origin;
        Assert.AreEqual("dependency", selectedPackOrigin?.ProjectID);
        Assert.AreEqual("depver001", selectedPackOrigin?.VersionID);
        Assert.AreEqual("optional-resource-pack", selectedPackOrigin?.FileType);
        Assert.AreEqual(optionalPack.Sha512, selectedPackOrigin?.Sha512);
        Assert.IsFalse(File.Exists(Path.Combine(root, "minecraft", "resourcepacks", "required.zip")));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "minecraft", "mods"))
            && Directory.EnumerateFileSystemEntries(Path.Combine(root, "minecraft", "mods")).Any());
        Assert.IsTrue(File.Exists(Path.Combine(world, "level.dat")));

        var alternateWorld = CreateWorldFolder(root, "WithoutPack");
        var alternateController = new InstanceContentController(store, content, modrinth, fabric,
            confirmSelection: (request, _) => Task.FromResult(new ContentConfirmationResult(
                request.Code == ContentConfirmationCode.DatapackResourcePacks
                    ? ContentConfirmationChoice.Alternative : ContentConfirmationChoice.Primary)));
        var alternate = await alternateController.InstallAsync(instance.Id,
            InstanceContentTarget.WorldDatapacks("WithoutPack"), server.Project("rootpack"));
        Assert.IsTrue(alternate.Succeeded, alternate.Failure?.ToString());
        Assert.IsTrue(File.Exists(Path.Combine(alternateWorld, "datapacks", "dependency.zip")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "minecraft", "resourcepacks", "required.zip")));

        var cancelledWorld = CreateWorldFolder(root, "Cancelled");
        var cancelledController = new InstanceContentController(store, content, modrinth, fabric,
            confirmSelection: (request, _) => Task.FromResult(new ContentConfirmationResult(
                request.Code == ContentConfirmationCode.DatapackResourcePacks
                    ? ContentConfirmationChoice.Cancel : ContentConfirmationChoice.Primary)));
        var cancelled = await cancelledController.InstallAsync(instance.Id,
            InstanceContentTarget.WorldDatapacks("Cancelled"), server.Project("rootpack"));
        Assert.IsFalse(cancelled.Succeeded);
        Assert.IsFalse(Directory.Exists(Path.Combine(cancelledWorld, "datapacks")));

        var defaultWorld = CreateWorldFolder(root, "LegacyChoice");
        var legacyController = new InstanceContentController(store, content, modrinth, fabric,
            (_, _) => Task.FromResult(ContentConfirmationChoice.Primary));
        var legacyChoice = await legacyController.InstallAsync(instance.Id,
            InstanceContentTarget.WorldDatapacks("LegacyChoice"), server.Project("rootpack"));
        Assert.IsTrue(legacyChoice.Succeeded, legacyChoice.Failure?.ToString());
        Assert.IsTrue(File.Exists(Path.Combine(root, "minecraft", "resourcepacks", "required.zip")));
        Assert.IsFalse(File.Exists(Path.Combine(defaultWorld, "datapacks", "required.zip")));
    }

    [TestMethod]
    public async Task VanillaWorldDatapackInstallExplainsFabricOnlyDependency()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary,
            modLoader: InstanceModLoader.Vanilla);
        CreateWorldFolder(root, "World");
        using var server = new ModrinthServer();
        server.AddProject("rootpack", "Root pack", "datapack");
        server.AddProject("moddep01", "Fabric-only dependency", "mod", allProjectTypes: ["mod"]);
        server.AddVersion("rootpack", "rootver01", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Datapack, [server.File("root.zip", DatapackZip("root"))],
            [Dependency(projectId: "moddep01")]);
        server.AddVersion("moddep01", "modver001", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Mod, [server.File("dependency.jar", FabricJar("dependency", "{}"))]);
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        ContentConfirmationRequest? dependencyRequest = null;
        var controller = new InstanceContentController(store, content, modrinth, fabric,
            (request, _) =>
            {
                if (request.Code == ContentConfirmationCode.Dependencies) dependencyRequest = request;
                return Task.FromResult(ContentConfirmationChoice.Primary);
            });

        var result = await controller.InstallAsync(instance.Id, InstanceContentTarget.WorldDatapacks("World"),
            server.Project("rootpack"));

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(dependencyRequest!.Projects.Any(project => project.Id == "moddep01"
            && project.NoteCode == ContentConfirmationNoteCode.RequiresFabric));
        Assert.IsFalse(File.Exists(Path.Combine(root, "minecraft", "saves", "World", "datapacks", "root.zip")));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "minecraft", "mods")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DatapackLinkedResourcePackReusesMatchingHashAndEnablesDisabledPack(bool storedOrigin)
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary);
        var firstWorld = CreateWorldFolder(root, "ReusePack");
        var secondWorld = CreateWorldFolder(root, "EnablePack");
        var linkedPackBytes = DatapackZip("linked resource pack");
        var localPackPath = Path.Combine(temporary.Root, "local-resource-pack.zip");
        await File.WriteAllBytesAsync(localPackPath, linkedPackBytes);
        await content.ImportAsync(instance.Id, InstanceContentTarget.Packs, localPackPath);
        if (storedOrigin)
        {
            var item = (await content.ListAsync(instance.Id, InstanceContentTarget.Packs)).Single();
            await content.WithContentLeaseAsync(instance.Id, async (leasedRoot, leasedInstance, token) =>
                await content.RecordOriginUnderLeaseAsync(leasedRoot, leasedInstance, item, InstanceContentTarget.Packs,
                    Hash(linkedPackBytes), Origin("unrelated", "oldversion", linkedPackBytes) with
                    {
                        FileType = "optional-resource-pack",
                        Api = new FabricApiDescriptor("unrelated", "oldversion", "old", "release",
                            "local-resource-pack.zip", "https://example.test/pack.zip", linkedPackBytes.Length,
                            Convert.ToHexString(SHA1.HashData(linkedPackBytes)).ToLowerInvariant(), Hash(linkedPackBytes)),
                        IconURL = new Uri("https://example.test/icon.png")
                    }, token));
        }
        var originalOrigin = (await content.ListAsync(instance.Id, InstanceContentTarget.Packs)).Single().Origin;
        var registryPath = Path.Combine(root, "minecraft", ".hako-resourcepacks.json");
        var registryBytes = await File.ReadAllBytesAsync(registryPath);

        using var server = new ModrinthServer();
        server.AddProject("rootpack", "Root pack", "datapack");
        var attached = server.File("linked.zip", linkedPackBytes, "required-resource-pack", primary: false);
        server.AddVersion("rootpack", "rootver01", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Datapack, [server.File("root.zip", DatapackZip("root")), attached]);
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var cancelSelection = true;
        var controller = new InstanceContentController(store, content, modrinth, fabric,
            confirmSelection: async (request, _) =>
            {
                CollectionAssert.AreEqual(registryBytes, await File.ReadAllBytesAsync(registryPath));
                return new ContentConfirmationResult(cancelSelection ? ContentConfirmationChoice.Cancel
                    : ContentConfirmationChoice.Primary, request.Code == ContentConfirmationCode.DatapackResourcePacks
                    ? "linked.zip" : null);
            });

        var cancelled = await controller.InstallAsync(instance.Id, InstanceContentTarget.WorldDatapacks("ReusePack"),
            server.Project("rootpack"));
        Assert.IsFalse(cancelled.Succeeded);
        Assert.AreEqual(0, server.DownloadRequests);
        CollectionAssert.AreEqual(registryBytes, await File.ReadAllBytesAsync(registryPath));
        Assert.IsFalse(Directory.Exists(Path.Combine(firstWorld, "datapacks")));
        cancelSelection = false;

        var first = await controller.InstallAsync(instance.Id, InstanceContentTarget.WorldDatapacks("ReusePack"),
            server.Project("rootpack"));
        Assert.IsTrue(first.Succeeded, first.Failure?.ToString());
        Assert.AreEqual(1, server.DownloadRequests);
        var installedPack = (await content.ListAsync(instance.Id, InstanceContentTarget.Packs)).Single();
        Assert.IsTrue(installedPack.Enabled);
        Assert.AreEqual(originalOrigin, installedPack.Origin);
        CollectionAssert.AreEqual(registryBytes, await File.ReadAllBytesAsync(registryPath));

        await content.SetEnabledAsync(instance.Id, installedPack, InstanceContentTarget.Packs, enabled: false);
        registryBytes = await File.ReadAllBytesAsync(registryPath);
        cancelSelection = true;
        var cancelledDisabled = await controller.InstallAsync(instance.Id, InstanceContentTarget.WorldDatapacks("EnablePack"),
            server.Project("rootpack"));
        Assert.IsFalse(cancelledDisabled.Succeeded);
        Assert.IsFalse((await content.ListAsync(instance.Id, InstanceContentTarget.Packs)).Single().Enabled);
        CollectionAssert.AreEqual(registryBytes, await File.ReadAllBytesAsync(registryPath));
        cancelSelection = false;
        server.OnDownload = async _ =>
        {
            Assert.IsFalse((await content.ListUnderLeaseAsync(root, instance, InstanceContentTarget.Packs, true,
                CancellationToken.None)).Single().Enabled);
            throw new IOException("Injected download failure.");
        };
        var failed = await controller.InstallAsync(instance.Id, InstanceContentTarget.WorldDatapacks("EnablePack"),
            server.Project("rootpack"));
        Assert.IsFalse(failed.Succeeded);
        Assert.IsFalse((await content.ListAsync(instance.Id, InstanceContentTarget.Packs)).Single().Enabled);
        CollectionAssert.AreEqual(registryBytes, await File.ReadAllBytesAsync(registryPath));
        server.OnDownload = async _ => Assert.IsFalse((await content.ListUnderLeaseAsync(root, instance,
            InstanceContentTarget.Packs, true, CancellationToken.None)).Single().Enabled);
        var second = await controller.InstallAsync(instance.Id, InstanceContentTarget.WorldDatapacks("EnablePack"),
            server.Project("rootpack"));
        Assert.IsTrue(second.Succeeded, second.Failure?.ToString());
        var reenabledPack = (await content.ListAsync(instance.Id, InstanceContentTarget.Packs)).Single();
        Assert.IsTrue(reenabledPack.Enabled);
        Assert.AreEqual(originalOrigin, reenabledPack.Origin);
        CollectionAssert.AreEqual(registryBytes, await File.ReadAllBytesAsync(registryPath));
        Assert.IsTrue(File.Exists(Path.Combine(firstWorld, "datapacks", "root.zip")));
        Assert.IsTrue(File.Exists(Path.Combine(secondWorld, "datapacks", "root.zip")));
        var disabledPacksFolder = Path.Combine(root, "minecraft", ".hako-disabled-resourcepacks");
        Assert.IsFalse(Directory.Exists(disabledPacksFolder) && Directory.EnumerateFileSystemEntries(disabledPacksFolder).Any());
    }

    [TestMethod]
    public async Task IncompatibleDatapackInAnotherWorldDoesNotBlockSelectedWorldInstall()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary,
            modLoader: InstanceModLoader.Vanilla);
        var selectedWorld = CreateWorldFolder(root, "Selected");
        CreateWorldFolder(root, "Other");
        var otherTarget = InstanceContentTarget.WorldDatapacks("Other");
        var otherSource = Path.Combine(temporary.Root, "conflict.zip");
        var conflictBytes = DatapackZip("other world conflict");
        await File.WriteAllBytesAsync(otherSource, conflictBytes);
        await content.ImportAsync(instance.Id, otherTarget, otherSource);
        var otherItem = (await content.ListAsync(instance.Id, otherTarget)).Single();
        await content.WithContentLeaseAsync(instance.Id, async (leasedRoot, leasedInstance, token) =>
        {
            await content.RecordOriginUnderLeaseAsync(leasedRoot, leasedInstance, otherItem, otherTarget,
                Hash(conflictBytes), Origin("conflict01", "conflictver", conflictBytes), token);
        });
        using var server = new ModrinthServer();
        server.AddProject("conflict01", "Other-world datapack", "datapack");
        server.AddProject("install01", "Selected-world datapack", "datapack");
        server.AddVersion("install01", "installver", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Datapack, [server.File("install.zip", DatapackZip("selected"))],
            [Dependency(projectId: "conflict01", type: "incompatible")]);
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var confirmations = new List<ContentConfirmationCode>();
        var controller = new InstanceContentController(store, content, modrinth, fabric,
            (request, _) =>
            {
                confirmations.Add(request.Code);
                return Task.FromResult(ContentConfirmationChoice.Primary);
            });

        var result = await controller.InstallAsync(instance.Id, InstanceContentTarget.WorldDatapacks("Selected"),
            server.Project("install01"));

        Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
        Assert.IsFalse(confirmations.Contains(ContentConfirmationCode.IncompatibleContent));
        Assert.IsTrue(File.Exists(Path.Combine(selectedWorld, "datapacks", "install.zip")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "minecraft", "saves", "Other", "datapacks", "conflict.zip")));
    }

    [TestMethod]
    public async Task CorruptWorldDatapackRegistryFallsBackForListingAndBlocksMutation()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary);
        var world = CreateWorldFolder(root, "Corrupt");
        var datapacks = Path.Combine(world, "datapacks");
        Directory.CreateDirectory(datapacks);
        var bytes = DatapackZip("visible with corrupt registry");
        var path = Path.Combine(datapacks, "pack.zip");
        await File.WriteAllBytesAsync(path, bytes);
        var registryPath = Path.Combine(world, ".hako-datapacks.json");
        const string corruptRegistry = "{broken";
        await File.WriteAllTextAsync(registryPath, corruptRegistry);
        using var server = new ModrinthServer();
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric);
        var target = InstanceContentTarget.WorldDatapacks("Corrupt");

        await controller.ReloadAsync(instance.Id, target);
        var state = controller.GetState(instance.Id, target);
        Assert.AreEqual(ContentFailureCode.RegistryCorrupt, state.Error?.Code);
        Assert.AreEqual(1, state.Items.Count);
        var mutation = await controller.SetEnabledAsync(instance.Id, target, state.Items.Single(), enabled: false);

        Assert.IsFalse(mutation.Succeeded);
        Assert.AreEqual(ContentFailureCode.RegistryCorrupt, mutation.Failure?.Code);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(corruptRegistry, await File.ReadAllTextAsync(registryPath));
    }

    [TestMethod]
    public async Task LateUpdateCheckForRemovedWorldPublishesWorldUnavailableAndClearsStaleItems()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary);
        var world = CreateWorldFolder(root, "LateRemoval");
        var target = InstanceContentTarget.WorldDatapacks("LateRemoval");
        var oldBytes = DatapackZip("old");
        var oldSource = Path.Combine(temporary.Root, "pack.zip");
        await File.WriteAllBytesAsync(oldSource, oldBytes);
        await content.ImportAsync(instance.Id, target, oldSource);
        using var server = new ModrinthServer();
        server.AddProject("project01", "Pack", "datapack");
        var oldVersion = server.AddVersion("project01", "oldver01", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Datapack, [server.File("pack.zip", oldBytes)]);
        var next = server.AddVersion("project01", "newver01", "2.0", "release", "2025-02-01T00:00:00Z",
            ModrinthContentKind.Datapack, [server.File("pack.zip", DatapackZip("new"))]);
        server.Recognize(Hash(oldBytes), oldVersion);
        server.AddUpdate(Hash(oldBytes), next);
        server.BeforeVersionUpdate = (_, _) =>
        {
            Directory.Delete(world, recursive: true);
            return Task.CompletedTask;
        };
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var controller = new InstanceContentController(store, content, modrinth, fabric);

        await controller.CheckUpdatesAsync(instance.Id, target);

        var state = controller.GetState(instance.Id, target);
        Assert.AreEqual(ContentFailureCode.WorldUnavailable, state.Error?.Code);
        Assert.AreEqual(ContentFailureCode.WorldUnavailable, state.UpdateMessage?.Code);
        Assert.AreEqual(0, state.Items.Count);
        Assert.AreEqual(0, state.Updates.Count);
        Assert.IsFalse(state.IsCheckingUpdates);
        Assert.IsFalse(Directory.Exists(world));
    }

    [TestMethod]
    public async Task DatapackUpdateAllReportsConfirmationRequiredWithoutDialogAndContinuesOtherItems()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root, content) = await CreateReadyInstanceAsync(temporary);
        var world = CreateWorldFolder(root, "Updates");
        var target = InstanceContentTarget.WorldDatapacks("Updates");
        using var server = new ModrinthServer();
        var oldABytes = DatapackZip("old A");
        var oldBBytes = DatapackZip("old B");
        var newABytes = DatapackZip("new A");
        var newBBytes = DatapackZip("new B");
        var oldAFile = server.File("a.zip", oldABytes);
        var oldBFile = server.File("b.zip", oldBBytes);
        server.AddProject("firstpack", "First pack", "datapack");
        server.AddProject("secondpack", "Second pack", "datapack");
        var oldA = server.AddVersion("firstpack", "firstold1", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Datapack, [oldAFile]);
        var oldB = server.AddVersion("secondpack", "secondold", "1.0", "release", "2025-01-01T00:00:00Z",
            ModrinthContentKind.Datapack, [oldBFile]);
        var nextA = server.AddVersion("firstpack", "firstnew1", "2.0", "release", "2025-02-01T00:00:00Z",
            ModrinthContentKind.Datapack,
            [server.File("a.zip", newABytes), server.File("attached.zip", DatapackZip("attached"), "required-resource-pack", primary: false)]);
        var nextB = server.AddVersion("secondpack", "secondnew", "2.0", "release", "2025-02-01T00:00:00Z",
            ModrinthContentKind.Datapack, [server.File("b.zip", newBBytes)]);
        server.Recognize(Hash(oldABytes), oldA);
        server.Recognize(Hash(oldBBytes), oldB);
        server.AddUpdate(Hash(oldABytes), nextA);
        server.AddUpdate(Hash(oldBBytes), nextB);
        await File.WriteAllBytesAsync(Path.Combine(temporary.Root, "a.zip"), oldABytes);
        await File.WriteAllBytesAsync(Path.Combine(temporary.Root, "b.zip"), oldBBytes);
        await content.ImportAsync(instance.Id, target, Path.Combine(temporary.Root, "a.zip"));
        await content.ImportAsync(instance.Id, target, Path.Combine(temporary.Root, "b.zip"));
        var itemB = (await content.ListAsync(instance.Id, target)).Single(item => item.Name == "b.zip");
        await content.SetEnabledAsync(instance.Id, itemB, target, enabled: false);
        var levelBefore = await File.ReadAllBytesAsync(Path.Combine(world, "level.dat"));
        using var modrinth = server.CreateModrinth(temporary.Paths);
        using var fabric = server.CreateFabric(temporary.Paths);
        var dialogCount = 0;
        var controller = new InstanceContentController(store, content, modrinth, fabric,
            (_, _) =>
            {
                dialogCount++;
                return Task.FromResult(ContentConfirmationChoice.Primary);
            });

        await controller.CheckUpdatesAsync(instance.Id, target);
        Assert.AreEqual(2, controller.GetState(instance.Id, target).Updates.Count);
        Assert.AreEqual("firstold1", controller.GetState(instance.Id, target).Items
            .Single(item => item.Name == "a.zip").Origin?.VersionID);
        var result = await controller.UpdateAllAsync(instance.Id, target);

        Assert.AreEqual(2, result.Items.Count);
        Assert.IsFalse(result.Items.Single(item => item.Filename == "a.zip").Succeeded);
        Assert.AreEqual(ContentFailureCode.ConfirmationRequired, result.Items.Single(item => item.Filename == "a.zip").Failure?.Code);
        Assert.IsTrue(result.Items.Single(item => item.Filename == "b.zip").Succeeded);
        Assert.AreEqual(0, dialogCount);
        CollectionAssert.AreEqual(oldABytes, await File.ReadAllBytesAsync(Path.Combine(world, "datapacks", "a.zip")));
        var updatedB = (await content.ListAsync(instance.Id, target)).Single(item => item.Name == "b.zip");
        Assert.IsTrue(updatedB.DisabledPack);
        Assert.AreEqual("secondnew", updatedB.Origin?.VersionID);
        CollectionAssert.AreEqual(newBBytes, await File.ReadAllBytesAsync(updatedB.Path));
        CollectionAssert.AreEqual(levelBefore, await File.ReadAllBytesAsync(Path.Combine(world, "level.dat")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "minecraft", "resourcepacks", "attached.zip")));
    }

    private static string CreateWorldFolder(string root, string worldId)
    {
        var path = Path.Combine(root, "minecraft", "saves", worldId);
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "level.dat"), WorldTestFixture.Create(worldId).Compressed);
        return path;
    }

    private static byte[] DatapackZip(string description)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("pack.mcmeta").Open(), new UTF8Encoding(false)))
                writer.Write("{\"pack\":{\"pack_format\":15,\"description\":\"" + description + "\"}}");
            using (var writer = new StreamWriter(archive.CreateEntry("data/fixture.txt").Open(), new UTF8Encoding(false)))
                writer.Write(description);
        }

        return memory.ToArray();
    }

    private sealed class CallbackRecycleOperation(Func<string, CancellationToken, Task<RecycleBinItemStatus>> action)
        : IRecycleBinOperation
    {
        public Task<RecycleBinItemStatus> DeleteAsync(string path, CancellationToken cancellationToken) => action(path, cancellationToken);
    }

    private static async Task<(InstanceStore Store, GameInstance Instance, string Root, InstanceContent Content)> CreateReadyInstanceAsync(
        TemporaryDirectory temporary, string name = "Build", string loader = "0.16.9", int java = 21,
        InstanceModLoader modLoader = InstanceModLoader.Fabric)
    {
        var store = temporary.CreateStore();
        await store.LoadAsync();
        var draft = new InstanceDraft
        {
            Name = name,
            ModLoader = modLoader,
            JavaMajorVersion = java,
            FabricConfiguration = modLoader == InstanceModLoader.Fabric ? new FabricConfiguration(loader) : null
        };
        var instance = store.Create(draft, "1.20.4", javaMajorVersion: java);
        store.SetInstallationStatus(instance.Id, InstanceInstallationState.Ready, pauseRequested: false);
        instance = store.List().Single(candidate => candidate.Id == instance.Id);
        return (store, instance, store.GetInstanceDirectory(instance.Id),
            new InstanceContent(store, new RecycleBin(new TestRecycleOperation())));
    }

    private static ModrinthDependency Dependency(string? projectId = null, string? versionId = null, string type = "required") =>
        new() { ProjectId = projectId, VersionId = versionId, Type = type };

    private static ModOrigin Origin(string projectId, string versionId, byte[] bytes) => new()
    {
        Source = ModSource.Modrinth,
        ProjectID = projectId,
        VersionID = versionId,
        PageURL = new Uri($"https://modrinth.com/mod/{projectId}"),
        Sha512 = Hash(bytes),
        VersionNumber = versionId
    };

    private static byte[] FabricJar(string id, string depends)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open()))
            writer.Write($"{{\"schemaVersion\":1,\"id\":\"{id}\",\"version\":\"1.0\",\"depends\":{depends}}}");
        return memory.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();

    private sealed class TestRecycleOperation : IRecycleBinOperation
    {
        public Task<RecycleBinItemStatus> DeleteAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return Task.FromResult(RecycleBinItemStatus.Recycled);
        }
    }

    private sealed class ModrinthServer : IDisposable
    {
        private static readonly Uri ApiBase = new("https://api.test/v2/");
        private readonly Dictionary<string, ProjectData> _projects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<VersionData>> _versions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, VersionData> _versionsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, VersionData> _recognized = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<VersionData>> _updates = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _downloads = new(StringComparer.Ordinal);
        private readonly HttpClient _http;
        private int _downloadRequests;
        private int _allDownloadsSawEmpty = 1;
        private string[] _observedContentDirectories = [];

        public ModrinthServer() => _http = new HttpClient(new CallbackHttpHandler(HandleAsync));
        public int DownloadRequests => Volatile.Read(ref _downloadRequests);
        public bool AllDownloadsSawEmptyContentDirectory => Volatile.Read(ref _allDownloadsSawEmpty) == 1;
        public bool AllDownloadsSawEmptyContentDirectories => AllDownloadsSawEmptyContentDirectory;
        public Func<CancellationToken, Task>? OnDownload { get; set; }
        public TaskCompletionSource? DownloadStarted { get; set; }
        public Func<IReadOnlyList<string>, CancellationToken, Task>? BeforeVersionUpdate { get; set; }

        public void ObserveContentDirectory(string path) => ObserveContentDirectories(path);

        public void ObserveContentDirectories(params string[] paths) => _observedContentDirectories = paths;

        public ModrinthClient CreateModrinth(HakoPaths paths) => new(_http, ApiBase, paths);
        public FabricClient CreateFabric(HakoPaths paths) => new(_http, paths, modrinthBase: ApiBase);

        public ModrinthProject Project(string id)
        {
            var project = _projects[id];
            return new ModrinthProject
            {
                Id = project.Id,
                Slug = project.Id,
                Title = project.Title,
                Description = "",
                IconUrl = project.IconUrl,
                ProjectType = project.Type,
                AllProjectTypes = project.AllProjectTypes
            };
        }

        public void AddProject(string id, string title, string type, string? iconUrl = null,
            IReadOnlyList<string>? allProjectTypes = null) =>
            _projects[id] = new ProjectData(id, title, type, iconUrl is null ? null : new Uri(iconUrl), allProjectTypes ?? [type]);

        public VersionData AddVersion(string projectId, string id, string number, string channel, string published,
            ModrinthContentKind kind, IReadOnlyList<FileData> files, IReadOnlyList<ModrinthDependency>? dependencies = null,
            IReadOnlyList<string>? gameVersions = null)
        {
            var loaders = kind switch
            {
                ModrinthContentKind.Mod => new[] { "fabric" },
                ModrinthContentKind.ResourcePack => new[] { "minecraft" },
                _ => new[] { "datapack" }
            };
            var version = BuildVersion(projectId, id, number, channel, published, gameVersions ?? ["1.20.4"],
                loaders, files, dependencies ?? []);
            _versionsById[id] = version;
            if (!_versions.TryGetValue(projectId, out var entries)) _versions[projectId] = entries = [];
            entries.Add(version);
            return version;
        }

        public VersionData WithProjectVersion(string projectId, string id, string number, string channel,
            IReadOnlyList<FileData> files) => BuildVersion(projectId, id, number, channel, "2025-01-01T00:00:00Z",
            ["1.20.4"], ["fabric"], files, []);

        public void ReplaceVersions(string projectId, IReadOnlyList<VersionData> versions) => _versions[projectId] = versions.ToList();

        public FileData File(string filename, byte[] bytes, string? fileType = null, bool primary = true)
        {
            var url = new Uri($"https://cdn.modrinth.com/{Guid.NewGuid():N}/{Uri.EscapeDataString(filename)}");
            _downloads[url.AbsolutePath] = bytes;
            return new FileData(filename, url, bytes, fileType, primary);
        }

        public void Recognize(string hash, VersionData version) => _recognized[hash] = version;

        public void AddUpdate(string currentHash, VersionData next)
        {
            if (!_updates.TryGetValue(currentHash, out var versions)) _updates[currentHash] = versions = [];
            versions.Add(next);
        }

        public void Dispose() => _http.Dispose();

        private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "cdn.modrinth.com")
            {
                Interlocked.Increment(ref _downloadRequests);
                if (DownloadStarted is not null) DownloadStarted.TrySetResult();
                if (OnDownload is not null) await OnDownload(cancellationToken);
                foreach (var contentDirectory in _observedContentDirectories)
                {
                    if (Directory.Exists(contentDirectory) && Directory.EnumerateFileSystemEntries(contentDirectory).Any())
                    {
                        Interlocked.Exchange(ref _allDownloadsSawEmpty, 0);
                        break;
                    }
                }
                return JsonBytes(HttpStatusCode.OK, _downloads[uri.AbsolutePath]);
            }

            if (uri.AbsolutePath.EndsWith("/project", StringComparison.Ordinal))
                return Json(HttpStatusCode.NotFound, "{}");
            if (uri.AbsolutePath.EndsWith("/version_files/update", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var hashes = body.RootElement.GetProperty("hashes").EnumerateArray().Select(value => value.GetString()!).ToArray();
                if (BeforeVersionUpdate is not null) await BeforeVersionUpdate(hashes, cancellationToken);
                var channels = body.RootElement.GetProperty("version_types").EnumerateArray().Select(value => value.GetString()!).ToArray();
                var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var hash in hashes)
                {
                    if (!_updates.TryGetValue(hash, out var candidates)) continue;
                    var match = candidates.Where(candidate => channels.Contains(candidate.Channel, StringComparer.Ordinal))
                        .OrderByDescending(candidate => candidate.Published, StringComparer.Ordinal).FirstOrDefault();
                    if (match is not null) result[hash] = JsonDocument.Parse(match.Json).RootElement.Clone();
                }

                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(result));
            }

            if (uri.AbsolutePath.EndsWith("/version_files", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var hash in body.RootElement.GetProperty("hashes").EnumerateArray().Select(value => value.GetString()!))
                {
                    if (_recognized.TryGetValue(hash, out var version))
                        result[hash] = new { id = version.Id, project_id = version.ProjectId, date_published = version.Published };
                }

                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(result));
            }

            if (uri.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal))
            {
                var ids = ReadQueryArray(uri, "ids");
                return Json(HttpStatusCode.OK, "[" + string.Join(',', ids.Where(_versionsById.ContainsKey).Select(id => _versionsById[id].Json)) + "]");
            }

            if (uri.AbsolutePath.EndsWith("/projects", StringComparison.Ordinal))
            {
                var ids = ReadQueryArray(uri, "ids");
                return Json(HttpStatusCode.OK, "[" + string.Join(',', ids.Where(_projects.ContainsKey).Select(id => _projects[id].Json)) + "]");
            }

            if (uri.AbsolutePath.Contains("/project/", StringComparison.Ordinal) && uri.AbsolutePath.EndsWith("/version", StringComparison.Ordinal))
            {
                var projectId = uri.AbsolutePath.Split('/')[^2];
                return Json(HttpStatusCode.OK, "[" + string.Join(',', _versions.GetValueOrDefault(projectId, []).Select(version => version.Json)) + "]");
            }

            return Json(HttpStatusCode.NotFound, "{}");
        }

        private static VersionData BuildVersion(string projectId, string id, string number, string channel,
            string published, IReadOnlyList<string> games, IReadOnlyList<string> loaders, IReadOnlyList<FileData> files,
            IReadOnlyList<ModrinthDependency> dependencies)
        {
            var dependencyRows = dependencies.Select(dependency => new
            {
                version_id = dependency.VersionId,
                project_id = dependency.ProjectId,
                dependency_type = dependency.Type
            }).ToArray();
            var json = JsonSerializer.Serialize(new
            {
                id,
                project_id = projectId,
                version_number = number,
                version_type = channel,
                date_published = published,
                game_versions = games,
                loaders,
                dependencies = dependencyRows,
                files = files.Select(file => new
                {
                    filename = file.Filename,
                    url = file.Url.AbsoluteUri,
                    size = file.Bytes.Length,
                    hashes = new { sha1 = file.Sha1, sha512 = file.Sha512 },
                    primary = file.Primary,
                    file_type = file.FileType
                })
            });
            return new VersionData(projectId, id, number, channel, published, loaders, files, dependencies, json);
        }

        private static string[] ReadQueryArray(Uri uri, string name)
        {
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var components = part.Split('=', 2);
                if (Uri.UnescapeDataString(components[0]) != name || components.Length != 2) continue;
                return JsonSerializer.Deserialize<string[]>(Uri.UnescapeDataString(components[1])) ?? [];
            }

            return [];
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        private static HttpResponseMessage JsonBytes(HttpStatusCode status, byte[] bytes) => new(status)
        {
            Content = new ByteArrayContent(bytes)
        };

        private sealed record ProjectData(string Id, string Title, string Type, Uri? IconUrl,
            IReadOnlyList<string> AllProjectTypes)
        {
            public string Json => JsonSerializer.Serialize(new
            {
                id = Id,
                slug = Id,
                title = Title,
                description = "",
                icon_url = IconUrl?.AbsoluteUri,
                project_type = Type,
                all_project_types = AllProjectTypes
            });
        }

        public sealed record FileData(string Filename, Uri Url, byte[] Bytes, string? FileType, bool Primary)
        {
            public string Sha1 => Convert.ToHexString(SHA1.HashData(Bytes)).ToLowerInvariant();
            public string Sha512 => Hash(Bytes);
        }

        public sealed record VersionData(string ProjectId, string Id, string Number, string Channel, string Published,
            IReadOnlyList<string> Loaders, IReadOnlyList<FileData> Files, IReadOnlyList<ModrinthDependency> Dependencies,
            string Json);
    }
}

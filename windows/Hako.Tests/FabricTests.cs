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
public sealed class FabricTests
{
    [TestMethod]
    public void VersionPredicatesUseAndInsideAlternativesAndSupportRangesWildcardsAndPrereleases()
    {
        Assert.IsTrue(new FabricVersionPredicate(">=0.16 <0.17").Matches("0.16.9"));
        Assert.IsFalse(new FabricVersionPredicate(">=0.16 <0.17").Matches("0.17.0"));
        Assert.IsTrue(new FabricVersionPredicate("~1.2.3").Matches("1.2.9"));
        Assert.IsFalse(new FabricVersionPredicate("~1.2.3").Matches("1.3.0"));
        Assert.IsTrue(new FabricVersionPredicate("^0.18.4").Matches("0.19.5"));
        Assert.IsTrue(new FabricVersionPredicate("1.2.x").Matches("1.2.99"));
        Assert.IsFalse(new FabricVersionPredicate("1.2.x").Matches("1.3.0"));
        Assert.IsFalse(new FabricVersionPredicate(">=1.0.0").Matches("1.0.0-beta.2"));
        Assert.IsTrue(new FabricVersionPredicate(["<0.10", ">=0.19"]).Matches("0.19.1"));
        Assert.IsFalse(new FabricVersionPredicate(["<0.10", ">=0.19"]).Matches("0.15.0"));

        var fromMacFixture = JsonSerializer.Deserialize(
            """{"id":"fabric-api","depends":{"fabricloader":["<0.10",">=0.19"],"java":">=17"}}""",
            HakoJsonContext.Default.FabricModMetadata)!;
        Assert.IsTrue(fromMacFixture.Supports("0.19.1", 17));
        Assert.IsFalse(fromMacFixture.Supports("0.15.0", 17));
    }

    [TestMethod]
    public void WildcardComparisonsHonorInclusiveAndExclusiveBoundaries()
    {
        var minimum = new FabricVersionPredicate(">=0.16.x");
        Assert.IsFalse(minimum.Matches("0.15.9"));
        Assert.IsTrue(minimum.Matches("0.16.0"));
        Assert.IsTrue(minimum.Matches("0.16.9"));
        Assert.IsTrue(minimum.Matches("0.17.2"));

        Assert.IsFalse(new FabricVersionPredicate(">0.16.x").Matches("0.16.9"));
        Assert.IsTrue(new FabricVersionPredicate(">0.16.x").Matches("0.17.0"));
        Assert.IsTrue(new FabricVersionPredicate("<=0.16.x").Matches("0.16.9"));
        Assert.IsFalse(new FabricVersionPredicate("<=0.16.x").Matches("0.17.0"));
        Assert.IsTrue(new FabricVersionPredicate("<0.16.x").Matches("0.15.9"));
        Assert.IsFalse(new FabricVersionPredicate("<0.16.x").Matches("0.16.0"));
    }

    [TestMethod]
    public void LatestApiSelectionPrefersReleaseThenBetaThenAlphaAndUsesNewestWithinChannel()
    {
        ModrinthVersion Version(string id, string channel, string date) => new()
        {
            Id = id,
            ProjectId = FabricClient.FabricApiProjectId,
            VersionNumber = id,
            VersionType = channel,
            DatePublished = DateTimeOffset.Parse(date),
            GameVersions = ["1.20.4"],
            Loaders = ["fabric"]
        };
        var versions = new[]
        {
            Version("alpha-new", "alpha", "2025-04-01T00:00:00Z"),
            Version("beta-new", "beta", "2025-05-01T00:00:00Z"),
            Version("release-old", "release", "2024-01-01T00:00:00Z"),
            Version("release-new", "release", "2024-02-01T00:00:00Z"),
            Version("other-minecraft", "release", "2026-01-01T00:00:00Z") with { GameVersions = ["1.20.1"] }
        };

        Assert.AreEqual("release-new", FabricClient.SelectLatestApiVersion(versions, "1.20.4").Id);
        Assert.AreEqual("beta-new", FabricClient.SelectLatestApiVersion(versions.Where(item => item.VersionType != "release"), "1.20.4").Id);
        Assert.AreEqual("alpha-new", FabricClient.SelectLatestApiVersion(versions.Where(item => item.VersionType == "alpha"), "1.20.4").Id);
    }

    [TestMethod]
    public async Task LateFabricPreparationCannotReplaceTheApiThatWillBeSaved()
    {
        var firstCompletion = new TaskCompletionSource<FabricPreparation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompletion = new TaskCompletionSource<FabricPreparation>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstCancellation = new CancellationTokenSource();
        var selectedVersion = "A";
        FabricPreparation? currentPreparation = null;
        var firstPreparation = Preparation("api-A");
        var secondPreparation = Preparation("api-B");

        var first = FabricPreparationCommit.PrepareAndCommitIfCurrentAsync(
            () => firstCompletion.Task,
            () => !firstCancellation.IsCancellationRequested && selectedVersion == "A",
            preparation => currentPreparation = preparation);

        selectedVersion = "B";
        firstCancellation.Cancel();
        var second = FabricPreparationCommit.PrepareAndCommitIfCurrentAsync(
            () => secondCompletion.Task,
            () => selectedVersion == "B",
            preparation => currentPreparation = preparation);

        secondCompletion.SetResult(secondPreparation);
        Assert.IsTrue(await second);
        firstCompletion.SetResult(firstPreparation);
        Assert.IsFalse(await first);

        var saved = new FabricConfiguration("0.16.9", currentPreparation?.Api);
        Assert.AreEqual(secondPreparation.Api, saved.Api);
    }

    [TestMethod]
    public async Task PreparationChecksApiHashAndFiltersLoadersByApiAndJavaRequirements()
    {
        using var temporary = new TemporaryDirectory();
        var apiJar = CreateApiJar("""{"id":"fabric-api","depends":{"fabricloader":">=0.16 <0.17","java":">=17 <22"}}""");
        var sha1 = Hash1(apiJar);
        var sha512 = Hash512(apiJar);
        var apiJson = ApiVersionsJson(apiJar, sha1, sha512);
        var handler = new CallbackHttpHandler((request, _) =>
        {
            Assert.AreEqual(FabricClient.UserAgent, request.Headers.UserAgent.ToString());
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/versions/loader/1.20.4", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse("""[{"loader":{"version":"0.16.9","stable":true}},{"loader":{"version":"0.17.2","stable":true}},{"loader":{"version":"0.16.8","stable":false}}]"""));
            if (path.EndsWith("/project/P7dR8mSH/version", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(apiJson));
            if (request.RequestUri.AbsoluteUri == "https://cdn.test/fabric-api.jar")
                return Task.FromResult(BytesResponse(apiJar));
            throw new AssertFailedException($"Unexpected Fabric request: {request.RequestUri}");
        });
        using var client = new FabricClient(new HttpClient(handler), cacheRoot: Path.Combine(temporary.Root, "FabricAPI"),
            metaBase: new Uri("https://meta.test/v2/"), modrinthBase: new Uri("https://api.test/v2/"));

        var result = await client.PrepareAsync("1.20.4", 17);

        Assert.AreEqual("release", result.Api.Channel);
        CollectionAssert.AreEqual(new[] { "0.16.9", "0.16.8" }, result.Loaders.Select(loader => loader.Version).ToArray());
        Assert.IsTrue(File.Exists(Path.Combine(temporary.Root, "FabricAPI", sha512 + ".jar")));
        Assert.AreEqual(3, handler.RequestCount);
    }

    [TestMethod]
    public async Task LoaderCatalogTreatsBadRequestWithEmptyArrayAsEmptyAndRejectsOtherBodies()
    {
        using var emptyClient = new FabricClient(new HttpClient(new CallbackHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("[]") }))),
            cacheRoot: Path.GetTempPath(), metaBase: new Uri("https://meta.test/v2/"));

        var loaders = await emptyClient.GetLoaderVersionsAsync("1.20.4");

        Assert.AreEqual(0, loaders.Count);

        using var invalidClient = new FabricClient(new HttpClient(new CallbackHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"missing\"}") }))),
            cacheRoot: Path.GetTempPath(), metaBase: new Uri("https://meta.test/v2/"));
        var exception = await Assert.ThrowsExactlyAsync<MojangException>(() => invalidClient.GetLoaderVersionsAsync("1.20.4"));
        Assert.AreEqual(MojangError.Http, exception.Error);
        Assert.AreEqual((int)HttpStatusCode.BadRequest, exception.StatusCode);
    }

    [TestMethod]
    public async Task ProfileMustInheritSelectedMinecraftAndMavenLibrariesUseSha1SidecarWhenNeeded()
    {
        var libraryBytes = Encoding.UTF8.GetBytes("fabric library");
        var checksum = Hash1(libraryBytes);
        var handler = new CallbackHttpHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/profile/json", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse("""{"id":"fabric-loader-0.16.9-1.20.4","inheritsFrom":"1.20.4","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"org.example:sample:1.2.3","url":"https://maven.test/","size":14}]}"""));
            if (request.RequestUri.AbsoluteUri == "https://maven.test/org/example/sample/1.2.3/sample-1.2.3.jar.sha1")
                return Task.FromResult(TextResponse(checksum + "\n"));
            throw new AssertFailedException($"Unexpected Fabric request: {request.RequestUri}");
        });
        using var client = new FabricClient(new HttpClient(handler), cacheRoot: Path.GetTempPath(), metaBase: new Uri("https://meta.test/v2/"));

        var (profile, _) = await client.GetProfileAsync("1.20.4", "0.16.9");
        var libraries = await client.ResolveLibrariesAsync(profile);

        Assert.AreEqual("1.20.4", profile.InheritsFrom);
        Assert.AreEqual("org/example/sample/1.2.3/sample-1.2.3.jar", libraries.Single().Path);
        Assert.AreEqual(checksum, libraries.Single().Download.Sha1);
        Assert.AreEqual(2, handler.RequestCount);

        var wrongParent = new FabricProfile { Id = "fabric-loader-0.16.9-1.20.1", InheritsFrom = "1.20.1", MainClass = "Main" };
        Assert.ThrowsExactly<MojangException>(() => wrongParent.Validate("1.20.4", "0.16.9"));
    }

    [TestMethod]
    public async Task InstallerWritesFabricProfileLibrariesAndProvisionedApiWithPinnedHashes()
    {
        using var temporary = new TemporaryDirectory();
        var apiJar = CreateApiJar("""{"id":"fabric-api","depends":{"fabricloader":">=0.16 <0.17","java":">=17"}}""");
        var api = Descriptor(apiJar);
        var clientBytes = Encoding.UTF8.GetBytes("minecraft client");
        var javaBytes = Encoding.UTF8.GetBytes("java runtime");
        var libraryBytes = Encoding.UTF8.GetBytes("fabric maven lib");
        var profileBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "fabric-loader-0.16.9-1.20.4",
            inheritsFrom = "1.20.4",
            mainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient",
            arguments = new { jvm = new[] { "-Dfabric.loader=true" } },
            libraries = new[] { new { name = "org.example:sample:1.2.3", url = "https://maven.test/", sha1 = Hash1(libraryBytes), size = libraryBytes.Length } }
        });
        var fabricHandler = new CallbackHttpHandler((request, _) => request.RequestUri!.AbsoluteUri switch
        {
            "https://meta.test/v2/versions/loader/1.20.4/0.16.9/profile/json" => Task.FromResult(BytesResponse(profileBytes)),
            "https://cdn.test/fabric-api.jar" => Task.FromResult(BytesResponse(apiJar)),
            _ => throw new AssertFailedException($"Unexpected Fabric request: {request.RequestUri}")
        });
        var mojangHandler = new CallbackHttpHandler((request, _) => request.RequestUri!.AbsoluteUri switch
        {
            "https://test/client.jar" => Task.FromResult(BytesResponse(clientBytes)),
            "https://test/javaw.exe" => Task.FromResult(BytesResponse(javaBytes)),
            "https://maven.test/org/example/sample/1.2.3/sample-1.2.3.jar" => Task.FromResult(BytesResponse(libraryBytes)),
            _ => throw new AssertFailedException($"Unexpected download: {request.RequestUri}")
        });
        using var mojang = new MojangClient(new HttpClient(mojangHandler));
        using var fabric = new FabricClient(new HttpClient(fabricHandler), cacheRoot: Path.Combine(temporary.Root, "FabricAPI"),
            metaBase: new Uri("https://meta.test/v2/"));
        var prepared = Prepared("1.20.4", clientBytes, javaBytes);
        var installer = new MinecraftInstaller(mojang, fabric);

        var result = await installer.InstallFabricAsync(prepared, Path.Combine(temporary.Root, "Build"),
            new FabricConfiguration("0.16.9", api));
        var root = Path.Combine(temporary.Root, "Build");

        Assert.AreEqual(Hash1(profileBytes), result.FabricProfileSHA1);
        CollectionAssert.AreEqual(profileBytes, File.ReadAllBytes(Path.Combine(root, "minecraft", ".hako-fabric.json")));
        Assert.AreEqual(Hash1(libraryBytes), Hash1(File.ReadAllBytes(Path.Combine(root, "minecraft", "libraries", "org", "example", "sample", "1.2.3", "sample-1.2.3.jar"))));
        CollectionAssert.AreEqual(apiJar, File.ReadAllBytes(Path.Combine(root, "minecraft", "mods", api.Filename)));
        Assert.IsTrue(InstanceContent.ApiWasProvisioned(root));
        Assert.AreEqual(3, mojangHandler.RequestCount);
        Assert.AreEqual(2, fabricHandler.RequestCount);

        var installedApi = Path.Combine(root, "minecraft", "mods", api.Filename);
        var disabledApi = installedApi + ".disabled";
        File.Move(installedApi, disabledApi);
        var repeated = await installer.InstallFabricAsync(prepared, root, new FabricConfiguration("0.16.9", api));

        Assert.AreEqual(Hash1(profileBytes), repeated.FabricProfileSHA1);
        Assert.IsFalse(File.Exists(installedApi));
        CollectionAssert.AreEqual(apiJar, File.ReadAllBytes(disabledApi));
        Assert.IsTrue(InstanceContent.ApiWasProvisioned(root));
        Assert.AreEqual(3, fabricHandler.RequestCount);
    }

    [TestMethod]
    public async Task InstallerCompletesFabricInstallationWithoutOptionalApi()
    {
        using var temporary = new TemporaryDirectory();
        var clientBytes = Encoding.UTF8.GetBytes("minecraft client");
        var javaBytes = Encoding.UTF8.GetBytes("java runtime");
        var profileBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "fabric-loader-0.16.9-1.20.4",
            inheritsFrom = "1.20.4",
            mainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient",
            libraries = Array.Empty<object>()
        });
        var fabricHandler = new CallbackHttpHandler((request, _) =>
        {
            Assert.AreEqual("https://meta.test/v2/versions/loader/1.20.4/0.16.9/profile/json", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(BytesResponse(profileBytes));
        });
        var mojangHandler = new CallbackHttpHandler((request, _) => request.RequestUri!.AbsoluteUri switch
        {
            "https://test/client.jar" => Task.FromResult(BytesResponse(clientBytes)),
            "https://test/javaw.exe" => Task.FromResult(BytesResponse(javaBytes)),
            _ => throw new AssertFailedException($"Unexpected download: {request.RequestUri}")
        });
        using var mojang = new MojangClient(new HttpClient(mojangHandler));
        using var fabric = new FabricClient(new HttpClient(fabricHandler), cacheRoot: Path.Combine(temporary.Root, "FabricAPI"),
            metaBase: new Uri("https://meta.test/v2/"));
        var root = Path.Combine(temporary.Root, "Build");

        var result = await new MinecraftInstaller(mojang, fabric).InstallFabricAsync(
            Prepared("1.20.4", clientBytes, javaBytes), root, new FabricConfiguration("0.16.9"));

        Assert.AreEqual(Hash1(profileBytes), result.FabricProfileSHA1);
        CollectionAssert.AreEqual(profileBytes, File.ReadAllBytes(Path.Combine(root, "minecraft", ".hako-fabric.json")));
        Assert.IsTrue(Directory.Exists(Path.Combine(root, "minecraft", "mods")));
        Assert.IsFalse(Directory.Exists(Path.Combine(temporary.Root, "FabricAPI")));
        Assert.AreEqual(1, fabricHandler.RequestCount);
        Assert.AreEqual(2, mojangHandler.RequestCount);
    }

    [TestMethod]
    public async Task ProvisionApiDoesNotReinstallAfterUserDeletesIt()
    {
        using var temporary = new TemporaryDirectory();
        var apiJar = CreateApiJar("""{"id":"fabric-api","depends":{}}""");
        var api = Descriptor(apiJar);
        var cache = Path.Combine(temporary.Root, "cache.jar");
        var root = Path.Combine(temporary.Root, "Build");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        await File.WriteAllBytesAsync(cache, apiJar);

        await InstanceContent.ProvisionAPIAsync(api, cache, root, CancellationToken.None);
        var installed = Path.Combine(root, "minecraft", "mods", api.Filename);
        Assert.IsTrue(File.Exists(installed));
        File.Delete(installed);
        await InstanceContent.ProvisionAPIAsync(api, cache, root, CancellationToken.None);

        Assert.IsTrue(InstanceContent.ApiWasProvisioned(root));
        Assert.IsFalse(File.Exists(installed));
    }

    [TestMethod]
    public void MacRegistryFixtureRoundTripsWithSwiftKeysAndOptionalFieldsOmitted()
    {
        using var temporary = new TemporaryDirectory();
        var minecraft = Path.Combine(temporary.Root, "minecraft");
        Directory.CreateDirectory(minecraft);
        var fixture = """{"apiProvisioned":true,"files":{"fabric-api-0.100.jar":{"source":"modrinth","projectID":"P7dR8mSH","versionID":"abc123","pageURL":"https://modrinth.com/mod/fabric-api/version/abc123","sha512":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","api":{"projectID":"P7dR8mSH","versionID":"abc123","version":"0.100","channel":"release","filename":"fabric-api-0.100.jar","url":"https://cdn.test/fabric-api.jar","size":4,"sha1":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","sha512":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"versionNumber":"0.100"}}}""";
        File.WriteAllText(Path.Combine(minecraft, ".hako-mods.json"), fixture);

        var registry = InstanceContent.ReadRegistry(temporary.Root);
        Assert.IsTrue(registry.ApiProvisioned);
        Assert.AreEqual(ModSource.Modrinth, registry.Files["fabric-api-0.100.jar"].Source);
        Assert.IsNull(registry.Files["fabric-api-0.100.jar"].IconURL);
        InstanceContent.WriteRegistry(temporary.Root, registry);

        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(minecraft, ".hako-mods.json")));
        var root = document.RootElement;
        Assert.IsTrue(root.TryGetProperty("apiProvisioned", out _));
        var origin = root.GetProperty("files").GetProperty("fabric-api-0.100.jar");
        CollectionAssert.AreEquivalent(new[] { "apiProvisioned", "files" }, root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual("modrinth", origin.GetProperty("source").GetString());
        Assert.IsTrue(origin.TryGetProperty("projectID", out _));
        Assert.IsTrue(origin.TryGetProperty("pageURL", out _));
        Assert.IsFalse(origin.TryGetProperty("iconURL", out _));
        Assert.IsFalse(origin.TryGetProperty("fileType", out _));
        Assert.IsTrue(origin.GetProperty("api").TryGetProperty("sha512", out _));
    }

    [TestMethod]
    public async Task RegistryRejectsMissingOrNullRequiredStructureWithoutOverwritingIt()
    {
        using var temporary = new TemporaryDirectory();
        var minecraft = Path.Combine(temporary.Root, "minecraft");
        Directory.CreateDirectory(minecraft);
        var registryPath = Path.Combine(minecraft, ".hako-mods.json");
        const string validOrigin = "{\"source\":\"local\",\"projectID\":\"local\",\"versionID\":\"local\",\"pageURL\":\"https://modrinth.com/\",\"sha512\":\"hash\"}";
        static string Registry(string origin) => $"{{\"apiProvisioned\":false,\"files\":{{\"item.jar\":{origin}}}}}";
        var invalidRegistries = new[]
        {
            "{\"files\":{}}",
            "{\"apiProvisioned\":false,\"files\":null}",
            "{\"apiProvisioned\":false,\"files\":{\"item.jar\":null}}",
            Registry(validOrigin.Replace(",\"sha512\":\"hash\"", "", StringComparison.Ordinal)),
            Registry(validOrigin.Replace("\"projectID\":\"local\"", "\"projectID\":null", StringComparison.Ordinal)),
            Registry(validOrigin.Replace("\"source\":\"local\"", "\"source\":null", StringComparison.Ordinal)),
            Registry(validOrigin.Replace("\"pageURL\":\"https://modrinth.com/\"", "\"pageURL\":null", StringComparison.Ordinal)),
            Registry(validOrigin.Replace("}", ",\"api\":{\"projectID\":null}}", StringComparison.Ordinal))
        };
        var api = Descriptor([1, 2, 3]);

        foreach (var invalid in invalidRegistries)
        {
            await File.WriteAllTextAsync(registryPath, invalid);
            await Assert.ThrowsExactlyAsync<JsonException>(() => InstanceContent.ProvisionAPIAsync(
                api, Path.Combine(temporary.Root, "missing.jar"), temporary.Root, CancellationToken.None));
            Assert.AreEqual(invalid, await File.ReadAllTextAsync(registryPath));
        }
    }

    [TestMethod]
    public async Task LaunchUsesFabricMainClassVersionAndLibraryOverridesAndMissingProfileFails()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Root;
        var manifest = new MojangVersionMetadata { Id = "1.20.4", MainClass = "vanilla.Main", MinecraftArguments = "--version ${version_name}" };
        var fabricLibrary = new LibraryInstallation(new MojangDownload(), "org/example/library/2.0/library-2.0.jar");
        var fabricClassifiedLibrary = new LibraryInstallation(new MojangDownload(), "org/example/library/2.0/library-2.0-windows.jar");
        var baseLibraries = new[]
        {
            new LibraryInstallation(new MojangDownload(), "org/example/library/1.0/library-1.0.jar"),
            new LibraryInstallation(new MojangDownload(), "org/example/library/1.0/library-1.0-windows.jar"),
            new LibraryInstallation(new MojangDownload(), "org/example/other/1.0/other-1.0.jar")
        };
        var profile = new MinecraftLaunchProfile("net.fabricmc.loader.impl.launch.knot.KnotClient", "1.20.4",
            "fabric-loader-0.16.9-1.20.4", [fabricLibrary, fabricClassifiedLibrary],
            JvmArguments: [Argument("-Dfabric.jvm=true")], GameArguments: [Argument("--fabricLaunch=true")]);

        foreach (var source in Enum.GetValues<LaunchArgumentSource>())
        {
            var plan = MinecraftLaunchPlan.Build(manifest, root, "javaw.exe", MinecraftLaunchIdentity.Offline("Player"),
                source, new InstanceParameters { MaximumMemoryMiB = 2048 }, GameArchitecture.X64,
                libraries: baseLibraries, profile: profile);
            var classpath = plan.Arguments[Array.IndexOf(plan.Arguments.ToArray(), "-cp") + 1];

            Assert.IsTrue(plan.Arguments.Contains(profile.MainClass));
            Assert.IsTrue(plan.Arguments.Contains("fabric-loader-0.16.9-1.20.4"));
            Assert.IsTrue(plan.Arguments.Contains("-Dfabric.jvm=true"));
            Assert.IsTrue(plan.Arguments.Contains("--fabricLaunch=true"));
            StringAssert.Contains(classpath, "library-2.0.jar");
            StringAssert.Contains(classpath, "library-2.0-windows.jar");
            StringAssert.Contains(classpath, "other-1.0.jar");
            Assert.IsFalse(classpath.Contains("library-1.0.jar", StringComparison.Ordinal));
        }

        var missing = await Assert.ThrowsExactlyAsync<MinecraftLaunchException>(() =>
            MinecraftLaunchPlan.LoadInstalledFabricProfileAsync(root, "1.20.4", "0.16.9", Hash1(Encoding.UTF8.GetBytes("absent"))));
        Assert.AreEqual(MinecraftLaunchError.FabricProfileMissing, missing.Error);

        var installedProfile = JsonSerializer.SerializeToUtf8Bytes(new FabricProfile
        {
            Id = "fabric-loader-0.16.9-1.20.4",
            InheritsFrom = "1.20.4",
            MainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient"
        }, HakoJsonContext.Default.FabricProfile);
        var fabricProfilePath = Path.Combine(root, "minecraft", ".hako-fabric.json");
        Directory.CreateDirectory(Path.GetDirectoryName(fabricProfilePath)!);
        await File.WriteAllBytesAsync(fabricProfilePath, installedProfile);
        var damaged = await Assert.ThrowsExactlyAsync<MinecraftLaunchException>(() =>
            MinecraftLaunchPlan.LoadInstalledFabricProfileAsync(root, "1.20.4", "0.16.9", Hash1(Encoding.UTF8.GetBytes("different profile"))));
        Assert.AreEqual(MinecraftLaunchError.FabricProfileMissing, damaged.Error);
        Assert.IsInstanceOfType<MojangException>(damaged.InnerException);
        Assert.AreEqual(MojangError.Integrity, ((MojangException)damaged.InnerException!).Error);
    }

    [TestMethod]
    public async Task RegistryDamageIsNotSilentlyReplacedAndModNameCollisionIgnoresCaseAndDisabledSuffix()
    {
        using var temporary = new TemporaryDirectory();
        var registryPath = Path.Combine(temporary.Root, "minecraft", ".hako-mods.json");
        Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
        await File.WriteAllTextAsync(registryPath, "{broken");
        var apiJar = CreateApiJar("""{"id":"fabric-api","depends":{}}""");
        try
        {
            await InstanceContent.ProvisionAPIAsync(Descriptor(apiJar), Path.Combine(temporary.Root, "missing.jar"),
                temporary.Root, CancellationToken.None);
            Assert.Fail("The malformed registry should be rejected.");
        }
        catch (JsonException)
        {
        }
        Assert.AreEqual("{broken", await File.ReadAllTextAsync(registryPath));
        File.Delete(registryPath);

        var mods = Path.Combine(temporary.Root, "minecraft", "mods");
        Directory.CreateDirectory(mods);
        await File.WriteAllTextAsync(Path.Combine(mods, "Existing.jar.disabled"), "user choice");
        var source = Path.Combine(temporary.Root, "source.jar");
        await File.WriteAllBytesAsync(source, Encoding.UTF8.GetBytes("jar"));
        await Assert.ThrowsExactlyAsync<IOException>(() => InstanceContent.PublishJarAsync(source, "existing.JAR", temporary.Root,
            origin: null, CancellationToken.None));
    }

    private static PreparedInstallation Prepared(string versionId, byte[] clientBytes, byte[] javaBytes)
    {
        var manifest = new MojangVersionMetadata
        {
            Id = versionId,
            MainClass = "net.minecraft.client.main.Main",
            JavaVersion = new MojangJavaRequirement { Component = "java-runtime-gamma", MajorVersion = 17 },
            Downloads = new Dictionary<string, MojangDownload>(StringComparer.OrdinalIgnoreCase)
            {
                ["client"] = new() { Url = "https://test/client.jar", Sha1 = Hash1(clientBytes), Size = clientBytes.Length }
            }
        };
        var manifestData = JsonSerializer.SerializeToUtf8Bytes(manifest, MojangJsonContext.Default.MojangVersionMetadata);
        var runtimeManifest = new MojangJavaRuntimeManifest
        {
            Files = new Dictionary<string, MojangJavaRuntimeEntry>
            {
                ["bin/javaw.exe"] = new()
                {
                    Type = "file",
                    Downloads = new Dictionary<string, MojangDownload>
                    {
                        ["raw"] = new() { Url = "https://test/javaw.exe", Sha1 = Hash1(javaBytes), Size = javaBytes.Length }
                    }
                }
            }
        };
        var runtimeData = JsonSerializer.SerializeToUtf8Bytes(runtimeManifest, MojangJsonContext.Default.MojangJavaRuntimeManifest);
        return new PreparedInstallation(
            new MojangVersionEntry { Id = versionId, Url = "https://test/version.json", Sha1 = Hash1(manifestData) },
            GameArchitecture.X64,
            manifest,
            manifestData,
            new MojangJavaRuntimePackage
            {
                Manifest = new MojangDownload { Url = "https://test/runtime.json", Sha1 = Hash1(runtimeData), Size = runtimeData.Length },
                Version = new MojangRuntimeVersion { Name = "17.0.1", Released = "2025-01-01" }
            },
            runtimeManifest,
            runtimeData,
            []);
    }

    private static string ApiVersionsJson(byte[] jar, string sha1, string sha512)
    {
        var versions = new[]
        {
            new { id = "release", project_id = FabricClient.FabricApiProjectId, version_number = "0.100.0", version_type = "release",
                date_published = "2024-01-01T00:00:00Z", game_versions = new[] { "1.20.4" }, loaders = new[] { "fabric" },
                files = new[] { new { filename = "fabric-api.jar", url = "https://cdn.test/fabric-api.jar", size = jar.Length,
                    hashes = new { sha1, sha512 }, primary = true, file_type = (string?)null } } },
            new { id = "newer-beta", project_id = FabricClient.FabricApiProjectId, version_number = "0.101.0-beta.1", version_type = "beta",
                date_published = "2026-01-01T00:00:00Z", game_versions = new[] { "1.20.4" }, loaders = new[] { "fabric" },
                files = new[] { new { filename = "fabric-api-beta.jar", url = "https://cdn.test/fabric-api.jar", size = jar.Length,
                    hashes = new { sha1, sha512 }, primary = true, file_type = (string?)null } } }
        };
        return JsonSerializer.Serialize(versions);
    }

    private static FabricApiDescriptor Descriptor(byte[] bytes) => new(
        FabricClient.FabricApiProjectId, "api-version", "0.100.0", "release", "fabric-api.jar",
        "https://cdn.test/fabric-api.jar", bytes.Length, Hash1(bytes), Hash512(bytes));

    private static FabricPreparation Preparation(string versionId) => new(
        new FabricApiDescriptor(FabricClient.FabricApiProjectId, versionId, "0.100.0", "release", "fabric-api.jar",
            "https://cdn.test/fabric-api.jar", 1, "sha1", "sha512"),
        new FabricModMetadata(),
        [new FabricLoaderVersion("0.16.9", true)]);

    private static byte[] CreateApiJar(string metadata)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open(), new UTF8Encoding(false)))
            writer.Write(metadata);
        return output.ToArray();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage TextResponse(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "text/plain")
    };

    private static HttpResponseMessage BytesResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
    };

    private static string Hash1(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
    private static string Hash512(byte[] bytes) => Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();

    private static MojangArgument Argument(string value) => new()
    {
        Value = JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone()
    };
}

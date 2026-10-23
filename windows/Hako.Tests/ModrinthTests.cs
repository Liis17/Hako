using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hako.Core.Minecraft;

namespace Hako.Tests;

[TestClass]
public sealed class ModrinthTests
{
    [TestMethod]
    public void ProjectArraysRoundTripWithSourceGeneratedContext()
    {
        ModrinthProject[] projects =
        [
            new() { Id = "dual", Slug = "dual-pack", Title = "Pack", Description = "Both types",
                ProjectType = "resourcepack", AllProjectTypes = ["resourcepack", "datapack"],
                IconUrl = new Uri("https://cdn.test/icon.png") },
            new() { Id = "empty-types", ProjectType = "mod", AllProjectTypes = [] }
        ];
        var json = JsonSerializer.Serialize(projects, Hako.Core.HakoJsonContext.Default.ModrinthProjectArray);
        var restored = JsonSerializer.Deserialize(json, Hako.Core.HakoJsonContext.Default.ModrinthProjectArray)!;
        Assert.AreEqual(2, restored.Length);
        Assert.AreEqual(projects[0].Id, restored[0].Id);
        Assert.AreEqual(projects[0].Slug, restored[0].Slug);
        Assert.AreEqual(projects[0].Title, restored[0].Title);
        Assert.AreEqual(projects[0].Description, restored[0].Description);
        Assert.AreEqual(projects[0].IconUrl, restored[0].IconUrl);
        Assert.AreEqual(projects[0].ProjectType, restored[0].ProjectType);
        CollectionAssert.AreEqual(projects[0].AllProjectTypes.ToArray(), restored[0].AllProjectTypes.ToArray());
        Assert.AreEqual(0, restored[1].AllProjectTypes.Count);

        var emptyJson = JsonSerializer.Serialize(Array.Empty<ModrinthProject>(), Hako.Core.HakoJsonContext.Default.ModrinthProjectArray);
        Assert.AreEqual("[]", emptyJson);
        Assert.AreEqual(0, JsonSerializer.Deserialize(emptyJson, Hako.Core.HakoJsonContext.Default.ModrinthProjectArray)!.Length);
    }

    private static readonly Uri ApiBase = new("https://api.test/v2/");

    [TestMethod]
    public async Task SearchUsesExactFacetsEscapesPlusAndSupportsPaginationAndSort()
    {
        using var temporary = new TemporaryDirectory();
        string? requestUri = null;
        using var client = new ModrinthClient(new HttpClient(new CallbackHttpHandler((request, _) =>
        {
            requestUri = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(TestData.Json(HttpStatusCode.OK,
                """{"hits":[{"project_id":"project123","title":"Sodium","project_type":"mod"}],"total_hits":41}"""));
        })), ApiBase, cacheRoot: temporary.Root);

        var page = await client.SearchAsync("  shaders + packs ", ModrinthContentKind.Mod, "1.20.4",
            ModrinthSort.Updated, offset: 20, limit: 10);

        Assert.AreEqual(41, page.TotalHits);
        Assert.AreEqual("project123", page.Hits.Single().Id);
        StringAssert.Contains(requestUri!, "query=shaders%20%2B%20packs");
        StringAssert.Contains(requestUri!, "index=updated");
        StringAssert.Contains(requestUri!, "offset=20");
        StringAssert.Contains(requestUri!, "limit=10");
        var decoded = Uri.UnescapeDataString(requestUri!);
        StringAssert.Contains(decoded, "project_type:mod");
        StringAssert.Contains(decoded, "categories:fabric");
        StringAssert.Contains(decoded, "versions:1.20.4");
        StringAssert.Contains(decoded, "environment!=dedicated_server_only");
    }

    [TestMethod]
    public async Task ResourcepackSearchUsesOnlyResourcepackAndExactVersionFacets()
    {
        using var temporary = new TemporaryDirectory();
        string? requestUri = null;
        using var client = new ModrinthClient(new HttpClient(new CallbackHttpHandler((request, _) =>
        {
            requestUri = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(TestData.Json(HttpStatusCode.OK, "{\"hits\":[],\"total_hits\":0}"));
        })), ApiBase, cacheRoot: temporary.Root);

        await client.SearchAsync("", ModrinthContentKind.ResourcePack, "26.1", offset: 40);

        var decoded = Uri.UnescapeDataString(requestUri!);
        StringAssert.Contains(decoded, "project_type:resourcepack");
        StringAssert.Contains(decoded, "versions:26.1");
        Assert.IsFalse(decoded.Contains("categories:fabric", StringComparison.Ordinal));
        Assert.IsFalse(decoded.Contains("environment!=dedicated_server_only", StringComparison.Ordinal));
        Assert.IsFalse(decoded.Contains("query=", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DatapackSearchUsesAllTypesCategoryAndMinecraftFacets()
    {
        using var temporary = new TemporaryDirectory();
        string? requestUri = null;
        using var client = new ModrinthClient(new HttpClient(new CallbackHttpHandler((request, _) =>
        {
            requestUri = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(TestData.Json(HttpStatusCode.OK, "{\"hits\":[],\"total_hits\":0}"));
        })), ApiBase, cacheRoot: temporary.Root);

        await client.SearchAsync("world gen", ModrinthContentKind.Datapack, "1.20.4");

        var decoded = Uri.UnescapeDataString(requestUri!);
        StringAssert.Contains(decoded, "all_project_types:datapack");
        StringAssert.Contains(decoded, "categories:datapack");
        StringAssert.Contains(decoded, "versions:1.20.4");
        Assert.IsFalse(decoded.Contains("project_type:mod", StringComparison.Ordinal));
        Assert.IsFalse(decoded.Contains("categories:fabric", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DatapackVersionsUseDatapackLoaderAndExcludeAttachedResourcePackFiles()
    {
        using var temporary = new TemporaryDirectory();
        var bytes = Encoding.UTF8.GetBytes("archive bytes");
        var requestUri = "";
        var json = "[" + string.Join(',',
            VersionJson("datapack", "release", "2025-01-02T00:00:00Z", ["1.20.4"], ["datapack"],
                [FileJson("main.zip", bytes, primary: true), FileJson("required.zip", bytes, "required-resource-pack"),
                 FileJson("optional.zip", bytes, "optional-resource-pack")]),
            VersionJson("wrong-loader", "release", "2025-01-04T00:00:00Z", ["1.20.4"], ["fabric"],
                [FileJson("fabric.zip", bytes)]),
            VersionJson("wrong-game", "release", "2025-01-05T00:00:00Z", ["1.20.5"], ["datapack"],
                [FileJson("wrong.zip", bytes)]),
            VersionJson("resource-only", "release", "2025-01-03T00:00:00Z", ["1.20.4"], ["datapack"],
                [FileJson("attached.zip", bytes, "required-resource-pack", primary: true)])) + "]";
        using var client = new ModrinthClient(new HttpClient(new CallbackHttpHandler((request, _) =>
        {
            requestUri = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(TestData.Json(HttpStatusCode.OK, json));
        })), ApiBase, cacheRoot: temporary.Root);

        var versions = await client.GetCompatibleVersionsAsync("project123", ModrinthContentKind.Datapack, "1.20.4");

        CollectionAssert.AreEqual(new[] { "datapack" }, versions.Select(version => version.Id).ToArray());
        Assert.AreEqual("main.zip", versions.Single().InstallableFile(ModrinthContentKind.Datapack)!.Filename);
        CollectionAssert.AreEqual(new[] { "required.zip", "optional.zip" },
            versions.Single().ResourcePacks.Select(file => file.Filename).ToArray());
        var decodedQuery = Uri.UnescapeDataString(requestUri).Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        StringAssert.Contains(decodedQuery, "loaders=[\"datapack\"]");
        StringAssert.Contains(decodedQuery, "game_versions=[\"1.20.4\"]");
    }

    [TestMethod]
    public async Task CompatibleVersionsUseActualVersionsFilesAndReleaseDateOrder()
    {
        using var temporary = new TemporaryDirectory();
        var good = Encoding.UTF8.GetBytes("valid");
        var bad = Encoding.UTF8.GetBytes("bad");
        var json = "[" + string.Join(',',
            VersionJson("alpha", "alpha", "2025-01-04T00:00:00Z", ["1.20.4"], ["fabric"], [FileJson("dev.jar", bad, "dev-jar")]),
            VersionJson("wrong-game", "release", "2025-01-05T00:00:00Z", ["1.20.5"], ["fabric"], [FileJson("game.jar", good)]),
            VersionJson("wrong-loader", "release", "2025-01-06T00:00:00Z", ["1.20.4"], ["forge"], [FileJson("loader.jar", good)]),
            VersionJson("beta", "beta", "2025-01-07T00:00:00Z", ["1.20.4"], ["fabric"], [FileJson("beta.jar", good)]),
            VersionJson("release-old", "release", "2025-01-02T00:00:00Z", ["1.20.4"], ["fabric"], [FileJson("first.jar", good), FileJson("primary.jar", good, primary: true)]),
            VersionJson("release-new", "release", "2025-01-08T00:00:00Z", ["1.20.4"], ["fabric"], [FileJson("release.jar", good)])
        ) + "]";
        using var client = new ModrinthClient(new HttpClient(new CallbackHttpHandler((request, _) =>
        {
            Assert.IsTrue(request.RequestUri!.AbsolutePath.EndsWith("/project/project123/version", StringComparison.Ordinal));
            return Task.FromResult(TestData.Json(HttpStatusCode.OK, json));
        })), ApiBase, cacheRoot: temporary.Root);

        var versions = await client.GetCompatibleVersionsAsync("project123", ModrinthContentKind.Mod, "1.20.4");

        CollectionAssert.AreEqual(new[] { "release-new", "release-old", "beta" }, versions.Select(version => version.Id).ToArray());
        Assert.AreEqual("primary.jar", versions[1].InstallableFile(ModrinthContentKind.Mod)!.Filename);
        Assert.AreEqual("beta", (await client.GetLatestVersionAsync("project123", ModrinthContentKind.Mod, "1.20.4", "beta"))!.Id);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.GetLatestVersionAsync(
            "project123", ModrinthContentKind.Mod, "1.20.4", "unknown"));
    }

    [TestMethod]
    public async Task VersionDependenciesDecodeAndNonInstallableJarVariantsAreExcluded()
    {
        using var temporary = new TemporaryDirectory();
        var bytes = Encoding.UTF8.GetBytes("dependency metadata");
        var json = VersionJson("version123", "release", "2025-01-01T00:00:00Z", ["1.20.4"], ["fabric"],
            [FileJson("valid.jar", bytes)])[..^1]
            + ",\"dependencies\":[{\"version_id\":\"dep123\",\"project_id\":null,\"dependency_type\":\"required\"}]}";
        using var client = new ModrinthClient(new HttpClient(new CallbackHttpHandler((_, _) =>
            Task.FromResult(TestData.Json(HttpStatusCode.OK, "[" + json + "]")))), ApiBase, cacheRoot: temporary.Root);

        var version = (await client.GetVersionsAsync(["version123"])).Single();
        Assert.AreEqual("dep123", version.Dependencies.Single().VersionId);
        Assert.AreEqual("required", version.Dependencies.Single().Type);
        Assert.IsNull(version.Dependencies.Single().ProjectId);
        Assert.AreEqual("valid.jar", version.InstallableFile(ModrinthContentKind.Mod)!.Filename);

        foreach (var name in new[] { "library-sources.jar", "library-dev.jar", "library-javadoc.jar", "CON.jar", "../escape.jar" })
        {
            var file = new ModrinthFile
            {
                Filename = name,
                Size = bytes.Length,
                Hashes = new Dictionary<string, string> { ["sha1"] = Hash1(bytes), ["sha512"] = Hash(bytes) }
            };
            Assert.IsFalse(file.IsInstallable(ModrinthContentKind.Mod), name);
        }
    }

    [TestMethod]
    public async Task ProjectsVersionsAndHashEndpointsValidateIdsAndPostSha512()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Root);
        var requests = new List<(string Method, string Path, string? Body)>();
        using var client = new ModrinthClient(new HttpClient(new CallbackHttpHandler(async (request, token) =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
            requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body));
            return request.RequestUri!.AbsolutePath switch
            {
                "/v2/projects" => TestData.Json(HttpStatusCode.OK, "[{\"id\":\"project123\",\"title\":\"Project\",\"project_type\":\"mod\"}]"),
                "/v2/versions" => TestData.Json(HttpStatusCode.OK, "[]"),
                "/v2/version_files" => TestData.Json(HttpStatusCode.OK, "{}"),
                _ => throw new AssertFailedException("Unexpected request: " + request.RequestUri)
            };
        })), ApiBase, cacheRoot: temporary.Root);

        var project = (await client.GetProjectsAsync(["project123"])).Single();
        Assert.AreEqual("project123", project.Id);
        Assert.AreEqual("https://modrinth.com/mod/project123", project.PageUrl.AbsoluteUri);
        Assert.AreEqual(0, (await client.GetVersionsAsync(["version123"])).Count);
        await Assert.ThrowsExactlyAsync<MojangException>(() => client.GetProjectsAsync(["bad/id"]));

        var path = Path.Combine(temporary.Root, "local.jar");
        var bytes = Encoding.UTF8.GetBytes("local file");
        await File.WriteAllBytesAsync(path, bytes);
        await client.VersionsOfAsync([path]);

        Assert.AreEqual(3, requests.Count);
        Assert.AreEqual("GET", requests[0].Method);
        Assert.AreEqual("POST", requests[2].Method);
        using var bodyJson = JsonDocument.Parse(requests[2].Body!);
        Assert.AreEqual("sha512", bodyJson.RootElement.GetProperty("algorithm").GetString());
        Assert.AreEqual(Hash(bytes), bodyJson.RootElement.GetProperty("hashes")[0].GetString());
    }

    [TestMethod]
    public async Task HashRecognitionReusesCacheInvalidatesChangedFileAndPrunesRemovedFiles()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Root);
        var cacheRoot = Path.Combine(temporary.Root, "Cache");
        var file = Path.Combine(temporary.Root, "local.jar");
        var firstBytes = Encoding.UTF8.GetBytes("first");
        await File.WriteAllBytesAsync(file, firstBytes);
        var requestCount = 0;
        using var http = new HttpClient(new CallbackHttpHandler(async (request, token) =>
        {
            requestCount++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var hashes = body.RootElement.GetProperty("hashes").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var response = hashes.ToDictionary(hash => hash, hash => new
            {
                id = "version123", project_id = "project123", date_published = "2025-01-01T00:00:00Z"
            });
            return TestData.Json(HttpStatusCode.OK, JsonSerializer.Serialize(response));
        }));
        using (var client = new ModrinthClient(http, ApiBase, cacheRoot: cacheRoot))
        {
            var first = await client.VersionsOfAsync([file]);
            Assert.AreEqual("version123", first[file].VersionId);
            Assert.AreEqual(1, requestCount);
            Assert.AreEqual(1, (await client.VersionsOfAsync([file])).Count);
            Assert.AreEqual(1, requestCount);

            await File.WriteAllBytesAsync(file, Encoding.UTF8.GetBytes("second"));
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
            await client.VersionsOfAsync([file]);
            Assert.AreEqual(2, requestCount);

            File.Delete(file);
            await client.VersionsOfAsync([]);
        }

        var cacheFile = Path.Combine(cacheRoot, "Modrinth", "files.json");
        using var cache = JsonDocument.Parse(await File.ReadAllBytesAsync(cacheFile));
        Assert.AreEqual(0, cache.RootElement.GetProperty("files").EnumerateObject().Count());
        Assert.AreEqual(0, cache.RootElement.GetProperty("versions").EnumerateObject().Count());
    }

    [TestMethod]
    public async Task ClientsSharingRootReloadTheLatestSnapshotBeforeSaving()
    {
        using var temporary = new TemporaryDirectory();
        var cacheRoot = Path.Combine(temporary.Root, "Cache");
        var cacheFolder = Path.Combine(cacheRoot, "Modrinth");
        Directory.CreateDirectory(cacheFolder);
        await File.WriteAllTextAsync(Path.Combine(cacheFolder, "files.json"), "{\"files\":{},\"versions\":{}}");
        var x = Path.Combine(temporary.Root, "x.jar");
        var y = Path.Combine(temporary.Root, "y.jar");
        var xBytes = Encoding.UTF8.GetBytes("x");
        var yBytes = Encoding.UTF8.GetBytes("y");
        await File.WriteAllBytesAsync(x, xBytes);
        await File.WriteAllBytesAsync(y, yBytes);
        var requestCount = 0;
        using var http = new HttpClient(new CallbackHttpHandler(async (request, token) =>
        {
            Interlocked.Increment(ref requestCount);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var matches = body.RootElement.GetProperty("hashes").EnumerateArray().Select(value => value.GetString()!)
                .ToDictionary(hash => hash, hash => new
                {
                    id = "version123", project_id = "project123", date_published = "2025-01-01T00:00:00Z"
                });
            return TestData.Json(HttpStatusCode.OK, JsonSerializer.Serialize(matches));
        }));
        using var first = new ModrinthClient(http, ApiBase, cacheRoot: cacheRoot);
        using var second = new ModrinthClient(http, ApiBase, cacheRoot: cacheRoot);

        await first.VersionsOfAsync([]);
        await second.VersionsOfAsync([]);
        Assert.AreEqual(1, (await first.VersionsOfAsync([x])).Count);
        Assert.AreEqual(1, (await second.VersionsOfAsync([y])).Count);
        Assert.AreEqual(2, requestCount);

        using var reader = new ModrinthClient(http, ApiBase, cacheRoot: cacheRoot);
        var reread = await reader.VersionsOfAsync([x, y]);
        Assert.AreEqual(2, reread.Count);
        Assert.AreEqual(2, requestCount);
        using var cache = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(cacheFolder, "files.json")));
        Assert.AreEqual(2, cache.RootElement.GetProperty("files").EnumerateObject().Count());
        Assert.AreEqual(2, cache.RootElement.GetProperty("versions").EnumerateObject().Count());
    }

    [TestMethod]
    public async Task ConcurrentClientsShareCacheUpdatesAndDifferentRootsDoNotBlockEachOther()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Root);
        var sharedRoot = Path.Combine(temporary.Root, "Shared");
        var firstRoot = Path.Combine(temporary.Root, "First");
        var secondRoot = Path.Combine(temporary.Root, "Second");
        var files = Enumerable.Range(0, 4).Select(index => Path.Combine(temporary.Root, $"file-{index}.jar")).ToArray();
        var bytes = Enumerable.Range(0, files.Length).Select(index => Encoding.UTF8.GetBytes($"file-{index}")).ToArray();
        for (var index = 0; index < files.Length; index++) await File.WriteAllBytesAsync(files[index], bytes[index]);

        var requestCount = 0;
        using var http = new HttpClient(new CallbackHttpHandler(async (request, token) =>
        {
            Interlocked.Increment(ref requestCount);
            await Task.Yield();
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var matches = body.RootElement.GetProperty("hashes").EnumerateArray().Select(value => value.GetString()!)
                .ToDictionary(hash => hash, hash => new
                {
                    id = "version123", project_id = "project123", date_published = "2025-01-01T00:00:00Z"
                });
            return TestData.Json(HttpStatusCode.OK, JsonSerializer.Serialize(matches));
        }));
        using var first = new ModrinthClient(http, ApiBase, cacheRoot: sharedRoot);
        using var second = new ModrinthClient(http, ApiBase, cacheRoot: sharedRoot);

        await Task.WhenAll(first.VersionsOfAsync([files[0]]), second.VersionsOfAsync([files[1]]));
        using var sharedReader = new ModrinthClient(http, ApiBase, cacheRoot: sharedRoot);
        Assert.AreEqual(2, (await sharedReader.VersionsOfAsync([files[0], files[1]])).Count);
        Assert.AreEqual(2, requestCount);

        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponses = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var independentHttp = new HttpClient(new CallbackHttpHandler(async (request, token) =>
        {
            if (Interlocked.Increment(ref requestCount) == 3) firstEntered.TrySetResult();
            if (Volatile.Read(ref requestCount) == 4) bothEntered.TrySetResult();
            await releaseResponses.Task.WaitAsync(token);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var matches = body.RootElement.GetProperty("hashes").EnumerateArray().Select(value => value.GetString()!)
                .ToDictionary(hash => hash, hash => new
                {
                    id = "version123", project_id = "project123", date_published = "2025-01-01T00:00:00Z"
                });
            return TestData.Json(HttpStatusCode.OK, JsonSerializer.Serialize(matches));
        }));
        using var firstRootClient = new ModrinthClient(independentHttp, ApiBase, cacheRoot: firstRoot);
        using var secondRootClient = new ModrinthClient(independentHttp, ApiBase, cacheRoot: secondRoot);
        var firstRootTask = firstRootClient.VersionsOfAsync([files[2]]);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondRootTask = secondRootClient.VersionsOfAsync([files[3]]);
        var bothRootsEntered = false;
        try
        {
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            bothRootsEntered = true;
        }
        finally
        {
            releaseResponses.TrySetResult();
        }

        await Task.WhenAll(firstRootTask, secondRootTask);
        Assert.IsTrue(bothRootsEntered, "Independent cache roots should not share a cache gate.");
        Assert.AreEqual(4, requestCount);
    }

    [TestMethod]
    public async Task MalformedHashCacheIsIgnoredAndReplacedAfterSuccessfulLookup()
    {
        using var temporary = new TemporaryDirectory();
        var cacheRoot = Path.Combine(temporary.Root, "Cache");
        var cacheFolder = Path.Combine(cacheRoot, "Modrinth");
        Directory.CreateDirectory(cacheFolder);
        await File.WriteAllTextAsync(Path.Combine(cacheFolder, "files.json"), "{broken");
        var bytes = Encoding.UTF8.GetBytes("sample");
        var file = Path.Combine(temporary.Root, "sample.jar");
        await File.WriteAllBytesAsync(file, bytes);
        var clientRequests = 0;
        using var client = new ModrinthClient(new HttpClient(new CallbackHttpHandler((_, _) =>
        {
            clientRequests++;
            var response = $"{{\"{Hash(bytes)}\":{{\"id\":\"version123\",\"project_id\":\"project123\",\"date_published\":\"2025-01-01T00:00:00Z\"}}}}";
            return Task.FromResult(TestData.Json(HttpStatusCode.OK, response));
        })), ApiBase, cacheRoot: cacheRoot);

        Assert.AreEqual(1, (await client.VersionsOfAsync([file])).Count);
        Assert.AreEqual(1, clientRequests);
        using var cache = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(cacheFolder, "files.json")));
        Assert.AreEqual(1, cache.RootElement.GetProperty("files").EnumerateObject().Count());

        await File.WriteAllTextAsync(Path.Combine(cacheFolder, "files.json"), "{\"files\":{\"C:\\\\BAD.JAR\":null},\"versions\":{}}");
        using var secondClient = new ModrinthClient(new HttpClient(new CallbackHttpHandler((_, _) =>
        {
            clientRequests++;
            var response = $"{{\"{Hash(bytes)}\":{{\"id\":\"version123\",\"project_id\":\"project123\",\"date_published\":\"2025-01-01T00:00:00Z\"}}}}";
            return Task.FromResult(TestData.Json(HttpStatusCode.OK, response));
        })), ApiBase, cacheRoot: cacheRoot);
        Assert.AreEqual(1, (await secondClient.VersionsOfAsync([file])).Count);
        Assert.AreEqual(2, clientRequests);
    }

    [TestMethod]
    public async Task UnknownHashesAreRememberedOnlyForTheCurrentClient()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Root);
        var file = Path.Combine(temporary.Root, "unknown.jar");
        await File.WriteAllTextAsync(file, "unknown");
        var requests = 0;
        using var http = new HttpClient(new CallbackHttpHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(TestData.Json(HttpStatusCode.OK, "{}"));
        }));
        using (var first = new ModrinthClient(http, ApiBase, cacheRoot: Path.Combine(temporary.Root, "Cache")))
        {
            Assert.AreEqual(0, (await first.VersionsOfAsync([file])).Count);
            Assert.AreEqual(0, (await first.VersionsOfAsync([file])).Count);
        }

        using var second = new ModrinthClient(http, ApiBase, cacheRoot: Path.Combine(temporary.Root, "Cache"));
        await second.VersionsOfAsync([file]);
        Assert.AreEqual(2, requests);
    }

    [TestMethod]
    public async Task UpdateLookupRequestsReleaseThenBetaThenAlphaAndChecksActualCompatibility()
    {
        using var temporary = new TemporaryDirectory();
        var payloads = new List<JsonDocument>();
        using var client = new ModrinthClient(new HttpClient(new CallbackHttpHandler(async (request, token) =>
        {
            payloads.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)));
            var channel = payloads[^1].RootElement.GetProperty("version_types")[0].GetString();
            var version = channel == "alpha"
                ? VersionJson("alpha123", "alpha", "2025-01-03T00:00:00Z", ["1.20.4"], ["fabric"], [FileJson("mod.jar", Encoding.UTF8.GetBytes("ok"))])
                : VersionJson("wrong123", channel!, "2025-01-02T00:00:00Z", ["1.20.5"], ["fabric"], [FileJson("mod.jar", Encoding.UTF8.GetBytes("ok"))]);
            var response = channel == "alpha" ? "{" + JsonSerializer.Serialize(Hash(Encoding.UTF8.GetBytes("mod"))) + ":" + version + "}" : "{}";
            return TestData.Json(HttpStatusCode.OK, response);
        })), ApiBase, cacheRoot: temporary.Root);

        var hash = Hash(Encoding.UTF8.GetBytes("mod"));
        var result = await client.LatestVersionsAsync([hash], ModrinthContentKind.Mod, "1.20.4");

        CollectionAssert.AreEqual(new[] { "release", "beta", "alpha" }, payloads.Select(document =>
            document.RootElement.GetProperty("version_types")[0].GetString()!).ToArray());
        Assert.AreEqual("alpha123", result[hash].Id);
        foreach (var payload in payloads) payload.Dispose();
    }

    [TestMethod]
    public async Task DownloadRejectsWrongSizeAndHashesAndRemovesStagingFiles()
    {
        using var temporary = new TemporaryDirectory();
        var bytes = Encoding.UTF8.GetBytes("payload");
        var handler = new CallbackHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        }));
        using var client = new ModrinthClient(new HttpClient(handler), ApiBase, cacheRoot: temporary.Root);
        var destination = Path.Combine(temporary.Root, "download.jar");

        var wrongHash = await Assert.ThrowsExactlyAsync<MojangException>(() => client.DownloadFileAsync(
            new Uri("https://cdn.test/mod.jar"), destination, bytes.Length, Hash1(Encoding.UTF8.GetBytes("wrong")), Hash(bytes)));
        Assert.AreEqual(MojangError.Integrity, wrongHash.Error, wrongHash.Message);
        Assert.IsFalse(File.Exists(destination));
        Assert.AreEqual(0, Directory.GetFiles(temporary.Root, "*.tmp").Length);

        var wrongSize = await Assert.ThrowsExactlyAsync<MojangException>(() => client.DownloadFileAsync(
            new Uri("https://cdn.test/mod.jar"), destination, bytes.Length + 1, Hash1(bytes), Hash(bytes)));
        Assert.AreEqual(MojangError.Integrity, wrongSize.Error);
        Assert.AreEqual(0, Directory.GetFiles(temporary.Root, "*.tmp").Length);
    }

    [TestMethod]
    public async Task DownloadCancellationCleansPartialFile()
    {
        using var temporary = new TemporaryDirectory();
        var body = new BlockingContent();
        using var client = new ModrinthClient(new HttpClient(new CallbackHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = body }))), ApiBase, cacheRoot: temporary.Root);
        using var cancellation = new CancellationTokenSource();
        var destination = Path.Combine(temporary.Root, "cancelled.jar");
        var task = client.DownloadFileAsync(new Uri("https://cdn.test/mod.jar"), destination, 10,
            new string('0', 40), new string('0', 128), cancellation.Token);
        await body.ReadStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task);
        Assert.IsFalse(File.Exists(destination));
        Assert.AreEqual(0, Directory.GetFiles(temporary.Root, "*.tmp").Length);
    }

    private static string VersionJson(string id, string channel, string date, string[] games, string[] loaders, string[] files) =>
        $$"""{"id":"{{id}}","project_id":"project123","version_number":"{{id}}","version_type":"{{channel}}","date_published":"{{date}}","game_versions":{{JsonSerializer.Serialize(games)}},"loaders":{{JsonSerializer.Serialize(loaders)}},"files":[{{string.Join(',', files)}}]}""";

    private static string FileJson(string filename, byte[] bytes, string? fileType = null, bool primary = false) =>
        $$"""{"filename":"{{filename}}","url":"https://cdn.test/{{filename}}","size":{{bytes.Length}},"hashes":{"sha1":"{{Hash1(bytes)}}","sha512":"{{Hash(bytes)}}"},"primary":{{primary.ToString().ToLowerInvariant()}}{{(fileType is null ? "" : $",\"file_type\":\"{fileType}\"")}}}""";

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();
    private static string Hash1(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    private sealed class BlockingContent : HttpContent
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new BlockingStream(ReadStarted));
    }

    private sealed class BlockingStream(TaskCompletionSource readStarted) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}

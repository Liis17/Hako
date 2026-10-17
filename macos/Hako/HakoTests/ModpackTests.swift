import CryptoKit
import Foundation
import SwiftData
import Testing

@Suite(.serialized) @MainActor struct ModpackTests {
    static func temporary() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }
    static func sha(_ bytes: Data) -> String { Insecure.SHA1.hash(data: bytes).map { String(format: "%02x", $0) }.joined() }
    static func manifest(files: [ModpackManifest.File] = [], dependencies: [String: String] = ["minecraft": "test"]) -> ModpackManifest {
        .init(formatVersion: 1, game: "minecraft", versionId: "v1", name: "Test Pack", files: files, dependencies: dependencies)
    }
    static func file(_ path: String, bytes: Data = Data("mod".utf8), client: String = "required", downloads: [URL]? = nil) -> ModpackManifest.File {
        .init(path: path, hashes: ["sha1": sha(bytes), "sha512": FabricClient.hash(bytes)], env: .init(client: client, server: "optional"), downloads: downloads ?? [URL(string: "https://cdn.modrinth.com/\(path)")!], fileSize: Int64(bytes.count))
    }
    static func archive(in root: URL, manifest: ModpackManifest, entries: [String: Data] = [:]) async throws -> URL {
        let source = root.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: source, withIntermediateDirectories: true)
        try JSONEncoder().encode(manifest).write(to: source.appendingPathComponent("modrinth.index.json"))
        for (path, bytes) in entries {
            let target = source.appendingPathComponent(path)
            try FileManager.default.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
            try bytes.write(to: target)
        }
        let archive = root.appendingPathComponent(UUID().uuidString + ".mrpack")
        try await InstanceBackup.zip(["-q", "-r", archive.path, "."], in: source)
        return archive
    }
    static func version(_ id: String, loader: String = "fabric", channel: String = "release", game: String = "test", filename: String = "pack.mrpack") throws -> ModrinthVersion {
        try JSONDecoder().decode(ModrinthVersion.self, from: JSONSerialization.data(withJSONObject: [
            "id": id, "project_id": "project", "version_number": id, "version_type": channel, "date_published": "2026-01-01", "game_versions": [game], "loaders": [loader],
            "files": [["filename": filename, "url": "https://cdn.modrinth.com/pack.mrpack", "size": 3, "hashes": ["sha1": sha(Data("mod".utf8)), "sha512": FabricClient.hash(Data("mod".utf8))], "primary": true]]
        ]))
    }

    static func plan(archive: URL, manifest: ModpackManifest) throws -> ModpackPlan {
        let bytes = try Data(contentsOf: archive)
        let file = ModrinthVersion.File(filename: "pack.mrpack", url: URL(string: "https://cdn.modrinth.com/pack.mrpack")!, size: Int64(bytes.count), hashes: ["sha1": sha(bytes), "sha512": FabricClient.hash(bytes)], primary: true, fileType: nil)
        return .init(projectID: "project", versionID: "v1", versionNumber: "1.0", pageURL: URL(string: "https://modrinth.com/modpack/project")!, archive: file, manifest: manifest)
    }

    static func minecraftRoutes() throws -> (MinecraftVersion, [String: Data]) {
        let bytes = Data("abc".utf8)
        func download(_ path: String, bytes: Data) -> [String: Any] { ["url": "https://cdn.modrinth.com/" + path, "sha1": sha(bytes), "size": bytes.count] }
        let runtime = try JSONSerialization.data(withJSONObject: ["files": ["bin/java": ["type": "file", "executable": true, "downloads": ["raw": download("java", bytes: bytes)]]]])
        let package: [String: Any] = ["manifest": download("runtime", bytes: runtime), "version": ["name": "17.0.1", "released": "2025-01-01"]]
        let runtimes = try JSONSerialization.data(withJSONObject: ["mac-os-arm64": ["java-runtime-gamma": [package]], "mac-os": ["java-runtime-gamma": [package]]])
        let metadata = try JSONSerialization.data(withJSONObject: ["id": "test", "downloads": ["client": download("client", bytes: bytes)], "libraries": [], "javaVersion": ["component": "java-runtime-gamma", "majorVersion": 17], "mainClass": "net.minecraft.client.main.Main", "arguments": ["game": [], "jvm": []]])
        let version = MinecraftVersion(id: "test", type: "release", url: URL(string: "https://cdn.modrinth.com/metadata")!, sha1: sha(metadata))
        let catalog = try JSONSerialization.data(withJSONObject: ["latest": ["release": "test"], "versions": [["id": "test", "type": "release", "url": version.url.absoluteString, "sha1": version.sha1]]])
        return (version, [MojangClient.catalogURL.path: catalog, MojangClient.runtimeURL.path: runtimes, "/metadata": metadata, "/runtime": runtime, "/client": bytes, "/java": bytes])
    }

    @Test func searchUsesOptionalMinecraftAndSupportedLoaderFacets() async throws {
        let session = ModpackTestProtocol.session(); defer { session.invalidateAndCancel() }
        ModpackTestProtocol.prepare(["/v2/search": Data(#"{"hits":[],"total_hits":0}"#.utf8)])
        let client = ModrinthClient(session: session)
        _ = try await client.searchModpacks("c++", minecraft: nil, sort: .downloads, offset: 20)
        _ = try await client.searchModpacks(" ", minecraft: "1.21.1", sort: .updated, offset: 0)
        let requests = ModpackTestProtocol.requests
        let first = try #require(URLComponents(url: requests[0].url!, resolvingAgainstBaseURL: false))
        let second = try #require(URLComponents(url: requests[1].url!, resolvingAgainstBaseURL: false))
        let facets = try JSONDecoder().decode([[String]].self, from: Data(try #require(first.queryItems?.first { $0.name == "facets" }?.value).utf8))
        #expect(facets == [["project_type:modpack"], ["categories:fabric", "categories:minecraft"], ["environment!=dedicated_server_only"]])
        #expect(first.percentEncodedQuery?.contains("c%2B%2B") == true)
        #expect(first.queryItems?.first { $0.name == "offset" }?.value == "20")
        #expect(second.queryItems?.first { $0.name == "query" } == nil)
        #expect(second.queryItems?.first { $0.name == "facets" }?.value?.contains("versions:1.21.1") == true)
    }

    @Test func versionsRejectUnsupportedLoadersAndFilesAndPreferRelease() async throws {
        let session = ModpackTestProtocol.session(); defer { session.invalidateAndCancel() }
        let versions = try [Self.version("beta", channel: "beta"), Self.version("release"), Self.version("forge", loader: "forge"), Self.version("other", game: "other"), Self.version("jar", filename: "mod.jar")]
        // Preserve API wire fields without making the application version model Encodable.
        let data = try JSONSerialization.data(withJSONObject: versions.map { value in
            ["id": value.id, "project_id": value.projectID, "version_number": value.number, "version_type": value.channel, "date_published": value.published, "game_versions": value.gameVersions, "loaders": value.loaders, "files": try JSONSerialization.jsonObject(with: JSONEncoder().encode(value.files))] as [String: Any]
        })
        ModpackTestProtocol.prepare(["/v2/project/project/version": data])
        #expect(try await ModrinthClient(session: session).modpackVersions(project: "project", minecraft: "test").map(\.id) == ["release", "beta"])
    }

    @Test func manifestRejectsUnknownDependenciesUnsafePathsAndCollisions() throws {
        for dependencies in [["minecraft": "test", "forge": "1"], ["minecraft": "test", "neoforge": "1"], ["minecraft": "test", "quilt-loader": "1"], ["fabric-loader": "1"]] {
            #expect(throws: ModpackError.self) { try Self.manifest(dependencies: dependencies).validate() }
        }
        for path in ["../escape", "/escape", "mods/../../escape", "C:/escape", "mods\\escape", "mods/.hako-mods.json", "versions/client.jar", "mods/*", "mods/a\nb"] {
            #expect(throws: ModpackError.self) { try Self.manifest(files: [Self.file(path, downloads: [URL(string: "https://cdn.modrinth.com/file")!])]).validate() }
        }
        for paths in [["mods/a.jar", "mods/A.jar"], ["mods/a", "mods/a/b"], ["Mods/a.jar", "mods/b.jar"]] {
            #expect(throws: ModpackError.self) { try Self.manifest(files: paths.map { Self.file($0) }).validate() }
        }
        #expect(throws: ModpackError.self) { try Self.manifest(files: [Self.file("mods/a.jar", downloads: [URL(string: "https://untrusted.test/a")!])]).validate() }
        try Self.manifest(files: [Self.file("mods/a.jar", client: "optional")], dependencies: ["minecraft": "test", "fabric-loader": "0.19.5"]).validate()
    }

    @Test func archiveReadsClientOverridesAndProtectsGameFiles() async throws {
        let root = try Self.temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let archive = try await Self.archive(in: root, manifest: Self.manifest(), entries: ["overrides/config/a.json": Data("common".utf8), "client-overrides/config/a.json": Data("client".utf8), "server-overrides/config/server.json": Data()])
        let contents = try await ModpackArchive.inspect(archive)
        #expect(contents.overrides == ["config/a.json": "client-overrides/config/a.json"])
        let target = root.appendingPathComponent("extracted")
        try await ModpackArchive.extract("client-overrides/config/a.json", in: archive, to: target)
        #expect(try Data(contentsOf: target) == Data("client".utf8))
        let unsafe = try await Self.archive(in: root, manifest: Self.manifest(), entries: ["overrides/libraries/a.jar": Data()])
        await #expect(throws: ModpackError.self) { try await ModpackArchive.inspect(unsafe) }
    }
    @Test func installIncludesOptionalFilesAndClientOverridesAndPreservesEditsOnRetry() async throws {
        let root = try Self.temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModpackTestProtocol.session(); defer { session.invalidateAndCancel() }
        let manifest = Self.manifest(files: [Self.file("mods/a.jar"), Self.file("mods/b.jar", client: "optional"), Self.file("mods/server.jar", client: "unsupported")])
        let archive = try await Self.archive(in: root, manifest: manifest, entries: ["overrides/config/a.json": Data("common".utf8), "client-overrides/config/a.json": Data("client".utf8), "server-overrides/config/server.json": Data()])
        let plan = try Self.plan(archive: archive, manifest: manifest)
        let instance = root.appendingPathComponent("instance")
        try FileManager.default.createDirectory(at: instance.appendingPathComponent("minecraft"), withIntermediateDirectories: true)
        try plan.write(in: instance)
        ModpackTestProtocol.prepare(["/pack.mrpack": try Data(contentsOf: archive), "/mods/a.jar": Data("mod".utf8), "/mods/b.jar": Data("mod".utf8)])
        let installer = ModpackInstaller(session: session, cache: root.appendingPathComponent("cache"))
        try await installer.install(in: instance) { _ in }
        #expect(try ModpackPlan.read(in: instance)?.completed == true)
        #expect(try Data(contentsOf: instance.appendingPathComponent("minecraft/mods/b.jar")) == Data("mod".utf8))
        #expect(try Data(contentsOf: instance.appendingPathComponent("minecraft/config/a.json")) == Data("client".utf8))
        #expect(!FileManager.default.fileExists(atPath: instance.appendingPathComponent("minecraft/mods/server.jar").path))
        #expect(!FileManager.default.fileExists(atPath: instance.appendingPathComponent("minecraft/config/server.json").path))
        try FileManager.default.removeItem(at: instance.appendingPathComponent("minecraft/mods/a.jar"))
        try Data("edited".utf8).write(to: instance.appendingPathComponent("minecraft/config/a.json"))
        ModpackTestProtocol.prepare([:])
        try await installer.install(in: instance) { _ in }
        #expect(ModpackTestProtocol.requests.isEmpty)
        #expect(!FileManager.default.fileExists(atPath: instance.appendingPathComponent("minecraft/mods/a.jar").path))
        #expect(try Data(contentsOf: instance.appendingPathComponent("minecraft/config/a.json")) == Data("edited".utf8))
    }

    @Test func failedDownloadPublishesNothingAndCanRetryWithAnotherURL() async throws {
        let root = try Self.temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModpackTestProtocol.session(); defer { session.invalidateAndCancel() }
        let manifest = Self.manifest(files: [Self.file("mods/a.jar", downloads: [URL(string: "https://cdn.modrinth.com/bad")!, URL(string: "https://cdn.modrinth.com/good")!])])
        let archive = try await Self.archive(in: root, manifest: manifest, entries: ["overrides/options.txt": Data("options".utf8)])
        let plan = try Self.plan(archive: archive, manifest: manifest)
        let instance = root.appendingPathComponent("instance")
        try FileManager.default.createDirectory(at: instance, withIntermediateDirectories: true)
        try plan.write(in: instance)
        ModpackTestProtocol.prepare(["/pack.mrpack": try Data(contentsOf: archive), "/bad": Data("bad".utf8)])
        let installer = ModpackInstaller(session: session, cache: root.appendingPathComponent("cache"))
        await #expect(throws: ModpackError.self) { try await installer.install(in: instance) { _ in } }
        #expect(try ModpackPlan.read(in: instance)?.completed == false)
        #expect(!FileManager.default.fileExists(atPath: instance.appendingPathComponent("minecraft/options.txt").path))
        ModpackTestProtocol.prepare(["/good": Data("mod".utf8)])
        try await installer.install(in: instance) { _ in }
        #expect(try ModpackPlan.read(in: instance)?.completed == true)
        #expect(ModpackTestProtocol.requests.map { $0.url!.path } == ["/bad", "/good"])
    }

    @Test func preparationPinsMinecraftAndFabricWithoutRequestingFabricAPI() async throws {
        let root = try Self.temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModpackTestProtocol.session(); defer { session.invalidateAndCancel() }
        let manifest = Self.manifest(dependencies: ["minecraft": "test", "fabric-loader": "0.19.5"])
        let archive = try await Self.archive(in: root, manifest: manifest)
        let plan = try Self.plan(archive: archive, manifest: manifest)
        var (_, routes) = try Self.minecraftRoutes()
        routes["/pack.mrpack"] = try Data(contentsOf: archive)
        routes["/v2/versions/loader/test"] = Data(#"[{"loader":{"version":"0.19.5","stable":true}}]"#.utf8)
        routes["/v2/versions/loader/test/0.19.5/profile/json"] = Data(#"{"id":"fabric-test","inheritsFrom":"test","mainClass":"KnotClient","libraries":[]}"#.utf8)
        ModpackTestProtocol.prepare(routes)
        let project = try JSONDecoder().decode(ModrinthProject.self, from: Data(#"{"id":"project","title":"Pack","project_type":"modpack"}"#.utf8))
        let version = ModrinthVersion(id: "v1", projectID: "project", number: "1.0", channel: "release", published: "2026-01-01", gameVersions: ["test"], loaders: ["fabric"], files: [plan.archive], dependencies: nil)
        let installer = ModpackInstaller(session: session, cache: root.appendingPathComponent("cache"))
        let prepared = try await installer.prepare(project: project, version: version, minecraft: MojangClient(session: session), fabric: FabricClient(session: session))
        #expect(prepared.minecraft.version.id == "test")
        #expect(prepared.plan.manifest.loaderVersion == "0.19.5")
        let gameInstaller = MinecraftInstaller(client: MojangClient(session: session), session: session, fabricClient: FabricClient(session: session))
        let result = try await gameInstaller.install(prepared.minecraft.version, at: root.appendingPathComponent("game"), fabric: .init(loaderVersion: "0.19.5", api: nil)) { _ in }
        #expect(result.fabricProfileSHA1 != nil)
        #expect(!ModpackTestProtocol.requests.contains { $0.url!.path.contains("P7dR8mSH") })
    }

    @Test func queueInstallsVanillaPackAndCreationRollsBackOnPersistenceFailure() async throws {
        let root = try Self.temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModpackTestProtocol.session(); defer { session.invalidateAndCancel() }
        let manifest = Self.manifest()
        let archive = try await Self.archive(in: root, manifest: manifest, entries: ["overrides/options.txt": Data("options".utf8)])
        let plan = try Self.plan(archive: archive, manifest: manifest)
        let (version, initialRoutes) = try Self.minecraftRoutes()
        var routes = initialRoutes; routes["/pack.mrpack"] = try Data(contentsOf: archive)
        ModpackTestProtocol.prepare(routes)
        let container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let minecraft = MojangClient(session: session)
        let modpacks = ModpackInstaller(session: session, cache: root.appendingPathComponent("cache"))
        let coordinator = InstallationCoordinator(context: container.mainContext, storage: .init(root: root.appendingPathComponent("instances")), client: minecraft, installer: MinecraftInstaller(client: minecraft, session: session), modpacks: modpacks)
        var draft = InstanceDraft(); draft.name = "Pack"
        let instance = try coordinator.store.create(draft, versionID: version.id, metadataURL: version.url.absoluteString, metadataSHA1: version.sha1, modpack: plan)
        coordinator.start()
        for _ in 0..<500 where instance.state == .queued || instance.state == .installing { try await Task.sleep(for: .milliseconds(10)) }
        #expect(instance.state == .ready, "\(instance.installationError ?? "")")
        #expect(try Data(contentsOf: root.appendingPathComponent("instances/Pack/minecraft/options.txt")) == Data("options".utf8))
        let copy = try await coordinator.store.duplicate(instance)
        let copyRoot = try coordinator.store.storage.directory(copy.folderName)
        #expect(try ModpackPlan.read(in: copyRoot)?.completed == true)
        let backup = try await coordinator.store.backup(instance, content: coordinator.content)
        let data = try await FabricClient.archiveEntry(ModpackPlan.filename, in: backup, limit: 1_048_576)
        #expect(try JSONDecoder().decode(ModpackPlan.self, from: data).completed)
        let failing = InstanceStore(context: container.mainContext, storage: coordinator.store.storage, persist: { throw ModpackError.archive })
        draft.name = "Failed"
        #expect(throws: ModpackError.self) { try failing.create(draft, versionID: version.id, metadataURL: version.url.absoluteString, metadataSHA1: version.sha1, modpack: plan) }
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("instances/Failed").path))
    }

    @Test func cancellationKeepsPlanPendingAndRestartReusesVerifiedDownloads() async throws {
        let root = try Self.temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModpackTestProtocol.session(); defer { session.invalidateAndCancel() }
        let manifest = Self.manifest(files: [Self.file("mods/a.jar")])
        let archive = try await Self.archive(in: root, manifest: manifest)
        let plan = try Self.plan(archive: archive, manifest: manifest)
        let instance = root.appendingPathComponent("instance")
        try FileManager.default.createDirectory(at: instance, withIntermediateDirectories: true)
        try plan.write(in: instance)
        ModpackTestProtocol.prepare(["/pack.mrpack": try Data(contentsOf: archive), "/mods/a.jar": Data("mod".utf8)])
        let cache = root.appendingPathComponent("cache")
        let installer = ModpackInstaller(session: session, cache: cache)
        let task = Task {
            try await installer.install(in: instance) { value in
                if value.completedBytes > 0 { withUnsafeCurrentTask { $0?.cancel() } }
            }
        }
        await #expect(throws: CancellationError.self) { try await task.value }
        #expect(try ModpackPlan.read(in: instance)?.completed == false)
        #expect(!FileManager.default.fileExists(atPath: instance.appendingPathComponent("minecraft/mods/a.jar").path))
        ModpackTestProtocol.prepare([:])
        try await ModpackInstaller(session: session, cache: cache).install(in: instance) { _ in }
        #expect(try ModpackPlan.read(in: instance)?.completed == true)
        #expect(ModpackTestProtocol.requests.isEmpty)
    }

    @Test func symlinkDestinationCannotOverwriteGameLibraries() async throws {
        let root = try Self.temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModpackTestProtocol.session(); defer { session.invalidateAndCancel() }
        let manifest = Self.manifest(files: [Self.file("mods/a.jar")])
        let archive = try await Self.archive(in: root, manifest: manifest)
        let plan = try Self.plan(archive: archive, manifest: manifest)
        let instance = root.appendingPathComponent("instance")
        let minecraft = instance.appendingPathComponent("minecraft")
        try FileManager.default.createDirectory(at: minecraft.appendingPathComponent("libraries"), withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(atPath: minecraft.appendingPathComponent("mods").path, withDestinationPath: "libraries")
        try plan.write(in: instance)
        ModpackTestProtocol.prepare(["/pack.mrpack": try Data(contentsOf: archive), "/mods/a.jar": Data("mod".utf8)])
        await #expect(throws: ModpackError.self) { try await ModpackInstaller(session: session, cache: root.appendingPathComponent("cache")).install(in: instance) { _ in } }
        #expect(!FileManager.default.fileExists(atPath: minecraft.appendingPathComponent("libraries/a.jar").path))
    }

    static func page(_ hits: [(String, String)], total: Int) throws -> Data {
        try JSONSerialization.data(withJSONObject: ["hits": hits.map { ["project_id": $0.0, "title": $0.0, "project_type": $0.1] }, "total_hits": total])
    }

    @Test func catalogPaginationUsesRawOffsetAndDeduplicatesRows() async throws {
        let session = ModpackTestProtocol.session(); defer { session.invalidateAndCancel() }
        let first = try Self.page([("a", "modpack"), ("ignored", "mod")], total: 4)
        let second = try Self.page([("a", "modpack"), ("b", "modpack")], total: 4)
        ModpackTestProtocol.prepare([:], handler: { url in
            let offset = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first { $0.name == "offset" }?.value
            return (offset == "0" ? first : second, 0)
        })
        let catalog = ModpackCatalog(client: ModrinthClient(session: session))
        await catalog.search(force: true)
        #expect(catalog.projects.map(\.id) == ["a"] && catalog.nextOffset == 2 && catalog.hasMore)
        await catalog.loadMore()
        #expect(catalog.projects.map(\.id) == ["a", "b"] && catalog.nextOffset == 4 && !catalog.hasMore)
        let offsets = ModpackTestProtocol.requests.map { URLComponents(url: $0.url!, resolvingAgainstBaseURL: false)?.queryItems?.first { $0.name == "offset" }?.value }
        #expect(offsets == ["0", "2"])
    }

    @Test func catalogIgnoresLateSearchAndLatePagination() async throws {
        let session = ModpackTestProtocol.session(); defer { session.invalidateAndCancel() }
        let old = try Self.page([("old", "modpack")], total: 2)
        let new = try Self.page([("new", "modpack")], total: 1)
        let (events, continuation) = AsyncStream<URLRequest>.makeStream()
        defer { continuation.finish() }
        ModpackTestProtocol.prepare([:], handler: { url in
            let query = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first { $0.name == "query" }?.value
            return (query == "old" ? old : new, query == "old" ? 0.15 : 0)
        }, onRequest: { continuation.yield($0) })
        var requests = events.makeAsyncIterator()
        let catalog = ModpackCatalog(client: ModrinthClient(session: session))
        catalog.query = "old"
        let stale = Task { await catalog.search(force: true) }
        _ = await requests.next()
        catalog.query = "new"
        await catalog.search(force: true)
        await stale.value
        #expect(catalog.projects.map(\.id) == ["new"])
        catalog.query = "old"
        await catalog.search(force: true)
        // Drain the two completed searches before awaiting the pagination request.
        _ = await requests.next(); _ = await requests.next()
        let stalePage = Task { await catalog.loadMore() }
        _ = await requests.next()
        catalog.query = "new"; catalog.minecraft = "1.21.1"; catalog.sort = .updated
        await catalog.search(force: true)
        await stalePage.value
        #expect(catalog.projects.map(\.id) == ["new"] && catalog.nextOffset == 1 && !catalog.loadingMore)
    }

}

nonisolated final class ModpackTestProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private nonisolated(unsafe) static var routes: [String: Data] = [:]
    private nonisolated(unsafe) static var recorded: [URLRequest] = []
    private nonisolated(unsafe) static var handler: (@Sendable (URL) -> (Data, TimeInterval))?
    private nonisolated(unsafe) static var onRequest: (@Sendable (URLRequest) -> Void)?
    private var work: DispatchWorkItem?
    static var requests: [URLRequest] { lock.withLock { recorded } }
    static func prepare(_ routes: [String: Data], handler: (@Sendable (URL) -> (Data, TimeInterval))? = nil, onRequest: (@Sendable (URLRequest) -> Void)? = nil) {
        lock.withLock { self.routes = routes; recorded = []; self.handler = handler; self.onRequest = onRequest }
    }
    static func session() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral; configuration.protocolClasses = [Self.self]
        return URLSession(configuration: configuration)
    }
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let url = request.url!
        let (data, handler, onRequest) = Self.lock.withLock {
            Self.recorded.append(request)
            return (Self.routes[url.path], Self.handler, Self.onRequest)
        }
        onRequest?(request)
        let custom = handler?(url)
        let bytes = custom?.0 ?? data
        let item = DispatchWorkItem { [self] in
            client?.urlProtocol(self, didReceive: HTTPURLResponse(url: url, statusCode: bytes == nil ? 404 : 200, httpVersion: nil, headerFields: nil)!, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: bytes ?? Data())
            client?.urlProtocolDidFinishLoading(self)
        }
        work = item
        DispatchQueue.global().asyncAfter(deadline: .now() + (custom?.1 ?? 0), execute: item)
    }
    override func stopLoading() { work?.cancel() }
}

import CryptoKit
import Foundation
import SwiftData
import Testing

@Suite(.serialized) @MainActor struct ModrinthTests {
    private let api = "api.modrinth.com/v2"

    @Test func searchUsesFacetsForModsAndPacksAndKeepsPlusInQuery() async throws {
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let page = #"{"hits":[{"project_id":"abc","slug":"abc","title":"A","description":"d","icon_url":"","project_type":"mod"}],"total_hits":1}"#
        ModrinthTestProtocol.prepare(["\(api)/search": Data(page.utf8)])
        let client = ModrinthClient(session: session)
        let result = try await client.search("c++", mods: true, minecraft: "1.21.1", sort: .downloads, offset: 20)
        #expect(result.total == 1 && result.hits.first?.iconURL == nil)
        #expect(result.hits.first?.pageURL.absoluteString == "https://modrinth.com/mod/abc")
        _ = try await client.search(" ", mods: false, minecraft: "1.21.1", sort: .relevance, offset: 0)
        let requests = ModrinthTestProtocol.requests.compactMap { URLComponents(url: $0.url!, resolvingAgainstBaseURL: false) }
        #expect(requests.count == 2)
        func item(_ components: URLComponents, _ name: String) -> String? { components.queryItems?.first { $0.name == name }?.value }
        func facets(_ components: URLComponents) throws -> [[String]] { try JSONDecoder().decode([[String]].self, from: Data(try #require(item(components, "facets")).utf8)) }
        #expect(try facets(requests[0]) == [["project_type:mod"], ["categories:fabric"], ["versions:1.21.1"], ["environment!=server_only"], ["environment!=dedicated_server_only"]])
        #expect(item(requests[0], "query") == "c++" && requests[0].percentEncodedQuery?.contains("c%2B%2B") == true)
        #expect(item(requests[0], "index") == "downloads" && item(requests[0], "offset") == "20" && item(requests[0], "limit") == "20")
        #expect(try facets(requests[1]) == [["project_type:resourcepack"], ["versions:1.21.1"]])
        #expect(item(requests[1], "query") == nil)
    }

    @Test func latestVersionPrefersReleaseAndSkipsForeignVersionsAndBrokenFiles() async throws {
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let bytes = Data("x".utf8)
        var broken = Self.version("broken", project: "root", date: "2026-10-09", file: ("broken.jar", bytes))
        broken["files"] = [["filename": "broken.jar", "url": "https://fixtures.test/broken.jar", "size": 1, "primary": true, "hashes": ["sha1": "a", "sha512": "b"]]]
        let versions = [
            Self.version("beta", project: "root", channel: "beta", date: "2026-10-08", file: ("beta.jar", bytes)),
            Self.version("release", project: "root", date: "2026-10-01", file: ("release.jar", bytes)),
            Self.version("other-minecraft", project: "root", date: "2026-10-07", minecraft: "1.20", file: ("other.jar", bytes)),
            Self.version("other-loader", project: "root", date: "2026-10-07", loader: "quilt", file: ("quilt.jar", bytes)),
            broken
        ]
        ModrinthTestProtocol.prepare(["\(api)/project/root/version": try JSONSerialization.data(withJSONObject: versions)])
        let client = ModrinthClient(session: session)
        #expect(try await client.latestVersion(project: "root", mods: true, minecraft: "test")?.id == "release")
        let packs = [Self.version("pack", project: "root", loader: "minecraft", file: ("pack.zip", bytes)), Self.version("jar", project: "root", loader: "minecraft", file: ("pack.jar", bytes))]
        ModrinthTestProtocol.prepare(["\(api)/project/root/version": try JSONSerialization.data(withJSONObject: packs)])
        #expect(try await client.latestVersion(project: "root", mods: false, minecraft: "test")?.id == "pack")
        await #expect(throws: MojangError.self) { try await client.latestVersion(project: "../root", mods: true, minecraft: "test") }
    }

    @Test func fileVersionsAreCachedOnDiskAndRefreshedAfterChange() async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let file = root.appendingPathComponent("mod.jar"), cache = root.appendingPathComponent("cache")
        try Data("one".utf8).write(to: file)
        let match = [FabricClient.hash(Data("one".utf8)): ["id": "v1", "project_id": "p1", "date_published": "2026-10-01"]]
        ModrinthTestProtocol.prepare(["\(api)/version_files": try JSONSerialization.data(withJSONObject: match)])
        var client = ModrinthClient(session: session, cache: cache)
        #expect(try await client.versions(of: [file])[file]?.projectID == "p1")
        _ = try await client.versions(of: [file])
        #expect(ModrinthTestProtocol.requests.count == 1)
        client = ModrinthClient(session: session, cache: cache)
        #expect(try await client.versions(of: [file])[file]?.versionID == "v1")
        #expect(ModrinthTestProtocol.requests.count == 1)
        try Data("changed".utf8).write(to: file)
        #expect(try await client.versions(of: [file])[file] == nil)
        _ = try await client.versions(of: [file])
        #expect(ModrinthTestProtocol.requests.count == 2)
    }

    @Test func missingDependencyIsOfferedAndInstalledWithOrigins() async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let (container, installations, controller, instance) = try Self.instance(root: root, session: session)
        _ = container
        let rootBytes = Data("root".utf8), depBytes = Data("dep".utf8)
        ModrinthTestProtocol.prepare([
            "\(api)/project/root/version": try JSONSerialization.data(withJSONObject: [Self.version("root-v", project: "root", file: ("root.jar", rootBytes), dependencies: [("dep", "required"), ("opt", "optional")])]),
            "\(api)/projects": Data(#"[{"id":"dep","slug":"dep","title":"Dep","description":"","icon_url":null,"project_type":"mod"}]"#.utf8),
            "\(api)/project/dep/version": try JSONSerialization.data(withJSONObject: [Self.version("dep-v", project: "dep", file: ("dep.jar", depBytes))]),
            "fixtures.test/root.jar": rootBytes, "fixtures.test/dep.jar": depBytes
        ])
        controller.install(try Self.project("root"), in: instance, mods: true)
        #expect(controller.catalogInstalling[instance.id] == "root")
        let confirmation = try await Self.confirmation(controller, installations, instance)
        #expect(confirmation.projects.map(\.title) == ["Dep"])
        #expect(confirmation.action == "Добавить с зависимостями" && confirmation.alternative == "Только мод")
        controller.resolveConfirmation(confirmation.id, choice: .primary)
        try await Self.settle(installations, instance)
        #expect(controller.errors[instance.id] == nil)
        #expect(controller.catalogInstalling[instance.id] == nil)
        let mods = try #require(controller.mods[instance.id])
        #expect(mods.map(\.name) == ["dep.jar", "root.jar"])
        #expect(mods.allSatisfy { $0.source == .modrinth })
        #expect(Set(mods.compactMap(\.origin?.projectID)) == ["dep", "root"])
        #expect(Set(try await controller.installedProjects(instance, mods: true).keys) == ["dep", "root"])
    }

    @Test func registeredFabricAPICountsAsInstalledAndOnlyModSkipsDependencies() async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let (container, installations, controller, instance) = try Self.instance(root: root, session: session)
        _ = container
        let apiBytes = try Data(contentsOf: #require(SkinTestFixtures.bundle.url(forResource: "fabric-api-test", withExtension: "zip")))
        let descriptor = FabricAPIDescriptor(projectID: FabricAPIDescriptor.project, versionID: "api", version: "api", channel: "release", filename: "api.jar", url: URL(string: "https://fixtures.test/api.jar")!, size: Int64(apiBytes.count), sha1: Self.sha1(apiBytes), sha512: FabricClient.hash(apiBytes))
        let cached = root.appendingPathComponent("api.jar"); try apiBytes.write(to: cached)
        try await installations.content.provisionAPI(descriptor, from: cached, in: controller.folder(instance, mods: true))
        let rootBytes = Data("root".utf8), depBytes = Data("dep".utf8)
        ModrinthTestProtocol.prepare([
            "\(api)/project/root/version": try JSONSerialization.data(withJSONObject: [Self.version("root-v", project: "root", file: ("root.jar", rootBytes), dependencies: [(FabricAPIDescriptor.project, "required"), ("dep", "required")])]),
            "\(api)/projects": Data(#"[{"id":"P7dR8mSH","slug":"fabric-api","title":"Fabric API","description":"","icon_url":null,"project_type":"mod"},{"id":"dep","slug":"dep","title":"Dep","description":"","icon_url":null,"project_type":"mod"}]"#.utf8),
            "\(api)/project/dep/version": try JSONSerialization.data(withJSONObject: [Self.version("dep-v", project: "dep", file: ("dep.jar", depBytes))]),
            "fixtures.test/root.jar": rootBytes, "fixtures.test/dep.jar": depBytes
        ])
        controller.install(try Self.project("root"), in: instance, mods: true)
        let confirmation = try await Self.confirmation(controller, installations, instance)
        #expect(confirmation.projects.map(\.title) == ["Dep"])
        controller.resolveConfirmation(confirmation.id, choice: .alternative)
        try await Self.settle(installations, instance)
        #expect(controller.errors[instance.id] == nil)
        #expect(controller.mods[instance.id]?.map(\.name) == ["api.jar", "root.jar"])
        #expect(controller.mods[instance.id]?.first?.origin?.api != nil)
    }

    @Test func corruptedDownloadLeavesInstanceUnchanged() async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let (container, installations, controller, instance) = try Self.instance(root: root, session: session)
        _ = container
        ModrinthTestProtocol.prepare([
            "\(api)/project/root/version": try JSONSerialization.data(withJSONObject: [Self.version("root-v", project: "root", file: ("root.jar", Data("root".utf8)))]),
            "fixtures.test/root.jar": Data("tampered".utf8)
        ])
        controller.install(try Self.project("root"), in: instance, mods: true)
        try await Self.settle(installations, instance)
        #expect(controller.errors[instance.id]?.contains("повреждён") == true)
        #expect(controller.mods[instance.id]?.isEmpty != false)
    }

    @Test func newerLoaderRequirementWarnsAndInstallsOnlyWhenConfirmed() async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let (container, installations, controller, instance) = try Self.instance(root: root, session: session)
        _ = container
        let source = root.appendingPathComponent("jar"); try FileManager.default.createDirectory(at: source, withIntermediateDirectories: true)
        try Data(#"{"schemaVersion":1,"id":"root","depends":{"fabricloader":">=0.20.0"}}"#.utf8).write(to: source.appendingPathComponent("fabric.mod.json"))
        let jar = root.appendingPathComponent("root.jar")
        let zip = Process(); zip.executableURL = URL(fileURLWithPath: "/usr/bin/zip"); zip.currentDirectoryURL = source
        zip.arguments = ["-q", jar.path, "fabric.mod.json"]; try zip.run(); zip.waitUntilExit()
        let bytes = try Data(contentsOf: jar)
        ModrinthTestProtocol.prepare([
            "\(api)/project/root/version": try JSONSerialization.data(withJSONObject: [Self.version("root-v", project: "root", file: ("root.jar", bytes))]),
            "fixtures.test/root.jar": bytes
        ])
        controller.install(try Self.project("root"), in: instance, mods: true)
        var confirmation = try await Self.confirmation(controller, installations, instance)
        #expect(confirmation.title == "Возможна несовместимость" && confirmation.destructive)
        #expect(confirmation.projects.first?.note == "Нужен Fabric Loader >=0.20.0, в сборке 0.19.5")
        controller.resolveConfirmation(confirmation.id, choice: .cancel)
        try await Self.settle(installations, instance)
        #expect(controller.errors[instance.id] == nil)
        #expect(controller.mods[instance.id]?.isEmpty != false)
        controller.install(try Self.project("root"), in: instance, mods: true)
        confirmation = try await Self.confirmation(controller, installations, instance)
        controller.resolveConfirmation(confirmation.id, choice: .primary)
        try await Self.settle(installations, instance)
        #expect(controller.mods[instance.id]?.map(\.name) == ["root.jar"])
    }

    @Test func declaredIncompatibilityWithInstalledModWarns() async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let (container, installations, controller, instance) = try Self.instance(root: root, session: session)
        _ = container
        let other = root.appendingPathComponent("other.jar"); try Data("other".utf8).write(to: other)
        try await installations.content.importItem(from: other, into: controller.folder(instance, mods: true), mods: true)
        let rootBytes = Data("root".utf8)
        ModrinthTestProtocol.prepare([
            "\(api)/project/root/version": try JSONSerialization.data(withJSONObject: [Self.version("root-v", project: "root", file: ("root.jar", rootBytes), dependencies: [("other", "incompatible")])]),
            "\(api)/version_files": try JSONSerialization.data(withJSONObject: [FabricClient.hash(Data("other".utf8)): ["id": "other-v", "project_id": "other", "date_published": "2026-10-01"]]),
            "\(api)/projects": Data(#"[{"id":"other","slug":"other","title":"Other","description":"","icon_url":null,"project_type":"mod"}]"#.utf8),
            "fixtures.test/root.jar": rootBytes
        ])
        controller.install(try Self.project("root"), in: instance, mods: true)
        let confirmation = try await Self.confirmation(controller, installations, instance)
        #expect(confirmation.projects.map(\.title) == ["Other"])
        #expect(confirmation.projects.first?.note == "Несовместим с «Root»")
        controller.resolveConfirmation(confirmation.id, choice: .cancel)
        try await Self.settle(installations, instance)
        #expect(controller.mods[instance.id]?.map(\.name) == ["other.jar"])
    }

    private static func temporaryRoot() -> URL { FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString) }

    private static func sha1(_ data: Data) -> String { Insecure.SHA1.hash(data: data).map { String(format: "%02x", $0) }.joined() }

    private static func version(_ id: String, project: String, channel: String = "release", date: String = "2026-10-06", minecraft: String = "test", loader: String = "fabric", file: (name: String, bytes: Data), dependencies: [(project: String, type: String)] = []) -> [String: Any] {
        ["id": id, "project_id": project, "version_number": id, "version_type": channel, "date_published": date, "game_versions": [minecraft], "loaders": [loader],
         "files": [["filename": file.name, "url": "https://fixtures.test/\(file.name)", "size": file.bytes.count, "primary": true, "hashes": ["sha1": sha1(file.bytes), "sha512": FabricClient.hash(file.bytes)]]],
         "dependencies": dependencies.map { ["project_id": $0.project, "version_id": NSNull(), "file_name": NSNull(), "dependency_type": $0.type] }]
    }

    private static func project(_ id: String) throws -> ModrinthProject {
        try JSONDecoder().decode(ModrinthProject.self, from: Data(#"{"project_id":"\#(id)","slug":"\#(id)","title":"Root","description":"","icon_url":null,"project_type":"mod"}"#.utf8))
    }

    private static func instance(root: URL, session: URLSession) throws -> (ModelContainer, InstallationCoordinator, InstanceContentController, GameInstance) {
        let container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let installations = InstallationCoordinator(context: container.mainContext, storage: .init(root: root.appendingPathComponent("instances")))
        let api = FabricAPIDescriptor(projectID: FabricAPIDescriptor.project, versionID: "api", version: "api", channel: "release", filename: "api.jar", url: URL(string: "https://fixtures.test/api.jar")!, size: 1, sha1: String(repeating: "a", count: 40), sha512: String(repeating: "b", count: 128))
        var draft = InstanceDraft(); draft.name = "Catalog"; draft.modLoader = .fabric; draft.fabricConfiguration = .init(loaderVersion: "0.19.5", api: api)
        let instance = try installations.store.create(draft, versionID: "test", metadataURL: "https://fixtures.test/version", metadataSHA1: "sha", javaMajorVersion: 21)
        instance.state = .ready
        return (container, installations, InstanceContentController(installations: installations, modrinth: ModrinthClient(session: session, cache: root.appendingPathComponent("modrinth-cache"))), instance)
    }

    private static func confirmation(_ controller: InstanceContentController, _ installations: InstallationCoordinator, _ instance: GameInstance) async throws -> ContentConfirmation {
        for _ in 0..<500 where controller.confirmation == nil && installations.contentBusy.contains(instance.id) { try await Task.sleep(for: .milliseconds(10)) }
        return try #require(controller.confirmation, "\(controller.errors[instance.id] ?? "нет подтверждения")")
    }

    private static func settle(_ installations: InstallationCoordinator, _ instance: GameInstance) async throws {
        for _ in 0..<500 where installations.contentBusy.contains(instance.id) { try await Task.sleep(for: .milliseconds(10)) }
        #expect(!installations.contentBusy.contains(instance.id))
    }
}

/// Отвечает по «хост + путь», без учёта query: запросы Modrinth различаются путём.
nonisolated final class ModrinthTestProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private nonisolated(unsafe) static var routes: [String: Data] = [:]
    private nonisolated(unsafe) static var recorded: [URLRequest] = []
    static var requests: [URLRequest] { lock.withLock { recorded } }
    static func prepare(_ routes: [String: Data]) { lock.withLock { self.routes = routes; recorded = [] } }
    static func session() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral; configuration.protocolClasses = [ModrinthTestProtocol.self]
        return URLSession(configuration: configuration)
    }
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let url = request.url!
        let data = Self.lock.withLock { Self.recorded.append(request); return Self.routes[(url.host ?? "") + url.path] }
        client?.urlProtocol(self, didReceive: HTTPURLResponse(url: url, statusCode: data == nil ? 404 : 200, httpVersion: nil, headerFields: nil)!, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: data ?? Data())
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

import CryptoKit
import Foundation
import Testing
import SwiftData

@Suite(.serialized) struct FabricTests {
    @Test func predicatesFollowFabricSemantics() throws {
        #expect(try FabricVersionPredicate(">=0.19.3").matches("0.19.5"))
        #expect(try !FabricVersionPredicate(">=0.19.3").matches("0.19.2"))
        #expect(try FabricVersionPredicate(">=0.16 <0.20").matches("0.19.5+build.1"))
        #expect(try !FabricVersionPredicate(">=0.16 <0.20").matches("0.20.0"))
        #expect(try FabricVersionPredicate("~1.2.3").matches("1.2.9"))
        #expect(try !FabricVersionPredicate("~1.2.3").matches("1.3.0"))
        #expect(try FabricVersionPredicate("^0.18.4").matches("0.19.5"))
        #expect(try FabricVersionPredicate("1.2.x").matches("1.2.3"))
        #expect(try !FabricVersionPredicate("1.2.x").matches("1.3.0"))
        #expect(try !FabricVersionPredicate(">=1.0.0").matches("1.0.0-beta.2"))
        let either = try JSONDecoder().decode(FabricVersionPredicate.self, from: Data("[\"<0.10\",\">=0.19\"]".utf8))
        #expect(try either.matches("0.19.5"))
        #expect(try !either.matches("0.18.4"))
    }

    @Test func metadataChecksLoaderAndJavaAndAllowsLegacySchema() throws {
        let metadata = try JSONDecoder().decode(FabricModMetadata.self, from: Data(#"{"id":"fabric-api","depends":{"fabricloader":">=0.19.3","java":">=25"}}"#.utf8))
        #expect(try metadata.supports(loader: "0.19.5", java: 25))
        #expect(try !metadata.supports(loader: "0.18.4", java: 25))
        #expect(try !metadata.supports(loader: "0.19.5", java: 21))
        let legacy = try JSONDecoder().decode(FabricModMetadata.self, from: Data(#"{"schemaVersion":0,"id":"fabric"}"#.utf8))
        #expect(try legacy.supports(loader: "0.19.5", java: 8))
    }

    @Test func loaderCatalogKeepsOrderAndDistinguishesUnsupportedFromNetwork() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [FabricTestProtocol.self]
        let session = URLSession(configuration: config); defer { session.invalidateAndCancel() }
        let url = URL(string: "https://meta.fabricmc.net/v2/versions/loader/26.3")!
        FabricTestProtocol.prepare([url: .init(data: Data(#"[{"loader":{"version":"0.19.5","stable":true}},{"loader":{"version":"0.19.4","stable":false}}]"#.utf8))])
        let client = FabricClient(session: session)
        #expect(try await client.loaderVersions(minecraft: "26.3").map(\.version) == ["0.19.5", "0.19.4"])
        FabricTestProtocol.prepare([url: .init(status: 400, data: Data("[]".utf8))])
        #expect(try await client.loaderVersions(minecraft: "26.3").isEmpty)
        FabricTestProtocol.prepare([url: .init(data: Data(), error: URLError(.notConnectedToInternet))])
        await #expect(throws: URLError.self) { try await client.loaderVersions(minecraft: "26.3") }
    }

    @Test func mavenPathsAndParentAreValidated() throws {
        let profile = try JSONDecoder().decode(FabricProfile.self, from: Data(#"{"id":"fabric-loader-0.19.5-26.3","inheritsFrom":"26.3","mainClass":"KnotClient","libraries":[{"name":"org.ow2.asm:asm:9.10.1","url":"https://maven.fabricmc.net/"}]}"#.utf8))
        try profile.validate(minecraft: "26.3")
        #expect(try profile.libraries[0].path == "org/ow2/asm/asm/9.10.1/asm-9.10.1.jar")
        #expect(throws: MojangError.self) { try profile.validate(minecraft: "1.21.5") }
    }

    @Test func apiSelectionPrefersReleaseThenNewestBetaAndRejectsMissingAPI() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [FabricTestProtocol.self]
        let session = URLSession(configuration: config); defer { session.invalidateAndCancel() }
        let client = FabricClient(session: session)
        var components = URLComponents(string: "https://api.modrinth.com/v2/project/P7dR8mSH/version")!
        components.queryItems = [.init(name: "game_versions", value: "[\"26.3\"]"), .init(name: "loaders", value: "[\"fabric\"]"), .init(name: "include_changelog", value: "false")]
        let url = components.url!
        func version(_ id: String, _ channel: String, _ date: String) -> [String: Any] {
            ["id": id, "project_id": "P7dR8mSH", "version_number": id, "version_type": channel, "date_published": date,
             "game_versions": ["26.3"], "loaders": ["fabric"], "files": [["filename": "api.jar", "url": "https://fixtures.test/api.jar", "size": 3, "primary": true, "hashes": ["sha1": String(repeating: "a", count: 40), "sha512": String(repeating: "b", count: 128)]]]]
        }
        FabricTestProtocol.prepare([url: .init(data: try JSONSerialization.data(withJSONObject: [version("beta", "beta", "2026-10-06"), version("release", "release", "2026-10-05")]))])
        #expect(try await client.latestAPI(minecraft: "26.3").versionID == "release")
        FabricTestProtocol.prepare([url: .init(data: try JSONSerialization.data(withJSONObject: [version("old", "beta", "2026-10-04"), version("new", "beta", "2026-10-06")]))])
        #expect(try await client.latestAPI(minecraft: "26.3").versionID == "new")
        FabricTestProtocol.prepare([url: .init(data: Data("[]".utf8))])
        await #expect(throws: MojangError.self) { try await client.latestAPI(minecraft: "26.3") }
    }

    @Test func fabricLaunchKeepsBaseClientAndMandatoryArgumentsInEveryMode() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        for minecraft in ["1.19", "26.3"] {
            let base = try JSONDecoder().decode(MinecraftVersionManifest.self, from: Data(contentsOf: #require(SkinTestFixtures.bundle.url(forResource: minecraft, withExtension: "json"))))
            let fabric = try JSONDecoder().decode(FabricProfile.self, from: Data(contentsOf: #require(SkinTestFixtures.bundle.url(forResource: "fabric-\(minecraft)", withExtension: "json"))))
            for source in LaunchArgumentSource.allCases {
                let plan = try MinecraftLaunchPlan.build(manifest: base, root: root, executable: root.appendingPathComponent("java/bin/java"), identity: .offline(name: "Player"), source: source, parameters: InstanceParameters(), fabric: fabric)
                #expect(plan.arguments.contains(fabric.mainClass))
                #expect(plan.arguments.contains("-XstartOnFirstThread"))
                #expect(plan.arguments.contains("-DFabricMcEmu= net.minecraft.client.main.Main "))
                #expect(!plan.arguments.contains(where: { $0.contains("${") }))
                let cp = try #require(plan.arguments.firstIndex(of: "-cp"))
                let classpath = plan.arguments[cp + 1]
                #expect(classpath.contains("versions/\(minecraft)/\(minecraft).jar"))
                #expect(classpath.contains("fabric-loader/0.19.5"))
                if minecraft == "26.3" { #expect(!classpath.contains("intermediary/")) }
                else { #expect(classpath.contains("intermediary/1.19")) }
                let templates = try MinecraftLaunchPlan.argumentTemplates(manifest: base, source: source, parameters: InstanceParameters(), fabric: fabric)
                #expect(templates.java.contains("-DFabricMcEmu= net.minecraft.client.main.Main "))
            }
        }
    }

    @Test func apiPreflightFiltersLoaderAndReusesOnlyVerifiedCache() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [FabricTestProtocol.self]
        let session = URLSession(configuration: config); defer { session.invalidateAndCancel() }
        let cache = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: cache) }
        let client = FabricClient(session: session, cache: cache)
        let bytes = try Data(contentsOf: #require(SkinTestFixtures.bundle.url(forResource: "fabric-api-test", withExtension: "zip")))
        let hash = FabricClient.hash(bytes), sha1 = Insecure.SHA1.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
        let file = URL(string: "https://fixtures.test/api.jar")!
        let api = FabricAPIDescriptor(projectID: "P7dR8mSH", versionID: "test", version: "test", channel: "release", filename: "api.jar", url: file, size: Int64(bytes.count), sha1: sha1, sha512: hash)
        FabricTestProtocol.prepare([file: .init(data: bytes)])
        #expect(try await client.metadata(for: api).supports(loader: "0.19.5", java: 25))
        #expect(try await !client.metadata(for: api).supports(loader: "0.19.2", java: 25))
        #expect(FabricTestProtocol.requests.filter { $0 == file }.count == 1)
        try Data("broken".utf8).write(to: cache.appendingPathComponent("\(hash).jar"))
        FabricTestProtocol.prepare([file: .init(data: Data("bad".utf8))])
        await #expect(throws: MojangError.self) { try await client.cachedAPI(api) }
    }

    @Test @MainActor func apiUpdateChecksCompatibilityWarnsOnDisableAndPreservesDisabledState() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [FabricTestProtocol.self]
        let session = URLSession(configuration: config); defer { session.invalidateAndCancel() }
        let fabric = FabricClient(session: session, cache: root.appendingPathComponent("cache"))
        let container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let installations = InstallationCoordinator(context: container.mainContext, storage: .init(root: root.appendingPathComponent("instances")), fabricClient: fabric)
        let bytes = try Data(contentsOf: #require(SkinTestFixtures.bundle.url(forResource: "fabric-api-test", withExtension: "zip")))
        let sha1 = Insecure.SHA1.hash(data: bytes).map { String(format: "%02x", $0) }.joined(), sha512 = FabricClient.hash(bytes)
        let apiURL = URL(string: "https://fixtures.test/new-api.jar")!
        let old = FabricAPIDescriptor(projectID: "P7dR8mSH", versionID: "old", version: "old", channel: "release", filename: "old-api.jar", url: apiURL, size: Int64(bytes.count), sha1: sha1, sha512: sha512)
        var draft = InstanceDraft(); draft.name = "Update"; draft.modLoader = .fabric; draft.fabricConfiguration = .init(loaderVersion: "0.19.2", api: old)
        let instance = try installations.store.create(draft, versionID: "test", metadataURL: "https://fixtures.test/version", metadataSHA1: "sha", javaMajorVersion: 17)
        instance.state = .ready
        let controller = InstanceContentController(installations: installations), folder = try controller.folder(instance, mods: true)
        let cached = root.appendingPathComponent("api.jar"); try bytes.write(to: cached)
        try await installations.content.provisionAPI(old, from: cached, in: folder)
        var components = URLComponents(string: "https://api.modrinth.com/v2/project/P7dR8mSH/version")!
        components.queryItems = [.init(name: "game_versions", value: "[\"test\"]"), .init(name: "loaders", value: "[\"fabric\"]"), .init(name: "include_changelog", value: "false")]
        let version: [String: Any] = ["id": "new", "project_id": "P7dR8mSH", "version_number": "new", "version_type": "release", "date_published": "2026-10-06", "game_versions": ["test"], "loaders": ["fabric"], "files": [["filename": "new-api.jar", "url": apiURL.absoluteString, "size": bytes.count, "primary": true, "hashes": ["sha1": sha1, "sha512": sha512]]]]
        FabricTestProtocol.prepare([components.url!: .init(data: try JSONSerialization.data(withJSONObject: [version])), apiURL: .init(data: bytes)])
        await controller.reload(instance, mods: true)
        await controller.checkUpdates(instance)
        #expect(controller.updates[instance.id]?.isEmpty != false)
        #expect(controller.updateMessages[instance.id]?.contains("другой версии") == true)
        instance.fabricConfigurationData = try JSONEncoder().encode(FabricConfiguration(loaderVersion: "0.19.5", api: old))
        await controller.checkUpdates(instance)
        #expect(controller.updates[instance.id]?["old-api.jar"]?.versionID == "new")
        let item = try #require(controller.mods[instance.id]?.first)
        controller.setEnabled(item, in: instance, enabled: false)
        for _ in 0..<100 where controller.confirmation == nil { try await Task.sleep(for: .milliseconds(10)) }
        #expect(controller.confirmation?.message.contains("Моды, зависящие") == true)
        controller.resolveConfirmation(try #require(controller.confirmation).id, accepted: true)
        for _ in 0..<100 where installations.contentBusy.contains(instance.id) { try await Task.sleep(for: .milliseconds(10)) }
        let disabled = try #require(controller.mods[instance.id]?.first)
        #expect(!disabled.enabled)
        controller.update(disabled, in: instance)
        for _ in 0..<100 where installations.contentBusy.contains(instance.id) { try await Task.sleep(for: .milliseconds(10)) }
        let updated = try #require(controller.mods[instance.id]?.first)
        #expect(!updated.enabled && updated.name == "new-api.jar.disabled")
        #expect(updated.origin?.versionID == "new")
        #expect(!FileManager.default.fileExists(atPath: disabled.url.path))
        try Data("changed outside".utf8).write(to: updated.url)
        await controller.reload(instance, mods: true)
        #expect(controller.mods[instance.id]?.first?.source == .local)
        #expect(controller.updates[instance.id]?.isEmpty != false)
    }
}

nonisolated final class FabricTestProtocol: URLProtocol, @unchecked Sendable {
    struct Response: Sendable { var status = 200; let data: Data; var delay: TimeInterval = 0; var error: URLError? }
    private static let lock = NSLock()
    private nonisolated(unsafe) static var responses: [URL: Response] = [:]
    private nonisolated(unsafe) static var recorded: [URL] = []
    private var delivery: DispatchWorkItem?
    static var requests: [URL] { lock.withLock { recorded } }
    static func prepare(_ responses: [URL: Response]) { lock.withLock { self.responses = responses; recorded = [] } }
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let response = Self.lock.withLock { Self.recorded.append(request.url!); return Self.responses[request.url!] ?? .init(status: 404, data: Data()) }
        let work = DispatchWorkItem { [weak self] in
            guard let self else { return }
            if let error = response.error { client?.urlProtocol(self, didFailWithError: error); return }
            client?.urlProtocol(self, didReceive: HTTPURLResponse(url: request.url!, statusCode: response.status, httpVersion: nil, headerFields: nil)!, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: response.data)
            client?.urlProtocolDidFinishLoading(self)
        }
        delivery = work
        DispatchQueue.global().asyncAfter(deadline: .now() + response.delay, execute: work)
    }
    override func stopLoading() { delivery?.cancel() }
}

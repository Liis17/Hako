import CryptoKit
import Foundation
import SwiftData
import Testing

@Suite(.serialized) @MainActor struct MinecraftInstallationTests {
    private func fixture(_ name: String) throws -> Data {
        let url = try #require(SkinTestFixtures.bundle.url(forResource: name, withExtension: "json"))
        return try Data(contentsOf: url)
    }
    private func session() -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [MojangTestProtocol.self]
        return URLSession(configuration: config)
    }
    private func sha(_ data: Data) -> String { Insecure.SHA1.hash(data: data).map { String(format: "%02x", $0) }.joined() }
    private func temporary() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }

    @Test func officialMetadataCoversLegacyModernAndNativeJavaWithoutNativeGame() throws {
        let legacy = try JSONDecoder().decode(MinecraftVersionManifest.self, from: fixture("1.6.4"))
        #expect(legacy.java.majorVersion == 8)
        #expect(legacy.java.component == "jre-legacy")
        #expect(try !MinecraftCompatibility.libraries(legacy, platform: .intel).isEmpty)
        for version in ["1.18.2", "22w18a"] {
            let manifest = try JSONDecoder().decode(MinecraftVersionManifest.self, from: fixture(version))
            #expect(throws: MojangError.self) { try MinecraftCompatibility.libraries(manifest, platform: .appleSilicon) }
        }
        for version in ["1.19", "26.3"] {
            let manifest = try JSONDecoder().decode(MinecraftVersionManifest.self, from: fixture(version))
            let arm = try MinecraftCompatibility.libraries(manifest, platform: .appleSilicon)
            #expect(arm.contains(where: { $0.path.contains("natives-macos-arm64") }))
            #expect(!arm.contains(where: { $0.path.contains("natives-macos.jar") }))
            #expect(try !MinecraftCompatibility.libraries(manifest, platform: .intel).isEmpty)
        }
        let current = try JSONDecoder().decode(MinecraftVersionManifest.self, from: fixture("26.3"))
        #expect(current.java.majorVersion == 25)
    }

    @Test func preflightDistinguishesUnsupportedFromNetworkFailure() async throws {
        let session = session()
        defer { session.invalidateAndCancel() }
        let url = URL(string: "https://fixtures.test/1.18.2")!
        let bytes = try fixture("1.18.2")
        let version = MinecraftVersion(id: "1.18.2", type: "release", url: url, sha1: sha(bytes))
        MojangTestProtocol.prepare([url: .init(data: bytes), MojangClient.runtimeURL: .init(data: try fixture("java-runtimes"))])
        let client = MojangClient(session: session)
        do { _ = try await client.prepare(version, platform: .appleSilicon); Issue.record("Unsupported version was accepted") }
        catch MojangError.unsupported { }
        MojangTestProtocol.prepare([url: .init(data: Data(), error: URLError(.notConnectedToInternet))])
        await #expect(throws: URLError.self) { try await MojangClient(session: session).prepare(version) }
    }

    @Test func preflightChoosesOfficialLegacyAndModernJavaForIntel() async throws {
        let session = session()
        defer { session.invalidateAndCancel() }
        var responses: [URL: MojangTestProtocol.Response] = [MojangClient.runtimeURL: .init(data: try fixture("java-runtimes"))]
        var versions: [(MinecraftVersion, Int)] = []
        for (id, major) in [("1.6.4", 8), ("1.18.2", 17), ("26.3", 25)] {
            let data = try fixture(id)
            let url = URL(string: "https://fixtures.test/\(id)")!
            responses[url] = .init(data: data)
            versions.append((MinecraftVersion(id: id, type: "release", url: url, sha1: sha(data)), major))
        }
        MojangTestProtocol.prepare(responses)
        let client = MojangClient(session: session)
        for (version, major) in versions {
            let prepared = try await client.prepare(version, platform: .intel)
            #expect(prepared.manifest.java.majorVersion == major)
            #expect(prepared.runtime.majorVersion == major)
        }
    }

    @Test func integrityRejectsWrongHashAndSize() throws {
        let data = Data("abc".utf8)
        let reference = MojangDownload(url: URL(string: "https://fixtures.test/file")!, sha1: "a9993e364706816aba3e25717850c26c9cd0d89d", size: 3)
        try MojangIntegrity.check(data, download: reference)
        #expect(throws: MojangError.self) { try MojangIntegrity.check(Data("bad".utf8), download: reference) }
        var wrongSize = reference
        wrongSize.size = 4
        #expect(throws: MojangError.self) { try MojangIntegrity.check(data, download: wrongSize) }
    }

    private func miniature(escapingLink: Bool = false, delay: TimeInterval = 0) throws -> (MinecraftVersion, [URL: MojangTestProtocol.Response]) {
        let base = "https://fixtures.test/"
        let data = Data("abc".utf8)
        func download(_ name: String) -> [String: Any] { ["url": base + name, "sha1": "a9993e364706816aba3e25717850c26c9cd0d89d", "size": 3] }
        let runtimeBytes = try JSONSerialization.data(withJSONObject: ["files": [
            "jre.bundle/Contents/Home/bin/java": ["type": "file", "executable": true, "downloads": ["raw": download("java")]],
            "jre.bundle/Contents/Home/bin/java-link": ["type": "link", "target": escapingLink ? "../../../../../../escape" : "java"]
        ]])
        let runtime: [String: Any] = ["manifest": ["url": base + "runtime", "sha1": sha(runtimeBytes), "size": runtimeBytes.count], "version": ["name": "17.0.1", "released": "2025-01-01"]]
        let runtimes = try JSONSerialization.data(withJSONObject: ["mac-os-arm64": ["java-runtime-gamma": [runtime]], "mac-os": ["java-runtime-gamma": [runtime]]])
        let assetsBytes = try JSONSerialization.data(withJSONObject: ["virtual": true, "map_to_resources": true, "objects": ["sounds/test.ogg": ["hash": "a9993e364706816aba3e25717850c26c9cd0d89d", "size": 3]]])
        let versionBytes = try JSONSerialization.data(withJSONObject: [
            "id": "test", "downloads": ["client": download("client")], "libraries": [],
            "javaVersion": ["component": "java-runtime-gamma", "majorVersion": 17],
            "assetIndex": ["id": "legacy", "url": base + "assets", "sha1": sha(assetsBytes), "size": assetsBytes.count]
        ])
        let url = URL(string: base + "version")!
        var responses: [URL: MojangTestProtocol.Response] = [
            url: .init(data: versionBytes), MojangClient.runtimeURL: .init(data: runtimes),
            URL(string: base + "runtime")!: .init(data: runtimeBytes), URL(string: base + "assets")!: .init(data: assetsBytes),
            URL(string: "https://resources.download.minecraft.net/a9/a9993e364706816aba3e25717850c26c9cd0d89d")!: .init(data: data, delay: delay)
        ]
        for path in ["java", "client"] { responses[URL(string: base + path)!] = .init(data: data, delay: delay) }
        return (.init(id: "test", type: "release", url: url, sha1: sha(versionBytes)), responses)
    }

    @Test func installationCopiesResourcesAndReusesOnlyVerifiedFiles() async throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let session = session()
        defer { session.invalidateAndCancel() }
        let (version, responses) = try miniature()
        MojangTestProtocol.prepare(responses)
        let installer = MinecraftInstaller(client: MojangClient(session: session), session: session)
        let result = try await installer.install(version, at: root) { _ in }
        #expect(result.javaMajorVersion == 17)
        #expect(FileManager.default.isExecutableFile(atPath: root.appendingPathComponent("java/\(result.javaExecutable)").path))
        #expect(try Data(contentsOf: root.appendingPathComponent("minecraft/resources/sounds/test.ogg")) == Data("abc".utf8))
        #expect(try Data(contentsOf: root.appendingPathComponent("minecraft/assets/virtual/legacy/sounds/test.ogg")) == Data("abc".utf8))
        #expect(try FileManager.default.destinationOfSymbolicLink(atPath: root.appendingPathComponent("java/jre.bundle/Contents/Home/bin/java-link").path) == "java")
        MojangTestProtocol.prepare(responses)
        _ = try await installer.install(version, at: root) { _ in }
        #expect(!MojangTestProtocol.requests.contains(URL(string: "https://fixtures.test/client")!))
        try Data("bad".utf8).write(to: root.appendingPathComponent("minecraft/versions/test/test.jar"))
        MojangTestProtocol.prepare(responses)
        _ = try await installer.install(version, at: root) { _ in }
        #expect(MojangTestProtocol.requests.contains(URL(string: "https://fixtures.test/client")!))
        #expect(try Data(contentsOf: root.appendingPathComponent("minecraft/versions/test/test.jar")) == Data("abc".utf8))
    }

    @Test func corruptDownloadIsNeverPublishedAndUnsafeLinkIsRejected() async throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let session = session()
        defer { session.invalidateAndCancel() }
        var (version, responses) = try miniature()
        responses[URL(string: "https://fixtures.test/client")!] = .init(data: Data("bad".utf8))
        MojangTestProtocol.prepare(responses)
        await #expect(throws: MojangError.self) { try await MinecraftInstaller(client: MojangClient(session: session), session: session).install(version, at: root) { _ in } }
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("minecraft/versions/test/test.jar").path))
        (version, responses) = try miniature(escapingLink: true)
        MojangTestProtocol.prepare(responses)
        await #expect(throws: MojangError.self) { try await MinecraftInstaller(client: MojangClient(session: session), session: session).install(version, at: root) { _ in } }
    }

    @Test func fabricInstallationPinsProfileAndDoesNotRestoreRemovedAPIOnRetry() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let cache = try temporary(); defer { try? FileManager.default.removeItem(at: cache) }
        let session = session(); defer { session.invalidateAndCancel() }
        var (version, responses) = try miniature()
        let apiBytes = try Data(contentsOf: #require(SkinTestFixtures.bundle.url(forResource: "fabric-api-test", withExtension: "zip")))
        let apiURL = URL(string: "https://fixtures.test/api.jar")!
        let api = FabricAPIDescriptor(projectID: "P7dR8mSH", versionID: "test", version: "test", channel: "release", filename: "api.jar", url: apiURL, size: Int64(apiBytes.count), sha1: sha(apiBytes), sha512: FabricClient.hash(apiBytes))
        let profileURL = URL(string: "https://meta.fabricmc.net/v2/versions/loader/test/0.19.5/profile/json")!
        let profile = Data(#"{"id":"fabric-loader-0.19.5-test","inheritsFrom":"test","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"net.fabricmc:fabric-loader:0.19.5","url":"https://fixtures.test/"}]}"#.utf8)
        let libraryURL = URL(string: "https://fixtures.test/net/fabricmc/fabric-loader/0.19.5/fabric-loader-0.19.5.jar")!
        responses[profileURL] = .init(data: profile); responses[apiURL] = .init(data: apiBytes)
        responses[libraryURL] = .init(data: Data("abc".utf8)); responses[URL(string: libraryURL.absoluteString + ".sha1")!] = .init(data: Data(sha(Data("abc".utf8)).utf8))
        MojangTestProtocol.prepare(responses)
        let fabric = FabricClient(session: session, cache: cache), content = InstanceContent()
        let installer = MinecraftInstaller(client: MojangClient(session: session), session: session, fabricClient: fabric, content: content)
        let result = try await installer.install(version, at: root, fabric: .init(loaderVersion: "0.19.5", api: api)) { _ in }
        #expect(result.fabricProfileSHA1 == sha(profile))
        #expect(try FabricProfile.installed(root: root, minecraft: "test", sha1: result.fabricProfileSHA1).mainClass.contains("KnotClient"))
        let mods = root.appendingPathComponent("minecraft/mods")
        #expect(try await content.apiWasProvisioned(in: mods))
        try FileManager.default.removeItem(at: mods.appendingPathComponent("api.jar"))
        _ = try await installer.install(version, at: root, fabric: .init(loaderVersion: "0.19.5", api: api)) { _ in }
        #expect(!FileManager.default.fileExists(atPath: mods.appendingPathComponent("api.jar").path))
        try Data("broken".utf8).write(to: root.appendingPathComponent("minecraft/.hako-fabric.json"))
        #expect(throws: MojangError.self) { try FabricProfile.installed(root: root, minecraft: "test", sha1: result.fabricProfileSHA1) }
    }

    @Test func interruptionRecoversQueueButKeepsUserPause() async throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let session = session()
        defer { session.invalidateAndCancel() }
        let (version, responses) = try miniature(delay: 0.3)
        MojangTestProtocol.prepare(responses)
        let container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let client = MojangClient(session: session)
        let coordinator = InstallationCoordinator(context: container.mainContext, storage: .init(root: root), client: client, installer: MinecraftInstaller(client: client, session: session))
        var draft = InstanceDraft(); draft.name = "Interrupted"
        let active = try coordinator.store.create(draft, versionID: version.id, metadataURL: version.url.absoluteString, metadataSHA1: version.sha1)
        active.state = .installing
        draft.name = "Paused"
        let paused = try coordinator.store.create(draft, versionID: version.id, metadataURL: version.url.absoluteString, metadataSHA1: version.sha1)
        paused.state = .paused
        try container.mainContext.save()
        coordinator.start()
        #expect(active.state == .installing)
        #expect(paused.state == .paused)
        try coordinator.pause(active)
        // Намерение уже на диске, даже пока отменённая задача ещё не завершилась.
        let saved = try #require(ModelContext(container).fetch(FetchDescriptor<GameInstance>()).first { $0.id == active.id })
        #expect(saved.pauseRequested)
        for _ in 0..<200 where active.state == .installing { try await Task.sleep(for: .milliseconds(10)) }
        #expect(active.state == .paused)
        #expect(coordinator.progress[active.id]?.stage == nil || coordinator.progress[active.id]?.stage == InstallationState.paused.title)
        try coordinator.enqueue(active)
        for _ in 0..<300 where active.state == .installing || active.state == .queued { try await Task.sleep(for: .milliseconds(10)) }
        #expect(active.state == .ready)
        #expect(paused.state == .paused)
    }

    @Test func relaunchRestoresPauseRequestedBeforeCancellationFinished() throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let coordinator = InstallationCoordinator(context: container.mainContext, storage: .init(root: root))
        var draft = InstanceDraft(); draft.name = "Stopping"
        let instance = try coordinator.store.create(draft, versionID: "v", metadataURL: "https://fixtures.test/version", metadataSHA1: "hash")
        instance.state = .installing
        instance.pauseRequested = true
        try container.mainContext.save()
        coordinator.start()
        #expect(instance.state == .paused)
    }

    @Test func invalidInstancePathDoesNotBlockFollowingInstallations() async throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let session = session()
        defer { session.invalidateAndCancel() }
        let (version, responses) = try miniature()
        MojangTestProtocol.prepare(responses)
        let container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let client = MojangClient(session: session)
        let coordinator = InstallationCoordinator(context: container.mainContext, storage: .init(root: root), client: client, installer: MinecraftInstaller(client: client, session: session))
        var draft = InstanceDraft(); draft.name = "Invalid"
        let invalid = try coordinator.store.create(draft, versionID: version.id, metadataURL: version.url.absoluteString, metadataSHA1: version.sha1)
        invalid.folderName = "../outside"
        draft.name = "Valid"
        let valid = try coordinator.store.create(draft, versionID: version.id, metadataURL: version.url.absoluteString, metadataSHA1: version.sha1)
        try container.mainContext.save()
        coordinator.start()
        #expect(invalid.state == .failed)
        for _ in 0..<300 where valid.state == .installing || valid.state == .queued { try await Task.sleep(for: .milliseconds(10)) }
        #expect(valid.state == .ready)
        #expect(coordinator.queueError == nil)
    }

    @Test func liveInstallationWhenRequested() async throws {
        guard let path = ProcessInfo.processInfo.environment["HAKO_LIVE_INSTALL_ROOT"] else { return }
        let client = MojangClient()
        let catalog = try await client.catalog()
        let version = try #require(catalog.versions.first { $0.id == catalog.latest["release"] })
        let root = URL(fileURLWithPath: path)
        let result = try await MinecraftInstaller(client: client).install(version, at: root) { value in
            try? Data("\(value.stage) \(Int(value.fraction * 100))%".utf8).write(to: URL(fileURLWithPath: "/tmp/hako-live-progress.txt"), options: .atomic)
        }
        let process = Process()
        process.executableURL = root.appendingPathComponent("java/\(result.javaExecutable)")
        process.arguments = ["-version"]
        let output = Pipe(); process.standardError = output
        try process.run()
        let text = String(decoding: output.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        process.waitUntilExit()
        #expect(process.terminationStatus == 0)
        #expect(text.contains("\"\(result.javaMajorVersion)."))
        try Data(text.utf8).write(to: URL(fileURLWithPath: "/tmp/hako-live-java.txt"))
    }
}

nonisolated final class MojangTestProtocol: URLProtocol, @unchecked Sendable {
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

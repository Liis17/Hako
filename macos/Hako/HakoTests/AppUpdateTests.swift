import CryptoKit
import Foundation
import SwiftData
import Testing

@Suite(.serialized) @MainActor struct AppUpdateTests {
    private static let commit = "899eced977c249d1029dee13b7bbf143def293fe"
    private static let releasePath = "api.github.com/repos/Liis17/Hako/releases/tags/nightly"
    private static let dmg = Data("dmg".utf8)

    private static func release(commit: String = commit, digest: String? = nil, size: Int? = nil) -> Data {
        let sha = SHA256.hash(data: dmg).map { String(format: "%02x", $0) }.joined()
        let json: [String: Any] = [
            "target_commitish": commit, "name": "Nightly \(commit.prefix(7))", "published_at": "2026-10-09T07:55:10Z",
            "html_url": "https://github.com/Liis17/Hako/releases/tag/nightly",
            "assets": [
                ["name": "notes.txt", "browser_download_url": "https://fixtures.test/notes.txt", "size": 1, "digest": NSNull()],
                ["name": "Hako.dmg", "browser_download_url": "https://fixtures.test/Hako.dmg", "size": size ?? dmg.count, "digest": digest ?? "sha256:\(sha.uppercased())"]
            ]
        ]
        return try! JSONSerialization.data(withJSONObject: json)
    }

    @Test func latestDecodesCommitAssetAndDigest() async throws {
        let session = AppUpdateTestProtocol.session(); defer { session.invalidateAndCancel() }
        AppUpdateTestProtocol.prepare([Self.releasePath: Self.release()])
        let release = try #require(try await AppReleaseClient(session: session).latest())
        #expect(release.commit == Self.commit && release.shortCommit == "899eced")
        #expect(release.publishedAt == Date(timeIntervalSince1970: 1_791_532_510))
        #expect(release.dmg?.url.absoluteString == "https://fixtures.test/Hako.dmg")
        #expect(release.dmg?.sha256 == SHA256.hash(data: Self.dmg).map { String(format: "%02x", $0) }.joined())
        let request = try #require(AppUpdateTestProtocol.requests.first)
        #expect(request.value(forHTTPHeaderField: "Accept") == "application/vnd.github+json")
        #expect(request.value(forHTTPHeaderField: "User-Agent")?.hasPrefix("Hako/") == true)
    }

    @Test func missingReleaseIsNotAnError() async throws {
        let session = AppUpdateTestProtocol.session(); defer { session.invalidateAndCancel() }
        AppUpdateTestProtocol.prepare([:])
        #expect(try await AppReleaseClient(session: session).latest() == nil)
    }

    @Test func updateRequiresDifferentFullCommitAndDMG() throws {
        let release = try AppRelease.decode(Self.release())
        #expect(release.isUpdate(for: "0000000000000000000000000000000000000000"))
        #expect(!release.isUpdate(for: Self.commit.uppercased()))
        #expect(!release.isUpdate(for: nil))
        let branch = try AppRelease.decode(Self.release(commit: "main"))
        #expect(!branch.isUpdate(for: Self.commit))
    }

    @Test func downloadVerifiesSizeAndSHA256() async throws {
        let session = AppUpdateTestProtocol.session(); defer { session.invalidateAndCancel() }
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: folder) }
        AppUpdateTestProtocol.prepare(["fixtures.test/Hako.dmg": Self.dmg])
        let client = AppReleaseClient(session: session)

        let good = try AppRelease.decode(Self.release())
        let file = try await client.download(try #require(good.dmg), into: folder) { _ in }
        #expect(try Data(contentsOf: file) == Self.dmg)

        for broken in [Self.release(digest: "sha256:\(String(repeating: "0", count: 64))"), Self.release(size: 99)] {
            let release = try AppRelease.decode(broken)
            await #expect(throws: MojangError.self) { try await client.download(release.dmg!, into: folder) { _ in } }
            #expect(!FileManager.default.fileExists(atPath: folder.appendingPathComponent("Hako.dmg").path))
        }
        let unsigned = try AppRelease.decode(Self.release(digest: "md5:x"))
        await #expect(throws: MojangError.self) { try await client.download(unsigned.dmg!, into: folder) { _ in } }
    }

    @Test func checkOffersOnlyDifferentCommitAndSkipsLocalBuilds() async throws {
        let session = AppUpdateTestProtocol.session(); defer { session.invalidateAndCancel() }
        let container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let store = InstanceStore(context: container.mainContext)
        AppUpdateTestProtocol.prepare([Self.releasePath: Self.release()])
        let client = AppReleaseClient(session: session)

        let current = AppUpdateCoordinator(store: store, client: client, currentCommit: Self.commit)
        await current.check()
        #expect(current.release == nil && current.lastChecked != nil && current.error == nil && current.activity == .idle)

        let outdated = AppUpdateCoordinator(store: store, client: client, currentCommit: String(repeating: "a", count: 40))
        await outdated.check()
        #expect(outdated.release?.commit == Self.commit)

        let local = AppUpdateCoordinator(store: store, client: client, currentCommit: nil)
        await local.check()
        #expect(local.isLocalBuild && local.release == nil && local.lastChecked == nil)
        #expect(AppUpdateTestProtocol.requests.count == 2)

        AppUpdateTestProtocol.prepare([:])
        await outdated.check()
        #expect(outdated.release == nil && outdated.error != nil)
    }
}

nonisolated final class AppUpdateTestProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private nonisolated(unsafe) static var routes: [String: Data] = [:]
    private nonisolated(unsafe) static var recorded: [URLRequest] = []
    static var requests: [URLRequest] { lock.withLock { recorded } }
    static func prepare(_ routes: [String: Data]) { lock.withLock { self.routes = routes; recorded = [] } }
    static func session() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral; configuration.protocolClasses = [AppUpdateTestProtocol.self]
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

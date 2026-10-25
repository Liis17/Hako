import CryptoKit
import Foundation
import SwiftData
import Testing

@Suite(.serialized) @MainActor struct AppUpdateTests {
    private static let commit = "899eced977c249d1029dee13b7bbf143def293fe"
    private static let pageOne = "api.github.com/repos/Liis17/Hako/releases?per_page=100&page=1"
    private static let pageTwo = "api.github.com/repositories/861234567/releases?per_page=100&page=2"
    private static let dmg = Data("dmg".utf8)

    private static func release(
        version: String = "1.0.1",
        tagName: String? = nil,
        commit: String = commit,
        publishedAt: String? = "2026-10-09T07:55:10Z",
        draft: Bool = false,
        prerelease: Bool = false,
        includesDMG: Bool = true,
        dmgNames: [String]? = nil,
        digest: String? = nil,
        size: Int? = nil
    ) -> [String: Any] {
        let sha = SHA256.hash(data: dmg).map { String(format: "%02x", $0) }.joined()
        var assets: [[String: Any]] = [
            ["name": "notes.txt", "browser_download_url": "https://fixtures.test/notes.txt", "size": 1, "digest": NSNull()]
        ]
        if includesDMG {
            for name in dmgNames ?? ["Hako-\(version)-macos.dmg"] {
                assets.append([
                    "name": name, "browser_download_url": "https://fixtures.test/\(name)",
                    "size": size ?? dmg.count, "digest": digest ?? "sha256:\(sha.uppercased())"
                ])
            }
        }
        return [
            "target_commitish": commit,
            "tag_name": tagName ?? "macos-v\(version)",
            "name": "Hako \(version)",
            "published_at": publishedAt.map { $0 as Any } ?? NSNull(),
            "html_url": "https://github.com/Liis17/Hako/releases/tag/\(tagName ?? "macos-v\(version)")",
            "draft": draft,
            "prerelease": prerelease,
            "assets": assets
        ]
    }

    private static func json(_ value: Any) -> Data {
        try! JSONSerialization.data(withJSONObject: value)
    }

    @Test func latestPaginatesAndSelectsHighestVersionAcrossUnorderedReleases() async throws {
        let session = AppUpdateTestProtocol.session(); defer { session.invalidateAndCancel() }
        let nextPage = "<https://api.github.com/repositories/861234567/releases?per_page=100&page=2>; rel=\"next\", <https://api.github.com/repositories/861234567/releases?per_page=100&page=3>; rel=\"last\""
        AppUpdateTestProtocol.prepare([
            Self.pageOne: .init(data: Self.json([
                Self.release(version: "1.11.0", publishedAt: "2026-10-10T07:55:10Z"),
                Self.release(version: "1.9.9", publishedAt: "2026-10-09T07:55:10Z")
            ]), headers: ["Link": nextPage]),
            Self.pageTwo: .init(data: Self.json([
                Self.release(version: "1.12.0", publishedAt: "2026-10-08T07:55:10Z"),
                Self.release(version: "1.10.0", publishedAt: "2026-10-07T07:55:10Z")
            ]))
        ])

        let release = try #require(try await AppReleaseClient(session: session).latest())
        #expect(release.version == "1.12.0")
        #expect(AppUpdateTestProtocol.requests.count == 2)
        #expect(AppUpdateTestProtocol.requests.map { AppUpdateTestProtocol.key($0.url!) } == [Self.pageOne, Self.pageTwo])
        #expect(AppUpdateTestProtocol.requests.allSatisfy { $0.value(forHTTPHeaderField: "Accept") == "application/vnd.github+json" })
        #expect(AppUpdateTestProtocol.requests.first?.value(forHTTPHeaderField: "User-Agent")?.hasPrefix("Hako/") == true)
    }

    @Test func latestIgnoresOtherPlatformsUnstableMalformedAndIncompleteReleases() async throws {
        let session = AppUpdateTestProtocol.session(); defer { session.invalidateAndCancel() }
        AppUpdateTestProtocol.prepare([
            Self.pageOne: .init(data: Self.json([
                Self.release(version: "1.99.0", tagName: "windows-v1.99.0"),
                Self.release(version: "1.98.0", draft: true),
                Self.release(version: "1.97.0", prerelease: true),
                Self.release(version: "1.96.0", commit: "main"),
                Self.release(version: "1.95.0", includesDMG: false),
                Self.release(version: "1.94.0", tagName: "nightly"),
                Self.release(version: "1.93.0", tagName: "macos-v1.93"),
                Self.release(version: "1.92.0", tagName: "macos-v01.92.0"),
                Self.release(version: "1.91.0", publishedAt: nil),
                Self.release(version: "1.4.0")
            ]))
        ])

        let release = try #require(try await AppReleaseClient(session: session).latest())
        #expect(release.version == "1.4.0")
    }

    @Test func releaseRequiresDMGNameToMatchItsTagVersion() throws {
        let oldName = try AppRelease.decode(Self.json(Self.release(version: "1.0.3", dmgNames: ["Hako.dmg"])))
        #expect(oldName.dmg == nil)
        #expect(!oldName.isStableMacOSRelease)

        let otherVersion = try AppRelease.decode(Self.json(Self.release(
            version: "1.0.3", tagName: "macos-v1.0.3", dmgNames: ["Hako-1.0.2-macos.dmg"]
        )))
        #expect(otherVersion.dmg == nil)
        #expect(!otherVersion.isStableMacOSRelease)
    }

    @Test func releaseSelectsExactVersionedDMGWhenSeveralAreAttached() throws {
        let release = try AppRelease.decode(Self.json(Self.release(
            version: "1.0.3", dmgNames: ["Hako.dmg", "Hako-1.0.2-macos.dmg", "Hako-1.0.3-macos.dmg"]
        )))
        #expect(release.dmg?.name == "Hako-1.0.3-macos.dmg")
    }

    @Test func updateComparisonUsesNumericVersionsAndNormalizesLegacyBundleVersion() throws {
        let release = try AppRelease.decode(Self.json(Self.release(version: "1.10.0")))
        #expect(release.isUpdate(for: "1.9.9"))
        #expect(!release.isUpdate(for: "1.10.0"))
        #expect(!release.isUpdate(for: "1.10.1"))
        #expect(!release.isUpdate(for: "1.10"))
        #expect(!release.isUpdate(for: nil))

        let initial = try AppRelease.decode(Self.json(Self.release(version: "1.0.0")))
        #expect(!initial.isUpdate(for: "1.0"))
    }

    @Test func emptyAndMissingReleaseListsAreNormalCheckResults() async throws {
        let session = AppUpdateTestProtocol.session(); defer { session.invalidateAndCancel() }
        let container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let coordinator = AppUpdateCoordinator(
            store: InstanceStore(context: container.mainContext), client: AppReleaseClient(session: session),
            currentCommit: Self.commit, currentVersion: "1.0.0"
        )

        AppUpdateTestProtocol.prepare([Self.pageOne: .init(data: Self.json([]))])
        await coordinator.check()
        #expect(coordinator.release == nil && coordinator.lastChecked != nil && coordinator.error == nil)

        AppUpdateTestProtocol.prepare([Self.pageOne: .init(data: Data(), statusCode: 404)])
        await coordinator.check()
        #expect(coordinator.release == nil && coordinator.lastChecked != nil && coordinator.error == nil)
    }

    @Test func networkAndJSONFailuresRemainErrors() async throws {
        let session = AppUpdateTestProtocol.session(); defer { session.invalidateAndCancel() }
        let client = AppReleaseClient(session: session)

        AppUpdateTestProtocol.prepareFailure(.notConnectedToInternet)
        await #expect(throws: URLError.self) { _ = try await client.latest() }

        AppUpdateTestProtocol.prepare([Self.pageOne: .init(data: Data("not json".utf8))])
        await #expect(throws: MojangError.self) { _ = try await client.latest() }

        AppUpdateTestProtocol.prepare([Self.pageOne: .init(data: Data(), statusCode: 500)])
        await #expect(throws: MojangError.self) { _ = try await client.latest() }
    }

    @Test func localBuildSkipsHTTPAndDifferentCommitAloneIsNotAnUpdate() async throws {
        let session = AppUpdateTestProtocol.session(); defer { session.invalidateAndCancel() }
        let container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        AppUpdateTestProtocol.prepare([Self.pageOne: .init(data: Self.json([Self.release(version: "1.0.1")]))])
        let client = AppReleaseClient(session: session)

        let current = AppUpdateCoordinator(
            store: InstanceStore(context: container.mainContext), client: client,
            currentCommit: String(repeating: "b", count: 40), currentVersion: "1.0.1"
        )
        await current.check()
        #expect(current.release == nil && current.lastChecked != nil && current.error == nil)

        let outdated = AppUpdateCoordinator(
            store: InstanceStore(context: container.mainContext), client: client,
            currentCommit: String(repeating: "a", count: 40), currentVersion: "1.0.0"
        )
        await outdated.check()
        #expect(outdated.release?.version == "1.0.1")

        let local = AppUpdateCoordinator(
            store: InstanceStore(context: container.mainContext), client: client,
            currentCommit: nil, currentVersion: "1.0.0"
        )
        await local.check()
        #expect(local.isLocalBuild && local.release == nil && local.lastChecked == nil)
        #expect(AppUpdateTestProtocol.requests.count == 2)
    }

    @Test func installerRejectsExtractedBundlesWithDifferentVersionOrCommit() throws {
        let release = try AppRelease.decode(Self.json(Self.release(version: "1.0.0")))
        let bundleInfo: [String: Any] = [
            "CFBundleIdentifier": "com.Launcher.Hako",
            "CFBundleShortVersionString": "1.0",
            AppUpdateInstaller.commitKey: Self.commit
        ]
        try AppUpdateInstaller.validate(bundleInfo: bundleInfo, expectedBundleIdentifier: "com.Launcher.Hako", release: release)

        var mismatchedVersion = bundleInfo
        mismatchedVersion["CFBundleShortVersionString"] = "1.0.1"
        #expect(throws: MojangError.self) {
            try AppUpdateInstaller.validate(bundleInfo: mismatchedVersion, expectedBundleIdentifier: "com.Launcher.Hako", release: release)
        }

        var mismatchedCommit = bundleInfo
        mismatchedCommit[AppUpdateInstaller.commitKey] = String(repeating: "a", count: 40)
        #expect(throws: MojangError.self) {
            try AppUpdateInstaller.validate(bundleInfo: mismatchedCommit, expectedBundleIdentifier: "com.Launcher.Hako", release: release)
        }
    }

    @Test func downloadStillVerifiesSizeAndSHA256() async throws {
        let session = AppUpdateTestProtocol.session(); defer { session.invalidateAndCancel() }
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: folder) }
        let assetName = "Hako-1.0.1-macos.dmg"
        AppUpdateTestProtocol.prepare(["fixtures.test/\(assetName)": .init(data: Self.dmg)])
        let client = AppReleaseClient(session: session)

        let good = try AppRelease.decode(Self.json(Self.release()))
        let file = try await client.download(try #require(good.dmg), into: folder) { _ in }
        #expect(file.lastPathComponent == assetName)
        #expect(try Data(contentsOf: file) == Self.dmg)

        for broken in [
            Self.release(digest: "sha256:\(String(repeating: "0", count: 64))"),
            Self.release(size: 99)
        ] {
            let release = try AppRelease.decode(Self.json(broken))
            await #expect(throws: MojangError.self) { try await client.download(release.dmg!, into: folder) { _ in } }
            #expect(!FileManager.default.fileExists(atPath: folder.appendingPathComponent(assetName).path))
        }
        let unsigned = try AppRelease.decode(Self.json(Self.release(digest: "md5:x")))
        await #expect(throws: MojangError.self) { try await client.download(unsigned.dmg!, into: folder) { _ in } }
    }
}

nonisolated final class AppUpdateTestProtocol: URLProtocol, @unchecked Sendable {
    struct StubResponse {
        let data: Data
        var statusCode = 200
        var headers: [String: String] = [:]

        init(data: Data, statusCode: Int = 200, headers: [String: String] = [:]) {
            self.data = data; self.statusCode = statusCode; self.headers = headers
        }
    }

    private static let lock = NSLock()
    private nonisolated(unsafe) static var routes: [String: StubResponse] = [:]
    private nonisolated(unsafe) static var recorded: [URLRequest] = []
    private nonisolated(unsafe) static var failure: URLError.Code?
    static var requests: [URLRequest] { lock.withLock { recorded } }

    static func key(_ url: URL) -> String {
        "\(url.host ?? "")\(url.path)\(url.query.map { "?\($0)" } ?? "")"
    }

    static func prepare(_ routes: [String: StubResponse]) {
        lock.withLock { self.routes = routes; recorded = []; failure = nil }
    }

    static func prepareFailure(_ error: URLError.Code) {
        lock.withLock { routes = [:]; recorded = []; failure = error }
    }

    static func session() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [AppUpdateTestProtocol.self]
        return URLSession(configuration: configuration)
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let url = request.url!
        let result = Self.lock.withLock { () -> (StubResponse?, URLError.Code?) in
            Self.recorded.append(request)
            return (Self.routes[Self.key(url)], Self.failure)
        }
        if let error = result.1 {
            client?.urlProtocol(self, didFailWithError: URLError(error))
            return
        }
        let stub = result.0 ?? StubResponse(data: Data(), statusCode: 404)
        client?.urlProtocol(self, didReceive: HTTPURLResponse(url: url, statusCode: stub.statusCode, httpVersion: nil, headerFields: stub.headers)!, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: stub.data)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}

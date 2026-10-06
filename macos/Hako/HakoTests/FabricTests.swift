import CryptoKit
import Foundation
import Testing

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
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [MojangTestProtocol.self]
        let session = URLSession(configuration: config); defer { session.invalidateAndCancel() }
        let url = URL(string: "https://meta.fabricmc.net/v2/versions/loader/26.3")!
        MojangTestProtocol.prepare([url: .init(data: Data(#"[{"loader":{"version":"0.19.5","stable":true}},{"loader":{"version":"0.19.4","stable":false}}]"#.utf8))])
        let client = FabricClient(session: session)
        #expect(try await client.loaderVersions(minecraft: "26.3").map(\.version) == ["0.19.5", "0.19.4"])
        MojangTestProtocol.prepare([url: .init(status: 400, data: Data("[]".utf8))])
        #expect(try await client.loaderVersions(minecraft: "26.3").isEmpty)
        MojangTestProtocol.prepare([url: .init(data: Data(), error: URLError(.notConnectedToInternet))])
        await #expect(throws: URLError.self) { try await client.loaderVersions(minecraft: "26.3") }
    }

    @Test func mavenPathsAndParentAreValidated() throws {
        let profile = try JSONDecoder().decode(FabricProfile.self, from: Data(#"{"id":"fabric-loader-0.19.5-26.3","inheritsFrom":"26.3","mainClass":"KnotClient","libraries":[{"name":"org.ow2.asm:asm:9.10.1","url":"https://maven.fabricmc.net/"}]}"#.utf8))
        try profile.validate(minecraft: "26.3")
        #expect(try profile.libraries[0].path == "org/ow2/asm/asm/9.10.1/asm-9.10.1.jar")
        #expect(throws: MojangError.self) { try profile.validate(minecraft: "1.21.5") }
    }

    @Test func apiSelectionPrefersReleaseThenNewestBetaAndRejectsMissingAPI() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [MojangTestProtocol.self]
        let session = URLSession(configuration: config); defer { session.invalidateAndCancel() }
        let client = FabricClient(session: session)
        var components = URLComponents(string: "https://api.modrinth.com/v2/project/P7dR8mSH/version")!
        components.queryItems = [.init(name: "game_versions", value: "[\"26.3\"]"), .init(name: "loaders", value: "[\"fabric\"]"), .init(name: "include_changelog", value: "false")]
        let url = components.url!
        func version(_ id: String, _ channel: String, _ date: String) -> [String: Any] {
            ["id": id, "project_id": "P7dR8mSH", "version_number": id, "version_type": channel, "date_published": date,
             "game_versions": ["26.3"], "loaders": ["fabric"], "files": [["filename": "api.jar", "url": "https://fixtures.test/api.jar", "size": 3, "primary": true, "hashes": ["sha1": String(repeating: "a", count: 40), "sha512": String(repeating: "b", count: 128)]]]]
        }
        MojangTestProtocol.prepare([url: .init(data: try JSONSerialization.data(withJSONObject: [version("beta", "beta", "2026-10-06"), version("release", "release", "2026-10-05")]))])
        #expect(try await client.latestAPI(minecraft: "26.3").versionID == "release")
        MojangTestProtocol.prepare([url: .init(data: try JSONSerialization.data(withJSONObject: [version("old", "beta", "2026-10-04"), version("new", "beta", "2026-10-06")]))])
        #expect(try await client.latestAPI(minecraft: "26.3").versionID == "new")
        MojangTestProtocol.prepare([url: .init(data: Data("[]".utf8))])
        await #expect(throws: MojangError.self) { try await client.latestAPI(minecraft: "26.3") }
    }
}

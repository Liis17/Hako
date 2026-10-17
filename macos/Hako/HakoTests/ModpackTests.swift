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
}

nonisolated final class ModpackTestProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private nonisolated(unsafe) static var routes: [String: Data] = [:]
    private nonisolated(unsafe) static var recorded: [URLRequest] = []
    static var requests: [URLRequest] { lock.withLock { recorded } }
    static func prepare(_ routes: [String: Data]) { lock.withLock { self.routes = routes; recorded = [] } }
    static func session() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral; configuration.protocolClasses = [Self.self]
        return URLSession(configuration: configuration)
    }
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let url = request.url!
        let data = Self.lock.withLock { Self.recorded.append(request); return Self.routes[url.path] }
        client?.urlProtocol(self, didReceive: HTTPURLResponse(url: url, statusCode: data == nil ? 404 : 200, httpVersion: nil, headerFields: nil)!, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: data ?? Data())
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

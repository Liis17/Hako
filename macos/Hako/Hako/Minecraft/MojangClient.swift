import CryptoKit
import Foundation

nonisolated enum MojangIntegrity {
    static func check(_ data: Data, download: MojangDownload) throws {
        let hash = Insecure.SHA1.hash(data: data).map { String(format: "%02x", $0) }.joined()
        guard hash == download.sha1.lowercased(), download.size == nil || Int64(data.count) == download.size else {
            throw MojangError.invalid("Загруженный файл повреждён. Повторите загрузку.")
        }
    }

    static func validFile(_ url: URL, download: MojangDownload) throws -> Bool {
        guard FileManager.default.fileExists(atPath: url.path) else { return false }
        if let size = download.size, try url.resourceValues(forKeys: [.fileSizeKey]).fileSize.map(Int64.init) != size { return false }
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hash = Insecure.SHA1()
        while let data = try handle.read(upToCount: 1_048_576), !data.isEmpty {
            try Task.checkCancellation()
            hash.update(data: data)
        }
        return hash.finalize().map { String(format: "%02x", $0) }.joined() == download.sha1.lowercased()
    }
}

actor MojangClient {
    static let catalogURL = URL(string: "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json")!
    static let runtimeURL = URL(string: "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json")!
    private let session: URLSession
    private var runtimeIndex: [String: [String: [JavaRuntimePackage]]]?
    private var prepared: [String: PreparedInstallation] = [:]

    init(session: URLSession = URLSession(configuration: .ephemeral)) { self.session = session }

    func catalog() async throws -> MinecraftCatalog {
        try JSONDecoder().decode(MinecraftCatalog.self, from: await data(at: Self.catalogURL))
    }

    func data(at url: URL) async throws -> Data {
        guard url.scheme == "https" else { throw MojangError.invalid("Ссылка загрузки должна использовать HTTPS.") }
        let (data, response) = try await session.data(from: url)
        guard let response = response as? HTTPURLResponse else { throw MojangError.invalid("Некорректный ответ сервера.") }
        guard (200..<300).contains(response.statusCode) else { throw MojangError.http(response.statusCode) }
        return data
    }

    func data(for download: MojangDownload) async throws -> Data {
        let data = try await data(at: download.url)
        try MojangIntegrity.check(data, download: download)
        return data
    }

    func prepare(_ version: MinecraftVersion, platform: MinecraftPlatform = .current) async throws -> PreparedInstallation {
        let key = "\(platform.rawValue):\(version.sha1)"
        if let cached = prepared[key] { return cached }
        let bytes = try await data(for: .init(url: version.url, sha1: version.sha1))
        let manifest = try JSONDecoder().decode(MinecraftVersionManifest.self, from: bytes)
        guard manifest.id == version.id, manifest.downloads["client"] != nil else {
            throw MojangError.unsupported("Mojang не предоставляет клиент этой версии.")
        }
        if runtimeIndex == nil {
            runtimeIndex = try JSONDecoder().decode([String: [String: [JavaRuntimePackage]]].self, from: await data(at: Self.runtimeURL))
        }
        guard let runtime = runtimeIndex?[platform.runtimeKey]?[manifest.java.component]?
            .filter({ $0.majorVersion == manifest.java.majorVersion })
            .max(by: { $0.version.released < $1.version.released }) else {
            throw MojangError.unsupported("Для этой версии требуется Java \(manifest.java.majorVersion), которую Mojang не предоставляет для \(platform == .appleSilicon ? "Apple Silicon" : "Intel Mac").")
        }
        let libraries = try MinecraftCompatibility.libraries(manifest, platform: platform)
        let result = PreparedInstallation(version: version, manifest: manifest, manifestData: bytes, runtime: runtime, libraries: libraries)
        prepared[key] = result
        return result
    }
}

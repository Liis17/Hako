import CryptoKit
import Foundation
import Darwin

nonisolated struct PreparedFabric: Sendable {
    let api: FabricAPIDescriptor
    let metadata: FabricModMetadata
    let loaders: [FabricLoaderVersion]
}

actor FabricClient {
    private let session: URLSession
    private let metaURL: URL
    private let modrinthURL: URL
    private let cache: URL

    init(session: URLSession = URLSession(configuration: .ephemeral), metaURL: URL = URL(string: "https://meta.fabricmc.net/v2")!, modrinthURL: URL = URL(string: "https://api.modrinth.com/v2")!, cache: URL = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0].appendingPathComponent("Hako/FabricAPI")) {
        self.session = session; self.metaURL = metaURL; self.modrinthURL = modrinthURL; self.cache = cache
    }

    private func data(_ url: URL, unsupportedIsEmpty: Bool = false) async throws -> Data {
        guard url.scheme == "https" else { throw MojangError.invalid(String(appLocalized: "Ссылка загрузки должна использовать HTTPS.")) }
        var request = URLRequest(url: url)
        request.setValue("Hako/\(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0") (https://github.com/Liis17/Hako)", forHTTPHeaderField: "User-Agent")
        let (bytes, response) = try await session.data(for: request)
        guard let response = response as? HTTPURLResponse else { throw MojangError.invalid(String(appLocalized: "Некорректный ответ сервера Fabric.")) }
        if unsupportedIsEmpty && response.statusCode == 400 && (try? JSONSerialization.jsonObject(with: bytes) as? [Any])?.isEmpty == true { return bytes }
        guard (200..<300).contains(response.statusCode) else { throw MojangError.http(response.statusCode) }
        try Task.checkCancellation()
        return bytes
    }

    func loaderVersions(minecraft: String) async throws -> [FabricLoaderVersion] {
        struct Entry: Decodable { let loader: FabricLoaderVersion }
        let bytes = try await data(metaURL.appendingPathComponent("versions/loader").appendingPathComponent(minecraft), unsupportedIsEmpty: true)
        return try JSONDecoder().decode([Entry].self, from: bytes).map(\.loader)
    }

    func latestAPI(minecraft: String) async throws -> FabricAPIDescriptor {
        struct Version: Decodable {
            struct File: Decodable {
                let filename: String; let url: URL; let size: Int64; let hashes: [String: String]; let primary: Bool; let file_type: String?
            }
            let id: String; let project_id: String; let version_number: String; let version_type: String; let date_published: String
            let game_versions: [String]; let loaders: [String]; let files: [File]
        }
        var components = URLComponents(url: modrinthURL.appendingPathComponent("project/\(FabricAPIDescriptor.project)/version"), resolvingAgainstBaseURL: false)!
        components.queryItems = [
            URLQueryItem(name: "game_versions", value: String(decoding: try JSONEncoder().encode([minecraft]), as: UTF8.self)),
            URLQueryItem(name: "loaders", value: "[\"fabric\"]"), URLQueryItem(name: "include_changelog", value: "false")
        ]
        let versions = try JSONDecoder().decode([Version].self, from: await data(components.url!))
        let priority = ["release": 0, "beta": 1, "alpha": 2]
        let sorted = versions.filter { $0.project_id == FabricAPIDescriptor.project && $0.game_versions.contains(minecraft) && $0.loaders.contains("fabric") && priority[$0.version_type] != nil }.sorted {
            let lhs = priority[$0.version_type]!, rhs = priority[$1.version_type]!
            return lhs == rhs ? $0.date_published > $1.date_published : lhs < rhs
        }
        guard let version = sorted.first else { throw MojangError.unsupported(String(appLocalized: "Для Minecraft \(minecraft) ещё нет совместимого Fabric API. Выберите другую версию Minecraft или Vanilla.")) }
        let files = version.files.filter { $0.filename.lowercased().hasSuffix(".jar") && !["sources-jar", "dev-jar", "javadoc-jar"].contains($0.file_type ?? "") }
        guard let file = files.first(where: \.primary) ?? files.first, let sha1 = file.hashes["sha1"], let sha512 = file.hashes["sha512"], file.size > 0 else { throw MojangError.invalid(String(appLocalized: "В описании Fabric API отсутствует проверяемый JAR-файл.")) }
        _ = try InstanceStorage.containedURL(file.filename, in: cache)
        guard !file.filename.contains("/"), sha1.count == 40, sha512.count == 128, (sha1 + sha512).allSatisfy(\.isHexDigit) else { throw MojangError.invalid(String(appLocalized: "Некорректное описание файла Fabric API.")) }
        return .init(projectID: version.project_id, versionID: version.id, version: version.version_number, channel: version.version_type, filename: file.filename, url: file.url, size: file.size, sha1: sha1, sha512: sha512)
    }

    func prepare(minecraft: String, java: Int) async throws -> PreparedFabric {
        async let versions = loaderVersions(minecraft: minecraft)
        let api = try await latestAPI(minecraft: minecraft)
        let metadata = try await metadata(for: api)
        var compatible: [FabricLoaderVersion] = []
        for loader in try await versions where try metadata.supports(loader: loader.version, java: java) { compatible.append(loader) }
        guard !compatible.isEmpty else { throw MojangError.unsupported(String(appLocalized: "Fabric API \(api.version) требует другую версию Java или Fabric Loader.")) }
        return .init(api: api, metadata: metadata, loaders: compatible)
    }

    func cachedAPI(_ api: FabricAPIDescriptor) async throws -> URL {
        guard api.projectID == FabricAPIDescriptor.project, api.sha512.count == 128, api.sha512.allSatisfy(\.isHexDigit) else { throw MojangError.invalid(String(appLocalized: "Некорректное описание Fabric API.")) }
        let target = try InstanceStorage.containedURL("\(api.sha512).jar", in: cache)
        if try Self.validAPI(target, api: api) { return target }
        let bytes = try await data(api.url)
        guard Int64(bytes.count) == api.size, Self.hash(bytes) == api.sha512.lowercased() else { throw MojangError.invalid(String(appLocalized: "Загруженный Fabric API повреждён. Повторите загрузку.")) }
        try MojangIntegrity.check(bytes, download: .init(url: api.url, sha1: api.sha1, size: api.size))
        try Task.checkCancellation()
        try FileManager.default.createDirectory(at: cache, withIntermediateDirectories: true)
        try bytes.write(to: target, options: .atomic)
        return target
    }

    func metadata(for api: FabricAPIDescriptor) async throws -> FabricModMetadata {
        let file = try await cachedAPI(api)
        return try await Self.readMetadata(file)
    }

    @concurrent private static func readMetadata(_ archive: URL) async throws -> FabricModMetadata {
        return try JSONDecoder().decode(FabricModMetadata.self, from: await archiveEntry("fabric.mod.json", in: archive, limit: 1_048_576))
    }

    @concurrent static func archiveEntry(_ entry: String, in archive: URL, limit: Int) async throws -> Data {
        guard !entry.hasPrefix("/"), !entry.contains("\\"), !entry.contains(where: { "*?[]".contains($0) }), entry.split(separator: "/", omittingEmptySubsequences: false).allSatisfy({ !$0.isEmpty && $0 != "." && $0 != ".." }) else { throw MojangError.invalid(String(appLocalized: "Некорректный путь внутри JAR.")) }
        try Task.checkCancellation()
        let output = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        guard FileManager.default.createFile(atPath: output.path, contents: nil, attributes: [.posixPermissions: 0o600]) else { throw MojangError.invalid(String(appLocalized: "Не удалось прочитать Fabric API.")) }
        defer { try? FileManager.default.removeItem(at: output) }
        let handle = try FileHandle(forWritingTo: output)
        defer { try? handle.close() }
        let process = Process(); process.executableURL = URL(fileURLWithPath: "/usr/bin/unzip")
        // waitUntilExit крутит run loop текущего потока и на потоках Swift Concurrency может не вернуться.
        defer { if process.isRunning { kill(process.processIdentifier, SIGKILL); while process.isRunning { usleep(1_000) } } }
        process.arguments = ["-p", archive.path, entry]; process.standardOutput = handle; process.standardError = FileHandle.nullDevice
        try process.run()
        let deadline = Date().addingTimeInterval(10)
        while process.isRunning {
            let size = try output.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
            if Task.isCancelled || Date() > deadline || size > limit {
                process.interrupt(); process.terminate(); kill(process.processIdentifier, SIGKILL); while process.isRunning { usleep(1_000) }
                try Task.checkCancellation()
                throw MojangError.invalid(String(appLocalized: "Не удалось прочитать metadata Fabric API."))
            }
            try await Task.sleep(for: .milliseconds(20))
        }
        guard process.terminationStatus == 0, (try output.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0) <= limit else { throw MojangError.invalid(String(appLocalized: "В Fabric API отсутствует fabric.mod.json.")) }
        return try Data(contentsOf: output)
    }

    func profile(minecraft: String, loader: String) async throws -> (FabricProfile, Data) {
        let url = metaURL.appendingPathComponent("versions/loader").appendingPathComponent(minecraft).appendingPathComponent(loader).appendingPathComponent("profile/json")
        let bytes = try await data(url)
        let profile = try JSONDecoder().decode(FabricProfile.self, from: bytes)
        try profile.validate(minecraft: minecraft)
        return (profile, bytes)
    }

    func libraries(_ profile: FabricProfile) async throws -> [LibraryInstallation] {
        var files: [LibraryInstallation] = []
        for library in profile.libraries {
            let path = try library.path
            let url = library.url.appendingPathComponent(path)
            let checksum: String
            if let sha1 = library.sha1 { checksum = sha1 }
            else { checksum = String(decoding: try await data(URL(string: url.absoluteString + ".sha1")!), as: UTF8.self).split(whereSeparator: \.isWhitespace).first.map(String.init) ?? "" }
            guard checksum.count == 40, checksum.allSatisfy(\.isHexDigit) else { throw MojangError.invalid(String(appLocalized: "Отсутствует контрольная сумма библиотеки Fabric.")) }
            files.append(.init(download: .init(url: url, sha1: checksum, size: library.size, path: path), path: path, extractionExcludes: nil))
        }
        return files
    }

    nonisolated static func hash(_ data: Data) -> String { SHA512.hash(data: data).map { String(format: "%02x", $0) }.joined() }
    nonisolated static func validAPI(_ file: URL, api: FabricAPIDescriptor) throws -> Bool {
        guard FileManager.default.fileExists(atPath: file.path), try file.resourceValues(forKeys: [.fileSizeKey]).fileSize.map(Int64.init) == api.size else { return false }
        return try hashFile(file) == api.sha512.lowercased()
    }
    nonisolated static func hashFile(_ file: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: file); defer { try? handle.close() }
        var hash = SHA512()
        while let data = try handle.read(upToCount: 1_048_576), !data.isEmpty { try Task.checkCancellation(); hash.update(data: data) }
        return hash.finalize().map { String(format: "%02x", $0) }.joined()
    }
}

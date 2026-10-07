import Foundation

nonisolated enum ModrinthContentKind: Sendable {
    case mod, resourcePack, datapack
    var loader: String { switch self { case .mod: "fabric"; case .resourcePack: "minecraft"; case .datapack: "datapack" } }
    var fileExtension: String { self == .mod ? ".jar" : ".zip" }
}

nonisolated enum ModrinthInstallTarget: Hashable, Sendable {
    case mods, packs, worldDatapacks(String)
    var kind: ModrinthContentKind { switch self { case .mods: .mod; case .packs: .resourcePack; case .worldDatapacks: .datapack } }
    var world: String? { if case .worldDatapacks(let folder) = self { folder } else { nil } }
}

nonisolated enum ModrinthSort: String, CaseIterable, Identifiable, Sendable {
    case relevance, downloads, newest, updated
    var id: Self { self }
    var title: String {
        switch self { case .relevance: String(appLocalized: "Релевантность"); case .downloads: String(appLocalized: "Загрузки"); case .newest: String(appLocalized: "Новые"); case .updated: String(appLocalized: "Обновлённые") }
    }
}

nonisolated struct ModrinthProject: Decodable, Identifiable, Sendable {
    let id: String
    let slug: String?
    let title: String
    let description: String
    let iconURL: URL?
    let projectType: String
    let allProjectTypes: [String]
    var pageURL: URL { URL(string: "https://modrinth.com")!.appendingPathComponent(projectType).appendingPathComponent(slug ?? id) }

    private enum CodingKeys: String, CodingKey { case id, projectID = "project_id", slug, title, description, iconURL = "icon_url", projectType = "project_type", allProjectTypes = "all_project_types" }
    /// Поиск отдаёт `project_id`, а `/projects` — `id`.
    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        id = try values.decodeIfPresent(String.self, forKey: .projectID) ?? values.decode(String.self, forKey: .id)
        slug = try values.decodeIfPresent(String.self, forKey: .slug)
        title = try values.decode(String.self, forKey: .title)
        description = try values.decodeIfPresent(String.self, forKey: .description) ?? ""
        iconURL = try values.decodeIfPresent(String.self, forKey: .iconURL).flatMap { $0.isEmpty ? nil : URL(string: $0) }
        projectType = try values.decode(String.self, forKey: .projectType)
        allProjectTypes = try values.decodeIfPresent([String].self, forKey: .allProjectTypes) ?? [projectType]
    }
}

nonisolated struct ModrinthSearchPage: Decodable, Sendable {
    let hits: [ModrinthProject]
    let total: Int
    private enum CodingKeys: String, CodingKey { case hits, total = "total_hits" }
}

nonisolated struct ModrinthVersion: Decodable, Sendable {
    struct File: Decodable, Sendable {
        let filename: String
        let url: URL
        let size: Int64
        let hashes: [String: String]
        let primary: Bool
        let fileType: String?
        var sha1: String { hashes["sha1"]?.lowercased() ?? "" }
        var sha512: String { hashes["sha512"]?.lowercased() ?? "" }
        private enum CodingKeys: String, CodingKey { case filename, url, size, hashes, primary, fileType = "file_type" }
    }
    struct Dependency: Decodable, Sendable {
        let versionID: String?
        let projectID: String?
        let type: String
        private enum CodingKeys: String, CodingKey { case versionID = "version_id", projectID = "project_id", type = "dependency_type" }
    }
    let id: String
    let projectID: String
    let number: String
    let channel: String
    let published: String
    let gameVersions: [String]
    let loaders: [String]
    let files: [File]
    let dependencies: [Dependency]?
    private enum CodingKeys: String, CodingKey {
        case id, projectID = "project_id", number = "version_number", channel = "version_type", published = "date_published"
        case gameVersions = "game_versions", loaders, files, dependencies
    }

    /// Проверяемый файл мода (`.jar`) или ресурспака (`.zip`); версии без него не устанавливаются.
    func file(mods: Bool) -> File? {
        file(kind: mods ? .mod : .resourcePack)
    }

    func file(kind: ModrinthContentKind) -> File? {
        let files = files.filter { file in
            !file.filename.contains("/") && !file.filename.contains("\\") && !file.filename.hasPrefix(".")
                && file.filename.lowercased().hasSuffix(kind.fileExtension) && file.url.scheme == "https" && file.size > 0
                && !["sources-jar", "dev-jar", "javadoc-jar"].contains(file.fileType ?? "")
                && file.sha1.count == 40 && file.sha512.count == 128 && (file.sha1 + file.sha512).allSatisfy(\.isHexDigit)
        }
        return files.first(where: \.primary) ?? files.first
    }
}

/// Версия Modrinth, которой соответствует локальный файл.
nonisolated struct ModrinthFileMatch: Codable, Sendable {
    let projectID: String
    let versionID: String
    let published: String
    let sha512: String
}

actor ModrinthClient {
    private struct FileCache: Codable {
        struct Entry: Codable { let size: Int64; let modified: Double; let sha512: String }
        var files: [String: Entry] = [:]
        var versions: [String: ModrinthFileMatch] = [:]
    }

    private let session: URLSession
    private let baseURL: URL
    private let cache: URL
    private var fileCache: FileCache?
    /// Хеши без проекта помнятся до закрытия Hako: файл могут опубликовать позже.
    private var unknownHashes: Set<String> = []

    init(session: URLSession = URLSession(configuration: .ephemeral), baseURL: URL = URL(string: "https://api.modrinth.com/v2")!, cache: URL = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0].appendingPathComponent("Hako/Modrinth")) {
        self.session = session; self.baseURL = baseURL; self.cache = cache
    }

    private func request(_ url: URL) -> URLRequest {
        var request = URLRequest(url: url)
        request.setValue("Hako/\(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0") (https://github.com/Liis17/Hako)", forHTTPHeaderField: "User-Agent")
        return request
    }

    private func data(_ request: URLRequest) async throws -> Data {
        guard request.url?.scheme == "https" else { throw MojangError.invalid(String(appLocalized: "Ссылка Modrinth должна использовать HTTPS.")) }
        let (bytes, response) = try await session.data(for: request)
        guard let response = response as? HTTPURLResponse else { throw MojangError.invalid(String(appLocalized: "Некорректный ответ сервера Modrinth.")) }
        guard (200..<300).contains(response.statusCode) else { throw MojangError.http(response.statusCode) }
        try Task.checkCancellation()
        return bytes
    }

    private func url(_ path: String, _ query: [String: String]) throws -> URL {
        var components = URLComponents(url: baseURL.appendingPathComponent(path), resolvingAgainstBaseURL: false)!
        components.queryItems = query.sorted { $0.key < $1.key }.map { URLQueryItem(name: $0.key, value: $0.value) }
        // URLComponents оставляет «+», а сервер читает его как пробел.
        components.percentEncodedQuery = components.percentEncodedQuery?.replacingOccurrences(of: "+", with: "%2B")
        guard let url = components.url else { throw MojangError.invalid(String(appLocalized: "Не удалось составить запрос к Modrinth.")) }
        return url
    }

    private func json<T: Encodable>(_ value: T) throws -> String { String(decoding: try JSONEncoder().encode(value), as: UTF8.self) }

    private func checkedIDs(_ ids: [String]) throws -> [String] {
        guard ids.allSatisfy({ !$0.isEmpty && $0.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber) } }) else { throw MojangError.invalid(String(appLocalized: "Некорректный идентификатор проекта Modrinth.")) }
        return ids
    }

    func search(_ query: String, mods: Bool, minecraft: String, sort: ModrinthSort, offset: Int) async throws -> ModrinthSearchPage {
        try await search(query, kind: mods ? .mod : .resourcePack, minecraft: minecraft, sort: sort, offset: offset)
    }

    func search(_ query: String, kind: ModrinthContentKind, minecraft: String, sort: ModrinthSort, offset: Int) async throws -> ModrinthSearchPage {
        var facets: [[String]] = switch kind {
        case .mod: [["project_type:mod"], ["categories:fabric"]]
        case .resourcePack: [["project_type:resourcepack"]]
        case .datapack: [["all_project_types:datapack"], ["categories:datapack"]]
        }
        facets.append(["versions:\(minecraft)"])
        if kind == .mod { facets.append(["environment!=dedicated_server_only"]) }
        var parameters = ["facets": try json(facets), "index": sort.rawValue, "offset": String(offset), "limit": "20"]
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines)
        if !query.isEmpty { parameters["query"] = query }
        return try JSONDecoder().decode(ModrinthSearchPage.self, from: await data(request(url("search", parameters))))
    }

    /// Последняя версия для Minecraft и загрузчика: release, затем beta, затем alpha; внутри канала — новейшая.
    /// С `channel` — новейшая версия только этого канала.
    func latestVersion(project: String, mods: Bool, minecraft: String, channel: String? = nil) async throws -> ModrinthVersion? {
        try await latestVersion(project: project, kind: mods ? .mod : .resourcePack, minecraft: minecraft, channel: channel)
    }

    func latestVersion(project: String, kind: ModrinthContentKind, minecraft: String, channel: String? = nil) async throws -> ModrinthVersion? {
        try await compatibleVersions(project: project, kind: kind, minecraft: minecraft).first { channel == nil || $0.channel == channel }
    }

    /// Устанавливаемые версии для Minecraft и загрузчика: release, затем beta, затем alpha; внутри канала — новейшие первыми.
    func compatibleVersions(project: String, mods: Bool, minecraft: String) async throws -> [ModrinthVersion] {
        try await compatibleVersions(project: project, kind: mods ? .mod : .resourcePack, minecraft: minecraft)
    }

    func compatibleVersions(project: String, kind: ModrinthContentKind, minecraft: String) async throws -> [ModrinthVersion] {
        let loader = kind.loader
        let path = "project/\(try checkedIDs([project])[0])/version"
        let versions = try JSONDecoder().decode([ModrinthVersion].self, from: await data(request(url(path, ["game_versions": try json([minecraft]), "loaders": try json([loader]), "include_changelog": "false"]))))
        let priority = ["release": 0, "beta": 1, "alpha": 2]
        return versions.filter { $0.projectID == project && $0.gameVersions.contains(minecraft) && $0.loaders.contains(loader) && priority[$0.channel] != nil && $0.file(kind: kind) != nil }.sorted {
            let lhs = priority[$0.channel]!, rhs = priority[$1.channel]!
            return lhs == rhs ? $0.published > $1.published : lhs < rhs
        }
    }

    func projects(_ ids: [String]) async throws -> [ModrinthProject] {
        guard !ids.isEmpty else { return [] }
        return try JSONDecoder().decode([ModrinthProject].self, from: await data(request(url("projects", ["ids": try json(checkedIDs(ids))]))))
    }

    func versions(_ ids: [String]) async throws -> [ModrinthVersion] {
        guard !ids.isEmpty else { return [] }
        return try JSONDecoder().decode([ModrinthVersion].self, from: await data(request(url("versions", ["ids": try json(checkedIDs(ids))]))))
    }

    private func post(_ path: String, _ body: [String: Any]) throws -> URLRequest {
        var request = request(baseURL.appendingPathComponent(path))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        return request
    }

    /// Узнаёт версии Modrinth по SHA-512 локальных файлов, в том числе скачанных вручную.
    /// Хеш берётся из кеша, пока у файла не изменились размер и дата; известные хеши не запрашиваются повторно.
    func versions(of files: [URL]) async throws -> [URL: ModrinthFileMatch] {
        let cacheFile = cache.appendingPathComponent("files.json")
        if fileCache == nil { fileCache = (try? Data(contentsOf: cacheFile)).flatMap { try? JSONDecoder().decode(FileCache.self, from: $0) } ?? FileCache() }
        var hashes: [URL: String] = [:], changed = false
        for file in files {
            var fresh = file; fresh.removeAllCachedResourceValues()
            let values = try fresh.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey])
            let size = Int64(values.fileSize ?? -1), modified = values.contentModificationDate?.timeIntervalSince1970 ?? 0
            if let entry = fileCache?.files[file.path], entry.size == size, entry.modified == modified { hashes[file] = entry.sha512; continue }
            let sha512 = try FabricClient.hashFile(file)
            hashes[file] = sha512; fileCache?.files[file.path] = .init(size: size, modified: modified, sha512: sha512); changed = true
        }
        let unknown = Set(hashes.values).filter { fileCache?.versions[$0] == nil && !unknownHashes.contains($0) }
        if !unknown.isEmpty {
            struct Match: Decodable { let id: String; let project_id: String; let date_published: String }
            let matches = try JSONDecoder().decode([String: Match].self, from: await data(post("version_files", ["hashes": Array(unknown), "algorithm": "sha512"])))
            for hash in unknown {
                if let match = matches[hash] { fileCache?.versions[hash] = .init(projectID: match.project_id, versionID: match.id, published: match.date_published, sha512: hash); changed = true }
                else { unknownHashes.insert(hash) }
            }
        }
        if changed, var stored = fileCache {
            // Записи удалённых файлов и их версий не накапливаются.
            stored.files = stored.files.filter { FileManager.default.fileExists(atPath: $0.key) }
            let used = Set(stored.files.values.map(\.sha512))
            stored.versions = stored.versions.filter { used.contains($0.key) }
            fileCache = stored
            try? FileManager.default.createDirectory(at: cache, withIntermediateDirectories: true)
            try? JSONEncoder().encode(stored).write(to: cacheFile, options: .atomic)
        }
        return hashes.compactMapValues { fileCache?.versions[$0] }
    }

    /// Новейшие версии проектов по SHA-512 их файлов с тем же приоритетом каналов, что у `latestVersion`.
    func latestVersions(for hashes: [String], mods: Bool, minecraft: String) async throws -> [String: ModrinthVersion] {
        let loader = mods ? "fabric" : "minecraft"
        var remaining = Set(hashes), result: [String: ModrinthVersion] = [:]
        for channel in ["release", "beta", "alpha"] where !remaining.isEmpty {
            let body: [String: Any] = ["hashes": Array(remaining), "algorithm": "sha512", "loaders": [loader], "game_versions": [minecraft], "version_types": [channel]]
            let found = try JSONDecoder().decode([String: ModrinthVersion].self, from: await data(post("version_files/update", body)))
            for (hash, version) in found where remaining.contains(hash) && version.channel == channel && version.gameVersions.contains(minecraft) && version.loaders.contains(loader) && version.file(mods: mods) != nil {
                result[hash] = version; remaining.remove(hash)
            }
        }
        return result
    }

    /// Загружает файл в `folder` под именем из Modrinth и проверяет размер и SHA-512.
    func download(_ file: ModrinthVersion.File, into folder: URL) async throws -> URL {
        guard file.url.scheme == "https" else { throw MojangError.invalid(String(appLocalized: "Ссылка загрузки должна использовать HTTPS.")) }
        let (temporary, response) = try await session.download(for: request(file.url))
        defer { try? FileManager.default.removeItem(at: temporary) }
        guard let response = response as? HTTPURLResponse else { throw MojangError.invalid(String(appLocalized: "Некорректный ответ сервера Modrinth.")) }
        guard (200..<300).contains(response.statusCode) else { throw MojangError.http(response.statusCode) }
        try Task.checkCancellation()
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let target = try InstanceStorage.containedURL(file.filename, in: folder)
        try FileManager.default.moveItem(at: temporary, to: target)
        guard try target.resourceValues(forKeys: [.fileSizeKey]).fileSize.map(Int64.init) == file.size, try FabricClient.hashFile(target) == file.sha512 else {
            try? FileManager.default.removeItem(at: target)
            throw MojangError.invalid(String(appLocalized: "Загруженный файл \(file.filename) повреждён. Повторите загрузку."))
        }
        return target
    }
}

import Foundation

nonisolated enum ModrinthSort: String, CaseIterable, Identifiable, Sendable {
    case relevance, downloads, newest, updated
    var id: Self { self }
    var title: String {
        switch self { case .relevance: "Релевантность"; case .downloads: "Загрузки"; case .newest: "Новые"; case .updated: "Обновлённые" }
    }
}

nonisolated struct ModrinthProject: Decodable, Identifiable, Sendable {
    let id: String
    let slug: String?
    let title: String
    let description: String
    let iconURL: URL?
    let projectType: String
    var pageURL: URL { URL(string: "https://modrinth.com")!.appendingPathComponent(projectType).appendingPathComponent(slug ?? id) }

    private enum CodingKeys: String, CodingKey { case id, projectID = "project_id", slug, title, description, iconURL = "icon_url", projectType = "project_type" }
    /// Поиск отдаёт `project_id`, а `/projects` — `id`.
    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        id = try values.decodeIfPresent(String.self, forKey: .projectID) ?? values.decode(String.self, forKey: .id)
        slug = try values.decodeIfPresent(String.self, forKey: .slug)
        title = try values.decode(String.self, forKey: .title)
        description = try values.decodeIfPresent(String.self, forKey: .description) ?? ""
        iconURL = try values.decodeIfPresent(String.self, forKey: .iconURL).flatMap { $0.isEmpty ? nil : URL(string: $0) }
        projectType = try values.decode(String.self, forKey: .projectType)
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
        let files = files.filter { file in
            !file.filename.contains("/") && !file.filename.contains("\\") && !file.filename.hasPrefix(".")
                && file.filename.lowercased().hasSuffix(mods ? ".jar" : ".zip") && file.url.scheme == "https" && file.size > 0
                && !["sources-jar", "dev-jar", "javadoc-jar"].contains(file.fileType ?? "")
                && file.sha1.count == 40 && file.sha512.count == 128 && (file.sha1 + file.sha512).allSatisfy(\.isHexDigit)
        }
        return files.first(where: \.primary) ?? files.first
    }
}

actor ModrinthClient {
    private let session: URLSession
    private let baseURL: URL

    init(session: URLSession = URLSession(configuration: .ephemeral), baseURL: URL = URL(string: "https://api.modrinth.com/v2")!) {
        self.session = session; self.baseURL = baseURL
    }

    private func request(_ url: URL) -> URLRequest {
        var request = URLRequest(url: url)
        request.setValue("Hako/\(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0") (https://github.com/Liis17/Hako)", forHTTPHeaderField: "User-Agent")
        return request
    }

    private func data(_ request: URLRequest) async throws -> Data {
        guard request.url?.scheme == "https" else { throw MojangError.invalid("Ссылка Modrinth должна использовать HTTPS.") }
        let (bytes, response) = try await session.data(for: request)
        guard let response = response as? HTTPURLResponse else { throw MojangError.invalid("Некорректный ответ сервера Modrinth.") }
        guard (200..<300).contains(response.statusCode) else { throw MojangError.http(response.statusCode) }
        try Task.checkCancellation()
        return bytes
    }

    private func url(_ path: String, _ query: [String: String]) throws -> URL {
        var components = URLComponents(url: baseURL.appendingPathComponent(path), resolvingAgainstBaseURL: false)!
        components.queryItems = query.sorted { $0.key < $1.key }.map { URLQueryItem(name: $0.key, value: $0.value) }
        // URLComponents оставляет «+», а сервер читает его как пробел.
        components.percentEncodedQuery = components.percentEncodedQuery?.replacingOccurrences(of: "+", with: "%2B")
        guard let url = components.url else { throw MojangError.invalid("Не удалось составить запрос к Modrinth.") }
        return url
    }

    private func json<T: Encodable>(_ value: T) throws -> String { String(decoding: try JSONEncoder().encode(value), as: UTF8.self) }

    private func checkedIDs(_ ids: [String]) throws -> [String] {
        guard ids.allSatisfy({ !$0.isEmpty && $0.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber) } }) else { throw MojangError.invalid("Некорректный идентификатор проекта Modrinth.") }
        return ids
    }

    func search(_ query: String, mods: Bool, minecraft: String, sort: ModrinthSort, offset: Int) async throws -> ModrinthSearchPage {
        var facets = mods ? [["project_type:mod"], ["categories:fabric"]] : [["project_type:resourcepack"]]
        facets.append(["versions:\(minecraft)"])
        if mods { facets += [["environment!=server_only"], ["environment!=dedicated_server_only"]] }
        var parameters = ["facets": try json(facets), "index": sort.rawValue, "offset": String(offset), "limit": "20"]
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines)
        if !query.isEmpty { parameters["query"] = query }
        return try JSONDecoder().decode(ModrinthSearchPage.self, from: await data(request(url("search", parameters))))
    }

    /// Последняя версия для Minecraft и загрузчика: release, затем beta, затем alpha; внутри канала — новейшая.
    func latestVersion(project: String, mods: Bool, minecraft: String) async throws -> ModrinthVersion? {
        let loader = mods ? "fabric" : "minecraft"
        let path = "project/\(try checkedIDs([project])[0])/version"
        let versions = try JSONDecoder().decode([ModrinthVersion].self, from: await data(request(url(path, ["game_versions": try json([minecraft]), "loaders": try json([loader]), "include_changelog": "false"]))))
        let priority = ["release": 0, "beta": 1, "alpha": 2]
        return versions.filter { $0.projectID == project && $0.gameVersions.contains(minecraft) && $0.loaders.contains(loader) && priority[$0.channel] != nil && $0.file(mods: mods) != nil }.sorted {
            let lhs = priority[$0.channel]!, rhs = priority[$1.channel]!
            return lhs == rhs ? $0.published > $1.published : lhs < rhs
        }.first
    }

    func projects(_ ids: [String]) async throws -> [ModrinthProject] {
        guard !ids.isEmpty else { return [] }
        return try JSONDecoder().decode([ModrinthProject].self, from: await data(request(url("projects", ["ids": try json(checkedIDs(ids))]))))
    }

    func versions(_ ids: [String]) async throws -> [ModrinthVersion] {
        guard !ids.isEmpty else { return [] }
        return try JSONDecoder().decode([ModrinthVersion].self, from: await data(request(url("versions", ["ids": try json(checkedIDs(ids))]))))
    }

    /// Узнаёт проекты Modrinth по SHA-512 локальных файлов, в том числе скачанных вручную.
    func projectIDs(of files: [URL]) async throws -> [URL: String] {
        var hashes: [URL: String] = [:]
        for file in files { hashes[file] = try FabricClient.hashFile(file) }
        guard !hashes.isEmpty else { return [:] }
        struct Match: Decodable { let project_id: String }
        var request = request(baseURL.appendingPathComponent("version_files"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: ["hashes": Array(Set(hashes.values)), "algorithm": "sha512"])
        let matches = try JSONDecoder().decode([String: Match].self, from: await data(request))
        return hashes.compactMapValues { matches[$0]?.project_id }
    }

    /// Загружает файл в `folder` под именем из Modrinth и проверяет размер и SHA-512.
    func download(_ file: ModrinthVersion.File, into folder: URL) async throws -> URL {
        guard file.url.scheme == "https" else { throw MojangError.invalid("Ссылка загрузки должна использовать HTTPS.") }
        let (temporary, response) = try await session.download(for: request(file.url))
        defer { try? FileManager.default.removeItem(at: temporary) }
        guard let response = response as? HTTPURLResponse else { throw MojangError.invalid("Некорректный ответ сервера Modrinth.") }
        guard (200..<300).contains(response.statusCode) else { throw MojangError.http(response.statusCode) }
        try Task.checkCancellation()
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let target = try InstanceStorage.containedURL(file.filename, in: folder)
        try FileManager.default.moveItem(at: temporary, to: target)
        guard try target.resourceValues(forKeys: [.fileSizeKey]).fileSize.map(Int64.init) == file.size, try FabricClient.hashFile(target) == file.sha512 else {
            try? FileManager.default.removeItem(at: target)
            throw MojangError.invalid("Загруженный файл \(file.filename) повреждён. Повторите загрузку.")
        }
        return target
    }
}

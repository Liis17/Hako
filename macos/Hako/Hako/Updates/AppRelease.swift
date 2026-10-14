import CryptoKit
import Foundation

nonisolated struct AppReleaseVersion: Comparable, Sendable {
    let major: UInt64
    let minor: UInt64
    let patch: UInt64

    static func parseTag(_ tag: String) -> Self? {
        let prefix = "macos-v"
        guard tag.hasPrefix(prefix) else { return nil }
        let components = tag.dropFirst(prefix.count).split(separator: ".", omittingEmptySubsequences: false)
        guard components.count == 3 else { return nil }
        return parse(components)
    }

    /// Bundle versions from the old macOS build may have only major and minor components.
    static func parse(_ value: String) -> Self? {
        let components = value.split(separator: ".", omittingEmptySubsequences: false)
        guard components.count == 2 || components.count == 3 else { return nil }
        return parse(components)
    }

    private static func parse(_ components: [Substring]) -> Self? {
        func number(_ component: Substring) -> UInt64? {
            let value = String(component)
            guard !value.isEmpty,
                  value == "0" || value.first != "0",
                  value.utf8.allSatisfy({ (48...57).contains($0) }) else { return nil }
            return UInt64(value)
        }

        guard let major = number(components[0]), let minor = number(components[1]) else { return nil }
        let patch: UInt64
        if components.count == 3 {
            guard let value = number(components[2]) else { return nil }
            patch = value
        } else {
            patch = 0
        }
        return Self(major: major, minor: minor, patch: patch)
    }

    static func < (lhs: Self, rhs: Self) -> Bool {
        if lhs.major != rhs.major { return lhs.major < rhs.major }
        if lhs.minor != rhs.minor { return lhs.minor < rhs.minor }
        return lhs.patch < rhs.patch
    }

    var description: String { "\(major).\(minor).\(patch)" }
}

/// Стабильный macOS-релиз Hako на GitHub.
nonisolated struct AppRelease: Decodable, Equatable, Sendable {
    struct Asset: Decodable, Equatable, Sendable {
        let name: String
        let url: URL
        let size: Int64
        let digest: String?
        /// GitHub отдаёт хеш ассета как `sha256:<hex>`.
        var sha256: String? { digest.flatMap { $0.hasPrefix("sha256:") ? String($0.dropFirst(7)).lowercased() : nil } }
        private enum CodingKeys: String, CodingKey { case name, url = "browser_download_url", size, digest }
    }

    static let dmgName = "Hako.dmg"

    /// CI создаёт релиз на коммите сборки; SHA используется для подтверждения происхождения и установки.
    let commit: String
    let tagName: String
    let name: String
    let publishedAt: Date?
    let pageURL: URL
    let draft: Bool
    let prerelease: Bool
    let assets: [Asset]

    var dmg: Asset? { assets.first { $0.name == Self.dmgName } }
    var version: String? { parsedVersion?.description }
    fileprivate var parsedVersion: AppReleaseVersion? { AppReleaseVersion.parseTag(tagName) }

    var isStableMacOSRelease: Bool {
        !draft && !prerelease && publishedAt != nil && parsedVersion != nil && Self.isFullCommit(commit) && dmg != nil
    }

    /// Обновление доступно только для более новой числовой версии.
    func isUpdate(for currentVersion: String?) -> Bool {
        guard isStableMacOSRelease,
              let releaseVersion = parsedVersion,
              let current = currentVersion.flatMap({ AppReleaseVersion.parse($0) }) else { return false }
        return releaseVersion > current
    }

    static func isFullCommit(_ commit: String) -> Bool {
        commit.utf8.count == 40 && commit.utf8.allSatisfy {
            (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0)
        }
    }

    private enum CodingKeys: String, CodingKey {
        case commit = "target_commitish", tagName = "tag_name", name, publishedAt = "published_at"
        case pageURL = "html_url", draft, prerelease, assets
    }

    static func decode(_ data: Data) throws -> AppRelease {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try decoder.decode(AppRelease.self, from: data)
    }

    static func decodeList(_ data: Data) throws -> [AppRelease] {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try decoder.decode([AppRelease].self, from: data)
    }
}

nonisolated struct AppReleaseClient: Sendable {
    static let releasesURL = URL(string: "https://api.github.com/repos/Liis17/Hako/releases")!
    static let releasesPage = URL(string: "https://github.com/Liis17/Hako/releases")!
    private let session: URLSession

    init(session: URLSession = URLSession(configuration: .ephemeral)) { self.session = session }

    private func request(_ url: URL) -> URLRequest {
        var request = URLRequest(url: url)
        request.setValue("Hako/\(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0") (https://github.com/Liis17/Hako)", forHTTPHeaderField: "User-Agent")
        return request
    }

    /// Возвращает максимальную стабильную версию macOS со всех страниц списка релизов.
    func latest() async throws -> AppRelease? {
        var components = URLComponents(url: Self.releasesURL, resolvingAgainstBaseURL: false)!
        components.queryItems = [URLQueryItem(name: "per_page", value: "100"), URLQueryItem(name: "page", value: "1")]
        var url = components.url!
        var releases: [AppRelease] = []

        while true {
            var request = request(url)
            request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
            let (data, response) = try await session.data(for: request)
            guard let response = response as? HTTPURLResponse else {
                throw MojangError.invalid(String(appLocalized: "Некорректный ответ GitHub."))
            }
            if response.statusCode == 404 { return nil }
            guard (200..<300).contains(response.statusCode) else { throw MojangError.http(response.statusCode) }
            do { releases += try AppRelease.decodeList(data) }
            catch { throw MojangError.invalid(String(appLocalized: "Некорректный ответ GitHub.")) }
            guard let next = try Self.nextPageURL(from: response, currentURL: url) else { break }
            url = next
        }

        let candidates = releases.compactMap { release -> (AppRelease, AppReleaseVersion)? in
            guard release.isStableMacOSRelease, let version = release.parsedVersion else { return nil }
            return (release, version)
        }
        return candidates.max { $0.1 < $1.1 }?.0
    }

    private static func nextPageURL(from response: HTTPURLResponse, currentURL: URL) throws -> URL? {
        guard let header = response.value(forHTTPHeaderField: "Link") else { return nil }
        for link in header.split(separator: ",") {
            let parts = link.split(separator: ";", omittingEmptySubsequences: false)
            guard let target = parts.first, parts.dropFirst().contains(where: isNextRelation) else { continue }
            let value = String(target).trimmingCharacters(in: .whitespacesAndNewlines)
            guard value.hasPrefix("<"), value.hasSuffix(">"),
                  let url = URL(string: String(value.dropFirst().dropLast()), relativeTo: currentURL)?.absoluteURL,
                  url.scheme == "https", url.host == "api.github.com", url.path == Self.releasesURL.path else {
                throw MojangError.invalid(String(appLocalized: "Некорректный ответ GitHub."))
            }
            return url
        }
        return nil
    }

    private static func isNextRelation(_ attribute: Substring) -> Bool {
        let parts = attribute.split(separator: "=", maxSplits: 1)
        guard parts.count == 2 else { return false }
        let name = String(parts[0]).trimmingCharacters(in: .whitespacesAndNewlines)
        let value = String(parts[1]).trimmingCharacters(in: CharacterSet(charactersIn: "\"' \t"))
        return name == "rel" && value.split(whereSeparator: \.isWhitespace).contains("next")
    }

    /// Загружает DMG в `folder` и проверяет размер и SHA-256 из релиза.
    func download(_ asset: AppRelease.Asset, into folder: URL, progress: @escaping @Sendable (Double) -> Void) async throws -> URL {
        guard asset.url.scheme == "https" else { throw MojangError.invalid(String(appLocalized: "Ссылка загрузки должна использовать HTTPS.")) }
        guard let sha256 = asset.sha256 else { throw MojangError.invalid(String(appLocalized: "GitHub не сообщил контрольную сумму обновления.")) }
        let (temporary, response) = try await session.download(for: request(asset.url), delegate: DownloadProgress(progress))
        defer { try? FileManager.default.removeItem(at: temporary) }
        guard let response = response as? HTTPURLResponse else { throw MojangError.invalid(String(appLocalized: "Некорректный ответ GitHub.")) }
        guard (200..<300).contains(response.statusCode) else { throw MojangError.http(response.statusCode) }
        try Task.checkCancellation()
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let target = folder.appendingPathComponent(AppRelease.dmgName)
        try? FileManager.default.removeItem(at: target)
        try FileManager.default.moveItem(at: temporary, to: target)
        guard try target.resourceValues(forKeys: [.fileSizeKey]).fileSize.map(Int64.init) == asset.size, try Self.hashFile(target) == sha256 else {
            try? FileManager.default.removeItem(at: target)
            throw MojangError.invalid(String(appLocalized: "Загруженное обновление повреждено. Повторите загрузку."))
        }
        return target
    }

    static func hashFile(_ file: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: file); defer { try? handle.close() }
        var hash = SHA256()
        while let data = try handle.read(upToCount: 1_048_576), !data.isEmpty { try Task.checkCancellation(); hash.update(data: data) }
        return hash.finalize().map { String(format: "%02x", $0) }.joined()
    }
}

/// Передаёт долю загрузки из `URLSessionTask.progress`.
private nonisolated final class DownloadProgress: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    private let handler: @Sendable (Double) -> Void
    private var observation: NSKeyValueObservation?

    init(_ handler: @escaping @Sendable (Double) -> Void) { self.handler = handler }

    func urlSession(_ session: URLSession, didCreateTask task: URLSessionTask) {
        observation = task.progress.observe(\.fractionCompleted) { [handler] progress, _ in handler(progress.fractionCompleted) }
    }
}

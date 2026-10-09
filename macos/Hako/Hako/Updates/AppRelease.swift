import CryptoKit
import Foundation

/// Релиз `nightly` на GitHub: CI пересоздаёт его на каждый push в main.
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

    /// CI создаёт релиз с `--target <SHA>`, поэтому здесь полный SHA коммита сборки.
    let commit: String
    let name: String
    let publishedAt: Date?
    let pageURL: URL
    let assets: [Asset]

    var dmg: Asset? { assets.first { $0.name == Self.dmgName } }
    var shortCommit: String { String(commit.prefix(7)) }

    /// Релиз только двигается вперёд, поэтому другой коммит означает более новую сборку.
    func isUpdate(for current: String?) -> Bool {
        guard let current, commit.count == 40, commit.allSatisfy(\.isHexDigit), dmg != nil else { return false }
        return commit.lowercased() != current.lowercased()
    }

    private enum CodingKeys: String, CodingKey { case commit = "target_commitish", name, publishedAt = "published_at", pageURL = "html_url", assets }

    static func decode(_ data: Data) throws -> AppRelease {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try decoder.decode(AppRelease.self, from: data)
    }
}

nonisolated struct AppReleaseClient: Sendable {
    static let releaseURL = URL(string: "https://api.github.com/repos/Liis17/Hako/releases/tags/nightly")!
    static let releasesPage = URL(string: "https://github.com/Liis17/Hako/releases")!
    private let session: URLSession

    init(session: URLSession = URLSession(configuration: .ephemeral)) { self.session = session }

    private func request(_ url: URL) -> URLRequest {
        var request = URLRequest(url: url)
        request.setValue("Hako/\(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0") (https://github.com/Liis17/Hako)", forHTTPHeaderField: "User-Agent")
        return request
    }

    /// `nil`, если релиза нет: CI удаляет его перед публикацией новой сборки.
    func latest() async throws -> AppRelease? {
        var request = request(Self.releaseURL)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        let (data, response) = try await session.data(for: request)
        guard let response = response as? HTTPURLResponse else { throw MojangError.invalid(String(appLocalized: "Некорректный ответ GitHub.")) }
        if response.statusCode == 404 { return nil }
        guard (200..<300).contains(response.statusCode) else { throw MojangError.http(response.statusCode) }
        do { return try AppRelease.decode(data) }
        catch { throw MojangError.invalid(String(appLocalized: "Некорректный ответ GitHub.")) }
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

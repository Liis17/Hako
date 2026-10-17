import Foundation

nonisolated struct ModpackManifest: Codable, Equatable, Sendable {
    struct File: Codable, Equatable, Sendable {
        struct Environment: Codable, Equatable, Sendable { let client: String; let server: String }
        let path: String
        let hashes: [String: String]
        let env: Environment?
        let downloads: [URL]
        let fileSize: Int64
        var installOnClient: Bool { env?.client != "unsupported" }
    }
    let formatVersion: Int
    let game: String
    let versionId: String
    let name: String
    let files: [File]
    let dependencies: [String: String]
    var minecraft: String { dependencies["minecraft"] ?? "" }
    var loaderVersion: String? { dependencies["fabric-loader"] }

    func validate() throws {
        guard formatVersion == 1, game == "minecraft", !minecraft.isEmpty,
              dependencies.keys.allSatisfy({ ["minecraft", "fabric-loader"].contains($0) }),
              dependencies.values.allSatisfy({ !$0.isEmpty }), files.count <= 50_000 else {
            throw ModpackError.invalidManifest
        }
        guard files.reduce(Int64(0), { $0 + max(0, min($1.fileSize, ModpackArchive.fileLimit)) }) <= ModpackArchive.totalLimit else { throw ModpackError.invalidManifest }
        var paths: [String: String] = [:]
        for file in files {
            try ModpackPaths.validate(file.path)
            try ModpackPaths.insert(file.path, into: &paths)
            guard file.fileSize > 0, file.fileSize <= ModpackArchive.fileLimit,
                  let sha1 = file.hashes["sha1"], let sha512 = file.hashes["sha512"],
                  sha1.count == 40, sha512.count == 128, (sha1 + sha512).allSatisfy(\.isHexDigit),
                  !file.downloads.isEmpty, file.downloads.allSatisfy(ModpackPaths.allowedDownload),
                  file.env.map({ ["required", "optional", "unsupported"].contains($0.client) && ["required", "optional", "unsupported"].contains($0.server) }) ?? true else {
                throw ModpackError.invalidManifest
            }
        }
    }
}

nonisolated struct ModpackPlan: Codable, Sendable {
    static let filename = ".hako-modpack.json"
    var formatVersion = 1
    let projectID: String
    let versionID: String
    let versionNumber: String
    let pageURL: URL
    let archive: ModrinthVersion.File
    let manifest: ModpackManifest
    var completed = false

    func validate() throws {
        guard formatVersion == 1, archive.isInstallable(extension: ".mrpack"), archive.size <= ModpackArchive.fileLimit else { throw ModpackError.invalidManifest }
        try manifest.validate()
    }

    static func read(in root: URL) throws -> Self? {
        let url = try InstanceStorage.containedURL(filename, in: root)
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        let plan = try JSONDecoder().decode(Self.self, from: Data(contentsOf: url))
        try plan.validate()
        return plan
    }

    func write(in root: URL) throws {
        try validate()
        try JSONEncoder().encode(self).write(to: InstanceStorage.containedURL(Self.filename, in: root), options: .atomic)
    }
}

nonisolated struct PreparedModpack: Sendable {
    let plan: ModpackPlan
    let minecraft: PreparedInstallation
}

nonisolated enum ModpackError: LocalizedError {
    case invalidManifest, unsafePath, archive, damagedFile(String)
    var errorDescription: String? {
        switch self {
        case .invalidManifest: String(appLocalized: "Модпак повреждён или использует неподдерживаемый формат, Minecraft или загрузчик.")
        case .unsafePath: String(appLocalized: "Модпак содержит небезопасные пути или конфликтующие файлы.")
        case .archive: String(appLocalized: "Не удалось прочитать архив модпака.")
        case .damagedFile(let name): String(appLocalized: "Файл модпака \(name) повреждён. Повторите загрузку.")
        }
    }
}

nonisolated enum ModpackPaths {
    static func key(_ path: String) -> String { path.precomposedStringWithCanonicalMapping.lowercased() }

    static func validate(_ path: String) throws {
        let parts = path.split(separator: "/", omittingEmptySubsequences: false)
        guard !path.isEmpty, !path.contains(where: { $0.isNewline || $0 == "\0" || "\\:*?[]".contains($0) }),
              parts.allSatisfy({ !$0.isEmpty && $0 != "." && $0 != ".." && !$0.lowercased().hasPrefix(".hako") }),
              !["versions", "libraries", "assets", "natives", "resources"].contains(parts.first?.lowercased() ?? "") else {
            throw ModpackError.unsafePath
        }
    }

    /// Same-case replacements between layers are allowed, file/directory and case collisions are not.
    static func insert(_ path: String, into paths: inout [String: String], replacing: Bool = false) throws {
        let folded = key(path)
        if let previous = paths[folded], !replacing || previous != path { throw ModpackError.unsafePath }
        guard paths[folded + "/"] == nil else { throw ModpackError.unsafePath }
        let parts = path.split(separator: "/")
        for count in 1..<parts.count {
            let parent = parts.prefix(count).joined(separator: "/")
            let parentKey = key(parent)
            guard paths[parentKey] == nil else { throw ModpackError.unsafePath }
            if let previous = paths[parentKey + "/"], previous != parent { throw ModpackError.unsafePath }
            paths[parentKey + "/"] = parent
        }
        paths[folded] = path
    }

    static func allowedDownload(_ url: URL) -> Bool {
        url.scheme == "https" && url.user == nil && url.password == nil
            && ["cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com"].contains(url.host?.lowercased() ?? "")
    }
}

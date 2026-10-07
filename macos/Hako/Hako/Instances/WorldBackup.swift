import Foundation

/// `data.json` в `.hakoworld`: все пути относительны папке `world/` внутри архива.
nonisolated struct WorldBackupManifest: Codable, Sendable {
    static let currentFormat = 1
    static let fileExtension = "hakoworld"

    struct SourceInstance: Codable, Sendable {
        let id: UUID
        let name: String
        let folderName: String
        let minecraftVersion: String
        let modLoader: String
        let loaderVersion: String?
    }

    struct Datapack: Codable, Sendable {
        let file: String
        let path: String
        let enabled: Bool
        let isDirectory: Bool
        let sha512: String?
        let iconPNG: Data?
    }

    var formatVersion = currentFormat
    var worldPath = "world"
    let backupCreatedAt: Date
    let name: String
    let folderName: String
    let saveVersion: String?
    let gameType: Int?
    let lastPlayed: Date?
    let size: Int64?
    let iconPNG: Data?
    let sourceInstance: SourceInstance
    let datapacks: [Datapack]

    static func encoder() -> JSONEncoder { InstanceBackupManifest.encoder() }
    static func decoder() -> JSONDecoder { InstanceBackupManifest.decoder() }
}

nonisolated enum WorldBackup {
    @concurrent static func archive(world: String, in instanceRoot: URL, manifest: WorldBackupManifest, into backups: URL) async throws -> URL {
        let source = try InstanceWorlds.worldFolder(world: world, in: instanceRoot)
        try InstanceWorlds.checkIndependentFiles(in: source)
        let manager = FileManager.default
        try manager.createDirectory(at: backups, withIntermediateDirectories: true)
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX"); formatter.dateFormat = "yyyy-MM-dd_HH-mm-ss"
        let target = try InstanceStorage.containedURL("\(manifest.sourceInstance.folderName)_\(world)_\(formatter.string(from: manifest.backupCreatedAt)).\(WorldBackupManifest.fileExtension)", in: backups)
        guard !manager.fileExists(atPath: target.path) else { throw InstanceFileError.message(String(appLocalized: "Резервная копия с таким именем уже существует. Повторите через секунду.")) }
        let partial = backups.appendingPathComponent(".\(UUID().uuidString).partial")
        let staging = manager.temporaryDirectory.appendingPathComponent("Hako-World-\(UUID().uuidString)")
        defer { try? manager.removeItem(at: partial); try? manager.removeItem(at: staging) }
        try manager.createDirectory(at: staging, withIntermediateDirectories: false)
        try manager.copyItem(at: source, to: staging.appendingPathComponent("world"))
        try InstanceWorlds.checkIndependentFiles(in: staging.appendingPathComponent("world"))
        try WorldBackupManifest.encoder().encode(manifest).write(to: staging.appendingPathComponent("data.json"))
        try Task.checkCancellation()
        try await InstanceBackup.zip(["-q", "-r", partial.path, "data.json", "world"], in: staging)
        _ = try InstanceWorlds.worldFolder(world: world, in: instanceRoot)
        try manager.moveItem(at: partial, to: target)
        return target
    }
}

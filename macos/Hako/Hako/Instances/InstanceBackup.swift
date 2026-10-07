import Foundation
import SwiftData

/// `data.json` в корне `.hakobackup`: сведения для окна восстановления и профиль, без которого сборку не запустить.
nonisolated struct InstanceBackupManifest: Codable, Sendable {
    static let fileName = "data.json"
    static let fileExtension = "hakobackup"
    static let currentFormat = 1

    struct Mod: Codable, Sendable {
        let file: String
        let enabled: Bool
        let source: ModSource
        let projectID: String?
        let versionID: String?
    }

    struct Profile: Codable, Sendable {
        let metadataURL: String
        let metadataSHA1: String
        let argumentSource: LaunchArgumentSource
        let offlineMode: Bool
        let offlineUsername: String
        let parameters: InstanceParameters
        let javaExecutable: String
        let legacyTexturepacks: Bool
        let fabricConfiguration: FabricConfiguration?
        let fabricProfileSHA1: String?
    }

    var formatVersion = currentFormat
    let backupCreatedAt: Date
    let name: String
    let folderName: String
    let instanceCreatedAt: Date
    let minecraftVersion: String
    let modLoader: String
    let loaderVersion: String?
    let javaMajorVersion: Int
    let iconSymbol: String
    /// PNG своей картинки сборки; в JSON — base64. Отсутствует, если выбрана иконка-символ.
    let iconPNG: Data?
    let modCount: Int
    let mods: [Mod]
    let profile: Profile

    static func encoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        return encoder
    }

    static func decoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }
}

extension InstanceStore {
    /// Упаковывает всю папку сборки в `~/.hako/backups/{папка}_{дата}.hakobackup` с `data.json` в корне архива.
    func backup(_ instance: GameInstance, content: InstanceContent) async throws -> URL {
        if let reason = managementBlockedReason(instance) { throw InstanceFileError.message(reason) }
        guard instance.state == .ready else { throw InstanceFileError.message(String(appLocalized: "Дождитесь завершения установки сборки.")) }
        guard try !context.fetch(FetchDescriptor<GameInstance>()).contains(where: { $0.folderName.lowercased() == InstanceStorage.backupsFolder }) else {
            throw InstanceFileError.message(String(appLocalized: "Папку backups занимает сборка. Переименуйте её, чтобы сохранять резервные копии."))
        }
        let root = try storage.directory(instance.folderName)
        let backups = try storage.directory(InstanceStorage.backupsFolder)
        let icon = try InstanceStorage.containedURL("icon.png", in: root)
        let iconPNG = instance.iconSymbol.isEmpty && FileManager.default.fileExists(atPath: icon.path) ? try Data(contentsOf: icon) : nil
        let fabric = try instance.fabricConfiguration()
        let profile = InstanceBackupManifest.Profile(
            metadataURL: instance.metadataURL, metadataSHA1: instance.metadataSHA1, argumentSource: instance.argumentSource,
            offlineMode: instance.offlineMode, offlineUsername: instance.offlineUsername, parameters: instance.parameters,
            javaExecutable: instance.javaExecutable, legacyTexturepacks: instance.legacyTexturepacks,
            fabricConfiguration: fabric, fabricProfileSHA1: instance.fabricProfileSHA1)
        let name = instance.name, folderName = instance.folderName, createdAt = instance.createdAt, versionID = instance.versionID
        let modLoader = instance.modLoaderRaw, javaMajorVersion = instance.javaMajorVersion, iconSymbol = instance.iconSymbol
        contentBusy.insert(instance.id)
        defer { contentBusy.remove(instance.id) }
        let modsFolder = try InstanceStorage.containedURL("minecraft/mods", in: root)
        let items: [InstanceContentItem]
        do { items = try await content.list(at: modsFolder, mods: true) }
        catch { items = try await content.list(at: modsFolder, mods: true, readOrigins: false) }
        let mods = items.map { InstanceBackupManifest.Mod(file: $0.logicalName, enabled: $0.enabled, source: $0.source, projectID: $0.origin?.projectID, versionID: $0.origin?.versionID) }
        let manifest = InstanceBackupManifest(
            backupCreatedAt: Date(), name: name, folderName: folderName, instanceCreatedAt: createdAt,
            minecraftVersion: versionID, modLoader: modLoader, loaderVersion: fabric?.loaderVersion,
            javaMajorVersion: javaMajorVersion, iconSymbol: iconSymbol, iconPNG: iconPNG,
            modCount: mods.count, mods: mods, profile: profile)
        return try await InstanceBackup.archive(root, manifest: manifest, into: backups)
    }
}

nonisolated enum InstanceBackup {
    /// Архив собирается во временный файл рядом с итоговым и появляется под своим именем только целиком.
    @concurrent static func archive(_ source: URL, manifest: InstanceBackupManifest, into backups: URL) async throws -> URL {
        let manager = FileManager.default
        try manager.createDirectory(at: backups, withIntermediateDirectories: true)
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd_HH-mm-ss"
        let target = backups.appendingPathComponent("\(manifest.folderName)_\(formatter.string(from: manifest.backupCreatedAt)).\(InstanceBackupManifest.fileExtension)")
        guard !manager.fileExists(atPath: target.path) else { throw InstanceFileError.message(String(appLocalized: "Резервная копия с таким именем уже существует. Повторите через секунду.")) }
        let partial = backups.appendingPathComponent(".\(UUID().uuidString).partial")
        let staging = manager.temporaryDirectory.appendingPathComponent("Hako-Backup-\(UUID().uuidString)")
        defer { try? manager.removeItem(at: partial); try? manager.removeItem(at: staging) }
        try manager.createDirectory(at: staging, withIntermediateDirectories: false)
        let data = staging.appendingPathComponent(InstanceBackupManifest.fileName)
        try InstanceBackupManifest.encoder().encode(manifest).write(to: data)
        try await zip(["-q", "-j", partial.path, data.path], in: staging)
        // -y сохраняет ссылки внутри jre.bundle ссылками, а не копиями их целей.
        try await zip(["-q", "-r", "-y", partial.path, ".", "-x", "minecraft/.hako-running.json", ".hako-game/*"], in: source)
        try manager.moveItem(at: partial, to: target)
        return target
    }

    /// Ожидание через terminationHandler: waitUntilExit на потоках Swift Concurrency может не вернуться.
    private static func zip(_ arguments: [String], in directory: URL) async throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/zip")
        process.arguments = arguments
        process.currentDirectoryURL = directory
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        let status: Int32 = try await withCheckedThrowingContinuation { continuation in
            process.terminationHandler = { continuation.resume(returning: $0.terminationStatus) }
            do { try process.run() } catch { continuation.resume(throwing: error) }
        }
        guard status == 0 else { throw InstanceFileError.message(String(appLocalized: "Не удалось создать архив резервной копии (zip, код \(status)).")) }
    }
}

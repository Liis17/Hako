import Foundation
import SwiftData
import Darwin

/// Все игровые файлы принадлежат одной сборке; корень можно заменить в тестах.
nonisolated struct InstanceStorage: Sendable {
    let root: URL

    init(root: URL = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".hako", isDirectory: true)) {
        self.root = root
    }

    func directory(_ folder: String) throws -> URL {
        guard !folder.contains("/") else { throw InstanceFileError.message("Недопустимая папка сборки.") }
        let target = try Self.containedURL(folder, in: root)
        do {
            if try root.appendingPathComponent(folder).resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink == true {
                throw InstanceFileError.message("Папка сборки не может быть символической ссылкой.")
            }
        } catch let error as CocoaError where error.code == .fileReadNoSuchFile || error.code == .fileNoSuchFile { }
        return target
    }

    static func containedURL(_ path: String, in root: URL) throws -> URL {
        let parts = path.split(separator: "/", omittingEmptySubsequences: false)
        guard !path.isEmpty, !path.contains("\0"), !path.contains("\\"),
              parts.allSatisfy({ !$0.isEmpty && $0 != "." && $0 != ".." }) else {
            throw InstanceFileError.message("Недопустимый путь к файлу сборки.")
        }
        let resolvedRoot = root.resolvingSymlinksInPath().standardizedFileURL.path + "/"
        var target = root.resolvingSymlinksInPath()
        for part in parts {
            target = target.appendingPathComponent(String(part)).resolvingSymlinksInPath().standardizedFileURL
            guard target.path.hasPrefix(resolvedRoot) else {
                throw InstanceFileError.message("Файл выходит за пределы папки сборки.")
            }
        }
        return target
    }

    func ensureAvailable(_ folder: String, excluding: String? = nil) throws {
        let manager = FileManager.default
        guard manager.fileExists(atPath: root.path) else { return }
        let entries = try manager.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)
        if entries.contains(where: { $0.lastPathComponent.lowercased() == folder.lowercased() && $0.lastPathComponent != excluding }) {
            throw InstanceFileError.message("Сборка или папка с таким именем уже существует.")
        }
    }

    func allocatedSize() throws -> Int64 {
        guard FileManager.default.fileExists(atPath: root.path) else { return 0 }
        var failure: Error?
        guard let files = FileManager.default.enumerator(at: root, includingPropertiesForKeys: [.isRegularFileKey, .isSymbolicLinkKey, .totalFileAllocatedSizeKey], errorHandler: { _, error in failure = error; return false }) else { return 0 }
        var result: Int64 = 0
        for case let file as URL in files {
            let values = try file.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .totalFileAllocatedSizeKey])
            if values.isRegularFile == true && values.isSymbolicLink != true { result += Int64(values.totalFileAllocatedSize ?? 0) }
        }
        if let failure { throw failure }
        return result
    }
}

@MainActor final class InstanceStore {
    let context: ModelContext
    let storage: InstanceStorage
    private let persist: () throws -> Void

    init(context: ModelContext, storage: InstanceStorage = .init(), persist: (() throws -> Void)? = nil) {
        self.context = context
        self.storage = storage
        self.persist = persist ?? { try context.save() }
    }

    func validateName(_ name: String, excluding instance: GameInstance? = nil) throws -> String {
        let folder = try InstanceName.folder(for: name)
        let instances = try context.fetch(FetchDescriptor<GameInstance>())
        guard !instances.contains(where: { $0.id != instance?.id && $0.folderName.lowercased() == folder.lowercased() }) else {
            throw InstanceFileError.message("Сборка с таким именем уже существует.")
        }
        try storage.ensureAvailable(folder, excluding: instance?.folderName)
        return folder
    }

    func create(_ draft: InstanceDraft, versionID: String, metadataURL: String, metadataSHA1: String, javaMajorVersion: Int = 0, legacyTexturepacks: Bool = false) throws -> GameInstance {
        try validateOffline(draft)
        let name = try InstanceName.validated(draft.name)
        let folder = try validateName(name)
        let url = try storage.directory(folder)
        try FileManager.default.createDirectory(at: storage.root, withIntermediateDirectories: true)
        guard mkdir(url.path, 0o755) == 0 else { throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
        let instance = GameInstance(name: name, folderName: folder, versionID: versionID, metadataURL: metadataURL, metadataSHA1: metadataSHA1)
        instance.javaMajorVersion = javaMajorVersion
        instance.legacyTexturepacks = legacyTexturepacks
        do {
            try FileManager.default.createDirectory(at: url.appendingPathComponent("java"), withIntermediateDirectories: false)
            try FileManager.default.createDirectory(at: url.appendingPathComponent("minecraft"), withIntermediateDirectories: false)
            if let data = draft.iconData { try data.write(to: url.appendingPathComponent("icon.png"), options: .atomic) }
            apply(draft, to: instance)
            context.insert(instance)
            try persist()
            return instance
        } catch {
            context.rollback()
            try? FileManager.default.removeItem(at: url)
            throw error
        }
    }

    func update(_ instance: GameInstance, with draft: InstanceDraft) throws {
        try validateOffline(draft)
        let name = try InstanceName.validated(draft.name)
        let folder = try validateName(name, excluding: instance)
        let oldFolder = instance.folderName
        guard folder == oldFolder || (instance.state != .installing && instance.state != .queued) else {
            throw InstanceFileError.message("Остановите загрузку перед переименованием сборки.")
        }
        let original = try storage.directory(oldFolder)
        let destination = try storage.directory(folder)
        _ = try InstanceStorage.containedURL("icon.png", in: original)
        let icon = original.appendingPathComponent("icon.png")
        let oldIcon = FileManager.default.fileExists(atPath: icon.path) ? try Data(contentsOf: icon) : nil
        var moved = false
        do {
            if folder != oldFolder {
                try moveDirectory(original, to: destination)
                moved = true
            }
            let destinationIcon = destination.appendingPathComponent("icon.png")
            if let data = draft.iconData { try data.write(to: destinationIcon, options: .atomic) }
            if !draft.iconSymbol.isEmpty && FileManager.default.fileExists(atPath: destinationIcon.path) {
                try FileManager.default.removeItem(at: destinationIcon)
            }
            apply(draft, to: instance)
            instance.name = name
            instance.folderName = folder
            instance.iconRevision = UUID()
            try persist()
        } catch {
            context.rollback()
            if moved { try? moveDirectory(destination, to: original) }
            if let oldIcon { try? oldIcon.write(to: icon, options: .atomic) }
            else { try? FileManager.default.removeItem(at: icon) }
            throw error
        }
    }

    private func apply(_ draft: InstanceDraft, to instance: GameInstance) {
        instance.iconSymbol = draft.iconSymbol
        instance.argumentSource = draft.argumentSource
        instance.offlineMode = draft.offlineMode
        instance.offlineUsername = draft.offlineUsername
        instance.parameters = draft.parameters
    }

    private func validateOffline(_ draft: InstanceDraft) throws {
        guard !draft.offlineMode || OfflineUsername.isValid(draft.offlineUsername) else { throw InstanceFileError.message("Ник: от 3 до 16 латинских букв, цифр или _.") }
    }

    private func moveDirectory(_ from: URL, to: URL) throws {
        if from.lastPathComponent.lowercased() == to.lastPathComponent.lowercased() {
            let temporary = storage.root.appendingPathComponent(".rename-\(UUID().uuidString)")
            try FileManager.default.moveItem(at: from, to: temporary)
            do { try FileManager.default.moveItem(at: temporary, to: to) }
            catch { try? FileManager.default.moveItem(at: temporary, to: from); throw error }
        } else {
            try FileManager.default.moveItem(at: from, to: to)
        }
    }
}

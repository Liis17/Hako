import Foundation
import SwiftData
import Darwin
import Observation

/// Все игровые файлы принадлежат одной сборке; корень можно заменить в тестах.
nonisolated struct InstanceStorage: Sendable {
    static let backupsFolder = "backups"
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

    /// Копия получает собственные файлы; на APFS `copyItem` клонирует их без лишнего места.
    @concurrent static func copyInstance(_ source: URL, to destination: URL) async throws {
        try FileManager.default.copyItem(at: source, to: destination)
        let record = try GameProcessRecord.url(in: destination)
        if FileManager.default.fileExists(atPath: record.path) { try FileManager.default.removeItem(at: record) }
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

@MainActor @Observable final class InstanceStore {
    let context: ModelContext
    let storage: InstanceStorage
    private let persist: () throws -> Void
    var launchBusy: Set<UUID> = []
    var contentBusy: Set<UUID> = []

    init(context: ModelContext, storage: InstanceStorage = .init(), persist: (() throws -> Void)? = nil) {
        self.context = context
        self.storage = storage
        self.persist = persist ?? { try context.save() }
    }

    func validateName(_ name: String, excluding instance: GameInstance? = nil) throws -> String {
        let folder = try InstanceName.folder(for: name)
        guard folder.lowercased() != InstanceStorage.backupsFolder else { throw InstanceFileError.message("Имя «backups» занято папкой резервных копий.") }
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
            if draft.modLoader == .fabric {
                guard let configuration = draft.fabricConfiguration, !configuration.loaderVersion.isEmpty else { throw InstanceFileError.message("Выберите версию Fabric Loader.") }
                instance.fabricConfigurationData = try JSONEncoder().encode(configuration)
            }
            instance.modLoaderRaw = draft.modLoader.rawValue
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
        guard folder == oldFolder || (instance.state != .installing && instance.state != .queued && !launchBusy.contains(instance.id) && !contentBusy.contains(instance.id)) else {
            throw InstanceFileError.message("Остановите загрузку и закройте игру перед переименованием сборки.")
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

    func updateSettings(_ instance: GameInstance, with draft: InstanceDraft) throws {
        try validateOffline(draft)
        let directory = try storage.directory(instance.folderName)
        _ = try InstanceStorage.containedURL("icon.png", in: directory)
        let icon = directory.appendingPathComponent("icon.png")
        let oldIcon = FileManager.default.fileExists(atPath: icon.path) ? try Data(contentsOf: icon) : nil
        let iconChanged = draft.iconData != nil || draft.iconSymbol != instance.iconSymbol
        do {
            if let data = draft.iconData { try data.write(to: icon, options: .atomic) }
            if !draft.iconSymbol.isEmpty && FileManager.default.fileExists(atPath: icon.path) {
                try FileManager.default.removeItem(at: icon)
            }
            apply(draft, to: instance)
            if iconChanged { instance.iconRevision = UUID() }
            try persist()
        } catch {
            context.rollback()
            if let oldIcon { try? oldIcon.write(to: icon, options: .atomic) }
            else { try? FileManager.default.removeItem(at: icon) }
            throw error
        }
    }

    func rename(_ instance: GameInstance, to input: String) throws {
        let name = try InstanceName.validated(input)
        let folder = try validateName(name, excluding: instance)
        let oldFolder = instance.folderName
        guard folder == oldFolder || (instance.state != .installing && instance.state != .queued && !launchBusy.contains(instance.id) && !contentBusy.contains(instance.id)) else {
            throw InstanceFileError.message("Остановите загрузку и закройте игру перед переименованием сборки.")
        }

        let original = try storage.directory(oldFolder)
        let destination = try storage.directory(folder)
        var moved = false
        do {
            if folder != oldFolder {
                try moveDirectory(original, to: destination)
                moved = true
            }
            instance.name = name
            instance.folderName = folder
            instance.iconRevision = UUID()
            try persist()
        } catch {
            context.rollback()
            if moved { try? moveDirectory(destination, to: original) }
            throw error
        }
    }

    /// Причина, по которой сборку сейчас нельзя удалить, дублировать или сохранить в резервную копию.
    func managementBlockedReason(_ instance: GameInstance) -> String? {
        if instance.state == .installing || instance.state == .queued { return "Остановите загрузку сборки." }
        if launchBusy.contains(instance.id) { return "Закройте Minecraft." }
        if contentBusy.contains(instance.id) { return "Дождитесь завершения операций с файлами сборки." }
        return nil
    }

    /// Первое свободное имя вида «Имя 2», «Имя 3»…, укороченное до 60 символов.
    func duplicateName(for instance: GameInstance) throws -> String {
        for number in 2...999 {
            let suffix = " \(number)"
            let name = String(instance.name.prefix(60 - suffix.count)).trimmingCharacters(in: .whitespaces) + suffix
            if (try? validateName(name)) != nil { return name }
        }
        throw InstanceFileError.message("Не удалось подобрать имя для копии сборки.")
    }

    /// Копирует папку сборки вместе с Java и создаёт профиль с теми же параметрами и новым UUID.
    func duplicate(_ instance: GameInstance) async throws -> GameInstance {
        if let reason = managementBlockedReason(instance) { throw InstanceFileError.message(reason) }
        guard instance.state == .ready else { throw InstanceFileError.message("Дождитесь завершения установки сборки.") }
        let source = try storage.directory(instance.folderName)
        let staged = storage.root.appendingPathComponent(".duplicate-\(UUID().uuidString)")
        contentBusy.insert(instance.id)
        defer { contentBusy.remove(instance.id) }
        var destination: URL?
        do {
            try await InstanceStorage.copyInstance(source, to: staged)
            // Имя выбирается после копирования: за это время могла появиться сборка с тем же именем.
            let name = try duplicateName(for: instance)
            let folder = try validateName(name)
            let target = try storage.directory(folder)
            try FileManager.default.moveItem(at: staged, to: target)
            destination = target
            let copy = GameInstance(name: name, folderName: folder, versionID: instance.versionID, metadataURL: instance.metadataURL, metadataSHA1: instance.metadataSHA1)
            apply(InstanceDraft(instance: instance), to: copy)
            copy.state = .ready
            copy.javaMajorVersion = instance.javaMajorVersion
            copy.javaExecutable = instance.javaExecutable
            copy.legacyTexturepacks = instance.legacyTexturepacks
            copy.modLoaderRaw = instance.modLoaderRaw
            copy.fabricConfigurationData = instance.fabricConfigurationData
            copy.fabricProfileSHA1 = instance.fabricProfileSHA1
            context.insert(copy)
            do { try persist() } catch { context.rollback(); throw error }
            return copy
        } catch {
            try? FileManager.default.removeItem(at: destination ?? staged)
            throw error
        }
    }

    /// Стирает папку сборки без корзины; время игры хранится отдельно и остаётся.
    func delete(_ instance: GameInstance) throws {
        if let reason = managementBlockedReason(instance) { throw InstanceFileError.message(reason) }
        let directory = try storage.directory(instance.folderName)
        let removed = storage.root.appendingPathComponent(".delete-\(UUID().uuidString)")
        let exists = FileManager.default.fileExists(atPath: directory.path)
        if exists { try FileManager.default.moveItem(at: directory, to: removed) }
        do {
            context.delete(instance)
            try persist()
        } catch {
            context.rollback()
            if exists { try? FileManager.default.moveItem(at: removed, to: directory) }
            throw error
        }
        if exists { Task.detached(priority: .utility) { try? FileManager.default.removeItem(at: removed) } }
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

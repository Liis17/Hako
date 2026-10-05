import Foundation

nonisolated struct InstanceContentItem: Identifiable, Sendable {
    let url: URL
    let isDirectory: Bool
    var id: URL { url }
    var name: String { url.lastPathComponent }
}

nonisolated enum PackImportError: LocalizedError {
    case exists(String)
    var errorDescription: String? {
        switch self { case .exists(let name): "Текстурпак \(name) уже существует." }
    }
}

actor InstanceContent {
    func list(at folder: URL, mods: Bool) throws -> [InstanceContentItem] {
        guard FileManager.default.fileExists(atPath: folder.path) else { return [] }
        return try FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey], options: .skipsHiddenFiles).compactMap { url in
            let values = try url.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
            guard values.isSymbolicLink != true else { return nil }
            let directory = values.isDirectory == true
            guard mods ? (!directory && url.pathExtension.lowercased() == "jar") : (directory || url.pathExtension.lowercased() == "zip") else { return nil }
            return InstanceContentItem(url: url, isDirectory: directory)
        }.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    /// Импорт всегда копирует содержимое, поэтому удаление оригинала не меняет сборку.
    func importPack(from source: URL, into folder: URL, replace: Bool = false) throws {
        let manager = FileManager.default
        let scoped = source.startAccessingSecurityScopedResource()
        defer { if scoped { source.stopAccessingSecurityScopedResource() } }
        let values = try source.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
        guard values.isSymbolicLink != true, values.isDirectory == true || source.pathExtension.lowercased() == "zip" else {
            throw InstanceFileError.message("Выберите ZIP-файл или папку текстурпака.")
        }
        if values.isDirectory == true {
            let sourcePath = source.resolvingSymlinksInPath().standardizedFileURL.path + "/"
            guard !(folder.resolvingSymlinksInPath().standardizedFileURL.path + "/").hasPrefix(sourcePath) else {
                throw InstanceFileError.message("Нельзя импортировать папку в неё саму. Выберите отдельную папку текстурпака.")
            }
            var failure: Error?
            let entries = manager.enumerator(at: source, includingPropertiesForKeys: [.isSymbolicLinkKey], errorHandler: { _, error in failure = error; return false })
            while let item = entries?.nextObject() as? URL {
                if try item.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink == true { throw InstanceFileError.message("Папка текстурпака содержит ссылки. Импортируйте папку с независимыми файлами.") }
            }
            if let failure { throw failure }
        }
        try manager.createDirectory(at: folder, withIntermediateDirectories: true)
        let existing = try manager.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil).first { $0.lastPathComponent.lowercased() == source.lastPathComponent.lowercased() }
        if existing != nil && !replace { throw PackImportError.exists(source.lastPathComponent) }
        let destination = try InstanceStorage.containedURL(source.lastPathComponent, in: folder)
        let staged = folder.appendingPathComponent(".import-\(UUID().uuidString)")
        let backup = folder.appendingPathComponent(".replace-\(UUID().uuidString)")
        defer { try? manager.removeItem(at: staged) }
        try manager.copyItem(at: source, to: staged)
        if let existing { try manager.moveItem(at: existing, to: backup) }
        do { try manager.moveItem(at: staged, to: destination) }
        catch { if let existing { try? manager.moveItem(at: backup, to: existing) }; throw error }
        if existing != nil { try manager.removeItem(at: backup) }
    }

    func trash(_ item: InstanceContentItem, in folder: URL) throws {
        let target = try InstanceStorage.containedURL(item.name, in: folder)
        try FileManager.default.trashItem(at: target, resultingItemURL: nil)
    }
}

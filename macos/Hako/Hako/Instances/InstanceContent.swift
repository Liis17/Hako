import Foundation

nonisolated struct ModOrigin: Codable, Sendable {
    let source: ModSource
    let projectID: String
    let versionID: String
    let pageURL: URL
    let sha512: String
    let api: FabricAPIDescriptor?

    init(source: ModSource, projectID: String, versionID: String, pageURL: URL, sha512: String, api: FabricAPIDescriptor? = nil) {
        self.source = source; self.projectID = projectID; self.versionID = versionID
        self.pageURL = pageURL; self.sha512 = sha512; self.api = api
    }

    init(api: FabricAPIDescriptor) {
        source = .modrinth; projectID = api.projectID; versionID = api.versionID
        pageURL = api.pageURL; sha512 = api.sha512; self.api = api
    }
}

nonisolated struct ModRegistry: Codable, Sendable {
    var apiProvisioned = false
    var files: [String: ModOrigin] = [:]
}

nonisolated struct InstanceContentItem: Identifiable, Sendable {
    let url: URL
    let isDirectory: Bool
    var origin: ModOrigin? = nil
    var modificationDate: Date? = nil
    var disabledPack = false
    var id: URL { url }
    var name: String { url.lastPathComponent }
    var enabled: Bool { !disabledPack && !name.lowercased().hasSuffix(".jar.disabled") }
    var logicalName: String { Self.logicalName(name) }
    var source: ModSource { origin?.source ?? .local }
    static func logicalName(_ name: String) -> String { name.lowercased().hasSuffix(".jar.disabled") ? String(name.dropLast(9)) : name }
}

nonisolated enum PackImportError: LocalizedError {
    case exists(String)
    var errorDescription: String? {
        switch self { case .exists(let name): String(appLocalized: "Файл \(name) уже существует.") }
    }
}

actor InstanceContent {
    private func registry(at folder: URL) throws -> ModRegistry {
        let raw = folder.deletingLastPathComponent().appendingPathComponent(".hako-mods.json")
        if (try? raw.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true { throw InstanceFileError.message(String(appLocalized: "Реестр модов не может быть ссылкой.")) }
        let file = try InstanceStorage.containedURL(".hako-mods.json", in: folder.deletingLastPathComponent())
        guard FileManager.default.fileExists(atPath: file.path) else { return .init() }
        return try JSONDecoder().decode(ModRegistry.self, from: Data(contentsOf: file))
    }

    private func saveRegistry(_ registry: ModRegistry, at folder: URL) throws {
        let raw = folder.deletingLastPathComponent().appendingPathComponent(".hako-mods.json")
        if (try? raw.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true { throw InstanceFileError.message(String(appLocalized: "Реестр модов не может быть ссылкой.")) }
        let file = try InstanceStorage.containedURL(".hako-mods.json", in: folder.deletingLastPathComponent())
        try JSONEncoder().encode(registry).write(to: file, options: .atomic)
    }

    func provisionAPI(_ api: FabricAPIDescriptor, from cached: URL, in folder: URL) throws {
        var registry = try registry(at: folder)
        guard !registry.apiProvisioned else { return }
        guard try FabricClient.validAPI(cached, api: api) else { throw InstanceFileError.message(String(appLocalized: "Файл Fabric API повреждён.")) }
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let target = try InstanceStorage.containedURL(api.filename, in: folder)
        if let existing = try existing(api.filename, in: folder, mods: true) {
            guard try FabricClient.validAPI(existing, api: api), (try existing.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) != true else { throw InstanceFileError.message(String(appLocalized: "Файл Fabric API уже существует. Уберите конфликтующий файл и повторите установку.")) }
            registry.files[api.filename.lowercased()] = ModOrigin(api: api); registry.apiProvisioned = true
            try saveRegistry(registry, at: folder)
            return
        }
        let staged = folder.appendingPathComponent(".api-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: staged) }
        try FileManager.default.copyItem(at: cached, to: staged)
        try FileManager.default.moveItem(at: staged, to: target)
        registry.files[api.filename.lowercased()] = ModOrigin(api: api); registry.apiProvisioned = true
        do { try saveRegistry(registry, at: folder) }
        catch { try? FileManager.default.removeItem(at: target); throw error }
    }

    func apiWasProvisioned(in folder: URL) throws -> Bool { try registry(at: folder).apiProvisioned }
    func list(at folder: URL, mods: Bool, readOrigins: Bool = true, includeDisabledPacks: Bool = true) throws -> [InstanceContentItem] {
        let origins = mods && readOrigins ? try registry(at: folder).files : [:]
        let disabledFolder = mods || !includeDisabledPacks ? nil : try disabledPacksFolder(folder)
        let folders = [folder] + (disabledFolder.map { [$0] } ?? [])
        let urls = try folders.flatMap { directory in
            FileManager.default.fileExists(atPath: directory.path) ? try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey, .isRegularFileKey, .contentModificationDateKey], options: .skipsHiddenFiles) : []
        }
        return try urls.compactMap { url in
            let values = try url.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey, .isRegularFileKey, .contentModificationDateKey])
            guard values.isSymbolicLink != true else { return nil }
            let directory = values.isDirectory == true
            let name = InstanceContentItem.logicalName(url.lastPathComponent)
            guard mods ? (values.isRegularFile == true && name.lowercased().hasSuffix(".jar")) : (directory || (values.isRegularFile == true && url.pathExtension.lowercased() == "zip")) else { return nil }
            var origin = origins[name.lowercased()]
            if let known = origin, try FabricClient.hashFile(url) != known.sha512.lowercased() { origin = nil }
            return InstanceContentItem(url: url, isDirectory: directory, origin: origin, modificationDate: values.contentModificationDate, disabledPack: !mods && url.deletingLastPathComponent().standardizedFileURL.path == disabledFolder?.standardizedFileURL.path)
        }.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    private func disabledPacksFolder(_ folder: URL) throws -> URL {
        let name = ".hako-disabled-\(folder.lastPathComponent)"
        let raw = folder.deletingLastPathComponent().appendingPathComponent(name)
        guard (try? raw.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) != true else { throw InstanceFileError.message(String(appLocalized: "Папка отключённых паков не может быть ссылкой.")) }
        return try InstanceStorage.containedURL(name, in: folder.deletingLastPathComponent())
    }

    func packIconData(_ item: InstanceContentItem) async throws -> Data? {
        let values = try item.url.resourceValues(forKeys: [.isSymbolicLinkKey])
        guard values.isSymbolicLink != true else { return nil }
        if !item.isDirectory { return try await FabricClient.archiveEntry("pack.png", in: item.url, limit: 4_194_304) }
        let raw = item.url.appendingPathComponent("pack.png")
        guard FileManager.default.fileExists(atPath: raw.path) else { return nil }
        let imageValues = try raw.resourceValues(forKeys: [.isSymbolicLinkKey, .isRegularFileKey, .fileSizeKey])
        guard imageValues.isSymbolicLink != true, imageValues.isRegularFile == true, (imageValues.fileSize ?? 0) <= 4_194_304 else { return nil }
        return try Data(contentsOf: InstanceStorage.containedURL("pack.png", in: item.url))
    }

    func modIconData(_ item: InstanceContentItem) async throws -> Data? {
        let values = try item.url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
        guard values.isRegularFile == true, values.isSymbolicLink != true else { return nil }
        let metadata = try await FabricClient.archiveEntry("fabric.mod.json", in: item.url, limit: 1_048_576)
        let json = try JSONSerialization.jsonObject(with: metadata) as? [String: Any]
        let path: String?
        if let single = json?["icon"] as? String { path = single }
        else if let sizes = json?["icon"] as? [String: String] {
            let candidates = sizes.compactMap { key, value in Int(key).map { (size: $0, path: value) } }.filter { $0.size > 0 }.sorted { $0.size < $1.size }
            path = (candidates.first { $0.size >= 80 } ?? candidates.last)?.path
        } else { path = nil }
        guard let path else { return nil }
        return try await FabricClient.archiveEntry(path, in: item.url, limit: 4_194_304)
    }

    /// Импорт всегда копирует содержимое, поэтому удаление оригинала не меняет сборку.
    func importPack(from source: URL, into folder: URL, replace: Bool = false) throws {
        try importItem(from: source, into: folder, mods: false, replace: replace)
    }

    private func existing(_ name: String, in folder: URL, mods: Bool, includeDisabledPacks: Bool = true) throws -> URL? {
        let folders = mods || !includeDisabledPacks ? [folder] : [folder, try disabledPacksFolder(folder)]
        let files = try folders.flatMap { directory in
            FileManager.default.fileExists(atPath: directory.path) ? try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil) : []
        }
        let matches = files.filter {
            (mods ? InstanceContentItem.logicalName($0.lastPathComponent) : $0.lastPathComponent).lowercased() == name.lowercased()
        }
        guard matches.count <= 1 else { throw InstanceFileError.message(String(appLocalized: "Найдено несколько файлов \(name). Уберите дубликаты в папке сборки.")) }
        if let file = matches.first, try file.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink == true { throw InstanceFileError.message(String(appLocalized: "Нельзя заменять файл-ссылку в сборке.")) }
        return matches.first
    }

    /// Проверка мира и публикация идут на одном акторе; исчезнувшие родительские папки не создаются заново.
    func importDatapack(from source: URL, world: String, in instanceRoot: URL, replace: Bool = false) throws {
        let folder = try InstanceWorlds.datapacksFolder(world: world, in: instanceRoot)
        try importItem(from: source, into: folder, mods: false, replace: replace, createIntermediateDirectories: false, includeDisabledPacks: false)
    }

    func importItem(from source: URL, into folder: URL, mods: Bool, replace: Bool = false, origin: ModOrigin? = nil, createIntermediateDirectories: Bool = true, includeDisabledPacks: Bool = true) throws {
        let manager = FileManager.default
        let scoped = source.startAccessingSecurityScopedResource()
        defer { if scoped { source.stopAccessingSecurityScopedResource() } }
        let values = try source.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey, .isRegularFileKey])
        let accepted = mods ? values.isRegularFile == true && source.pathExtension.lowercased() == "jar" : values.isDirectory == true || values.isRegularFile == true && source.pathExtension.lowercased() == "zip"
        guard values.isSymbolicLink != true, accepted else {
            throw InstanceFileError.message(mods ? String(appLocalized: "Выберите JAR-файл мода.") : String(appLocalized: "Выберите ZIP-файл или папку ресурспака."))
        }
        if values.isDirectory == true {
            let sourcePath = source.resolvingSymlinksInPath().standardizedFileURL.path + "/"
            guard !(folder.resolvingSymlinksInPath().standardizedFileURL.path + "/").hasPrefix(sourcePath) else {
                throw InstanceFileError.message(String(appLocalized: "Нельзя импортировать папку в неё саму. Выберите отдельную папку текстурпака."))
            }
            var failure: Error?
            let entries = manager.enumerator(at: source, includingPropertiesForKeys: [.isSymbolicLinkKey], errorHandler: { _, error in failure = error; return false })
            while let item = entries?.nextObject() as? URL {
                if try item.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink == true { throw InstanceFileError.message(String(appLocalized: "Папка текстурпака содержит ссылки. Импортируйте папку с независимыми файлами.")) }
            }
            if let failure { throw failure }
        }
        var registry = mods ? try registry(at: folder) : nil
        if createIntermediateDirectories || !manager.fileExists(atPath: folder.path) {
            try manager.createDirectory(at: folder, withIntermediateDirectories: createIntermediateDirectories)
        }
        let existing = try existing(source.lastPathComponent, in: folder, mods: mods, includeDisabledPacks: includeDisabledPacks)
        if existing != nil && !replace { throw PackImportError.exists(source.lastPathComponent) }
        let disabled = mods && existing?.lastPathComponent.lowercased().hasSuffix(".jar.disabled") == true
        let destinationFolder = !mods && existing != nil ? existing!.deletingLastPathComponent() : folder
        let destination = try InstanceStorage.containedURL(source.lastPathComponent + (disabled ? ".disabled" : ""), in: destinationFolder)
        let staged = folder.appendingPathComponent(".import-\(UUID().uuidString)")
        let backup = folder.appendingPathComponent(".replace-\(UUID().uuidString)")
        defer { try? manager.removeItem(at: staged) }
        try manager.copyItem(at: source, to: staged)
        if let existing { try manager.moveItem(at: existing, to: backup) }
        do {
            try manager.moveItem(at: staged, to: destination)
            if registry != nil {
                registry?.files[source.lastPathComponent.lowercased()] = origin
                try saveRegistry(registry!, at: folder)
            }
        } catch {
            if manager.fileExists(atPath: destination.path) { try? manager.removeItem(at: destination) }
            if let existing { try? manager.moveItem(at: backup, to: existing) }
            throw error
        }
        if existing != nil { try manager.removeItem(at: backup) }
    }

    func setEnabled(_ item: InstanceContentItem, in folder: URL, enabled: Bool, mods: Bool = true) throws {
        let source = try checkedFile(item, in: folder)
        guard enabled != item.enabled else { return }
        let name = mods ? item.logicalName + (enabled ? "" : ".disabled") : item.name
        let targetFolder = mods || enabled ? folder : try disabledPacksFolder(folder)
        try FileManager.default.createDirectory(at: targetFolder, withIntermediateDirectories: true)
        let target = try InstanceStorage.containedURL(name, in: targetFolder)
        let collision = try FileManager.default.contentsOfDirectory(at: targetFolder, includingPropertiesForKeys: nil).contains { $0.lastPathComponent.lowercased() == name.lowercased() }
        guard !collision else { throw PackImportError.exists(name) }
        try FileManager.default.moveItem(at: source, to: target)
    }

    private func checkedFile(_ item: InstanceContentItem, in folder: URL) throws -> URL {
        let parent = item.disabledPack ? try disabledPacksFolder(folder) : folder
        guard item.url.deletingLastPathComponent().standardizedFileURL.path == parent.standardizedFileURL.path else { throw InstanceFileError.message(String(appLocalized: "Файл не принадлежит папке сборки.")) }
        let target = try InstanceStorage.containedURL(item.name, in: parent)
        let raw = parent.appendingPathComponent(item.name)
        guard (try raw.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) != true else { throw InstanceFileError.message(String(appLocalized: "Файл содержимого не может быть ссылкой.")) }
        return target
    }

    func trash(_ item: InstanceContentItem, in folder: URL, mods: Bool = false) throws {
        let target = try checkedFile(item, in: folder)
        var registry = mods ? try registry(at: folder) : nil
        var trashed: NSURL?
        try FileManager.default.trashItem(at: target, resultingItemURL: &trashed)
        if registry != nil {
            registry?.files.removeValue(forKey: item.logicalName.lowercased())
            do { try saveRegistry(registry!, at: folder) }
            catch { if let trashed { try? FileManager.default.moveItem(at: trashed as URL, to: target) }; throw error }
        }
    }

    func updateAPI(_ item: InstanceContentItem, to api: FabricAPIDescriptor, from cached: URL, in folder: URL) throws {
        let old = try checkedFile(item, in: folder)
        guard let origin = item.origin, origin.source == .modrinth, origin.projectID == FabricAPIDescriptor.project,
              try FabricClient.hashFile(old) == origin.sha512, try FabricClient.validAPI(cached, api: api) else { throw InstanceFileError.message(String(appLocalized: "Файл мода изменился. Обновите список и повторите действие.")) }
        try replaceItem(item, from: cached, filename: api.filename, in: folder, mods: true, origin: ModOrigin(api: api))
    }

    /// Заменяет мод или ресурспак новым файлом, сохраняя отключение; реестр модов получает новое происхождение.
    func replaceItem(_ item: InstanceContentItem, from source: URL, filename: String, in folder: URL, mods: Bool, expectedSHA512: String? = nil, origin: ModOrigin?) throws {
        let old = try checkedFile(item, in: folder)
        if let expectedSHA512, try FabricClient.hashFile(old) != expectedSHA512.lowercased() { throw InstanceFileError.message(String(appLocalized: "Файл изменился. Обновите список и повторите действие.")) }
        var registry = mods ? try registry(at: folder) : nil
        let parent = old.deletingLastPathComponent()
        let newName = filename + (mods && !item.enabled ? ".disabled" : "")
        if let other = try existing(filename, in: folder, mods: mods), other.standardizedFileURL.path != old.standardizedFileURL.path { throw PackImportError.exists(filename) }
        let target = try InstanceStorage.containedURL(newName, in: parent)
        let staged = parent.appendingPathComponent(".update-\(UUID().uuidString)")
        let backup = parent.appendingPathComponent(".backup-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: staged) }
        try FileManager.default.copyItem(at: source, to: staged)
        try Task.checkCancellation()
        try FileManager.default.moveItem(at: old, to: backup)
        do {
            try FileManager.default.moveItem(at: staged, to: target)
            if registry != nil {
                registry?.files.removeValue(forKey: item.logicalName.lowercased())
                registry?.files[filename.lowercased()] = origin
                try saveRegistry(registry!, at: folder)
            }
        } catch {
            try? FileManager.default.removeItem(at: target); try? FileManager.default.moveItem(at: backup, to: old)
            throw error
        }
        try FileManager.default.removeItem(at: backup)
    }
}

import Foundation

nonisolated struct InstanceWorld: Identifiable, Sendable {
    let id: String
    let name: String
    let version: String?
    let gameType: Int?
    let lastPlayed: Date?
    let size: Int64?
    let iconData: Data?
    let metadataError: String?
}

/// Миры всегда принадлежат saves одной сборки. Все обходы и чтение выполняются вне MainActor.
actor InstanceWorlds {
    func list(in instanceRoot: URL) throws -> [InstanceWorld] {
        let saves = try Self.checkedURL("minecraft/saves", in: instanceRoot)
        let manager = FileManager.default
        guard manager.fileExists(atPath: saves.path) else { return [] }
        let folders = try manager.contentsOfDirectory(at: saves, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey], options: .skipsHiddenFiles)
        var worlds: [InstanceWorld] = []
        for folder in folders {
            try Task.checkCancellation()
            let values = try folder.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
            guard values.isDirectory == true, values.isSymbolicLink != true,
                  manager.fileExists(atPath: folder.appendingPathComponent("level.dat").path) || manager.fileExists(atPath: folder.appendingPathComponent("level.dat_old").path) else { continue }
            var metadata: WorldMetadata?, failure: Error?
            for file in ["level.dat", "level.dat_old"] {
                do {
                    let url = try Self.checkedURL(file, in: folder)
                    guard let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize, size <= 16_777_216 else { throw CocoaError(.fileReadCorruptFile) }
                    metadata = try WorldMetadata.read(Data(contentsOf: url))
                    break
                } catch is CancellationError { throw CancellationError() }
                catch { if failure == nil { failure = error } }
            }
            let icon: Data?
            if let url = try? Self.checkedURL("icon.png", in: folder), let size = try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize, size <= 4_194_304 {
                icon = try? Data(contentsOf: url)
            } else { icon = nil }
            let size: Int64?
            do { size = try Self.size(of: folder) }
            catch is CancellationError { throw CancellationError() }
            catch { size = nil }
            worlds.append(.init(id: folder.lastPathComponent, name: metadata?.name ?? folder.lastPathComponent, version: metadata?.version, gameType: metadata?.gameType, lastPlayed: metadata?.lastPlayed, size: size, iconData: icon, metadataError: metadata == nil ? String(appLocalized: "Не удалось прочитать сведения о мире: \(failure?.localizedDescription ?? "")") : nil))
        }
        return worlds.sorted {
            let lhs = $0.lastPlayed ?? .distantPast, rhs = $1.lastPlayed ?? .distantPast
            return lhs == rhs ? $0.name.localizedStandardCompare($1.name) == .orderedAscending : lhs > rhs
        }
    }

    nonisolated static func datapacksFolder(world: String, in instanceRoot: URL) throws -> URL {
        guard !world.contains("/") else { throw InstanceFileError.message(String(appLocalized: "Недопустимая папка мира.")) }
        let folder = try checkedURL("minecraft/saves/\(world)", in: instanceRoot)
        guard (try? folder.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true,
              ["level.dat", "level.dat_old"].contains(where: { file in
                  guard let url = try? checkedURL(file, in: folder) else { return false }
                  return (try? url.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true
              }) else { throw InstanceFileError.message(String(appLocalized: "Мир больше не существует. Обновите список миров.")) }
        return try checkedURL("datapacks", in: folder)
    }

    nonisolated private static func checkedURL(_ path: String, in root: URL) throws -> URL {
        let target = try InstanceStorage.containedURL(path, in: root)
        var raw = root
        for component in path.split(separator: "/") {
            raw.appendPathComponent(String(component))
            if (try? raw.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true {
                throw InstanceFileError.message(String(appLocalized: "Файлы мира не могут быть символическими ссылками."))
            }
        }
        return target
    }

    nonisolated private static func size(of folder: URL) throws -> Int64 {
        var failure: Error?
        guard let files = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey], errorHandler: { _, error in failure = error; return false }) else { throw CocoaError(.fileReadUnknown) }
        var size: Int64 = 0
        for case let file as URL in files {
            try Task.checkCancellation()
            let values = try file.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey])
            if values.isSymbolicLink == true { files.skipDescendants(); continue }
            if values.isRegularFile == true {
                guard let bytes = values.fileSize else { throw CocoaError(.fileReadUnknown) }
                size += Int64(bytes)
            }
        }
        if let failure { throw failure }
        return size
    }
}

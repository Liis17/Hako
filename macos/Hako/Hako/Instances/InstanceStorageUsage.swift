import Foundation

/// Занятое сборками место по категориям; датапаки мира не входят в размер мира.
nonisolated struct InstanceStorageUsage: Sendable, Equatable {
    enum Category: CaseIterable, Sendable {
        case worlds, minecraft, screenshots, java, mods, datapacks, resourcepacks, backups
    }

    private var bytes: [Category: Int64] = [:]

    var total: Int64 { bytes.values.reduce(0, +) }

    subscript(category: Category) -> Int64 {
        get { bytes[category] ?? 0 }
        set { bytes[category] = newValue }
    }

    /// Категория файла по пути относительно корня `~/.hako`: `[сборка, minecraft, saves, мир, datapacks, …]`.
    static func category(of components: [String]) -> Category {
        guard let first = components.first else { return .minecraft }
        if first == InstanceStorage.backupsFolder || first == InstanceStorage.worldsFolder { return .backups }
        guard components.count > 2 else { return .minecraft }
        if components[1] == "java" { return .java }
        guard components[1] == "minecraft" else { return .minecraft }
        switch components[2] {
        case "saves":
            guard components.count > 5 else { return .worlds }
            return ["datapacks", ".hako-disabled-datapacks"].contains(components[4]) ? .datapacks : .worlds
        case "mods": return .mods
        case "resourcepacks", "texturepacks", ".hako-disabled-resourcepacks", ".hako-disabled-texturepacks": return .resourcepacks
        case "screenshots": return .screenshots
        default: return .minecraft
        }
    }
}

nonisolated extension InstanceStorage {
    /// Один проход по `~/.hako` с теми же правилами подсчёта, что и в `allocatedSize()`.
    func usage() throws -> InstanceStorageUsage {
        var result = InstanceStorageUsage()
        guard FileManager.default.fileExists(atPath: root.path) else { return result }
        var failure: Error?
        let keys: [URLResourceKey] = [.isRegularFileKey, .isSymbolicLinkKey, .totalFileAllocatedSizeKey]
        guard let files = FileManager.default.enumerator(at: root, includingPropertiesForKeys: keys, errorHandler: { _, error in failure = error; return false }) else { return result }
        for case let file as URL in files {
            let values = try file.resourceValues(forKeys: Set(keys))
            guard values.isRegularFile == true, values.isSymbolicLink != true else { continue }
            let category = InstanceStorageUsage.category(of: Array(file.pathComponents.suffix(files.level)))
            result[category] += Int64(values.totalFileAllocatedSize ?? 0)
        }
        if let failure { throw failure }
        return result
    }
}

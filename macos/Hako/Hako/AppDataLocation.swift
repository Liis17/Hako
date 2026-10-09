import Foundation

enum AppDataLocation {
    static func storeURL(home: URL = FileManager.default.homeDirectoryForCurrentUser, defaults: UserDefaults = .standard) throws -> URL {
        let manager = FileManager.default
        let container = home.appendingPathComponent("Library/Containers/com.Launcher.Hako/Data/Library")
        let sandboxStore = container.appendingPathComponent("Application Support/default.store")
        let existingStore = home.appendingPathComponent("Library/Application Support/default.store")
        let newStore = home.appendingPathComponent("Library/Application Support/Hako/default.store")
        let pinned = defaults.string(forKey: "hako.modelStorePath").map { URL(fileURLWithPath: $0) }
        if !(try filePresent(newStore)) {
            for legacy in [pinned, sandboxStore, existingStore].compactMap({ $0 }) where legacy != newStore {
                guard try filePresent(legacy) else { continue }
                try manager.createDirectory(at: newStore.deletingLastPathComponent(), withIntermediateDirectories: true)
                for suffix in ["", "-wal", "-shm"] {
                    let source = URL(fileURLWithPath: legacy.path + suffix)
                    if manager.fileExists(atPath: source.path) { try manager.copyItem(at: source, to: URL(fileURLWithPath: newStore.path + suffix)) }
                }
                break
            }
        }
        defaults.removeObject(forKey: "hako.modelStorePath")

        if !defaults.bool(forKey: "hako.didMigrateGameDefaults") {
            let preferences = container.appendingPathComponent("Preferences/com.Launcher.Hako.plist")
            if try filePresent(preferences) {
                let data = try Data(contentsOf: preferences)
                let old = try PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any] ?? [:]
                for key in [GameLaunchDefaults.Key.javaPath, GameLaunchDefaults.Key.javaArguments, GameLaunchDefaults.Key.minecraftArguments, GameLaunchDefaults.Key.fullscreen, GameLaunchDefaults.Key.windowWidth, GameLaunchDefaults.Key.windowHeight] {
                    if let value = old[key] { defaults.set(value, forKey: key) }
                }
            }
            defaults.set(true, forKey: "hako.didMigrateGameDefaults")
        }
        try manager.createDirectory(at: newStore.deletingLastPathComponent(), withIntermediateDirectories: true)
        return newStore
    }

    private static func filePresent(_ url: URL) throws -> Bool {
        do { return try url.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile == true }
        catch let error as CocoaError where error.code == .fileReadNoSuchFile || error.code == .fileNoSuchFile { return false }
    }
}

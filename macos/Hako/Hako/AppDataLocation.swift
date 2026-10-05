import Foundation

enum AppDataLocation {
    static func storeURL(home: URL = FileManager.default.homeDirectoryForCurrentUser, defaults: UserDefaults = .standard) throws -> URL {
        let manager = FileManager.default
        let container = home.appendingPathComponent("Library/Containers/com.Launcher.Hako/Data/Library")
        let sandboxStore = container.appendingPathComponent("Application Support/default.store")
        let existingStore = home.appendingPathComponent("Library/Application Support/default.store")
        let newStore = home.appendingPathComponent("Library/Application Support/Hako/default.store")
        let selected: URL
        if let pinned = defaults.string(forKey: "hako.modelStorePath") {
            selected = URL(fileURLWithPath: pinned)
        } else if try filePresent(sandboxStore) { selected = sandboxStore }
        else if try filePresent(existingStore) { selected = existingStore }
        else { selected = newStore }

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
        try manager.createDirectory(at: selected.deletingLastPathComponent(), withIntermediateDirectories: true)
        defaults.set(selected.path, forKey: "hako.modelStorePath")
        return selected
    }

    private static func filePresent(_ url: URL) throws -> Bool {
        do { return try url.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile == true }
        catch let error as CocoaError where error.code == .fileReadNoSuchFile || error.code == .fileNoSuchFile { return false }
    }
}

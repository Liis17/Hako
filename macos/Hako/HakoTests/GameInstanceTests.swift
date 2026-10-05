import Foundation
import SwiftData
import Testing

@MainActor struct GameInstanceTests {
    private func container() throws -> ModelContainer {
        try ModelContainer(for: Account.self, GameInstance.self, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
    }

    private func temporary() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    private func draft(_ name: String) -> InstanceDraft {
        var draft = InstanceDraft()
        draft.name = name
        return draft
    }

    @Test func namesRespectDirectoryContract() throws {
        #expect(try InstanceName.folder(for: "  My Pack 2  ") == "My_Pack_2")
        #expect(try InstanceName.validated(String(repeating: "A", count: 60)).count == 60)
        for name in ["", "   ", "Сборка", "Pack_1", "../Pack", "Pack.1", String(repeating: "A", count: 61)] {
            #expect(throws: InstanceFileError.self) { try InstanceName.validated(name) }
        }
    }

    @Test func profilesHaveIndependentFilesAndRenameWithoutRedownloading() throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let store = InstanceStore(context: container.mainContext, storage: .init(root: root))
        let first = try store.create(draft("First Pack"), versionID: "1.19", metadataURL: "https://example.test/version", metadataSHA1: "hash")
        _ = try store.create(draft("Second Pack"), versionID: "1.19", metadataURL: "https://example.test/version", metadataSHA1: "hash")
        let file = root.appendingPathComponent("First_Pack/minecraft/client.jar")
        try Data("first".utf8).write(to: file)
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("Second_Pack/minecraft/client.jar").path))
        #expect(throws: InstanceFileError.self) { try store.validateName("first pack") }
        try FileManager.default.createDirectory(at: root.appendingPathComponent("External"), withIntermediateDirectories: false)
        #expect(throws: InstanceFileError.self) { try store.validateName("external") }
        first.state = .ready
        try container.mainContext.save()
        let id = first.id
        var edit = InstanceDraft(instance: first)
        edit.name = "Renamed Pack"
        try store.update(first, with: edit)
        #expect(first.id == id)
        #expect(try Data(contentsOf: root.appendingPathComponent("Renamed_Pack/minecraft/client.jar")) == Data("first".utf8))
        edit.name = "renamed pack"
        try store.update(first, with: edit)
        #expect(FileManager.default.fileExists(atPath: root.appendingPathComponent("renamed_pack/minecraft/client.jar").path))
    }

    @Test func failedSaveRollsBackNameFolderAndIcon() throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        var fail = false
        let store = InstanceStore(context: container.mainContext, storage: .init(root: root), persist: {
            if fail { throw InstanceFileError.message("Test save failure") }
            try container.mainContext.save()
        })
        var original = draft("Original")
        original.iconSymbol = ""
        original.iconData = Data("original icon".utf8)
        let instance = try store.create(original, versionID: "1.19", metadataURL: "url", metadataSHA1: "hash")
        instance.state = .ready
        try container.mainContext.save()
        fail = true
        var changed = InstanceDraft(instance: instance)
        changed.name = "Changed"
        changed.iconData = Data("changed icon".utf8)
        #expect(throws: InstanceFileError.self) { try store.update(instance, with: changed) }
        #expect(instance.name == "Original")
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("Changed").path))
        #expect(try Data(contentsOf: root.appendingPathComponent("Original/icon.png")) == original.iconData)
        #expect(throws: InstanceFileError.self) { try store.create(draft("Failed"), versionID: "v", metadataURL: "url", metadataSHA1: "hash") }
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("Failed").path))
    }

    @Test func linkedParametersFollowCurrentDefaultsAndOverridesStayIndependent() throws {
        let suite = "hako.tests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let instance = GameInstance(name: "Pack", folderName: "Pack", versionID: "v", metadataURL: "url", metadataSHA1: "hash")
        instance.argumentSource = .global
        defaults.set("-Xmx2G", forKey: GameLaunchDefaults.Key.javaArguments)
        #expect(instance.effectiveParameters(from: defaults).javaArguments == "-Xmx2G")
        defaults.set("-Xmx4G", forKey: GameLaunchDefaults.Key.javaArguments)
        #expect(instance.effectiveParameters(from: defaults).javaArguments == "-Xmx4G")
        instance.parameters = instance.effectiveParameters(from: defaults)
        instance.argumentSource = .custom
        defaults.set("-Xmx8G", forKey: GameLaunchDefaults.Key.javaArguments)
        #expect(instance.effectiveParameters(from: defaults).javaArguments == "-Xmx4G")
    }

    @Test func addingInstanceSchemaPreservesAccount() throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let url = root.appendingPathComponent("default.store")
        do {
            let old = try ModelContainer(for: Account.self, configurations: ModelConfiguration(url: url))
            old.mainContext.insert(Account(xbox: XboxProfile(xuid: "preserved", gamertag: "Player", avatarURL: nil), email: nil))
            try old.mainContext.save()
        }
        let updated = try ModelContainer(for: Account.self, GameInstance.self, configurations: ModelConfiguration(url: url))
        #expect(try updated.mainContext.fetch(FetchDescriptor<Account>()).first?.xuid == "preserved")
    }

    @Test func sandboxLocationAndPreferencesArePreservedOnce() throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let suite = "hako.tests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let library = root.appendingPathComponent("Library/Containers/com.Launcher.Hako/Data/Library")
        let oldStore = library.appendingPathComponent("Application Support/default.store")
        let preferences = library.appendingPathComponent("Preferences/com.Launcher.Hako.plist")
        try FileManager.default.createDirectory(at: oldStore.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: preferences.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data().write(to: oldStore)
        let plist = try PropertyListSerialization.data(fromPropertyList: [GameLaunchDefaults.Key.javaArguments: "-Xmx3G"], format: .binary, options: 0)
        try plist.write(to: preferences)
        #expect(try AppDataLocation.storeURL(home: root, defaults: defaults) == oldStore)
        #expect(defaults.string(forKey: GameLaunchDefaults.Key.javaArguments) == "-Xmx3G")
        defaults.set("-Xmx5G", forKey: GameLaunchDefaults.Key.javaArguments)
        _ = try AppDataLocation.storeURL(home: root, defaults: defaults)
        #expect(defaults.string(forKey: GameLaunchDefaults.Key.javaArguments) == "-Xmx5G")
    }

    @Test func pathsCannotEscapeThroughTraversalOrSymlinks() throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        for path in ["../escape", "/absolute", "a/../b", "a//b"] {
            #expect(throws: InstanceFileError.self) { try InstanceStorage.containedURL(path, in: root) }
        }
        try FileManager.default.createSymbolicLink(at: root.appendingPathComponent("escape"), withDestinationURL: root.deletingLastPathComponent())
        #expect(throws: InstanceFileError.self) { try InstanceStorage.containedURL("escape/file", in: root) }
    }

    @Test func instanceDirectoryCannotAliasAnotherInstance() throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let store = InstanceStore(context: container.mainContext, storage: .init(root: root))
        let first = try store.create(draft("First"), versionID: "v", metadataURL: "url", metadataSHA1: "hash")
        _ = try store.create(draft("Second"), versionID: "v", metadataURL: "url", metadataSHA1: "hash")
        let second = root.appendingPathComponent("Second")
        try FileManager.default.removeItem(at: root.appendingPathComponent("First"))
        try FileManager.default.createSymbolicLink(at: root.appendingPathComponent("First"), withDestinationURL: second)
        #expect(throws: InstanceFileError.self) { try store.storage.directory("First") }
        first.state = .paused
        var edit = InstanceDraft(instance: first); edit.name = "Renamed"
        #expect(throws: InstanceFileError.self) { try store.update(first, with: edit) }
        #expect(FileManager.default.fileExists(atPath: second.path))
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("Renamed").path))
    }
}

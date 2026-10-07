import Foundation
import SwiftData
import Testing

@MainActor struct InstanceManagementTests {
    private func container() throws -> ModelContainer {
        try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
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

    @Test func deleteErasesFolderAndRecord() throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let store = InstanceStore(context: container.mainContext, storage: .init(root: root))
        let instance = try store.create(draft("Doomed Pack"), versionID: "1.19", metadataURL: "url", metadataSHA1: "hash")
        instance.state = .ready
        try container.mainContext.save()
        try Data("world".utf8).write(to: root.appendingPathComponent("Doomed_Pack/minecraft/level.dat"))
        try store.delete(instance)
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("Doomed_Pack").path))
        #expect(try container.mainContext.fetch(FetchDescriptor<GameInstance>()).isEmpty)
    }

    @Test func deleteRestoresFolderWhenSaveFails() throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        var fail = false
        let store = InstanceStore(context: container.mainContext, storage: .init(root: root), persist: {
            if fail { throw InstanceFileError.message("Test save failure") }
            try container.mainContext.save()
        })
        let instance = try store.create(draft("Kept"), versionID: "1.19", metadataURL: "url", metadataSHA1: "hash")
        instance.state = .failed
        try container.mainContext.save()
        fail = true
        #expect(throws: InstanceFileError.self) { try store.delete(instance) }
        #expect(FileManager.default.fileExists(atPath: root.appendingPathComponent("Kept/minecraft").path))
        #expect(try container.mainContext.fetch(FetchDescriptor<GameInstance>()).count == 1)
    }

    @Test func busyInstancesCannotBeDeleted() throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let store = InstanceStore(context: container.mainContext, storage: .init(root: root))
        let instance = try store.create(draft("Running"), versionID: "1.19", metadataURL: "url", metadataSHA1: "hash")
        instance.state = .ready
        store.launchBusy.insert(instance.id)
        #expect(throws: InstanceFileError.self) { try store.delete(instance) }
        store.launchBusy.removeAll()
        instance.state = .installing
        #expect(throws: InstanceFileError.self) { try store.delete(instance) }
        #expect(FileManager.default.fileExists(atPath: root.appendingPathComponent("Running").path))
    }

    @Test func backupsFolderNameIsReserved() throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let store = InstanceStore(context: container.mainContext, storage: .init(root: root))
        #expect(throws: InstanceFileError.self) { try store.validateName("Backups") }
    }
}

import Foundation
import SwiftData
import Testing

@MainActor struct ContentControllerTests {
    @Test func replacementWaitsWithLockAndContinuesAfterViewIndependentConfirmation() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let installations = InstallationCoordinator(context: container.mainContext, storage: .init(root: root))
        var draft = InstanceDraft(); draft.name = "Content"
        let instance = try installations.store.create(draft, versionID: "test", metadataURL: "https://fixtures.test/version", metadataSHA1: "sha")
        instance.state = .ready; instance.modLoaderRaw = "fabric"
        let controller = InstanceContentController(installations: installations)
        let source = root.appendingPathComponent("mod.jar"); try Data("old".utf8).write(to: source)
        let folder = try controller.folder(instance, mods: true)
        try await installations.content.importItem(from: source, into: folder, mods: true)
        try Data("new".utf8).write(to: source)
        controller.importFiles([source], into: instance, mods: true)
        for _ in 0..<100 where controller.confirmation == nil { try await Task.sleep(for: .milliseconds(10)) }
        #expect(controller.confirmation != nil)
        #expect(installations.store.contentBusy.contains(instance.id))
        #expect(throws: InstanceFileError.self) { try installations.store.rename(instance, to: "Moved") }
        #expect(throws: InstanceFileError.self) { try installations.enqueue(instance) }
        let confirmationID = try #require(controller.confirmation).id
        controller.resolveConfirmation(UUID(), accepted: true)
        #expect(controller.confirmation?.id == confirmationID)
        controller.resolveConfirmation(confirmationID, accepted: true)
        for _ in 0..<100 where installations.store.contentBusy.contains(instance.id) { try await Task.sleep(for: .milliseconds(10)) }
        #expect(!installations.store.contentBusy.contains(instance.id))
        #expect(controller.mods[instance.id]?.count == 1)
        #expect(try Data(contentsOf: folder.appendingPathComponent("mod.jar")) == Data("new".utf8))
        installations.store.launchBusy.insert(instance.id)
        #expect(controller.disabledReason(instance, mods: true) != nil)
    }
}

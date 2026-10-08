import AppKit
import SwiftData
import Testing

@Suite(.serialized) @MainActor struct DockLaunchMenuTests {
    @Test func menuTracksReadyInstancesRenameAndDeletion() throws {
        let fixture = try DockMenuFixture()
        defer { fixture.cleanup() }
        let beta = try fixture.instance("Beta"), alpha = try fixture.instance("Alpha")
        _ = try fixture.instance("Queued", state: .queued)
        let menu = fixture.menu.makeMenu()
        #expect(menu.items.map(\.title) == ["Запустить сборку", "Alpha", "Beta"])
        #expect(!menu.autoenablesItems && !menu.items[0].isEnabled)
        #expect(menu.items[1].image != nil && menu.items[1].representedObject as? UUID == alpha.id)
        try fixture.store.rename(alpha, to: "Gamma")
        #expect(fixture.menu.makeMenu().items.map(\.title) == ["Запустить сборку", "Beta", "Gamma"])
        try fixture.store.delete(alpha)
        try fixture.store.delete(beta)
        let empty = fixture.menu.makeMenu()
        #expect(empty.items.map(\.title) == ["Запустить сборку", "Нет установленных сборок"])
        #expect(empty.items.allSatisfy { !$0.isEnabled })
    }

    @Test func busyInstancesAreDisabledAndOnlineGuestCanRequestConnection() throws {
        let fixture = try DockMenuFixture()
        defer { fixture.cleanup() }
        let instance = try fixture.instance("Online")
        #expect(fixture.menu.makeMenu().items[1].isEnabled)
        fixture.store.contentBusy.insert(instance.id)
        #expect(!fixture.menu.makeMenu().items[1].isEnabled)
        fixture.store.contentBusy.remove(instance.id)
        fixture.store.launchBusy.insert(instance.id)
        #expect(!fixture.menu.makeMenu().items[1].isEnabled)
    }

    @Test func nativeMenuActionRequestsTheSameUuidAndDisablesPendingLaunch() async throws {
        let fixture = try DockMenuFixture()
        defer { fixture.cleanup() }
        let instance = try fixture.instance("Dock Pack")
        let item = fixture.menu.makeMenu().items[1]
        let action = try #require(item.action)
        #expect(NSApplication.shared.sendAction(action, to: item.target, from: item))
        for _ in 0..<100 {
            if fixture.quick.presentation != nil { break }
            try await Task.sleep(for: .milliseconds(20))
        }
        #expect(fixture.quick.presentation?.instanceID == instance.id)
        #expect(!fixture.menu.makeMenu().items[1].isEnabled)
        fixture.quick.cancelPresentations()
        for _ in 0..<100 {
            if fixture.quick.preparingIDs.isEmpty { break }
            try await Task.sleep(for: .milliseconds(20))
        }
        #expect(fixture.quick.preparingIDs.isEmpty && fixture.quick.error == nil)
    }
}

@MainActor private final class DockMenuFixture {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    let container: ModelContainer
    let store: InstanceStore
    let quick: QuickLaunchCoordinator
    var menu: DockLaunchMenu { .init(store: store, quickLaunch: quick) }

    init() throws {
        container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        store = InstanceStore(context: container.mainContext, storage: .init(root: root))
        let sessions = MinecraftSessionCoordinator(context: container.mainContext)
        let playtime = try PlaytimeCoordinator(context: container.mainContext, journalDirectory: root.appendingPathComponent("sessions"))
        quick = QuickLaunchCoordinator(store: store, games: .init(store: store, sessions: sessions, playtime: playtime))
    }

    func instance(_ name: String, state: InstallationState = .ready) throws -> GameInstance {
        var draft = InstanceDraft(); draft.name = name
        let instance = try store.create(draft, versionID: "1.21", metadataURL: "url", metadataSHA1: "sha")
        instance.state = state
        try container.mainContext.save()
        return instance
    }

    func cleanup() { try? FileManager.default.removeItem(at: root) }
}

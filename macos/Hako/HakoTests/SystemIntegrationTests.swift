import AppIntents
import CoreSpotlight
import Darwin
import SwiftData
import Testing

@Suite(.serialized) @MainActor struct SystemIntegrationTests {
    private func eventually(_ condition: () -> Bool) async throws {
        for _ in 0..<100 { if condition() { return }; try await Task.sleep(for: .milliseconds(20)) }
        #expect(condition())
    }

    @Test func queriesResolveUuidAfterRenameAndSuggestOnlyReadyInstances() async throws {
        let fixture = try SystemIntegrationFixture()
        defer { fixture.cleanup() }
        let ready = try fixture.instance("Vanilla Pack"), queued = try fixture.instance("Fabric Pack", state: .queued)
        let query = GameInstanceQuery(catalog: fixture.catalog, indexer: fixture.indexer)
        #expect(try await query.suggestedEntities().map(\.id) == [ready.id])
        #expect(try await query.entities(matching: "  VANILLA ").map(\.id) == [ready.id])
        #expect(try await query.entities(matching: "missing").isEmpty)
        let originalEntities = try fixture.catalog.entities(for: [ready.id])
        let original = try #require(originalEntities.first)
        try fixture.store.rename(ready, to: "Renamed Pack")
        let renamedEntities = try await query.entities(for: [original.id])
        let renamed = try #require(renamedEntities.first)
        #expect(renamed.id == original.id && renamed.name == "Renamed Pack")
        #expect(renamed.attributeSet.title == "Renamed Pack")
        #expect(renamed.attributeSet.keywords?.contains("Minecraft") == true)
        queued.state = .ready
        try fixture.container.mainContext.save()
        #expect(try await query.allEntities().count == 2)
        try fixture.store.delete(ready)
        #expect(try await query.entities(for: [original.id]).isEmpty)
        #expect(try await query.entities(matching: "Renamed").isEmpty)
    }

    @Test func indexUpdatesReadyRenameIconAndDeletionWithoutRewritingUnchangedEntries() async throws {
        let fixture = try SystemIntegrationFixture()
        defer { fixture.cleanup() }
        let instance = try fixture.instance("Indexed Pack"), queued = try fixture.instance("Queued Pack", state: .queued)
        try await fixture.indexer.synchronize()
        #expect(fixture.recorder.resets == 1 && fixture.recorder.entries.count == 1 && fixture.recorder.entries[instance.id] != nil)
        let batches = fixture.recorder.batches.count
        try await fixture.indexer.synchronize()
        #expect(fixture.recorder.batches.count == batches && fixture.recorder.shortcutUpdates == 1)
        let id = instance.id
        try fixture.store.rename(instance, to: "New Name")
        try await fixture.indexer.synchronize()
        #expect(fixture.recorder.entries[id]?.name == "New Name" && fixture.recorder.entries.count == 1)
        let revision = instance.iconRevision
        instance.iconSymbol = "leaf.fill"; instance.iconRevision = UUID()
        try fixture.container.mainContext.save()
        try await fixture.indexer.synchronize()
        #expect(fixture.recorder.entries[id]?.iconRevision != revision)
        queued.state = .ready
        try fixture.container.mainContext.save()
        try await fixture.indexer.synchronize()
        #expect(fixture.recorder.entries.count == 2)
        try fixture.store.delete(instance)
        try await fixture.indexer.synchronize()
        #expect(fixture.recorder.entries[id] == nil && fixture.recorder.entries[queued.id] != nil)
        #expect(fixture.recorder.resets == 1)
    }

    @Test func modelContextSavesAutomaticallyRefreshTheIndex() async throws {
        let fixture = try SystemIntegrationFixture()
        defer { fixture.cleanup() }
        fixture.indexer.start()
        try await eventually { fixture.recorder.resets == 1 }
        let instance = try fixture.instance("Installing Pack", state: .queued)
        #expect(fixture.recorder.entries.isEmpty)
        instance.state = .ready
        try fixture.container.mainContext.save()
        try await eventually { fixture.recorder.entries[instance.id] != nil }
        try fixture.store.rename(instance, to: "Finished Pack")
        try await eventually { fixture.recorder.entries[instance.id]?.name == "Finished Pack" }
        let id = instance.id
        try fixture.store.delete(instance)
        try await eventually { fixture.recorder.entries[id] == nil }
    }

    @Test func queuedIndexWritesCannotResurrectAnInstanceDeletedDuringAnUpsert() async throws {
        let fixture = try SystemIntegrationFixture()
        defer { fixture.cleanup() }
        let instance = try fixture.instance("Old Pack"), id = instance.id
        try await fixture.indexer.synchronize()
        try fixture.store.rename(instance, to: "New Pack")
        fixture.recorder.holdNextIndex = true
        let first = Task { try await fixture.indexer.synchronize() }
        try await eventually { fixture.recorder.gate != nil }
        try fixture.store.delete(instance)
        let second = Task { try await fixture.indexer.synchronize() }
        fixture.recorder.gate?.resume(); fixture.recorder.gate = nil
        try await first.value
        try await second.value
        #expect(fixture.recorder.entries[id] == nil && fixture.recorder.maxActive == 1)
    }

    @Test func systemReindexCallbacksReplaceLostAndStaleContent() async throws {
        let fixture = try SystemIntegrationFixture()
        defer { fixture.cleanup() }
        let first = try fixture.instance("First Pack"), second = try fixture.instance("Second Pack")
        try await fixture.indexer.synchronize()
        fixture.recorder.entries.removeAll()
        let query = GameInstanceQuery(catalog: fixture.catalog, indexer: fixture.indexer)
        try await query.reindexEntities(for: [first.id], indexDescription: CSSearchableIndexDescription())
        #expect(fixture.recorder.entries[first.id] != nil && fixture.recorder.entries[second.id] == nil)
        try await query.reindexAllEntities(indexDescription: CSSearchableIndexDescription())
        #expect(fixture.recorder.entries.count == 2 && fixture.recorder.resets == 2)
        let id = first.id
        try fixture.store.delete(first)
        try await query.reindexEntities(for: [id], indexDescription: CSSearchableIndexDescription())
        #expect(fixture.recorder.entries[id] == nil)
    }

    @Test func failedIndexingStillUpdatesShortcutsAndAllowsOrdinaryGameLaunch() async throws {
        let fixture = try SystemIntegrationFixture()
        defer { fixture.cleanup() }
        let instance = try fixture.instance("Offline Pack")
        fixture.recorder.failIndex = true
        await #expect(throws: InstanceFileError.self) { try await fixture.indexer.synchronize() }
        #expect(fixture.recorder.shortcutUpdates == 1)
        try await fixture.games.launchAndWait(instance, account: nil)
        #expect(fixture.games.states[instance.id] == .running)
        fixture.recorder.failIndex = false
        try await fixture.indexer.synchronize()
        #expect(fixture.recorder.entries[instance.id] != nil)
    }

    @Test func aFailedRebuildRetriesEvenWhenModelsHaveNotChanged() async throws {
        let fixture = try SystemIntegrationFixture()
        defer { fixture.cleanup() }
        let instance = try fixture.instance("Retry Pack")
        try await fixture.indexer.synchronize()
        fixture.recorder.failIndex = true
        await #expect(throws: InstanceFileError.self) { try await fixture.indexer.synchronize(rebuild: true) }
        #expect(fixture.recorder.entries.isEmpty)
        fixture.recorder.failIndex = false
        try await fixture.indexer.synchronize()
        #expect(fixture.recorder.entries[instance.id] != nil)
        fixture.recorder.failIndex = true
        fixture.recorder.entries.removeAll()
        await #expect(throws: InstanceFileError.self) { try await fixture.indexer.reindex(identifiers: [instance.id]) }
        fixture.recorder.failIndex = false
        try await fixture.indexer.synchronize()
        #expect(fixture.recorder.entries[instance.id] != nil)
    }

    @Test func intentUsesUuidAndWaitsForNavigationBeforeLaunchingTheGame() async throws {
        let fixture = try SystemIntegrationFixture()
        defer { fixture.cleanup() }
        let instance = try fixture.instance("Siri Pack")
        let entities = try fixture.catalog.entities(for: [instance.id])
        let entity = try #require(entities.first)
        let intent = LaunchGameInstanceIntent(target: entity, launcher: fixture.quick)
        let task = Task { try await intent.perform() }
        try await eventually { fixture.quick.presentation != nil }
        #expect(fixture.quick.presentation?.instanceID == entity.id && fixture.games.states[entity.id] == nil)
        try fixture.store.rename(instance, to: "Renamed Siri Pack")
        fixture.quick.completePresentation(try #require(fixture.quick.presentation).id)
        _ = try await task.value
        #expect(fixture.games.states[entity.id] == .running)
        let directory = try fixture.store.storage.directory(instance.folderName)
        let beforeRecord = try GameProcessRecord.load(in: directory)
        let before = try #require(beforeRecord)
        fixture.quick.registerWindowPresenter { [weak quick = fixture.quick] in
            if let request = quick?.presentation { quick?.completePresentation(request.id) }
        }
        _ = try await intent.perform()
        let afterRecord = try GameProcessRecord.load(in: directory)
        let after = try #require(afterRecord)
        #expect(after.pid == before.pid && after.sessionID == before.sessionID)
        #expect(LaunchGameInstanceIntent.allowedExecutionTargets == .main)
    }
}

@MainActor private final class SpotlightRecorder {
    var entries: [UUID: GameInstanceEntity] = [:]
    var batches: [[GameInstanceEntity]] = []
    var resets = 0, shortcutUpdates = 0, active = 0, maxActive = 0
    var failIndex = false, holdNextIndex = false
    var gate: CheckedContinuation<Void, Never>?

    var dependencies: InstanceSpotlightIndexer.Dependencies {
        .init(reset: {
            self.resets += 1; self.entries.removeAll()
        }, index: { entities in
            self.active += 1; self.maxActive = max(self.maxActive, self.active)
            defer { self.active -= 1 }
            if self.holdNextIndex {
                self.holdNextIndex = false
                await withCheckedContinuation { self.gate = $0 }
            }
            if self.failIndex { throw InstanceFileError.message("Индекс недоступен") }
            self.batches.append(entities)
            for entity in entities { self.entries[entity.id] = entity }
        }, delete: { ids in
            for id in ids { self.entries[id] = nil }
        }, updateShortcuts: { self.shortcutUpdates += 1 })
    }
}

@MainActor private final class SystemIntegrationFixture {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    let container: ModelContainer
    let store: InstanceStore
    let catalog: InstanceEntityCatalog
    let recorder = SpotlightRecorder()
    let indexer: InstanceSpotlightIndexer
    let games: GameLaunchCoordinator
    let quick: QuickLaunchCoordinator
    let playtime: PlaytimeCoordinator

    init() throws {
        container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        store = InstanceStore(context: container.mainContext, storage: .init(root: root))
        catalog = InstanceEntityCatalog(store: store)
        indexer = InstanceSpotlightIndexer(catalog: catalog, dependencies: recorder.dependencies)
        let sessions = MinecraftSessionCoordinator(context: container.mainContext, dependencies: .init(load: { _ in Issue.record("Offline requested auth"); return nil }))
        playtime = try PlaytimeCoordinator(context: container.mainContext, journalDirectory: root.appendingPathComponent("sessions"))
        let helper = SkinTestFixtures.bundle.bundleURL.deletingLastPathComponent().appendingPathComponent("HakoPlaytimeHelper")
        games = GameLaunchCoordinator(store: store, sessions: sessions, playtime: playtime, runner: .init(helperURL: helper), prepare: { request in
            .init(executable: URL(fileURLWithPath: "/bin/sleep"), arguments: ["20"], workingDirectory: request.root.appendingPathComponent("minecraft"))
        })
        quick = QuickLaunchCoordinator(store: store, games: games)
    }

    func instance(_ name: String, state: InstallationState = .ready) throws -> GameInstance {
        var draft = InstanceDraft(); draft.name = name; draft.offlineMode = true
        let instance = try store.create(draft, versionID: "1.21", metadataURL: "url", metadataSHA1: "sha")
        instance.state = state
        try container.mainContext.save()
        return instance
    }

    func cleanup() {
        indexer.stop()
        for instance in (try? container.mainContext.fetch(FetchDescriptor<GameInstance>())) ?? [] {
            if let directory = try? store.storage.directory(instance.folderName),
               let record = try? GameProcessRecord.load(in: directory), record.isRunning { kill(record.pid, SIGTERM) }
        }
        try? FileManager.default.removeItem(at: root)
    }
}

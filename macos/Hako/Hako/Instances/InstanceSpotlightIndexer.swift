import AppIntents
import CoreSpotlight
import Foundation
import OSLog
import SwiftData

@MainActor final class InstanceSpotlightIndexer {
    static let indexName = "com.Launcher.Hako.instances"

    struct Dependencies {
        var reset: () async throws -> Void
        var index: ([GameInstanceEntity]) async throws -> Void
        var delete: ([UUID]) async throws -> Void
        var updateShortcuts: () -> Void

        static func live() -> Dependencies {
            let index = CSSearchableIndex(name: InstanceSpotlightIndexer.indexName)
            return .init(reset: { try await index.deleteAppEntities(ofType: GameInstanceEntity.self) },
                         index: { try await index.indexAppEntities($0) },
                         delete: { try await index.deleteAppEntities(identifiedBy: $0, ofType: GameInstanceEntity.self) },
                         updateShortcuts: { HakoShortcuts.updateAppShortcutParameters() })
        }
    }

    private let catalog: InstanceEntityCatalog
    private let dependencies: Dependencies
    private let logger = Logger(subsystem: "com.Launcher.Hako", category: "Spotlight")
    private var applied: [UUID: GameInstanceEntity]?
    private var operation: Task<Void, any Error>?
    private var observer: NSObjectProtocol?

    init(catalog: InstanceEntityCatalog, dependencies: Dependencies? = nil) {
        self.catalog = catalog; self.dependencies = dependencies ?? .live()
    }

    func start() {
        guard observer == nil else { return }
        observer = NotificationCenter.default.addObserver(forName: ModelContext.didSave, object: catalog.store.context, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.scheduleSync() }
        }
        scheduleSync()
    }

    func stop() {
        if let observer { NotificationCenter.default.removeObserver(observer) }
        observer = nil
    }

    func synchronize(rebuild: Bool = false) async throws {
        let previous = operation
        let task = Task {
            _ = await previous?.result
            try await self.apply(rebuild: rebuild)
        }
        operation = task
        try await task.value
    }

    func reindex(identifiers: [UUID]) async throws {
        let previous = operation
        let task = Task {
            _ = await previous?.result
            let entities = try self.catalog.readyEntities().filter { identifiers.contains($0.id) }
            let present = Set(entities.map(\.id)), missing = identifiers.filter { !present.contains($0) }
            do {
                if !missing.isEmpty { try await self.dependencies.delete(missing) }
                if !entities.isEmpty { try await self.dependencies.index(entities) }
            } catch { self.applied = nil; throw error }
            if self.applied != nil {
                for id in missing { self.applied?[id] = nil }
                for entity in entities { self.applied?[entity.id] = entity }
            }
            self.dependencies.updateShortcuts()
        }
        operation = task
        try await task.value
    }

    private func scheduleSync() {
        Task { [weak self] in
            guard let self else { return }
            do { try await synchronize() }
            catch { logger.error("Could not synchronize instance index: \(error.localizedDescription, privacy: .public)") }
        }
    }

    private func apply(rebuild: Bool) async throws {
        let entities = try catalog.readyEntities()
        let desired = Dictionary(uniqueKeysWithValues: entities.map { ($0.id, $0) })
        guard rebuild || applied != desired else { return }
        dependencies.updateShortcuts()
        do {
            if rebuild || applied == nil {
                try await dependencies.reset()
                if !entities.isEmpty { try await dependencies.index(entities) }
            } else if let applied {
                let removed = applied.keys.filter { desired[$0] == nil }
                let changed = entities.filter { applied[$0.id] != $0 }
                if !removed.isEmpty { try await dependencies.delete(removed) }
                if !changed.isEmpty { try await dependencies.index(changed) }
            }
        } catch { applied = nil; throw error }
        applied = desired
    }
}

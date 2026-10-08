import AppIntents
import CoreSpotlight
import Foundation
import SwiftData

nonisolated struct GameInstanceEntity: IndexedEntity, Equatable {
    static let typeDisplayRepresentation = TypeDisplayRepresentation(name: "Сборка Minecraft")
    static let defaultQuery = GameInstanceQuery()

    let id: UUID
    let name: String
    let version: String
    let loader: String
    let iconSymbol: String
    let iconRevision: UUID
    let iconURL: URL?

    var displayRepresentation: DisplayRepresentation {
        let image: DisplayRepresentation.Image = iconURL.map { .init(url: $0) }
            ?? .init(systemName: iconSymbol.isEmpty ? "shippingbox.fill" : iconSymbol)
        return .init(title: "\(name)", subtitle: "Minecraft \(version) · \(loader)", image: image)
    }

    var attributeSet: CSSearchableItemAttributeSet {
        let attributes = defaultAttributeSet
        attributes.title = name
        attributes.contentDescription = "Minecraft \(version) · \(loader)"
        attributes.keywords = ["Hako", "Minecraft", name, version, loader, "Launch", "Запустить"]
        attributes.thumbnailURL = iconURL
        return attributes
    }
}

@MainActor final class InstanceEntityCatalog {
    let store: InstanceStore

    init(store: InstanceStore) { self.store = store }

    func readyEntities() throws -> [GameInstanceEntity] {
        try instances().filter { $0.state == .ready }.map(entity)
    }

    func entities(for identifiers: [UUID]) throws -> [GameInstanceEntity] {
        let values = Dictionary(uniqueKeysWithValues: try instances().map { ($0.id, $0) })
        return identifiers.compactMap { values[$0].map(entity) }
    }

    func entities(matching string: String) throws -> [GameInstanceEntity] {
        let query = string.trimmingCharacters(in: .whitespacesAndNewlines)
        return try readyEntities().filter { query.isEmpty || $0.name.localizedStandardContains(query) }
    }

    private func instances() throws -> [GameInstance] {
        try store.context.fetch(FetchDescriptor<GameInstance>()).sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    private func entity(_ instance: GameInstance) -> GameInstanceEntity {
        let icon = instance.iconSymbol.isEmpty ? try? InstanceStorage.containedURL("icon.png", in: store.storage.directory(instance.folderName)) : nil
        return .init(id: instance.id, name: instance.name, version: instance.versionID, loader: instance.loaderTitle,
                     iconSymbol: instance.iconSymbol, iconRevision: instance.iconRevision, iconURL: icon)
    }
}

nonisolated struct GameInstanceQuery: EntityStringQuery, EnumerableEntityQuery, IndexedEntityQuery {
    static var allowedExecutionTargets: IntentExecutionTargets { .main }
    private var catalog: InstanceEntityCatalog?
    private var indexer: InstanceSpotlightIndexer?

    init() {}
    init(catalog: InstanceEntityCatalog, indexer: InstanceSpotlightIndexer? = nil) {
        self.catalog = catalog; self.indexer = indexer
    }

    @MainActor private var resolvedCatalog: InstanceEntityCatalog { catalog ?? HakoServices.shared.instanceCatalog }
    @MainActor private var resolvedIndexer: InstanceSpotlightIndexer { indexer ?? HakoServices.shared.spotlight }

    @MainActor func entities(for identifiers: [UUID]) async throws -> [GameInstanceEntity] {
        try resolvedCatalog.entities(for: identifiers)
    }

    @MainActor func entities(matching string: String) async throws -> [GameInstanceEntity] {
        try resolvedCatalog.entities(matching: string)
    }

    @MainActor func allEntities() async throws -> [GameInstanceEntity] { try resolvedCatalog.readyEntities() }
    @MainActor func suggestedEntities() async throws -> [GameInstanceEntity] { try resolvedCatalog.readyEntities() }

    @MainActor func reindexEntities(for identifiers: [UUID], indexDescription: CSSearchableIndexDescription) async throws {
        try await resolvedIndexer.reindex(identifiers: identifiers)
    }

    @MainActor func reindexAllEntities(indexDescription: CSSearchableIndexDescription) async throws {
        try await resolvedIndexer.synchronize(rebuild: true)
    }
}

import SwiftData

/// Единственные контейнер и координаторы основного процесса, включая системные команды.
@MainActor final class HakoServices {
    static let shared: HakoServices = {
        do { return try HakoServices() }
        catch { fatalError("Could not create ModelContainer: \(error)") }
    }()

    let modelContainer: ModelContainer
    let installations: InstallationCoordinator
    let content: InstanceContentController
    let sessions: MinecraftSessionCoordinator
    let games: GameLaunchCoordinator
    let playtime: PlaytimeCoordinator
    let renameExit = InstanceRenameExitCoordinator()
    let quickLaunch: QuickLaunchCoordinator
    let instanceCatalog: InstanceEntityCatalog
    let spotlight: InstanceSpotlightIndexer

    private init() throws {
        let schema = HakoSchema.schema
        let configuration = ModelConfiguration(schema: schema, url: try AppDataLocation.storeURL())
        let container = try ModelContainer(for: schema, configurations: [configuration])
        try LaunchSettingsMigration.run(context: container.mainContext)
        modelContainer = container
        installations = InstallationCoordinator(context: container.mainContext)
        content = InstanceContentController(installations: installations)
        sessions = MinecraftSessionCoordinator(context: container.mainContext)
        playtime = try PlaytimeCoordinator(context: container.mainContext)
        games = GameLaunchCoordinator(store: installations.store, sessions: sessions, playtime: playtime)
        quickLaunch = QuickLaunchCoordinator(store: installations.store, games: games)
        instanceCatalog = InstanceEntityCatalog(store: installations.store)
        spotlight = InstanceSpotlightIndexer(catalog: instanceCatalog)
        spotlight.start()
    }

    func start() {
        games.start()
        installations.start()
    }
}

import Foundation
import SwiftData

/// Единственные контейнер и координаторы основного процесса, включая системные команды.
@MainActor final class HakoServices {
    static let shared: HakoServices = {
        do { return try HakoServices() }
        catch { fatalError("Could not create ModelContainer: \(error)") }
    }()

    static func forCurrentLaunch() -> HakoServices {
        #if DEBUG
        guard ProcessInfo.processInfo.arguments.contains("--character-editor-preview") else { return shared }
        do { return try HakoServices(characterEditorPreview: true) }
        catch { fatalError("Could not create isolated character preview: \(error)") }
        #else
        return shared
        #endif
    }

    let modelContainer: ModelContainer
    let installations: InstallationCoordinator
    let content: InstanceContentController
    let backupRestore: BackupRestoreCoordinator
    let sessions: MinecraftSessionCoordinator
    let hakoAccount: HakoAccountCoordinator
    let appearances: MinecraftAppearanceStore
    let skinLoader: MinecraftSkinLoader
    let characterEditor: MinecraftCharacterEditorCoordinator
    let games: GameLaunchCoordinator
    let playtime: PlaytimeCoordinator
    let renameExit = InstanceRenameExitCoordinator()
    let quickLaunch: QuickLaunchCoordinator
    let instanceCatalog: InstanceEntityCatalog
    let spotlight: InstanceSpotlightIndexer
    let updates: AppUpdateCoordinator
    let startsInCharacterEditor: Bool
    private var didStart = false

    private init(characterEditorPreview: Bool = false) throws {
        let schema = HakoSchema.schema
        let configuration: ModelConfiguration
        let fixtureSession: URLSession?
        let storage: InstanceStorage
        let playtimeDirectory: URL
        if characterEditorPreview {
            MinecraftCharacterPreviewFixture.reset()
            fixtureSession = MinecraftCharacterPreviewFixture.makeSession()
            configuration = ModelConfiguration(schema: schema, isStoredInMemoryOnly: true)
            let root = FileManager.default.temporaryDirectory.appending(path: "Hako-Character-Editor-Preview", directoryHint: .isDirectory)
            storage = InstanceStorage(root: root)
            playtimeDirectory = root.appending(path: "playtime", directoryHint: .isDirectory)
        } else {
            fixtureSession = nil
            configuration = ModelConfiguration(schema: schema, url: try AppDataLocation.storeURL())
            storage = .init()
            playtimeDirectory = PlaytimeJournal.directory()
        }
        let container = try ModelContainer(for: schema, configurations: [configuration])
        if !characterEditorPreview { try LaunchSettingsMigration.run(context: container.mainContext) }
        modelContainer = container
        installations = InstallationCoordinator(context: container.mainContext, storage: storage)
        content = InstanceContentController(installations: installations)
        backupRestore = BackupRestoreCoordinator(installations: installations, content: content)
        appearances = MinecraftAppearanceStore()
        if let fixtureSession {
            skinLoader = MinecraftSkinLoader(session: fixtureSession)
        } else {
            skinLoader = .shared
        }
        let sessionDependencies: MinecraftSessionCoordinator.Dependencies?
        if characterEditorPreview {
            sessionDependencies = .init(
                load: { _ in AccountTokens(microsoftRefreshToken: "preview-refresh-token", minecraftAccessToken: "preview-access-token", minecraftTokenExpiration: .now.addingTimeInterval(3600)) },
                save: { _, _ in },
                refresh: { _ in MicrosoftToken(accessToken: "preview-microsoft-token", refreshToken: "preview-refresh-token", idToken: nil) },
                signIn: { _ in
                    MinecraftSession(
                        profile: MinecraftProfile(uuid: "00000000000000000000000000000001", name: "PreviewPlayer", skinURL: URL(string: "https://textures.minecraft.net/texture/hako-preview-skin"), skinVariant: .classic),
                        accessToken: "preview-access-token-2", expiration: .now.addingTimeInterval(3600)
                    )
                }
            )
        } else {
            sessionDependencies = nil
        }
        sessions = MinecraftSessionCoordinator(context: container.mainContext, dependencies: sessionDependencies, appearances: appearances)
        if characterEditorPreview {
            hakoAccount = HakoAccountCoordinator(
                api: PreviewHakoAuthAPI(), store: PreviewHakoSessionStore(), initiallyRestored: true
            )
        } else {
            hakoAccount = HakoAccountCoordinator(api: HakoAuthAPIClient(), store: HakoSessionKeychainStore())
        }
        characterEditor = MinecraftCharacterEditorCoordinator(
            api: MinecraftProfileAPIClient(session: fixtureSession ?? .shared), sessions: sessions,
            context: container.mainContext, appearances: appearances
        )
        playtime = try PlaytimeCoordinator(context: container.mainContext, journalDirectory: playtimeDirectory)
        games = GameLaunchCoordinator(store: installations.store, sessions: sessions, playtime: playtime)
        quickLaunch = QuickLaunchCoordinator(store: installations.store, games: games)
        instanceCatalog = InstanceEntityCatalog(store: installations.store)
        spotlight = InstanceSpotlightIndexer(catalog: instanceCatalog)
        if !characterEditorPreview { spotlight.start() }
        updates = AppUpdateCoordinator(store: installations.store)
        startsInCharacterEditor = characterEditorPreview
        if characterEditorPreview {
            let account = Account(xbox: XboxProfile(xuid: "character-editor-preview", gamertag: "PreviewPlayer", avatarURL: nil), email: "preview@example.test")
            account.minecraftUUID = "00000000000000000000000000000001"
            account.minecraftName = "PreviewPlayer"
            account.minecraftSkinURL = URL(string: "https://textures.minecraft.net/texture/hako-preview-skin")
            account.minecraftSkinVariant = MinecraftSkinVariant.classic.rawValue
            container.mainContext.insert(account)
            try container.mainContext.save()
            sessions.installFixtureIdentity(
                .init(name: "PreviewPlayer", uuid: account.minecraftUUID!, accessToken: "preview-access-token", xuid: account.xuid),
                for: account,
                expiration: .now.addingTimeInterval(3600)
            )
        }
    }

    func start() {
        guard !startsInCharacterEditor, !didStart else { return }
        didStart = true
        Task { await hakoAccount.initialize() }
        games.start()
        installations.start()
        updates.start()
    }
}

nonisolated struct PreviewHakoAuthAPI: HakoAuthAPI {
    func register(deviceName: String) async throws -> HakoPendingAuthRequest { throw HakoAuthError.serviceUnavailable }
    func login(username: String, deviceName: String) async throws -> HakoPendingAuthRequest { throw HakoAuthError.serviceUnavailable }
    func requestStatus(_ request: HakoPendingAuthRequest) async throws -> HakoAuthRequestStatus { throw HakoAuthError.serviceUnavailable }
    func exchange(_ request: HakoPendingAuthRequest) async throws -> HakoExchangeResult { throw HakoAuthError.serviceUnavailable }
    func me(token: String) async throws -> HakoMeResult { throw HakoAuthError.serviceUnavailable }
    func logout(token: String, timeout: Duration) async throws { throw HakoAuthError.serviceUnavailable }
}

nonisolated struct PreviewHakoSessionStore: HakoSessionStore {
    func load() async throws -> HakoSession? { nil }
    func save(_ session: HakoSession) async throws { throw HakoAuthError.persistence }
    func clear(ifMatching token: String) async throws -> Bool { false }
}

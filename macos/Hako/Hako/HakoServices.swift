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
        if ProcessInfo.processInfo.arguments.contains("--playtime-profile-preview") {
            do { return try HakoServices(playtimeProfilePreview: true) }
            catch { fatalError("Could not create isolated playtime profile preview: \(error)") }
        }
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
    let appStorage: UserDefaults
    let startsInCharacterEditor: Bool
    let startsInProfilePreview: Bool
    private var didStart = false

    private init(characterEditorPreview: Bool = false, playtimeProfilePreview: Bool = false) throws {
        let isolatedPreview = characterEditorPreview || playtimeProfilePreview
        appStorage = isolatedPreview ? UserDefaults(suiteName: "Hako.Preview.\(UUID().uuidString)")! : .standard
        let schema = HakoSchema.schema
        let configuration: ModelConfiguration
        let fixtureSession: URLSession?
        let storage: InstanceStorage
        let playtimeDirectory: URL
        if isolatedPreview {
            MinecraftCharacterPreviewFixture.reset()
            fixtureSession = MinecraftCharacterPreviewFixture.makeSession()
            configuration = ModelConfiguration(schema: schema, isStoredInMemoryOnly: true)
            let previewName = characterEditorPreview ? "Hako-Character-Editor-Preview" : "Hako-Playtime-Profile-Preview-\(UUID().uuidString)"
            let root = FileManager.default.temporaryDirectory.appending(path: previewName, directoryHint: .isDirectory)
            storage = InstanceStorage(root: root)
            playtimeDirectory = root.appending(path: "playtime", directoryHint: .isDirectory)
        } else {
            fixtureSession = nil
            configuration = ModelConfiguration(schema: schema, url: try AppDataLocation.storeURL())
            storage = .init()
            playtimeDirectory = PlaytimeJournal.directory()
        }
        let container = try ModelContainer(for: schema, configurations: [configuration])
        if !isolatedPreview { try LaunchSettingsMigration.run(context: container.mainContext) }
        if playtimeProfilePreview {
            let microsoft = PlayerPlaytime(ownerKey: PlaytimeCoordinator.ownerKey("playtime-preview-xuid"))
            microsoft.totalSeconds = 3720
            let guest = PlayerPlaytime(ownerKey: PlaytimeCoordinator.ownerKey(nil))
            guest.totalSeconds = 45
            container.mainContext.insert(microsoft)
            container.mainContext.insert(guest)
            try container.mainContext.insert(HakoPlaytimeSubmission(
                sessionID: UUID(uuidString: "33333333-3333-4333-8333-333333333333")!,
                hakoOwnerID: PreviewHakoAuthAPI.ownerID,
                durationMS: 45_000,
                localAllocations: ["guest": 45_000]
            ))
            try container.mainContext.save()
        }
        let playtimeAPI: any HakoPlaytimeAPI = playtimeProfilePreview ? PreviewHakoPlaytimeAPI() : HakoPlaytimeAPIClient()
        let playtimeCoordinator = try PlaytimeCoordinator(
            context: container.mainContext,
            journalDirectory: playtimeDirectory,
            api: playtimeAPI
        )
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
        if isolatedPreview {
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
        } else if playtimeProfilePreview {
            let fixtureSession = HakoSession(
                token: "preview-hako-token",
                account: PreviewHakoAuthAPI.previewAccount,
                deviceName: "Preview Mac",
                expiresAt: .now.addingTimeInterval(3600),
                verifiedAt: .now
            )
            hakoAccount = HakoAccountCoordinator(
                api: PreviewHakoAuthAPI(), store: PreviewHakoSessionStore(), playtime: playtimeCoordinator,
                initiallyRestored: true, initialSession: fixtureSession
            )
        } else {
            hakoAccount = HakoAccountCoordinator(
                api: HakoAuthAPIClient(), store: HakoSessionKeychainStore(), playtime: playtimeCoordinator
            )
        }
        characterEditor = MinecraftCharacterEditorCoordinator(
            api: MinecraftProfileAPIClient(session: fixtureSession ?? .shared), sessions: sessions,
            context: container.mainContext, appearances: appearances
        )
        playtime = playtimeCoordinator
        games = GameLaunchCoordinator(store: installations.store, sessions: sessions, playtime: playtime)
        quickLaunch = QuickLaunchCoordinator(store: installations.store, games: games)
        instanceCatalog = InstanceEntityCatalog(store: installations.store)
        spotlight = InstanceSpotlightIndexer(catalog: instanceCatalog)
        if !isolatedPreview { spotlight.start() }
        updates = AppUpdateCoordinator(store: installations.store)
        startsInCharacterEditor = characterEditorPreview
        startsInProfilePreview = playtimeProfilePreview
        if isolatedPreview {
            let isProfilePreview = playtimeProfilePreview
            let account = Account(
                xbox: XboxProfile(
                    xuid: isProfilePreview ? "playtime-preview-xuid" : "character-editor-preview",
                    gamertag: isProfilePreview ? "PlaytimePreview" : "PreviewPlayer",
                    avatarURL: nil
                ),
                email: isProfilePreview ? "playtime-preview@example.test" : "preview@example.test"
            )
            account.minecraftUUID = "00000000000000000000000000000001"
            account.minecraftName = isProfilePreview ? "PlaytimePreview" : "PreviewPlayer"
            account.minecraftSkinURL = URL(string: "https://textures.minecraft.net/texture/hako-preview-skin")
            account.minecraftSkinVariant = MinecraftSkinVariant.classic.rawValue
            container.mainContext.insert(account)
            try container.mainContext.save()
            sessions.installFixtureIdentity(
                .init(name: account.minecraftName!, uuid: account.minecraftUUID!, accessToken: "preview-access-token", xuid: account.xuid),
                for: account,
                expiration: .now.addingTimeInterval(3600)
            )
        }
    }

    func start() {
        guard !startsInCharacterEditor, !didStart else { return }
        didStart = true
        playtime.start()
        if startsInProfilePreview { return }
        Task { await hakoAccount.initialize() }
        games.start()
        installations.start()
        updates.start()
    }
}

nonisolated struct PreviewHakoAuthAPI: HakoAuthAPI {
    static let ownerID = "44444444-4444-4444-8444-444444444444"
    static let previewAccount = HakoAccount(id: ownerID, username: "PlaytimePreview", createdAt: .now)
    func register(deviceName: String) async throws -> HakoPendingAuthRequest { throw HakoAuthError.serviceUnavailable }
    func login(username: String, deviceName: String) async throws -> HakoPendingAuthRequest { throw HakoAuthError.serviceUnavailable }
    func requestStatus(_ request: HakoPendingAuthRequest) async throws -> HakoAuthRequestStatus { throw HakoAuthError.serviceUnavailable }
    func exchange(_ request: HakoPendingAuthRequest) async throws -> HakoExchangeResult { throw HakoAuthError.serviceUnavailable }
    func me(token: String) async throws -> HakoMeResult {
        HakoMeResult(account: Self.previewAccount, deviceName: "Preview Mac", sessionExpiresAt: .now.addingTimeInterval(3600))
    }
    func logout(token: String, timeout: Duration) async throws { throw HakoAuthError.serviceUnavailable }
}

nonisolated struct PreviewHakoPlaytimeAPI: HakoPlaytimeAPI {
    func postSession(sessionID: UUID, durationMS: Int64, token: String) async throws -> UUID {
        throw CancellationError()
    }

    func totalDuration(token: String) async throws -> Int64 { 21_600_000 }
}

nonisolated struct PreviewHakoSessionStore: HakoSessionStore {
    func load() async throws -> HakoSession? { nil }
    func save(_ session: HakoSession) async throws {}
    func clear(ifMatching token: String) async throws -> Bool { false }
}

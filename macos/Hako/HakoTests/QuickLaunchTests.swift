import Darwin
import Foundation
import SwiftData
import Testing

@Suite(.serialized) @MainActor struct QuickLaunchTests {
    private func eventually(_ condition: () -> Bool) async throws {
        for _ in 0..<100 { if condition() { return }; try await Task.sleep(for: .milliseconds(20)) }
        #expect(condition())
    }

    private func acceptNavigation(_ coordinator: QuickLaunchCoordinator) {
        coordinator.registerWindowPresenter { [weak coordinator] in
            if let request = coordinator?.presentation { coordinator?.completePresentation(request.id) }
        }
    }

    @Test func waitsForProfileAndCoalescesOfflineRequestsWithoutAuthentication() async throws {
        let probe = QuickLaunchProbe()
        let fixture = try QuickLaunchFixture(dependencies: .init(load: { _ in Issue.record("Offline requested auth"); return nil }), prepare: { request in
            await probe.add(request.identity)
            return .init(executable: URL(fileURLWithPath: "/bin/sleep"), arguments: ["20"], workingDirectory: request.root.appendingPathComponent("minecraft"))
        })
        defer { fixture.cleanup() }
        let instance = try fixture.instance()
        let first = Task { try await fixture.quick.launch(instanceID: instance.id) }
        let second = Task { try await fixture.quick.launch(instanceID: instance.id) }
        try await eventually { fixture.quick.presentation != nil }
        #expect(await probe.count == 0)
        let request = try #require(fixture.quick.presentation)
        #expect(request.instanceID == instance.id && fixture.quick.preparingIDs == [instance.id])
        fixture.quick.completePresentation(request.id)
        #expect(try await first.value == .started)
        #expect(try await second.value == .started)
        #expect(await probe.count == 1 && fixture.games.states[instance.id] == .running)
        #expect(fixture.quick.preparingIDs.isEmpty && fixture.quick.presentation == nil)
        acceptNavigation(fixture.quick)
        #expect(try await fixture.quick.launch(instanceID: instance.id) == .alreadyRunning)
        #expect(await probe.count == 1)
    }

    @Test func coldOnlineLaunchRefreshesSessionBeforePreparingGame() async throws {
        let probe = QuickLaunchProbe()
        var tokens = AccountTokens(microsoftRefreshToken: "old"), refreshes = 0
        let fixture = try QuickLaunchFixture(dependencies: .init(load: { _ in tokens }, save: { value, _ in tokens = value }, refresh: { _ in
            refreshes += 1
            return .init(accessToken: "ms", refreshToken: "new", idToken: nil)
        }, signIn: { _ in .init(profile: .init(uuid: "uuid", name: "Steve", skinURL: nil), accessToken: "mc", expiration: .now.addingTimeInterval(3600)) }), prepare: { request in
            await probe.add(request.identity)
            throw InstanceFileError.message("Подготовка проверена")
        })
        defer { fixture.cleanup() }
        let instance = try fixture.instance(offline: false)
        let account = Account(xbox: .init(xuid: "player", gamertag: "Steve", avatarURL: nil), email: nil)
        fixture.container.mainContext.insert(account)
        try fixture.container.mainContext.save()
        acceptNavigation(fixture.quick)
        await #expect(throws: InstanceFileError.self) { try await fixture.quick.launch(instanceID: instance.id) }
        #expect(refreshes == 1 && tokens.microsoftRefreshToken == "new")
        #expect(await probe.identities.first?.accessToken == "mc")
        #expect(fixture.quick.error == "Подготовка проверена")
        #expect(fixture.playtime.totalSeconds(xuid: account.xuid) == 0)
    }

    @Test func authenticationFailureIsReportedWithoutOfflineFallback() async throws {
        let probe = QuickLaunchProbe()
        let fixture = try QuickLaunchFixture(dependencies: .init(load: { _ in nil }), prepare: { request in
            await probe.add(request.identity)
            throw InstanceFileError.message("Unexpected preparation")
        })
        defer { fixture.cleanup() }
        let instance = try fixture.instance(offline: false)
        fixture.container.mainContext.insert(Account(xbox: .init(xuid: "player", gamertag: "Steve", avatarURL: nil), email: nil))
        try fixture.container.mainContext.save()
        acceptNavigation(fixture.quick)
        await #expect(throws: InstanceFileError.self) { try await fixture.quick.launch(instanceID: instance.id) }
        #expect(fixture.quick.error == "Данные входа недоступны. Войдите в Microsoft повторно.")
        #expect(await probe.count == 0 && !instance.offlineMode)
    }

    @Test func guestCannotLaunchAnOnlineInstance() async throws {
        let fixture = try QuickLaunchFixture()
        defer { fixture.cleanup() }
        let instance = try fixture.instance(offline: false)
        acceptNavigation(fixture.quick)
        await #expect(throws: InstanceFileError.self) { try await fixture.quick.launch(instanceID: instance.id) }
        #expect(fixture.quick.error?.contains("Minecraft-токен") == true && !instance.offlineMode)
    }

    @Test func signOutDuringConnectionDoesNotAuthorizeTheOldAccount() async throws {
        var refreshing = false
        let fixture = try QuickLaunchFixture(dependencies: .init(load: { _ in .init(microsoftRefreshToken: "old") }, refresh: { _ in
            refreshing = true
            try await Task.sleep(for: .seconds(10))
            return .init(accessToken: "ms", refreshToken: "new", idToken: nil)
        }))
        defer { fixture.cleanup() }
        let instance = try fixture.instance(offline: false)
        let account = Account(xbox: .init(xuid: "player", gamertag: "Steve", avatarURL: nil), email: nil)
        fixture.container.mainContext.insert(account)
        try fixture.container.mainContext.save()
        acceptNavigation(fixture.quick)
        let task = Task { try await fixture.quick.launch(instanceID: instance.id) }
        try await eventually { refreshing }
        fixture.games.sessions.signOut()
        fixture.container.mainContext.delete(account)
        try fixture.container.mainContext.save()
        await #expect(throws: InstanceFileError.self) { try await task.value }
        #expect(fixture.quick.error == "Minecraft-сессия изменилась или завершилась. Повторите запуск.")
        #expect(fixture.games.states[instance.id] == nil)
    }

    @Test func recoversAnAlreadyRunningGameBeforeConsideringAuthentication() async throws {
        let fixture = try QuickLaunchFixture(prepare: { request in
            .init(executable: URL(fileURLWithPath: "/bin/sleep"), arguments: ["20"], workingDirectory: request.root.appendingPathComponent("minecraft"))
        })
        defer { fixture.cleanup() }
        let instance = try fixture.instance()
        try await fixture.games.launchAndWait(instance, account: nil)
        let directory = try fixture.store.storage.directory(instance.folderName)
        let beforeRecord = try GameProcessRecord.load(in: directory)
        let before = try #require(beforeRecord)
        let helper = SkinTestFixtures.bundle.bundleURL.deletingLastPathComponent().appendingPathComponent("HakoPlaytimeHelper")
        let recovered = GameLaunchCoordinator(store: fixture.store, sessions: fixture.games.sessions, playtime: fixture.playtime, runner: .init(helperURL: helper), prepare: { _ in
            Issue.record("Running game was launched again")
            throw InstanceFileError.message("Unexpected preparation")
        })
        let quick = QuickLaunchCoordinator(store: fixture.store, games: recovered)
        acceptNavigation(quick)
        #expect(try await quick.launch(instanceID: instance.id) == .alreadyRunning)
        let afterRecord = try GameProcessRecord.load(in: directory)
        let after = try #require(afterRecord)
        #expect(after.pid == before.pid && after.sessionID == before.sessionID)
        #expect(recovered.states[instance.id] == .running)
    }

    @Test func deletionWhileWaitingForNavigationDoesNotLaunchStaleModel() async throws {
        let fixture = try QuickLaunchFixture()
        defer { fixture.cleanup() }
        let instance = try fixture.instance(), id = instance.id
        let task = Task { try await fixture.quick.launch(instanceID: id) }
        try await eventually { fixture.quick.presentation != nil }
        let request = try #require(fixture.quick.presentation)
        try fixture.store.delete(instance)
        fixture.quick.completePresentation(request.id)
        await #expect(throws: InstanceFileError.self) { try await task.value }
        #expect(fixture.quick.error == "Сборка больше не существует.")
    }

    @Test func navigationFailureAndWindowCloseDoNotBeginGamePreparation() async throws {
        let fixture = try QuickLaunchFixture()
        defer { fixture.cleanup() }
        let instance = try fixture.instance()
        let first = Task { try await fixture.quick.launch(instanceID: instance.id) }
        try await eventually { fixture.quick.presentation != nil }
        fixture.quick.completePresentation(try #require(fixture.quick.presentation).id, error: InstanceFileError.message("Ошибка переименования"))
        await #expect(throws: InstanceFileError.self) { try await first.value }
        #expect(fixture.quick.error == "Ошибка переименования")
        fixture.quick.error = nil
        let second = Task { try await fixture.quick.launch(instanceID: instance.id) }
        try await eventually { fixture.quick.presentation != nil }
        fixture.quick.cancelPresentations()
        await #expect(throws: CancellationError.self) { try await second.value }
        #expect(fixture.games.states[instance.id] == nil && fixture.quick.error == nil)
    }

    @Test func busyAndUninstalledInstancesFailBeforeAuthentication() async throws {
        let fixture = try QuickLaunchFixture(dependencies: .init(load: { _ in Issue.record("Blocked launch requested auth"); return nil }))
        defer { fixture.cleanup() }
        let instance = try fixture.instance(offline: false)
        acceptNavigation(fixture.quick)
        fixture.store.contentBusy.insert(instance.id)
        await #expect(throws: InstanceFileError.self) { try await fixture.quick.launch(instanceID: instance.id) }
        #expect(fixture.quick.error == "Дождитесь завершения операций с файлами сборки.")
        fixture.store.contentBusy.remove(instance.id)
        instance.state = .installing
        await #expect(throws: InstanceFileError.self) { try await fixture.quick.launch(instanceID: instance.id) }
        #expect(fixture.quick.error == "Дождитесь завершения установки сборки.")
    }
}

@MainActor private final class QuickLaunchFixture {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    let container: ModelContainer
    let store: InstanceStore
    let playtime: PlaytimeCoordinator
    let games: GameLaunchCoordinator
    let quick: QuickLaunchCoordinator

    init(dependencies: MinecraftSessionCoordinator.Dependencies? = nil, prepare: @escaping @Sendable (MinecraftLaunchRequest) async throws -> MinecraftLaunchPlan = { _ in throw InstanceFileError.message("Unexpected preparation") }) throws {
        container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        store = InstanceStore(context: container.mainContext, storage: .init(root: root))
        let sessions = MinecraftSessionCoordinator(context: container.mainContext, dependencies: dependencies)
        playtime = try PlaytimeCoordinator(context: container.mainContext, journalDirectory: root.appendingPathComponent("sessions"))
        let runner = GameProcessRunner(helperURL: SkinTestFixtures.bundle.bundleURL.deletingLastPathComponent().appendingPathComponent("HakoPlaytimeHelper"))
        games = GameLaunchCoordinator(store: store, sessions: sessions, playtime: playtime, runner: runner, prepare: prepare)
        quick = QuickLaunchCoordinator(store: store, games: games)
    }

    func instance(offline: Bool = true) throws -> GameInstance {
        var draft = InstanceDraft(); draft.name = "Quick Pack"; draft.offlineMode = offline
        let instance = try store.create(draft, versionID: "v", metadataURL: "url", metadataSHA1: "sha")
        instance.state = .ready
        try container.mainContext.save()
        return instance
    }

    func cleanup() {
        for instance in (try? container.mainContext.fetch(FetchDescriptor<GameInstance>())) ?? [] {
            if let directory = try? store.storage.directory(instance.folderName),
               let record = try? GameProcessRecord.load(in: directory), record.isRunning { kill(record.pid, SIGTERM) }
        }
        try? FileManager.default.removeItem(at: root)
    }
}

private actor QuickLaunchProbe {
    var identities: [MinecraftLaunchIdentity] = []
    var count: Int { identities.count }
    func add(_ identity: MinecraftLaunchIdentity) { identities.append(identity) }
}

import Foundation
import Security
import SwiftData
import Testing

@Suite(.serialized) @MainActor struct HakoAuthTests {
    private func eventually(_ condition: @escaping @MainActor () async -> Bool) async throws {
        for _ in 0..<200 {
            if await condition() { return }
            try await Task.sleep(for: .milliseconds(10))
        }
        #expect(await condition())
    }

    private func testSleep(clock: HakoAuthTestClock) -> @Sendable (Duration) async throws -> Void {
        { duration in
            if duration > .seconds(60) {
                try await Task.sleep(for: .seconds(86_400))
            } else {
                clock.advance(by: duration)
            }
        }
    }

    private func coordinator(
        api: HakoAuthTestService,
        store: HakoAuthTestSessionStore,
        clock: HakoAuthTestClock,
        initialSession: HakoSession? = nil
    ) -> HakoAccountCoordinator {
        HakoAccountCoordinator(
            api: api,
            store: store,
            now: { clock.now() },
            sleep: testSleep(clock: clock),
            initiallyRestored: initialSession != nil,
            initialSession: initialSession
        )
    }

    @Test func registrationLoginStatusAndExchangeUseIndependentWireDTOs() async throws {
        let requestID = UUID(uuidString: "55555555-5555-4555-8555-555555555555")!
        let expires = "2030-05-04T03:02:01.123Z"
        HakoAuthTestURLProtocol.prepare([
            "auth.test/v1/auth/register": .json([
                "request_id": requestID.uuidString,
                "bot_link": "https://t.me/HakoLoginBot?start=abc123",
                "exchange_secret": "request-secret",
                "expires_at": expires,
            ]),
            "auth.test/v1/auth/requests/\(requestID.uuidString.lowercased())": .json([
                "status": "approved", "expires_at": expires,
            ]),
            "auth.test/v1/auth/exchange": .json([
                "token": "session-token",
                "token_expires_at": "2030-05-18T03:02:01Z",
                "account": ["id": "11111111-1111-4111-8111-111111111111", "username": "Player_123", "created_at": "2026-01-02T03:04:05Z"],
            ]),
        ])
        let session = HakoAuthTestURLProtocol.session()
        defer { session.invalidateAndCancel() }
        let client = HakoAuthAPIClient(session: session, baseURL: URL(string: "https://auth.test:443/")!)

        let request = try await client.register(deviceName: "  Mac mini  ")
        let status = try await client.requestStatus(request)
        let exchange = try await client.exchange(request)

        #expect(request.requestID == requestID)
        #expect(request.botLink?.absoluteString == "https://t.me/HakoLoginBot?start=abc123")
        #expect(request.description == "HakoPendingAuthRequest(requestID: \(requestID), expiresAt: \(request.expiresAt))")
        #expect(status.status == .approved)
        #expect(exchange.account.username == "Player_123")
        #expect(exchange.token == "session-token")
        #expect(!exchange.debugDescription.contains("session-token"))

        let requests = HakoAuthTestURLProtocol.requests
        #expect(requests.count == 3)
        #expect(requests[0].httpMethod == "POST")
        #expect(requests[0].value(forHTTPHeaderField: "Authorization") == nil)
        let registerBody = try #require(HakoAuthTestURLProtocol.bodies[0].flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: String] })
        #expect(registerBody == ["device_name": "Mac mini"])
        #expect(requests[1].value(forHTTPHeaderField: "Authorization") == "Bearer request-secret")
        #expect(requests[2].value(forHTTPHeaderField: "Authorization") == "Bearer request-secret")
        #expect(try #require(HakoAuthTestURLProtocol.bodies[2].flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: String] }) == ["request_id": requestID.uuidString])
    }

    @Test func loginNormalizesTelegramUsernameAndTreatsBotLinkAsOptional() async throws {
        let requestID = UUID(uuidString: "66666666-6666-4666-8666-666666666666")!
        HakoAuthTestURLProtocol.prepare([
            "auth.test/v1/auth/login": .json([
                "request_id": requestID.uuidString,
                "bot_link": "http://example.com/not-telegram",
                "exchange_secret": "secret",
                "expires_at": "2030-05-04T03:02:01Z",
            ]),
        ])
        let session = HakoAuthTestURLProtocol.session()
        defer { session.invalidateAndCancel() }
        let client = HakoAuthAPIClient(session: session, baseURL: URL(string: "https://auth.test:443/")!)

        let pending = try await client.login(username: "  @Player_123  ", deviceName: "Mac")
        let body = try #require(HakoAuthTestURLProtocol.bodies.first?.flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: String] })
        #expect(body == ["username": "Player_123", "device_name": "Mac"])
        #expect(pending.botLink == nil)
    }

    @Test func telegramLinksAndUsernamesAreStrictlyValidated() throws {
        let valid = ["https://t.me/HakoBot?start=abc", "https://t.me:443/HakoBot?start=abc&source=hako"]
        let invalid = [
            "http://t.me/HakoBot?start=abc",
            "https://t.me:444/HakoBot?start=abc",
            "https://user@t.me/HakoBot?start=abc",
            "https://t.me/HakoBot",
            "https://t.me/HakoBot?start=",
            "https://t.me/HakoBot/extra?start=abc",
            "https://t.me/HakoBot%2Fextra?start=abc",
            "https://t.me/HakoBot/?start=abc",
        ]
        #expect(valid.allSatisfy { HakoTelegramLink.validate($0) != nil })
        #expect(invalid.allSatisfy { HakoTelegramLink.validate($0) == nil })
        #expect(try HakoUsername.normalize("  @Player_123  ") == "Player_123")
        #expect(throws: HakoAuthError.invalidUsername) { try HakoUsername.normalize("@привет") }
        #expect(throws: HakoAuthError.invalidUsername) { try HakoUsername.normalize("x") }
    }

    @Test func clientRejectsRedirectsAndBoundsWholeRequestDeadline() async throws {
        HakoAuthTestURLProtocol.prepare([
            "auth.test/v1/auth/register": .redirect(URL(string: "https://elsewhere.test/collect")!),
        ])
        let redirectSession = HakoAuthTestURLProtocol.session()
        defer { redirectSession.invalidateAndCancel() }
        let redirectClient = HakoAuthAPIClient(session: redirectSession, baseURL: URL(string: "https://auth.test:443/")!)
        await #expect(throws: HakoAuthError.redirectRejected) { try await redirectClient.register(deviceName: "Mac") }
        #expect(HakoAuthTestURLProtocol.requests.count == 1)

        HakoAuthTestURLProtocol.prepareHoldingRequest()
        let slowSession = HakoAuthTestURLProtocol.session()
        defer { slowSession.invalidateAndCancel() }
        let slowClient = HakoAuthAPIClient(session: slowSession, baseURL: URL(string: "https://auth.test:443/")!, requestTimeout: .milliseconds(40))
        await #expect(throws: HakoAuthError.timeout) { try await slowClient.register(deviceName: "Mac") }
    }

    @Test func sessionKeychainStoreDistinguishesMissingFailureAndTokenMatches() async throws {
        let backend = HakoAuthTestKeychainBackend()
        let store = HakoSessionKeychainStore(backend: backend)
        #expect(try await store.load() == nil)
        let first = Self.session(token: "hako-one")
        try await store.save(first)
        try await store.save(first.withDeviceName("MacBook"))
        #expect(backend.addCount == 1)
        #expect(backend.updateCount == 1)
        #expect(try await store.load()?.deviceName == "MacBook")
        #expect(try await store.clear(ifMatching: "other-token") == false)
        #expect(try await store.load()?.token == "hako-one")
        #expect(try await store.clear(ifMatching: "hako-one"))
        #expect(try await store.load() == nil)

        backend.setReadFailure(true)
        await #expect(throws: HakoSessionStoreError.self) { try await store.load() }
    }

    @Test func loginPersistsBeforePublishingAndExchangesOnlyOnce() async throws {
        let clock = HakoAuthTestClock()
        let api = HakoAuthTestService(now: clock.now())
        let store = HakoAuthTestSessionStore()
        let coordinator = coordinator(api: api, store: store, clock: clock)
        await coordinator.initialize()
        coordinator.beginLogin(username: " @Player_123 ")

        try await eventually { coordinator.session != nil }
        #expect(coordinator.session?.token == "session-token")
        #expect(coordinator.sessionStatus == .verified)
        #expect(await api.loginUsernames == ["Player_123"])
        #expect(await api.registerCalls == 0)
        #expect(await api.exchangeCalls == 1)
        #expect(await store.saveCalls == 1)
        #expect(await store.storedSession?.token == coordinator.session?.token)
        coordinator.stop()
    }

    @Test func registrationDoesNotSupplyUsernameAndExpiryShortensToServerDeadline() async throws {
        let clock = HakoAuthTestClock()
        let api = HakoAuthTestService(now: clock.now())
        await api.setStatuses([.init(status: .pending, expiresAt: clock.now().addingTimeInterval(3))])
        let store = HakoAuthTestSessionStore()
        let coordinator = HakoAccountCoordinator(
            api: api, store: store, now: { clock.now() },
            sleep: { duration in clock.advance(by: duration) }
        )
        await coordinator.initialize()
        coordinator.beginRegistration()

        try await eventually { coordinator.attemptPhase == .failed }
        #expect(coordinator.attemptError == .requestExpired)
        #expect(await api.registerCalls == 1)
        #expect(await api.loginUsernames.isEmpty)
        #expect(await api.statusCalls == 1)
        #expect(await api.exchangeCalls == 0)
        #expect(clock.sleepDurations == [.seconds(2), .seconds(1)])
        coordinator.stop()
    }

    @Test func cancellationWhilePollingCannotExchangeLateApproval() async throws {
        let clock = HakoAuthTestClock()
        let api = HakoAuthTestService(now: clock.now())
        await api.suspendNextStatus()
        let store = HakoAuthTestSessionStore()
        let coordinator = coordinator(api: api, store: store, clock: clock)
        await coordinator.initialize()
        coordinator.beginLogin(username: "Player_123")
        try await eventually { await api.statusCalls == 1 }

        coordinator.cancelAttempt()
        await api.resumeStatus(.init(status: .approved, expiresAt: clock.now().addingTimeInterval(300)))
        try await Task.sleep(for: .milliseconds(30))
        #expect(coordinator.attemptPhase == .idle)
        #expect(coordinator.session == nil)
        #expect(await api.exchangeCalls == 0)
        #expect(await store.saveCalls == 0)
        coordinator.stop()
    }

    @Test func cancellationDuringAsyncPersistenceClearsOnlyStaleTokenAndRevokesIt() async throws {
        let clock = HakoAuthTestClock()
        let api = HakoAuthTestService(now: clock.now())
        let store = HakoAuthTestSessionStore()
        await store.suspendNextSave()
        let coordinator = coordinator(api: api, store: store, clock: clock)
        await coordinator.initialize()
        coordinator.beginRegistration()
        try await eventually { await store.saveCalls == 1 }

        coordinator.cancelAttempt()
        await store.resumeSave()
        try await eventually {
            let stored = await store.storedSession
            let logouts = await api.logoutCalls
            return stored == nil && logouts.count == 1
        }
        #expect(coordinator.session == nil)
        #expect(await store.clearCalls == 1)
        #expect(await api.logoutCalls.first?.timeout == .seconds(5))
        coordinator.stop()
    }

    @Test func exchangeFailureRequiresFreshLoginAndDoesNotRetryConsumedRequest() async throws {
        let clock = HakoAuthTestClock()
        let api = HakoAuthTestService(now: clock.now())
        await api.setExchangeError(.network)
        let store = HakoAuthTestSessionStore()
        let coordinator = coordinator(api: api, store: store, clock: clock)
        await coordinator.initialize()
        coordinator.beginRegistration()

        try await eventually { coordinator.attemptPhase == .failed }
        #expect(coordinator.attemptError == .network)
        #expect(coordinator.requiresFreshLogin)
        #expect(await api.exchangeCalls == 1)
        #expect(await store.saveCalls == 0)
        coordinator.stop()

        let consumedAPI = HakoAuthTestService(now: clock.now())
        await consumedAPI.setStatuses([.init(status: .consumed, expiresAt: clock.now().addingTimeInterval(300))])
        let consumedStore = HakoAuthTestSessionStore()
        let consumedCoordinator = self.coordinator(api: consumedAPI, store: consumedStore, clock: clock)
        await consumedCoordinator.initialize()
        consumedCoordinator.beginRegistration()
        try await eventually { consumedCoordinator.attemptPhase == .failed }
        #expect(consumedCoordinator.attemptError == .requestConsumed)
        #expect(consumedCoordinator.requiresFreshLogin)
        #expect(await consumedAPI.exchangeCalls == 0)
        consumedCoordinator.stop()
    }

    @Test func saveFailureAndExpiredExchangeTokenAreRevokedWithoutPublishing() async throws {
        let clock = HakoAuthTestClock()
        do {
            let api = HakoAuthTestService(now: clock.now())
            let store = HakoAuthTestSessionStore()
            await store.setSaveFailure(true)
            let coordinator = coordinator(api: api, store: store, clock: clock)
            await coordinator.initialize()
            coordinator.beginRegistration()
            try await eventually { coordinator.attemptPhase == .failed }
            try await eventually { await api.logoutCalls.count == 1 }
            #expect(coordinator.attemptError == .persistence)
            #expect(coordinator.session == nil)
            #expect(await api.logoutCalls.first?.timeout == .seconds(5))
            coordinator.stop()
        }

        do {
            let api = HakoAuthTestService(now: clock.now())
            await api.setExchangeExpiry(clock.now().addingTimeInterval(-1))
            let store = HakoAuthTestSessionStore()
            let coordinator = coordinator(api: api, store: store, clock: clock)
            await coordinator.initialize()
            coordinator.beginLogin(username: "Player_123")
            try await eventually { coordinator.attemptPhase == .failed }
            try await eventually { await api.logoutCalls.count == 1 }
            #expect(coordinator.attemptError == .invalidResponse)
            #expect(coordinator.session == nil)
            #expect(await store.saveCalls == 0)
            coordinator.stop()
        }
    }

    @Test func unreadableStartupStorageShowsWarningAndBlocksAutomaticOverwrite() async throws {
        let clock = HakoAuthTestClock()
        let api = HakoAuthTestService(now: clock.now())
        let store = HakoAuthTestSessionStore()
        await store.setLoadFailure(true)
        let coordinator = coordinator(api: api, store: store, clock: clock)

        await coordinator.initialize()
        coordinator.beginRegistration()

        #expect(!coordinator.isRestoringSession)
        #expect(coordinator.startupError == .storageUnavailable)
        #expect(coordinator.attemptError == .storageUnavailable)
        #expect(await api.registerCalls == 0)
        #expect(await store.saveCalls == 0)
        #expect(await store.clearCalls == 0)
        coordinator.stop()
    }

    @Test func startupRestoresImmediatelyAndVerificationRenewsCachedProfileInBackground() async throws {
        let clock = HakoAuthTestClock()
        let old = Self.session(token: "cached-token", username: "OldName", expiry: clock.now().addingTimeInterval(100))
        let api = HakoAuthTestService(now: clock.now())
        await api.suspendNextMe()
        let store = HakoAuthTestSessionStore(session: old)
        let coordinator = coordinator(api: api, store: store, clock: clock)

        await coordinator.initialize()
        #expect(!coordinator.isRestoringSession)
        #expect(coordinator.session == old)
        try await eventually { await api.meCalls == 1 }
        await api.resumeMe(.success(.init(
            account: Self.account(username: "NewName"), deviceName: "Renamed Mac",
            sessionExpiresAt: clock.now().addingTimeInterval(86_400)
        )))
        try await eventually { coordinator.sessionStatus == .verified && coordinator.session?.account.username == "NewName" }

        #expect(coordinator.session?.token == "cached-token")
        #expect(coordinator.session?.deviceName == "Renamed Mac")
        #expect(await store.saveCalls == 1)
        #expect(await store.storedSession?.verifiedAt != nil)
        coordinator.stop()
    }

    @Test func transientVerificationRetainsCachedSessionAndUnauthorizedClearsOnlyHako() async throws {
        let clock = HakoAuthTestClock()
        do {
            let cached = Self.session(token: "cached", expiry: clock.now().addingTimeInterval(100))
            let api = HakoAuthTestService(now: clock.now())
            await api.suspendNextMe()
            let store = HakoAuthTestSessionStore(session: cached)
            let coordinator = coordinator(api: api, store: store, clock: clock)
            await coordinator.initialize()
            try await eventually { await api.meCalls == 1 }
            await api.resumeMe(.failure(.network))
            try await eventually { coordinator.verificationError == .network }
            #expect(coordinator.session == cached)
            #expect(coordinator.sessionStatus == .unverified)
            #expect(await store.clearCalls == 0)
            coordinator.stop()
        }

        do {
            let cached = Self.session(token: "revoked", expiry: clock.now().addingTimeInterval(100))
            let api = HakoAuthTestService(now: clock.now())
            await api.setMeError(.unauthorized)
            let store = HakoAuthTestSessionStore(session: cached)
            let coordinator = coordinator(api: api, store: store, clock: clock)
            await coordinator.initialize()
            try await eventually { coordinator.session == nil }
            #expect(coordinator.sessionStatus == .signedOut)
            #expect(await store.clearCalls == 1)
            #expect(await api.logoutCalls.isEmpty)
            coordinator.stop()
        }
    }

    @Test func profileVerificationCoalescesAndThrottlesAndStaleResultCannotUndoLogout() async throws {
        let clock = HakoAuthTestClock()
        let initial = Self.session(token: "cached", expiry: clock.now().addingTimeInterval(100))
        let api = HakoAuthTestService(now: clock.now())
        await api.suspendNextMe()
        let store = HakoAuthTestSessionStore(session: initial)
        let profileCoordinator = coordinator(api: api, store: store, clock: clock, initialSession: initial)

        let first = Task { await profileCoordinator.verifyOnProfileEntry() }
        try await eventually { await api.meCalls == 1 }
        let second = Task { await profileCoordinator.verifyOnProfileEntry() }
        await api.resumeMe(.success(.init(account: initial.account, deviceName: initial.deviceName,
                                          sessionExpiresAt: clock.now().addingTimeInterval(86_400))))
        await first.value
        await second.value
        await profileCoordinator.verifyOnProfileEntry()
        #expect(await api.meCalls == 1)
        clock.advance(seconds: 61)
        await profileCoordinator.verifyOnProfileEntry()
        #expect(await api.meCalls == 2)
        profileCoordinator.stop()

        let staleAPI = HakoAuthTestService(now: clock.now())
        await staleAPI.suspendNextMe()
        let staleStore = HakoAuthTestSessionStore(session: initial)
        let staleCoordinator = coordinator(api: staleAPI, store: staleStore, clock: clock, initialSession: initial)
        let verification = Task { await staleCoordinator.verifyManually() }
        try await eventually { await staleAPI.meCalls == 1 }
        #expect(await staleCoordinator.logout() == .signedOut)
        await staleAPI.resumeMe(.success(.init(account: initial.account, deviceName: initial.deviceName,
                                               sessionExpiresAt: clock.now().addingTimeInterval(86_400))))
        await verification.value
        #expect(staleCoordinator.session == nil)
        #expect(await staleStore.storedSession == nil)
        #expect(await staleStore.saveCalls == 0)
        staleCoordinator.stop()
    }

    @Test func logoutFailurePreservesSessionAndAllowsRetryOrExplicitLocalRemoval() async throws {
        let clock = HakoAuthTestClock()
        let initial = Self.session(token: "cached", expiry: clock.now().addingTimeInterval(100))
        let api = HakoAuthTestService(now: clock.now())
        await api.setLogoutError(.network)
        let store = HakoAuthTestSessionStore(session: initial)
        let firstCoordinator = coordinator(api: api, store: store, clock: clock, initialSession: initial)

        #expect(await firstCoordinator.logout() == .remoteLogoutFailed)
        #expect(firstCoordinator.session == initial)
        #expect(firstCoordinator.sessionStatus == .logoutFailed)
        #expect(await store.clearCalls == 0)
        await api.setLogoutError(.unauthorized)
        #expect(await firstCoordinator.logout() == .signedOut)
        #expect(firstCoordinator.session == nil)
        #expect(await store.clearCalls == 1)
        firstCoordinator.stop()

        let localAPI = HakoAuthTestService(now: clock.now())
        await localAPI.setLogoutError(.network)
        let localStore = HakoAuthTestSessionStore(session: initial)
        let localCoordinator = coordinator(api: localAPI, store: localStore, clock: clock, initialSession: initial)
        #expect(await localCoordinator.logout() == .remoteLogoutFailed)
        #expect(await localCoordinator.clearLocalSessionOnly())
        #expect(localCoordinator.session == nil)
        #expect(await localAPI.logoutCalls.count == 1)
        localCoordinator.stop()
    }

    @Test func logoutSucceedsWhenUnauthorizedVerificationAlreadyClearedSession() async throws {
        let clock = HakoAuthTestClock()
        let cached = Self.session(token: "cached")
        let api = HakoAuthTestService(now: clock.now())
        await api.setMeError(.unauthorized)
        await api.suspendNextLogout()
        let store = HakoAuthTestSessionStore(session: cached)
        await store.suspendNextClear()
        let coordinator = coordinator(api: api, store: store, clock: clock, initialSession: cached)
        let verification = Task { await coordinator.verifyManually() }
        try await eventually { await store.clearCalls == 1 }
        let logout = Task { await coordinator.logout() }
        try await eventually { await api.logoutCalls.count == 1 }
        await store.resumeClear()
        await verification.value
        #expect(coordinator.session == nil)
        await api.resumeLogout()
        #expect(await logout.value == .signedOut)
        #expect(await store.storedSession == nil)
        coordinator.stop()
    }

    @Test func quickLaunchSurvivesProviderRestorationAndLastLogout() {
        var routing = LauncherPresentationState()
        routing.quickLaunchRequested()
        routing.providersChanged(microsoftSession: false, hakoSession: true, restoreVisible: false)
        routing.providersChanged(microsoftSession: false, hakoSession: false, restoreVisible: false)
        #expect(routing.isGuestLauncher)
        #expect(routing.isQuickLaunchActive)
        routing.quickLaunchEnded()
        #expect(!routing.isGuestLauncher)
    }

    @Test func providerRoutingCoversAllProviderCombinationsAndRestore() {
        for (microsoft, hako) in [(false, false), (true, false), (false, true), (true, true)] {
            var routing = LauncherPresentationState()
            routing.providersChanged(microsoftSession: microsoft, hakoSession: hako, restoreVisible: false)
            #expect(routing.hasProviderSession == (microsoft || hako))
            #expect(routing.hasMicrosoftSession == microsoft)
            #expect(routing.hasHakoSession == hako)
            #expect(!routing.isGuestLauncher)
        }
        var restored = LauncherPresentationState()
        restored.providersChanged(microsoftSession: false, hakoSession: false, restoreVisible: true)
        #expect(!restored.hasProviderSession && restored.isGuestLauncher)
        restored.providersChanged(microsoftSession: false, hakoSession: false, restoreVisible: false)
        #expect(!restored.isGuestLauncher)
    }

    @Test func hakoOnlySessionDoesNotCreateMicrosoftIdentityOrMoveGuestPlaytime() throws {
        let container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let api = HakoAuthTestService(now: .now)
        let store = HakoAuthTestSessionStore(session: Self.session(token: "hako-only"))
        let coordinator = HakoAccountCoordinator(api: api, store: store, initiallyRestored: true, initialSession: Self.session(token: "hako-only"))
        var routing = LauncherPresentationState()
        routing.providersChanged(microsoftSession: false, hakoSession: coordinator.session != nil, restoreVisible: false)
        #expect(routing.hasProviderSession)
        #expect(try container.mainContext.fetch(FetchDescriptor<Account>()).isEmpty)
        let playtime = try PlaytimeCoordinator(context: container.mainContext)
        #expect(playtime.totalSeconds(xuid: nil) == 0)
        coordinator.stop()
    }

    private static func account(username: String = "Player_123") -> HakoAccount {
        HakoAccount(id: "11111111-1111-4111-8111-111111111111", username: username, createdAt: Date(timeIntervalSince1970: 1_700_000_000))
    }

    private static func session(token: String, username: String = "Player_123", expiry: Date? = nil) -> HakoSession {
        HakoSession(token: token, account: account(username: username), deviceName: "Test Mac",
                    expiresAt: expiry ?? Date(timeIntervalSince1970: 1_900_000_000), verifiedAt: nil)
    }
}

nonisolated private final class HakoAuthTestClock: @unchecked Sendable {
    private let lock = NSLock()
    private var current = Date(timeIntervalSince1970: 1_800_000_000)
    private var sleeps: [Duration] = []

    var sleepDurations: [Duration] { lock.withLock { sleeps } }
    func now() -> Date { lock.withLock { current } }
    func advance(seconds: TimeInterval) { lock.withLock { current.addTimeInterval(seconds) } }
    func advance(by duration: Duration) {
        let parts = duration.components
        advance(seconds: Double(parts.seconds) + Double(parts.attoseconds) / 1_000_000_000_000_000_000)
        lock.withLock { sleeps.append(duration) }
    }
}

private actor HakoAuthTestService: HakoAuthAPI {
    private let defaultRequestID = UUID(uuidString: "55555555-5555-4555-8555-555555555555")!
    private let defaultAccount = HakoAccount(id: "11111111-1111-4111-8111-111111111111", username: "Player_123", createdAt: Date(timeIntervalSince1970: 1_700_000_000))
    private var requestExpiry: Date
    private var exchangeExpiry: Date?
    private var statuses: [HakoAuthRequestStatus] = [.init(status: .approved, expiresAt: Date(timeIntervalSince1970: 1_900_000_000))]
    private var statusIndex = 0
    private var suspendStatus = false
    private var statusContinuation: CheckedContinuation<HakoAuthRequestStatus, any Error>?
    private var exchangeError: HakoAuthError?
    private var meError: HakoAuthError?
    private var meResult: HakoMeResult
    private var suspendMe = false
    private var meContinuation: CheckedContinuation<Result<HakoMeResult, HakoAuthError>, Never>?
    private var logoutError: HakoAuthError?
    private var suspendLogout = false
    private var logoutContinuation: CheckedContinuation<Void, Never>?
    private(set) var registerCalls = 0
    private(set) var loginUsernames: [String] = []
    private(set) var statusCalls = 0
    private(set) var exchangeCalls = 0
    private(set) var meCalls = 0
    private(set) var logoutCalls: [(token: String, timeout: Duration)] = []

    init(now: Date) {
        requestExpiry = now.addingTimeInterval(300)
        meResult = HakoMeResult(account: defaultAccount, deviceName: "Verified Mac", sessionExpiresAt: now.addingTimeInterval(86_400))
    }

    func register(deviceName: String) async throws -> HakoPendingAuthRequest {
        registerCalls += 1
        return pending()
    }

    func login(username: String, deviceName: String) async throws -> HakoPendingAuthRequest {
        loginUsernames.append(username)
        return pending()
    }

    func requestStatus(_ request: HakoPendingAuthRequest) async throws -> HakoAuthRequestStatus {
        statusCalls += 1
        if suspendStatus {
            suspendStatus = false
            return try await withCheckedThrowingContinuation { statusContinuation = $0 }
        }
        guard !statuses.isEmpty else { return HakoAuthRequestStatus(status: .approved, expiresAt: requestExpiry) }
        let status = statuses[min(statusIndex, statuses.count - 1)]
        statusIndex += 1
        return status
    }

    func exchange(_ request: HakoPendingAuthRequest) async throws -> HakoExchangeResult {
        exchangeCalls += 1
        if let exchangeError { throw exchangeError }
        return HakoExchangeResult(token: "session-token", tokenExpiresAt: exchangeExpiry ?? requestExpiry.addingTimeInterval(86_400), account: defaultAccount)
    }

    func me(token: String) async throws -> HakoMeResult {
        meCalls += 1
        if suspendMe {
            suspendMe = false
            let result = await withCheckedContinuation { meContinuation = $0 }
            return try result.get()
        }
        if let meError { throw meError }
        return meResult
    }

    func logout(token: String, timeout: Duration) async throws {
        logoutCalls.append((token, timeout))
        if suspendLogout {
            suspendLogout = false
            await withCheckedContinuation { logoutContinuation = $0 }
        }
        if let logoutError { throw logoutError }
    }

    func setStatuses(_ statuses: [HakoAuthRequestStatus]) { self.statuses = statuses; statusIndex = 0 }
    func suspendNextStatus() { suspendStatus = true }
    func resumeStatus(_ status: HakoAuthRequestStatus) { statusContinuation?.resume(returning: status); statusContinuation = nil }
    func setExchangeError(_ error: HakoAuthError?) { exchangeError = error }
    func setExchangeExpiry(_ date: Date) { exchangeExpiry = date }
    func setMeError(_ error: HakoAuthError?) { meError = error }
    func setMeResult(_ result: HakoMeResult) { meResult = result }
    func suspendNextMe() { suspendMe = true }
    func resumeMe(_ result: Result<HakoMeResult, HakoAuthError>) { meContinuation?.resume(returning: result); meContinuation = nil }
    func suspendNextLogout() { suspendLogout = true }
    func resumeLogout() { logoutContinuation?.resume(); logoutContinuation = nil }
    func setLogoutError(_ error: HakoAuthError?) { logoutError = error }

    private func pending() -> HakoPendingAuthRequest {
        HakoPendingAuthRequest(requestID: defaultRequestID, exchangeSecret: "exchange-secret", expiresAt: requestExpiry,
                               botLink: URL(string: "https://t.me/HakoBot?start=test"))
    }
}

private actor HakoAuthTestSessionStore: HakoSessionStore {
    private var stored: HakoSession?
    private var loadFailure = false
    private var saveFailure = false
    private var clearFailure = false
    private var suspendSave = false
    private var suspendClear = false
    private var clearContinuation: CheckedContinuation<Void, Never>?
    private var saveContinuation: CheckedContinuation<Void, Never>?
    private(set) var saveCalls = 0
    private(set) var clearCalls = 0

    init(session: HakoSession? = nil) { stored = session }

    var storedSession: HakoSession? { stored }

    func load() async throws -> HakoSession? {
        if loadFailure { throw HakoSessionStoreError() }
        return stored
    }

    func save(_ session: HakoSession) async throws {
        saveCalls += 1
        if suspendSave {
            suspendSave = false
            await withCheckedContinuation { saveContinuation = $0 }
        }
        if saveFailure { throw HakoSessionStoreError() }
        stored = session
    }

    func clear(ifMatching token: String) async throws -> Bool {
        clearCalls += 1
        if suspendClear {
            suspendClear = false
            await withCheckedContinuation { clearContinuation = $0 }
        }
        if clearFailure { throw HakoSessionStoreError() }
        guard let stored else { return true }
        guard stored.token == token else { return false }
        self.stored = nil
        return true
    }

    func setLoadFailure(_ value: Bool) { loadFailure = value }
    func setSaveFailure(_ value: Bool) { saveFailure = value }
    func setClearFailure(_ value: Bool) { clearFailure = value }
    func suspendNextClear() { suspendClear = true }
    func resumeClear() { clearContinuation?.resume(); clearContinuation = nil }
    func suspendNextSave() { suspendSave = true }
    func resumeSave() { saveContinuation?.resume(); saveContinuation = nil }
}

nonisolated private final class HakoAuthTestKeychainBackend: HakoSessionKeychainBackend, @unchecked Sendable {
    private let lock = NSLock()
    private var data: Data?
    private var readFailure = false
    private(set) var addCount = 0
    private(set) var updateCount = 0
    private(set) var deleteCount = 0

    func copySessionData() throws -> Data? {
        try lock.withLock {
            if readFailure { throw HakoSessionStoreError() }
            return data
        }
    }

    func updateOrAddSessionData(_ data: Data) throws {
        lock.withLock {
            if self.data == nil { addCount += 1 } else { updateCount += 1 }
            self.data = data
        }
    }

    func deleteSessionData() throws { lock.withLock { deleteCount += 1; data = nil } }
    func setReadFailure(_ value: Bool) { lock.withLock { readFailure = value } }
}

nonisolated private final class HakoAuthTestURLProtocol: URLProtocol, @unchecked Sendable {
    struct StubResponse {
        let data: Data
        var statusCode = 200
        var headers: [String: String] = ["Content-Type": "application/json"]
        var hold = false

        static func json(_ object: [String: Any]) -> StubResponse {
            StubResponse(data: try! JSONSerialization.data(withJSONObject: object))
        }
        static func redirect(_ url: URL) -> StubResponse { StubResponse(data: Data(), statusCode: 302, headers: ["Location": url.absoluteString]) }
        static let held = StubResponse(data: Data(), hold: true)
    }

    private static let lock = NSLock()
    private nonisolated(unsafe) static var routes: [String: StubResponse] = [:]
    private nonisolated(unsafe) static var recorded: [URLRequest] = []
    private nonisolated(unsafe) static var recordedBodies: [Data?] = []
    private static var holding = false
    static var requests: [URLRequest] { lock.withLock { recorded } }
    static var bodies: [Data?] { lock.withLock { recordedBodies } }

    static func prepare(_ routes: [String: StubResponse]) { lock.withLock { self.routes = routes; recorded = []; recordedBodies = []; holding = false } }
    static func prepareHoldingRequest() { lock.withLock { routes = [:]; recorded = []; recordedBodies = []; holding = true } }
    static func session() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [HakoAuthTestURLProtocol.self]
        return URLSession(configuration: configuration)
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let route = Self.lock.withLock { () -> (StubResponse?, Bool) in
            Self.recorded.append(request)
            Self.recordedBodies.append(Self.body(of: request))
            return (Self.routes[Self.key(request.url!)], Self.holding)
        }
        if route.1 || route.0?.hold == true { return }
        let stub = route.0 ?? StubResponse(data: Data(), statusCode: 404)
        let response = HTTPURLResponse(url: request.url!, statusCode: stub.statusCode, httpVersion: nil, headerFields: stub.headers)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: stub.data)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}

    private static func key(_ url: URL) -> String { "\(url.host ?? "")\(url.path)" }

    private static func body(of request: URLRequest) -> Data? {
        if let body = request.httpBody { return body }
        guard let stream = request.httpBodyStream else { return nil }
        stream.open()
        defer { stream.close() }
        var result = Data()
        var buffer = [UInt8](repeating: 0, count: 4096)
        while stream.hasBytesAvailable {
            let count = stream.read(&buffer, maxLength: buffer.count)
            if count <= 0 { break }
            result.append(buffer, count: count)
        }
        return result.isEmpty ? nil : result
    }
}

private extension HakoSession {
    func withDeviceName(_ value: String) -> HakoSession {
        HakoSession(token: token, account: account, deviceName: value, expiresAt: expiresAt, verifiedAt: verifiedAt)
    }
}

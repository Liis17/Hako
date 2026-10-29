import Foundation
import Observation

@MainActor @Observable final class HakoAccountCoordinator {
    private static let pollInterval: Duration = .seconds(2)
    private static let verificationThrottle: TimeInterval = 60
    private static let verificationInterval: Duration = .seconds(24 * 60 * 60)

    private(set) var session: HakoSession?
    private(set) var sessionStatus: HakoSessionStatus = .signedOut
    private(set) var isRestoringSession = true
    private(set) var startupError: HakoAuthError?
    private(set) var verificationError: HakoAuthError?
    private(set) var attemptPhase: HakoAttemptPhase = .idle
    private(set) var authorizationMode: HakoAuthorizationMode = .login
    private(set) var attemptError: HakoAuthError?
    private(set) var botLink: URL?
    private(set) var pendingExpiresAt: Date?
    private(set) var requiresFreshLogin = false
    private(set) var isLoggingOut = false

    @ObservationIgnored private let api: any HakoAuthAPI
    @ObservationIgnored private let store: any HakoSessionStore
    @ObservationIgnored private let now: @Sendable () -> Date
    @ObservationIgnored private let sleep: @Sendable (Duration) async throws -> Void
    @ObservationIgnored private var storageWasReadable = false
    @ObservationIgnored private var activeAttempt: Task<Void, Never>?
    @ObservationIgnored private var attemptGeneration: UInt64 = 0
    @ObservationIgnored private var activeAttemptID: UUID?
    @ObservationIgnored private var activeVerification: Task<Void, Never>?
    @ObservationIgnored private var verificationGeneration: UInt64 = 0
    @ObservationIgnored private var activeVerificationID: UUID?
    @ObservationIgnored private var sessionGeneration: UInt64 = 0
    @ObservationIgnored private var lastVerificationAttemptAt: Date?
    @ObservationIgnored private var periodicVerification: Task<Void, Never>?
    @ObservationIgnored private var logoutInProgress = false

    init(
        api: any HakoAuthAPI,
        store: any HakoSessionStore,
        now: @escaping @Sendable () -> Date = { .now },
        sleep: @escaping @Sendable (Duration) async throws -> Void = { try await Task.sleep(for: $0) },
        initiallyRestored: Bool = false,
        initialSession: HakoSession? = nil
    ) {
        self.api = api
        self.store = store
        self.now = now
        self.sleep = sleep
        if initiallyRestored {
            self.session = initialSession
            self.sessionStatus = initialSession.map { $0.expiresAt <= now() ? .expired : .unverified } ?? .signedOut
            self.isRestoringSession = false
            self.storageWasReadable = true
        }
    }

    var hasProviderSession: Bool { session != nil }
    var canStartAuthentication: Bool { !isRestoringSession && storageWasReadable && !logoutInProgress }
    var pendingSecondsRemaining: Int {
        guard let pendingExpiresAt else { return 0 }
        return max(0, Int(ceil(pendingExpiresAt.timeIntervalSince(now()))))
    }

    func initialize() async {
        guard isRestoringSession else { return }
        do {
            let stored = try await store.load()
            storageWasReadable = true
            startupError = nil
            setSession(stored)
            sessionStatus = stored.map { $0.expiresAt <= now() ? .expired : .unverified } ?? .signedOut
        } catch {
            storageWasReadable = false
            startupError = .storageUnavailable
            sessionStatus = .signedOut
        }
        isRestoringSession = false
        if session != nil {
            startPeriodicVerification()
            Task { [weak self] in await self?.verifyOnStartup() }
        }
    }

    func beginLogin(username: String) {
        do {
            let normalized = try HakoUsername.normalize(username)
            beginAttempt(mode: .login, username: normalized)
        } catch let error as HakoAuthError {
            failToStart(error)
        } catch {
            failToStart(.invalidUsername)
        }
    }

    func beginRegistration() {
        beginAttempt(mode: .register, username: nil)
    }

    func cancelAttempt() {
        invalidateAttempt()
        clearPendingRequest()
        attemptError = nil
        requiresFreshLogin = false
        attemptPhase = .idle
    }

    func stop() {
        cancelAttempt()
        cancelVerification()
        periodicVerification?.cancel()
        periodicVerification = nil
    }

    func verifyOnStartup() async {
        await verify(respectProfileThrottle: false)
    }

    func verifyOnProfileEntry() async {
        await verify(respectProfileThrottle: true)
    }

    func verifyManually() async {
        await verify(respectProfileThrottle: false)
    }

    func logout() async -> HakoLogoutResult {
        guard let expected = session else { return .signedOut }
        guard !logoutInProgress else { return .remoteLogoutFailed }
        logoutInProgress = true
        isLoggingOut = true
        let expectedGeneration = sessionGeneration
        invalidateAttempt()
        clearPendingRequest()
        cancelVerification()
        attemptError = nil
        attemptPhase = .idle
        defer {
            logoutInProgress = false
            isLoggingOut = false
            if session != nil { startPeriodicVerification() }
        }

        do {
            do {
                try await api.logout(token: expected.token, timeout: .seconds(20))
            } catch HakoAuthError.unauthorized {
                // An expired or revoked token is already signed out remotely.
            }
            let cleared = await clearLocalSession(expected: expected, generation: expectedGeneration)
            if !cleared && session != nil {
                setLogoutFailure(.persistence, expected: expected, generation: expectedGeneration)
                return .localClearFailed
            }
            return .signedOut
        } catch is CancellationError {
            return .remoteLogoutFailed
        } catch let error as HakoAuthError {
            setLogoutFailure(error, expected: expected, generation: expectedGeneration)
            return .remoteLogoutFailed
        } catch {
            setLogoutFailure(.network, expected: expected, generation: expectedGeneration)
            return .remoteLogoutFailed
        }
    }

    func clearLocalSessionOnly() async -> Bool {
        guard let expected = session, !logoutInProgress else { return session == nil }
        logoutInProgress = true
        isLoggingOut = true
        let expectedGeneration = sessionGeneration
        invalidateAttempt()
        clearPendingRequest()
        cancelVerification()
        attemptPhase = .idle
        attemptError = nil
        defer {
            logoutInProgress = false
            isLoggingOut = false
            if session != nil { startPeriodicVerification() }
        }
        let cleared = await clearLocalSession(expected: expected, generation: expectedGeneration)
        if !cleared {
            sessionStatus = .logoutFailed
            verificationError = .persistence
        }
        return cleared
    }

    private func beginAttempt(mode: HakoAuthorizationMode, username: String?) {
        guard !isRestoringSession else { failToStart(.serviceUnavailable); return }
        guard storageWasReadable else { failToStart(.storageUnavailable); return }
        guard !logoutInProgress else { failToStart(.serviceUnavailable); return }
        invalidateAttempt()
        cancelVerification()
        attemptGeneration &+= 1
        let generation = attemptGeneration
        let id = UUID()
        activeAttemptID = id
        authorizationMode = mode
        requiresFreshLogin = false
        attemptError = nil
        clearPendingRequest()
        attemptPhase = .requesting
        let task = Task { [weak self] in
            guard let self else { return }
            await self.runAttempt(generation: generation, id: id, mode: mode, username: username)
        }
        activeAttempt = task
    }

    private func runAttempt(generation: UInt64, id: UUID, mode: HakoAuthorizationMode, username: String?) async {
        var exchangeStarted = false
        var issuedToken: String?
        do {
            let deviceName = HakoDeviceName.current()
            let request: HakoPendingAuthRequest
            switch mode {
            case .register:
                request = try await api.register(deviceName: deviceName)
            case .login:
                request = try await api.login(username: username ?? "", deviceName: deviceName)
            }
            guard isCurrentAttempt(generation, id) else { return }
            botLink = request.botLink
            pendingExpiresAt = request.expiresAt
            attemptPhase = .pending

            var effectiveExpiry = request.expiresAt
            while true {
                try Task.checkCancellation()
                let remaining = effectiveExpiry.timeIntervalSince(now())
                guard remaining > 0 else { throw HakoAuthError.requestExpired }
                let sleepNanoseconds = Int64(min(2, remaining) * 1_000_000_000)
                try await sleep(.nanoseconds(sleepNanoseconds))
                try Task.checkCancellation()
                guard isCurrentAttempt(generation, id) else { return }
                guard now() < effectiveExpiry else { throw HakoAuthError.requestExpired }

                let status = try await api.requestStatus(request)
                guard isCurrentAttempt(generation, id) else { return }
                effectiveExpiry = min(effectiveExpiry, status.expiresAt)
                pendingExpiresAt = effectiveExpiry
                guard now() < effectiveExpiry else { throw HakoAuthError.requestExpired }
                switch status.status {
                case .pending:
                    continue
                case .approved:
                    break
                case .denied:
                    throw HakoAuthError.denied
                case .expired:
                    throw HakoAuthError.requestExpired
                case .accountExists:
                    throw HakoAuthError.accountExists
                case .consumed:
                    throw HakoAuthError.requestConsumed
                }
                break
            }

            guard isCurrentAttempt(generation, id) else { return }
            attemptPhase = .exchanging
            exchangeStarted = true
            let exchange = try await api.exchange(request)
            issuedToken = exchange.token
            guard isCurrentAttempt(generation, id) else {
                await revokeStaleToken(exchange.token)
                return
            }
            guard exchange.tokenExpiresAt > now() else {
                await revokeStaleToken(exchange.token)
                issuedToken = nil
                throw HakoAuthError.invalidResponse
            }

            let session = HakoSession(
                token: exchange.token,
                account: exchange.account,
                deviceName: deviceName,
                expiresAt: exchange.tokenExpiresAt,
                verifiedAt: now()
            )
            do {
                try await store.save(session)
            } catch {
                await revokeStaleToken(exchange.token)
                issuedToken = nil
                throw HakoAuthError.persistence
            }
            guard isCurrentAttempt(generation, id) else {
                await clearStoredSession(ifMatching: exchange.token)
                await revokeStaleToken(exchange.token)
                return
            }

            // Publish only after Keychain accepted the complete session.
            setSession(session)
            sessionStatus = .verified
            verificationError = nil
            startupError = nil
            lastVerificationAttemptAt = now()
            clearPendingRequest()
            attemptError = nil
            requiresFreshLogin = false
            attemptPhase = .idle
            issuedToken = nil
            startPeriodicVerification()
        } catch is CancellationError {
            if let issuedToken { await revokeStaleToken(issuedToken) }
        } catch let error as HakoAuthError {
            if let issuedToken { await revokeStaleToken(issuedToken) }
            setAttemptFailure(
                error,
                generation: generation,
                id: id,
                requiresFreshLogin: exchangeStarted || error == .requestConsumed
            )
        } catch {
            if let issuedToken { await revokeStaleToken(issuedToken) }
            setAttemptFailure(.network, generation: generation, id: id, requiresFreshLogin: exchangeStarted)
        }
        if activeAttemptID == id {
            activeAttemptID = nil
            activeAttempt = nil
        }
    }

    private func failToStart(_ error: HakoAuthError) {
        authorizationMode = .login
        attemptError = error
        attemptPhase = .failed
        requiresFreshLogin = false
    }

    private func setAttemptFailure(_ error: HakoAuthError, generation: UInt64, id: UUID, requiresFreshLogin: Bool) {
        guard isCurrentAttempt(generation, id) else { return }
        clearPendingRequest(preservingExpiry: false)
        self.requiresFreshLogin = requiresFreshLogin
        attemptError = error
        attemptPhase = .failed
    }

    private func invalidateAttempt() {
        activeAttempt?.cancel()
        activeAttempt = nil
        activeAttemptID = nil
        attemptGeneration &+= 1
    }

    private func isCurrentAttempt(_ generation: UInt64, _ id: UUID) -> Bool {
        generation == attemptGeneration && activeAttemptID == id && !Task.isCancelled && !logoutInProgress
    }

    private func verify(respectProfileThrottle: Bool) async {
        guard !logoutInProgress, let expected = session else { return }
        if let activeVerification {
            await activeVerification.value
            return
        }
        let timestamp = now()
        if respectProfileThrottle,
           let lastVerificationAttemptAt,
           timestamp.timeIntervalSince(lastVerificationAttemptAt) < Self.verificationThrottle {
            return
        }

        verificationGeneration &+= 1
        let generation = verificationGeneration
        let expectedSessionGeneration = sessionGeneration
        let id = UUID()
        activeVerificationID = id
        lastVerificationAttemptAt = timestamp
        verificationError = nil
        sessionStatus = .verifying
        let task = Task { [weak self] in
            guard let self else { return }
            await self.runVerification(
                expected: expected,
                sessionGeneration: expectedSessionGeneration,
                verificationGeneration: generation,
                id: id
            )
        }
        activeVerification = task
        await task.value
    }

    private func runVerification(
        expected: HakoSession,
        sessionGeneration expectedSessionGeneration: UInt64,
        verificationGeneration expectedVerificationGeneration: UInt64,
        id: UUID
    ) async {
        do {
            let result = try await api.me(token: expected.token)
            guard isCurrentVerification(expected, expectedSessionGeneration, expectedVerificationGeneration, id) else { return }
            guard result.sessionExpiresAt > now() else { throw HakoAuthError.invalidResponse }
            let renewed = HakoSession(
                token: expected.token,
                account: result.account,
                deviceName: result.deviceName,
                expiresAt: result.sessionExpiresAt,
                verifiedAt: now()
            )
            do {
                try await store.save(renewed)
            } catch {
                throw HakoAuthError.persistence
            }
            guard isCurrentVerification(expected, expectedSessionGeneration, expectedVerificationGeneration, id) else { return }
            setSession(renewed)
            sessionStatus = .verified
            verificationError = nil
        } catch is CancellationError {
            // A logout or a newer provider transition owns the resulting state.
        } catch HakoAuthError.unauthorized {
            guard isCurrentVerification(expected, expectedSessionGeneration, expectedVerificationGeneration, id) else { return }
            let cleared = await clearLocalSession(expected: expected, generation: expectedSessionGeneration)
            if !cleared, isCurrentVerification(expected, expectedSessionGeneration, expectedVerificationGeneration, id) {
                setVerificationFailure(.persistence, for: expected)
            }
        } catch let error as HakoAuthError {
            setVerificationFailure(error, expected: expected, sessionGeneration: expectedSessionGeneration,
                                   verificationGeneration: expectedVerificationGeneration, id: id)
        } catch {
            setVerificationFailure(.network, expected: expected, sessionGeneration: expectedSessionGeneration,
                                   verificationGeneration: expectedVerificationGeneration, id: id)
        }
        if activeVerificationID == id {
            activeVerificationID = nil
            activeVerification = nil
        }
    }

    private func isCurrentVerification(_ expected: HakoSession, _ expectedSessionGeneration: UInt64,
                                       _ expectedVerificationGeneration: UInt64, _ id: UUID) -> Bool {
        !logoutInProgress && session == expected && sessionGeneration == expectedSessionGeneration &&
            verificationGeneration == expectedVerificationGeneration && activeVerificationID == id && !Task.isCancelled
    }

    private func setVerificationFailure(_ error: HakoAuthError, expected: HakoSession,
                                        sessionGeneration expectedSessionGeneration: UInt64,
                                        verificationGeneration expectedVerificationGeneration: UInt64, id: UUID) {
        guard isCurrentVerification(expected, expectedSessionGeneration, expectedVerificationGeneration, id) else { return }
        setVerificationFailure(error, for: expected)
    }

    private func setVerificationFailure(_ error: HakoAuthError, for expected: HakoSession) {
        verificationError = error
        sessionStatus = expected.expiresAt <= now() ? .expired : .unverified
    }

    private func clearLocalSession(expected: HakoSession, generation: UInt64) async -> Bool {
        guard session == expected, sessionGeneration == generation else { return false }
        do {
            let cleared = try await store.clear(ifMatching: expected.token)
            guard cleared, session == expected, sessionGeneration == generation else { return false }
            setSession(nil)
            sessionStatus = .signedOut
            verificationError = nil
            return true
        } catch {
            verificationError = .persistence
            return false
        }
    }

    private func setLogoutFailure(_ error: HakoAuthError, expected: HakoSession, generation: UInt64) {
        guard session == expected, sessionGeneration == generation else { return }
        sessionStatus = .logoutFailed
        verificationError = error
    }

    private func clearStoredSession(ifMatching token: String) async {
        _ = try? await store.clear(ifMatching: token)
    }

    private func revokeStaleToken(_ token: String) async {
        let api = self.api
        await Task.detached {
            try? await api.logout(token: token, timeout: .seconds(5))
        }.value
    }

    private func setSession(_ value: HakoSession?) {
        guard session != value else { return }
        session = value
        sessionGeneration &+= 1
    }

    private func clearPendingRequest(preservingExpiry: Bool = false) {
        botLink = nil
        if !preservingExpiry { pendingExpiresAt = nil }
    }

    private func cancelVerification() {
        activeVerification?.cancel()
        activeVerification = nil
        activeVerificationID = nil
        verificationGeneration &+= 1
        periodicVerification?.cancel()
        periodicVerification = nil
    }

    private func startPeriodicVerification() {
        periodicVerification?.cancel()
        guard session != nil else { return }
        periodicVerification = Task { [weak self] in
            while !Task.isCancelled {
                do { try await self?.sleep(Self.verificationInterval) }
                catch { return }
                guard let self, !Task.isCancelled, self.session != nil else { return }
                await self.verify(respectProfileThrottle: false)
            }
        }
    }
}

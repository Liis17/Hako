import Foundation
import Observation
import SwiftData

@MainActor @Observable final class PlaytimeCoordinator {
    // Callback завершения игры должен сохранять рабочее хранилище вместе с контекстом.
    @ObservationIgnored private let container: ModelContainer
    @ObservationIgnored private let context: ModelContext
    @ObservationIgnored private let persist: () throws -> Void
    @ObservationIgnored private let saveAcknowledgement: (ModelContext) throws -> Void
    @ObservationIgnored private let api: any HakoPlaytimeAPI
    @ObservationIgnored private let now: () -> Date
    @ObservationIgnored private let helper: PlaytimeHelperClient
    @ObservationIgnored private let retryDelays: [Duration]
    @ObservationIgnored private var polling: Task<Void, Never>?
    @ObservationIgnored private var delivery: Task<Void, Never>?
    @ObservationIgnored private var deliveryGeneration: UInt64 = 0
    @ObservationIgnored private var currentHakoSession: HakoSession?
    let journalDirectory: URL
    private(set) var errorMessage: String?
    private(set) var hakoSyncErrorMessage: String?
    private(set) var hakoServerTotalsMS: [String: Int64] = [:]
    private var players: [String: PlayerPlaytime]
    private var instances: [String: InstancePlaytime]
    private var sessions: [UUID: PlaytimeSession]
    private var submissions: [UUID: HakoPlaytimeSubmission]

    init(
        context: ModelContext,
        journalDirectory: URL = PlaytimeJournal.directory(),
        api: any HakoPlaytimeAPI = HakoPlaytimeAPIClient(),
        helper: PlaytimeHelperClient = PlaytimeHelperClient(),
        retryDelays: [Duration] = [.seconds(1), .seconds(2), .seconds(4), .seconds(8), .seconds(16), .seconds(32), .seconds(60), .seconds(120), .seconds(300)],
        now: @escaping () -> Date = { .now },
        persist: (() throws -> Void)? = nil,
        saveAcknowledgement: ((ModelContext) throws -> Void)? = nil
    ) throws {
        container = context.container
        self.context = context
        self.persist = persist ?? { try context.save() }
        self.saveAcknowledgement = saveAcknowledgement ?? { try $0.save() }
        self.api = api
        self.helper = helper
        self.retryDelays = retryDelays.isEmpty ? [.seconds(300)] : retryDelays
        self.now = now
        self.journalDirectory = journalDirectory
        players = Dictionary(uniqueKeysWithValues: try context.fetch(FetchDescriptor<PlayerPlaytime>()).map { ($0.ownerKey, $0) })
        instances = Dictionary(uniqueKeysWithValues: try context.fetch(FetchDescriptor<InstancePlaytime>()).map { ($0.key, $0) })
        sessions = Dictionary(uniqueKeysWithValues: try context.fetch(FetchDescriptor<PlaytimeSession>()).map { ($0.id, $0) })
        submissions = Dictionary(uniqueKeysWithValues: try context.fetch(FetchDescriptor<HakoPlaytimeSubmission>()).map { ($0.sessionID, $0) })
        for submission in submissions.values {
            guard UUID(uuidString: submission.hakoOwnerID) != nil,
                  submission.durationMS >= 0,
                  (try? submission.localAllocations()) != nil else {
                throw InstanceFileError.message(String(appLocalized: "Не удалось прочитать очередь игрового времени Hako."))
            }
        }
    }

    deinit {
        polling?.cancel()
        delivery?.cancel()
    }

    func start() {
        guard polling == nil else { return }
        refresh()
        polling = Task { [weak self] in
            while !Task.isCancelled {
                do { try await Task.sleep(for: .seconds(PlaytimeJournal.checkpointInterval)) }
                catch { return }
                self?.refresh()
            }
        }
    }

    func refresh() {
        do {
            try reconcile()
            errorMessage = nil
            scheduleDelivery()
        } catch {
            errorMessage = String(appLocalized: "Не удалось сохранить игровое время: \(error.localizedDescription)")
        }
    }

    /// Applies an authenticated Hako profile. Session and queue ownership are always tied to the Hako account UUID.
    func updateHakoSession(_ session: HakoSession?) async {
        if currentHakoSession != session { cancelDelivery() }
        let previousOwnerID = Self.canonicalOwnerID(currentHakoSession?.account.id)
        let newOwnerID = Self.canonicalOwnerID(session?.account.id)
        currentHakoSession = session

        if previousOwnerID != newOwnerID {
            hakoSyncErrorMessage = nil
            do { try await rotateActiveSessions(fromLocalOwnerKey: nil, toLocalOwnerKey: nil, hakoOwnerID: newOwnerID) }
            catch {
                hakoSyncErrorMessage = String(appLocalized: "Не удалось разделить игровую сессию: \(error.localizedDescription)")
            }
        }

        guard let session else {
            if let previousOwnerID { hakoServerTotalsMS[previousOwnerID] = nil }
            return
        }

        do {
            try reconcile()
            if session.expiresAt > now(), let ownerID = newOwnerID {
                try reserveUnsubmittedHistory(for: ownerID)
                do { try await refreshServerTotal(for: session, ownerID: ownerID) }
                catch {
                    hakoSyncErrorMessage = String(appLocalized: "Не удалось синхронизировать время с Hako: \(error.localizedDescription)")
                }
                scheduleDelivery()
            }
        } catch {
            hakoSyncErrorMessage = String(appLocalized: "Не удалось синхронизировать время с Hako: \(error.localizedDescription)")
        }
    }

    func retryHakoSync() async {
        guard let session = currentHakoSession,
              session.expiresAt > now(),
              let ownerID = Self.canonicalOwnerID(session.account.id) else { return }
        cancelDelivery()
        do {
            try reconcile()
            try reserveUnsubmittedHistory(for: ownerID)
            do { try await refreshServerTotal(for: session, ownerID: ownerID) }
            catch {
                hakoSyncErrorMessage = String(appLocalized: "Не удалось синхронизировать время с Hako: \(error.localizedDescription)")
            }
            scheduleDelivery()
        } catch {
            hakoSyncErrorMessage = String(appLocalized: "Не удалось синхронизировать время с Hako: \(error.localizedDescription)")
        }
    }

    func reconcile() throws {
        guard FileManager.default.fileExists(atPath: journalDirectory.path) else { return }
        let files = try FileManager.default.contentsOfDirectory(at: journalDirectory, includingPropertiesForKeys: nil)
        var failure: Error?
        for url in files where url.pathExtension == "json" {
            do {
                var journal = try PlaytimeJournal.load(from: url)
                guard sessions[journal.sessionID] != nil || journal.isFinished else { continue }
                if !journal.isFinished, !journal.process.isRunning, journal.helper?.isRunning != true {
                    // После выключения Mac или потери помощника известна только последняя контрольная точка.
                    guard let lock = try PlaytimeJournalLock.acquire(sessionID: journal.sessionID, directory: journalDirectory) else { continue }
                    try withExtendedLifetime(lock) {
                        journal = try PlaytimeJournal.load(from: url)
                        journal.isFinished = true
                        try journal.save(in: journalDirectory)
                    }
                }
                if sessions[journal.sessionID] != nil {
                    try credit(sessionID: journal.sessionID, elapsedSeconds: journal.elapsedSeconds)
                }
                if journal.isFinished {
                    if let session = sessions[journal.sessionID] {
                        try enqueueEndedSession(session, durationSeconds: journal.elapsedSeconds)
                    }
                    try removeSession(journal.sessionID)
                    try FileManager.default.removeItem(at: url)
                    try? FileManager.default.removeItem(at: journal.splitRequestURL(in: journalDirectory))
                    try? FileManager.default.removeItem(at: journalDirectory.appendingPathComponent("\(journal.sessionID.uuidString).lock"))
                }
            } catch { if failure == nil { failure = error } }
        }
        if let failure { throw failure }
    }

    func cancelSession(_ id: UUID) throws {
        if FileManager.default.fileExists(atPath: journalDirectory.appendingPathComponent("\(id.uuidString).json").path) {
            refresh()
            return
        }
        try removeSession(id)
    }

    private func removeSession(_ id: UUID) throws {
        guard let session = sessions[id] else { return }
        let ownerKey = session.ownerKey
        let instanceID = session.instanceID
        let creditedSeconds = session.creditedSeconds
        let hakoOwnerID = session.hakoOwnerID
        context.delete(session)
        do { try persist() }
        catch {
            let restored = PlaytimeSession(instanceID: instanceID, ownerKey: ownerKey, hakoOwnerID: hakoOwnerID)
            restored.id = id
            restored.creditedSeconds = creditedSeconds
            context.insert(restored)
            sessions[id] = restored
            throw error
        }
        sessions[id] = nil
    }

    func beginSession(instanceID: UUID, xuid: String?) throws -> UUID {
        try createSession(
            instanceID: instanceID,
            ownerKey: Self.ownerKey(xuid),
            hakoOwnerID: Self.canonicalOwnerID(currentHakoSession?.account.id)
        )
    }

    private func createSession(instanceID: UUID, ownerKey: String, hakoOwnerID: String?) throws -> UUID {
        let session = PlaytimeSession(instanceID: instanceID, ownerKey: ownerKey, hakoOwnerID: hakoOwnerID)
        context.insert(session)
        do { try persist() }
        catch { context.delete(session); throw error }
        sessions[session.id] = session
        return session.id
    }

    func resumeSession(instanceID: UUID, sessionID: UUID?, xuid: String?) throws -> UUID {
        if let sessionID, sessions[sessionID]?.instanceID == instanceID { return sessionID }
        if let continuation = sessions.values.first(where: {
            $0.instanceID == instanceID && FileManager.default.fileExists(
                atPath: journalDirectory.appendingPathComponent("\($0.id.uuidString).json").path
            )
        }) {
            return continuation.id
        }
        return try beginSession(instanceID: instanceID, xuid: xuid)
    }

    func hakoOwnerID(sessionID: UUID) -> String? { sessions[sessionID]?.hakoOwnerID }

    func totalSeconds(xuid: String?) -> TimeInterval { players[Self.ownerKey(xuid)]?.totalSeconds ?? 0 }

    func instanceSeconds(_ id: UUID, xuid: String?) -> TimeInterval {
        instances[InstancePlaytime.key(ownerKey: Self.ownerKey(xuid), instanceID: id)]?.totalSeconds ?? 0
    }

    func hakoServerSeconds(ownerID: String) -> TimeInterval? {
        Self.canonicalOwnerID(ownerID).flatMap { hakoServerTotalsMS[$0] }.map { TimeInterval($0) / 1000 }
    }

    func hakoPendingSeconds(ownerID: String) -> TimeInterval {
        guard let owner = Self.canonicalOwnerID(ownerID) else { return 0 }
        var pendingMS = submissions.values.filter { $0.hakoOwnerID == owner }.reduce(Int64(0)) {
            Self.saturatingAdd($0, $1.durationMS)
        }
        let queuedSessionIDs = Set(submissions.keys)
        for session in sessions.values where session.hakoOwnerID == owner && !queuedSessionIDs.contains(session.id) {
            pendingMS = Self.saturatingAdd(pendingMS, Self.milliseconds(session.creditedSeconds))
        }
        pendingMS = Self.saturatingAdd(pendingMS, unsubmittedHistoryMilliseconds(excludingActiveOwner: owner))
        return TimeInterval(pendingMS) / 1000
    }

    func credit(sessionID: UUID, elapsedSeconds: TimeInterval) throws {
        guard elapsedSeconds.isFinite, elapsedSeconds >= 0, let session = sessions[sessionID] else {
            throw InstanceFileError.message(String(appLocalized: "Не удалось прочитать игровую сессию для учёта времени."))
        }
        let delta = elapsedSeconds - session.creditedSeconds
        guard delta > 0 else { return }
        let key = InstancePlaytime.key(ownerKey: session.ownerKey, instanceID: session.instanceID)
        let existingPlayer = players[session.ownerKey], existingInstance = instances[key]
        let player = existingPlayer ?? PlayerPlaytime(ownerKey: session.ownerKey)
        let instance = existingInstance ?? InstancePlaytime(ownerKey: session.ownerKey, instanceID: session.instanceID)
        if existingPlayer == nil { context.insert(player) }
        if existingInstance == nil { context.insert(instance) }
        let previousPlayer = player.totalSeconds
        let previousInstance = instance.totalSeconds
        let previousCredit = session.creditedSeconds
        player.totalSeconds += delta
        instance.totalSeconds += delta
        session.creditedSeconds = elapsedSeconds
        do { try persist() }
        catch {
            player.totalSeconds = previousPlayer
            instance.totalSeconds = previousInstance
            session.creditedSeconds = previousCredit
            if existingPlayer == nil { context.delete(player) }
            if existingInstance == nil { context.delete(instance) }
            throw error
        }
        players[session.ownerKey] = player
        instances[key] = instance
    }

    /// Guest totals move to Microsoft, while amounts already reserved for a Hako owner stay with their original local counter.
    func transferGuest(to xuid: String) async throws {
        let owner = Self.ownerKey(xuid)
        try await rotateActiveSessions(
            fromLocalOwnerKey: "guest",
            toLocalOwnerKey: owner,
            hakoOwnerID: Self.canonicalOwnerID(currentHakoSession?.account.id)
        )

        let guest = players["guest"]
        let existingPlayer = players[owner]
        let player = existingPlayer ?? PlayerPlaytime(ownerKey: owner)
        if existingPlayer == nil { context.insert(player) }
        let queuedGuestMS = submissions.values.reduce(Int64(0)) { total, item in
            Self.saturatingAdd(total, (try? item.localAllocations()["guest"]) ?? 0)
        }
        let guestSeconds = guest?.totalSeconds ?? 0
        let movableMS = max(0, Self.milliseconds(guestSeconds) - queuedGuestMS)
        let movableSeconds = TimeInterval(movableMS) / 1000
        let previousPlayer = player.totalSeconds
        let previousGuest = guestSeconds
        let guestInstances = instances.values.filter { $0.ownerKey == "guest" }
        let transfers = guestInstances.map { source in
            let key = InstancePlaytime.key(ownerKey: owner, instanceID: source.instanceID)
            let existing = instances[key]
            let target = existing ?? InstancePlaytime(ownerKey: owner, instanceID: source.instanceID)
            if existing == nil { context.insert(target) }
            return (source: source, target: target, previous: target.totalSeconds, seconds: source.totalSeconds, isNew: existing == nil)
        }
        player.totalSeconds += movableSeconds
        guest?.totalSeconds = max(0, guestSeconds - movableSeconds)
        for transfer in transfers {
            transfer.target.totalSeconds += transfer.seconds
            transfer.source.totalSeconds = 0
        }
        do { try persist() }
        catch {
            player.totalSeconds = previousPlayer
            guest?.totalSeconds = previousGuest
            for transfer in transfers {
                transfer.target.totalSeconds = transfer.previous
                transfer.source.totalSeconds = transfer.seconds
                if transfer.isNew { context.delete(transfer.target) }
            }
            if existingPlayer == nil { context.delete(player) }
            throw error
        }
        players[owner] = player
        for transfer in transfers { instances[transfer.target.key] = transfer.target }
        scheduleDelivery()
    }

    func signOutMicrosoft(xuid: String) async throws {
        try await rotateActiveSessions(
            fromLocalOwnerKey: Self.ownerKey(xuid),
            toLocalOwnerKey: Self.ownerKey(nil),
            hakoOwnerID: Self.canonicalOwnerID(currentHakoSession?.account.id)
        )
    }

    private func rotateActiveSessions(
        fromLocalOwnerKey sourceLocalOwner: String?,
        toLocalOwnerKey targetLocalOwner: String?,
        hakoOwnerID targetHakoOwner: String?
    ) async throws {
        let active = Array(sessions.values)
        for session in active {
            if let sourceLocalOwner, session.ownerKey != sourceLocalOwner { continue }
            let instanceID = session.instanceID
            let desiredLocalOwner = targetLocalOwner ?? session.ownerKey
            guard desiredLocalOwner != session.ownerKey || targetHakoOwner != session.hakoOwnerID else { continue }
            let journalURL = journalDirectory.appendingPathComponent("\(session.id.uuidString).json")
            guard FileManager.default.fileExists(atPath: journalURL.path) else {
                let previousLocalOwner = session.ownerKey
                let previousHakoOwner = session.hakoOwnerID
                session.ownerKey = desiredLocalOwner
                session.hakoOwnerID = targetHakoOwner
                do { try persist() }
                catch {
                    session.ownerKey = previousLocalOwner
                    session.hakoOwnerID = previousHakoOwner
                    throw error
                }
                continue
            }

            let oldJournal = try await finishSegmentForRotation(sessionID: session.id)
            guard let oldJournal else { continue }
            try reconcile()
            guard oldJournal.process.isRunning else { continue }
            let continuationID = try createSession(
                instanceID: instanceID,
                ownerKey: desiredLocalOwner,
                hakoOwnerID: targetHakoOwner
            )
            var continuation = PlaytimeJournal(
                sessionID: continuationID,
                process: oldJournal.process,
                startedUptime: ProcessInfo.processInfo.systemUptime,
                hakoOwnerID: targetHakoOwner
            )
            continuation.checkpoint()
            try continuation.save(in: journalDirectory)
            try await helper.attach(to: continuation, in: journalDirectory)
        }
    }

    private func finishSegmentForRotation(sessionID: UUID) async throws -> PlaytimeJournal? {
        let journalURL = journalDirectory.appendingPathComponent("\(sessionID.uuidString).json")
        guard FileManager.default.fileExists(atPath: journalURL.path) else { return nil }
        var journal = try PlaytimeJournal.load(from: journalURL)
        if journal.isFinished { return journal }
        try Data().write(to: journal.splitRequestURL(in: journalDirectory), options: .atomic)

        for _ in 0..<150 {
            guard FileManager.default.fileExists(atPath: journalURL.path) else { return nil }
            journal = try PlaytimeJournal.load(from: journalURL)
            if journal.isFinished { return journal }

            if !journal.process.isRunning || journal.helper?.isRunning != true {
                if let lock = try PlaytimeJournalLock.acquire(sessionID: sessionID, directory: journalDirectory) {
                    try withExtendedLifetime(lock) {
                        var final = try PlaytimeJournal.load(from: journalURL)
                        if !final.isFinished {
                            final.checkpoint()
                            final.isFinished = true
                            try final.save(in: journalDirectory)
                        }
                    }
                }
            }
            try await Task.sleep(for: .milliseconds(20))
        }
        throw InstanceFileError.message(String(appLocalized: "Не удалось завершить сегмент игрового времени."))
    }

    private func enqueueEndedSession(_ session: PlaytimeSession, durationSeconds: TimeInterval) throws {
        guard let ownerID = Self.canonicalOwnerID(session.hakoOwnerID), !submissions.keys.contains(session.id) else { return }
        let durationMS = Self.milliseconds(durationSeconds)
        guard durationMS > 0 else { return }
        try enqueue(
            sessionID: session.id,
            hakoOwnerID: ownerID,
            durationMS: durationMS,
            localAllocations: [session.ownerKey: durationMS]
        )
    }

    private func reserveUnsubmittedHistory(for ownerID: String) throws {
        let allocations = unsubmittedHistoryAllocations(excludingActiveOwner: ownerID)
        guard !allocations.isEmpty else { return }
        let durationMS = allocations.values.reduce(Int64(0), Self.saturatingAdd)
        guard durationMS > 0 else { return }
        try enqueue(sessionID: UUID(), hakoOwnerID: ownerID, durationMS: durationMS, localAllocations: allocations)
    }

    private func enqueue(
        sessionID: UUID,
        hakoOwnerID: String,
        durationMS: Int64,
        localAllocations: [String: Int64]
    ) throws {
        guard submissions[sessionID] == nil else { return }
        let item = try HakoPlaytimeSubmission(
            sessionID: sessionID,
            hakoOwnerID: hakoOwnerID,
            durationMS: durationMS,
            localAllocations: localAllocations
        )
        context.insert(item)
        do { try persist() }
        catch { context.delete(item); throw error }
        submissions[sessionID] = item
        scheduleDelivery()
    }

    private func refreshServerTotal(for session: HakoSession, ownerID: String) async throws {
        guard session.expiresAt > now() else { return }
        let total = try await api.totalDuration(token: session.token)
        guard total >= 0 else { throw HakoAuthError.invalidResponse }
        hakoServerTotalsMS[ownerID] = total
        hakoSyncErrorMessage = nil
    }

    private func scheduleDelivery() {
        guard delivery == nil,
              let session = currentHakoSession,
              session.expiresAt > now(),
              let ownerID = Self.canonicalOwnerID(session.account.id),
              submissions.values.contains(where: { $0.hakoOwnerID == ownerID }) else { return }
        let generation = deliveryGeneration
        delivery = Task { [weak self] in await self?.drainQueue(generation: generation) }
    }

    private func cancelDelivery() {
        delivery?.cancel()
        delivery = nil
        deliveryGeneration &+= 1
    }

    private func drainQueue(generation: UInt64) async {
        defer { if deliveryGeneration == generation { delivery = nil } }
        var retryIndex = 0
        var didSubmit = false
        while !Task.isCancelled {
            guard deliveryGeneration == generation else { return }
            guard let session = currentHakoSession,
                  session.expiresAt > now(),
                  let ownerID = Self.canonicalOwnerID(session.account.id) else { return }
            guard let item = submissions.values
                .filter({ $0.hakoOwnerID == ownerID })
                .sorted(by: { $0.sessionID.uuidString < $1.sessionID.uuidString })
                .first else {
                if didSubmit {
                    do {
                        hakoServerTotalsMS[ownerID] = try await api.totalDuration(token: session.token)
                        hakoSyncErrorMessage = nil
                    } catch {
                        hakoSyncErrorMessage = String(appLocalized: "Не удалось синхронизировать время с Hako: \(error.localizedDescription)")
                    }
                }
                return
            }
            do {
                let returnedID = try await api.postSession(
                    sessionID: item.sessionID,
                    durationMS: item.durationMS,
                    token: session.token
                )
                guard returnedID == item.sessionID else { throw HakoAuthError.invalidResponse }
                try acknowledge(item)
                didSubmit = true
                retryIndex = 0
                hakoSyncErrorMessage = nil
            } catch is CancellationError {
                return
            } catch {
                hakoSyncErrorMessage = String(appLocalized: "Не удалось синхронизировать время с Hako: \(error.localizedDescription)")
                do { try await Task.sleep(for: retryDelays[min(retryIndex, retryDelays.count - 1)]) }
                catch { return }
                retryIndex = min(retryIndex + 1, retryDelays.count - 1)
            }
        }
    }

    private func acknowledge(_ item: HakoPlaytimeSubmission) throws {
        guard submissions[item.sessionID] === item else { return }
        let allocations = try item.localAllocations()
        let acknowledgementContext = ModelContext(container)
        let sessionID = item.sessionID
        let submissionDescriptor = FetchDescriptor<HakoPlaytimeSubmission>(predicate: #Predicate { $0.sessionID == sessionID })
        guard let persistedItem = try acknowledgementContext.fetch(submissionDescriptor).first else {
            // A parallel idempotent delivery may already have committed this acknowledgement.
            for ownerKey in allocations.keys {
                let playerDescriptor = FetchDescriptor<PlayerPlaytime>(predicate: #Predicate { $0.ownerKey == ownerKey })
                if let latestPlayer = try acknowledgementContext.fetch(playerDescriptor).first {
                    players[ownerKey]?.totalSeconds = latestPlayer.totalSeconds
                }
            }
            context.delete(item)
            submissions[item.sessionID] = nil
            return
        }
        var acknowledgedTotals: [String: TimeInterval] = [:]
        for (ownerKey, amountMS) in allocations where amountMS > 0 {
            let playerDescriptor = FetchDescriptor<PlayerPlaytime>(predicate: #Predicate { $0.ownerKey == ownerKey })
            guard let player = try acknowledgementContext.fetch(playerDescriptor).first else { continue }
            player.totalSeconds = max(0, player.totalSeconds - TimeInterval(amountMS) / 1000)
            acknowledgedTotals[ownerKey] = player.totalSeconds
        }
        acknowledgementContext.delete(persistedItem)
        try saveAcknowledgement(acknowledgementContext)

        // Mirror the committed transaction into the UI context without saving its unrelated edits.
        for (ownerKey, total) in acknowledgedTotals { players[ownerKey]?.totalSeconds = total }
        context.delete(item)
        submissions[item.sessionID] = nil
    }

    private func unsubmittedHistoryMilliseconds(excludingActiveOwner ownerID: String) -> Int64 {
        unsubmittedHistoryAllocations(excludingActiveOwner: ownerID).values.reduce(Int64(0), Self.saturatingAdd)
    }

    private func unsubmittedHistoryAllocations(excludingActiveOwner ownerID: String) -> [String: Int64] {
        var reservedByOwner: [String: Int64] = [:]
        for item in submissions.values {
            guard let allocations = try? item.localAllocations() else { continue }
            for (ownerKey, amount) in allocations {
                reservedByOwner[ownerKey] = Self.saturatingAdd(reservedByOwner[ownerKey, default: 0], max(0, amount))
            }
        }

        var activeByOwner: [String: Int64] = [:]
        let queuedIDs = Set(submissions.keys)
        for session in sessions.values where session.hakoOwnerID == ownerID && !queuedIDs.contains(session.id) {
            activeByOwner[session.ownerKey] = Self.saturatingAdd(
                activeByOwner[session.ownerKey, default: 0], Self.milliseconds(session.creditedSeconds)
            )
        }

        var available: [String: Int64] = [:]
        for (ownerKey, player) in players {
            let total = Self.milliseconds(player.totalSeconds)
            let reserved = reservedByOwner[ownerKey, default: 0]
            let active = activeByOwner[ownerKey, default: 0]
            let amount = max(0, total - min(total, Self.saturatingAdd(reserved, active)))
            if amount > 0 { available[ownerKey] = amount }
        }
        return available
    }

    private static func milliseconds(_ seconds: TimeInterval) -> Int64 {
        guard seconds.isFinite, seconds > 0 else { return 0 }
        let milliseconds = seconds * 1000
        guard milliseconds < Double(Int64.max) else { return Int64.max }
        return Int64(milliseconds.rounded(.down))
    }

    private static func saturatingAdd(_ lhs: Int64, _ rhs: Int64) -> Int64 {
        let (sum, overflow) = lhs.addingReportingOverflow(rhs)
        return overflow ? Int64.max : sum
    }

    private static func canonicalOwnerID(_ id: String?) -> String? {
        guard let id, let uuid = UUID(uuidString: id) else { return nil }
        return uuid.uuidString.lowercased()
    }

    static func ownerKey(_ xuid: String?) -> String { xuid.map { "account:\($0)" } ?? "guest" }
}

import Foundation
import Observation
import SwiftData

@MainActor @Observable final class PlaytimeCoordinator {
    @ObservationIgnored private let context: ModelContext
    @ObservationIgnored private let persist: () throws -> Void
    @ObservationIgnored private var polling: Task<Void, Never>?
    let journalDirectory: URL
    private(set) var errorMessage: String?
    private var players: [String: PlayerPlaytime]
    private var instances: [String: InstancePlaytime]
    private var sessions: [UUID: PlaytimeSession]

    init(context: ModelContext, journalDirectory: URL = PlaytimeJournal.directory(), persist: (() throws -> Void)? = nil) throws {
        self.context = context; self.persist = persist ?? { try context.save() }
        self.journalDirectory = journalDirectory
        players = Dictionary(uniqueKeysWithValues: try context.fetch(FetchDescriptor<PlayerPlaytime>()).map { ($0.ownerKey, $0) })
        instances = Dictionary(uniqueKeysWithValues: try context.fetch(FetchDescriptor<InstancePlaytime>()).map { ($0.key, $0) })
        sessions = Dictionary(uniqueKeysWithValues: try context.fetch(FetchDescriptor<PlaytimeSession>()).map { ($0.id, $0) })
    }

    deinit { polling?.cancel() }

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
        do { try reconcile(); errorMessage = nil }
        catch { errorMessage = "Не удалось сохранить игровое время: \(error.localizedDescription)" }
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
                if sessions[journal.sessionID] != nil { try credit(sessionID: journal.sessionID, elapsedSeconds: journal.elapsedSeconds) }
                if journal.isFinished {
                    try removeSession(journal.sessionID)
                    try FileManager.default.removeItem(at: url)
                    try? FileManager.default.removeItem(at: journalDirectory.appendingPathComponent("\(journal.sessionID.uuidString).lock"))
                }
            } catch { if failure == nil { failure = error } }
        }
        if let failure { throw failure }
    }

    func cancelSession(_ id: UUID) throws {
        if FileManager.default.fileExists(atPath: journalDirectory.appendingPathComponent("\(id.uuidString).json").path) { refresh(); return }
        try removeSession(id)
    }

    private func removeSession(_ id: UUID) throws {
        guard let session = sessions[id] else { return }
        let ownerKey = session.ownerKey, instanceID = session.instanceID, creditedSeconds = session.creditedSeconds
        context.delete(session)
        do { try persist() }
        catch {
            let restored = PlaytimeSession(instanceID: instanceID, ownerKey: ownerKey)
            restored.id = id; restored.creditedSeconds = creditedSeconds
            context.insert(restored); sessions[id] = restored
            throw error
        }
        sessions[id] = nil
    }

    func beginSession(instanceID: UUID, xuid: String?) throws -> UUID {
        let session = PlaytimeSession(instanceID: instanceID, ownerKey: Self.ownerKey(xuid))
        context.insert(session)
        do { try persist() }
        catch { context.delete(session); throw error }
        sessions[session.id] = session
        return session.id
    }

    func resumeSession(instanceID: UUID, sessionID: UUID?, xuid: String?) throws -> UUID {
        if let sessionID, sessions[sessionID]?.instanceID == instanceID { return sessionID }
        return try beginSession(instanceID: instanceID, xuid: xuid)
    }

    func totalSeconds(xuid: String?) -> TimeInterval { players[Self.ownerKey(xuid)]?.totalSeconds ?? 0 }
    func instanceSeconds(_ id: UUID, xuid: String?) -> TimeInterval {
        instances[InstancePlaytime.key(ownerKey: Self.ownerKey(xuid), instanceID: id)]?.totalSeconds ?? 0
    }

    func credit(sessionID: UUID, elapsedSeconds: TimeInterval) throws {
        guard elapsedSeconds.isFinite, elapsedSeconds >= 0, let session = sessions[sessionID] else {
            throw InstanceFileError.message("Не удалось прочитать игровую сессию для учёта времени.")
        }
        let delta = elapsedSeconds - session.creditedSeconds
        guard delta > 0 else { return }
        let key = InstancePlaytime.key(ownerKey: session.ownerKey, instanceID: session.instanceID)
        let existingPlayer = players[session.ownerKey], existingInstance = instances[key]
        let player = existingPlayer ?? PlayerPlaytime(ownerKey: session.ownerKey)
        let instance = existingInstance ?? InstancePlaytime(ownerKey: session.ownerKey, instanceID: session.instanceID)
        if existingPlayer == nil { context.insert(player) }
        if existingInstance == nil { context.insert(instance) }
        let previousPlayer = player.totalSeconds, previousInstance = instance.totalSeconds, previousCredit = session.creditedSeconds
        player.totalSeconds += delta; instance.totalSeconds += delta; session.creditedSeconds = elapsedSeconds
        do { try persist() }
        catch {
            player.totalSeconds = previousPlayer; instance.totalSeconds = previousInstance; session.creditedSeconds = previousCredit
            if existingPlayer == nil { context.delete(player) }
            if existingInstance == nil { context.delete(instance) }
            throw error
        }
        players[session.ownerKey] = player; instances[key] = instance
    }

    func transferGuest(to xuid: String) throws {
        let owner = Self.ownerKey(xuid), guest = players["guest"]
        let existingPlayer = players[owner]
        let player = existingPlayer ?? PlayerPlaytime(ownerKey: owner)
        if existingPlayer == nil { context.insert(player) }
        let previousPlayer = player.totalSeconds, guestSeconds = guest?.totalSeconds ?? 0
        let guestSessions = sessions.values.filter { $0.ownerKey == "guest" }
        let guestInstances = instances.values.filter { $0.ownerKey == "guest" }
        let transfers = guestInstances.map { source in
            let key = InstancePlaytime.key(ownerKey: owner, instanceID: source.instanceID)
            let existing = instances[key]
            let target = existing ?? InstancePlaytime(ownerKey: owner, instanceID: source.instanceID)
            if existing == nil { context.insert(target) }
            return (source: source, target: target, previous: target.totalSeconds, seconds: source.totalSeconds, isNew: existing == nil)
        }
        player.totalSeconds += guestSeconds; guest?.totalSeconds = 0
        for transfer in transfers { transfer.target.totalSeconds += transfer.seconds; transfer.source.totalSeconds = 0 }
        for session in guestSessions { session.ownerKey = owner }
        do { try persist() }
        catch {
            player.totalSeconds = previousPlayer; guest?.totalSeconds = guestSeconds
            for transfer in transfers {
                transfer.target.totalSeconds = transfer.previous; transfer.source.totalSeconds = transfer.seconds
                if transfer.isNew { context.delete(transfer.target) }
            }
            for session in guestSessions { session.ownerKey = "guest" }
            if existingPlayer == nil { context.delete(player) }
            throw error
        }
        players[owner] = player
        for transfer in transfers { instances[transfer.target.key] = transfer.target }
    }

    private static func ownerKey(_ xuid: String?) -> String { xuid.map { "account:\($0)" } ?? "guest" }
}

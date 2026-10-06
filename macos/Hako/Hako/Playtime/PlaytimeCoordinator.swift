import Foundation
import Observation
import SwiftData

@MainActor @Observable final class PlaytimeCoordinator {
    @ObservationIgnored private let context: ModelContext
    @ObservationIgnored private let persist: () throws -> Void
    private var players: [String: PlayerPlaytime]
    private var instances: [String: InstancePlaytime]
    private var sessions: [UUID: PlaytimeSession]

    init(context: ModelContext, persist: (() throws -> Void)? = nil) throws {
        self.context = context; self.persist = persist ?? { try context.save() }
        players = Dictionary(uniqueKeysWithValues: try context.fetch(FetchDescriptor<PlayerPlaytime>()).map { ($0.ownerKey, $0) })
        instances = Dictionary(uniqueKeysWithValues: try context.fetch(FetchDescriptor<InstancePlaytime>()).map { ($0.key, $0) })
        sessions = Dictionary(uniqueKeysWithValues: try context.fetch(FetchDescriptor<PlaytimeSession>()).map { ($0.id, $0) })
    }

    func beginSession(instanceID: UUID, xuid: String?) throws -> UUID {
        let session = PlaytimeSession(instanceID: instanceID, ownerKey: Self.ownerKey(xuid))
        context.insert(session)
        do { try persist() }
        catch { context.delete(session); throw error }
        sessions[session.id] = session
        return session.id
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

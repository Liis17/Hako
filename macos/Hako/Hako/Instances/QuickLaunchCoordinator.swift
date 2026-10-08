import Foundation
import Observation
import SwiftData

nonisolated enum QuickLaunchOutcome: Equatable, Sendable {
    case started, alreadyRunning
}

nonisolated struct QuickLaunchPresentation: Identifiable, Equatable, Sendable {
    let id = UUID()
    let instanceID: UUID
}

@MainActor @Observable final class QuickLaunchCoordinator {
    private let store: InstanceStore
    private let games: GameLaunchCoordinator
    private var presentations: [QuickLaunchPresentation] = []
    @ObservationIgnored private var waiters: [UUID: CheckedContinuation<Void, any Error>] = [:]
    @ObservationIgnored private var tasks: [UUID: Task<QuickLaunchOutcome, any Error>] = [:]
    @ObservationIgnored private var showWindow: (() -> Void)?
    private(set) var preparingIDs: Set<UUID> = []
    var error: String?

    var presentation: QuickLaunchPresentation? { presentations.first }

    init(store: InstanceStore, games: GameLaunchCoordinator) {
        self.store = store; self.games = games
    }

    func registerWindowPresenter(_ presenter: @escaping () -> Void) {
        showWindow = presenter
        if presentation != nil { presenter() }
    }

    func completePresentation(_ requestID: UUID, error: (any Error)? = nil) {
        guard let waiter = waiters.removeValue(forKey: requestID) else { return }
        presentations.removeAll { $0.id == requestID }
        if let error { waiter.resume(throwing: error) }
        else { waiter.resume() }
    }

    func cancelPresentations() {
        for request in presentations { completePresentation(request.id, error: CancellationError()) }
    }

    func launch(instanceID: UUID) async throws -> QuickLaunchOutcome {
        if let task = tasks[instanceID] { return try await task.value }
        error = nil
        preparingIDs.insert(instanceID)
        let task = Task { try await self.performLaunch(instanceID) }
        tasks[instanceID] = task
        defer { tasks[instanceID] = nil; preparingIDs.remove(instanceID) }
        do { return try await task.value }
        catch {
            if !(error is CancellationError) { self.error = error.localizedDescription }
            throw error
        }
    }

    private func instance(_ id: UUID) throws -> GameInstance {
        guard let instance = try store.context.fetch(FetchDescriptor<GameInstance>(predicate: #Predicate { $0.id == id })).first else {
            throw InstanceFileError.message(String(appLocalized: "Сборка больше не существует."))
        }
        return instance
    }

    private func performLaunch(_ id: UUID) async throws -> QuickLaunchOutcome {
        showWindow?()
        games.start()
        _ = try instance(id)
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
            let request = QuickLaunchPresentation(instanceID: id)
            waiters[request.id] = continuation
            presentations.append(request)
            showWindow?()
        }
        var instance = try self.instance(id)
        if games.states[id] == .running { return .alreadyRunning }
        if instance.state != .ready {
            throw InstanceFileError.message(String(appLocalized: "Дождитесь завершения установки сборки."))
        }
        if store.contentBusy.contains(id) {
            throw InstanceFileError.message(String(appLocalized: "Дождитесь завершения операций с файлами сборки."))
        }
        let account = try store.context.fetch(FetchDescriptor<Account>()).first
        if !instance.offlineMode, let account {
            let xuid = account.xuid
            await games.sessions.connect(account)
            guard try store.context.fetch(FetchDescriptor<Account>()).first?.xuid == xuid else {
                throw InstanceFileError.message(String(appLocalized: "Minecraft-сессия изменилась или завершилась. Повторите запуск."))
            }
            if games.sessions.identity(for: account) == nil, case .failed(let message) = games.sessions.statuses[xuid] {
                throw InstanceFileError.message(message)
            }
        }
        instance = try self.instance(id)
        if games.states[id] == .running { return .alreadyRunning }
        try await games.launchAndWait(instance, account: account)
        return .started
    }
}

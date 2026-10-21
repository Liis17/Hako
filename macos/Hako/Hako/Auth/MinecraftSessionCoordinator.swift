import Foundation
import Observation
import SwiftData

enum MinecraftStatus {
    case idle, connecting, failed(String)
}

enum MinecraftSessionError: LocalizedError {
    case unavailable
    case refreshedTokenRejected
    case accountChanged

    var errorDescription: String? {
        switch self {
        case .unavailable:
            String(appLocalized: "Minecraft-сессия недоступна. Повторите подключение аккаунта.")
        case .refreshedTokenRejected:
            String(appLocalized: "Minecraft не принял обновлённую сессию. Подключите аккаунт повторно.")
        case .accountChanged:
            String(appLocalized: "Аккаунт изменился. Откройте редактор персонажа снова.")
        }
    }
}

@MainActor @Observable final class MinecraftSessionCoordinator {
    struct Dependencies {
        var load: (String) -> AccountTokens? = { TokenKeychain.load(for: $0) }
        var save: (AccountTokens, String) throws -> Void = { try TokenKeychain.save($0, for: $1) }
        var refresh: (String) async throws -> MicrosoftToken = { try await MicrosoftAuth.refresh($0) }
        var signIn: (MicrosoftToken) async throws -> MinecraftSession = { try await MicrosoftAuth.signInToMinecraft(with: $0) }
        var now: () -> Date = { .now }
    }

    let appearances: MinecraftAppearanceStore

    private let context: ModelContext
    private let dependencies: Dependencies
    private var identities: [String: MinecraftLaunchIdentity] = [:]
    private var expirations: [String: Date] = [:]
    private var tasks: [String: Task<Void, Never>] = [:]
    private var forcedRefreshTasks: [String: Task<MinecraftLaunchIdentity, Error>] = [:]
    private var generations: [String: UUID] = [:]
    private var refreshGenerations: [String: UUID] = [:]
    // Keep the model so its ID follows SwiftData's first-save remapping. Compare current IDs
    // to also recognize the same saved account read through SwiftUI's separate ModelContext.
    private var accounts: [String: Account] = [:]
    private var sessionEpoch = UUID()
    private(set) var statuses: [String: MinecraftStatus] = [:]
    private(set) var checkedAt = Date.distantPast

    init(context: ModelContext, dependencies: Dependencies? = nil, appearances: MinecraftAppearanceStore? = nil) {
        self.context = context
        self.dependencies = dependencies ?? .init()
        self.appearances = appearances ?? MinecraftAppearanceStore()
    }

    func identity(for account: Account?) -> MinecraftLaunchIdentity? {
        _ = checkedAt
        guard let account, accounts[account.xuid]?.persistentModelID == account.persistentModelID,
              let expiry = expirations[account.xuid], expiry > dependencies.now()
        else { return nil }
        return identities[account.xuid]
    }

    func installFixtureIdentity(_ identity: MinecraftLaunchIdentity, for account: Account, expiration: Date) {
        accounts[account.xuid] = account
        identities[account.xuid] = identity
        expirations[account.xuid] = expiration
        statuses[account.xuid] = .idle
    }

    func connect(_ account: Account, force: Bool = false) async {
        checkedAt = dependencies.now()
        let xuid = account.xuid
        if let previous = accounts[xuid], previous.persistentModelID != account.persistentModelID {
            tasks.removeValue(forKey: xuid)?.cancel()
            forcedRefreshTasks.removeValue(forKey: xuid)?.cancel()
            generations[xuid] = nil; refreshGenerations[xuid] = nil
            identities[xuid] = nil; expirations[xuid] = nil; statuses[xuid] = nil
        }
        accounts[xuid] = account
        let epoch = sessionEpoch

        if let refreshTask = forcedRefreshTasks[xuid] {
            _ = try? await refreshTask.value
        }
        if let task = tasks[xuid] {
            await task.value
            return
        }
        guard !Task.isCancelled, isCurrent(account: account, xuid: xuid, epoch: epoch) else { return }

        let generation = UUID()
        let appearanceRevision = appearances.revision(for: xuid)
        generations[xuid] = generation
        let task = Task { [self] in
            defer {
                if generations[xuid] == generation {
                    tasks[xuid] = nil
                    generations[xuid] = nil
                }
            }
            guard !Task.isCancelled, isCurrent(account: account, xuid: xuid, epoch: epoch, generation: generation) else { return }
            if !force, case .failed = statuses[xuid] { return }
            guard var tokens = dependencies.load(xuid) else {
                identities[xuid] = nil; expirations[xuid] = nil
                statuses[xuid] = .failed(String(appLocalized: "Данные входа недоступны. Войдите в Microsoft повторно."))
                return
            }
            if let token = tokens.minecraftAccessToken, !token.isEmpty, let expiry = tokens.minecraftTokenExpiration,
               expiry > dependencies.now(), let uuid = account.minecraftUUID, let name = account.minecraftName {
                identities[xuid] = .init(name: name, uuid: uuid, accessToken: token, xuid: xuid)
                expirations[xuid] = expiry
                if !force && expiry.timeIntervalSince(dependencies.now()) > 60 { statuses[xuid] = .idle; return }
            } else { identities[xuid] = nil; expirations[xuid] = nil }
            statuses[xuid] = .connecting
            do {
                let refreshed = try await dependencies.refresh(tokens.microsoftRefreshToken)
                try Task.checkCancellation()
                guard isCurrent(account: account, xuid: xuid, epoch: epoch, generation: generation) else { return }
                tokens.microsoftRefreshToken = refreshed.refreshToken
                try dependencies.save(tokens, xuid)
                let session = try await dependencies.signIn(refreshed)
                try Task.checkCancellation()
                guard isCurrent(account: account, xuid: xuid, epoch: epoch, generation: generation) else { return }
                tokens.minecraftAccessToken = session.accessToken; tokens.minecraftTokenExpiration = session.expiration
                try dependencies.save(tokens, xuid)
                if appearances.revision(for: xuid) == appearanceRevision {
                    account.connect(session.profile)
                    try context.save()
                }
                identities[xuid] = .init(name: session.profile.name, uuid: session.profile.uuid, accessToken: session.accessToken, xuid: xuid)
                expirations[xuid] = session.expiration; statuses[xuid] = .idle
            } catch {
                guard !Task.isCancelled, isCurrent(account: account, xuid: xuid, epoch: epoch, generation: generation) else { return }
                statuses[xuid] = .failed(error.localizedDescription)
            }
        }
        tasks[xuid] = task
        await task.value
    }

    /// Refreshes once after a 401 and guarantees the retry uses a different Minecraft token.
    func refreshAfterUnauthorized(_ account: Account, rejectedToken: String) async throws -> MinecraftLaunchIdentity {
        let xuid = account.xuid
        try Task.checkCancellation()
        guard accounts[xuid]?.persistentModelID == account.persistentModelID else { throw MinecraftSessionError.accountChanged }
        let startingEpoch = sessionEpoch
        if let task = tasks[xuid] { await task.value }
        try Task.checkCancellation()
        guard isCurrent(account: account, xuid: xuid, epoch: startingEpoch) else { throw MinecraftSessionError.accountChanged }
        if let current = identity(for: account), current.accessToken != rejectedToken { return current }
        if let task = forcedRefreshTasks[xuid] {
            let identity = try await task.value
            try Task.checkCancellation()
            guard isCurrent(account: account, xuid: xuid, epoch: startingEpoch) else { throw MinecraftSessionError.accountChanged }
            return identity
        }

        let generation = UUID()
        let epoch = sessionEpoch
        let appearanceRevision = appearances.revision(for: xuid)
        refreshGenerations[xuid] = generation
        let task = Task { [self] () throws -> MinecraftLaunchIdentity in
            defer {
                if refreshGenerations[xuid] == generation {
                    forcedRefreshTasks[xuid] = nil
                    refreshGenerations[xuid] = nil
                }
            }
            try Task.checkCancellation()
            guard isCurrent(account: account, xuid: xuid, epoch: epoch) else { throw MinecraftSessionError.accountChanged }
            guard var tokens = dependencies.load(xuid) else { throw MinecraftSessionError.unavailable }
            let refreshed = try await dependencies.refresh(tokens.microsoftRefreshToken)
            try Task.checkCancellation()
            guard isCurrent(account: account, xuid: xuid, epoch: epoch) else { throw MinecraftSessionError.accountChanged }
            tokens.microsoftRefreshToken = refreshed.refreshToken
            try dependencies.save(tokens, xuid)
            let session = try await dependencies.signIn(refreshed)
            try Task.checkCancellation()
            guard isCurrent(account: account, xuid: xuid, epoch: epoch) else { throw MinecraftSessionError.accountChanged }
            guard session.accessToken != rejectedToken else {
                identities[xuid] = nil; expirations[xuid] = nil
                throw MinecraftSessionError.refreshedTokenRejected
            }
            tokens.minecraftAccessToken = session.accessToken
            tokens.minecraftTokenExpiration = session.expiration
            try dependencies.save(tokens, xuid)
            if appearances.revision(for: xuid) == appearanceRevision {
                account.connect(session.profile)
                try context.save()
            }
            let identity = MinecraftLaunchIdentity(name: session.profile.name, uuid: session.profile.uuid, accessToken: session.accessToken, xuid: xuid)
            identities[xuid] = identity
            expirations[xuid] = session.expiration
            statuses[xuid] = .idle
            return identity
        }
        forcedRefreshTasks[xuid] = task
        return try await task.value
    }

    func monitor(_ account: Account) async {
        while !Task.isCancelled {
            await connect(account)
            do { try await Task.sleep(for: .seconds(30)) } catch { return }
        }
    }

    func signOut() {
        sessionEpoch = UUID()
        for task in tasks.values { task.cancel() }
        for task in forcedRefreshTasks.values { task.cancel() }
        tasks.removeAll(); forcedRefreshTasks.removeAll()
        generations.removeAll(); refreshGenerations.removeAll()
        for xuid in Array(appearances.profiles.keys) { appearances.clear(xuid) }
        identities.removeAll(); expirations.removeAll(); accounts.removeAll(); statuses.removeAll()
    }

    private func isCurrent(account: Account, xuid: String, epoch: UUID, generation: UUID? = nil) -> Bool {
        sessionEpoch == epoch && accounts[xuid]?.persistentModelID == account.persistentModelID && (generation == nil || generations[xuid] == generation)
    }
}

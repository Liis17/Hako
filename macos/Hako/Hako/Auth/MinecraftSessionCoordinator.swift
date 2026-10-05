import Foundation
import Observation
import SwiftData

enum MinecraftStatus {
    case idle, connecting, failed(String)
}

@MainActor @Observable final class MinecraftSessionCoordinator {
    struct Dependencies {
        var load: (String) -> AccountTokens? = { TokenKeychain.load(for: $0) }
        var save: (AccountTokens, String) throws -> Void = { try TokenKeychain.save($0, for: $1) }
        var refresh: (String) async throws -> MicrosoftToken = { try await MicrosoftAuth.refresh($0) }
        var signIn: (MicrosoftToken) async throws -> MinecraftSession = { try await MicrosoftAuth.signInToMinecraft(with: $0) }
        var now: () -> Date = { .now }
    }

    private let context: ModelContext
    private let dependencies: Dependencies
    private var identities: [String: MinecraftLaunchIdentity] = [:]
    private var expirations: [String: Date] = [:]
    private var tasks: [String: Task<Void, Never>] = [:]
    private var generations: [String: UUID] = [:]
    private(set) var statuses: [String: MinecraftStatus] = [:]
    private(set) var checkedAt = Date.distantPast

    init(context: ModelContext, dependencies: Dependencies? = nil) { self.context = context; self.dependencies = dependencies ?? .init() }

    func identity(for account: Account?) -> MinecraftLaunchIdentity? {
        _ = checkedAt
        guard let account, let expiry = expirations[account.xuid], expiry > dependencies.now() else { return nil }
        return identities[account.xuid]
    }

    func connect(_ account: Account, force: Bool = false) async {
        checkedAt = dependencies.now()
        let xuid = account.xuid
        if let task = tasks[xuid] { await task.value; return }
        let generation = UUID()
        generations[xuid] = generation
        let task = Task { [self] in
            if !force, case .failed = statuses[xuid] { return }
            guard var tokens = dependencies.load(xuid) else {
                identities[xuid] = nil; expirations[xuid] = nil
                statuses[xuid] = .failed("Данные входа недоступны. Войдите в Microsoft повторно.")
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
                tokens.microsoftRefreshToken = refreshed.refreshToken
                try dependencies.save(tokens, xuid)
                let session = try await dependencies.signIn(refreshed)
                try Task.checkCancellation()
                tokens.minecraftAccessToken = session.accessToken; tokens.minecraftTokenExpiration = session.expiration
                try dependencies.save(tokens, xuid)
                account.connect(session.profile)
                try context.save()
                identities[xuid] = .init(name: session.profile.name, uuid: session.profile.uuid, accessToken: session.accessToken, xuid: xuid)
                expirations[xuid] = session.expiration; statuses[xuid] = .idle
            } catch {
                guard !Task.isCancelled else { return }
                statuses[xuid] = .failed(error.localizedDescription)
            }
        }
        tasks[xuid] = task
        await task.value
        if generations[xuid] == generation { tasks[xuid] = nil; generations[xuid] = nil }
    }

    func monitor(_ account: Account) async {
        while !Task.isCancelled {
            await connect(account)
            do { try await Task.sleep(for: .seconds(30)) } catch { return }
        }
    }

    func signOut() {
        for task in tasks.values { task.cancel() }
        tasks.removeAll(); generations.removeAll()
        identities.removeAll(); expirations.removeAll(); statuses.removeAll()
    }
}

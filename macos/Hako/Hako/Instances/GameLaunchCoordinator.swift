import Foundation
import Observation
import SwiftData

nonisolated enum GameRunState: Equatable, Sendable {
    case preparing, running, failed(String)
}

nonisolated struct MinecraftLaunchRequest: Sendable {
    let root: URL
    let version: String
    let sha1: String
    let executable: URL
    let identity: MinecraftLaunchIdentity
    let source: LaunchArgumentSource
    let parameters: InstanceParameters
    let clientID: String
}

@MainActor @Observable final class GameLaunchCoordinator {
    let sessions: MinecraftSessionCoordinator
    private let store: InstanceStore
    private let runner: GameProcessRunner
    private let prepareLaunch: @Sendable (MinecraftLaunchRequest) async throws -> MinecraftLaunchPlan
    private var attempts: [UUID: UUID] = [:]
    private var tasks: [UUID: Task<Void, Never>] = [:]
    private var started = false
    private(set) var states: [UUID: GameRunState] = [:]

    init(store: InstanceStore, sessions: MinecraftSessionCoordinator, runner: GameProcessRunner = .init(), prepare: @escaping @Sendable (MinecraftLaunchRequest) async throws -> MinecraftLaunchPlan = GameLaunchCoordinator.prepare) {
        self.store = store; self.sessions = sessions; self.runner = runner
        prepareLaunch = prepare
    }

    func start() {
        guard !started else { return }
        started = true
        do {
            for instance in try store.context.fetch(FetchDescriptor<GameInstance>()) {
                do {
                    let root = try store.storage.directory(instance.folderName)
                    if let record = try GameProcessRecord.load(in: root), record.instanceID == instance.id, record.isRunning {
                        let id = instance.id, attempt = UUID()
                        attempts[id] = attempt; states[id] = .running; store.launchBusy.insert(id)
                        Task { [runner, weak self] in await runner.observe(record, root: root) { [weak self] status in await self?.finished(id, attempt: attempt, status: status) } }
                    }
                } catch { states[instance.id] = .failed("Не удалось восстановить статус игры: \(error.localizedDescription)") }
            }
        } catch { started = false }
    }

    func disabledReason(_ instance: GameInstance, account: Account?) -> String? {
        if instance.state != .ready { return "Дождитесь завершения установки сборки." }
        if states[instance.id] == .preparing { return "Подготавливаем запуск…" }
        if states[instance.id] == .running { return "Эта сборка уже запущена." }
        if instance.offlineMode { return OfflineUsername.isValid(instance.offlineUsername) ? nil : "Проверьте ник в настройках offline-mode." }
        if sessions.identity(for: account) == nil { return "Нужен Minecraft-токен. Подключите аккаунт или включите offline-mode в настройках сборки." }
        return nil
    }

    func launch(_ instance: GameInstance, account: Account?) {
        guard disabledReason(instance, account: account) == nil else { return }
        let id = instance.id, attempt = UUID()
        let offline = instance.offlineMode, username = instance.offlineUsername, folder = instance.folderName
        let source = instance.argumentSource, parameters = instance.effectiveParameters()
        let override = source == .global ? GameLaunchDefaults.load().javaPath.trimmingCharacters(in: .whitespacesAndNewlines) : ""
        let javaExecutable = instance.javaExecutable, version = instance.versionID, sha1 = instance.metadataSHA1
        attempts[id] = attempt; states[id] = .preparing; store.launchBusy.insert(id)
        tasks[id] = Task { [self] in
            defer { tasks[id] = nil }
            do {
                let identity: MinecraftLaunchIdentity
                if offline { identity = try .offline(name: username) }
                else {
                    guard let account else { throw InstanceFileError.message("Войдите в Minecraft или включите offline-mode.") }
                    await sessions.connect(account)
                    guard let current = sessions.identity(for: account) else { throw InstanceFileError.message("Minecraft-сессия недоступна. Повторите подключение аккаунта.") }
                    identity = current
                }
                let root = try store.storage.directory(folder)
                let executable = override.isEmpty ? try InstanceStorage.containedURL("java/\(javaExecutable)", in: root) : URL(fileURLWithPath: (override as NSString).expandingTildeInPath)
                let plan = try await prepareLaunch(.init(root: root, version: version, sha1: sha1, executable: executable, identity: identity, source: source, parameters: parameters, clientID: MicrosoftAuth.clientID))
                try Task.checkCancellation()
                if !offline, sessions.identity(for: account)?.accessToken != identity.accessToken { throw InstanceFileError.message("Minecraft-сессия изменилась или завершилась. Повторите запуск.") }
                let record = try await runner.start(plan, id: id, root: root) { [weak self] status in await self?.finished(id, attempt: attempt, status: status) }
                if attempts[id] == attempt, states[id] == .preparing {
                    if record.startSeconds > 0 { states[id] = .running }
                    else { store.launchBusy.remove(id) }
                }
            } catch {
                guard attempts[id] == attempt else { return }
                store.launchBusy.remove(id)
                states[id] = .failed(error.localizedDescription)
            }
        }
    }

    func logURL(_ instance: GameInstance) -> URL? { try? InstanceStorage.containedURL("minecraft/logs/hako-launch.log", in: store.storage.directory(instance.folderName)) }

    private func finished(_ id: UUID, attempt: UUID, status: Int32?) {
        guard attempts[id] == attempt else { return }
        store.launchBusy.remove(id)
        if let status, status != 0 { states[id] = .failed("Minecraft завершился с кодом \(status). Подробности — в журнале запуска.") }
        else { states[id] = nil }
    }

    @concurrent static func prepare(_ request: MinecraftLaunchRequest) async throws -> MinecraftLaunchPlan {
        let root = request.root, version = request.version, sha1 = request.sha1, executable = request.executable
        let url = try InstanceStorage.containedURL("minecraft/versions/\(version)/\(version).json", in: root)
        let data = try Data(contentsOf: url)
        try MojangIntegrity.check(data, download: .init(url: url, sha1: sha1))
        let manifest = try JSONDecoder().decode(MinecraftVersionManifest.self, from: data)
        guard manifest.id == version else { throw MojangError.invalid("Описание версии не соответствует сборке.") }
        var assets: MinecraftAssetIndex?
        if let index = manifest.assetIndex {
            let url = try InstanceStorage.containedURL("minecraft/assets/indexes/\(index.id).json", in: root)
            let data = try Data(contentsOf: url)
            try MojangIntegrity.check(data, download: index.download)
            assets = try JSONDecoder().decode(MinecraftAssetIndex.self, from: data)
        }
        try await JavaLaunchValidation.validate(executable, minimumMajor: manifest.java.majorVersion)
        let plan = try MinecraftLaunchPlan.build(manifest: manifest, root: root, executable: executable, identity: request.identity, source: request.source, parameters: request.parameters, assetIndex: assets, clientID: request.clientID)
        guard FileManager.default.fileExists(atPath: try InstanceStorage.containedURL("minecraft/versions/\(version)/\(version).jar", in: root).path) else { throw InstanceFileError.message("Файл Minecraft отсутствует. Повторите установку сборки.") }
        return plan
    }
}

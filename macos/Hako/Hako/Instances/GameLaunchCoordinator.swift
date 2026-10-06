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
    var fabric: FabricConfiguration? = nil
    var fabricProfileSHA1: String? = nil
}

@MainActor @Observable final class GameLaunchCoordinator {
    let sessions: MinecraftSessionCoordinator
    let playtime: PlaytimeCoordinator
    private let store: InstanceStore
    private let runner: GameProcessRunner
    private let prepareLaunch: @Sendable (MinecraftLaunchRequest) async throws -> MinecraftLaunchPlan
    private var attempts: [UUID: UUID] = [:]
    private var tasks: [UUID: Task<Void, Never>] = [:]
    private var started = false
    private(set) var states: [UUID: GameRunState] = [:]

    init(store: InstanceStore, sessions: MinecraftSessionCoordinator, playtime: PlaytimeCoordinator, runner: GameProcessRunner = .init(), prepare: @escaping @Sendable (MinecraftLaunchRequest) async throws -> MinecraftLaunchPlan = GameLaunchCoordinator.prepare) {
        self.store = store; self.sessions = sessions; self.runner = runner
        self.playtime = playtime
        prepareLaunch = prepare
    }

    func start() {
        guard !started else { return }
        started = true
        playtime.start()
        do {
            let xuid = try store.context.fetch(FetchDescriptor<Account>()).first?.xuid
            for instance in try store.context.fetch(FetchDescriptor<GameInstance>()) {
                do {
                    let root = try store.storage.directory(instance.folderName)
                    if var record = try GameProcessRecord.load(in: root), record.instanceID == instance.id, record.isRunning {
                        let id = instance.id, attempt = UUID()
                        let sessionID = try playtime.resumeSession(instanceID: id, sessionID: record.sessionID, xuid: xuid)
                        record.sessionID = sessionID
                        let restoredRecord = record, tracking = PlaytimeTrackingRequest(sessionID: sessionID, directory: playtime.journalDirectory)
                        attempts[id] = attempt; states[id] = .running; store.launchBusy.insert(id)
                        Task { [runner, weak self] in
                            do { try await runner.observe(restoredRecord, root: root, tracking: tracking) { [weak self] status in await self?.finished(id, attempt: attempt, status: status) } }
                            catch {
                                guard let self, self.attempts[id] == attempt else { return }
                                self.states[id] = .failed("Не удалось восстановить учёт времени: \(error.localizedDescription)")
                            }
                        }
                    }
                } catch { states[instance.id] = .failed("Не удалось восстановить статус игры: \(error.localizedDescription)") }
            }
        } catch { started = false }
    }

    func disabledReason(_ instance: GameInstance, account: Account?) -> String? {
        if store.contentBusy.contains(instance.id) { return "Дождитесь завершения операций с файлами сборки." }
        if instance.state != .ready { return "Дождитесь завершения установки сборки." }
        if states[instance.id] == .preparing { return "Подготавливаем запуск…" }
        if states[instance.id] == .running { return "Эта сборка уже запущена." }
        if store.launchBusy.contains(instance.id) { return "Эта сборка уже запущена." }
        if instance.offlineMode { return OfflineUsername.isValid(instance.offlineUsername) ? nil : "Проверьте ник в настройках offline-mode." }
        if sessions.identity(for: account) == nil { return "Нужен Minecraft-токен. Подключите аккаунт или включите offline-mode в настройках сборки." }
        return nil
    }

    func launch(_ instance: GameInstance, account: Account?) {
        guard disabledReason(instance, account: account) == nil else { return }
        let id = instance.id, attempt = UUID()
        let sessionID: UUID
        do { sessionID = try playtime.beginSession(instanceID: id, xuid: account?.xuid) }
        catch { states[id] = .failed("Не удалось начать учёт времени: \(error.localizedDescription)"); return }
        let offline = instance.offlineMode, username = instance.offlineUsername, folder = instance.folderName
        let source = instance.argumentSource, parameters = instance.effectiveParameters()
        let override = source == .global ? GameLaunchDefaults.load().javaPath.trimmingCharacters(in: .whitespacesAndNewlines) : ""
        let javaExecutable = instance.javaExecutable, version = instance.versionID, sha1 = instance.metadataSHA1
        let fabricProfileSHA1 = instance.fabricProfileSHA1
        let fabric: FabricConfiguration?
        do { fabric = try instance.fabricConfiguration() }
        catch { try? playtime.cancelSession(sessionID); states[id] = .failed(error.localizedDescription); return }
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
                let plan = try await prepareLaunch(.init(root: root, version: version, sha1: sha1, executable: executable, identity: identity, source: source, parameters: parameters, clientID: MicrosoftAuth.clientID, fabric: fabric, fabricProfileSHA1: fabricProfileSHA1))
                try Task.checkCancellation()
                if !offline, sessions.identity(for: account)?.accessToken != identity.accessToken { throw InstanceFileError.message("Minecraft-сессия изменилась или завершилась. Повторите запуск.") }
                let record = try await runner.start(plan, id: id, root: root, tracking: .init(sessionID: sessionID, directory: playtime.journalDirectory)) { [weak self] status in await self?.finished(id, attempt: attempt, status: status) }
                if attempts[id] == attempt, states[id] == .preparing {
                    if record.startSeconds > 0 { states[id] = .running }
                    else { store.launchBusy.remove(id) }
                }
                playtime.refresh()
            } catch {
                guard attempts[id] == attempt else { return }
                store.launchBusy.remove(id)
                states[id] = .failed(error.localizedDescription)
                try? playtime.cancelSession(sessionID)
            }
        }
    }

    func logURL(_ instance: GameInstance) -> URL? { try? InstanceStorage.containedURL("minecraft/logs/hako-launch.log", in: store.storage.directory(instance.folderName)) }

    private func finished(_ id: UUID, attempt: UUID, status: Int32?) {
        guard attempts[id] == attempt else { return }
        store.launchBusy.remove(id)
        playtime.refresh()
        if case .failed = states[id] { return }
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
        let fabric = try request.fabric.map { _ in try FabricProfile.installed(root: root, minecraft: version, sha1: request.fabricProfileSHA1) }
        if let fabric, let configuration = request.fabric, fabric.id != "fabric-loader-\(configuration.loaderVersion)-\(version)" {
            throw InstanceFileError.message("Установленная версия Fabric не соответствует сборке. Повторите установку.")
        }
        var assets: MinecraftAssetIndex?
        if let index = manifest.assetIndex {
            let url = try InstanceStorage.containedURL("minecraft/assets/indexes/\(index.id).json", in: root)
            let data = try Data(contentsOf: url)
            try MojangIntegrity.check(data, download: index.download)
            assets = try JSONDecoder().decode(MinecraftAssetIndex.self, from: data)
        }
        try await JavaLaunchValidation.validate(executable, minimumMajor: manifest.java.majorVersion)
        let plan = try MinecraftLaunchPlan.build(manifest: manifest, root: root, executable: executable, identity: request.identity, source: request.source, parameters: request.parameters, assetIndex: assets, clientID: request.clientID, fabric: fabric)
        if let fabric {
            for library in fabric.libraries {
                guard FileManager.default.fileExists(atPath: try InstanceStorage.containedURL("minecraft/libraries/\(library.path)", in: root).path) else {
                    throw InstanceFileError.message("Библиотека Fabric отсутствует. Повторите установку сборки.")
                }
            }
        }
        guard FileManager.default.fileExists(atPath: try InstanceStorage.containedURL("minecraft/versions/\(version)/\(version).jar", in: root).path) else { throw InstanceFileError.message("Файл Minecraft отсутствует. Повторите установку сборки.") }
        return plan
    }
}

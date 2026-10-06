import Foundation
import Observation
import SwiftData

/// Одна очередь на приложение: переходы между страницами и выход из аккаунта не прерывают установку.
@MainActor @Observable final class InstallationCoordinator {
    let store: InstanceStore
    let client: MojangClient
    let fabricClient: FabricClient
    let content: InstanceContent
    private let installer: MinecraftInstaller
    private var activeID: UUID?
    private var activeTask: Task<Void, Never>?
    private var started = false
    var progress: [UUID: InstallationProgress] = [:]
    var queueError: String?
    var contentBusy: Set<UUID> {
        get { store.contentBusy }
        set { store.contentBusy = newValue }
    }
    private var lastProgressUpdate = Date.distantPast

    init(context: ModelContext, storage: InstanceStorage = .init(), client: MojangClient = .init(), fabricClient: FabricClient = .init(), content: InstanceContent = .init(), installer: MinecraftInstaller? = nil) {
        store = InstanceStore(context: context, storage: storage)
        self.client = client
        self.fabricClient = fabricClient
        self.content = content
        self.installer = installer ?? MinecraftInstaller(client: client, fabricClient: fabricClient, content: content)
    }

    func start() {
        guard !started else { return }
        started = true
        do {
            for instance in try store.context.fetch(FetchDescriptor<GameInstance>()) {
                if instance.pauseRequested { instance.state = .paused }
                else if instance.state == .installing { instance.state = .queued }
            }
            try store.context.save()
            pump()
        } catch {
            started = false
            queueError = "Не удалось восстановить очередь: \(error.localizedDescription)"
        }
    }

    func enqueue(_ instance: GameInstance) throws {
        guard !contentBusy.contains(instance.id) else { throw InstanceFileError.message("Дождитесь завершения операций с файлами сборки.") }
        guard !store.launchBusy.contains(instance.id) else { throw InstanceFileError.message("Закройте Minecraft перед повторной установкой сборки.") }
        guard activeID != instance.id else { return }
        let oldState = instance.state
        let oldPause = instance.pauseRequested
        let oldError = instance.installationError
        instance.pauseRequested = false
        instance.state = .queued
        instance.installationError = nil
        do { try store.context.save() }
        catch {
            instance.state = oldState; instance.pauseRequested = oldPause; instance.installationError = oldError
            throw error
        }
        pump()
    }

    func scheduleQueuedInstallations() {
        if started { pump() } else { start() }
    }

    func pause(_ instance: GameInstance) throws {
        let oldState = instance.state
        let oldPause = instance.pauseRequested
        instance.pauseRequested = true
        if activeID != instance.id { instance.state = .paused }
        do { try store.context.save() }
        catch { instance.state = oldState; instance.pauseRequested = oldPause; throw error }
        if activeID == instance.id {
            progress[instance.id]?.stage = "Останавливаем загрузку…"
            activeTask?.cancel()
        } else {
            progress[instance.id]?.stage = InstallationState.paused.title
            pump()
        }
    }

    private func pump() {
        guard activeTask == nil else { return }
        do {
            let instances = try store.context.fetch(FetchDescriptor<GameInstance>(sortBy: [SortDescriptor(\.createdAt)]))
            queueError = nil
            for instance in instances where instance.state == .queued {
                if contentBusy.contains(instance.id) { continue }
                let root: URL
                let version: MinecraftVersion
                let fabric: FabricConfiguration?
                do {
                    guard let url = URL(string: instance.metadataURL), url.scheme == "https" else {
                        throw InstanceFileError.message("Не удалось прочитать описание версии.")
                    }
                    version = MinecraftVersion(id: instance.versionID, type: "release", url: url, sha1: instance.metadataSHA1)
                    root = try store.storage.directory(instance.folderName)
                    fabric = try instance.fabricConfiguration()
                } catch {
                    instance.state = .failed
                    instance.installationError = error.localizedDescription
                    try store.context.save()
                    continue
                }
                let id = instance.id
                instance.state = .installing
                do { try store.context.save() }
                catch { instance.state = .queued; throw error }
                activeID = instance.id
                activeTask = Task { [self] in
                    defer {
                        activeID = nil
                        activeTask = nil
                        pump()
                    }
                    do {
                        let result = try await installer.install(version, at: root, fabric: fabric) { [weak self] value in
                            await self?.setProgress(value, id: id)
                        }
                        try Task.checkCancellation()
                        instance.javaMajorVersion = result.javaMajorVersion
                        instance.javaExecutable = result.javaExecutable
                        instance.legacyTexturepacks = result.legacyTexturepacks
                        instance.fabricProfileSHA1 = result.fabricProfileSHA1
                        instance.state = .ready
                        instance.installationError = nil
                        progress.removeValue(forKey: instance.id)
                    } catch {
                        if instance.pauseRequested {
                            instance.state = .paused
                            instance.installationError = nil
                        } else {
                            instance.state = .failed
                            instance.installationError = error.localizedDescription
                        }
                        progress[instance.id]?.stage = instance.state.title
                    }
                    do { try store.context.save() }
                    catch {
                        instance.state = .failed
                        instance.installationError = "Не удалось сохранить состояние сборки: \(error.localizedDescription)"
                    }
                }
                return
            }
        } catch {
            activeID = nil
            queueError = "Не удалось начать установку: \(error.localizedDescription)"
        }
    }

    private func setProgress(_ value: InstallationProgress, id: UUID) {
        if let previous = progress[id], previous.stage == value.stage, value.fraction - previous.fraction < 0.002, Date().timeIntervalSince(lastProgressUpdate) < 0.15 { return }
        progress[id] = value
        lastProgressUpdate = Date()
    }
}

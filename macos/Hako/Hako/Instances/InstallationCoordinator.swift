import Foundation
import Observation
import SwiftData

/// Одна очередь на приложение: переходы между страницами и выход из аккаунта не прерывают установку.
@MainActor @Observable final class InstallationCoordinator {
    let store: InstanceStore
    let client: MojangClient
    private let installer: MinecraftInstaller
    private var activeID: UUID?
    private var activeTask: Task<Void, Never>?
    private var userPaused: Set<UUID> = []
    private var started = false
    var progress: [UUID: InstallationProgress] = [:]
    var queueError: String?

    init(context: ModelContext, storage: InstanceStorage = .init(), client: MojangClient = .init(), installer: MinecraftInstaller? = nil) {
        store = InstanceStore(context: context, storage: storage)
        self.client = client
        self.installer = installer ?? MinecraftInstaller(client: client)
    }

    func start() {
        guard !started else { return }
        started = true
        do {
            for instance in try store.context.fetch(FetchDescriptor<GameInstance>()) where instance.state == .installing { instance.state = .queued }
            try store.context.save()
            pump()
        } catch {
            started = false
            queueError = "Не удалось восстановить очередь: \(error.localizedDescription)"
        }
    }

    func enqueue(_ instance: GameInstance) throws {
        guard activeID != instance.id else { return }
        userPaused.remove(instance.id)
        instance.state = .queued
        instance.installationError = nil
        try store.context.save()
        pump()
    }

    func scheduleQueuedInstallations() { pump() }

    func pause(_ instance: GameInstance) throws {
        if activeID == instance.id {
            userPaused.insert(instance.id)
            progress[instance.id]?.stage = "Останавливаем загрузку…"
            activeTask?.cancel()
        } else {
            instance.state = .paused
            try store.context.save()
        }
    }

    private func pump() {
        guard activeTask == nil else { return }
        do {
            let instances = try store.context.fetch(FetchDescriptor<GameInstance>(sortBy: [SortDescriptor(\.createdAt)]))
            guard let instance = instances.first(where: { $0.state == .queued }) else { return }
            guard let url = URL(string: instance.metadataURL) else {
                instance.state = .failed
                instance.installationError = "Не удалось прочитать описание версии."
                try store.context.save()
                pump()
                return
            }
            let version = MinecraftVersion(id: instance.versionID, type: "release", url: url, sha1: instance.metadataSHA1)
            let root = try store.storage.directory(instance.folderName)
            let id = instance.id
            activeID = instance.id
            instance.state = .installing
            try store.context.save()
            activeTask = Task { [self] in
                defer {
                    activeID = nil
                    activeTask = nil
                    pump()
                }
                do {
                    let result = try await installer.install(version, at: root) { [weak self] value in
                        await self?.setProgress(value, id: id)
                    }
                    try Task.checkCancellation()
                    instance.javaMajorVersion = result.javaMajorVersion
                    instance.javaExecutable = result.javaExecutable
                    instance.legacyTexturepacks = result.legacyTexturepacks
                    instance.state = .ready
                    instance.installationError = nil
                    progress.removeValue(forKey: instance.id)
                } catch {
                    if userPaused.remove(instance.id) != nil {
                        instance.state = .paused
                        instance.installationError = nil
                    } else {
                        instance.state = .failed
                        instance.installationError = error.localizedDescription
                    }
                }
                do { try store.context.save() }
                catch {
                    instance.state = .failed
                    instance.installationError = "Не удалось сохранить состояние сборки: \(error.localizedDescription)"
                }
            }
        } catch {
            activeID = nil
            queueError = "Не удалось начать установку: \(error.localizedDescription)"
        }
    }

    private func setProgress(_ value: InstallationProgress, id: UUID) { progress[id] = value }
}

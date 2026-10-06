import Foundation
import Observation

@MainActor struct ContentConfirmation: Identifiable {
    let id = UUID()
    let title: String
    let message: String
    let action: String
    let destructive: Bool
}

/// Lives with the launcher, not with the currently selected content tab.
@MainActor @Observable final class InstanceContentController {
    let installations: InstallationCoordinator
    var mods: [UUID: [InstanceContentItem]] = [:]
    var packs: [UUID: [InstanceContentItem]] = [:]
    var errors: [UUID: String] = [:]
    var updates: [UUID: [String: FabricAPIDescriptor]] = [:]
    var updateMessages: [UUID: String] = [:]
    private(set) var checkingUpdates: Set<UUID> = []
    private(set) var confirmations: [ContentConfirmation] = []
    @ObservationIgnored private var answers: [UUID: CheckedContinuation<Bool, Never>] = [:]
    @ObservationIgnored private var reloads: [String: UUID] = [:]

    init(installations: InstallationCoordinator) { self.installations = installations }
    var confirmation: ContentConfirmation? { confirmations.first }

    func folder(_ instance: GameInstance, mods: Bool) throws -> URL {
        try InstanceStorage.containedURL("minecraft/\(mods ? "mods" : instance.legacyTexturepacks ? "texturepacks" : "resourcepacks")", in: installations.store.storage.directory(instance.folderName))
    }

    func disabledReason(_ instance: GameInstance, mods: Bool) -> String? {
        if installations.store.launchBusy.contains(instance.id) { return "Закройте Minecraft перед изменением файлов сборки." }
        if installations.contentBusy.contains(instance.id) { return "Дождитесь завершения операции с файлами." }
        if mods && instance.modLoader != .fabric { return "Моды доступны для сборок с Fabric." }
        if mods && (instance.state == .queued || instance.state == .installing) { return "Дождитесь завершения установки Fabric." }
        return nil
    }

    func reload(_ instance: GameInstance, mods: Bool, clearError: Bool = true) async {
        let key = "\(instance.id):\(mods)", request = UUID(), folderName = instance.folderName
        reloads[key] = request
        do {
            let folder = try folder(instance, mods: mods)
            let items: [InstanceContentItem]
            do { items = try await installations.content.list(at: folder, mods: mods) }
            catch {
                guard mods else { throw error }
                items = try await installations.content.list(at: folder, mods: mods, readOrigins: false)
                guard !Task.isCancelled, reloads[key] == request, instance.folderName == folderName else { return }
                errors[instance.id] = "Не удалось прочитать реестр модов. Источники и обновления недоступны: \(error.localizedDescription)"
                self.mods[instance.id] = items
                return
            }
            guard !Task.isCancelled, reloads[key] == request, instance.folderName == folderName else { return }
            if mods {
                self.mods[instance.id] = items
                let eligible = items.filter { $0.origin?.api != nil }
                updates[instance.id] = updates[instance.id]?.filter { key, api in eligible.contains { $0.logicalName.lowercased() == key && $0.origin?.versionID != api.versionID } }
                if eligible.isEmpty { updateMessages[instance.id] = nil }
            } else { packs[instance.id] = items }
            if clearError { errors[instance.id] = nil }
        } catch {
            if !Task.isCancelled && reloads[key] == request && instance.folderName == folderName { errors[instance.id] = error.localizedDescription }
        }
    }

    private func confirm(_ confirmation: ContentConfirmation) async -> Bool {
        await withCheckedContinuation { continuation in
            answers[confirmation.id] = continuation; confirmations.append(confirmation)
        }
    }

    func resolveConfirmation(_ id: UUID, accepted: Bool) {
        guard let confirmation = confirmations.first, confirmation.id == id else { return }
        confirmations.removeFirst()
        answers.removeValue(forKey: confirmation.id)?.resume(returning: accepted)
    }

    @discardableResult private func perform(_ instance: GameInstance, mods: Bool, operation: @escaping @MainActor (URL) async throws -> Void) -> Bool {
        if let reason = disabledReason(instance, mods: mods) { errors[instance.id] = reason; return false }
        let folder: URL
        do { folder = try self.folder(instance, mods: mods) }
        catch { errors[instance.id] = error.localizedDescription; return false }
        installations.contentBusy.insert(instance.id); errors[instance.id] = nil
        Task {
            defer { installations.contentBusy.remove(instance.id); installations.scheduleQueuedInstallations() }
            do { try await operation(folder); await reload(instance, mods: mods, clearError: false) }
            catch { errors[instance.id] = error.localizedDescription; await reload(instance, mods: mods, clearError: false) }
        }
        return true
    }

    func importFiles(_ sources: [URL], into instance: GameInstance, mods: Bool) {
        guard !sources.isEmpty else { return }
        // Keep file-picker grants alive while the batch waits for a replacement decision.
        let scoped = sources.filter { $0.startAccessingSecurityScopedResource() }
        if let reason = disabledReason(instance, mods: mods) {
            scoped.forEach { $0.stopAccessingSecurityScopedResource() }; errors[instance.id] = reason; return
        }
        let started = perform(instance, mods: mods) { [self] folder in
            defer { scoped.forEach { $0.stopAccessingSecurityScopedResource() } }
            var failures: [String] = []
            for source in sources {
                do { try await installations.content.importItem(from: source, into: folder, mods: mods) }
                catch PackImportError.exists {
                    let allowed = await confirm(.init(title: "Заменить \(mods ? "мод" : "ресурспак")?", message: "\(source.lastPathComponent) уже существует в сборке «\(instance.name)».", action: "Заменить", destructive: true))
                    if allowed {
                        do { try await installations.content.importItem(from: source, into: folder, mods: mods, replace: true) }
                        catch { failures.append("\(source.lastPathComponent): \(error.localizedDescription)") }
                    }
                } catch { failures.append("\(source.lastPathComponent): \(error.localizedDescription)") }
            }
            if !failures.isEmpty { errors[instance.id] = failures.joined(separator: "\n") }
        }
        if !started { scoped.forEach { $0.stopAccessingSecurityScopedResource() } }
    }

    func setEnabled(_ item: InstanceContentItem, in instance: GameInstance, enabled: Bool, mods: Bool = true) {
        perform(instance, mods: mods) { [self] folder in
            if !enabled && item.origin?.projectID == FabricAPIDescriptor.project {
                guard await confirm(.init(title: "Отключить Fabric API?", message: "Моды, зависящие от Fabric API, могут больше не давать игре запуститься.", action: "Отключить", destructive: false)) else { return }
            }
            try await installations.content.setEnabled(item, in: folder, enabled: enabled, mods: mods)
        }
    }

    func delete(_ item: InstanceContentItem, in instance: GameInstance, mods: Bool) {
        perform(instance, mods: mods) { [self] folder in
            let warning = item.origin?.projectID == FabricAPIDescriptor.project ? " Моды, зависящие от Fabric API, могут больше не давать игре запуститься." : ""
            guard await confirm(.init(title: "Удалить \(mods ? "мод" : "ресурспак")?", message: "\(item.logicalName) будет перемещён в корзину.\(warning)", action: "Удалить", destructive: true)) else { return }
            try await installations.content.trash(item, in: folder, mods: mods)
        }
    }

    func checkUpdates(_ instance: GameInstance) async {
        guard instance.modLoader == .fabric, !checkingUpdates.contains(instance.id),
              mods[instance.id]?.contains(where: { $0.origin?.api != nil }) == true else { return }
        checkingUpdates.insert(instance.id); updateMessages[instance.id] = nil
        defer { checkingUpdates.remove(instance.id) }
        do {
            guard let configuration = try instance.fabricConfiguration() else { return }
            let latest = try await installations.fabricClient.latestAPI(minecraft: instance.versionID)
            guard !Task.isCancelled else { return }
            let current = (mods[instance.id] ?? []).filter { $0.origin?.api != nil && $0.origin?.versionID != latest.versionID }
            guard !current.isEmpty else { updates[instance.id] = [:]; return }
            let metadata = try await installations.fabricClient.metadata(for: latest)
            guard !Task.isCancelled else { return }
            guard try metadata.supports(loader: configuration.loaderVersion, java: instance.javaMajorVersion) else {
                updates[instance.id] = [:]
                updateMessages[instance.id] = "Fabric API \(latest.version) требует другой версии Loader или Java. Текущая версия сохранена."
                return
            }
            var available: [String: FabricAPIDescriptor] = [:]
            for previous in current where mods[instance.id]?.contains(where: { $0.logicalName == previous.logicalName && $0.origin?.sha512 == previous.origin?.sha512 && $0.origin?.versionID != latest.versionID }) == true {
                available[previous.logicalName.lowercased()] = latest
            }
            updates[instance.id] = available
        } catch {
            if !Task.isCancelled { updateMessages[instance.id] = "Не удалось проверить обновление Fabric API: \(error.localizedDescription)" }
        }
    }

    func update(_ item: InstanceContentItem, in instance: GameInstance) {
        guard item.origin?.api != nil, let next = updates[instance.id]?[item.logicalName.lowercased()] else { return }
        perform(instance, mods: true) { [self] folder in
            guard let configuration = try instance.fabricConfiguration() else { throw InstanceFileError.message("Конфигурация Fabric отсутствует.") }
            let metadata = try await installations.fabricClient.metadata(for: next)
            guard try metadata.supports(loader: configuration.loaderVersion, java: instance.javaMajorVersion) else { throw InstanceFileError.message("Обновление Fabric API несовместимо с Loader или Java сборки.") }
            let cached = try await installations.fabricClient.cachedAPI(next)
            try await installations.content.updateAPI(item, to: next, from: cached, in: folder)
            updates[instance.id] = nil; updateMessages[instance.id] = nil
        }
    }
}

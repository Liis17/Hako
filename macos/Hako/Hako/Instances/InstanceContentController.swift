import Foundation
import Observation
import SwiftData

@MainActor struct ContentConfirmation: Identifiable {
    struct Project: Identifiable {
        let id: String
        let title: String
        let iconURL: URL?
        let note: String?
    }
    let id = UUID()
    let title: String
    let message: String
    /// `nil` скрывает основную кнопку, когда выполнить действие нельзя.
    let action: String?
    let destructive: Bool
    var alternative: String? = nil
    var projects: [Project] = []
    var resourcePacks: [Project] = []
}

enum ContentChoice: Equatable { case cancel, primary, alternative, resourcePack(String) }

/// Новая версия Modrinth для файла сборки.
struct ModrinthUpdate {
    let projectID: String
    let version: ModrinthVersion
    let currentSHA512: String
    let file: ModrinthVersion.File
}

/// Lives with the launcher, not with the currently selected content tab.
@MainActor @Observable final class InstanceContentController {
    let installations: InstallationCoordinator
    var mods: [UUID: [InstanceContentItem]] = [:]
    var packs: [UUID: [InstanceContentItem]] = [:]
    var datapacks: [UUID: [String: [InstanceContentItem]]] = [:]
    var worldInstallMessages: [UUID: [String: String]] = [:]
    var worldOperations: [UUID: [String: String]] = [:]
    var worldRevisions: [UUID: Int] = [:]
    var worldBackupURLs: [UUID: URL] = [:]
    var errors: [UUID: String] = [:]
    var updates: [UUID: [String: FabricAPIDescriptor]] = [:]
    var updateMessages: [UUID: String] = [:]
    private(set) var checkingUpdates: Set<UUID> = []
    /// Обновления модов и ресурспаков из Modrinth по ключу раздела `updateKey`, внутри — по имени файла.
    private(set) var modrinthUpdates: [String: [String: ModrinthUpdate]] = [:]
    private(set) var checkingModrinth: Set<String> = []
    var modrinthUpdateMessages: [String: String] = [:]
    private(set) var confirmations: [ContentConfirmation] = []
    /// Проект Modrinth, который сейчас устанавливается в сборку.
    private(set) var catalogInstalling: [UUID: String] = [:]
    private(set) var catalogInstallTargets: [UUID: ModrinthInstallTarget] = [:]
    let modrinth: ModrinthClient
    let worlds = InstanceWorlds()
    let screenshots = InstanceScreenshots()
    @ObservationIgnored private var answers: [UUID: CheckedContinuation<ContentChoice, Never>] = [:]
    @ObservationIgnored private var reloads: [String: UUID] = [:]
    @ObservationIgnored private var modrinthCheckRequests: [String: UUID] = [:]

    init(installations: InstallationCoordinator, modrinth: ModrinthClient = .init()) { self.installations = installations; self.modrinth = modrinth }
    var confirmation: ContentConfirmation? { confirmations.first }

    func folder(_ instance: GameInstance, mods: Bool) throws -> URL {
        try InstanceStorage.containedURL("minecraft/\(mods ? "mods" : instance.legacyTexturepacks ? "texturepacks" : "resourcepacks")", in: installations.store.storage.directory(instance.folderName))
    }

    func folder(_ instance: GameInstance, target: ModrinthInstallTarget) throws -> URL {
        if let world = target.world {
            return try InstanceWorlds.datapacksFolder(world: world, in: installations.store.storage.directory(instance.folderName))
        }
        return try folder(instance, mods: target == .mods)
    }

    func items(_ instance: GameInstance, target: ModrinthInstallTarget) -> [InstanceContentItem] {
        if let world = target.world { return datapacks[instance.id]?[world] ?? [] }
        return (target == .mods ? mods : packs)[instance.id] ?? []
    }

    func disabledReason(_ instance: GameInstance, target: ModrinthInstallTarget) -> String? {
        if target.world != nil, let reason = worldBlockedReason(instance) { return reason }
        if let reason = disabledReason(instance, mods: target == .mods) { return reason }
        if target.world != nil {
            do { _ = try folder(instance, target: target) }
            catch { return error.localizedDescription }
        }
        return nil
    }

    func reload(_ instance: GameInstance, target: ModrinthInstallTarget, clearError: Bool = true) async {
        guard let world = target.world else { await reload(instance, mods: target == .mods, clearError: clearError); return }
        let key = "\(instance.id):world:\(world)", request = UUID(), folderName = instance.folderName
        reloads[key] = request
        do {
            let contentFolder = try folder(instance, target: target)
            let items: [InstanceContentItem]
            do { items = try await installations.content.list(at: contentFolder, mods: false) }
            catch {
                items = try await installations.content.list(at: contentFolder, mods: false, readOrigins: false)
                guard !Task.isCancelled, reloads[key] == request, instance.folderName == folderName else { return }
                datapacks[instance.id, default: [:]][world] = items
                await retainUpdates(updateKey(instance, target: target), items: items, at: contentFolder)
                guard !Task.isCancelled, reloads[key] == request, instance.folderName == folderName else { return }
                errors[instance.id] = String(appLocalized: "Не удалось прочитать реестр происхождения содержимого. Источники и обновления недоступны: \(error.localizedDescription)")
                return
            }
            guard !Task.isCancelled, reloads[key] == request, instance.folderName == folderName else { return }
            datapacks[instance.id, default: [:]][world] = items
            let updatesKey = updateKey(instance, target: target)
            await retainUpdates(updatesKey, items: items, at: contentFolder)
            guard !Task.isCancelled, reloads[key] == request, instance.folderName == folderName else { return }
            if clearError { errors[instance.id] = nil }
        } catch {
            if !Task.isCancelled, reloads[key] == request, instance.folderName == folderName { errors[instance.id] = error.localizedDescription }
        }
    }

    func disabledReason(_ instance: GameInstance, mods: Bool) -> String? {
        if installations.store.launchBusy.contains(instance.id) { return String(appLocalized: "Закройте Minecraft перед изменением файлов сборки.") }
        if installations.contentBusy.contains(instance.id) { return String(appLocalized: "Дождитесь завершения операции с файлами.") }
        if mods && instance.modLoader != .fabric { return String(appLocalized: "Моды доступны для сборок с Fabric.") }
        if mods && (instance.state == .queued || instance.state == .installing) { return String(appLocalized: "Дождитесь завершения установки Fabric.") }
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
                items = try await installations.content.list(at: folder, mods: mods, readOrigins: false)
                guard !Task.isCancelled, reloads[key] == request, instance.folderName == folderName else { return }
                if mods { self.mods[instance.id] = items } else { self.packs[instance.id] = items }
                await retainUpdates(updateKey(instance, mods: mods), items: items, at: folder)
                guard !Task.isCancelled, reloads[key] == request, instance.folderName == folderName else { return }
                errors[instance.id] = String(appLocalized: "Не удалось прочитать реестр происхождения содержимого. Источники и обновления недоступны: \(error.localizedDescription)")
                return
            }
            guard !Task.isCancelled, reloads[key] == request, instance.folderName == folderName else { return }
            if mods {
                self.mods[instance.id] = items
                let eligible = items.filter { $0.origin?.api != nil }
                updates[instance.id] = updates[instance.id]?.filter { key, api in eligible.contains { $0.logicalName.lowercased() == key && $0.origin?.versionID != api.versionID } }
                if eligible.isEmpty { updateMessages[instance.id] = nil }
            } else { packs[instance.id] = items }
            let updatesKey = updateKey(instance, mods: mods)
            await retainUpdates(updatesKey, items: items, at: folder)
            guard !Task.isCancelled, reloads[key] == request, instance.folderName == folderName else { return }
            if clearError { errors[instance.id] = nil }
        } catch {
            if !Task.isCancelled && reloads[key] == request && instance.folderName == folderName { errors[instance.id] = error.localizedDescription }
        }
    }

    private func retainUpdates(_ key: String, items: [InstanceContentItem], at folder: URL) async {
        guard let available = modrinthUpdates[key] else { return }
        var retained: [String: ModrinthUpdate] = [:]
        for (name, update) in available {
            guard let item = items.first(where: { !$0.isDirectory && $0.logicalName.lowercased() == name }),
                  (try? await installations.content.hasHash(item, update.currentSHA512, in: folder)) == true else { continue }
            retained[name] = update
        }
        modrinthUpdates[key] = retained
    }

    private func choose(_ confirmation: ContentConfirmation) async -> ContentChoice {
        await withCheckedContinuation { continuation in
            answers[confirmation.id] = continuation; confirmations.append(confirmation)
        }
    }

    private func confirm(_ confirmation: ContentConfirmation) async -> Bool { await choose(confirmation) == .primary }

    func resolveConfirmation(_ id: UUID, accepted: Bool) { resolveConfirmation(id, choice: accepted ? .primary : .cancel) }

    func resolveConfirmation(_ id: UUID, choice: ContentChoice) {
        guard let confirmation = confirmations.first, confirmation.id == id else { return }
        confirmations.removeFirst()
        answers.removeValue(forKey: confirmation.id)?.resume(returning: choice)
    }

    @discardableResult private func perform(_ instance: GameInstance, mods: Bool, operation: @escaping @MainActor (URL) async throws -> Void) -> Bool {
        perform(instance, target: mods ? .mods : .packs, operation: operation)
    }

    @discardableResult private func perform(_ instance: GameInstance, target: ModrinthInstallTarget, operation: @escaping @MainActor (URL) async throws -> Void) -> Bool {
        if let reason = disabledReason(instance, target: target) { errors[instance.id] = reason; return false }
        let folder: URL
        do { folder = try self.folder(instance, target: target) }
        catch { errors[instance.id] = error.localizedDescription; return false }
        invalidateModrinthUpdates(instance, target: target)
        installations.contentBusy.insert(instance.id); errors[instance.id] = nil
        Task {
            defer { installations.contentBusy.remove(instance.id); installations.scheduleQueuedInstallations() }
            do { try await operation(folder); await reload(instance, target: target, clearError: false) }
            catch { errors[instance.id] = error.localizedDescription; await reload(instance, target: target, clearError: false) }
        }
        return true
    }

    func importFiles(_ sources: [URL], into instance: GameInstance, mods: Bool) {
        importFiles(sources, into: instance, target: mods ? .mods : .packs)
    }

    func importFiles(_ sources: [URL], into instance: GameInstance, target: ModrinthInstallTarget) {
        guard !sources.isEmpty else { return }
        // Keep file-picker grants alive while the batch waits for a replacement decision.
        let scoped = sources.filter { $0.startAccessingSecurityScopedResource() }
        if let reason = disabledReason(instance, target: target) {
            scoped.forEach { $0.stopAccessingSecurityScopedResource() }; errors[instance.id] = reason; return
        }
        let started = perform(instance, target: target) { [self] folder in
            defer { scoped.forEach { $0.stopAccessingSecurityScopedResource() } }
            var failures: [String] = [], imported = 0
            for source in sources {
                do {
                    if let world = target.world {
                        try await installations.content.importDatapack(from: source, world: world, in: installations.store.storage.directory(instance.folderName))
                    } else {
                        try await installations.content.importItem(from: source, into: folder, mods: target == .mods)
                    }
                    imported += 1
                }
                catch PackImportError.exists {
                    let title = target.world != nil ? String(appLocalized: "Заменить датапак?") : target == .mods ? String(appLocalized: "Заменить мод?") : String(appLocalized: "Заменить ресурспак?")
                    let message = if let world = target.world { String(appLocalized: "\(source.lastPathComponent) уже существует в мире «\(world)» сборки «\(instance.name)».") }
                        else { String(appLocalized: "\(source.lastPathComponent) уже существует в сборке «\(instance.name)».") }
                    let allowed = await confirm(.init(title: title, message: message, action: String(appLocalized: "Заменить"), destructive: true))
                    if allowed {
                        do {
                            if let world = target.world {
                                try await installations.content.importDatapack(from: source, world: world, in: installations.store.storage.directory(instance.folderName), replace: true)
                            } else {
                                try await installations.content.importItem(from: source, into: folder, mods: target == .mods, replace: true)
                            }
                            imported += 1
                        }
                        catch { failures.append("\(source.lastPathComponent): \(error.localizedDescription)") }
                    }
                } catch { failures.append("\(source.lastPathComponent): \(error.localizedDescription)") }
            }
            if imported > 0, let world = target.world {
                worldInstallMessages[instance.id, default: [:]][world] = String(appLocalized: "Изменения датапаков применятся при следующем открытии мира.")
            }
            if !failures.isEmpty { errors[instance.id] = failures.joined(separator: "\n") }
        }
        if !started { scoped.forEach { $0.stopAccessingSecurityScopedResource() } }
    }

    func setEnabled(_ item: InstanceContentItem, in instance: GameInstance, enabled: Bool, mods: Bool = true) {
        perform(instance, mods: mods) { [self] folder in
            if !enabled && item.origin?.projectID == FabricAPIDescriptor.project {
                guard await confirm(.init(title: String(appLocalized: "Отключить Fabric API?"), message: String(appLocalized: "Моды, зависящие от Fabric API, могут больше не давать игре запуститься."), action: String(appLocalized: "Отключить"), destructive: false)) else { return }
            }
            try await installations.content.setEnabled(item, in: folder, enabled: enabled, mods: mods)
        }
    }

    func setDatapackEnabled(_ item: InstanceContentItem, world: String, in instance: GameInstance, enabled: Bool) {
        if let reason = worldBlockedReason(instance) { errors[instance.id] = reason; return }
        perform(instance, target: .worldDatapacks(world)) { [self] _ in
            try await installations.content.setDatapackEnabled(item, world: world, in: installations.store.storage.directory(instance.folderName), enabled: enabled)
            worldInstallMessages[instance.id, default: [:]][world] = String(appLocalized: "Изменения датапаков применятся при следующем открытии мира.")
        }
    }

    func worldBlockedReason(_ instance: GameInstance) -> String? {
        if let reason = installations.store.managementBlockedReason(instance) { return reason }
        if instance.state != .ready { return String(appLocalized: "Дождитесь завершения установки сборки.") }
        return nil
    }

    enum WorldAction { case delete, duplicate, backup }

    func manageWorld(_ world: InstanceWorld, in instance: GameInstance, action: WorldAction) {
        if let reason = worldBlockedReason(instance) { errors[instance.id] = reason; return }
        let root: URL
        do { root = try installations.store.storage.directory(instance.folderName); _ = try InstanceWorlds.worldFolder(world: world.id, in: root) }
        catch { errors[instance.id] = error.localizedDescription; return }
        let title: String
        switch action {
        case .delete: title = String(appLocalized: "Удаляем мир…")
        case .duplicate: title = String(appLocalized: "Копируем мир…")
        case .backup: title = String(appLocalized: "Создаём резервную копию мира…")
        }
        installations.contentBusy.insert(instance.id); errors[instance.id] = nil
        worldOperations[instance.id, default: [:]][world.id] = title
        Task { [self] in
            defer {
                installations.contentBusy.remove(instance.id)
                worldOperations[instance.id]?[world.id] = nil
                worldRevisions[instance.id, default: 0] += 1
                installations.scheduleQueuedInstallations()
            }
            do {
                switch action {
                case .delete:
                    guard await confirm(.init(title: String(appLocalized: "Удалить мир?"), message: String(appLocalized: "Мир «\(world.name)» и его датапаки будут перемещены в корзину."), action: String(appLocalized: "Удалить"), destructive: true)) else { return }
                    let target = ModrinthInstallTarget.worldDatapacks(world.id), key = updateKey(instance, target: target)
                    invalidateModrinthUpdates(instance, target: target); modrinthUpdates[key] = nil; modrinthUpdateMessages[key] = nil
                    try await worlds.trash(world.id, in: root)
                    datapacks[instance.id]?[world.id] = nil; worldInstallMessages[instance.id]?[world.id] = nil
                case .duplicate:
                    _ = try await worlds.duplicate(world, in: root)
                case .backup:
                    guard try !installations.store.context.fetch(FetchDescriptor<GameInstance>()).contains(where: { $0.folderName.lowercased() == InstanceStorage.worldsFolder }) else {
                        throw InstanceFileError.message(String(appLocalized: "Папку worlds занимает сборка. Переименуйте её, чтобы сохранять резервные копии миров."))
                    }
                    let latest = try await worlds.list(in: root).first { $0.id == world.id }
                    guard let latest else { throw InstanceFileError.message(String(appLocalized: "Мир больше не существует. Обновите список миров.")) }
                    let packs = try await installations.content.datapackBackupEntries(world: world.id, in: root)
                    let source = WorldBackupManifest.SourceInstance(id: instance.id, name: instance.name, folderName: instance.folderName, minecraftVersion: instance.versionID, modLoader: instance.modLoaderRaw, loaderVersion: try instance.fabricConfiguration()?.loaderVersion)
                    let manifest = WorldBackupManifest(backupCreatedAt: Date(), name: latest.name, folderName: latest.id, saveVersion: latest.version, gameType: latest.gameType, lastPlayed: latest.lastPlayed, size: latest.size, iconPNG: latest.iconData, sourceInstance: source, datapacks: packs)
                    worldBackupURLs[instance.id] = try await WorldBackup.archive(world: world.id, in: root, manifest: manifest, into: installations.store.storage.directory(InstanceStorage.worldsFolder))
                }
            } catch { errors[instance.id] = error.localizedDescription }
        }
    }

    func delete(_ item: InstanceContentItem, in instance: GameInstance, mods: Bool) {
        perform(instance, mods: mods) { [self] folder in
            let message = mods && item.origin?.projectID == FabricAPIDescriptor.project
                ? String(appLocalized: "\(item.logicalName) будет перемещён в корзину. Моды, зависящие от Fabric API, могут больше не давать игре запуститься.")
                : String(appLocalized: "\(item.logicalName) будет перемещён в корзину.")
            guard await confirm(.init(title: mods ? String(appLocalized: "Удалить мод?") : String(appLocalized: "Удалить ресурспак?"), message: message, action: String(appLocalized: "Удалить"), destructive: true)) else { return }
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
                updateMessages[instance.id] = String(appLocalized: "Fabric API \(latest.version) требует другой версии Loader или Java. Текущая версия сохранена.")
                return
            }
            var available: [String: FabricAPIDescriptor] = [:]
            for previous in current where mods[instance.id]?.contains(where: { $0.logicalName == previous.logicalName && $0.origin?.sha512 == previous.origin?.sha512 && $0.origin?.versionID != latest.versionID }) == true {
                available[previous.logicalName.lowercased()] = latest
            }
            updates[instance.id] = available
        } catch {
            if !Task.isCancelled { updateMessages[instance.id] = String(appLocalized: "Не удалось проверить обновление Fabric API: \(error.localizedDescription)") }
        }
    }

    func update(_ item: InstanceContentItem, in instance: GameInstance) {
        guard item.origin?.api != nil, let next = updates[instance.id]?[item.logicalName.lowercased()] else { return }
        perform(instance, mods: true) { [self] folder in try await applyAPIUpdate(item, to: next, in: instance, folder: folder) }
    }

    private func applyAPIUpdate(_ item: InstanceContentItem, to next: FabricAPIDescriptor, in instance: GameInstance, folder: URL) async throws {
        guard let configuration = try instance.fabricConfiguration() else { throw InstanceFileError.message(String(appLocalized: "Конфигурация Fabric отсутствует.")) }
        let metadata = try await installations.fabricClient.metadata(for: next)
        guard try metadata.supports(loader: configuration.loaderVersion, java: instance.javaMajorVersion) else { throw InstanceFileError.message(String(appLocalized: "Обновление Fabric API несовместимо с Loader или Java сборки.")) }
        let cached = try await installations.fabricClient.cachedAPI(next)
        try await installations.content.updateAPI(item, to: next, from: cached, in: folder)
        updates[instance.id] = nil; updateMessages[instance.id] = nil
    }

    private func updateKey(_ instance: GameInstance, mods: Bool) -> String { "\(instance.id):\(mods)" }

    private func updateKey(_ instance: GameInstance, target: ModrinthInstallTarget) -> String {
        if target == .mods { return updateKey(instance, mods: true) }
        if target == .packs { return updateKey(instance, mods: false) }
        return "\(instance.id):world:\(target.world ?? "")"
    }

    private func invalidateModrinthUpdates(_ instance: GameInstance, target: ModrinthInstallTarget) {
        let key = updateKey(instance, target: target)
        modrinthCheckRequests[key] = nil
        checkingModrinth.remove(key)
        modrinthUpdateMessages[key] = nil
    }

    func isCheckingUpdates(_ instance: GameInstance, mods: Bool) -> Bool {
        isCheckingUpdates(instance, target: mods ? .mods : .packs)
    }

    func isCheckingUpdates(_ instance: GameInstance, target: ModrinthInstallTarget) -> Bool {
        checkingModrinth.contains(updateKey(instance, target: target)) || target == .mods && checkingUpdates.contains(instance.id)
    }

    func modrinthUpdate(for item: InstanceContentItem, in instance: GameInstance, mods: Bool) -> ModrinthUpdate? {
        modrinthUpdate(for: item, in: instance, target: mods ? .mods : .packs)
    }

    func modrinthUpdate(for item: InstanceContentItem, in instance: GameInstance, target: ModrinthInstallTarget) -> ModrinthUpdate? {
        modrinthUpdates[updateKey(instance, target: target)]?[item.logicalName.lowercased()]
    }

    func updateMessage(_ instance: GameInstance, target: ModrinthInstallTarget) -> String? {
        modrinthUpdateMessages[updateKey(instance, target: target)] ?? (target == .mods ? updateMessages[instance.id] : nil)
    }

    /// Fabric API остаётся на прежнем пути; остальные файлы назначения проверяются через Modrinth.
    func checkAllUpdates(_ instance: GameInstance, mods: Bool) async {
        await checkAllUpdates(instance, target: mods ? .mods : .packs)
    }

    func checkAllUpdates(_ instance: GameInstance, target: ModrinthInstallTarget) async {
        if target == .mods { await checkUpdates(instance) }
        await checkModrinthUpdates(instance, target: target)
    }

    func checkModrinthUpdates(_ instance: GameInstance, mods: Bool) async {
        await checkModrinthUpdates(instance, target: mods ? .mods : .packs)
    }

    /// Обновление предлагается, если новая версия содержит соответствующий файл и опубликована позже текущей.
    func checkModrinthUpdates(_ instance: GameInstance, target: ModrinthInstallTarget) async {
        let key = updateKey(instance, target: target)
        guard !checkingModrinth.contains(key) else { return }
        let request = UUID(), folderName = instance.folderName
        modrinthCheckRequests[key] = request; checkingModrinth.insert(key)
        modrinthUpdateMessages[key] = nil
        defer {
            if modrinthCheckRequests[key] == request {
                modrinthCheckRequests[key] = nil
                checkingModrinth.remove(key)
            }
        }
        do {
            let contentFolder = try folder(instance, target: target)
            let items = try await installations.content.list(at: contentFolder, mods: target == .mods).filter { !$0.isDirectory && $0.origin?.api == nil }
            guard !Task.isCancelled, modrinthCheckRequests[key] == request else { return }
            guard !items.isEmpty else { modrinthUpdates[key] = [:]; return }
            let matches = try await modrinth.versions(of: items.map(\.url))
            guard !Task.isCancelled, modrinthCheckRequests[key] == request else { return }
            guard !matches.isEmpty else { modrinthUpdates[key] = [:]; return }

            // Metadata enriches the local registry when a manually installed ZIP is recognized by hash.
            let versions = (try? await modrinth.versions(Array(Set(matches.values.map(\.versionID))))) ?? []
            let projects = (try? await modrinth.projects(Array(Set(matches.values.map(\.projectID))))) ?? []
            let versionsByID = Dictionary(versions.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
            let projectsByID = Dictionary(projects.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
            let attachedPackHashes = Set(items.compactMap { item -> String? in
                guard target == .packs, let match = matches[item.url] else { return nil }
                let fileType = item.origin?.fileType ?? versionsByID[match.versionID]?.files.first(where: { $0.sha512 == match.sha512 })?.fileType
                return fileType == "required-resource-pack" || fileType == "optional-resource-pack" ? match.sha512 : nil
            })
            let ordinaryHashes = Set(matches.values.map(\.sha512)).subtracting(attachedPackHashes)
            var latest: [String: ModrinthVersion] = [:]
            if !ordinaryHashes.isEmpty {
                latest.merge(try await modrinth.latestVersions(for: Array(ordinaryHashes), kind: target.kind, minecraft: instance.versionID), uniquingKeysWith: { _, next in next })
            }
            if !attachedPackHashes.isEmpty {
                latest.merge(try await modrinth.latestVersions(for: Array(attachedPackHashes), kind: .datapack, minecraft: instance.versionID), uniquingKeysWith: { _, next in next })
            }
            guard !Task.isCancelled, modrinthCheckRequests[key] == request, instance.folderName == folderName else { return }
            let currentFolder = try folder(instance, target: target)
            let currentItems = try await installations.content.list(at: currentFolder, mods: target == .mods)
            guard !Task.isCancelled, modrinthCheckRequests[key] == request, instance.folderName == folderName else { return }
            var available: [String: ModrinthUpdate] = [:], recordedOrigins = false
            for item in items {
                guard let match = matches[item.url], let current = currentItems.first(where: { $0.url == item.url }),
                      try await installations.content.hasHash(current, match.sha512, in: currentFolder) else { continue }
                let oldVersion = versionsByID[match.versionID]
                let oldFileType = item.origin?.fileType ?? oldVersion?.files.first(where: { $0.sha512 == match.sha512 })?.fileType
                if let oldVersion, let project = projectsByID[match.projectID] {
                    let origin = ModOrigin(source: .modrinth, projectID: match.projectID, versionID: oldVersion.id, pageURL: project.pageURL, sha512: match.sha512, versionNumber: oldVersion.number, iconURL: project.iconURL, fileType: oldFileType)
                    try await installations.content.recordOrigin(origin, for: current, in: currentFolder, mods: target == .mods)
                    recordedOrigins = true
                }
                guard let next = latest[match.sha512], next.projectID == match.projectID, next.published > match.published else { continue }
                let fileKind: ModrinthContentKind = attachedPackHashes.contains(match.sha512) ? .resourcePack : target.kind
                guard let file = next.updateFile(kind: fileKind, matchingFileType: oldFileType, matchingFilename: current.logicalName), file.sha512 != match.sha512 else { continue }
                available[item.logicalName.lowercased()] = .init(projectID: match.projectID, version: next, currentSHA512: match.sha512, file: file)
            }
            guard !Task.isCancelled, modrinthCheckRequests[key] == request, instance.folderName == folderName else { return }
            _ = try folder(instance, target: target)
            if recordedOrigins { await reload(instance, target: target, clearError: false) }
            guard !Task.isCancelled, modrinthCheckRequests[key] == request, instance.folderName == folderName else { return }
            modrinthUpdates[key] = available
        } catch {
            if !Task.isCancelled, modrinthCheckRequests[key] == request { modrinthUpdateMessages[key] = String(appLocalized: "Не удалось проверить обновления: \(error.localizedDescription)") }
        }
    }

    func updateFromModrinth(_ item: InstanceContentItem, in instance: GameInstance, mods: Bool) {
        updateFromModrinth(item, in: instance, target: mods ? .mods : .packs)
    }

    func updateFromModrinth(_ item: InstanceContentItem, in instance: GameInstance, target: ModrinthInstallTarget) {
        guard let update = modrinthUpdate(for: item, in: instance, target: target), catalogInstalling[instance.id] == nil else { return }
        catalogInstalling[instance.id] = update.projectID; catalogInstallTargets[instance.id] = target
        let started = perform(instance, target: target) { [self] _ in
            defer { catalogInstalling[instance.id] = nil; catalogInstallTargets[instance.id] = nil }
            guard let project = try await modrinth.projects([update.projectID]).first else { throw InstanceFileError.message(String(appLocalized: "Проект Modrinth не найден.")) }
            _ = try await installFromModrinth(project, in: instance, target: target, version: update.version, requestedFile: update.file, replacing: (item, update.currentSHA512))
        }
        if !started { catalogInstalling[instance.id] = nil; catalogInstallTargets[instance.id] = nil }
    }

    func hasUpdates(_ instance: GameInstance, mods: Bool) -> Bool {
        hasUpdates(instance, target: mods ? .mods : .packs)
    }

    func hasUpdates(_ instance: GameInstance, target: ModrinthInstallTarget) -> Bool {
        modrinthUpdates[updateKey(instance, target: target)]?.isEmpty == false || target == .mods && updates[instance.id]?.isEmpty == false
    }

    /// Ставит доступные обновления только в выбранном назначении.
    func updateAll(_ instance: GameInstance, mods: Bool) {
        updateAll(instance, target: mods ? .mods : .packs)
    }

    func updateAll(_ instance: GameInstance, target: ModrinthInstallTarget) {
        guard catalogInstalling[instance.id] == nil else { return }
        let current = items(instance, target: target)
        let apiUpdates = target == .mods ? current.compactMap { item in
            item.origin?.api == nil ? nil : updates[instance.id]?[item.logicalName.lowercased()].map { (item, $0) }
        } : []
        let modrinthUpdates = current.compactMap { item in modrinthUpdate(for: item, in: instance, target: target).map { (item, $0) } }
        guard let first = apiUpdates.first.map({ _ in FabricAPIDescriptor.project }) ?? modrinthUpdates.first?.1.projectID else { return }
        catalogInstalling[instance.id] = first; catalogInstallTargets[instance.id] = target
        let started = perform(instance, target: target) { [self] folder in
            defer { catalogInstalling[instance.id] = nil; catalogInstallTargets[instance.id] = nil }
            var failures: [String] = []
            for (item, next) in apiUpdates {
                do { try await applyAPIUpdate(item, to: next, in: instance, folder: folder) }
                catch is CancellationError { throw CancellationError() }
                catch { failures.append("\(item.logicalName): \(error.localizedDescription)") }
            }
            for (item, update) in modrinthUpdates {
                catalogInstalling[instance.id] = update.projectID
                do {
                    guard let project = try await modrinth.projects([update.projectID]).first else { throw InstanceFileError.message(String(appLocalized: "Проект Modrinth не найден.")) }
                    _ = try await installFromModrinth(project, in: instance, target: target, version: update.version, requestedFile: update.file, replacing: (item, update.currentSHA512), interactive: false)
                } catch is CancellationError { throw CancellationError() }
                catch { failures.append("\(item.logicalName): \(error.localizedDescription)") }
            }
            if !failures.isEmpty { throw InstanceFileError.message(failures.joined(separator: "\n")) }
        }
        if !started { catalogInstalling[instance.id] = nil; catalogInstallTargets[instance.id] = nil }
    }

    /// Установленные проекты Modrinth: происхождения из реестра и совпадения SHA-512 остальных файлов.
    func installedProjects(_ instance: GameInstance, mods: Bool) async throws -> [String: [InstanceContentItem]] {
        try await installedProjects(instance, target: mods ? .mods : .packs)
    }

    func installedProjects(_ instance: GameInstance, target: ModrinthInstallTarget) async throws -> [String: [InstanceContentItem]] {
        let items = try await installations.content.list(at: folder(instance, target: target), mods: target == .mods)
        var result: [String: [InstanceContentItem]] = [:], unknown: [InstanceContentItem] = []
        for item in items {
            if let origin = item.origin, origin.source == .modrinth { result[origin.projectID, default: []].append(item) }
            else if !item.isDirectory { unknown.append(item) }
        }
        let matches = try await modrinth.versions(of: unknown.map(\.url))
        for item in unknown { if let match = matches[item.url] { result[match.projectID, default: []].append(item) } }
        return result
    }

    /// `channel` — канал Modrinth (`release`, `beta`, `alpha`) устанавливаемой версии проекта; зависимости его не наследуют.
    func install(_ project: ModrinthProject, in instance: GameInstance, mods: Bool, channel: String = "release") {
        install(project, in: instance, target: mods ? .mods : .packs, channel: channel)
    }

    func install(_ project: ModrinthProject, in instance: GameInstance, target: ModrinthInstallTarget, channel: String = "release") {
        guard catalogInstalling[instance.id] == nil else { return }
        catalogInstalling[instance.id] = project.id
        catalogInstallTargets[instance.id] = target
        let started = perform(instance, target: target) { [self] _ in
            defer { catalogInstalling[instance.id] = nil; catalogInstallTargets[instance.id] = nil }
            if let world = target.world { worldInstallMessages[instance.id]?[world] = nil }
            let added = try await installFromModrinth(project, in: instance, target: target, channel: channel)
            if added, let world = target.world {
                worldInstallMessages[instance.id, default: [:]][world] = String(appLocalized: "Датапак «\(project.title)» добавлен. Он будет доступен при следующем открытии мира.")
            }
        }
        if !started { catalogInstalling[instance.id] = nil; catalogInstallTargets[instance.id] = nil }
    }

    private struct CatalogDownload {
        let project: ModrinthProject
        let version: ModrinthVersion
        let file: ModrinthVersion.File
        let target: ModrinthInstallTarget
        var mods: Bool { target == .mods }

        var origin: ModOrigin? {
            // Fabric API сохраняет дескриптор, чтобы работала проверка его обновлений.
            if project.id == FabricAPIDescriptor.project {
                return ModOrigin(api: .init(projectID: project.id, versionID: version.id, version: version.number, channel: version.channel, filename: file.filename, url: file.url, size: file.size, sha1: file.sha1, sha512: file.sha512))
            }
            return ModOrigin(source: .modrinth, projectID: project.id, versionID: version.id, pageURL: project.pageURL, sha512: file.sha512, versionNumber: version.number, iconURL: project.iconURL, fileType: file.fileType)
        }
    }

    /// С `replacing` новая версия заменяет файл сборки вместо импорта рядом с ним.
    /// Без `interactive` вместо диалогов подтверждения установка отменяется ошибкой до изменения сборки.
    private func installFromModrinth(_ project: ModrinthProject, in instance: GameInstance, target: ModrinthInstallTarget, channel: String? = nil, version requested: ModrinthVersion? = nil, requestedFile: ModrinthVersion.File? = nil, replacing: (item: InstanceContentItem, sha512: String)? = nil, interactive: Bool = true) async throws -> Bool {
        let mods = target == .mods
        let minecraft = instance.versionID
        let latest = requested == nil ? try await modrinth.latestVersion(project: project.id, kind: target.kind, minecraft: minecraft, channel: channel) : requested
        guard let version = latest, let file = requestedFile ?? version.file(kind: target.kind), file.isInstallable(kind: target.kind) else {
            let title = project.title
            let message = switch (channel, mods) {
            case ("release"?, true): String(appLocalized: "У «\(title)» нет релиза для Minecraft \(minecraft) и Fabric.")
            case ("release"?, false): String(appLocalized: "У «\(title)» нет релиза для Minecraft \(minecraft).")
            case ("beta"?, true): String(appLocalized: "У «\(title)» нет беты для Minecraft \(minecraft) и Fabric.")
            case ("beta"?, false): String(appLocalized: "У «\(title)» нет беты для Minecraft \(minecraft).")
            case ("alpha"?, true): String(appLocalized: "У «\(title)» нет альфы для Minecraft \(minecraft) и Fabric.")
            case ("alpha"?, false): String(appLocalized: "У «\(title)» нет альфы для Minecraft \(minecraft).")
            case (_, true): String(appLocalized: "У «\(title)» нет версии для Minecraft \(minecraft) и Fabric.")
            case (_, false): String(appLocalized: "У «\(title)» нет версии для Minecraft \(minecraft).")
            }
            throw InstanceFileError.message(message)
        }
        let modsAvailable = instance.modLoader == .fabric && instance.state != .queued && instance.state != .installing
        var installed: [ModrinthInstallTarget: [String: [InstanceContentItem]]] = [:]
        var downloads: [CatalogDownload] = [], enable: [(item: InstanceContentItem, target: ModrinthInstallTarget)] = [], listed: [ContentConfirmation.Project] = []
        var pending = [version], seen: Set<String> = [project.id]
        // Обязательные зависимости обходятся в ширину, включая зависимости зависимостей.
        while !pending.isEmpty && seen.count < 30 {
            let required = (pending.removeFirst().dependencies ?? []).filter { $0.type == "required" }
            var ids = required.compactMap(\.projectID)
            ids += try await modrinth.versions(required.filter { $0.projectID == nil }.compactMap(\.versionID)).map(\.projectID)
            for dependency in try await modrinth.projects(ids.filter { seen.insert($0).inserted }) {
                let entry = { (note: String?) in ContentConfirmation.Project(id: dependency.id, title: dependency.title, iconURL: dependency.iconURL, note: note) }
                var dependencyTarget: ModrinthInstallTarget?, next: ModrinthVersion?
                if target.world != nil, dependency.projectType == "mod" || dependency.projectType == "datapack" || dependency.allProjectTypes.contains("datapack") {
                    next = try await modrinth.latestVersion(project: dependency.id, kind: .datapack, minecraft: minecraft)
                    if next != nil || dependency.projectType == "datapack" || dependency.allProjectTypes.contains("datapack") { dependencyTarget = target }
                }
                if dependencyTarget == nil {
                    if dependency.projectType == "mod" { dependencyTarget = .mods }
                    if dependency.projectType == "resourcepack" { dependencyTarget = .packs }
                }
                guard let dependencyTarget else { listed.append(entry(String(appLocalized: "Не поддерживается Hako"))); continue }
                guard dependencyTarget != .mods || modsAvailable else { listed.append(entry(String(appLocalized: "Нужна сборка с Fabric"))); continue }
                if installed[dependencyTarget] == nil { installed[dependencyTarget] = try await installedProjects(instance, target: dependencyTarget) }
                if let present = installed[dependencyTarget]?[dependency.id] {
                    guard !present.contains(where: \.enabled), let disabled = present.first else { continue }
                    enable.append((disabled, dependencyTarget)); listed.append(entry(String(appLocalized: "Отключён — будет включён")))
                    continue
                }
                if next == nil { next = try await modrinth.latestVersion(project: dependency.id, kind: dependencyTarget.kind, minecraft: minecraft) }
                guard let next, let nextFile = next.file(kind: dependencyTarget.kind) else {
                    listed.append(entry(String(appLocalized: "Нет совместимой версии"))); continue
                }
                downloads.append(.init(project: dependency, version: next, file: nextFile, target: dependencyTarget)); listed.append(entry(nil)); pending.append(next)
            }
        }
        if !listed.isEmpty {
            guard interactive else { throw InstanceFileError.message(String(appLocalized: "«\(project.title)» требует подтверждения — обновите отдельно.")) }
            let alternative = replacing != nil ? String(appLocalized: "Только обновить") : target.kind == .datapack ? String(appLocalized: "Только датапак") : mods ? String(appLocalized: "Только мод") : String(appLocalized: "Только ресурспак")
            let message = if let world = target.world { String(appLocalized: "Для работы «\(project.title)» в мире «\(world)» сборки «\(instance.name)» нужны:") } else { String(appLocalized: "Для работы «\(project.title)» в сборке «\(instance.name)» нужны:") }
            let choice = await choose(.init(title: String(appLocalized: "Нужны зависимости"), message: message, action: downloads.isEmpty && enable.isEmpty ? nil : String(appLocalized: "Добавить с зависимостями"), destructive: false, alternative: alternative, projects: listed))
            if choice == .cancel { return false }
            if choice == .alternative { downloads = []; enable = [] }
        }
        downloads.append(.init(project: project, version: version, file: file, target: target))
        for datapack in downloads where datapack.target.kind == .datapack {
            let resources = datapack.version.resourcePacks
            guard !resources.isEmpty else { continue }
            guard interactive else { throw InstanceFileError.message(String(appLocalized: "«\(project.title)» требует подтверждения — обновите отдельно.")) }
            let options = resources.map { resource in
                ContentConfirmation.Project(id: resource.filename, title: resource.filename, iconURL: nil, note: resource.fileType == "required-resource-pack" ? String(appLocalized: "Обязательный ресурспак") : String(appLocalized: "Необязательный ресурспак"))
            }
            let choice = await choose(.init(
                title: String(appLocalized: "Ресурспак для датапака"),
                message: String(appLocalized: "У «\(datapack.project.title)» есть связанные ресурспаки. Выберите один вариант для сборки «\(instance.name)». После установки включите его в настройках Minecraft."),
                action: String(appLocalized: "Добавить с ресурспаком"), destructive: false,
                alternative: String(appLocalized: "Без ресурспака"), resourcePacks: options))
            if choice == .cancel { return false }
            if choice == .alternative { continue }
            let selected: ModrinthVersion.File?
            if case .resourcePack(let filename) = choice { selected = resources.first { $0.filename == filename } }
            else { selected = resources.first }
            guard let selected else { return false }
            let present = try await installations.content.list(at: folder(instance, mods: false), mods: false)
            let matches = try await modrinth.versions(of: present.filter { !$0.isDirectory }.map(\.url))
            if let matching = present.first(where: { matches[$0.url]?.sha512 == selected.sha512 }) {
                if !matching.enabled { enable.append((matching, .packs)) }
            } else {
                downloads.append(.init(project: datapack.project, version: datapack.version, file: selected, target: .packs))
            }
        }
        // Все файлы загружаются и проверяются до того, как сборка изменится.
        let staging = FileManager.default.temporaryDirectory.appendingPathComponent("Hako-Modrinth-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: staging) }
        let loader = try instance.fabricConfiguration()?.loaderVersion
        var files: [URL] = [], warnings: [ContentConfirmation.Project] = []
        for download in downloads {
            let url = try await modrinth.download(download.file, into: staging.appendingPathComponent(UUID().uuidString))
            if download.target.kind == .datapack {
                do {
                    let metadata = try await FabricClient.archiveEntry("pack.mcmeta", in: url, limit: 1_048_576)
                    guard let json = try JSONSerialization.jsonObject(with: metadata) as? [String: Any], json["pack"] is [String: Any] else { throw CocoaError(.fileReadCorruptFile) }
                } catch is CancellationError { throw CancellationError() }
                catch {
                    throw InstanceFileError.message(String(appLocalized: "В датапаке «\(download.project.title)» нет корректного pack.mcmeta."))
                }
            }
            if download.mods, let loader {
                let notes = await compatibilityNotes(url, loader: loader, java: instance.javaMajorVersion)
                if !notes.isEmpty { warnings.append(.init(id: download.project.id, title: download.project.title, iconURL: download.project.iconURL, note: notes.joined(separator: "; "))) }
            }
            files.append(url)
        }
        // Конфликты, которые объявляют новые версии, с уже включёнными проектами сборки.
        var conflicts: [String: String] = [:]
        for download in downloads {
            let incompatible = (download.version.dependencies ?? []).filter { $0.type == "incompatible" }
            var ids = incompatible.compactMap(\.projectID)
            ids += try await modrinth.versions(incompatible.filter { $0.projectID == nil }.compactMap(\.versionID)).map(\.projectID)
            for id in ids {
                let targets: [ModrinthInstallTarget] = [.mods, .packs] + (target.world == nil ? [] : [target])
                for kind in targets {
                    if installed[kind] == nil { installed[kind] = try await installedProjects(instance, target: kind) }
                    if installed[kind]?[id]?.contains(where: \.enabled) == true { conflicts[id] = download.project.title }
                }
            }
        }
        for conflict in try await modrinth.projects(Array(conflicts.keys)) {
            warnings.append(.init(id: conflict.id, title: conflict.title, iconURL: conflict.iconURL, note: String(appLocalized: "Несовместим с «\(conflicts[conflict.id] ?? project.title)»")))
        }
        if !warnings.isEmpty {
            guard interactive else { throw InstanceFileError.message(String(appLocalized: "«\(project.title)» требует подтверждения — обновите отдельно.")) }
            let message = replacing == nil
                ? String(appLocalized: "Эти проблемы могут помешать запуску игры. Установить «\(project.title)» всё равно?")
                : String(appLocalized: "Эти проблемы могут помешать запуску игры. Обновить «\(project.title)» всё равно?")
            let action = replacing == nil ? String(appLocalized: "Установить всё равно") : String(appLocalized: "Обновить всё равно")
            guard await confirm(.init(title: String(appLocalized: "Возможна несовместимость"), message: message, action: action, destructive: true, projects: warnings)) else { return false }
        }
        _ = try folder(instance, target: target)
        var added = false
        for (download, url) in zip(downloads, files) {
            let destination = try folder(instance, target: download.target)
            if let replacing, download.project.id == project.id, download.target == target {
                if let world = target.world {
                    try await installations.content.replaceDatapack(replacing.item, from: url, filename: url.lastPathComponent, world: world, in: installations.store.storage.directory(instance.folderName), expectedSHA512: replacing.sha512, origin: download.origin)
                } else {
                    try await installations.content.replaceItem(replacing.item, from: url, filename: url.lastPathComponent, in: destination, mods: mods, expectedSHA512: replacing.sha512, origin: download.origin)
                }
                modrinthUpdates[updateKey(instance, target: target)]?.removeValue(forKey: replacing.item.logicalName.lowercased())
                continue
            }
            do { try await publish(download, from: url, in: instance) }
            catch PackImportError.exists {
                let title = download.target.kind == .datapack ? String(appLocalized: "Заменить датапак?") : download.mods ? String(appLocalized: "Заменить мод?") : String(appLocalized: "Заменить ресурспак?")
                let message = if let world = download.target.world { String(appLocalized: "\(url.lastPathComponent) уже существует в мире «\(world)» сборки «\(instance.name)».") } else { String(appLocalized: "\(url.lastPathComponent) уже существует в сборке «\(instance.name)».") }
                guard await confirm(.init(title: title, message: message, action: String(appLocalized: "Заменить"), destructive: true)) else { continue }
                try await publish(download, from: url, in: instance, replace: true)
            }
            if download.project.id == project.id, download.target == target { added = true }
        }
        for (item, itemTarget) in enable {
            if let world = itemTarget.world { try await installations.content.setDatapackEnabled(item, world: world, in: installations.store.storage.directory(instance.folderName), enabled: true) }
            else { try await installations.content.setEnabled(item, in: folder(instance, target: itemTarget), enabled: true, mods: itemTarget == .mods) }
        }
        let changed = Set(downloads.map(\.target) + enable.map(\.target))
        for other in changed where other != target { await reload(instance, target: other, clearError: false) }
        return added
    }

    private func publish(_ download: CatalogDownload, from url: URL, in instance: GameInstance, replace: Bool = false) async throws {
        if let world = download.target.world {
            try await installations.content.importDatapack(from: url, world: world, in: installations.store.storage.directory(instance.folderName), replace: replace, origin: download.origin)
        } else {
            try await installations.content.importItem(from: url, into: folder(instance, target: download.target), mods: download.mods, replace: replace, origin: download.origin)
        }
    }

    /// Требования fabric.mod.json к Loader и Java, которым сборка не отвечает.
    /// Без fabric.mod.json или с нечитаемым требованием проверка пропускается.
    private func compatibilityNotes(_ file: URL, loader: String, java: Int) async -> [String] {
        guard let data = try? await FabricClient.archiveEntry("fabric.mod.json", in: file, limit: 1_048_576),
              let metadata = try? JSONDecoder().decode(FabricModMetadata.self, from: data) else { return [] }
        var notes: [String] = []
        if let predicate = metadata.depends?["fabricloader"], (try? predicate.matches(loader)) == false {
            notes.append(String(appLocalized: "Нужен Fabric Loader \(predicate.alternatives.formatted(.list(type: .or).locale(AppLanguage.current.locale))), в сборке \(loader)"))
        }
        if java > 0, let predicate = metadata.depends?["java"], (try? predicate.matches(String(java))) == false {
            notes.append(String(appLocalized: "Нужна Java \(predicate.alternatives.formatted(.list(type: .or).locale(AppLanguage.current.locale))), в сборке \(java)"))
        }
        return notes
    }
}

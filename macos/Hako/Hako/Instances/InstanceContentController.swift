import Foundation
import Observation

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
}

enum ContentChoice { case cancel, primary, alternative }

/// Новая версия Modrinth для файла сборки.
struct ModrinthUpdate {
    let projectID: String
    let version: ModrinthVersion
    let currentSHA512: String
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
    /// Обновления модов и ресурспаков из Modrinth по ключу раздела `updateKey`, внутри — по имени файла.
    private(set) var modrinthUpdates: [String: [String: ModrinthUpdate]] = [:]
    private(set) var checkingModrinth: Set<String> = []
    var modrinthUpdateMessages: [String: String] = [:]
    private(set) var confirmations: [ContentConfirmation] = []
    /// Проект Modrinth, который сейчас устанавливается в сборку.
    private(set) var catalogInstalling: [UUID: String] = [:]
    let modrinth: ModrinthClient
    @ObservationIgnored private var answers: [UUID: CheckedContinuation<ContentChoice, Never>] = [:]
    @ObservationIgnored private var reloads: [String: UUID] = [:]

    init(installations: InstallationCoordinator, modrinth: ModrinthClient = .init()) { self.installations = installations; self.modrinth = modrinth }
    var confirmation: ContentConfirmation? { confirmations.first }

    func folder(_ instance: GameInstance, mods: Bool) throws -> URL {
        try InstanceStorage.containedURL("minecraft/\(mods ? "mods" : instance.legacyTexturepacks ? "texturepacks" : "resourcepacks")", in: installations.store.storage.directory(instance.folderName))
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
                guard mods else { throw error }
                items = try await installations.content.list(at: folder, mods: mods, readOrigins: false)
                guard !Task.isCancelled, reloads[key] == request, instance.folderName == folderName else { return }
                errors[instance.id] = String(appLocalized: "Не удалось прочитать реестр модов. Источники и обновления недоступны: \(error.localizedDescription)")
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
            let key = updateKey(instance, mods: mods)
            modrinthUpdates[key] = modrinthUpdates[key]?.filter { name, _ in items.contains { $0.logicalName.lowercased() == name } }
            if clearError { errors[instance.id] = nil }
        } catch {
            if !Task.isCancelled && reloads[key] == request && instance.folderName == folderName { errors[instance.id] = error.localizedDescription }
        }
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
                    let allowed = await confirm(.init(title: mods ? String(appLocalized: "Заменить мод?") : String(appLocalized: "Заменить ресурспак?"), message: String(appLocalized: "\(source.lastPathComponent) уже существует в сборке «\(instance.name)»."), action: String(appLocalized: "Заменить"), destructive: true))
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
                guard await confirm(.init(title: String(appLocalized: "Отключить Fabric API?"), message: String(appLocalized: "Моды, зависящие от Fabric API, могут больше не давать игре запуститься."), action: String(appLocalized: "Отключить"), destructive: false)) else { return }
            }
            try await installations.content.setEnabled(item, in: folder, enabled: enabled, mods: mods)
        }
    }

    func delete(_ item: InstanceContentItem, in instance: GameInstance, mods: Bool) {
        perform(instance, mods: mods) { [self] folder in
            let message = item.origin?.projectID == FabricAPIDescriptor.project
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
        perform(instance, mods: true) { [self] folder in
            guard let configuration = try instance.fabricConfiguration() else { throw InstanceFileError.message(String(appLocalized: "Конфигурация Fabric отсутствует.")) }
            let metadata = try await installations.fabricClient.metadata(for: next)
            guard try metadata.supports(loader: configuration.loaderVersion, java: instance.javaMajorVersion) else { throw InstanceFileError.message(String(appLocalized: "Обновление Fabric API несовместимо с Loader или Java сборки.")) }
            let cached = try await installations.fabricClient.cachedAPI(next)
            try await installations.content.updateAPI(item, to: next, from: cached, in: folder)
            updates[instance.id] = nil; updateMessages[instance.id] = nil
        }
    }

    private func updateKey(_ instance: GameInstance, mods: Bool) -> String { "\(instance.id):\(mods)" }

    func isCheckingUpdates(_ instance: GameInstance, mods: Bool) -> Bool {
        checkingModrinth.contains(updateKey(instance, mods: mods)) || mods && checkingUpdates.contains(instance.id)
    }

    func modrinthUpdate(for item: InstanceContentItem, in instance: GameInstance, mods: Bool) -> ModrinthUpdate? {
        modrinthUpdates[updateKey(instance, mods: mods)]?[item.logicalName.lowercased()]
    }

    /// Fabric API проверяется прежним путём, остальные файлы раздела — через Modrinth.
    func checkAllUpdates(_ instance: GameInstance, mods: Bool) async {
        if mods { await checkUpdates(instance) }
        await checkModrinthUpdates(instance, mods: mods)
    }

    /// Обновление предлагается, если новая версия содержит другой файл и опубликована позже текущей.
    func checkModrinthUpdates(_ instance: GameInstance, mods: Bool) async {
        let key = updateKey(instance, mods: mods)
        guard !checkingModrinth.contains(key) else { return }
        let items = ((mods ? self.mods : packs)[instance.id] ?? []).filter { !$0.isDirectory && $0.origin?.api == nil }
        guard !items.isEmpty else { modrinthUpdates[key] = [:]; return }
        checkingModrinth.insert(key); modrinthUpdateMessages[key] = nil
        defer { checkingModrinth.remove(key) }
        do {
            let matches = try await modrinth.versions(of: items.map(\.url))
            let latest = try await modrinth.latestVersions(for: Array(Set(matches.values.map(\.sha512))), mods: mods, minecraft: instance.versionID)
            guard !Task.isCancelled else { return }
            var available: [String: ModrinthUpdate] = [:]
            for item in items {
                guard let match = matches[item.url], let next = latest[match.sha512], next.projectID == match.projectID,
                      let file = next.file(mods: mods), file.sha512 != match.sha512, next.published > match.published else { continue }
                available[item.logicalName.lowercased()] = .init(projectID: match.projectID, version: next, currentSHA512: match.sha512)
            }
            modrinthUpdates[key] = available
        } catch {
            if !Task.isCancelled { modrinthUpdateMessages[key] = String(appLocalized: "Не удалось проверить обновления: \(error.localizedDescription)") }
        }
    }

    func updateFromModrinth(_ item: InstanceContentItem, in instance: GameInstance, mods: Bool) {
        guard let update = modrinthUpdate(for: item, in: instance, mods: mods), catalogInstalling[instance.id] == nil else { return }
        catalogInstalling[instance.id] = update.projectID
        let started = perform(instance, mods: mods) { [self] _ in
            defer { catalogInstalling[instance.id] = nil }
            guard let project = try await modrinth.projects([update.projectID]).first else { throw InstanceFileError.message(String(appLocalized: "Проект Modrinth не найден.")) }
            try await installFromModrinth(project, in: instance, mods: mods, version: update.version, replacing: (item, update.currentSHA512))
        }
        if !started { catalogInstalling[instance.id] = nil }
    }

    /// Установленные проекты Modrinth: происхождения из реестра и совпадения SHA-512 остальных файлов.
    func installedProjects(_ instance: GameInstance, mods: Bool) async throws -> [String: [InstanceContentItem]] {
        let items = try await installations.content.list(at: folder(instance, mods: mods), mods: mods)
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
        guard catalogInstalling[instance.id] == nil else { return }
        catalogInstalling[instance.id] = project.id
        let started = perform(instance, mods: mods) { [self] _ in
            defer { catalogInstalling[instance.id] = nil }
            try await installFromModrinth(project, in: instance, mods: mods, channel: channel)
        }
        if !started { catalogInstalling[instance.id] = nil }
    }

    private struct CatalogDownload {
        let project: ModrinthProject
        let version: ModrinthVersion
        let file: ModrinthVersion.File
        let mods: Bool

        var origin: ModOrigin? {
            guard mods else { return nil }
            // Fabric API сохраняет дескриптор, чтобы работала проверка его обновлений.
            if project.id == FabricAPIDescriptor.project {
                return ModOrigin(api: .init(projectID: project.id, versionID: version.id, version: version.number, channel: version.channel, filename: file.filename, url: file.url, size: file.size, sha1: file.sha1, sha512: file.sha512))
            }
            return ModOrigin(source: .modrinth, projectID: project.id, versionID: version.id, pageURL: project.pageURL, sha512: file.sha512)
        }
    }

    /// С `replacing` новая версия заменяет файл сборки вместо импорта рядом с ним.
    private func installFromModrinth(_ project: ModrinthProject, in instance: GameInstance, mods: Bool, channel: String? = nil, version requested: ModrinthVersion? = nil, replacing: (item: InstanceContentItem, sha512: String)? = nil) async throws {
        let minecraft = instance.versionID
        let latest = requested == nil ? try await modrinth.latestVersion(project: project.id, mods: mods, minecraft: minecraft, channel: channel) : requested
        guard let version = latest, let file = version.file(mods: mods) else {
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
        var installed: [Bool: [String: [InstanceContentItem]]] = [:]
        var downloads: [CatalogDownload] = [], enable: [(item: InstanceContentItem, mods: Bool)] = [], listed: [ContentConfirmation.Project] = []
        var pending = [version], seen: Set<String> = [project.id]
        // Обязательные зависимости обходятся в ширину, включая зависимости зависимостей.
        while !pending.isEmpty && seen.count < 30 {
            let required = (pending.removeFirst().dependencies ?? []).filter { $0.type == "required" }
            var ids = required.compactMap(\.projectID)
            ids += try await modrinth.versions(required.filter { $0.projectID == nil }.compactMap(\.versionID)).map(\.projectID)
            for dependency in try await modrinth.projects(ids.filter { seen.insert($0).inserted }) {
                let dependencyMods = dependency.projectType == "mod"
                let entry = { (note: String?) in ContentConfirmation.Project(id: dependency.id, title: dependency.title, iconURL: dependency.iconURL, note: note) }
                guard dependencyMods || dependency.projectType == "resourcepack" else { listed.append(entry(String(appLocalized: "Не поддерживается Hako"))); continue }
                guard !dependencyMods || modsAvailable else { listed.append(entry(String(appLocalized: "Нужна сборка с Fabric"))); continue }
                if installed[dependencyMods] == nil { installed[dependencyMods] = try await installedProjects(instance, mods: dependencyMods) }
                if let present = installed[dependencyMods]?[dependency.id] {
                    guard !present.contains(where: \.enabled), let disabled = present.first else { continue }
                    enable.append((disabled, dependencyMods)); listed.append(entry(String(appLocalized: "Отключён — будет включён")))
                    continue
                }
                guard let next = try await modrinth.latestVersion(project: dependency.id, mods: dependencyMods, minecraft: minecraft), let nextFile = next.file(mods: dependencyMods) else {
                    listed.append(entry(String(appLocalized: "Нет совместимой версии"))); continue
                }
                downloads.append(.init(project: dependency, version: next, file: nextFile, mods: dependencyMods)); listed.append(entry(nil)); pending.append(next)
            }
        }
        if !listed.isEmpty {
            let choice = await choose(.init(title: String(appLocalized: "Нужны зависимости"), message: String(appLocalized: "Для работы «\(project.title)» в сборке «\(instance.name)» нужны:"), action: downloads.isEmpty && enable.isEmpty ? nil : String(appLocalized: "Добавить с зависимостями"), destructive: false, alternative: replacing != nil ? String(appLocalized: "Только обновить") : mods ? String(appLocalized: "Только мод") : String(appLocalized: "Только ресурспак"), projects: listed))
            if choice == .cancel { return }
            if choice == .alternative { downloads = []; enable = [] }
        }
        downloads.append(.init(project: project, version: version, file: file, mods: mods))
        // Все файлы загружаются и проверяются до того, как сборка изменится.
        let staging = FileManager.default.temporaryDirectory.appendingPathComponent("Hako-Modrinth-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: staging) }
        let loader = try instance.fabricConfiguration()?.loaderVersion
        var files: [URL] = [], warnings: [ContentConfirmation.Project] = []
        for download in downloads {
            let url = try await modrinth.download(download.file, into: staging.appendingPathComponent(UUID().uuidString))
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
                for kind in [true, false] {
                    if installed[kind] == nil { installed[kind] = try await installedProjects(instance, mods: kind) }
                    if installed[kind]?[id]?.contains(where: \.enabled) == true { conflicts[id] = download.project.title }
                }
            }
        }
        for conflict in try await modrinth.projects(Array(conflicts.keys)) {
            warnings.append(.init(id: conflict.id, title: conflict.title, iconURL: conflict.iconURL, note: String(appLocalized: "Несовместим с «\(conflicts[conflict.id] ?? project.title)»")))
        }
        if !warnings.isEmpty {
            let message = replacing == nil
                ? String(appLocalized: "Эти проблемы могут помешать запуску игры. Установить «\(project.title)» всё равно?")
                : String(appLocalized: "Эти проблемы могут помешать запуску игры. Обновить «\(project.title)» всё равно?")
            let action = replacing == nil ? String(appLocalized: "Установить всё равно") : String(appLocalized: "Обновить всё равно")
            guard await confirm(.init(title: String(appLocalized: "Возможна несовместимость"), message: message, action: action, destructive: true, projects: warnings)) else { return }
        }
        for (download, url) in zip(downloads, files) {
            let target = try folder(instance, mods: download.mods)
            if let replacing, download.project.id == project.id, download.mods == mods {
                try await installations.content.replaceItem(replacing.item, from: url, filename: url.lastPathComponent, in: target, mods: mods, expectedSHA512: replacing.sha512, origin: download.origin)
                modrinthUpdates[updateKey(instance, mods: mods)]?.removeValue(forKey: replacing.item.logicalName.lowercased())
                continue
            }
            do { try await installations.content.importItem(from: url, into: target, mods: download.mods, origin: download.origin) }
            catch PackImportError.exists {
                guard await confirm(.init(title: download.mods ? String(appLocalized: "Заменить мод?") : String(appLocalized: "Заменить ресурспак?"), message: String(appLocalized: "\(url.lastPathComponent) уже существует в сборке «\(instance.name)»."), action: String(appLocalized: "Заменить"), destructive: true)) else { continue }
                try await installations.content.importItem(from: url, into: target, mods: download.mods, replace: true, origin: download.origin)
            }
        }
        for (item, itemMods) in enable { try await installations.content.setEnabled(item, in: folder(instance, mods: itemMods), enabled: true, mods: itemMods) }
        if !mods && (downloads.contains(where: \.mods) || enable.contains(where: \.mods)) { await reload(instance, mods: true, clearError: false) }
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

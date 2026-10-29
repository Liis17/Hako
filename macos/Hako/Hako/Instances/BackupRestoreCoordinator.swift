import Foundation
import Observation
import SwiftData

struct LauncherPresentationState: Equatable {
    private(set) var isExplicitGuest = false
    private(set) var isRestoreLauncher = false
    private(set) var isQuickLaunchActive = false
    private(set) var hasMicrosoftSession = false
    private(set) var hasHakoSession = false

    var isGuestLauncher: Bool { isExplicitGuest || isRestoreLauncher || isQuickLaunchActive }
    var hasProviderSession: Bool { hasMicrosoftSession || hasHakoSession }

    mutating func continueAsGuest() { isExplicitGuest = true }
    mutating func restoreRequested() { isRestoreLauncher = true }
    mutating func quickLaunchRequested() { isQuickLaunchActive = true }
    mutating func quickLaunchEnded() { isQuickLaunchActive = false }

    mutating func providersChanged(microsoftSession: Bool, hakoSession: Bool, restoreVisible: Bool) {
        hasMicrosoftSession = microsoftSession
        hasHakoSession = hakoSession
        if hasProviderSession {
            isExplicitGuest = false
            isRestoreLauncher = false
        } else {
            isRestoreLauncher = restoreVisible
        }
    }

    mutating func accountsChanged(hasAccount: Bool, restoreVisible: Bool) {
        providersChanged(microsoftSession: hasAccount, hakoSession: false, restoreVisible: restoreVisible)
    }
}

nonisolated enum InstanceRestoreAction: String, Sendable {
    case newInstance
    case replace
}

nonisolated enum WorldRestoreAction: String, Sendable {
    case copy
    case replace
}

@MainActor struct RestoreNavigation: Equatable {
    let id: UUID
    let instanceID: UUID
    let world: String?
}

@MainActor @Observable final class BackupRestoreCoordinator {
    private struct QueueItem {
        let id: UUID
        let url: URL
        let keys: Set<String>
    }

    let installations: InstallationCoordinator
    let content: InstanceContentController
    private(set) var staged: StagedRestoreArchive?
    private(set) var currentURL: URL?
    private(set) var isPreparing = false
    private(set) var isCommitting = false
    private(set) var isCleaningStage = false
    private(set) var errorMessage: String?
    private(set) var completionMessage: String?
    private(set) var presentationRequestID: UUID?
    var instanceAction: InstanceRestoreAction = .newInstance
    var instanceTargetID: UUID?
    var instanceName = ""
    var worldTargetID: UUID?
    var worldAction: WorldRestoreAction = .copy {
        didSet { if !applyingWorldDefaults { worldActionWasEdited = true } }
    }
    var worldName = "" {
        didSet { if !applyingWorldDefaults { worldNameWasEdited = true } }
    }
    var worldFolderName = "" {
        didSet { if !applyingWorldDefaults { worldFolderWasEdited = true } }
    }
    var awaitingConfirmation = false
    private(set) var navigation: RestoreNavigation?

    @ObservationIgnored private let worldMove: (URL, URL) throws -> Void
    @ObservationIgnored private var queue: [QueueItem] = []
    @ObservationIgnored private var active: QueueItem?
    @ObservationIgnored private var identifiers: Set<String> = []
    @ObservationIgnored private var preparationTask: Task<Void, Never>?
    @ObservationIgnored private var windowPresenter: (() -> Void)?
    @ObservationIgnored private var preserveStage = false
    @ObservationIgnored private var navigationAfterQueue: RestoreNavigation?
    @ObservationIgnored private var applyingWorldDefaults = false
    @ObservationIgnored private var worldActionWasEdited = false
    @ObservationIgnored private var worldNameWasEdited = false
    @ObservationIgnored private var worldFolderWasEdited = false

    init(installations: InstallationCoordinator, content: InstanceContentController, worldMove: ((URL, URL) throws -> Void)? = nil) {
        self.installations = installations
        self.content = content
        self.worldMove = worldMove ?? { try FileManager.default.moveItem(at: $0, to: $1) }
        let root = installations.store.storage.root
        Task { await RestoreArchive.cleanAbandonedStaging(under: root) }
    }

    var isPresented: Bool { currentURL != nil }
    var isQueueing: Bool { !queue.isEmpty }
    var shouldPresentRestoreLauncher: Bool {
        isPresented || isQueueing || isCleaningStage || completionMessage != nil || navigation != nil
    }
    var queuedCount: Int { queue.count }
    var archiveName: String { currentURL?.lastPathComponent ?? "" }
    var currentKind: RestoreArchiveKind? { staged?.kind }

    func registerWindowPresenter(_ presenter: @escaping () -> Void) {
        windowPresenter = presenter
        if currentURL != nil || !queue.isEmpty { presenter() }
    }

    func enqueue(_ urls: [URL]) {
        for url in urls {
            let fileExtension = url.pathExtension.lowercased()
            guard fileExtension == InstanceBackupManifest.fileExtension || fileExtension == WorldBackupManifest.fileExtension else {
                if currentURL == nil {
                    currentURL = url
                    errorMessage = String(appLocalized: "Выберите файл .hakobackup или .hakoworld.")
                }
                continue
            }
            let keys = Self.identities(of: url)
            guard identifiers.isDisjoint(with: keys) else { continue }
            identifiers.formUnion(keys)
            queue.append(.init(id: UUID(), url: url, keys: keys))
        }
        if currentURL != nil || !queue.isEmpty { requestWindowPresentation() }
        beginNextIfNeeded(presentWindow: false)
    }

    private func requestWindowPresentation() {
        presentationRequestID = UUID()
        windowPresenter?()
    }

    func consumePresentationRequest(_ id: UUID) {
        guard presentationRequestID == id else { return }
        presentationRequestID = nil
    }

    func cancelOrDismiss() {
        guard !isCommitting, !isCleaningStage else { return }
        if let preparationTask {
            preparationTask.cancel()
            return
        }
        finishCurrent()
    }

    func requestCommit() {
        guard staged != nil, !isPreparing, !isCommitting, errorMessage == nil else { return }
        if needsConfirmation {
            awaitingConfirmation = true
        } else {
            commitConfirmed()
        }
    }

    func commitConfirmed() {
        guard let staged, !isPreparing, !isCommitting, errorMessage == nil else { return }
        awaitingConfirmation = false
        isCommitting = true
        completionMessage = nil
        Task { [weak self] in await self?.commit(staged) }
    }

    func dismissCompletionMessage() { completionMessage = nil }

    func completeNavigation(_ id: UUID) {
        guard navigation?.id == id else { return }
        navigation = nil
    }

    func cancelNavigation(_ id: UUID) {
        if navigationAfterQueue?.id == id { navigationAfterQueue = nil }
        if navigation?.id == id { navigation = nil }
    }

    var confirmationTitle: String {
        switch currentKind {
        case .instance: String(appLocalized: "Заменить сборку?")
        case .world: String(appLocalized: "Восстановить мир?")
        case nil: ""
        }
    }

    var confirmationMessage: String {
        guard let staged else { return "" }
        switch staged.kind {
        case .instance:
            return String(appLocalized: "Все текущие файлы выбранной сборки будут заменены без резервной копии. Имя, папка, дата создания и время игры сохранятся.")
        case .world(let manifest):
            var details: [String] = []
            if worldTargetID != manifest.sourceInstance.id { details.append(String(appLocalized: "Мир будет установлен в другую сборку.")) }
            if let target = selectedWorldTarget, target.versionID != manifest.sourceInstance.minecraftVersion {
                details.append(String(appLocalized: "Версия Minecraft отличается: в копии \(manifest.sourceInstance.minecraftVersion), в сборке \(target.versionID)."))
            }
            if let target = selectedWorldTarget, target.modLoaderRaw != manifest.sourceInstance.modLoader {
                details.append(String(appLocalized: "Загрузчик отличается: в копии \(manifest.sourceInstance.modLoader), в сборке \(target.loaderTitle)."))
            } else if let target = selectedWorldTarget,
                      (try? target.fabricConfiguration()?.loaderVersion) != manifest.sourceInstance.loaderVersion {
                let loader = (try? target.fabricConfiguration())?.loaderVersion ?? "—"
                details.append(String(appLocalized: "Версия Fabric Loader отличается: в копии \(manifest.sourceInstance.loaderVersion ?? "—"), в сборке \(loader)."))
            }
            if worldAction == .replace { details.append(String(appLocalized: "Существующая папка мира будет заменена без резервной копии.")) }
            return details.joined(separator: "\n")
        }
    }

    var matchedInstance: GameInstance? {
        guard case .instance(let manifest) = staged?.kind else { return nil }
        let instances = (try? installations.store.context.fetch(FetchDescriptor<GameInstance>())) ?? []
        return Self.matchingInstance(for: manifest, among: instances)
    }

    var isLegacyInstanceMatch: Bool {
        guard case .instance(let manifest) = staged?.kind else { return false }
        return manifest.instanceID == nil && matchedInstance != nil
    }

    static func matchingInstance(for manifest: InstanceBackupManifest, among instances: [GameInstance]) -> GameInstance? {
        if let id = manifest.instanceID { return instances.first { $0.id == id } }
        let matches = instances.filter {
            $0.folderName.compare(manifest.folderName, options: .caseInsensitive) == .orderedSame &&
                $0.versionID == manifest.minecraftVersion && $0.modLoaderRaw == manifest.modLoader &&
                ((try? $0.fabricConfiguration()?.loaderVersion) ?? nil) == manifest.loaderVersion
        }
        return matches.count == 1 ? matches[0] : nil
    }

    var selectedInstance: GameInstance? {
        guard let instanceTargetID else { return nil }
        return (try? installations.store.context.fetch(FetchDescriptor<GameInstance>()))?.first { $0.id == instanceTargetID }
    }

    var allInstances: [GameInstance] {
        ((try? installations.store.context.fetch(FetchDescriptor<GameInstance>())) ?? []).sorted {
            $0.name.localizedStandardCompare($1.name) == .orderedAscending
        }
    }

    var instanceNameError: String? {
        guard instanceAction == .newInstance else { return nil }
        do { _ = try installations.store.validateName(instanceName); return nil }
        catch { return error.localizedDescription }
    }

    var instanceTargetError: String? {
        guard instanceAction == .replace else { return nil }
        guard let target = selectedInstance, matchedInstance?.id == target.id else {
            return String(appLocalized: "Для этой копии не найдена однозначно совпадающая сборка. Создайте новую сборку.")
        }
        return installations.store.managementBlockedReason(target)
    }

    var selectedWorldTarget: GameInstance? {
        guard let worldTargetID else { return nil }
        return readyWorldTargets.first { $0.id == worldTargetID }
    }

    var readyWorldTargets: [GameInstance] {
        allInstances.filter { $0.state == .ready }
    }

    var worldTargetError: String? {
        guard case .world = staged?.kind else { return nil }
        guard !readyWorldTargets.isEmpty else { return String(appLocalized: "Нет готовых сборок для восстановления мира. Сначала создайте и установите сборку.") }
        guard let target = selectedWorldTarget else { return String(appLocalized: "Выберите готовую сборку." ) }
        if let reason = content.worldBlockedReason(target) { return reason }
        return nil
    }

    var worldNameError: String? {
        guard case .world = staged?.kind else { return nil }
        do { try Self.validateWorldFolder(worldFolderName); return nil }
        catch { return error.localizedDescription }
    }

    var worldFolderError: String? {
        guard let target = selectedWorldTarget,
              (try? Self.validateWorldFolder(worldFolderName)) != nil,
              worldFolderIsOccupied(), worldAction == .copy else { return nil }
        _ = target
        return String(appLocalized: "Папка мира уже существует. Выберите замену или другое имя.")
    }

    func worldTargetReason(for instance: GameInstance) -> String? {
        if instance.state != .ready { return String(appLocalized: "Сборка не готова.") }
        return content.worldBlockedReason(instance)
    }

    var needsConfirmation: Bool {
        switch currentKind {
        case .instance: return instanceAction == .replace
        case .world(let manifest):
            return worldAction == .replace || worldTargetID != manifest.sourceInstance.id || worldConfigurationDiffers(from: manifest.sourceInstance)
        case nil: return false
        }
    }

    func selectWorldTarget(_ id: UUID?) {
        let changed = worldTargetID != id
        worldTargetID = id
        guard let target = id.flatMap({ selectedID in readyWorldTargets.first { $0.id == selectedID } }),
              case .world(let manifest) = staged?.kind else { return }
        let saves = (try? worldSavesFolder(for: target))
        let names = (try? FileManager.default.contentsOfDirectory(atPath: saves?.path ?? "")) ?? []
        let archivedFolder = try? Self.validWorldFolder(manifest.folderName)
        let existingArchiveFolder = archivedFolder.flatMap { archived in names.first { Self.worldFolderKey($0) == Self.worldFolderKey(archived) } }
        if changed && !worldFolderWasEdited {
            let base = Self.safeWorldFolder(manifest.folderName, fallback: manifest.name)
            let suggestion = existingArchiveFolder ?? Self.uniqueWorldFolder(base: base, occupied: names)
            applyWorldDefaults { worldFolderName = suggestion }
        }
        if changed && !worldActionWasEdited {
            applyWorldDefaults { worldAction = existingArchiveFolder == nil ? .copy : .replace }
        }
        if !worldFolderWasEdited && worldAction == .copy && worldFolderIsOccupied() {
            let base = Self.safeWorldFolder(manifest.folderName, fallback: manifest.name)
            applyWorldDefaults { worldFolderName = Self.uniqueWorldFolder(base: base, occupied: names) }
        }
        if changed && !worldNameWasEdited { applyWorldDefaults { worldName = manifest.name } }
    }

    func setWorldAction(_ action: WorldRestoreAction) {
        guard worldAction != action else { return }
        worldAction = action
        guard !worldFolderWasEdited, case .world(let manifest) = staged?.kind,
              let target = selectedWorldTarget else { return }
        let base = Self.safeWorldFolder(manifest.folderName, fallback: manifest.name)
        let names = (try? FileManager.default.contentsOfDirectory(atPath: (try? worldSavesFolder(for: target))?.path ?? "")) ?? []
        if action == .copy && worldFolderIsOccupied() {
            applyWorldDefaults { worldFolderName = Self.uniqueWorldFolder(base: base, occupied: names) }
        } else if action == .replace,
                  let existing = names.first(where: { Self.worldFolderKey($0) == Self.worldFolderKey(base) }) {
            applyWorldDefaults { worldFolderName = existing }
        }
    }

    private func applyWorldDefaults(_ update: () -> Void) {
        applyingWorldDefaults = true
        update()
        applyingWorldDefaults = false
    }

    func worldFolderIsOccupied() -> Bool {
        guard let target = selectedWorldTarget, (try? Self.validateWorldFolder(worldFolderName)) != nil,
              let saves = try? worldSavesFolder(for: target),
              let names = try? FileManager.default.contentsOfDirectory(atPath: saves.path) else { return false }
        return names.contains { Self.worldFolderKey($0) == Self.worldFolderKey(worldFolderName) }
    }

    var worldArchiveFolderIsOccupied: Bool {
        guard case .world(let manifest) = staged?.kind,
              let archived = try? Self.validWorldFolder(manifest.folderName),
              let target = selectedWorldTarget,
              let saves = try? worldSavesFolder(for: target),
              let names = try? FileManager.default.contentsOfDirectory(atPath: saves.path) else { return false }
        return names.contains { Self.worldFolderKey($0) == Self.worldFolderKey(archived) }
    }

    private func beginNextIfNeeded(presentWindow: Bool = true) {
        guard !isCleaningStage, active == nil, currentURL == nil, let next = queue.first else { return }
        queue.removeFirst()
        active = next
        currentURL = next.url
        if presentWindow { windowPresenter?() }
        worldTargetID = nil
        applyWorldDefaults {
            worldAction = .copy
            worldName = ""
            worldFolderName = ""
        }
        worldActionWasEdited = false
        worldNameWasEdited = false
        worldFolderWasEdited = false
        isPreparing = true
        errorMessage = nil
        staged = nil
        _ = next.url.startAccessingSecurityScopedResource()
        let id = next.id
        preparationTask = Task { [weak self] in
            guard let self else { return }
            do {
                let result = try await RestoreArchive.prepare(next.url, under: self.installations.store.storage.root)
                if Task.isCancelled || self.active?.id != id {
                    await RestoreArchive.discardStageOffActor(result.directory)
                    guard self.active?.id == id else { return }
                    self.isPreparing = false
                    self.preparationTask = nil
                    self.finishCurrent()
                    return
                }
                self.staged = result
                self.isPreparing = false
                self.preparationTask = nil
                self.setInitialChoice(for: result)
            } catch is CancellationError {
                guard self.active?.id == id else { return }
                self.isPreparing = false
                self.preparationTask = nil
                self.finishCurrent()
            } catch {
                guard self.active?.id == id else { return }
                self.isPreparing = false
                self.preparationTask = nil
                self.errorMessage = error.localizedDescription
            }
        }
    }

    private func setInitialChoice(for stage: StagedRestoreArchive) {
        switch stage.kind {
        case .instance(let manifest):
            let match = matchedInstance
            instanceTargetID = match?.id
            instanceAction = match == nil ? .newInstance : .replace
            instanceName = uniqueInstanceName(base: manifest.name)
        case .world(let manifest):
            let ready = readyWorldTargets
            if let exact = ready.first(where: { $0.id == manifest.sourceInstance.id }) {
                selectWorldTarget(exact.id)
            } else {
                let compatible = ready.filter {
                    $0.folderName.compare(manifest.sourceInstance.folderName, options: .caseInsensitive) == .orderedSame &&
                        $0.versionID == manifest.sourceInstance.minecraftVersion && $0.modLoaderRaw == manifest.sourceInstance.modLoader &&
                        ((try? $0.fabricConfiguration()?.loaderVersion) ?? nil) == manifest.sourceInstance.loaderVersion
                }
                selectWorldTarget(compatible.count == 1 ? compatible[0].id : nil)
                if compatible.count != 1 { applyWorldDefaults { worldName = manifest.name } }
            }
        }
    }

    private func commit(_ stage: StagedRestoreArchive) async {
        do {
            switch stage.kind {
            case .instance(let manifest):
                let replacing = instanceAction == .replace ? selectedInstance : nil
                guard instanceAction != .replace || replacing != nil else {
                    throw InstanceFileError.message(String(appLocalized: "Выбранная сборка для замены больше не существует."))
                }
                guard instanceAction != .replace || replacing?.id == matchedInstance?.id else {
                    throw InstanceFileError.message(String(appLocalized: "Сборка больше не совпадает с этой резервной копией. Создайте новую сборку."))
                }
                if let error = instanceNameError, instanceAction == .newInstance { throw InstanceFileError.message(error) }
                let result = try await installations.store.restoreInstance(manifest, stagedDirectory: stage.directory, name: instanceName, replacing: replacing)
                content.invalidateAfterRestore(result.instance)
                installations.scheduleQueuedInstallations()
                navigationAfterQueue = .init(id: UUID(), instanceID: result.instance.id, world: nil)
                completionMessage = result.cleanupWarning
                finishCurrent(preserve: false)
            case .world(let manifest):
                let requestedWorldName = worldName
                let requestedFolderName = worldFolderName
                let requestedAction = worldAction
                guard let target = selectedWorldTarget else { throw InstanceFileError.message(worldTargetError ?? String(appLocalized: "Выберите готовую сборку.")) }
                if let error = worldTargetError { throw InstanceFileError.message(error) }
                if let error = worldNameError { throw InstanceFileError.message(error) }
                guard !requestedWorldName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw InstanceFileError.message(String(appLocalized: "Введите название мира.")) }
                try await RestoreArchive.renameWorld(in: stage.directory, to: requestedWorldName)
                let warning = try await commitWorld(stage: stage, manifest: manifest, target: target,
                                                    folderName: requestedFolderName, action: requestedAction)
                navigationAfterQueue = .init(id: UUID(), instanceID: target.id, world: requestedFolderName)
                completionMessage = warning
                finishCurrent(preserve: false)
            }
        } catch {
            installations.scheduleQueuedInstallations()
            preserveStage = error is InstanceRestoreCommitFailure || error is WorldRestoreCommitFailure
            errorMessage = error.localizedDescription
            isCommitting = false
        }
    }

    private func commitWorld(
        stage: StagedRestoreArchive,
        manifest: WorldBackupManifest,
        target: GameInstance,
        folderName worldFolderName: String,
        action worldAction: WorldRestoreAction
    ) async throws -> String? {
        let store = installations.store
        if let reason = content.worldBlockedReason(target) { throw InstanceFileError.message(reason) }
        guard target.state == .ready,
              try store.context.fetch(FetchDescriptor<GameInstance>()).contains(where: { $0.id == target.id }) else {
            throw InstanceFileError.message(String(appLocalized: "Сборка для восстановления больше не готова."))
        }
        try Self.validateWorldFolder(worldFolderName)
        let instanceRoot = try store.storage.directory(target.folderName)
        store.contentBusy.insert(target.id)
        defer {
            store.contentBusy.remove(target.id)
            installations.scheduleQueuedInstallations()
        }
        let saves = try worldSavesFolder(for: target, create: true)
        let destination = saves.appendingPathComponent(worldFolderName, isDirectory: true)
        let names = try FileManager.default.contentsOfDirectory(atPath: saves.path)
        let existingName = names.first { Self.worldFolderKey($0) == Self.worldFolderKey(worldFolderName) }
        let occupied = existingName != nil
        let existingDestination = existingName.map { saves.appendingPathComponent($0, isDirectory: true) }
        let existingIdentity = existingDestination.flatMap(Self.directoryIdentity)
        guard !occupied || existingIdentity != nil else {
            throw InstanceFileError.message(String(appLocalized: "Папка назначения содержит небезопасный объект."))
        }
        guard !occupied || worldAction == .replace else {
            throw InstanceFileError.message(String(appLocalized: "Папка мира уже существует. Выберите замену или другое имя."))
        }
        if occupied {
            let worlds = try await content.worlds.list(in: instanceRoot)
            guard worlds.contains(where: { $0.id.caseInsensitiveCompare(worldFolderName) == .orderedSame && $0.metadataError == nil }) else {
                throw InstanceFileError.message(String(appLocalized: "Папка назначения уже занята и не является готовым миром."))
            }
        }
        guard let stagedWorld = try? InstanceStorage.containedURL(manifest.worldPath, in: stage.directory) else {
            throw InstanceFileError.message(String(appLocalized: "Временная папка мира повреждена."))
        }
        let source = stagedWorld
        try InstanceWorlds.checkIndependentFiles(in: source)
        let retired = saves.appendingPathComponent(".hako-restore-retired-world-\(UUID().uuidString)", isDirectory: true)
        let sourcePath = stagedWorld.standardizedFileURL.path
        guard source.standardizedFileURL.path == sourcePath else { throw InstanceFileError.message(String(appLocalized: "Временная папка мира вышла за пределы распаковки.")) }
        var stagedDevice = stat(), savesDevice = stat()
        guard stat(stagedWorld.path, &stagedDevice) == 0, stat(saves.path, &savesDevice) == 0 else {
            throw POSIXError(.init(rawValue: errno) ?? .EIO)
        }
        var targetStage: URL?
        let swapSource: URL
        if stagedDevice.st_dev == savesDevice.st_dev {
            swapSource = stagedWorld
        } else {
            targetStage = try await RestoreArchive.stageWorldCopy(source, under: saves)
            swapSource = targetStage!
        }
        var oldMoved = false, newMoved = false
        do {
            guard target.state == .ready, !store.launchBusy.contains(target.id),
                  store.contentBusy.contains(target.id),
                  try store.context.fetch(FetchDescriptor<GameInstance>()).contains(where: { $0.id == target.id }) else {
                throw InstanceFileError.message(String(appLocalized: "Сборка для восстановления больше не готова."))
            }
            let latestNames = try FileManager.default.contentsOfDirectory(atPath: saves.path)
            let latestName = latestNames.first { Self.worldFolderKey($0) == Self.worldFolderKey(worldFolderName) }
            guard latestName == existingName,
                  latestName.flatMap({ Self.directoryIdentity(saves.appendingPathComponent($0, isDirectory: true)) }) == existingIdentity else {
                throw InstanceFileError.message(String(appLocalized: "Папка назначения изменилась. Проверьте список миров и повторите восстановление."))
            }
            if let targetStage { try RestoreArchive.prepareWorldStageForCommit(targetStage) }
            if occupied, let existingDestination { try worldMove(existingDestination, retired); oldMoved = true }
            try worldMove(swapSource, destination); newMoved = true
            _ = try await content.worlds.list(in: instanceRoot)
        } catch {
            var recovery: [URL] = []
            if newMoved {
                do { try worldMove(destination, swapSource) }
                catch { recovery.append(destination) }
            }
            if oldMoved {
                do { try worldMove(retired, existingDestination ?? destination) }
                catch { recovery.append(retired) }
            }
            if !recovery.isEmpty {
                if let targetStage, FileManager.default.fileExists(atPath: targetStage.path) { recovery.append(targetStage) }
                throw WorldRestoreCommitFailure(message: String(appLocalized: "Не удалось откатить восстановление мира. Данные сохранены по путям: \(recovery.map(\.path).joined(separator: ", "))"), recoveryLocations: recovery)
            }
            if let targetStage, FileManager.default.fileExists(atPath: targetStage.path) {
                do { try await RestoreArchive.discardWorldStage(targetStage) }
                catch {
                    throw WorldRestoreCommitFailure(
                        message: String(appLocalized: "Не удалось очистить временную папку восстановления. Данные сохранены по пути: \(targetStage.path)"),
                        recoveryLocations: [targetStage]
                    )
                }
            }
            throw error
        }
        content.invalidateAfterRestore(target, world: worldFolderName)
        if oldMoved {
            do { try await RestoreArchive.removeRetiredDirectory(at: retired) }
            catch { return String(appLocalized: "Мир восстановлен, но прежняя папка осталась в \(retired.path). Удалите её после проверки.") }
        }
        return nil
    }

    private func worldConfigurationDiffers(from source: WorldBackupManifest.SourceInstance) -> Bool {
        guard let target = selectedWorldTarget else { return false }
        return target.versionID != source.minecraftVersion || target.modLoaderRaw != source.modLoader ||
            ((try? target.fabricConfiguration()?.loaderVersion) ?? nil) != source.loaderVersion
    }

    private func uniqueInstanceName(base: String) -> String {
        let candidate = (try? InstanceName.validated(base)) ?? String(appLocalized: "Восстановленная сборка")
        if (try? installations.store.validateName(candidate)) != nil { return candidate }
        for number in 2...999 {
            let suffix = " \(number)"
            let name = String(candidate.prefix(60 - suffix.count)).trimmingCharacters(in: .whitespaces) + suffix
            if (try? installations.store.validateName(name)) != nil { return name }
        }
        return String(appLocalized: "Восстановленная сборка")
    }

    private static func worldFolderKey(_ name: String) -> String {
        name.precomposedStringWithCanonicalMapping.lowercased(with: Locale(identifier: "en_US_POSIX"))
    }

    private static func directoryIdentity(_ url: URL) -> String? {
        var metadata = stat()
        guard lstat(url.path, &metadata) == 0, metadata.st_mode & S_IFMT == S_IFDIR else { return nil }
        return "\(metadata.st_dev):\(metadata.st_ino)"
    }

    private func worldSavesFolder(for instance: GameInstance, create: Bool = false) throws -> URL {
        let root = try installations.store.storage.directory(instance.folderName)
        let minecraft = root.appendingPathComponent("minecraft", isDirectory: true)
        let saves = minecraft.appendingPathComponent("saves", isDirectory: true)
        for directory in [minecraft, saves] {
            var metadata = stat()
            if lstat(directory.path, &metadata) == 0 {
                guard metadata.st_mode & S_IFMT == S_IFDIR else { throw InstanceFileError.message(String(appLocalized: "Папка миров содержит небезопасный объект.")) }
            } else if errno == ENOENT {
                if create {
                    guard mkdir(directory.path, 0o755) == 0 || errno == EEXIST else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
                    guard lstat(directory.path, &metadata) == 0, metadata.st_mode & S_IFMT == S_IFDIR else {
                        throw InstanceFileError.message(String(appLocalized: "Папка миров содержит небезопасный объект."))
                    }
                }
            } else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
        }
        return saves
    }

    private static func validateWorldFolder(_ input: String) throws {
        guard !input.isEmpty, input != ".", input != "..", input == input.precomposedStringWithCanonicalMapping,
              input.utf8.count <= 240,
              !input.contains("/"), !input.contains("\\"), !input.contains(":"), !input.contains("\0"),
              input.unicodeScalars.allSatisfy({ !CharacterSet.controlCharacters.contains($0) }) else {
            throw InstanceFileError.message(String(appLocalized: "Недопустимое имя папки мира."))
        }
    }

    private static func validWorldFolder(_ input: String) throws -> String {
        try validateWorldFolder(input)
        return input
    }

    private static func safeWorldFolder(_ input: String, fallback: String) -> String {
        let candidate = input.precomposedStringWithCanonicalMapping
        if (try? validateWorldFolder(candidate)) != nil { return candidate }
        let allowed = fallback.precomposedStringWithCanonicalMapping.unicodeScalars.map { scalar -> Character in
            if CharacterSet.alphanumerics.contains(scalar) || scalar == " " || scalar == "_" || scalar == "-" { return Character(scalar) }
            return "_"
        }
        let cleaned = String(allowed).trimmingCharacters(in: CharacterSet(charactersIn: " ._"))
        let value = String((cleaned.isEmpty ? "Restored World" : cleaned).prefix(120))
        return (try? validateWorldFolder(value)) == nil ? "Restored World" : value
    }

    private static func uniqueWorldFolder(base: String, occupied: [String]) -> String {
        let names = Set(occupied.map(Self.worldFolderKey))
        if !names.contains(worldFolderKey(base)) { return base }
        for number in 2...999 {
            let suffix = " \(number)"
            let name = String(base.prefix(200 - suffix.utf8.count)) + suffix
            if !names.contains(worldFolderKey(name)) { return name }
        }
        return "Restored World \(UUID().uuidString.prefix(8))"
    }

    private func finishCurrent(preserve: Bool? = nil) {
        guard !isCleaningStage else { return }
        let keep = preserve ?? preserveStage
        let directoryToDiscard = keep ? nil : staged?.directory
        if let active {
            active.url.stopAccessingSecurityScopedResource()
            identifiers.subtract(active.keys)
        }
        active = nil
        currentURL = nil
        staged = nil
        errorMessage = nil
        isPreparing = false
        isCommitting = false
        awaitingConfirmation = false
        preserveStage = false
        preparationTask = nil
        if let directoryToDiscard {
            isCleaningStage = true
            Task { [weak self] in
                await RestoreArchive.discardStageOffActor(directoryToDiscard)
                guard let self else { return }
                self.isCleaningStage = false
                self.beginNextIfNeeded()
                self.publishNavigationIfQueueDrained()
            }
        } else {
            beginNextIfNeeded()
            publishNavigationIfQueueDrained()
        }
    }

    private func publishNavigationIfQueueDrained() {
        if currentURL == nil, queue.isEmpty, !isCleaningStage, let navigationAfterQueue {
            self.navigationAfterQueue = nil
            navigation = navigationAfterQueue
        }
    }

    private static func identities(of url: URL) -> Set<String> {
        let resolvedPath = url.resolvingSymlinksInPath().standardizedFileURL.path
        var metadata = stat()
        if stat(resolvedPath, &metadata) == 0 {
            return ["path:\(resolvedPath)", "file:\(metadata.st_dev):\(metadata.st_ino)"]
        }
        return ["path:\(resolvedPath)"]
    }
}

nonisolated extension RestoreArchive {
    @concurrent static func renameWorld(in staging: URL, to name: String) async throws {
        let world = try InstanceStorage.containedURL("world", in: staging)
        for file in ["level.dat", "level.dat_old"] {
            let url = world.appendingPathComponent(file)
            guard FileManager.default.fileExists(atPath: url.path) else { continue }
            let bytes = try RestoreArchive.worldMetadataData(at: url)
            let renamed = try WorldMetadata.renamed(bytes, to: name)
            try renamed.write(to: url, options: .atomic)
        }
    }
}

nonisolated struct WorldRestoreCommitFailure: LocalizedError, Sendable {
    let message: String
    let recoveryLocations: [URL]
    var errorDescription: String? { message }
}

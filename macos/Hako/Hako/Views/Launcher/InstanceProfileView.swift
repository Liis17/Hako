import AppKit
import SwiftUI
import UniformTypeIdentifiers

private enum InstanceSection: String, CaseIterable, Identifiable {
    case mods = "Моды", packs = "Ресурспаки", settings = "Настройки"
    var id: Self { self }
}

private let profileColumnWidth: CGFloat = 900

struct InstanceProfileView: View {
    let instance: GameInstance
    var account: Account?
    let onBack: () -> Void
    @Environment(InstanceRenameExitCoordinator.self) private var renameExit
    @Environment(InstallationCoordinator.self) private var installations
    @Environment(PlaytimeCoordinator.self) private var playtime
    @State private var section = InstanceSection.packs
    @State private var actionError: String?
    @State private var nameDraft: String
    @State private var renameAction: RenameAction?

    private enum RenameAction {
        case save
        case section(InstanceSection)

        var isSectionChange: Bool {
            if case .section = self { return true }
            return false
        }
    }

    init(instance: GameInstance, account: Account?, onBack: @escaping () -> Void) {
        self.instance = instance
        self.account = account
        self.onBack = onBack
        _section = State(initialValue: instance.modLoader == .fabric ? .mods : .packs)
        _nameDraft = State(initialValue: instance.name)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            Button("Все сборки", systemImage: "chevron.left", action: onBack).buttonStyle(.plain).foregroundStyle(.secondary)
            HStack(spacing: 20) {
                InstanceIcon(symbol: instance.iconSymbol, url: try? InstanceStorage.containedURL("icon.png", in: installations.store.storage.directory(instance.folderName)))
                    .frame(width: 76, height: 76).id(instance.iconRevision)
                VStack(alignment: .leading, spacing: 6) {
                    Text(instance.name).font(.system(size: 44, weight: .heavy)).tracking(-1).lineLimit(2).minimumScaleFactor(0.55)
                    HStack(spacing: 8) {
                        Text("Minecraft \(instance.versionID) · \(instance.loaderTitle) · Java \(instance.javaMajorVersion)")
                            .lineLimit(1).truncationMode(.tail).foregroundStyle(.secondary)
                        if instance.state == .ready {
                            Label("В игре: \(PlaytimeFormatter.string(playtime.instanceSeconds(instance.id, xuid: account?.xuid)))", systemImage: "clock")
                                .font(.callout).foregroundStyle(.secondary).fixedSize()
                        }
                    }
                }
                Spacer(minLength: 12)
                if instance.state == .ready {
                    InstancePlayControls(instance: instance, account: account).frame(maxWidth: 260, alignment: .trailing)
                }
            }
            if instance.state != .ready { installationPanel }
            Picker("Раздел сборки", selection: Binding(get: { section }, set: requestSectionChange)) {
                ForEach(InstanceSection.allCases) { item in
                    Text(item.rawValue).tag(item)
                        .disabled(item == .mods && instance.modLoader != .fabric)
                        .selectionDisabled(item == .mods && instance.modLoader != .fabric)
                        .help(item == .mods && instance.modLoader != .fabric ? "Моды доступны только для сборок с выбранным модлоадером." : "")
                }
            }.pickerStyle(.segmented).labelsHidden().frame(maxWidth: 600, alignment: .leading)
            ScrollView {
                Group {
                switch section {
                case .mods: InstanceFilesView(instance: instance, mods: true)
                case .packs: InstanceFilesView(instance: instance, mods: false)
                    case .settings: InstanceSettingsView(instance: instance, name: $nameDraft, onSaveName: { renameAction = .save })
                }
                }.padding(2).padding(.bottom, 32)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }.padding(.horizontal, -2).id(section)
        }.frame(maxWidth: profileColumnWidth, maxHeight: .infinity, alignment: .topLeading)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .onAppear(perform: syncPendingRename)
        .onChange(of: nameDraft) { syncPendingRename() }
        .onChange(of: instance.name) { oldName, newName in
            if nameDraft == oldName { nameDraft = newName }
            syncPendingRename()
        }
        .onDisappear { if renameExit.pendingRename?.instanceID == instance.id { renameExit.pendingRename = nil } }
        .alert(actionError == nil ? "Переименовать сборку?" : "Не удалось выполнить действие", isPresented: Binding(get: { renameAction != nil || actionError != nil }, set: { if !$0 { renameAction = nil; actionError = nil } })) {
            if actionError != nil {
                Button("ОК", role: .cancel) { actionError = nil }
            } else {
                Button("Переименовать") { confirmRename() }
                Button(renameAction?.isSectionChange == true ? "Продолжить без переименования" : "Отмена", role: .cancel) { declineRename() }
            }
        } message: {
            Text(actionError ?? "Будет переименована папка с файлами этой сборки в ~/.hako.")
        }
    }

    private func requestSectionChange(_ newSection: InstanceSection) {
        guard newSection != section else { return }
        if nameDraft != instance.name { renameAction = .section(newSection) }
        else { section = newSection }
    }

    private func confirmRename() {
        do {
            guard !installations.contentBusy.contains(instance.id) else {
                throw InstanceFileError.message("Дождитесь завершения операций с файлами перед переименованием.")
            }
            try installations.store.rename(instance, to: nameDraft)
            nameDraft = instance.name
            renameExit.pendingRename = nil
            if let renameAction, case .section(let newSection) = renameAction { section = newSection }
            renameAction = nil
            actionError = nil
        } catch {
            renameAction = nil
            actionError = error.localizedDescription
        }
    }

    private func declineRename() {
        if let renameAction, case .section(let newSection) = renameAction {
            nameDraft = instance.name
            renameExit.pendingRename = nil
            section = newSection
        }
        renameAction = nil
    }

    private func syncPendingRename() {
        if nameDraft == instance.name {
            if renameExit.pendingRename?.instanceID == instance.id { renameExit.pendingRename = nil }
        } else {
            renameExit.pendingRename = PendingInstanceRename(instanceID: instance.id, name: nameDraft)
        }
    }

    private var installationPanel: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text(instance.state.title).font(.headline)
                Spacer()
                if instance.state == .installing || instance.state == .queued {
                    Button("Остановить") { do { try installations.pause(instance) } catch { actionError = error.localizedDescription } }.buttonStyle(.glass)
                } else {
                    Button(instance.state == .paused ? "Продолжить" : "Повторить") {
                        do { try installations.enqueue(instance) } catch { actionError = error.localizedDescription }
                    }.buttonStyle(.glass)
                }
            }
            if let progress = installations.progress[instance.id] {
                ProgressView(value: progress.fraction).tint(.sakuraDeep)
                HStack { Text(progress.stage); Spacer(); if progress.totalBytes > 0 { Text("\(Int(progress.fraction * 100))%") } }.font(.caption).foregroundStyle(.secondary)
            } else if instance.state == .queued { Text("Загрузка начнётся, когда завершится предыдущая сборка.").font(.callout).foregroundStyle(.secondary) }
            if let error = instance.installationError { Text(error).font(.callout).foregroundStyle(Color.shu).textSelection(.enabled) }
        }.instanceSurface()
    }
}

private struct InstanceSettingsView: View {
    let instance: GameInstance
    @Binding var nameDraft: String
    let onSaveName: () -> Void
    @Environment(InstallationCoordinator.self) private var installations
    @Environment(GameLaunchCoordinator.self) private var games
    @State private var draft: InstanceDraft
    @State private var parametersValid = true
    @State private var error: String?
    @State private var manifest: MinecraftVersionManifest?
    @State private var manifestError: String?
    @State private var fabricProfile: FabricProfile?

    init(instance: GameInstance, name: Binding<String>, onSaveName: @escaping () -> Void) {
        self.instance = instance
        _nameDraft = name
        self.onSaveName = onSaveName
        _draft = State(initialValue: InstanceDraft(instance: instance))
    }
    private var renameBlocked: Bool { instance.state == .installing || instance.state == .queued || installations.contentBusy.contains(instance.id) || installations.store.launchBusy.contains(instance.id) }
    private var nameError: String? {
        do { _ = try installations.store.validateName(nameDraft, excluding: instance); return nil }
        catch { return error.localizedDescription }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            VStack(alignment: .leading, spacing: 20) {
                Text("Профиль сборки").font(.title3.weight(.semibold))
                InstanceIconPicker(draft: $draft, existingIcon: try? InstanceStorage.containedURL("icon.png", in: installations.store.storage.directory(instance.folderName)))
                VStack(alignment: .leading, spacing: 8) {
                    HStack { Text("Название").font(.callout.weight(.medium)); Spacer(); Text("\(nameDraft.count)/60").font(.caption).foregroundStyle(.secondary) }
                    TextField("Название сборки", text: $nameDraft).textFieldStyle(.roundedBorder).disabled(renameBlocked)
                    if renameBlocked { Text("Переименование доступно после остановки загрузки, закрытия игры и завершения операций с файлами.").font(.caption).foregroundStyle(.secondary) }
                    if let nameError { Text(nameError).font(.caption).foregroundStyle(Color.shu) }
                    Button("Сохранить имя", action: onSaveName)
                        .buttonStyle(.glass)
                        .disabled(renameBlocked || nameDraft == instance.name || nameError != nil)
                }
                Text("Minecraft \(instance.versionID) · \(instance.loaderTitle)").foregroundStyle(.secondary)
                Text("Папка: ~/.hako/\(instance.folderName)").font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
            }.instanceSurface()
            VStack(alignment: .leading, spacing: 18) {
                Text("Параметры запуска").font(.title3.weight(.semibold))
                InstanceParametersEditor(draft: $draft, isValid: $parametersValid, manifest: manifest, fabric: fabricProfile)
                if draft.argumentSource == .mojang && manifest == nil {
                    if let manifestError {
                        Text(manifestError).font(.caption).foregroundStyle(Color.shu)
                        Button("Повторить загрузку параметров") { Task { await loadManifest() } }.buttonStyle(.glass)
                    } else { ProgressView("Загружаем параметры Mojang…").font(.caption) }
                }
            }.instanceSurface()
            Label("Иконка и параметры сохраняются автоматически.", systemImage: "checkmark.circle")
                .font(.caption).foregroundStyle(.secondary)
            if games.states[instance.id] == .running || games.states[instance.id] == .preparing {
                Text("Изменения параметров применятся при следующем запуске игры.").font(.caption).foregroundStyle(.secondary)
            }
            if let error { Text(error).font(.callout).foregroundStyle(Color.shu) }
        }.onChange(of: draft.iconSymbol) { saveSettings() }
        .onChange(of: draft.iconData) { saveSettings() }
        .onChange(of: draft.argumentSource) { saveSettings() }
        .onChange(of: draft.offlineMode) { saveSettings() }
        .onChange(of: draft.offlineUsername) { saveSettings() }
        .onChange(of: draft.parameters) { saveSettings() }
        .task(id: "\(instance.folderName):\(instance.state.rawValue)") { await loadManifest() }
    }

    private func saveSettings() {
        guard parametersValid else { return }
        do {
            try installations.store.updateSettings(instance, with: draft)
            draft = InstanceDraft(instance: instance)
            error = nil
        } catch let saveError { error = saveError.localizedDescription }
    }

    private func loadManifest() async {
        manifestError = nil
        do {
            guard let url = URL(string: instance.metadataURL) else { throw MojangError.invalid("Не удалось прочитать ссылку описания версии.") }
            let local = try InstanceStorage.containedURL("minecraft/versions/\(instance.versionID)/\(instance.versionID).json", in: installations.store.storage.directory(instance.folderName))
            let result = try await installations.client.manifest(.init(url: url, sha1: instance.metadataSHA1), installedAt: local)
            guard !Task.isCancelled else { return }
            guard result.id == instance.versionID else { throw MojangError.invalid("Описание версии не соответствует сборке.") }
            if let configuration = try instance.fabricConfiguration() {
                let root = try installations.store.storage.directory(instance.folderName)
                if instance.fabricProfileSHA1 != nil { fabricProfile = try FabricProfile.installed(root: root, minecraft: instance.versionID, sha1: instance.fabricProfileSHA1) }
                else { fabricProfile = try await installations.fabricClient.profile(minecraft: instance.versionID, loader: configuration.loaderVersion).0 }
                guard !Task.isCancelled else { return }
            }
            manifest = result
        } catch { if !Task.isCancelled { manifestError = error.localizedDescription } }
    }
}

private struct InstanceFilesView: View {
    let instance: GameInstance
    let mods: Bool
    @Environment(InstanceContentController.self) private var content
    @Environment(\.scenePhase) private var scenePhase
    @State private var importing = false
    @State private var dropTargeted = false

    private var items: [InstanceContentItem] { (mods ? content.mods : content.packs)[instance.id] ?? [] }
    private var disabledReason: String? { content.disabledReason(instance, mods: mods) }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack {
                Text(mods ? "Моды" : "Ресурспаки").font(.title3.weight(.semibold))
                Spacer()
                if mods {
                    Button("Проверить обновления", systemImage: "arrow.clockwise") { Task { await content.reload(instance, mods: true); await content.checkUpdates(instance) } }
                        .buttonStyle(.glass).disabled(content.checkingUpdates.contains(instance.id))
                }
                Button("Открыть папку", systemImage: "folder") {
                    do {
                        let folder = try content.folder(instance, mods: mods)
                        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                        NSWorkspace.shared.open(folder)
                    } catch { content.errors[instance.id] = error.localizedDescription }
                }.buttonStyle(.glass)
                Button("Добавить…", systemImage: "plus") { importing = true }.buttonStyle(.glass).disabled(disabledReason != nil)
            }
            VStack(spacing: 8) {
                Image(systemName: "square.and.arrow.down").font(.title2).foregroundStyle(Color.sakuraDeep)
                Text(mods ? "Перетащите JAR-файлы модов" : "Перетащите ZIP-архивы ресурспаков").font(.callout.weight(.medium))
                Text(mods ? "Файлы будут скопированы в эту сборку." : "Также можно добавить папку ресурспака через кнопку «Добавить».")
                    .font(.caption).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity).padding(22)
            .background(dropTargeted ? Color.sakuraDeep.opacity(0.12) : .white.opacity(0.25), in: .rect(cornerRadius: 14))
            .overlay { RoundedRectangle(cornerRadius: 14).strokeBorder(dropTargeted ? Color.sakuraDeep : .secondary.opacity(0.35), style: StrokeStyle(lineWidth: 1.5, dash: [6, 5])) }
            .opacity(disabledReason == nil ? 1 : 0.6)
            .onDrop(of: [UTType.fileURL.identifier], isTargeted: $dropTargeted, perform: receiveDrop)
            if let reason = disabledReason { Text(reason).font(.caption).foregroundStyle(.secondary) }
            if content.installations.contentBusy.contains(instance.id) { ProgressView("Обрабатываем файлы…").font(.callout) }
            if let error = content.errors[instance.id] { Text(error).font(.callout).foregroundStyle(Color.shu) }
            if mods && content.checkingUpdates.contains(instance.id) { ProgressView("Проверяем Fabric API…").font(.caption) }
            if mods, let message = content.updateMessages[instance.id] { Text(message).font(.caption).foregroundStyle(.secondary) }
            if items.isEmpty {
                Text(mods ? "В сборке пока нет модов." : "Ресурспаков пока нет.").foregroundStyle(.secondary).padding(.vertical, 16)
            }
            ForEach(items) { item in
                HStack(spacing: 12) {
                    if mods {
                        Toggle("Активность \(item.logicalName)", isOn: Binding(get: { item.enabled }, set: { content.setEnabled(item, in: instance, enabled: $0) }))
                            .toggleStyle(.switch).labelsHidden().disabled(disabledReason != nil)
                    } else { Image(systemName: item.isDirectory ? "folder" : "doc.zipper").foregroundStyle(Color.sakuraDeep) }
                    VStack(alignment: .leading, spacing: 6) {
                        Text(item.logicalName).lineLimit(2).textSelection(.enabled).foregroundStyle(item.enabled ? .primary : .secondary)
                        if mods {
                            HStack(spacing: 8) {
                                Label(item.source.title, systemImage: item.source.symbol).font(.caption).padding(.horizontal, 7).padding(.vertical, 3)
                                    .background(.white.opacity(0.6), in: Capsule())
                                if let api = item.origin?.api { Text(api.version).font(.caption).foregroundStyle(.secondary) }
                                if item.source == .local { Text("Обновляется вручную").font(.caption).foregroundStyle(.secondary) }
                                if !item.enabled { Text("Отключён").font(.caption).foregroundStyle(.secondary) }
                            }
                        }
                    }
                    Spacer(minLength: 8)
                    if mods, item.origin?.api != nil, let update = content.updates[instance.id]?[item.logicalName.lowercased()] {
                        Button("Обновить", systemImage: "arrow.down.circle") { content.update(item, in: instance) }
                            .buttonStyle(.glass).help("Fabric API \(update.version)").disabled(disabledReason != nil)
                    }
                    Menu {
                        Button("Показать в Finder", systemImage: "folder") { NSWorkspace.shared.activateFileViewerSelecting([item.url]) }
                        if let origin = item.origin { Button("Открыть страницу на \(origin.source.title)", systemImage: "arrow.up.right.square") { NSWorkspace.shared.open(origin.pageURL) } }
                        Divider()
                        Button("Удалить", systemImage: "trash", role: .destructive) { content.delete(item, in: instance, mods: mods) }.disabled(disabledReason != nil)
                    } label: { Image(systemName: "ellipsis").frame(width: 24, height: 24) }.menuStyle(.borderlessButton).fixedSize().help("Действия с файлом")
                }.padding(14).background(.white.opacity(0.45), in: .rect(cornerRadius: 12))
            }
        }
        .instanceSurface()
        .task(id: "\(instance.folderName):\(instance.state.rawValue)") {
            await content.reload(instance, mods: mods)
            if mods { await content.checkUpdates(instance) }
        }
        .onChange(of: scenePhase) { _, phase in if phase == .active { Task { await content.reload(instance, mods: mods) } } }
        .fileImporter(isPresented: $importing, allowedContentTypes: mods ? [UTType(filenameExtension: "jar") ?? .data] : [.zip, .folder], allowsMultipleSelection: true) { result in
            do { content.importFiles(try result.get(), into: instance, mods: mods) }
            catch { content.errors[instance.id] = error.localizedDescription }
        }
    }

    private func receiveDrop(_ providers: [NSItemProvider]) -> Bool {
        guard disabledReason == nil else { return false }
        Task {
            var urls: [URL] = []
            for provider in providers {
                let url: URL? = await withCheckedContinuation { continuation in
                    provider.loadObject(ofClass: NSURL.self) { item, _ in
                        continuation.resume(returning: (item as? NSURL).map { $0 as URL })
                    }
                }
                if let url { urls.append(url) }
            }
            if urls.isEmpty { content.errors[instance.id] = "Не удалось прочитать перетаскиваемые файлы." }
            else { content.importFiles(urls, into: instance, mods: mods) }
        }
        return true
    }
}

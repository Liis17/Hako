import AppKit
import SwiftUI
import UniformTypeIdentifiers

private enum InstanceSection: String, CaseIterable, Identifiable {
    case mods = "Моды", packs = "Текстурпаки", settings = "Настройки"
    var id: Self { self }
}

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
                    Text("Minecraft \(instance.versionID) · Vanilla · Java \(instance.javaMajorVersion)").foregroundStyle(.secondary)
                    Label("Наиграно: \(PlaytimeFormatter.string(playtime.instanceSeconds(instance.id, xuid: account?.xuid)))", systemImage: "clock")
                        .font(.callout).foregroundStyle(.secondary)
                }
                Spacer(minLength: 12)
                InstancePlayControls(instance: instance, account: account).frame(maxWidth: 260, alignment: .leading)
            }
            if instance.state != .ready { installationPanel }
            Picker("Раздел сборки", selection: Binding(get: { section }, set: requestSectionChange)) {
                ForEach(InstanceSection.allCases) { item in
                    Text(item.rawValue).tag(item)
                        .disabled(item == .mods)
                        .selectionDisabled(item == .mods)
                        .help(item == .mods ? "Моды доступны только для сборок с выбранным модлоадером." : "")
                }
            }.pickerStyle(.segmented).labelsHidden().frame(maxWidth: 600)
            ScrollView {
                Group {
                switch section {
                case .mods: InstanceFilesView(instance: instance, mods: true)
                case .packs: InstanceFilesView(instance: instance, mods: false)
                    case .settings: InstanceSettingsView(instance: instance, name: $nameDraft, onSaveName: { renameAction = .save })
                }
                }.frame(maxWidth: 800, alignment: .leading).padding(2).padding(.bottom, 32)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }.id(section)
        }.frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
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
                Text("Minecraft \(instance.versionID) · Vanilla").foregroundStyle(.secondary)
                Text("Папка: ~/.hako/\(instance.folderName)").font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
            }.instanceSurface()
            VStack(alignment: .leading, spacing: 18) {
                Text("Параметры запуска").font(.title3.weight(.semibold))
                InstanceParametersEditor(draft: $draft, isValid: $parametersValid, manifest: manifest)
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
        .task(id: draft.argumentSource) { if draft.argumentSource == .mojang && manifest == nil { await loadManifest() } }
    }

    private func saveSettings() {
        guard parametersValid else { return }
        do {
            try installations.store.updateSettings(instance, with: draft)
            draft = InstanceDraft(instance: instance)
            error = nil
        } catch { error = error.localizedDescription }
    }

    private func loadManifest() async {
        manifestError = nil
        do {
            guard let url = URL(string: instance.metadataURL) else { throw MojangError.invalid("Не удалось прочитать ссылку описания версии.") }
            let local = try InstanceStorage.containedURL("minecraft/versions/\(instance.versionID)/\(instance.versionID).json", in: installations.store.storage.directory(instance.folderName))
            let result = try await installations.client.manifest(.init(url: url, sha1: instance.metadataSHA1), installedAt: local)
            guard !Task.isCancelled else { return }
            guard result.id == instance.versionID else { throw MojangError.invalid("Описание версии не соответствует сборке.") }
            manifest = result
        } catch { if !Task.isCancelled { manifestError = error.localizedDescription } }
    }
}

private struct InstanceFilesView: View {
    let instance: GameInstance
    let mods: Bool
    @Environment(InstallationCoordinator.self) private var installations
    @Environment(\.scenePhase) private var scenePhase
    @State private var content = InstanceContent()
    @State private var items: [InstanceContentItem] = []
    @State private var error: String?
    @State private var importing = false
    @State private var pendingImports: [URL] = []
    @State private var replacement: URL?
    @State private var deleting: InstanceContentItem?

    private var busy: Bool { installations.contentBusy.contains(instance.id) }
    private func folder() throws -> URL {
        try InstanceStorage.containedURL("minecraft/\(mods ? "mods" : instance.legacyTexturepacks ? "texturepacks" : "resourcepacks")", in: installations.store.storage.directory(instance.folderName))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack {
                Text(mods ? "Моды" : "Текстурпаки").font(.title3.weight(.semibold))
                Spacer()
                Button("Открыть папку", systemImage: "folder") {
                    do { let folder = try folder(); try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true); NSWorkspace.shared.open(folder) }
                    catch { self.error = error.localizedDescription }
                }.buttonStyle(.glass)
                if !mods { Button("Добавить…", systemImage: "plus") { importing = true }.buttonStyle(.glass).disabled(busy) }
            }
            if mods { Text("Vanilla не поддерживает моды. Установка загрузчиков появится позже.").font(.callout).foregroundStyle(.secondary) }
            if busy { ProgressView("Копируем текстурпак…") }
            if let error { Text(error).font(.callout).foregroundStyle(Color.shu) }
            if items.isEmpty {
                Text(mods ? "В папке сборки нет модов." : "Текстурпаков пока нет. Добавьте ZIP-файл или папку с компьютера.")
                    .foregroundStyle(.secondary).frame(maxWidth: .infinity, alignment: .leading).padding(.vertical, 24)
            } else {
                ForEach(items) { item in
                    HStack(spacing: 12) {
                        Image(systemName: mods ? "puzzlepiece.extension" : item.isDirectory ? "folder" : "doc.zipper").foregroundStyle(Color.sakuraDeep)
                        Text(item.name).lineLimit(2).textSelection(.enabled)
                        Spacer()
                        if !mods { Button { deleting = item } label: { Image(systemName: "trash") }.buttonStyle(.plain).help("Удалить текстурпак").disabled(busy) }
                    }.padding(14).background(.white.opacity(0.45), in: .rect(cornerRadius: 12))
                }
            }
        }.instanceSurface()
        .task(id: instance.folderName) { await reload() }
        .onChange(of: instance.state) { Task { await reload() } }
        .onChange(of: scenePhase) { _, phase in if phase == .active { Task { await reload() } } }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.zip, .folder], allowsMultipleSelection: true) { result in
            do { pendingImports = try result.get(); Task { await importNext() } }
            catch { self.error = error.localizedDescription }
        }
        .alert("Заменить текстурпак?", isPresented: Binding(get: { replacement != nil }, set: { if !$0 { replacement = nil } }), presenting: replacement) { source in
            Button("Отмена", role: .cancel) { replacement = nil; Task { await importNext() } }
            Button("Заменить", role: .destructive) { replacement = nil; Task { await importOne(source, replace: true); await importNext() } }
        } message: { source in Text("Текстурпак \(source.lastPathComponent) уже существует в сборке.") }
        .alert("Удалить текстурпак?", isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }), presenting: deleting) { item in
            Button("Отмена", role: .cancel) { deleting = nil }
            Button("Удалить", role: .destructive) {
                deleting = nil
                Task {
                    installations.contentBusy.insert(instance.id)
                    defer { installations.contentBusy.remove(instance.id) }
                    do { try await content.trash(item, in: folder()); await reload() }
                    catch { self.error = error.localizedDescription }
                }
            }
        } message: { _ in Text("Текстурпак будет перемещён в корзину.") }
    }

    private func reload() async {
        do { let result = try await content.list(at: folder(), mods: mods); if !Task.isCancelled { items = result; error = nil } }
        catch { if !Task.isCancelled { self.error = error.localizedDescription } }
    }
    private func importNext() async {
        while !pendingImports.isEmpty && replacement == nil {
            let source = pendingImports.removeFirst()
            await importOne(source, replace: false)
        }
    }
    private func importOne(_ source: URL, replace: Bool) async {
        installations.contentBusy.insert(instance.id)
        defer { installations.contentBusy.remove(instance.id) }
        do { try await content.importPack(from: source, into: folder(), replace: replace); await reload() }
        catch PackImportError.exists { replacement = source }
        catch { self.error = error.localizedDescription }
    }
}

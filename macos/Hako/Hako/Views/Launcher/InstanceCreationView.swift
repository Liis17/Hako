import AppKit
import SwiftUI

private enum VersionFilter: String, CaseIterable, Identifiable {
    case releases = "Релизы", snapshots = "Снапшоты", historical = "История", all = "Все"
    var id: Self { self }
    func includes(_ version: MinecraftVersion) -> Bool {
        switch self {
        case .releases: version.type == "release"
        case .snapshots: version.type == "snapshot"
        case .historical: version.type == "old_alpha" || version.type == "old_beta"
        case .all: true
        }
    }
}

struct InstanceCreationView: View {
    @Environment(InstallationCoordinator.self) private var installations
    @Environment(\.dismiss) private var dismiss
    let onCreated: () -> Void
    @State private var draft = InstanceDraft()
    @State private var catalog: MinecraftCatalog?
    @State private var catalogError: String?
    @State private var filter = VersionFilter.releases
    @State private var search = ""
    @State private var selectedID = ""
    @State private var prepared: PreparedInstallation?
    @State private var checking = false
    @State private var compatibilityError: String?
    @State private var unsupported = false
    @State private var advanced = false
    @State private var parametersValid = true
    @State private var cancelConfirmation = false
    @State private var saveError: String?
    @State private var saving = false

    private var versions: [MinecraftVersion] {
        (catalog?.versions ?? []).filter { filter.includes($0) && (search.isEmpty || $0.id.localizedCaseInsensitiveContains(search)) }
    }
    private var nameError: String? {
        do { _ = try installations.store.validateName(draft.name); return nil }
        catch { return error.localizedDescription }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            Text("Новая сборка").font(.system(size: 30, weight: .bold))
            ScrollView {
                VStack(alignment: .leading, spacing: 24) {
                    InstanceIconPicker(draft: $draft)
                    VStack(alignment: .leading, spacing: 8) {
                        HStack { Text("Название").font(.headline); Spacer(); Text("\(draft.name.count)/60").font(.caption).foregroundStyle(.secondary) }
                        TextField("Например, My Vanilla Pack", text: $draft.name).textFieldStyle(.roundedBorder).accessibilityLabel("Название сборки")
                        Text(nameError ?? "Пробелы в имени папки будут заменены на _.")
                            .font(.caption).foregroundStyle(nameError != nil && !draft.name.isEmpty ? Color.shu : .secondary)
                    }
                    VStack(alignment: .leading, spacing: 12) {
                        Text("Версия Minecraft").font(.headline)
                        if let catalogError {
                            Text(catalogError).font(.callout).foregroundStyle(Color.shu)
                            Button("Повторить загрузку каталога") { Task { await loadCatalog() } }.buttonStyle(.glass)
                        } else if catalog == nil {
                            ProgressView("Загружаем версии…")
                        } else {
                            Picker("Тип версий", selection: $filter) {
                                ForEach(VersionFilter.allCases) { Text($0.rawValue).tag($0) }
                            }.pickerStyle(.segmented).labelsHidden()
                            TextField("Поиск версии", text: $search).textFieldStyle(.roundedBorder)
                            if versions.isEmpty { Text("Версии не найдены.").foregroundStyle(.secondary) }
                            else {
                                Picker("Версия", selection: $selectedID) {
                                    ForEach(versions) { Text($0.id).tag($0.id) }
                                }.pickerStyle(.menu)
                            }
                            if checking { ProgressView("Проверяем совместимость с вашим Mac…").font(.callout) }
                            if let prepared { Label("Поддерживается · Java \(prepared.manifest.java.majorVersion)", systemImage: "checkmark.circle.fill").font(.callout).foregroundStyle(.secondary) }
                            if let compatibilityError {
                                Text(compatibilityError).font(.callout).foregroundStyle(Color.shu)
                                if !unsupported { Button("Повторить проверку") { Task { await checkCompatibility() } }.buttonStyle(.glass) }
                            }
                        }
                    }
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Загрузчик модов").font(.headline)
                        Picker("Загрузчик", selection: .constant("vanilla")) { Text("Vanilla — без загрузчика").tag("vanilla") }.disabled(true)
                        Text("Сейчас доступны только ванильные сборки.").font(.caption).foregroundStyle(.secondary)
                    }
                    VStack(alignment: .leading, spacing: 0) {
                        Button {
                            withAnimation { advanced.toggle() }
                        } label: {
                            HStack(spacing: 10) {
                                Image(systemName: advanced ? "chevron.down" : "chevron.right")
                                    .font(.system(size: 12, weight: .semibold))
                                    .foregroundStyle(.secondary)
                                Text("Дополнительные настройки")
                                Spacer(minLength: 0)
                            }
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)

                        if advanced {
                            InstanceParametersEditor(draft: $draft, isValid: $parametersValid)
                                .padding(.top, 16)
                        }
                    }
                    if let saveError { Text(saveError).foregroundStyle(Color.shu).font(.callout) }
                }.padding(.horizontal, 2).padding(.top, 6).padding(.bottom, 4)
            }
            HStack {
                Text("Java и файлы игры загрузятся в фоне.").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("Отмена") { cancelConfirmation = true }.buttonStyle(.glass).keyboardShortcut(.cancelAction)
                Button("Сохранить сборку", action: save).buttonStyle(.glassProminent).tint(.sakuraDeep)
                    .keyboardShortcut(.defaultAction)
                    .disabled(saving || prepared == nil || checking || nameError != nil || !parametersValid)
            }
        }
        .padding(28).frame(width: 660, height: 500)
        .interactiveDismissDisabled()
        .background { CreationDismissGuard { cancelConfirmation = true } }
        .alert("Отменить создание сборки?", isPresented: $cancelConfirmation) {
            Button("Продолжить создание", role: .cancel) {}
            Button("Отменить создание", role: .destructive) { dismiss() }
        } message: { Text("Название, иконка и выбранные параметры не будут сохранены.") }
        .task { await loadCatalog() }
        .task(id: selectedID) { await checkCompatibility() }
        .onChange(of: filter) { selectVisibleVersion() }
        .onChange(of: search) { selectVisibleVersion() }
    }

    private func selectVisibleVersion() {
        if !versions.contains(where: { $0.id == selectedID }) { selectedID = versions.first?.id ?? "" }
    }
    private func loadCatalog() async {
        catalogError = nil
        do {
            let result = try await installations.client.catalog()
            guard !Task.isCancelled else { return }
            catalog = result
            selectedID = result.latest["release"] ?? result.versions.first?.id ?? ""
        } catch { if !Task.isCancelled { catalogError = error.localizedDescription } }
    }
    private func checkCompatibility() async {
        prepared = nil; compatibilityError = nil; unsupported = false
        guard let version = catalog?.versions.first(where: { $0.id == selectedID }) else { checking = false; return }
        checking = true
        do {
            let result = try await installations.client.prepare(version)
            guard !Task.isCancelled, selectedID == version.id else { return }
            prepared = result; checking = false
        } catch {
            guard !Task.isCancelled, selectedID == version.id else { return }
            compatibilityError = error.localizedDescription
            if case MojangError.unsupported = error { unsupported = true }
            checking = false
        }
    }
    private func save() {
        guard let prepared, !saving, parametersValid else { return }
        saving = true
        do {
            _ = try installations.store.create(draft, versionID: prepared.version.id, metadataURL: prepared.version.url.absoluteString, metadataSHA1: prepared.version.sha1, javaMajorVersion: prepared.manifest.java.majorVersion, legacyTexturepacks: prepared.manifest.legacyTexturepacks)
            installations.scheduleQueuedInstallations()
            onCreated()
            dismiss()
        } catch { saveError = error.localizedDescription; saving = false }
    }
}

/// Локальный монитор перехватывает Escape/Cmd+W до системного закрытия окна.
private struct CreationDismissGuard: NSViewRepresentable {
    let cancel: () -> Void
    func makeNSView(context: Context) -> MonitorView { let view = MonitorView(); view.cancel = cancel; return view }
    func updateNSView(_ view: MonitorView, context: Context) { view.cancel = cancel }
    static func dismantleNSView(_ view: MonitorView, coordinator: ()) { view.stop() }

    final class MonitorView: NSView {
        var cancel: (() -> Void)?
        private var monitor: Any?
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            stop()
            guard window != nil else { return }
            monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
                guard let self, let window = self.window, event.window === window || event.window === window.sheetParent else { return event }
                let closeShortcut = event.modifierFlags.contains(.command) && (event.characters?.lowercased() == "w" || event.charactersIgnoringModifiers?.lowercased() == "w")
                if event.keyCode == 53 || closeShortcut {
                    self.cancel?(); return nil
                }
                return event
            }
        }
        func stop() { if let monitor { NSEvent.removeMonitor(monitor); self.monitor = nil } }
    }
}

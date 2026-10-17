import SwiftUI

struct ModpackCreationView: View {
    let project: ModrinthProject
    let minecraft: String?
    let client: ModrinthClient
    let onCreated: () -> Void
    @Environment(InstallationCoordinator.self) private var installations
    @Environment(\.dismiss) private var dismiss
    @State private var draft: InstanceDraft
    @State private var versions: [ModrinthVersion] = []
    @State private var selectedID = ""
    @State private var loading = true
    @State private var checking = false
    @State private var versionsError: String?
    @State private var preparationError: String?
    @State private var saveError: String?
    @State private var prepared: PreparedModpack?
    @State private var saving = false
    @State private var retry = UUID()

    init(project: ModrinthProject, minecraft: String?, client: ModrinthClient, onCreated: @escaping () -> Void) {
        self.project = project; self.minecraft = minecraft; self.client = client; self.onCreated = onCreated
        var draft = InstanceDraft(); draft.name = project.title
        _draft = State(initialValue: draft)
    }

    private var nameError: String? {
        do { _ = try installations.store.validateName(draft.name); return nil }
        catch { return error.localizedDescription }
    }
    private var preparationKey: String { "\(selectedID):\(retry)" }

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            Text("Установить модпак").font(.system(size: 30, weight: .bold))
            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    HStack(spacing: 14) {
                        InstanceIcon(symbol: draft.iconSymbol, data: draft.iconData).frame(width: 56, height: 56)
                        VStack(alignment: .leading, spacing: 4) {
                            Text(project.title).font(.headline)
                            Text(project.description).font(.callout).foregroundStyle(.secondary).lineLimit(3)
                        }
                    }
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Название").font(.headline)
                        TextField("Название сборки", text: $draft.name).textFieldStyle(.roundedBorder)
                        if let nameError { Text(nameError).font(.caption).foregroundStyle(Color.shu) }
                    }
                    VStack(alignment: .leading, spacing: 10) {
                        Text("Версия модпака").font(.headline)
                        if loading { ProgressView("Загружаем версии…") }
                        else if let versionsError {
                            Text(versionsError).foregroundStyle(Color.shu)
                            Button("Повторить") { Task { await loadVersions() } }.buttonStyle(.glass)
                        } else if versions.isEmpty {
                            Text("Нет поддерживаемых версий модпака для выбранной версии Minecraft.").foregroundStyle(.secondary)
                        } else {
                            Picker("Версия модпака", selection: $selectedID) {
                                ForEach(versions, id: \.id) { version in
                                    Text(verbatim: "\(version.number) · Minecraft \(version.gameVersions.joined(separator: ", ")) · \(version.loaders.contains("fabric") ? "Fabric" : "Vanilla") · \(version.channel)")
                                        .tag(version.id)
                                }
                            }.labelsHidden().pickerStyle(.menu)
                        }
                        if checking { ProgressView("Проверяем модпак и совместимость с вашим Mac…").font(.callout) }
                        if let prepared {
                            let manifest = prepared.plan.manifest
                            let loader = manifest.loaderVersion.map { "Fabric \($0)" } ?? "Vanilla"
                            Label("Minecraft \(manifest.minecraft) · \(loader) · Java \(prepared.minecraft.manifest.java.majorVersion)", systemImage: "checkmark.circle.fill")
                                .font(.callout).foregroundStyle(.secondary)
                        }
                        if let preparationError {
                            Text(preparationError).foregroundStyle(Color.shu).font(.callout)
                            Button("Повторить проверку") { retry = UUID() }.buttonStyle(.glass)
                        }
                    }
                    Text("Моды и настройки установятся в составе, выбранном автором модпака. Необязательные клиентские файлы тоже будут установлены.")
                        .font(.caption).foregroundStyle(.secondary)
                    if let saveError { Text(saveError).font(.callout).foregroundStyle(Color.shu) }
                }.padding(2)
            }
            HStack {
                Text("Установка продолжится в фоне.").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("Отмена") { dismiss() }.buttonStyle(.glass).keyboardShortcut(.cancelAction)
                Button("Создать сборку", action: save).buttonStyle(.glassProminent).tint(.sakuraDeep).keyboardShortcut(.defaultAction)
                    .disabled(saving || checking || prepared?.plan.versionID != selectedID || prepared == nil || nameError != nil)
            }
        }
        .padding(28).frame(width: 660, height: 500)
        .interactiveDismissDisabled(saving)
        .task { await loadVersions() }
        .task(id: preparationKey) { await prepare() }
        .task {
            if let icon = await installations.modpacks.icon(project.iconURL), !Task.isCancelled {
                draft.iconData = icon; draft.iconSymbol = ""
            }
        }
    }

    private func loadVersions() async {
        loading = true; versionsError = nil; prepared = nil; selectedID = ""
        do {
            let result = try await client.modpackVersions(project: project.id, minecraft: minecraft)
            guard !Task.isCancelled else { return }
            versions = result; selectedID = result.first?.id ?? ""; loading = false
        } catch {
            guard !Task.isCancelled else { return }
            versionsError = error.localizedDescription; loading = false
        }
    }

    private func prepare() async {
        let key = preparationKey
        prepared = nil; preparationError = nil; checking = false
        guard let version = versions.first(where: { $0.id == selectedID }) else { return }
        checking = true
        do {
            let result = try await installations.modpacks.prepare(project: project, version: version, minecraft: installations.client, fabric: installations.fabricClient)
            guard !Task.isCancelled, key == preparationKey else { return }
            prepared = result; checking = false
        } catch {
            guard !Task.isCancelled, key == preparationKey else { return }
            preparationError = error.localizedDescription; checking = false
        }
    }

    private func save() {
        guard let prepared, prepared.plan.versionID == selectedID, !checking, !saving, nameError == nil else { return }
        saving = true
        do {
            if let loader = prepared.plan.manifest.loaderVersion {
                draft.modLoader = .fabric
                draft.fabricConfiguration = .init(loaderVersion: loader, api: nil)
            }
            let game = prepared.minecraft
            _ = try installations.store.create(draft, versionID: game.version.id, metadataURL: game.version.url.absoluteString, metadataSHA1: game.version.sha1, javaMajorVersion: game.manifest.java.majorVersion, legacyTexturepacks: game.manifest.legacyTexturepacks, modpack: prepared.plan)
            installations.scheduleQueuedInstallations()
            dismiss()
            onCreated()
        } catch { saveError = error.localizedDescription; saving = false }
    }
}

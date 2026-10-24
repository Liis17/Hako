import SwiftUI

struct BackupRestoreSheet: View {
    @Bindable var coordinator: BackupRestoreCoordinator

    private var worldGameType: LocalizedStringKey {
        guard case .world(let manifest) = coordinator.currentKind else { return "Режим неизвестен" }
        switch manifest.gameType {
        case 0: return "Выживание"
        case 1: return "Творческий"
        case 2: return "Приключение"
        case 3: return "Наблюдатель"
        default: return "Режим неизвестен"
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            HStack(alignment: .top, spacing: 14) {
                Image(systemName: "archivebox.fill")
                    .font(.system(size: 26, weight: .semibold))
                    .foregroundStyle(Color.sakuraDeep)
                VStack(alignment: .leading, spacing: 5) {
                    Text("Восстановление из резервной копии").font(.title2.bold())
                    Text(coordinator.archiveName).font(.callout).foregroundStyle(.secondary).lineLimit(2)
                }
                Spacer(minLength: 0)
                if coordinator.queuedCount > 0 {
                    Text("В очереди: \(coordinator.queuedCount)").font(.caption).foregroundStyle(.secondary)
                }
            }

            if coordinator.isPreparing {
                HStack(spacing: 12) {
                    ProgressView()
                    Text("Проверяем архив и готовим изолированную распаковку…").font(.callout)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(18)
                .background(.regularMaterial, in: .rect(cornerRadius: 14))
            } else if let error = coordinator.errorMessage {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .font(.callout).foregroundStyle(Color.shu)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
            } else if let kind = coordinator.currentKind {
                Group {
                    switch kind {
                    case .instance(let manifest): instanceOptions(manifest)
                    case .world(let manifest): worldOptions(manifest)
                    }
                }
                .disabled(coordinator.isCommitting || coordinator.awaitingConfirmation)
            }

            HStack {
                Button(coordinator.errorMessage == nil ? "Отмена" : "Закрыть") {
                    coordinator.cancelOrDismiss()
                }
                .buttonStyle(.glass)
                .keyboardShortcut(.cancelAction)
                .disabled(coordinator.isCommitting)
                Spacer()
                if coordinator.isCommitting {
                    ProgressView().controlSize(.small)
                    Text("Восстанавливаем…").font(.callout).foregroundStyle(.secondary)
                } else if coordinator.errorMessage == nil && !coordinator.isPreparing {
                    Button("Восстановить") { coordinator.requestCommit() }
                        .buttonStyle(.glassProminent)
                        .tint(.sakuraDeep)
                        .keyboardShortcut(.defaultAction)
                        .disabled(!canCommit)
                }
            }
        }
        .padding(28)
        .frame(width: 680)
        .fixedSize(horizontal: false, vertical: true)
        .confirmationDialog(
            coordinator.confirmationTitle,
            isPresented: Binding(get: { coordinator.awaitingConfirmation }, set: { coordinator.awaitingConfirmation = $0 }),
            titleVisibility: .visible
        ) {
            Button("Восстановить", role: .destructive) { coordinator.commitConfirmed() }
            Button("Отмена", role: .cancel) { coordinator.awaitingConfirmation = false }
        } message: {
            Text(coordinator.confirmationMessage)
        }
        .interactiveDismissDisabled(coordinator.isCommitting)
    }

    @ViewBuilder private func instanceOptions(_ manifest: InstanceBackupManifest) -> some View {
        HStack(spacing: 16) {
            InstanceIcon(symbol: manifest.iconSymbol, data: manifest.iconPNG)
                .frame(width: 60, height: 60)
            VStack(alignment: .leading, spacing: 4) {
                Text(manifest.name).font(.title3.bold())
                Text("Minecraft \(manifest.minecraftVersion) · \(manifest.modLoader == "fabric" ? "Fabric \(manifest.loaderVersion ?? "")" : "Vanilla")")
                    .font(.callout).foregroundStyle(.secondary)
                Text("Копия от \(manifest.backupCreatedAt.formatted(date: .abbreviated, time: .shortened))")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(16)
        .background(.regularMaterial, in: .rect(cornerRadius: 16))

        Picker("Действие", selection: $coordinator.instanceAction) {
            Text("Новая сборка").tag(InstanceRestoreAction.newInstance)
            Text("Заменить совпадающую сборку").tag(InstanceRestoreAction.replace).disabled(coordinator.matchedInstance == nil)
        }
        .pickerStyle(.segmented)

        if coordinator.instanceAction == .replace {
            Picker("Сборка для замены", selection: $coordinator.instanceTargetID) {
                if let instance = coordinator.matchedInstance {
                    let reason = coordinator.installations.store.managementBlockedReason(instance)
                    Text("\(instance.name) · Minecraft \(instance.versionID) · \(instance.loaderTitle)\(reason.map { " · \($0)" } ?? "")")
                        .tag(Optional(instance.id))
                        .disabled(reason != nil)
                }
            }
            if let reason = coordinator.instanceTargetError {
                Text(reason).font(.caption).foregroundStyle(Color.shu)
            }
            Text("Будут восстановлены файлы и профиль запуска. Текущие файлы сборки заменятся; имя, папка, дата создания и время игры сохранятся.")
                .font(.caption).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        } else {
            TextField("Имя сборки", text: $coordinator.instanceName)
                .textFieldStyle(.roundedBorder)
            if let error = coordinator.instanceNameError {
                Text(error).font(.caption).foregroundStyle(Color.shu)
            }
        }
    }

    @ViewBuilder private func worldOptions(_ manifest: WorldBackupManifest) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(manifest.name).font(.title3.bold())
            Text("Папка: \(manifest.folderName) · Сохранение Minecraft \(manifest.saveVersion ?? "—")")
                .font(.callout).foregroundStyle(.secondary)
            HStack(spacing: 6) { Text("Режим игры:"); Text(worldGameType) }
                .font(.callout).foregroundStyle(.secondary)
            if let lastPlayed = manifest.lastPlayed {
                Text("Последняя игра: \(lastPlayed.formatted(date: .abbreviated, time: .shortened))")
                    .font(.caption).foregroundStyle(.secondary)
            }
            if let size = manifest.size {
                Text(size, format: .byteCount(style: .file)).font(.caption).foregroundStyle(.secondary)
            }
            Text("Источник: \(manifest.sourceInstance.name) · Minecraft \(manifest.sourceInstance.minecraftVersion) · \(manifest.sourceInstance.modLoader == "fabric" ? "Fabric \(manifest.sourceInstance.loaderVersion ?? "")" : "Vanilla")")
                .font(.callout).foregroundStyle(.secondary)
            Text("Копия от \(manifest.backupCreatedAt.formatted(date: .abbreviated, time: .shortened))")
                .font(.caption).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(16)
        .background(.regularMaterial, in: .rect(cornerRadius: 16))

        if coordinator.readyWorldTargets.isEmpty {
            Label("Нет готовых сборок для восстановления мира. Сначала создайте и установите сборку.", systemImage: "exclamationmark.triangle")
                .font(.callout).foregroundStyle(Color.shu)
                .fixedSize(horizontal: false, vertical: true)
        } else {
            Picker("Сборка назначения", selection: Binding(get: { coordinator.worldTargetID }, set: coordinator.selectWorldTarget)) {
                Text("Выберите готовую сборку").tag(Optional<UUID>.none)
                ForEach(coordinator.allInstances) { instance in
                    let reason = coordinator.worldTargetReason(for: instance)
                    Text("\(instance.name) · Minecraft \(instance.versionID) · \(instance.loaderTitle)\(reason.map { " · \($0)" } ?? "")")
                        .tag(Optional(instance.id))
                        .disabled(reason != nil)
                }
            }
            TextField("Название мира", text: $coordinator.worldName)
                .textFieldStyle(.roundedBorder)
            TextField("Папка мира", text: $coordinator.worldFolderName)
                .textFieldStyle(.roundedBorder)
            if let error = coordinator.worldTargetError {
                Text(error).font(.caption).foregroundStyle(Color.shu)
            }
            if let error = coordinator.worldNameError {
                Text(error).font(.caption).foregroundStyle(Color.shu)
            }
            if let error = coordinator.worldFolderError {
                Text(error).font(.caption).foregroundStyle(Color.shu)
            }
            if coordinator.worldFolderIsOccupied() {
                Picker("Если папка уже существует", selection: $coordinator.worldAction) {
                    Text("Добавить отдельную копию").tag(WorldRestoreAction.copy)
                    Text("Заменить существующий мир").tag(WorldRestoreAction.replace)
                }
                .pickerStyle(.segmented)
            }
            Text("Мир и его датапаки будут восстановлены в выбранную сборку. Minecraft и Fabric при необходимости могут отличаться; приложение запросит подтверждение перед записью.")
                .font(.caption).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var canCommit: Bool {
        guard !coordinator.isCommitting, !coordinator.isPreparing, coordinator.errorMessage == nil else { return false }
        switch coordinator.currentKind {
        case .instance:
            return coordinator.instanceAction == .newInstance
                ? coordinator.instanceNameError == nil
                : coordinator.instanceTargetError == nil
        case .world:
            return coordinator.worldTargetError == nil && coordinator.worldNameError == nil &&
                coordinator.worldFolderError == nil && !coordinator.worldName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        case nil: return false
        }
    }
}

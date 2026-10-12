import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct InstanceWorldsView: View {
    let instance: GameInstance
    let onDatapacks: (InstanceWorld) -> Void
    @Environment(InstanceContentController.self) private var content
    @Environment(\.scenePhase) private var scenePhase
    @State private var worlds: [InstanceWorld] = []
    @State private var loading = true
    @State private var error: String?
    @State private var request = UUID()
    @State private var refreshID = UUID()

    private var gameBusy: Bool { content.installations.store.launchBusy.contains(instance.id) }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack {
                Text("Миры").font(.title3.weight(.semibold))
                if !worlds.isEmpty { Text(worlds.count, format: .number).font(.caption.monospacedDigit()).foregroundStyle(.secondary) }
                Spacer()
                Button("Открыть папку", systemImage: "folder") {
                    do {
                        let root = try content.installations.store.storage.directory(instance.folderName)
                        let folder = try InstanceStorage.containedURL("minecraft/saves", in: root)
                        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                        NSWorkspace.shared.open(folder)
                    } catch { content.errors[instance.id] = error.localizedDescription }
                }.buttonStyle(.glass)
                Button("Обновить", systemImage: "arrow.clockwise") { Task { await reload() } }
                    .buttonStyle(.glass).disabled(loading)
            }
            if let error = error ?? content.errors[instance.id] {
                HStack(alignment: .top) {
                    Text(error).font(.callout).foregroundStyle(Color.shu).textSelection(.enabled)
                    Spacer()
                    Button("Повторить") { Task { await reload() } }.buttonStyle(.glass).disabled(loading)
                }
            }
            if loading && worlds.isEmpty {
                ProgressView("Читаем миры…").frame(maxWidth: .infinity).padding(.vertical, 32)
            } else if worlds.isEmpty && error == nil {
                ContentUnavailableView("Миров пока нет", systemImage: "globe.europe.africa", description: Text("Здесь появятся миры, созданные в этой сборке Minecraft."))
            }
            LazyVStack(spacing: 8) {
                ForEach(worlds) { world in
                    InstanceWorldRow(instance: instance, world: world, refreshID: refreshID) { onDatapacks(world) }
                }
            }
            if loading && !worlds.isEmpty { ProgressView().controlSize(.small) }
        }
        .instanceSurface()
        .task(id: "\(instance.folderName):\(instance.state.rawValue)") { await reload() }
        .onChange(of: scenePhase) { _, phase in if phase == .active { Task { await reload() } } }
        .onChange(of: gameBusy) { wasBusy, busy in if wasBusy && !busy { Task { await reload() } } }
        .onChange(of: content.worldRevisions[instance.id]) { Task { await reload(clearError: false) } }
        .onDisappear { request = UUID() }
        .alert("Резервная копия мира создана", isPresented: Binding(get: { content.worldBackupURLs[instance.id] != nil }, set: { if !$0 { content.worldBackupURLs[instance.id] = nil } }), presenting: content.worldBackupURLs[instance.id]) { url in
            Button("Показать в Finder") { NSWorkspace.shared.activateFileViewerSelecting([url]) }
            Button("ОК", role: .cancel) {}
        } message: { url in
            Text("Файл \(url.lastPathComponent) сохранён в ~/.hako/worlds.")
        }
    }

    private func reload(clearError: Bool = true) async {
        let current = UUID(), folderName = instance.folderName
        request = current; loading = true; error = nil
        do {
            let root = try content.installations.store.storage.directory(folderName)
            let result = try await content.worlds.list(in: root)
            guard !Task.isCancelled, request == current, instance.folderName == folderName else { return }
            worlds = result
            refreshID = UUID()
            if clearError { content.errors[instance.id] = nil }
        } catch {
            guard !Task.isCancelled, request == current, instance.folderName == folderName else { return }
            self.error = error.localizedDescription
        }
        loading = false
    }
}

private struct InstanceWorldRow: View {
    let instance: GameInstance
    let world: InstanceWorld
    let refreshID: UUID
    let onDatapacks: () -> Void
    @Environment(InstanceContentController.self) private var content
    @Environment(\.locale) private var locale
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var expanded = false
    @State private var loading = false
    @State private var importing = false
    @State private var dropTargeted = false

    private var items: [InstanceContentItem] { content.datapacks[instance.id]?[world.id] ?? [] }
    private var target: ModrinthInstallTarget { .worldDatapacks(world.id) }
    private var blockedReason: String? { content.worldBlockedReason(instance) }
    private var message: String? { content.worldInstallMessages[instance.id]?[world.id] }

    private var gameType: LocalizedStringKey {
        switch world.gameType {
        case 0: "Выживание"
        case 1: "Творческий"
        case 2: "Приключение"
        case 3: "Наблюдатель"
        default: "Режим неизвестен"
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(alignment: .top, spacing: 14) {
                Button {
                    withAnimation(reduceMotion ? nil : .smooth(duration: 0.2)) { expanded.toggle() }
                } label: { header }
                .buttonStyle(.plain)
                .accessibilityHint("Показать или скрыть установленные датапаки")
                VStack(alignment: .trailing, spacing: 12) {
                    Button(action: showMenu) { Image(systemName: "ellipsis").foregroundStyle(.primary).frame(width: 24, height: 24) }
                        .buttonStyle(.glass).fixedSize()
                        .help("Действия с миром")
                        .accessibilityLabel("Действия с миром «\(world.name)»")
                    if let size = world.size { Text(size, format: .byteCount(style: .file)).font(.callout.monospacedDigit()).foregroundStyle(.secondary) }
                    else { Text("Размер недоступен").font(.caption).foregroundStyle(.secondary) }
                }
            }
            if let operation = content.worldOperations[instance.id]?[world.id] {
                HStack { ProgressView().controlSize(.small); Text(operation).font(.callout).foregroundStyle(.secondary) }
            }
            if expanded {
                Divider()
                HStack {
                    Text("Датапаки").font(.headline)
                    if !items.isEmpty { Text(items.count, format: .number).font(.caption.monospacedDigit()).foregroundStyle(.secondary) }
                    Spacer()
                    Button("Modrinth", systemImage: ModSource.modrinth.symbol, action: onDatapacks).buttonStyle(.glass)
                        .accessibilityLabel("Добавить датапак в мир «\(world.name)»")
                    Button("Добавить…", systemImage: "plus") { importing = true }
                        .buttonStyle(.glass).disabled(blockedReason != nil)
                }
                HStack {
                    Button("Проверить обновления", systemImage: "arrow.clockwise") {
                        Task { await reloadPacks() }
                    }.buttonStyle(.glass).disabled(loading || content.isCheckingUpdates(instance, target: target))
                    if content.hasUpdates(instance, target: target) {
                        Button("Обновить все", systemImage: "arrow.down.circle") { content.updateAll(instance, target: target) }
                            .buttonStyle(.glass).disabled(blockedReason != nil)
                            .help("Обновить все датапаки этого мира")
                    }
                }
                VStack(spacing: 8) {
                    Image(systemName: "square.and.arrow.down").font(.title2).foregroundStyle(Color.sakuraDeep)
                    Text("Перетащите ZIP-архивы датапаков").font(.callout.weight(.medium))
                    Text("Файлы будут скопированы в этот мир.").font(.caption).foregroundStyle(.secondary)
                    Text("Также можно добавить папку датапака через кнопку «Добавить».").font(.caption).foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity).padding(22)
                .background(dropTargeted ? Color.sakuraDeep.opacity(0.12) : .white.opacity(0.25), in: .rect(cornerRadius: 14))
                .overlay { RoundedRectangle(cornerRadius: 14).strokeBorder(dropTargeted ? Color.sakuraDeep : .secondary.opacity(0.35), style: StrokeStyle(lineWidth: 1.5, dash: [6, 5])) }
                .opacity(blockedReason == nil ? 1 : 0.6)
                .onDrop(of: [UTType.fileURL.identifier], isTargeted: $dropTargeted, perform: receiveDrop)
                if content.installations.contentBusy.contains(instance.id) { ProgressView("Обрабатываем файлы…").font(.callout) }
                if content.isCheckingUpdates(instance, target: target) { ProgressView("Проверяем обновления…").font(.caption) }
                if let message = content.updateMessage(instance, target: target) { Text(message).font(.caption).foregroundStyle(.secondary) }
                if loading && items.isEmpty { ProgressView("Читаем датапаки…").frame(maxWidth: .infinity).padding(.vertical, 12) }
                else if items.isEmpty { Text("В этом мире пока нет датапаков.").font(.callout).foregroundStyle(.secondary).padding(.vertical, 8) }
                ForEach(items) { item in
                    HStack(spacing: 12) {
                        InstanceFileIcon(item: item, mods: false)
                        VStack(alignment: .leading, spacing: 3) {
                            Text(item.name).font(.callout.weight(.medium)).lineLimit(2)
                            HStack(spacing: 8) {
                                Label(item.source.title, systemImage: item.source.symbol).font(.caption)
                                if let version = item.origin?.versionNumber { Text(version).font(.caption).foregroundStyle(.secondary) }
                                Text(item.enabled ? "Включён" : "Отключён").font(.caption).foregroundStyle(.secondary)
                            }
                        }
                        Spacer()
                        if let update = content.modrinthUpdate(for: item, in: instance, target: target) {
                            if content.catalogInstalling[instance.id] == update.projectID && content.catalogInstallTargets[instance.id] == target {
                                ProgressView().controlSize(.small).frame(width: 24)
                            } else {
                                Button { content.updateFromModrinth(item, in: instance, target: target) } label: {
                                    Image(systemName: "arrow.down.circle.fill").font(.title2).foregroundStyle(.orange)
                                }.buttonStyle(.plain).disabled(blockedReason != nil)
                                    .help("Обновить до \(update.version.number)").accessibilityLabel("Обновить \(item.name)")
                            }
                        }
                        Toggle("Включён", isOn: Binding(get: { item.enabled }, set: { content.setDatapackEnabled(item, world: world.id, in: instance, enabled: $0) }))
                            .toggleStyle(.switch).labelsHidden().disabled(blockedReason != nil)
                            .accessibilityLabel("Включить датапак «\(item.name)»")
                        Button { showDatapackMenu(item) } label: { Image(systemName: "ellipsis").frame(width: 24, height: 24) }
                            .buttonStyle(.glass).fixedSize().help("Действия с файлом")
                            .accessibilityLabel("Действия с датапаком «\(item.name)»")
                    }
                    .padding(10)
                    .background(.white.opacity(0.3), in: .rect(cornerRadius: 10))
                }
                if let blockedReason { Text(blockedReason).font(.caption).foregroundStyle(.secondary) }
                Text("Изменения датапаков применятся при следующем открытии мира.").font(.caption).foregroundStyle(.secondary)
            }
        }
        .padding(18)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.white.opacity(0.45), in: .rect(cornerRadius: 14))
        .task(id: "\(expanded):\(refreshID)") { if expanded { await reloadPacks() } }
        .onChange(of: scenePhase) { _, phase in if phase == .active && expanded { Task { await reloadPacks() } } }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.zip, .folder], allowsMultipleSelection: true) { result in
            do { content.importFiles(try result.get(), into: instance, target: target) }
            catch { content.errors[instance.id] = error.localizedDescription }
        }
    }

    private func reloadPacks() async {
        loading = true
        await content.reload(instance, target: .worldDatapacks(world.id), clearError: false)
        await content.checkAllUpdates(instance, target: target)
        loading = false
    }

    private func showMenu() {
        let menu = NSMenu(); menu.autoenablesItems = false
        menu.addItem(ClosureMenuItem(String(appLocalized: "Открыть в Finder"), systemImage: "folder") {
            do {
                let root = try content.installations.store.storage.directory(instance.folderName)
                NSWorkspace.shared.open(try InstanceWorlds.worldFolder(world: world.id, in: root))
            } catch { content.errors[instance.id] = error.localizedDescription }
        })
        menu.addItem(.separator())
        menu.addItem(ClosureMenuItem(String(appLocalized: "Сделать копию"), systemImage: "doc.on.doc", enabled: blockedReason == nil) { content.manageWorld(world, in: instance, action: .duplicate) })
        menu.addItem(ClosureMenuItem(String(appLocalized: "Создать резервную копию"), systemImage: "archivebox", enabled: blockedReason == nil) { content.manageWorld(world, in: instance, action: .backup) })
        menu.addItem(.separator())
        menu.addItem(ClosureMenuItem(String(appLocalized: "Удалить мир…"), systemImage: "trash", enabled: blockedReason == nil) { content.manageWorld(world, in: instance, action: .delete) })
        menu.popUp(positioning: nil, at: NSEvent.mouseLocation, in: nil)
    }

    private func showDatapackMenu(_ item: InstanceContentItem) {
        let menu = NSMenu(); menu.autoenablesItems = false
        menu.addItem(ClosureMenuItem(String(appLocalized: "Показать в Finder"), systemImage: "folder") { NSWorkspace.shared.activateFileViewerSelecting([item.url]) })
        if let origin = item.origin {
            menu.addItem(ClosureMenuItem(String(appLocalized: "Открыть страницу на \(origin.source.title)"), systemImage: "arrow.up.right.square") { NSWorkspace.shared.open(origin.pageURL) })
        }
        menu.popUp(positioning: nil, at: NSEvent.mouseLocation, in: nil)
    }

    private func receiveDrop(_ providers: [NSItemProvider]) -> Bool {
        guard blockedReason == nil else { return false }
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
            if urls.isEmpty { content.errors[instance.id] = String(appLocalized: "Не удалось прочитать перетаскиваемые файлы.") }
            else { content.importFiles(urls, into: instance, target: target) }
        }
        return true
    }

    private var header: some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: expanded ? "chevron.down" : "chevron.right")
                .font(.callout.weight(.semibold)).foregroundStyle(.secondary).frame(width: 12).padding(.top, 28)
            Group {
                if let data = world.iconData, let image = NSImage(data: data) {
                    Image(nsImage: image).resizable().interpolation(.none).scaledToFill()
                } else {
                    Image(systemName: "globe.europe.africa.fill").font(.title).foregroundStyle(Color.sakuraDeep)
                }
            }
            .frame(width: 76, height: 76)
            .background(.white.opacity(0.35), in: .rect(cornerRadius: 12))
            .clipShape(.rect(cornerRadius: 12))
            .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 5) {
                Text(world.name).font(.title3.weight(.semibold)).lineLimit(2)
                HStack(spacing: 8) {
                    if let version = world.version { Text("Minecraft \(version)") }
                    else { Text("Версия неизвестна") }
                    Text(verbatim: "·").accessibilityHidden(true)
                    Text(gameType)
                }.font(.callout).foregroundStyle(.secondary)
                if let date = world.lastPlayed {
                    Text("Последняя игра: \(date.formatted(.dateTime.day().month().year().hour().minute().locale(locale)))")
                        .font(.caption).foregroundStyle(.secondary)
                } else { Text("Дата последней игры неизвестна").font(.caption).foregroundStyle(.secondary) }
                if let error = world.metadataError { Text(error).font(.caption).foregroundStyle(Color.shu).textSelection(.enabled) }
                if let message { Label(message, systemImage: "checkmark.circle").font(.caption).foregroundStyle(.secondary) }
            }
            Spacer(minLength: 0)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .contentShape(.rect)
    }
}

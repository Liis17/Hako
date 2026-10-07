import AppKit
import SwiftUI

/// Каталог для сборки или её мира: точная версия Minecraft и загрузчик выбранного типа содержимого.
struct ModrinthCatalogView: View {
    let instance: GameInstance
    let target: ModrinthInstallTarget
    var worldName: String? = nil
    let onBack: () -> Void
    @Environment(InstanceContentController.self) private var content
    @Environment(\.scenePhase) private var scenePhase
    @State private var query = ""
    @State private var sort = ModrinthSort.relevance
    @State private var searchedQuery = ""
    @State private var projects: [ModrinthProject] = []
    @State private var total = 0
    @State private var loading = false
    @State private var loadError: String?
    @State private var moreError: String?
    @State private var installed: Set<String> = []
    /// Проект → канал → новейшая совместимая версия; нет записи, пока версии не загружены.
    @State private var channels: [String: [String: ModrinthVersion]] = [:]
    @State private var settling: String?
    @State private var position = ScrollPosition(edge: .top)
    @FocusState private var searchFocused: Bool

    private struct SearchKey: Equatable { let query: String; let sort: ModrinthSort }
    private var mods: Bool { target == .mods }
    private var title: LocalizedStringKey {
        switch target.kind { case .mod: "Моды с Modrinth"; case .resourcePack: "Ресурспаки с Modrinth"; case .datapack: "Датапаки с Modrinth" }
    }
    private var searchPlaceholder: LocalizedStringKey {
        switch target.kind { case .mod: "Поиск модов"; case .resourcePack: "Поиск ресурспаков"; case .datapack: "Поиск датапаков" }
    }
    private var installing: String? { content.catalogInstallTargets[instance.id] == target ? content.catalogInstalling[instance.id] : nil }

    init(instance: GameInstance, target: ModrinthInstallTarget, worldName: String? = nil, onBack: @escaping () -> Void) {
        self.instance = instance; self.target = target; self.worldName = worldName; self.onBack = onBack
    }

    init(instance: GameInstance, mods: Bool, onBack: @escaping () -> Void) {
        self.init(instance: instance, target: mods ? .mods : .packs, onBack: onBack)
    }
    private var disabledReason: String? { installing == nil ? content.disabledReason(instance, target: target) : nil }
    private var installedKey: [String] { content.items(instance, target: target).map { "\($0.name):\($0.enabled)" } }

    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            Button(target.world == nil ? "Назад к сборке" : "Назад к мирам", systemImage: "chevron.left", action: onBack)
                .buttonStyle(.plain).foregroundStyle(.secondary)
                .keyboardShortcut("[", modifiers: .command).help(target.world == nil ? "Назад к сборке (⌘[)" : "Назад к мирам (⌘[)")
            VStack(alignment: .leading, spacing: 6) {
                Text(title).font(.system(size: 44, weight: .heavy)).tracking(-1).lineLimit(1).minimumScaleFactor(0.55)
                if let world = target.world {
                    Text("\(worldName ?? world) · \(instance.name) · Minecraft \(instance.versionID)").lineLimit(1).foregroundStyle(.secondary)
                } else {
                    Text("\(instance.name) · Minecraft \(instance.versionID)\(mods ? " · \(instance.loaderTitle)" : "")").lineLimit(1).foregroundStyle(.secondary)
                }
            }
            HStack(spacing: 12) {
                searchField
                Menu {
                    Picker("Сортировка", selection: $sort) {
                        ForEach(ModrinthSort.allCases) { Text($0.title).tag($0) }
                    }.pickerStyle(.inline).labelsHidden()
                } label: { Label(sort.title, systemImage: "arrow.up.arrow.down") }
                    .menuStyle(.button).buttonStyle(.glass).controlSize(.large).fixedSize().help("Сортировка")
            }
            if let disabledReason { Text(disabledReason).font(.callout).foregroundStyle(.secondary) }
            if let error = content.errors[instance.id] { Text(error).font(.callout).foregroundStyle(Color.shu).textSelection(.enabled) }
            if let world = target.world, let message = content.worldInstallMessages[instance.id]?[world] {
                Label(message, systemImage: "checkmark.circle").font(.callout).foregroundStyle(.secondary)
            }
            ScrollView {
                feed.padding(2).padding(.bottom, 32)
            }
            .scrollPosition($position)
            .padding(.horizontal, -2)
        }
        .background {
            Button("Поиск") { searchFocused = true }.keyboardShortcut("f").opacity(0).allowsHitTesting(false).accessibilityHidden(true)
        }
        .onAppear { searchFocused = true }
        .task { await content.reload(instance, target: target) }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active { Task { await content.reload(instance, target: target); await refreshInstalled() } }
        }
        .task(id: SearchKey(query: query, sort: sort)) { await search() }
        .task(id: installedKey) { await refreshInstalled() }
        .onChange(of: installing) { finished, current in
            // Держим индикатор, пока список установленного не обновится.
            guard let finished, current == nil else { return }
            settling = finished
            Task { await refreshInstalled(); if settling == finished { settling = nil } }
        }
    }

    private var searchField: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass").foregroundStyle(.secondary).accessibilityHidden(true)
            TextField(searchPlaceholder, text: $query)
                .textFieldStyle(.plain).focused($searchFocused)
                .onExitCommand { query = "" }
            if loading && !projects.isEmpty {
                ProgressView().controlSize(.small)
            } else if !query.isEmpty {
                Button { query = "" } label: { Image(systemName: "xmark.circle.fill") }
                    .buttonStyle(.plain).foregroundStyle(.secondary).help("Очистить поиск").accessibilityLabel("Очистить поиск")
            }
        }
        .padding(.horizontal, 14).padding(.vertical, 9)
        .glassEffect(.regular, in: .capsule)
        .overlay { Capsule().strokeBorder(Color.sakuraDeep.opacity(searchFocused ? 0.55 : 0), lineWidth: 1.5) }
        .animation(.smooth(duration: 0.2), value: searchFocused)
    }

    @ViewBuilder private var feed: some View {
        VStack(alignment: .leading, spacing: 8) {
            if projects.isEmpty {
                Group {
                    if let loadError {
                        ContentUnavailableView {
                            Label("Каталог недоступен", systemImage: "wifi.exclamationmark")
                        } description: { Text(loadError) } actions: {
                            Button("Повторить") { Task { await search(force: true) } }.buttonStyle(.glass)
                        }
                    } else if loading {
                        ProgressView("Загружаем каталог…").padding(.vertical, 40)
                    } else {
                        ContentUnavailableView("Ничего не найдено", systemImage: "magnifyingglass", description: Text("Измените запрос или сортировку."))
                    }
                }.frame(maxWidth: .infinity)
            }
            LazyVStack(spacing: 8) {
                ForEach(projects) { project in
                    ModrinthCatalogRow(project: project, kind: target.kind, status: status(project), channels: channels[project.id]) { content.install(project, in: instance, target: target, channel: $0) }
                        .task(id: project.id) { await loadChannels(project) }
                }
                if !projects.isEmpty && projects.count < total { footer }
            }
            .opacity(loading && !projects.isEmpty ? 0.55 : 1)
            .animation(.smooth(duration: 0.2), value: loading)
        }
        .instanceSurface()
    }

    @ViewBuilder private var footer: some View {
        if let moreError {
            HStack {
                Text(moreError).font(.caption).foregroundStyle(Color.shu)
                Button("Повторить") { self.moreError = nil; Task { await loadMore() } }.buttonStyle(.glass)
            }.padding(.vertical, 12)
        } else {
            ProgressView().controlSize(.small).padding(.vertical, 12)
                .task(id: projects.count) { await loadMore() }
        }
    }

    private func status(_ project: ModrinthProject) -> ModrinthCatalogRow.Status {
        if installing == project.id || settling == project.id { return .installing }
        if installed.contains(project.id) { return .installed }
        return disabledReason == nil && installing == nil ? .available : .unavailable
    }

    private func search(force: Bool = false) async {
        // Задержка только при наборе текста: сортировка и повтор отвечают сразу.
        if query != searchedQuery && !force {
            try? await Task.sleep(for: .milliseconds(300))
            guard !Task.isCancelled else { return }
        }
        loading = true; loadError = nil
        do {
            let page = try await content.modrinth.search(query, kind: target.kind, minecraft: instance.versionID, sort: sort, offset: 0)
            guard !Task.isCancelled else { return }
            projects = page.hits; total = page.total; searchedQuery = query; moreError = nil
            position.scrollTo(edge: .top)
        } catch {
            guard !Task.isCancelled else { return }
            // Прежние результаты не соответствуют запросу, поэтому показываем ошибку с повтором.
            projects = []; total = 0; loadError = error.localizedDescription
        }
        loading = false
    }

    private func loadMore() async {
        let key = SearchKey(query: searchedQuery, sort: sort)
        do {
            let page = try await content.modrinth.search(key.query, kind: target.kind, minecraft: instance.versionID, sort: key.sort, offset: projects.count)
            guard !Task.isCancelled, key == SearchKey(query: searchedQuery, sort: sort) else { return }
            // Порядок выдачи может сместиться между страницами.
            let known = Set(projects.map(\.id))
            projects += page.hits.filter { !known.contains($0.id) }
            total = page.hits.isEmpty ? projects.count : page.total
        } catch {
            if !Task.isCancelled { moreError = error.localizedDescription }
        }
    }

    /// Ошибка не запоминается: при следующем появлении строки версии запросятся снова.
    private func loadChannels(_ project: ModrinthProject) async {
        guard channels[project.id] == nil, let versions = try? await content.modrinth.compatibleVersions(project: project.id, kind: target.kind, minecraft: instance.versionID) else { return }
        channels[project.id] = versions.reduce(into: [:]) { if $0[$1.channel] == nil { $0[$1.channel] = $1 } }
    }

    private func refreshInstalled() async {
        guard let found = try? await content.installedProjects(instance, target: target) else { return }
        installed = Set(found.keys)
    }
}

struct ModrinthCatalogRow: View {
    enum Status { case available, unavailable, installing, installed }
    let project: ModrinthProject
    let kind: ModrinthContentKind
    let status: Status
    /// `nil`, пока версии проекта загружаются.
    let channels: [String: ModrinthVersion]?
    let onAdd: (String) -> Void
    @State private var hovered = false

    private var channelNote: String? {
        guard let channels, channels["release"] == nil else { return nil }
        switch (channels["beta"] != nil, channels["alpha"] != nil) {
        case (true, true): return String(appLocalized: "Доступны только бета и альфа")
        case (true, false): return String(appLocalized: "Доступна только бета")
        case (false, true): return String(appLocalized: "Доступна только альфа")
        case (false, false): return String(appLocalized: "Нет совместимой версии")
        }
    }

    var body: some View {
        HStack(spacing: 14) {
            Button(action: openPage) {
                AsyncImage(url: project.iconURL) { phase in
                    if let image = phase.image { image.resizable().scaledToFit() }
                    else { Image(systemName: kind == .mod ? "puzzlepiece.extension.fill" : kind == .datapack ? "shippingbox.fill" : "photo.fill").font(.title2).foregroundStyle(Color.sakuraDeep) }
                }
                .frame(width: 44, height: 44)
                .background(.white.opacity(0.35), in: .rect(cornerRadius: 10))
                .clipShape(.rect(cornerRadius: 10))
            }
            .buttonStyle(.plain).pointerStyle(.link)
            .help("Открыть страницу на Modrinth").accessibilityLabel("Открыть «\(project.title)» на Modrinth")
            VStack(alignment: .leading, spacing: 4) {
                Button(action: openPage) {
                    Text(project.title).font(.headline).foregroundStyle(.primary).lineLimit(1).underline(hovered)
                }
                .buttonStyle(.plain).pointerStyle(.link).onHover { hovered = $0 }
                .help("Открыть страницу на Modrinth")
                if !project.description.isEmpty {
                    Text(project.description).font(.callout).foregroundStyle(.secondary).lineLimit(2)
                }
                if let channelNote { Text(channelNote).font(.caption).foregroundStyle(.secondary) }
            }
            Spacer(minLength: 12)
            Group {
                switch status {
                case .installing:
                    HStack(spacing: 8) { ProgressView().controlSize(.small); Text("Добавляем…").foregroundStyle(.secondary) }
                case .installed:
                    Label("Установлен", systemImage: "checkmark.circle.fill").foregroundStyle(.secondary)
                case .available, .unavailable:
                    HStack(spacing: 8) {
                        Button("Добавить", systemImage: "plus") { onAdd("release") }.buttonStyle(.glass)
                            .disabled(status == .unavailable || channels != nil && channels?["release"] == nil)
                            .accessibilityLabel("Добавить «\(project.title)»")
                        Button(action: showChannelMenu) { Image(systemName: "ellipsis").frame(maxHeight: .infinity) }.buttonStyle(.glass)
                            .help("Другие версии").accessibilityLabel("Другие версии «\(project.title)»")
                    }
                    .fixedSize()
                }
            }
            .frame(width: 180, alignment: .trailing)
            .transition(.opacity)
        }
        .padding(14)
        .background(.white.opacity(0.45), in: .rect(cornerRadius: 12))
        .animation(.smooth(duration: 0.25), value: status)
    }

    private func showChannelMenu() {
        let menu = NSMenu(); menu.autoenablesItems = false
        let beta = channels?["beta"], alpha = channels?["alpha"]
        for (channel, version, title) in [
            ("beta", beta, beta.map { String(appLocalized: "Установить бету \($0.number)") } ?? String(appLocalized: "Бета недоступна")),
            ("alpha", alpha, alpha.map { String(appLocalized: "Установить альфу \($0.number)") } ?? String(appLocalized: "Альфа недоступна")),
        ] {
            menu.addItem(ClosureMenuItem(title, enabled: version != nil && status != .unavailable) { onAdd(channel) })
        }
        menu.popUp(positioning: nil, at: NSEvent.mouseLocation, in: nil)
    }

    private func openPage() { NSWorkspace.shared.open(project.pageURL) }
}

import SwiftUI

struct ModpackCatalogView: View {
    let onBack: () -> Void
    let onCreated: () -> Void
    @Environment(InstallationCoordinator.self) private var installations
    @State private var catalog: ModpackCatalog
    @State private var versions: [MinecraftVersion] = []
    @State private var versionsError: String?
    @State private var selected: ModrinthProject?
    @State private var position = ScrollPosition(edge: .top)
    @FocusState private var searchFocused: Bool

    init(client: ModrinthClient, onBack: @escaping () -> Void, onCreated: @escaping () -> Void) {
        _catalog = State(initialValue: ModpackCatalog(client: client))
        self.onBack = onBack; self.onCreated = onCreated
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            Button("Назад к сборкам", systemImage: "chevron.left", action: onBack)
                .buttonStyle(.plain).foregroundStyle(.secondary).keyboardShortcut("[", modifiers: .command)
            VStack(alignment: .leading, spacing: 6) {
                Text("Сборки с Modrinth").font(.system(size: 44, weight: .heavy)).tracking(-1).lineLimit(1).minimumScaleFactor(0.55)
                Text("Готовые модпаки для Fabric и Vanilla").foregroundStyle(.secondary)
            }
            HStack(spacing: 12) {
                HStack(spacing: 10) {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("Поиск модпаков", text: $catalog.query).textFieldStyle(.plain).focused($searchFocused)
                    if !catalog.query.isEmpty {
                        Button { catalog.query = "" } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary) }
                            .buttonStyle(.plain).help("Очистить поиск").accessibilityLabel("Очистить поиск")
                    }
                }.padding(14).glassEffect(in: .rect(cornerRadius: 16))
                Menu {
                    Picker("Версия Minecraft", selection: $catalog.minecraft) {
                        Text("Все версии Minecraft").tag(nil as String?)
                        ForEach(versions) { Text(verbatim: $0.id).tag(Optional($0.id)) }
                    }
                } label: { Label(catalog.minecraft ?? String(appLocalized: "Все версии Minecraft"), systemImage: "line.3.horizontal.decrease") }
                    .menuStyle(.button).buttonStyle(.glass).controlSize(.large).fixedSize()
                    .accessibilityLabel("Версия Minecraft")
                Menu {
                    Picker("Сортировка", selection: $catalog.sort) {
                        ForEach(ModrinthSort.allCases) { Text($0.title).tag($0) }
                    }
                } label: { Label(catalog.sort.title, systemImage: "arrow.up.arrow.down") }
                    .menuStyle(.button).buttonStyle(.glass).controlSize(.large).fixedSize().accessibilityLabel("Сортировка")
            }
            if let versionsError {
                HStack {
                    Text(versionsError).font(.caption).foregroundStyle(Color.shu)
                    Button("Повторить загрузку версий") { Task { await loadVersions() } }.buttonStyle(.glass)
                }
            }
            ScrollView {
                LazyVStack(spacing: 0) {
                    if catalog.loading {
                        ProgressView("Загружаем модпаки…").frame(maxWidth: .infinity).padding(32)
                    } else if let error = catalog.error {
                        VStack(spacing: 12) {
                            Text(error).foregroundStyle(Color.shu)
                            Button("Повторить") { Task { await catalog.search(force: true) } }.buttonStyle(.glass)
                        }.frame(maxWidth: .infinity).padding(28)
                    } else {
                        ForEach(catalog.projects) { project in
                            row(project)
                            if project.id != catalog.projects.last?.id { Divider().padding(.horizontal, 20) }
                        }
                        if catalog.projects.isEmpty && !catalog.hasMore {
                            Text("Модпаки не найдены.").foregroundStyle(.secondary).frame(maxWidth: .infinity).padding(32)
                        }
                        if let error = catalog.moreError {
                            HStack {
                                Text(error).font(.caption).foregroundStyle(Color.shu)
                                Button("Повторить") { Task { await catalog.loadMore() } }.buttonStyle(.glass)
                            }.padding(20)
                        } else if catalog.hasMore {
                            ProgressView().padding(20).frame(maxWidth: .infinity)
                                .task(id: catalog.nextOffset) { await catalog.loadMore() }
                        }
                    }
                }.instanceSurface().padding(2).padding(.bottom, 32)
            }.scrollPosition($position).padding(.horizontal, -2)
        }
        .task { await loadVersions() }
        .task(id: catalog.key) { position.scrollTo(edge: .top); await catalog.search() }
        .onAppear { searchFocused = true }
        .sheet(item: $selected) { project in
            ModpackCreationView(project: project, minecraft: catalog.minecraft, client: catalog.client, onCreated: onCreated)
                .environment(installations)
        }
    }

    private func row(_ project: ModrinthProject) -> some View {
        HStack(spacing: 16) {
            AsyncImage(url: project.iconURL) { image in image.resizable().scaledToFit() } placeholder: {
                Image(systemName: "shippingbox.fill").font(.title).foregroundStyle(Color.sakuraDeep)
            }
            .frame(width: 56, height: 56).background(.white.opacity(0.35), in: .rect(cornerRadius: 12)).clipShape(.rect(cornerRadius: 12))
            VStack(alignment: .leading, spacing: 5) {
                Text(project.title).font(.headline).lineLimit(1)
                Text(project.description).font(.callout).foregroundStyle(.secondary).lineLimit(2)
            }.frame(maxWidth: .infinity, alignment: .leading)
            Button("Выбрать") { selected = project }.buttonStyle(.glass).accessibilityLabel("Выбрать \(project.title)")
        }.padding(20)
    }

    private func loadVersions() async {
        versionsError = nil
        do {
            let result = try await installations.client.catalog()
            guard !Task.isCancelled else { return }
            versions = result.versions
        } catch { if !Task.isCancelled { versionsError = error.localizedDescription } }
    }
}

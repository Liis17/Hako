//
//  SettingsView.swift
//  Hako
//

import AppKit
import SwiftUI
import SwiftData

private enum SettingsSection {
    case general
    case game
    case storage
    case about
}

struct SettingsView: View {
    @State private var section = SettingsSection.general

    var body: some View {
        LauncherPage(caption: "設定", title: "Настройки") {
            VStack(alignment: .leading, spacing: 24) {
                Picker("Раздел настроек", selection: $section) {
                    Text("Основные").tag(SettingsSection.general)
                    Text("Игра").tag(SettingsSection.game)
                    Text("Хранилище").tag(SettingsSection.storage)
                    Text("О приложении").tag(SettingsSection.about)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(maxWidth: 600, alignment: .leading)

                ScrollView {
                    Group {
                        switch section {
                        case .general:
                            GeneralSettingsView()
                        case .game:
                            GameSettingsView()
                        case .storage:
                            StorageSettingsView()
                        case .about:
                            AboutSettingsView()
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 2)
                    .padding(.bottom, 32)
                }
                .id(section)
            }
            .frame(maxWidth: 800, maxHeight: .infinity, alignment: .topLeading)
        }
    }
}

private struct GeneralSettingsView: View {
    var body: some View {
        SettingsCard(title: "Язык", systemImage: "globe") {
            AppLanguagePicker()
            Text("Язык интерфейса Hako. Меню и системные окна macOS используют язык системы.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }
}

private struct GameSettingsView: View {
    @AppStorage(GameLaunchDefaults.Key.javaPath) private var javaPath = GameLaunchDefaults.standard.javaPath
    @AppStorage(GameLaunchDefaults.Key.javaArguments) private var javaArguments = GameLaunchDefaults.standard.javaArguments
    @AppStorage(GameLaunchDefaults.Key.minecraftArguments) private var minecraftArguments = GameLaunchDefaults.standard.minecraftArguments
    @AppStorage(GameLaunchDefaults.Key.fullscreen) private var fullscreen = GameLaunchDefaults.standard.fullscreen
    @AppStorage(GameLaunchDefaults.Key.windowWidth) private var windowWidth = GameLaunchDefaults.standard.windowWidth
    @AppStorage(GameLaunchDefaults.Key.windowHeight) private var windowHeight = GameLaunchDefaults.standard.windowHeight
    @AppStorage(GameLaunchDefaults.Key.maximumMemoryMiB) private var maximumMemoryMiB = JavaMemoryPolicy.current.initialMiB

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            Text("Аргументы, память и путь Java для сборок с глобальными параметрами. Настройки окна служат начальными только при создании сборки. Изменения сохраняются автоматически.")
                .font(.callout)
                .foregroundStyle(.secondary)

            SettingsCard(title: "Java", systemImage: "terminal") {
                VStack(alignment: .leading, spacing: 8) {
                    Text("Путь к Java")
                        .font(.callout.weight(.medium))
                    HStack(spacing: 12) {
                        TextField("Не задан", text: $javaPath)
                            .font(.system(.body, design: .monospaced))
                            .textFieldStyle(.roundedBorder)
                            .accessibilityLabel("Путь к Java")

                        Button("Выбрать…", action: chooseJava)
                            .buttonStyle(.glass)
                    }
                    Text("Если путь задан, сборки с глобальными параметрами используют эту Java вместо рекомендуемой Java внутри сборки.")
                        .font(.caption).foregroundStyle(.secondary)
                }

                VStack(alignment: .leading, spacing: 8) {
                    Text("Аргументы запуска Java")
                        .font(.callout.weight(.medium))
                    TextField("Дополнительные аргументы Java", text: $javaArguments, axis: .vertical)
                        .font(.system(.body, design: .monospaced))
                        .textFieldStyle(.roundedBorder)
                        .lineLimit(2...4)
                        .accessibilityLabel("Аргументы запуска Java")
                }
                JavaMemorySlider(value: $maximumMemoryMiB)
                Text("-Xmx и MaxHeapSize из аргументов заменяются лимитом ползунка при запуске.").font(.caption).foregroundStyle(.secondary)
            }

            SettingsCard(title: "Minecraft", systemImage: "cube") {
                VStack(alignment: .leading, spacing: 8) {
                    Text("Аргументы запуска Minecraft")
                        .font(.callout.weight(.medium))
                    TextField("Дополнительные аргументы Minecraft", text: $minecraftArguments, axis: .vertical)
                        .font(.system(.body, design: .monospaced))
                        .textFieldStyle(.roundedBorder)
                        .lineLimit(2...4)
                        .accessibilityLabel("Аргументы запуска Minecraft")
                }
            }

            SettingsCard(title: "Окно", systemImage: "macwindow") {
                Toggle("Полноэкранный режим", isOn: $fullscreen)
                    .toggleStyle(.switch)
                    .tint(.sakuraDeep)

                HStack(alignment: .top, spacing: 20) {
                    WindowDimensionField(title: String(appLocalized: "Ширина"), value: $windowWidth)
                    WindowDimensionField(title: String(appLocalized: "Высота"), value: $windowHeight)
                }
                .disabled(fullscreen)

                Text("Размер окна указан в пикселях и применяется в оконном режиме.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private func chooseJava() {
        let panel = NSOpenPanel()
        panel.title = String(appLocalized: "Выбор Java")
        panel.message = String(appLocalized: "Выберите файл java в папке bin установленной Java.")
        panel.prompt = String(appLocalized: "Выбрать")
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.treatsFilePackagesAsDirectories = true
        panel.begin { response in
            guard response == .OK, let url = panel.url else { return }
            javaPath = url.path
            url.stopAccessingSecurityScopedResource()
        }
    }
}

private struct WindowDimensionField: View {
    let title: String
    @Binding var value: Int
    @State private var text: String

    init(title: String, value: Binding<Int>) {
        self.title = title
        _value = value
        _text = State(initialValue: String(value.wrappedValue))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title)
                .font(.callout.weight(.medium))
            HStack {
                TextField(title, text: $text)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityLabel("\(title) окна в пикселях")
                Text("px")
                    .foregroundStyle(.secondary)
            }
            if GameLaunchDefaults.windowDimension(from: text) == nil {
                Text("Введите целое число больше 0")
                    .font(.caption)
                    .foregroundStyle(Color.shu)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .onChange(of: text) { _, newValue in
            if let dimension = GameLaunchDefaults.windowDimension(from: newValue) {
                value = dimension
            }
        }
        .onChange(of: value) { _, newValue in
            if GameLaunchDefaults.windowDimension(from: text) != newValue {
                text = String(newValue)
            }
        }
    }
}

private struct DiskSpace {
    let volumeName: String
    let total: Int
    let free: Int

    var used: Int { total - free }
    var fractionUsed: Double { Double(used) / Double(total) }

    static func load(from url: URL = FileManager.default.homeDirectoryForCurrentUser) throws -> DiskSpace {
        let values = try url.resourceValues(forKeys: [.volumeNameKey, .volumeTotalCapacityKey, .volumeAvailableCapacityKey])
        guard let total = values.volumeTotalCapacity, total > 0,
              let free = values.volumeAvailableCapacity, free >= 0 else {
            throw CocoaError(.fileReadUnknown)
        }
        return DiskSpace(volumeName: values.volumeName ?? String(appLocalized: "Диск с данными Hako"), total: total, free: min(free, total))
    }
}

private struct StorageSettingsView: View {
    @Environment(\.locale) private var locale
    @Environment(\.scenePhase) private var scenePhase
    @Environment(InstallationCoordinator.self) private var installations
    @Query private var instances: [GameInstance]
    @State private var diskSpace: Result<DiskSpace, Error>?
    @State private var usage: InstanceStorageUsage?
    @State private var storageError: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            SettingsCard(title: "Место на диске", systemImage: "internaldrive") {
                switch diskSpace {
                case .success(let space):
                    Text(space.volumeName)
                        .foregroundStyle(.secondary)
                    let instanceBytes = min(usage?.total ?? 0, Int64(space.used))
                    StorageBar(segments: [
                        .init(color: .sakuraDeep, bytes: instanceBytes),
                        .init(color: .secondary.opacity(0.45), bytes: Int64(space.used) - instanceBytes)
                    ], total: Int64(space.total))
                        .accessibilityElement()
                        .accessibilityLabel("Занято на диске")
                        .accessibilityValue("\(formatBytes(space.used)) из \(formatBytes(space.total))")
                    HStack(alignment: .top) {
                        diskAmount("Занято", bytes: space.used)
                        Spacer()
                        diskAmount("Свободно", bytes: space.free)
                        Spacer()
                        diskAmount("Всего", bytes: space.total)
                    }
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Сборки Minecraft")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        if let usage {
                            let share = percent(Double(usage.total) / Double(space.total))
                            Text("\(formatBytes(usage.total)) · \(share) диска")
                                .fontWeight(.medium)
                        } else {
                            Text("—").fontWeight(.medium)
                        }
                    }
                case .failure:
                    Text("Не удалось получить данные")
                        .foregroundStyle(.secondary)
                case nil:
                    ProgressView("Получаем информацию о диске…")
                }
            }

            SettingsCard(title: "Кеш", systemImage: "tray") {
                HStack {
                    Text("Объём кеша игры")
                        .foregroundStyle(.secondary)
                    Spacer()
                    Text("0 Б")
                        .fontWeight(.medium)
                }
                ProgressView(value: 0)
                    .tint(.sakuraDeep)
                    .accessibilityLabel("Размер кеша игры")
                    .accessibilityValue("0 Б")
                Text("Кеш игры пока не создан.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }

            SettingsCard(title: "Сборки Minecraft", systemImage: "shippingbox") {
                HStack {
                    Text("Занимают на диске")
                        .foregroundStyle(.secondary)
                    Spacer()
                    Text(usage.map { formatBytes($0.total) } ?? "—")
                        .fontWeight(.medium)
                }
                if let usage, usage.total > 0 {
                    StorageBar(segments: InstanceStorageUsage.Category.allCases.map { .init(color: $0.color, bytes: usage[$0]) }, total: usage.total)
                        .accessibilityHidden(true)
                    VStack(spacing: 10) {
                        ForEach(InstanceStorageUsage.Category.allCases, id: \.self) { category in
                            HStack(spacing: 10) {
                                Circle()
                                    .fill(category.color)
                                    .frame(width: 10, height: 10)
                                Text(category.title)
                                Spacer()
                                Text(percent(Double(usage[category]) / Double(usage.total)))
                                    .foregroundStyle(.secondary)
                                    .frame(minWidth: 56, alignment: .trailing)
                                Text(formatBytes(usage[category]))
                                    .fontWeight(.medium)
                                    .frame(minWidth: 80, alignment: .trailing)
                            }
                            .font(.callout.monospacedDigit())
                            .accessibilityElement(children: .combine)
                        }
                    }
                }
                Text(storageError ?? (instances.isEmpty ? String(appLocalized: "Сборок пока нет.") : String(appLocalized: "\(instances.count) сборок · ~/.hako")))
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
        }
        .task { refreshDiskSpace(); await refreshInstanceSize() }
        .onChange(of: instances.map { "\($0.folderName):\($0.installationState)" }) { Task { await refreshInstanceSize() } }
        .onChange(of: installations.contentBusy) { Task { await refreshInstanceSize() } }
        .onChange(of: scenePhase) { _, newValue in
            if newValue == .active { refreshDiskSpace(); Task { await refreshInstanceSize() } }
        }
    }

    private func refreshInstanceSize() async {
        let storage = installations.store.storage
        do {
            usage = try await Task.detached(priority: .utility) { try storage.usage() }.value
            storageError = nil
        } catch { storageError = String(appLocalized: "Не удалось получить размер сборок.") }
    }

    private func refreshDiskSpace() {
        diskSpace = Result { try DiskSpace.load() }
    }

    private func diskAmount(_ title: LocalizedStringKey, bytes: Int) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title)
                .font(.caption)
                .foregroundStyle(.secondary)
            Text(formatBytes(bytes))
                .fontWeight(.medium)
        }
    }

    private func formatBytes(_ bytes: some BinaryInteger) -> String {
        Int64(bytes).formatted(.byteCount(style: .file).locale(locale))
    }

    private func percent(_ fraction: Double) -> String {
        fraction.formatted(.percent.precision(.fractionLength(0...1)).locale(locale))
    }
}

private extension InstanceStorageUsage.Category {
    var title: LocalizedStringKey {
        switch self {
        case .worlds: "Миры"
        case .minecraft: "Файлы Minecraft"
        case .screenshots: "Скриншоты"
        case .java: "Java"
        case .mods: "Моды"
        case .datapacks: "Датапаки"
        case .resourcepacks: "Ресурспаки"
        case .backups: "Резервные копии"
        }
    }

    var color: Color {
        switch self {
        case .worlds: .sakuraDeep
        case .minecraft: Color(hex: 0x7C8DB5)
        case .screenshots: Color(hex: 0xF2884B)
        case .java: Color(hex: 0xE5B93C)
        case .mods: Color(hex: 0x4FA37A)
        case .datapacks: Color(hex: 0x3FA7B8)
        case .resourcepacks: Color(hex: 0x8E6BBF)
        case .backups: Color(hex: 0x9A9A9A)
        }
    }
}

/// Сегментированная полоса: ширина сегмента — доля `bytes` от `total`, остаток остаётся пустым.
private struct StorageBar: View {
    struct Segment: Identifiable {
        let id = UUID()
        let color: Color
        let bytes: Int64
    }

    let segments: [Segment]
    let total: Int64

    var body: some View {
        GeometryReader { proxy in
            HStack(spacing: 0) {
                ForEach(segments.filter { $0.bytes > 0 }) { segment in
                    Rectangle()
                        .fill(segment.color)
                        .frame(width: max(2, proxy.size.width * Double(segment.bytes) / Double(max(total, 1))))
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .frame(height: 12)
        .background(.quaternary, in: Capsule())
        .clipShape(Capsule())
    }
}

private struct AboutSettingsView: View {
    private var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "—"
    }

    private var buildNumber: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "—"
    }

    @Environment(AppUpdateCoordinator.self) private var updates

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            aboutCard
            AppUpdateCard()
        }
    }

    private var aboutCard: some View {
        SettingsCard(title: "Hako", systemImage: "info.circle") {
            HStack(spacing: 20) {
                Image(nsImage: NSApplication.shared.applicationIconImage)
                    .resizable()
                    .scaledToFit()
                    .frame(width: 88, height: 88)
                    .accessibilityHidden(true)

                VStack(alignment: .leading, spacing: 8) {
                    Text("Лаунчер Minecraft для macOS")
                        .font(.title3)
                    Group {
                        if let commit = updates.currentCommit {
                            Text("Версия \(version) · сборка \(buildNumber) · \(String(commit.prefix(7)))")
                        } else {
                            Text("Версия \(version) · сборка \(buildNumber) · локальная сборка")
                        }
                    }
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
                }
            }

            Link(destination: URL(string: "https://github.com/Liis17/Hako")!) {
                Label("Hako на GitHub", systemImage: "arrow.up.right")
            }
            .buttonStyle(.glass)
            .controlSize(.large)

            Text("Hako is not affiliated with or endorsed by Mojang or Microsoft. Minecraft is a trademark of Microsoft.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .textSelection(.enabled)
        }
    }
}

private struct AppUpdateCard: View {
    @Environment(\.locale) private var locale
    @Environment(AppUpdateCoordinator.self) private var updates
    @AppStorage(AppUpdateCoordinator.automaticCheckKey) private var checkAutomatically = true

    var body: some View {
        SettingsCard(title: "Обновления", systemImage: "arrow.down.circle") {
            if updates.isLocalBuild {
                Text("Эта копия Hako собрана локально. Обновления приходят только в сборки из GitHub Releases.")
                    .foregroundStyle(.secondary)
                Link(destination: AppReleaseClient.releasesPage) {
                    Label("Открыть релизы", systemImage: "arrow.up.right")
                }
                .buttonStyle(.glass)
            } else {
                status
                if let error = updates.error {
                    Text(error)
                        .font(.callout)
                        .foregroundStyle(.red)
                        .textSelection(.enabled)
                }
                HStack(spacing: 12) {
                    if let release = updates.release {
                        Button("Обновить и перезапустить") { Task { await updates.install() } }
                            .buttonStyle(.glassProminent)
                            .tint(.sakuraDeep)
                        Link(destination: release.pageURL) {
                            Label("Открыть релиз", systemImage: "arrow.up.right")
                        }
                        .buttonStyle(.glass)
                    }
                    Button("Проверить сейчас") { Task { await updates.check() } }
                        .buttonStyle(.glass)
                }
                .controlSize(.large)
                .disabled(updates.activity != .idle)
                Toggle("Проверять обновления автоматически", isOn: $checkAutomatically)
                Text("Hako проверяет обновления при запуске и раз в 6 часов.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }

    @ViewBuilder private var status: some View {
        switch updates.activity {
        case .checking:
            ProgressView("Проверяем обновления…")
        case .downloading(let fraction):
            ProgressView("Загружаем обновление…", value: fraction)
                .tint(.sakuraDeep)
        case .installing:
            ProgressView("Устанавливаем обновление…")
        case .idle:
            if let release = updates.release {
                if let date = release.publishedAt {
                    Text("Доступна новая версия Hako: \(release.shortCommit) от \(date.formatted(.dateTime.day().month().hour().minute().locale(locale))).")
                } else {
                    Text("Доступна новая версия Hako: \(release.shortCommit).")
                }
            } else if updates.lastChecked != nil {
                Text("Установлена последняя версия Hako.")
                    .foregroundStyle(.secondary)
            } else {
                Text("Обновления ещё не проверялись.")
                    .foregroundStyle(.secondary)
            }
        }
    }
}

private struct SettingsCard<Content: View>: View {
    let title: LocalizedStringKey
    let systemImage: String
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Label(title, systemImage: systemImage)
                .font(.title3.weight(.semibold))
            content
        }
        .padding(20)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.sakuraDeep.opacity(0.1), in: .rect(cornerRadius: 22))
        .background(.regularMaterial, in: .rect(cornerRadius: 22))
        .overlay {
            RoundedRectangle(cornerRadius: 22)
                .strokeBorder(.white.opacity(0.5), lineWidth: 1)
        }
    }
}

#Preview {
    let container = try! ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
    return SettingsView()
        .modelContainer(container)
        .environment(InstallationCoordinator(context: container.mainContext))
        .environment(AppUpdateCoordinator(store: InstanceStore(context: container.mainContext)))
        .defaultAppStorage(UserDefaults(suiteName: "com.Launcher.Hako.settings.preview")!)
        .padding(.horizontal, 56)
        .padding(.top, 40)
        .background { SakuraBackground() }
        .frame(width: 1280, height: 720)
}

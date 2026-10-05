//
//  SettingsView.swift
//  Hako
//

import AppKit
import SwiftUI
import SwiftData

private enum SettingsSection {
    case game
    case storage
    case about
}

struct SettingsView: View {
    @State private var section = SettingsSection.game

    var body: some View {
        LauncherPage(caption: "設定", title: "Настройки") {
            VStack(alignment: .leading, spacing: 24) {
                Picker("Раздел настроек", selection: $section) {
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
                    WindowDimensionField(title: "Ширина", value: $windowWidth)
                    WindowDimensionField(title: "Высота", value: $windowHeight)
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
        panel.title = "Выбор Java"
        panel.message = "Выберите файл java в папке bin установленной Java."
        panel.prompt = "Выбрать"
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
        return DiskSpace(volumeName: values.volumeName ?? "Диск с данными Hako", total: total, free: min(free, total))
    }
}

private struct StorageSettingsView: View {
    @Environment(\.scenePhase) private var scenePhase
    @Environment(InstallationCoordinator.self) private var installations
    @Query private var instances: [GameInstance]
    @State private var diskSpace: Result<DiskSpace, Error>?
    @State private var instanceBytes: Int64?
    @State private var storageError: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            SettingsCard(title: "Место на диске", systemImage: "internaldrive") {
                switch diskSpace {
                case .success(let space):
                    Text(space.volumeName)
                        .foregroundStyle(.secondary)
                    ProgressView(value: space.fractionUsed)
                        .tint(.sakuraDeep)
                        .accessibilityLabel("Занято на диске")
                        .accessibilityValue("\(formatBytes(space.used)) из \(formatBytes(space.total))")
                    HStack(alignment: .top) {
                        diskAmount("Занято", bytes: space.used)
                        Spacer()
                        diskAmount("Свободно", bytes: space.free)
                        Spacer()
                        diskAmount("Всего", bytes: space.total)
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
                    Text(instanceBytes.map { $0.formatted(.byteCount(style: .file)) } ?? "—")
                        .fontWeight(.medium)
                }
                Text(storageError ?? (instances.isEmpty ? "Сборок пока нет." : "\(instances.count) сборок · ~/.hako"))
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
            let bytes = try await Task.detached(priority: .utility) { try storage.allocatedSize() }.value
            instanceBytes = bytes; storageError = nil
        } catch { storageError = "Не удалось получить размер сборок." }
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

    private func formatBytes(_ bytes: Int) -> String {
        Int64(bytes).formatted(.byteCount(style: .file).locale(Locale(identifier: "ru_RU")))
    }
}

private struct AboutSettingsView: View {
    private var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "—"
    }

    private var buildNumber: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "—"
    }

    var body: some View {
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
                    Text("Версия \(version) · сборка \(buildNumber)")
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
    let container = try! ModelContainer(for: Account.self, GameInstance.self, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
    return SettingsView()
        .modelContainer(container)
        .environment(InstallationCoordinator(context: container.mainContext))
        .defaultAppStorage(UserDefaults(suiteName: "com.Launcher.Hako.settings.preview")!)
        .padding(.horizontal, 56)
        .padding(.top, 40)
        .background { SakuraBackground() }
        .frame(width: 1280, height: 720)
}

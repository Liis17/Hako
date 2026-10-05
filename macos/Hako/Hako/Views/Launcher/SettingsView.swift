//
//  SettingsView.swift
//  Hako
//

import AppKit
import SwiftUI

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
                            Text("Информация о хранилище появится позже.")
                                .foregroundStyle(.secondary)
                        case .about:
                            Text("Информация о приложении появится позже.")
                                .foregroundStyle(.secondary)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 2)
                    .padding(.bottom, 32)
                }
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

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            Text("Настройки по умолчанию для новых сборок. Изменения сохраняются автоматически.")
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
        .glassEffect(.regular, in: .rect(cornerRadius: 22))
    }
}

#Preview {
    SettingsView()
        .defaultAppStorage(UserDefaults(suiteName: "com.Launcher.Hako.settings.preview")!)
        .padding(.horizontal, 56)
        .padding(.top, 40)
        .background { SakuraBackground() }
        .frame(width: 1280, height: 720)
}

import AppKit
import SwiftUI

struct InstanceWorldsView: View {
    let instance: GameInstance
    let onDatapacks: (InstanceWorld) -> Void
    @Environment(InstanceContentController.self) private var content
    @Environment(\.scenePhase) private var scenePhase
    @State private var worlds: [InstanceWorld] = []
    @State private var loading = true
    @State private var error: String?
    @State private var request = UUID()

    private var gameBusy: Bool { content.installations.store.launchBusy.contains(instance.id) }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack {
                Text("Миры").font(.title3.weight(.semibold))
                if !worlds.isEmpty { Text(worlds.count, format: .number).font(.caption.monospacedDigit()).foregroundStyle(.secondary) }
                Spacer()
                Button("Обновить", systemImage: "arrow.clockwise") { Task { await reload() } }
                    .buttonStyle(.glass).disabled(loading)
            }
            if let error {
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
                    InstanceWorldRow(world: world, message: content.worldInstallMessages[instance.id]?[world.id]) { onDatapacks(world) }
                }
            }
            if loading && !worlds.isEmpty { ProgressView().controlSize(.small) }
        }
        .instanceSurface()
        .task(id: "\(instance.folderName):\(instance.state.rawValue)") { await reload() }
        .onChange(of: scenePhase) { _, phase in if phase == .active { Task { await reload() } } }
        .onChange(of: gameBusy) { wasBusy, busy in if wasBusy && !busy { Task { await reload() } } }
        .onDisappear { request = UUID() }
    }

    private func reload() async {
        let current = UUID(), folderName = instance.folderName
        request = current; loading = true; error = nil
        do {
            let root = try content.installations.store.storage.directory(folderName)
            let result = try await content.worlds.list(in: root)
            guard !Task.isCancelled, request == current, instance.folderName == folderName else { return }
            worlds = result
        } catch {
            guard !Task.isCancelled, request == current, instance.folderName == folderName else { return }
            self.error = error.localizedDescription
        }
        loading = false
    }
}

private struct InstanceWorldRow: View {
    let world: InstanceWorld
    let message: String?
    let onDatapacks: () -> Void
    @Environment(\.locale) private var locale

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
        HStack(alignment: .top, spacing: 14) {
            Group {
                if let data = world.iconData, let image = NSImage(data: data) {
                    Image(nsImage: image).resizable().interpolation(.none).scaledToFill()
                } else {
                    Image(systemName: "globe.europe.africa.fill").font(.title).foregroundStyle(Color.sakuraDeep)
                }
            }
            .frame(width: 64, height: 64)
            .background(.white.opacity(0.35), in: .rect(cornerRadius: 12))
            .clipShape(.rect(cornerRadius: 12))
            .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 5) {
                Text(world.name).font(.headline).lineLimit(2)
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
            Spacer(minLength: 8)
            VStack(alignment: .trailing, spacing: 10) {
                if let size = world.size { Text(size, format: .byteCount(style: .file)).font(.callout.monospacedDigit()).foregroundStyle(.secondary) }
                else { Text("Размер недоступен").font(.caption).foregroundStyle(.secondary) }
                Button("Добавить датапак", systemImage: "plus", action: onDatapacks)
                    .buttonStyle(.glass).fixedSize()
                    .accessibilityLabel("Добавить датапак в мир «\(world.name)»")
            }
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.white.opacity(0.45), in: .rect(cornerRadius: 12))
    }
}

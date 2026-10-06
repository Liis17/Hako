import AppKit
import SwiftUI

struct InstancePlayControls: View {
    let instance: GameInstance
    var account: Account?
    var compact = false
    var playtime: String?
    @Environment(GameLaunchCoordinator.self) private var games

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 12) {
                if compact {
                    if let playtime {
                        Label("В игре: \(playtime)", systemImage: "clock").font(.callout).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer(minLength: 8)
                }
                Button("Играть", systemImage: "play.fill") { games.launch(instance, account: account) }
                    .buttonStyle(.glassProminent).tint(.sakuraDeep).controlSize(compact ? .regular : .large)
                    .disabled(games.disabledReason(instance, account: account) != nil)
                    .accessibilityLabel("Играть в \(instance.name)")
                if instance.offlineMode && !compact {
                    Label(instance.offlineUsername, systemImage: "wifi.slash").font(.caption).foregroundStyle(.secondary).lineLimit(1)
                        .help("Offline-mode: \(instance.offlineUsername)")
                }
            }
            if instance.offlineMode && compact {
                Label(instance.offlineUsername, systemImage: "wifi.slash").font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    .help("Offline-mode: \(instance.offlineUsername)").frame(maxWidth: .infinity, alignment: .trailing)
            }
            switch games.states[instance.id] {
            case .preparing:
                HStack(spacing: 8) { ProgressView().controlSize(.small); Text("Подготавливаем запуск…").font(.caption) }
            case .running:
                Label("Игра запущена", systemImage: "play.circle").font(.caption).foregroundStyle(.secondary)
            case .failed(let message):
                Text(message).font(.caption).foregroundStyle(Color.shu).lineLimit(compact ? 2 : nil).help(message)
                if let url = games.logURL(instance), FileManager.default.fileExists(atPath: url.path) {
                    Button("Журнал запуска", systemImage: "doc.text") { NSWorkspace.shared.open(url) }.buttonStyle(.plain).font(.caption)
                }
            case nil:
                if let reason = games.disabledReason(instance, account: account) {
                    Text(compact && instance.state == .ready && !instance.offlineMode ? "Нужен Minecraft-вход или offline-mode." : reason)
                        .font(.caption).foregroundStyle(.secondary).lineLimit(compact ? 2 : nil)
                }
            }
        }.help(games.disabledReason(instance, account: account) ?? "Запустить Minecraft")
    }
}

import SwiftUI

struct PlaytimeSummaryView: View {
    let xuid: String?
    @Environment(PlaytimeCoordinator.self) private var playtime

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 16) {
                Image(systemName: "clock").font(.system(size: 22)).foregroundStyle(.secondary)
                    .frame(width: 56)
                VStack(alignment: .leading, spacing: 4) {
                    Text("Время в Minecraft").font(.callout).foregroundStyle(.secondary)
                    Text(PlaytimeFormatter.string(playtime.totalSeconds(xuid: xuid)))
                        .font(.title2.weight(.semibold)).monospacedDigit()
                }
                Spacer(minLength: 0)
            }
            .accessibilityElement(children: .combine)
            if let error = playtime.errorMessage { Text(error).font(.caption).foregroundStyle(Color.shu) }
        }
        .padding(16)
        .glassEffect(.regular, in: .rect(cornerRadius: 22))
    }
}

struct HakoPlaytimeSummaryView: View {
    let ownerID: String
    @Environment(PlaytimeCoordinator.self) private var playtime

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Время Hako")
                .font(.callout)
                .foregroundStyle(.secondary)
            if let serverSeconds = playtime.hakoServerSeconds(ownerID: ownerID) {
                Text("На сервере: \(PlaytimeFormatter.string(serverSeconds))")
                    .font(.title3.weight(.semibold))
                    .monospacedDigit()
            } else {
                Text("Серверное время недоступно")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            Text("Ожидает синхронизации: \(PlaytimeFormatter.string(playtime.hakoPendingSeconds(ownerID: ownerID)))")
                .font(.callout)
                .foregroundStyle(.secondary)
                .monospacedDigit()
            if let error = playtime.hakoSyncErrorMessage {
                Text(error)
                    .font(.caption)
                    .foregroundStyle(Color.shu)
                    .textSelection(.enabled)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(16)
        .glassEffect(.regular, in: .rect(cornerRadius: 22))
    }
}

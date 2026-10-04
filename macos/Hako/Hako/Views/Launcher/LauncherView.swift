//
//  LauncherView.swift
//  Hako
//

import SwiftData
import SwiftUI

enum LauncherTab {
    case instances
    case settings
    case profile
}

/// Состояние автоматического подключения Minecraft к аккаунту Microsoft.
enum MinecraftStatus {
    case idle
    case connecting
    case failed(String)
}

/// Главная страница лаунчера: рейл вкладок слева и содержимое выбранной вкладки.
struct LauncherView: View {
    let account: Account

    @Environment(\.modelContext) private var modelContext
    @State private var tab = LauncherTab.instances
    @State private var minecraftStatus = MinecraftStatus.idle

    var body: some View {
        HStack(spacing: 0) {
            LauncherRail(account: account, selection: $tab)
                .padding(12)

            Group {
                switch tab {
                case .instances:
                    InstancesView()
                case .settings:
                    SettingsView()
                case .profile:
                    ProfileView(account: account, minecraftStatus: minecraftStatus)
                }
            }
            .id(tab)
            .transition(.blurReplace)
            .padding(.horizontal, 56)
            .padding(.top, 40)
        }
        .animation(.smooth, value: tab)
        .task { await connectMinecraftIfNeeded() }
    }

    /// При открытии лаунчера тихо пробует подключить Minecraft по сохранённому refresh token.
    private func connectMinecraftIfNeeded() async {
        guard account.minecraftUUID == nil, let tokens = TokenKeychain.load(for: account.xuid) else { return }
        minecraftStatus = .connecting
        do {
            let token = try await MicrosoftAuth.refresh(tokens.microsoftRefreshToken)
            // Microsoft выдаёт новый refresh token — сохраняем его, даже если Minecraft снова недоступен.
            var updated = AccountTokens(microsoftRefreshToken: token.refreshToken)
            try TokenKeychain.save(updated, for: account.xuid)

            let session = try await MicrosoftAuth.signInToMinecraft(with: token)
            updated.minecraftAccessToken = session.accessToken
            updated.minecraftTokenExpiration = session.expiration
            try TokenKeychain.save(updated, for: account.xuid)
            account.connect(session.profile)
            try modelContext.save()
            minecraftStatus = .idle
        } catch {
            // Отмена — лаунчер закрыт выходом из аккаунта; показывать нечего.
            guard !Task.isCancelled else { return }
            minecraftStatus = .failed(error.localizedDescription)
        }
    }
}

#Preview {
    let container = try! ModelContainer(for: Account.self, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
    let account = Account(xbox: XboxProfile(xuid: "preview", gamertag: "Steve", avatarURL: nil), email: "steve@example.com")
    container.mainContext.insert(account)

    return LauncherView(account: account)
        .modelContainer(container)
        .background { SakuraBackground() }
        .frame(width: 1280, height: 720)
}

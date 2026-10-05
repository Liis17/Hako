//
//  LauncherView.swift
//  Hako
//

import SwiftData
import SwiftUI

enum LauncherTab: Hashable {
    case instances
    case instance(UUID)
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
    @Environment(InstallationCoordinator.self) private var installations
    @Query(sort: \GameInstance.createdAt) private var instances: [GameInstance]
    @State private var tab = LauncherTab.instances
    @State private var minecraftStatus = MinecraftStatus.idle
    @State private var creatingInstance = false

    var body: some View {
        HStack(spacing: 0) {
            LauncherRail(account: account, instances: instances, selection: $tab, onCreate: { creatingInstance = true })
                .padding(12)

            Group {
                switch tab {
                case .instances:
                    InstancesView(instances: instances, onCreate: { creatingInstance = true }, onOpen: { tab = .instance($0.id) })
                case .instance(let id):
                    if let instance = instances.first(where: { $0.id == id }) {
                        InstanceProfileView(instance: instance, onBack: { tab = .instances })
                    }
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
        .sheet(isPresented: $creatingInstance) {
            InstanceCreationView(onCreated: { tab = .instances })
                .environment(installations)
        }
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
    let container = try! ModelContainer(for: Account.self, GameInstance.self, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
    let account = Account(xbox: XboxProfile(xuid: "preview", gamertag: "Steve", avatarURL: nil), email: "steve@example.com")
    container.mainContext.insert(account)

    return LauncherView(account: account)
        .modelContainer(container)
        .environment(InstallationCoordinator(context: container.mainContext))
        .background { SakuraBackground() }
        .frame(width: 1280, height: 720)
}

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

/// Главная страница лаунчера: рейл вкладок слева и содержимое выбранной вкладки.
struct LauncherView: View {
    let account: Account?
    var onSignIn: () -> Void = {}

    @Environment(InstallationCoordinator.self) private var installations
    @Environment(MinecraftSessionCoordinator.self) private var sessions
    @Query(sort: \GameInstance.createdAt) private var instances: [GameInstance]
    @State private var tab = LauncherTab.instances
    @State private var creatingInstance = false

    var body: some View {
        HStack(spacing: 0) {
            LauncherRail(account: account, instances: instances, selection: $tab, onCreate: { creatingInstance = true })
                .padding(12)

            Group {
                switch tab {
                case .instances:
                    InstancesView(instances: instances, account: account, onCreate: { creatingInstance = true }, onOpen: { tab = .instance($0.id) })
                case .instance(let id):
                    if let instance = instances.first(where: { $0.id == id }) {
                        InstanceProfileView(instance: instance, account: account, onBack: { tab = .instances })
                    }
                case .settings:
                    SettingsView()
                case .profile:
                    if let account { ProfileView(account: account, minecraftStatus: sessions.statuses[account.xuid] ?? .idle) }
                    else { GuestProfileView(onSignIn: onSignIn) }
                }
            }
            .id(tab)
            .transition(.blurReplace)
            .padding(.horizontal, 56)
            .padding(.top, 40)
        }
        .animation(.smooth, value: tab)
        .sheet(isPresented: $creatingInstance) {
            InstanceCreationView(onCreated: { tab = .instances })
                .environment(installations)
        }
    }

}

#Preview {
    let container = try! ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
    let account = Account(xbox: XboxProfile(xuid: "preview", gamertag: "Steve", avatarURL: nil), email: "steve@example.com")
    let installations = InstallationCoordinator(context: container.mainContext)
    let sessions = MinecraftSessionCoordinator(context: container.mainContext)
    let playtime = try! PlaytimeCoordinator(context: container.mainContext)
    container.mainContext.insert(account)

    return LauncherView(account: account)
        .modelContainer(container)
        .environment(installations)
        .environment(sessions)
        .environment(playtime)
        .environment(GameLaunchCoordinator(store: installations.store, sessions: sessions, playtime: playtime))
        .background { SakuraBackground() }
        .frame(width: 1280, height: 720)
}

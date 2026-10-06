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
    @Environment(InstanceRenameExitCoordinator.self) private var renameExit
    @Query(sort: \GameInstance.createdAt) private var instances: [GameInstance]
    @State private var tab = LauncherTab.instances
    @State private var creatingInstance = false
    @State private var pendingTab: LauncherTab?
    @State private var navigationError: String?

    var body: some View {
        let _ = renameExit.pendingRename
        let _ = renameExit.exitRequest
        HStack(spacing: 0) {
            LauncherRail(account: account, instances: instances, selection: Binding(get: { tab }, set: requestTab), onCreate: { creatingInstance = true })
                .padding(12)

            Group {
                switch tab {
                case .instances:
                    InstancesView(instances: instances, account: account, onCreate: { creatingInstance = true }, onOpen: { requestTab(.instance($0.id)) })
                case .instance(let id):
                    if let instance = instances.first(where: { $0.id == id }) {
                        InstanceProfileView(instance: instance, account: account, onBack: { requestTab(.instances) })
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
            InstanceCreationView(onCreated: { requestTab(.instances) })
                .environment(installations)
        }
        .alert(navigationError == nil ? "Переименовать сборку?" : "Не удалось переименовать сборку", isPresented: renamePromptPresented) {
            if navigationError != nil {
                Button("ОК", role: .cancel) { navigationError = nil }
            } else if renameExit.exitRequest == .closeWindow {
                Button("Переименовать и закрыть") { confirmPendingRename() }
                Button("Закрыть без переименования", role: .cancel) { declinePendingRename() }
            } else if renameExit.exitRequest == .terminateApplication {
                Button("Переименовать и выйти") { confirmPendingRename() }
                Button("Выйти без переименования", role: .cancel) { declinePendingRename() }
            } else {
                Button("Переименовать и перейти") { confirmPendingRename() }
                Button("Продолжить без переименования", role: .cancel) { declinePendingRename() }
            }
        } message: {
            Text(navigationError ?? renameWarning)
        }
    }

    private var renamePromptPresented: Binding<Bool> {
        Binding(
            get: { navigationError != nil || pendingTab != nil || renameExit.exitRequest != nil },
            set: { if !$0 { dismissRenamePrompt() } }
        )
    }

    private var renameWarning: String {
        guard let pending = renameExit.pendingRename else { return "Переименование изменит имя папки с файлами сборки в ~/.hako." }
        return "Папка с файлами сборки будет переименована в ~/.hako при сохранении имени «\(pending.name)»."
    }

    private func requestTab(_ destination: LauncherTab) {
        guard destination != tab else { return }
        if case .instance(let currentID) = tab,
           renameExit.pendingRename?.instanceID == currentID {
            pendingTab = destination
        } else {
            tab = destination
        }
    }

    private func confirmPendingRename() {
        guard let pending = renameExit.pendingRename,
              let instance = instances.first(where: { $0.id == pending.instanceID }) else {
            declinePendingRename()
            return
        }
        do {
            guard !installations.contentBusy.contains(instance.id) else {
                throw InstanceFileError.message("Дождитесь завершения операций с файлами перед переименованием.")
            }
            try installations.store.rename(instance, to: pending.name)
            renameExit.pendingRename = nil
            if renameExit.exitRequest != nil {
                pendingTab = nil
                renameExit.resumeExit()
            } else if let pendingTab {
                self.pendingTab = nil
                tab = pendingTab
            }
        } catch {
            pendingTab = nil
            renameExit.cancelExit()
            navigationError = error.localizedDescription
        }
    }

    private func declinePendingRename() {
        renameExit.pendingRename = nil
        if renameExit.exitRequest != nil {
            pendingTab = nil
            renameExit.resumeExit()
        } else if let pendingTab {
            self.pendingTab = nil
            tab = pendingTab
        }
    }

    private func dismissRenamePrompt() {
        if navigationError != nil {
            navigationError = nil
        } else if pendingTab != nil || renameExit.exitRequest != nil {
            declinePendingRename()
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
        .environment(InstanceRenameExitCoordinator())
        .environment(installations)
        .environment(sessions)
        .environment(playtime)
        .environment(GameLaunchCoordinator(store: installations.store, sessions: sessions, playtime: playtime))
        .background { SakuraBackground() }
        .frame(width: 1280, height: 720)
}

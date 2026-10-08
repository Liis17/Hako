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
    @Environment(InstanceContentController.self) private var content
    @Environment(MinecraftSessionCoordinator.self) private var sessions
    @Environment(InstanceRenameExitCoordinator.self) private var renameExit
    @Environment(QuickLaunchCoordinator.self) private var quickLaunch
    @Query(sort: \GameInstance.createdAt) private var instances: [GameInstance]
    @State private var tab = LauncherTab.instances
    @State private var creatingInstance = false
    @State private var pendingTab: LauncherTab?
    @State private var navigationError: String?
    @State private var deleteError: String?

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
                        InstanceProfileView(instance: instance, account: account, onBack: { requestTab(.instances) }, onDelete: { delete(instance) })
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
            .alert("Не удалось удалить сборку", isPresented: Binding(get: { deleteError != nil }, set: { if !$0 { deleteError = nil } })) {
                Button("ОК", role: .cancel) { deleteError = nil }
            } message: {
                Text(deleteError ?? "")
            }
        }
        .animation(.smooth, value: tab)
        .task(id: quickLaunch.presentation?.id) {
            if let request = quickLaunch.presentation {
                requestTab(.instance(request.instanceID))
                completeQuickLaunchNavigation()
            }
        }
        .onChange(of: tab) { completeQuickLaunchNavigation() }
        .sheet(isPresented: $creatingInstance) {
            InstanceCreationView(onCreated: { requestTab(.instances) })
                .environment(installations)
        }
        .sheet(item: Binding(get: { content.confirmation }, set: { _ in })) { confirmation in
            VStack(alignment: .leading, spacing: 20) {
                Text(confirmation.title).font(.title2.bold())
                Text(confirmation.message).fixedSize(horizontal: false, vertical: true)
                if !confirmation.projects.isEmpty {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 10) {
                            ForEach(confirmation.projects) { project in
                                HStack(spacing: 10) {
                                    AsyncImage(url: project.iconURL) { image in image.resizable().scaledToFit() } placeholder: {
                                        Image(systemName: "puzzlepiece.extension.fill").foregroundStyle(Color.sakuraDeep)
                                    }
                                    .frame(width: 32, height: 32).background(.white.opacity(0.35), in: .rect(cornerRadius: 7)).clipShape(.rect(cornerRadius: 7))
                                    VStack(alignment: .leading, spacing: 2) {
                                        Text(project.title).lineLimit(1)
                                        if let note = project.note { Text(note).font(.caption).foregroundStyle(.secondary).lineLimit(1) }
                                    }
                                }.frame(height: 32)
                            }
                        }.frame(maxWidth: .infinity, alignment: .leading)
                    }.frame(height: min(CGFloat(confirmation.projects.count) * 42 - 10, 260))
                }
                HStack {
                    Spacer()
                    Button("Отмена") { content.resolveConfirmation(confirmation.id, choice: .cancel) }.buttonStyle(.glass).keyboardShortcut(.cancelAction)
                    if let alternative = confirmation.alternative {
                        Button(alternative) { content.resolveConfirmation(confirmation.id, choice: .alternative) }.buttonStyle(.glass)
                    }
                    if let action = confirmation.action {
                        Button(action, role: confirmation.destructive ? .destructive : nil) { content.resolveConfirmation(confirmation.id, choice: .primary) }
                            .buttonStyle(.glassProminent).tint(.sakuraDeep).keyboardShortcut(.defaultAction)
                    }
                }
            }.padding(28).frame(width: confirmation.alternative == nil ? 460 : 560).interactiveDismissDisabled()
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
        guard let pending = renameExit.pendingRename else { return String(appLocalized: "Переименование изменит имя папки с файлами сборки в ~/.hako.") }
        return String(appLocalized: "Папка с файлами сборки будет переименована в ~/.hako при сохранении имени «\(pending.name)».")
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

    private func completeQuickLaunchNavigation() {
        guard let request = quickLaunch.presentation, tab == .instance(request.instanceID) else { return }
        quickLaunch.completePresentation(request.id)
    }

    private func confirmPendingRename() {
        guard let pending = renameExit.pendingRename,
              let instance = instances.first(where: { $0.id == pending.instanceID }) else {
            declinePendingRename()
            return
        }
        do {
            guard !installations.contentBusy.contains(instance.id) else {
                throw InstanceFileError.message(String(appLocalized: "Дождитесь завершения операций с файлами перед переименованием."))
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
            if let request = quickLaunch.presentation { quickLaunch.completePresentation(request.id, error: error) }
            else { navigationError = error.localizedDescription }
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

    /// Профиль закрывается до удаления, чтобы уходящая страница не читала удалённую модель.
    private func delete(_ instance: GameInstance) {
        if renameExit.pendingRename?.instanceID == instance.id { renameExit.pendingRename = nil }
        withAnimation(.smooth, completionCriteria: .removed) { tab = .instances } completion: {
            do { try installations.store.delete(instance) }
            catch { deleteError = error.localizedDescription }
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
    let games = GameLaunchCoordinator(store: installations.store, sessions: sessions, playtime: playtime)

    return LauncherView(account: account)
        .modelContainer(container)
        .environment(InstanceRenameExitCoordinator())
        .environment(installations)
        .environment(InstanceContentController(installations: installations))
        .environment(sessions)
        .environment(playtime)
        .environment(games)
        .environment(QuickLaunchCoordinator(store: installations.store, games: games))
        .background { SakuraBackground() }
        .frame(width: 1280, height: 720)
}

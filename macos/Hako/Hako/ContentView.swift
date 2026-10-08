//
//  ContentView.swift
//  Hako
//
//  Created by Li_is on 04/10/2026.
//

import SwiftData
import SwiftUI
import AppKit

/// Корень окна: приветствие, вход или лаунчер с аккаунтом/гостевым режимом.
struct ContentView: View {
    @Query private var accounts: [Account]
    @State private var isSigningIn = false
    @State private var isGuestLauncher = false
    @AppStorage(AppLanguage.storageKey) private var language = AppLanguage.current
    @Environment(MinecraftSessionCoordinator.self) private var sessions
    @Environment(QuickLaunchCoordinator.self) private var quickLaunch
    @Environment(InstanceRenameExitCoordinator.self) private var renameExit
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        ZStack {
            SakuraBackground()

            if isSigningIn {
                LoginView(onBack: { isSigningIn = false })
                    .transition(.blurReplace)
            } else if accounts.first != nil || isGuestLauncher {
                LauncherView(account: accounts.first, onSignIn: { isSigningIn = true })
                    .transition(.blurReplace)
            } else {
                WelcomeView(onStart: { isSigningIn = true }, onContinueWithoutAccount: { isGuestLauncher = true })
                    .transition(.blurReplace)
            }
        }
        .animation(.smooth, value: isSigningIn)
        .animation(.smooth, value: accounts.isEmpty)
        .animation(.smooth, value: isGuestLauncher)
        // После входа и после выхода следующий экран без аккаунта — приветствие.
        .onChange(of: accounts.isEmpty) { isSigningIn = false; if accounts.isEmpty { sessions.signOut(); isGuestLauncher = false } }
        .task(id: accounts.first?.xuid) { if let account = accounts.first { await sessions.monitor(account) } }
        .onAppear {
            let renameExit = renameExit, openWindow = openWindow
            quickLaunch.registerWindowPresenter { [weak renameExit] in
                if let window = renameExit?.window { window.makeKeyAndOrderFront(nil) }
                else { openWindow(id: "main") }
                NSApp.activate()
            }
        }
        .task(id: quickLaunch.presentation?.id) {
            if quickLaunch.presentation != nil { isSigningIn = false; isGuestLauncher = true }
        }
        .onDisappear { quickLaunch.cancelPresentations() }
        .alert("Не удалось запустить сборку", isPresented: Binding(get: { quickLaunch.error != nil }, set: { if !$0 { quickLaunch.error = nil } })) {
            Button("ОК", role: .cancel) { quickLaunch.error = nil }
        } message: {
            Text(quickLaunch.error ?? "")
        }
        .frame(minWidth: 960, minHeight: 540)
        .environment(\.locale, language.locale)
        .preferredColorScheme(.light)
    }
}

#Preview {
    let container = try! ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
    let installations = InstallationCoordinator(context: container.mainContext)
    let sessions = MinecraftSessionCoordinator(context: container.mainContext)
    let playtime = try! PlaytimeCoordinator(context: container.mainContext)
    let games = GameLaunchCoordinator(store: installations.store, sessions: sessions, playtime: playtime)
    return ContentView()
        .modelContainer(container)
        .environment(installations)
        .environment(sessions)
        .environment(playtime)
        .environment(InstanceRenameExitCoordinator())
        .environment(games)
        .environment(QuickLaunchCoordinator(store: installations.store, games: games))
        .frame(width: 1280, height: 720)
}

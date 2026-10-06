//
//  ContentView.swift
//  Hako
//
//  Created by Li_is on 04/10/2026.
//

import SwiftData
import SwiftUI

/// Корень окна: приветствие, вход или лаунчер с аккаунтом/гостевым режимом.
struct ContentView: View {
    @Query private var accounts: [Account]
    @State private var isSigningIn = false
    @State private var isGuestLauncher = false
    @Environment(MinecraftSessionCoordinator.self) private var sessions

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
        .frame(minWidth: 960, minHeight: 540)
        .preferredColorScheme(.light)
    }
}

#Preview {
    let container = try! ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
    let installations = InstallationCoordinator(context: container.mainContext)
    let sessions = MinecraftSessionCoordinator(context: container.mainContext)
    let playtime = try! PlaytimeCoordinator(context: container.mainContext)
    return ContentView()
        .modelContainer(container)
        .environment(installations)
        .environment(sessions)
        .environment(playtime)
        .environment(InstanceRenameExitCoordinator())
        .environment(GameLaunchCoordinator(store: installations.store, sessions: sessions, playtime: playtime))
        .frame(width: 1280, height: 720)
}

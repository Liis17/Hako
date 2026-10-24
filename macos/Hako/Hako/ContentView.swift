//
//  ContentView.swift
//  Hako
//
//  Created by Li_is on 04/10/2026.
//

import SwiftData
import SwiftUI
import AppKit
import Foundation

/// Корень окна: приветствие, вход или лаунчер с аккаунтом/гостевым режимом.
struct ContentView: View {
    var startsInCharacterEditor = false
    @Query private var accounts: [Account]
    @State private var isSigningIn = false
    @State private var presentation = LauncherPresentationState()
    @AppStorage(AppLanguage.storageKey) private var storedLanguage = AppLanguage.current.rawValue
    @Environment(MinecraftSessionCoordinator.self) private var sessions
    @Environment(MinecraftCharacterEditorCoordinator.self) private var characterEditor
    @Environment(QuickLaunchCoordinator.self) private var quickLaunch
    @Environment(BackupRestoreCoordinator.self) private var backupRestore
    @Environment(InstanceRenameExitCoordinator.self) private var renameExit
    @Environment(\.openWindow) private var openWindow

    private var language: AppLanguage {
        #if DEBUG
        if ProcessInfo.processInfo.arguments.contains("--character-editor-preview") { return AppLanguage.current }
        #endif
        return AppLanguage(rawValue: storedLanguage) ?? AppLanguage.current
    }

    var body: some View {
        ZStack {
            SakuraBackground()

            if isSigningIn {
                LoginView(onBack: { isSigningIn = false })
                    .transition(.blurReplace)
            } else if accounts.first != nil || presentation.isGuestLauncher {
                LauncherView(account: accounts.first, onSignIn: { isSigningIn = true }, startsInCharacterEditor: startsInCharacterEditor)
                    .transition(.blurReplace)
            } else {
                WelcomeView(onStart: { isSigningIn = true }, onContinueWithoutAccount: { presentation.continueAsGuest() })
                    .transition(.blurReplace)
            }
        }
        .animation(.smooth, value: isSigningIn)
        .animation(.smooth, value: accounts.isEmpty)
        .animation(.smooth, value: presentation.isGuestLauncher)
        // После выхода без активного восстановления экран без аккаунта — приветствие.
        .onChange(of: accounts.isEmpty) { _, isEmpty in
            isSigningIn = false
            if isEmpty { sessions.signOut() }
            presentation.accountsChanged(hasAccount: !isEmpty, restoreVisible: backupRestore.shouldPresentRestoreLauncher)
        }
        .onChange(of: accounts.first?.persistentModelID) { characterEditor.invalidateForAccountChange() }
        .task(id: accounts.first?.persistentModelID) { if let account = accounts.first { await sessions.monitor(account) } }
        .onAppear {
            if backupRestore.shouldPresentRestoreLauncher {
                isSigningIn = false
                presentation.restoreRequested()
            }
            let renameExit = renameExit, openWindow = openWindow
            quickLaunch.registerWindowPresenter { [weak renameExit] in
                if let window = renameExit?.window { window.makeKeyAndOrderFront(nil) }
                else { openWindow(id: "main") }
                NSApp.activate()
            }
            backupRestore.registerWindowPresenter { [weak renameExit] in
                if let window = renameExit?.window { window.makeKeyAndOrderFront(nil) }
                else { openWindow(id: "main") }
                NSApp.activate()
            }
        }
        .task(id: quickLaunch.presentation?.id) {
            if quickLaunch.presentation != nil { isSigningIn = false; presentation.restoreRequested() }
        }
        .task(id: backupRestore.presentationRequestID) {
            guard let requestID = backupRestore.presentationRequestID else { return }
            isSigningIn = false
            presentation.restoreRequested()
            backupRestore.consumePresentationRequest(requestID)
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
        .tint(.sakuraDeep)
    }
}

#Preview {
    let container = try! ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
    let installations = InstallationCoordinator(context: container.mainContext)
    let content = InstanceContentController(installations: installations)
    let backupRestore = BackupRestoreCoordinator(installations: installations, content: content)
    let sessions = MinecraftSessionCoordinator(context: container.mainContext)
    let playtime = try! PlaytimeCoordinator(context: container.mainContext)
    let games = GameLaunchCoordinator(store: installations.store, sessions: sessions, playtime: playtime)
    return ContentView()
        .modelContainer(container)
        .environment(installations)
        .environment(content)
        .environment(backupRestore)
        .environment(sessions)
        .environment(playtime)
        .environment(InstanceRenameExitCoordinator())
        .environment(games)
        .environment(QuickLaunchCoordinator(store: installations.store, games: games))
        .frame(width: 1280, height: 720)
}

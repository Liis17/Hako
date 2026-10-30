//
//  HakoApp.swift
//  Hako
//
//  Created by Li_is on 04/10/2026.
//

import SwiftUI
import SwiftData

@main
struct HakoApp: App {
    @NSApplicationDelegateAdaptor(HakoApplicationDelegate.self) private var appDelegate
    private let services: HakoServices

    init() {
        let services = HakoServices.forCurrentLaunch()
        self.services = services
        appDelegate.renameExit = services.renameExit
        appDelegate.services = services
    }

    var body: some Scene {
        Window("Hako", id: "main") {
            ContentView(
                startsInCharacterEditor: services.startsInCharacterEditor,
                startsInProfilePreview: services.startsInProfilePreview
            )
                .defaultAppStorage(services.appStorage)
                .environment(services.installations)
                .environment(services.content)
                .environment(services.backupRestore)
                .environment(services.sessions)
                .environment(services.hakoAccount)
                .environment(services.appearances)
                .environment(services.skinLoader)
                .environment(services.characterEditor)
                .environment(services.games)
                .environment(services.playtime)
                .environment(services.renameExit)
                .environment(services.quickLaunch)
                .environment(services.updates)
                .background(InstanceRenameWindowCloseGuard(coordinator: services.renameExit))
                .task { services.start() }
        }
        .modelContainer(services.modelContainer)
        .defaultSize(width: 1280, height: services.startsInProfilePreview ? 900 : 720)
        .windowResizability(.contentMinSize)
        .windowStyle(.hiddenTitleBar)
        .windowBackgroundDragBehavior(.enabled)
        .restorationBehavior(.disabled)
    }
}

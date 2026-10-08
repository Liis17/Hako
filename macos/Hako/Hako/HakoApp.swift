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
    private let services = HakoServices.shared

    var body: some Scene {
        Window("Hako", id: "main") {
            ContentView()
                .environment(services.installations)
                .environment(services.content)
                .environment(services.sessions)
                .environment(services.games)
                .environment(services.playtime)
                .environment(services.renameExit)
                .environment(services.quickLaunch)
                .background(InstanceRenameWindowCloseGuard(coordinator: services.renameExit))
                .onAppear { appDelegate.renameExit = services.renameExit }
                .task { services.start() }
        }
        .modelContainer(services.modelContainer)
        .defaultSize(width: 1280, height: 720)
        .windowResizability(.contentMinSize)
        .windowStyle(.hiddenTitleBar)
        .windowBackgroundDragBehavior(.enabled)
        .restorationBehavior(.disabled)
    }
}

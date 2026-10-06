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
    let sharedModelContainer: ModelContainer
    @State private var installations: InstallationCoordinator
    @State private var sessions: MinecraftSessionCoordinator
    @State private var games: GameLaunchCoordinator
    @State private var playtime: PlaytimeCoordinator
    @State private var renameExit = InstanceRenameExitCoordinator()

    init() {
        let schema = HakoSchema.schema
        do {
            let modelConfiguration = ModelConfiguration(schema: schema, url: try AppDataLocation.storeURL())
            let container = try ModelContainer(for: schema, configurations: [modelConfiguration])
            try LaunchSettingsMigration.run(context: container.mainContext)
            sharedModelContainer = container
            let installations = InstallationCoordinator(context: container.mainContext)
            let sessions = MinecraftSessionCoordinator(context: container.mainContext)
            let playtime = try PlaytimeCoordinator(context: container.mainContext)
            _installations = State(initialValue: installations)
            _sessions = State(initialValue: sessions)
            _games = State(initialValue: GameLaunchCoordinator(store: installations.store, sessions: sessions, playtime: playtime))
            _playtime = State(initialValue: playtime)
        } catch {
            fatalError("Could not create ModelContainer: \(error)")
        }
    }

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environment(installations)
                .environment(sessions)
                .environment(games)
                .environment(playtime)
                .environment(renameExit)
                .background(InstanceRenameWindowCloseGuard(coordinator: renameExit))
                .onAppear { appDelegate.renameExit = renameExit }
                .task { games.start(); installations.start() }
        }
        .modelContainer(sharedModelContainer)
        .defaultSize(width: 1280, height: 720)
        .windowResizability(.contentMinSize)
        .windowStyle(.hiddenTitleBar)
        .windowBackgroundDragBehavior(.enabled)
        .restorationBehavior(.disabled)
    }
}

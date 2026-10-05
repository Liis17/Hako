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
    let sharedModelContainer: ModelContainer
    @State private var installations: InstallationCoordinator
    @State private var sessions: MinecraftSessionCoordinator
    @State private var games: GameLaunchCoordinator

    init() {
        let schema = Schema([
            Account.self,
            GameInstance.self,
        ])
        do {
            let modelConfiguration = ModelConfiguration(schema: schema, url: try AppDataLocation.storeURL())
            let container = try ModelContainer(for: schema, configurations: [modelConfiguration])
            try LaunchSettingsMigration.run(context: container.mainContext)
            sharedModelContainer = container
            let installations = InstallationCoordinator(context: container.mainContext)
            let sessions = MinecraftSessionCoordinator(context: container.mainContext)
            _installations = State(initialValue: installations)
            _sessions = State(initialValue: sessions)
            _games = State(initialValue: GameLaunchCoordinator(store: installations.store, sessions: sessions))
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

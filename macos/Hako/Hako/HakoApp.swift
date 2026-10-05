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
            _installations = State(initialValue: InstallationCoordinator(context: container.mainContext))
        } catch {
            fatalError("Could not create ModelContainer: \(error)")
        }
    }

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environment(installations)
                .task { installations.start() }
        }
        .modelContainer(sharedModelContainer)
        .defaultSize(width: 1280, height: 720)
        .windowResizability(.contentMinSize)
        .windowStyle(.hiddenTitleBar)
        .windowBackgroundDragBehavior(.enabled)
        .restorationBehavior(.disabled)
    }
}

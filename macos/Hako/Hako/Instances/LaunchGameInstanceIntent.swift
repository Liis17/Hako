import AppIntents
import Foundation

struct LaunchGameInstanceIntent: OpenIntent {
    static let title: LocalizedStringResource = "Запустить сборку"
    static let description = IntentDescription("Запустить установленную сборку Minecraft в Hako.")
    static var supportedModes: IntentModes { .foreground(.immediate) }
    static var allowedExecutionTargets: IntentExecutionTargets { .main }

    @Parameter(title: "Сборка", requestValueDialog: "Какую сборку запустить?")
    var target: GameInstanceEntity

    private var launcher: QuickLaunchCoordinator?

    init() {}
    init(target: GameInstanceEntity, launcher: QuickLaunchCoordinator? = nil) {
        self.target = target; self.launcher = launcher
    }

    static var parameterSummary: some ParameterSummary { Summary("Запустить \(\.$target)") }

    @MainActor func perform() async throws -> some IntentResult & ProvidesDialog {
        let outcome = try await (launcher ?? HakoServices.shared.quickLaunch).launch(instanceID: target.id)
        if outcome == .alreadyRunning { return .result(dialog: "Эта сборка уже запущена.") }
        return .result(dialog: "Сборка запущена.")
    }
}

nonisolated struct HakoShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
        AppShortcut(intent: LaunchGameInstanceIntent(), phrases: [
            "Запусти \(\.$target) в \(.applicationName)",
            "Запусти сборку в \(.applicationName)"
        ], shortTitle: "Запустить сборку", systemImageName: "play.fill")
    }
}

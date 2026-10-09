import AppKit
import Foundation
import Observation

nonisolated enum AppUpdateActivity: Equatable, Sendable {
    case idle, checking, installing
    case downloading(Double)
}

/// Проверка релиза `nightly` при запуске и раз в 6 часов, загрузка и установка новой сборки.
@MainActor @Observable final class AppUpdateCoordinator {
    static let automaticCheckKey = "checkUpdatesAutomatically"
    private static let interval: Duration = .seconds(6 * 60 * 60)

    /// `nil` у локальных сборок: им не с чем сравнить релиз.
    let currentCommit: String?
    private let store: InstanceStore
    private let client: AppReleaseClient
    private(set) var activity = AppUpdateActivity.idle
    /// Найденная новая сборка; остаётся после ошибки установки, чтобы повторить.
    private(set) var release: AppRelease?
    private(set) var lastChecked: Date?
    var error: String?
    @ObservationIgnored private var started = false

    var isLocalBuild: Bool { currentCommit == nil }

    init(store: InstanceStore, client: AppReleaseClient = .init(), currentCommit: String? = AppUpdateInstaller.commit(of: .main)) {
        self.store = store; self.client = client; self.currentCommit = currentCommit
    }

    func start() {
        guard !started, !isLocalBuild else { return }
        started = true
        Task {
            while !Task.isCancelled {
                if UserDefaults.standard.object(forKey: Self.automaticCheckKey) as? Bool ?? true { await check(manual: false) }
                try? await Task.sleep(for: Self.interval)
            }
        }
    }

    /// Автоматическая проверка не показывает ошибки сети: следующая попытка будет через 6 часов.
    func check(manual: Bool = true) async {
        guard !isLocalBuild, activity == .idle else { return }
        activity = .checking
        defer { activity = .idle }
        do {
            let latest = try await client.latest()
            release = latest.flatMap { $0.isUpdate(for: currentCommit) ? $0 : nil }
            lastChecked = .now; error = nil
            if latest == nil, manual { error = String(appLocalized: "Новая версия Hako сейчас публикуется. Повторите проверку через несколько минут.") }
        } catch {
            if manual { self.error = error.localizedDescription }
        }
    }

    func install() async {
        guard let release, let asset = release.dmg, activity == .idle else { return }
        let app = Bundle.main.bundleURL
        error = nil
        activity = .downloading(0)
        do {
            try ensureIdleFiles()
            try AppUpdateInstaller.preflight(app)
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("HakoUpdate", isDirectory: true)
            defer { try? FileManager.default.removeItem(at: folder) }
            let dmg = try await client.download(asset, into: folder) { fraction in
                Task { @MainActor in if case .downloading = self.activity { self.activity = .downloading(fraction) } }
            }
            try ensureIdleFiles()
            activity = .installing
            try await AppUpdateInstaller.install(dmg: dmg, release: release, replacing: app)
            try AppUpdateInstaller.relaunch(app)
            NSApp.terminate(nil)
        } catch {
            activity = .idle
            self.error = error.localizedDescription
        }
    }

    /// Перезапуск во время копирования или удаления оставил бы файлы сборки недописанными.
    private func ensureIdleFiles() throws {
        guard store.contentBusy.isEmpty else { throw MojangError.invalid(String(appLocalized: "Дождитесь завершения операций с файлами сборок.")) }
    }
}

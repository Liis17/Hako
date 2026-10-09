import Foundation

/// Замена запущенного Hako.app сборкой из DMG релиза и перезапуск.
nonisolated enum AppUpdateInstaller {
    static let commitKey = "HakoCommit"

    /// Коммит, из которого CI собрал приложение; у локальных сборок его нет.
    static func commit(of bundle: Bundle) -> String? {
        (bundle.object(forInfoDictionaryKey: commitKey) as? String).flatMap { $0.isEmpty ? nil : $0 }
    }

    /// Проверяется до загрузки: без записи в папку приложения замену не выполнить.
    static func preflight(_ app: URL) throws {
        guard !app.path.contains("/AppTranslocation/"), FileManager.default.isWritableFile(atPath: app.deletingLastPathComponent().path) else {
            throw MojangError.invalid(String(appLocalized: "Переместите Hako в папку «Программы» и откройте его оттуда, чтобы обновлять автоматически."))
        }
    }

    /// Извлекает Hako.app из образа на том же томе, что и `app`, проверяет копию и атомарно заменяет `app`.
    static func install(dmg: URL, release: AppRelease, replacing app: URL) async throws {
        let manager = FileManager.default
        let staging = try manager.url(for: .itemReplacementDirectory, in: .userDomainMask, appropriateFor: app, create: true)
        defer { try? manager.removeItem(at: staging) }
        let mount = staging.appendingPathComponent("mount")
        let copy = staging.appendingPathComponent(app.lastPathComponent)
        try manager.createDirectory(at: mount, withIntermediateDirectories: false)
        try await run("/usr/bin/hdiutil", ["attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount.path, dmg.path])
        let copied = Result { try manager.copyItem(at: mount.appendingPathComponent("Hako.app"), to: copy) }
        try? await run("/usr/bin/hdiutil", ["detach", "-force", mount.path])
        try copied.get()

        let info = NSDictionary(contentsOf: copy.appendingPathComponent("Contents/Info.plist"))
        guard info?["CFBundleIdentifier"] as? String == Bundle.main.bundleIdentifier, info?[commitKey] as? String == release.commit else {
            throw MojangError.invalid(String(appLocalized: "Загруженное обновление не подходит для этой копии Hako."))
        }
        try await run("/usr/bin/codesign", ["--verify", "--deep", "--strict", copy.path])
        _ = try manager.replaceItemAt(app, withItemAt: copy)
    }

    /// Открывает `app` после выхода текущего процесса; вызывающий завершает приложение сам.
    static func relaunch(_ app: URL) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        process.arguments = ["-c", "while kill -0 \"$0\" 2>/dev/null; do sleep 0.2; done; /usr/bin/open \"$1\"", String(ProcessInfo.processInfo.processIdentifier), app.path]
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
    }

    /// Ожидание через terminationHandler: waitUntilExit на потоках Swift Concurrency может не вернуться.
    private static func run(_ tool: String, _ arguments: [String]) async throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: tool)
        process.arguments = arguments
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        let status: Int32 = try await withCheckedThrowingContinuation { continuation in
            process.terminationHandler = { continuation.resume(returning: $0.terminationStatus) }
            do { try process.run() } catch { continuation.resume(throwing: error) }
        }
        let name = URL(fileURLWithPath: tool).lastPathComponent
        guard status == 0 else { throw MojangError.invalid(String(appLocalized: "Не удалось установить обновление (\(name), код \(String(status))).")) }
    }
}

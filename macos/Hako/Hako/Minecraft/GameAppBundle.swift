import Foundation

/// Обёртка `.app` для Java: macOS читает категорию «игры» из главного bundle процесса и включает игровой режим.
nonisolated enum GameAppBundle {
    /// Пересоздаёт `.hako-game/Minecraft.app` с копией `java` и возвращает путь к исполняемому файлу внутри него.
    static func prepare(java: URL, in root: URL) async throws -> URL {
        let manager = FileManager.default
        let bundle = try InstanceStorage.containedURL(".hako-game/Minecraft.app", in: root)
        let contents = bundle.appendingPathComponent("Contents")
        let executable = contents.appendingPathComponent("MacOS/java")
        if manager.fileExists(atPath: bundle.path) { try manager.removeItem(at: bundle) }
        try manager.createDirectory(at: executable.deletingLastPathComponent(), withIntermediateDirectories: true)
        let source = java.resolvingSymlinksInPath()
        try manager.copyItem(at: source, to: executable)
        // bin/java ищет libjli.dylib через rpath @loader_path/../lib, а JAVA_HOME — по realpath библиотеки.
        let library = source.deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("lib")
        try manager.createSymbolicLink(at: contents.appendingPathComponent("lib"), withDestinationURL: library)
        let info: [String: Any] = [
            "CFBundleExecutable": "java",
            "CFBundleIdentifier": "dev.hako.minecraft",
            "CFBundleName": "Minecraft",
            "CFBundlePackageType": "APPL",
            "LSApplicationCategoryType": "public.app-category.games",
            "GCSupportsGameMode": true,
            "LSSupportsGameMode": true,
        ]
        try PropertyListSerialization.data(fromPropertyList: info, format: .xml, options: 0)
            .write(to: contents.appendingPathComponent("Info.plist"))
        // Исходная подпись java не покрывает Info.plist, и AMFI убивает процесс (SIGKILL).
        // Ad-hoc подпись без hardened runtime связывает plist и не требует library validation для libjli.
        try await sign(bundle)
        return executable
    }

    /// Ожидание через terminationHandler: waitUntilExit на потоках Swift Concurrency может не вернуться.
    private static func sign(_ bundle: URL) async throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/codesign")
        process.arguments = ["--force", "--sign", "-", bundle.path]
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        let status: Int32 = try await withCheckedThrowingContinuation { continuation in
            process.terminationHandler = { continuation.resume(returning: $0.terminationStatus) }
            do { try process.run() } catch { continuation.resume(throwing: error) }
        }
        guard status == 0 else { throw InstanceFileError.message("Не удалось подготовить запуск Java (codesign, код \(status)).") }
    }
}

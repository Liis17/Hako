import Foundation
import Darwin

nonisolated enum JavaLaunchValidation {
    @concurrent static func validate(_ executable: URL, minimumMajor: Int, platform: MinecraftPlatform = .current, timeout: Duration = .seconds(10)) async throws {
        guard FileManager.default.isExecutableFile(atPath: executable.path) else { throw InstanceFileError.message(String(appLocalized: "По указанному пути нет исполняемого файла Java.")) }
        let data = try probe(executable, timeout: timeout)
        try Task.checkCancellation()
        let text = String(decoding: data, as: UTF8.self)
        let properties = text.split(separator: "\n").reduce(into: [String: String]()) { result, line in
            let pair = line.split(separator: "=", maxSplits: 1).map { $0.trimmingCharacters(in: .whitespaces) }
            if pair.count == 2 { result[pair[0]] = pair[1] }
        }
        let version = properties["java.version"] ?? ""
        let pieces = version.split(separator: ".")
        let major = pieces.first == "1" && pieces.count > 1 ? Int(pieces[1]) : Int(version.prefix(while: \.isNumber))
        guard let major, major >= minimumMajor else { throw InstanceFileError.message(String(appLocalized: "Для этой версии Minecraft требуется Java \(minimumMajor) или новее. Выбранная Java не подходит.")) }
        let architecture = properties["os.arch"] ?? ""
        let supported = platform == .appleSilicon ? ["aarch64", "arm64"] : ["amd64", "x86_64"]
        guard supported.contains(architecture) else { throw InstanceFileError.message(String(appLocalized: "Архитектура выбранной Java не соответствует процессору Mac и библиотекам сборки.")) }
    }

    // Process и его run loop остаются на одном фоновом потоке до завершения проверки.
    private static func probe(_ executable: URL, timeout: Duration) throws -> Data {
        let output = FileManager.default.temporaryDirectory.appendingPathComponent("hako-java-\(UUID().uuidString).log")
        guard FileManager.default.createFile(atPath: output.path, contents: nil, attributes: [.posixPermissions: 0o600]) else { throw InstanceFileError.message(String(appLocalized: "Не удалось подготовить проверку Java.")) }
        defer { try? FileManager.default.removeItem(at: output) }
        let handle = try FileHandle(forUpdating: output)
        defer { try? handle.close() }
        let process = Process()
        process.executableURL = executable; process.arguments = ["-XshowSettings:properties", "-version"]
        process.standardOutput = handle; process.standardError = handle
        process.environment = environment()
        try process.run()
        defer {
            if process.isRunning { kill(process.processIdentifier, SIGKILL); process.waitUntilExit() }
        }
        let deadline = ContinuousClock.now + timeout
        while process.isRunning {
            try Task.checkCancellation()
            guard ContinuousClock.now < deadline else { throw InstanceFileError.message(String(appLocalized: "Проверка Java превысила время ожидания. Выберите другой исполняемый файл java.")) }
            Thread.sleep(forTimeInterval: 0.025)
        }
        process.waitUntilExit()
        try Task.checkCancellation()
        try handle.seek(toOffset: 0)
        guard process.terminationStatus == 0 else { throw InstanceFileError.message(String(appLocalized: "Не удалось проверить выбранную Java. Выберите исполняемый файл java из папки bin.")) }
        return try handle.read(upToCount: 64 * 1024) ?? Data()
    }

    static func environment() -> [String: String] {
        var environment = ProcessInfo.processInfo.environment
        for key in ["JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS"] { environment[key] = nil }
        return environment
    }
}

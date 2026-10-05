import Foundation

nonisolated enum JavaLaunchValidation {
    @concurrent static func validate(_ executable: URL, minimumMajor: Int, platform: MinecraftPlatform = .current) async throws {
        guard FileManager.default.isExecutableFile(atPath: executable.path) else { throw InstanceFileError.message("По указанному пути нет исполняемого файла Java.") }
        let process = Process(), pipe = Pipe()
        process.executableURL = executable; process.arguments = ["-XshowSettings:properties", "-version"]
        process.standardOutput = pipe; process.standardError = pipe
        process.environment = environment()
        try process.run()
        let timeout = DispatchWorkItem { if process.isRunning { process.terminate() } }
        DispatchQueue.global().asyncAfter(deadline: .now() + 10, execute: timeout)
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit(); timeout.cancel()
        try Task.checkCancellation()
        let text = String(decoding: data, as: UTF8.self)
        guard process.terminationStatus == 0 else { throw InstanceFileError.message("Не удалось проверить выбранную Java. Выберите исполняемый файл java из папки bin.") }
        let properties = text.split(separator: "\n").reduce(into: [String: String]()) { result, line in
            let pair = line.split(separator: "=", maxSplits: 1).map { $0.trimmingCharacters(in: .whitespaces) }
            if pair.count == 2 { result[pair[0]] = pair[1] }
        }
        let version = properties["java.version"] ?? ""
        let pieces = version.split(separator: ".")
        let major = pieces.first == "1" && pieces.count > 1 ? Int(pieces[1]) : Int(version.prefix(while: \.isNumber))
        guard let major, major >= minimumMajor else { throw InstanceFileError.message("Для этой версии Minecraft требуется Java \(minimumMajor) или новее. Выбранная Java не подходит.") }
        let architecture = properties["os.arch"] ?? ""
        let supported = platform == .appleSilicon ? ["aarch64", "arm64"] : ["amd64", "x86_64"]
        guard supported.contains(architecture) else { throw InstanceFileError.message("Архитектура выбранной Java не соответствует процессору Mac и библиотекам сборки.") }
    }

    static func environment() -> [String: String] {
        var environment = ProcessInfo.processInfo.environment
        for key in ["JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS"] { environment[key] = nil }
        return environment
    }
}

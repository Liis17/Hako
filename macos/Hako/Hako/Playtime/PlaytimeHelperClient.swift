import Foundation

actor PlaytimeHelperClient {
    nonisolated static var bundledExecutable: URL { Bundle.main.bundleURL.appendingPathComponent("Contents/MacOS/HakoPlaytimeHelper") }
    private let executable: URL
    private var helpers: [UUID: Process] = [:]

    init(executable: URL = PlaytimeHelperClient.bundledExecutable) { self.executable = executable }

    func attach(to url: URL) async throws {
        let journal = try PlaytimeJournal.load(from: url)
        if journal.isFinished || journal.helper?.isRunning == true { return }
        guard FileManager.default.isExecutableFile(atPath: executable.path) else {
            throw InstanceFileError.message("Помощник учёта игрового времени отсутствует. Переустановите Hako.")
        }
        let process = Process()
        process.executableURL = executable; process.arguments = [url.path]
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = FileHandle.nullDevice; process.standardError = FileHandle.nullDevice
        process.terminationHandler = { [weak self] _ in Task { await self?.finished(journal.sessionID) } }
        try process.run()
        helpers[journal.sessionID] = process
        do {
            for _ in 0..<100 {
                let current = try PlaytimeJournal.load(from: url)
                if current.isFinished || current.helper?.isRunning == true { return }
                if !process.isRunning { break }
                try await Task.sleep(for: .milliseconds(20))
            }
            throw InstanceFileError.message("Не удалось запустить помощник учёта игрового времени.")
        } catch {
            if process.isRunning { process.terminate() }
            throw error
        }
    }

    private func finished(_ id: UUID) { helpers[id] = nil }
}

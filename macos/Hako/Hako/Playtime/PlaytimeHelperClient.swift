import Foundation

actor PlaytimeHelperClient {
    nonisolated static var bundledExecutable: URL { Bundle.main.bundleURL.appendingPathComponent("Contents/MacOS/HakoPlaytimeHelper") }
    private let executable: URL
    private var helpers: [UUID: Process] = [:]

    init(executable: URL = PlaytimeHelperClient.bundledExecutable) { self.executable = executable }

    func attach(to journal: PlaytimeJournal, in directory: URL) async throws {
        let url = journal.url(in: directory)
        let process = Process()
        do {
            let current = try PlaytimeJournal.load(from: url)
            if current.isFinished || current.helper?.isRunning == true { return }
            guard FileManager.default.isExecutableFile(atPath: executable.path) else {
                throw InstanceFileError.message("Помощник учёта игрового времени отсутствует. Переустановите Hako.")
            }
            process.executableURL = executable; process.arguments = [url.path]
            process.standardInput = FileHandle.nullDevice
            process.standardOutput = FileHandle.nullDevice; process.standardError = FileHandle.nullDevice
            process.terminationHandler = { [weak self] _ in Task { await self?.finished(journal.sessionID) } }
            try process.run()
            helpers[journal.sessionID] = process
            for _ in 0..<100 {
                let current = try PlaytimeJournal.load(from: url)
                if current.isFinished || current.helper?.isRunning == true { return }
                if !process.isRunning { break }
                try await Task.sleep(for: .milliseconds(20))
            }
            throw InstanceFileError.message("Не удалось запустить помощник учёта игрового времени.")
        } catch {
            if process.isRunning { process.terminate() }
            // Завершение игры могло уже сохранить время и удалить журнал во время подключения.
            if !journal.process.isRunning, !FileManager.default.fileExists(atPath: url.path) { return }
            throw error
        }
    }

    private func finished(_ id: UUID) { helpers[id] = nil }
}

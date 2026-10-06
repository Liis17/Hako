import Foundation
import Darwin

/// Источник завершения и таймер работают на одной очереди; только она изменяет журнал.
private final class PlaytimeWatcher: @unchecked Sendable {
    private let directory: URL
    private var journal: PlaytimeJournal
    private let queue = DispatchQueue(label: "Hako.playtime")
    private var lock: PlaytimeJournalLock?
    private var observer: DispatchSourceProcess?
    private var timer: DispatchSourceTimer?

    init(url: URL) throws {
        directory = url.deletingLastPathComponent()
        journal = try PlaytimeJournal.load(from: url)
    }

    func run() throws -> Bool {
        guard let lock = try PlaytimeJournalLock.acquire(sessionID: journal.sessionID, directory: directory) else { return false }
        self.lock = lock
        journal = try PlaytimeJournal.load(from: journal.url(in: directory))
        guard !journal.isFinished else { return false }
        journal.helper = PlaytimeProcessIdentity.read(pid: getpid())
        if !journal.process.isRunning {
            journal.isFinished = true
            try journal.save(in: directory)
            return false
        }
        journal.checkpoint()
        try journal.save(in: directory)
        let process = journal.process
        let observer = DispatchSource.makeProcessSource(identifier: process.pid, eventMask: .exit, queue: queue)
        observer.setEventHandler { [self] in
            if !journal.isFinished { journal.checkpoint(); journal.isFinished = true }
            saveCheckpoint()
        }
        self.observer = observer
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + PlaytimeJournal.checkpointInterval, repeating: PlaytimeJournal.checkpointInterval)
        timer.setEventHandler { [self] in
            if !journal.isFinished { journal.checkpoint() }
            saveCheckpoint()
        }
        self.timer = timer
        observer.resume(); timer.resume()
        // Игра могла завершиться между проверкой PID и подключением наблюдателя.
        if !process.isRunning {
            queue.async { [self] in
                if !journal.isFinished { journal.checkpoint(); journal.isFinished = true; saveCheckpoint() }
            }
        }
        return true
    }

    private func saveCheckpoint() {
        do {
            try journal.save(in: directory)
            if journal.isFinished { exit(0) }
        } catch {
            // Следующий тик повторит запись; время завершившейся игры уже зафиксировано в памяти.
        }
    }
}

do {
    guard CommandLine.arguments.count == 2 else { exit(1) }
    let watcher = try PlaytimeWatcher(url: URL(fileURLWithPath: CommandLine.arguments[1]))
    if try watcher.run() { dispatchMain() }
} catch { exit(1) }

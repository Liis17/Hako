import Foundation
import Darwin

nonisolated struct GameProcessRecord: Codable, Equatable, Sendable {
    let instanceID: UUID
    let pid: Int32
    let startSeconds: UInt64
    let startMicroseconds: UInt64
    var sessionID: UUID? = nil

    static func identity(pid: Int32, instanceID: UUID) -> Self? {
        guard let identity = PlaytimeProcessIdentity.read(pid: pid) else { return nil }
        return .init(instanceID: instanceID, pid: pid, startSeconds: identity.startSeconds, startMicroseconds: identity.startMicroseconds)
    }
    var isRunning: Bool { PlaytimeProcessIdentity(pid: pid, startSeconds: startSeconds, startMicroseconds: startMicroseconds).isRunning }
    static func url(in root: URL) throws -> URL { try InstanceStorage.containedURL("minecraft/.hako-running.json", in: root) }
    static func load(in root: URL) throws -> Self? {
        let url = try url(in: root)
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        let data = try Data(contentsOf: url)
        return try? JSONDecoder().decode(Self.self, from: data)
    }
}

nonisolated struct PlaytimeTrackingRequest: Sendable {
    let sessionID: UUID
    let directory: URL
}

actor GameProcessRunner {
    private var processes: [UUID: Process] = [:]
    private var observers: [UUID: DispatchSourceProcess] = [:]
    private var records: [UUID: GameProcessRecord] = [:]
    private let helper: PlaytimeHelperClient

    init(helperURL: URL = PlaytimeHelperClient.bundledExecutable) { helper = PlaytimeHelperClient(executable: helperURL) }

    func start(_ plan: MinecraftLaunchPlan, id: UUID, root: URL, tracking: PlaytimeTrackingRequest? = nil, onExit: @escaping @Sendable (Int32?) async -> Void) async throws -> GameProcessRecord {
        if let previous = try GameProcessRecord.load(in: root), previous.isRunning {
            throw InstanceFileError.message("Эта сборка уже запущена.")
        }
        let log = try InstanceStorage.containedURL("minecraft/logs/hako-launch.log", in: root)
        try FileManager.default.createDirectory(at: log.deletingLastPathComponent(), withIntermediateDirectories: true)
        if !FileManager.default.fileExists(atPath: log.path) { FileManager.default.createFile(atPath: log.path, contents: nil, attributes: [.posixPermissions: 0o600]) }
        let output = try FileHandle(forWritingTo: log)
        defer { try? output.close() }
        try output.truncate(atOffset: 0)
        let process = Process()
        process.executableURL = try await GameAppBundle.prepare(java: plan.executable, in: root); process.arguments = plan.arguments
        process.currentDirectoryURL = plan.workingDirectory
        process.environment = JavaLaunchValidation.environment()
        process.standardOutput = output; process.standardError = output
        let startedUptime = ProcessInfo.processInfo.systemUptime
        process.terminationHandler = { [weak self] process in
            let endedUptime = ProcessInfo.processInfo.systemUptime
            Task { await self?.finished(id, pid: process.processIdentifier, root: root, tracking: tracking, endedUptime: endedUptime); await onExit(process.terminationStatus) }
        }
        try process.run()
        guard var record = GameProcessRecord.identity(pid: process.processIdentifier, instanceID: id) else {
            // Короткий процесс уже завершился: никакой записи о запущенной игре не остаётся.
            process.waitUntilExit()
            if let tracking {
                var journal = PlaytimeJournal(sessionID: tracking.sessionID, process: .init(pid: process.processIdentifier, startSeconds: 0, startMicroseconds: 0), startedUptime: startedUptime)
                journal.checkpoint(); journal.isFinished = true
                try journal.save(in: tracking.directory)
            }
            return .init(instanceID: id, pid: process.processIdentifier, startSeconds: 0, startMicroseconds: 0, sessionID: tracking?.sessionID)
        }
        record.sessionID = tracking?.sessionID
        processes[id] = process
        records[id] = record
        do {
            try JSONEncoder().encode(record).write(to: GameProcessRecord.url(in: root), options: .atomic)
            if let tracking {
                let journal = PlaytimeJournal(sessionID: tracking.sessionID, process: .init(pid: record.pid, startSeconds: record.startSeconds, startMicroseconds: record.startMicroseconds), startedUptime: startedUptime)
                try journal.save(in: tracking.directory)
                try await helper.attach(to: journal, in: tracking.directory)
            }
        } catch {
            if process.isRunning { process.terminate() }
            try? await Task.sleep(for: .milliseconds(100))
            if record.isRunning { kill(record.pid, SIGKILL) }
            throw error
        }
        return record
    }

    func observe(_ record: GameProcessRecord, root: URL, tracking: PlaytimeTrackingRequest? = nil, onExit: @escaping @Sendable (Int32?) async -> Void) async throws {
        guard record.isRunning else { Task { await onExit(nil) }; return }
        let observer = DispatchSource.makeProcessSource(identifier: record.pid, eventMask: .exit, queue: .global())
        observer.setEventHandler { [weak self] in
            let endedUptime = ProcessInfo.processInfo.systemUptime
            Task { await self?.finished(record.instanceID, pid: record.pid, root: root, tracking: tracking, endedUptime: endedUptime); await onExit(nil) }
        }
        observers[record.instanceID] = observer
        records[record.instanceID] = record
        observer.resume()
        try JSONEncoder().encode(record).write(to: GameProcessRecord.url(in: root), options: .atomic)
        if let tracking {
            let journal = PlaytimeJournal(sessionID: tracking.sessionID, process: .init(pid: record.pid, startSeconds: record.startSeconds, startMicroseconds: record.startMicroseconds), startedUptime: ProcessInfo.processInfo.systemUptime)
            if !FileManager.default.fileExists(atPath: journal.url(in: tracking.directory).path) { try journal.save(in: tracking.directory) }
            try await helper.attach(to: journal, in: tracking.directory)
        }
        if !record.isRunning {
            finished(record.instanceID, pid: record.pid, root: root, tracking: tracking, endedUptime: ProcessInfo.processInfo.systemUptime)
            await onExit(nil)
        }
    }

    private func finished(_ id: UUID, pid: Int32, root: URL, tracking: PlaytimeTrackingRequest?, endedUptime: TimeInterval) {
        if let tracking { finishJournal(tracking, endedUptime: endedUptime) }
        guard let record = records[id], record.pid == pid else { return }
        processes[record.instanceID] = nil
        records[record.instanceID] = nil
        observers.removeValue(forKey: record.instanceID)?.cancel()
        if (try? GameProcessRecord.load(in: root)) == record { try? FileManager.default.removeItem(at: GameProcessRecord.url(in: root)) }
    }

    private func finishJournal(_ tracking: PlaytimeTrackingRequest, endedUptime: TimeInterval) {
        do {
            let url = tracking.directory.appendingPathComponent("\(tracking.sessionID.uuidString).json")
            guard FileManager.default.fileExists(atPath: url.path) else { return }
            guard let lock = try PlaytimeJournalLock.acquire(sessionID: tracking.sessionID, directory: tracking.directory) else { return }
            try withExtendedLifetime(lock) {
                guard FileManager.default.fileExists(atPath: url.path) else { return }
                var journal = try PlaytimeJournal.load(from: url)
                guard !journal.isFinished else { return }
                journal.checkpoint(atUptime: endedUptime); journal.isFinished = true
                try journal.save(in: tracking.directory)
            }
        } catch { /* Сохранённая контрольная точка остаётся для следующего чтения Hako. */ }
    }
}

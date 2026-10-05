import Foundation
import Darwin

nonisolated struct GameProcessRecord: Codable, Equatable, Sendable {
    let instanceID: UUID
    let pid: Int32
    let startSeconds: UInt64
    let startMicroseconds: UInt64

    static func identity(pid: Int32, instanceID: UUID) -> Self? {
        var info = proc_bsdinfo()
        let size = Int32(MemoryLayout<proc_bsdinfo>.size)
        guard proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, size) == size, info.pbi_status != SZOMB else { return nil }
        return .init(instanceID: instanceID, pid: pid, startSeconds: info.pbi_start_tvsec, startMicroseconds: info.pbi_start_tvusec)
    }
    var isRunning: Bool { Self.identity(pid: pid, instanceID: instanceID) == self }
    static func url(in root: URL) throws -> URL { try InstanceStorage.containedURL("minecraft/.hako-running.json", in: root) }
    static func load(in root: URL) throws -> Self? {
        let url = try url(in: root)
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        let data = try Data(contentsOf: url)
        return try? JSONDecoder().decode(Self.self, from: data)
    }
}

actor GameProcessRunner {
    private var processes: [UUID: Process] = [:]
    private var observers: [UUID: DispatchSourceProcess] = [:]
    private var records: [UUID: GameProcessRecord] = [:]

    func start(_ plan: MinecraftLaunchPlan, id: UUID, root: URL, onExit: @escaping @Sendable (Int32?) async -> Void) throws -> GameProcessRecord {
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
        process.executableURL = plan.executable; process.arguments = plan.arguments
        process.currentDirectoryURL = plan.workingDirectory
        process.environment = JavaLaunchValidation.environment()
        process.standardOutput = output; process.standardError = output
        process.terminationHandler = { [weak self] process in
            Task { await self?.finished(id, pid: process.processIdentifier, root: root); await onExit(process.terminationStatus) }
        }
        try process.run()
        guard let record = GameProcessRecord.identity(pid: process.processIdentifier, instanceID: id) else {
            // Короткий процесс уже завершился: никакой записи о запущенной игре не остаётся.
            process.waitUntilExit()
            return .init(instanceID: id, pid: process.processIdentifier, startSeconds: 0, startMicroseconds: 0)
        }
        do { try JSONEncoder().encode(record).write(to: GameProcessRecord.url(in: root), options: .atomic) }
        catch { process.terminate(); throw error }
        processes[id] = process
        records[id] = record
        return record
    }

    func observe(_ record: GameProcessRecord, root: URL, onExit: @escaping @Sendable (Int32?) async -> Void) {
        guard record.isRunning else { Task { await onExit(nil) }; return }
        let observer = DispatchSource.makeProcessSource(identifier: record.pid, eventMask: .exit, queue: .global())
        observer.setEventHandler { [weak self] in
            Task { await self?.finished(record.instanceID, pid: record.pid, root: root); await onExit(nil) }
        }
        observers[record.instanceID] = observer
        records[record.instanceID] = record
        observer.resume()
    }

    private func finished(_ id: UUID, pid: Int32, root: URL) {
        guard let record = records[id], record.pid == pid else { return }
        processes[record.instanceID] = nil
        records[record.instanceID] = nil
        observers.removeValue(forKey: record.instanceID)?.cancel()
        if (try? GameProcessRecord.load(in: root)) == record { try? FileManager.default.removeItem(at: GameProcessRecord.url(in: root)) }
    }
}

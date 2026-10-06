import Foundation
import Darwin

nonisolated struct PlaytimeProcessIdentity: Codable, Equatable, Sendable {
    let pid: Int32
    let startSeconds: UInt64
    let startMicroseconds: UInt64

    static func read(pid: Int32) -> Self? {
        var info = proc_bsdinfo()
        let size = Int32(MemoryLayout<proc_bsdinfo>.size)
        guard proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, size) == size, info.pbi_status != SZOMB else { return nil }
        return .init(pid: pid, startSeconds: info.pbi_start_tvsec, startMicroseconds: info.pbi_start_tvusec)
    }

    var isRunning: Bool { Self.read(pid: pid) == self }
}

nonisolated struct PlaytimeJournal: Codable, Sendable {
    static let checkpointInterval: TimeInterval = 10
    let sessionID: UUID
    let process: PlaytimeProcessIdentity
    let startedUptime: TimeInterval
    var elapsedSeconds: TimeInterval = 0
    var isFinished = false
    var helper: PlaytimeProcessIdentity?

    static func directory(home: URL = FileManager.default.homeDirectoryForCurrentUser) -> URL {
        home.appendingPathComponent("Library/Application Support/Hako/Playtime/sessions", isDirectory: true)
    }

    func url(in directory: URL) -> URL { directory.appendingPathComponent("\(sessionID.uuidString).json") }
    static func load(from url: URL) throws -> Self { try JSONDecoder().decode(Self.self, from: Data(contentsOf: url)) }

    func save(in directory: URL) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        try JSONEncoder().encode(self).write(to: url(in: directory), options: .atomic)
    }

    mutating func checkpoint(atUptime uptime: TimeInterval = ProcessInfo.processInfo.systemUptime) {
        elapsedSeconds = max(elapsedSeconds, max(0, uptime - startedUptime))
    }
}

/// Один писатель на сессию; блокировка освобождается системой при завершении помощника.
nonisolated final class PlaytimeJournalLock {
    private let descriptor: Int32
    private init(descriptor: Int32) { self.descriptor = descriptor }
    deinit { flock(descriptor, LOCK_UN); close(descriptor) }

    static func acquire(sessionID: UUID, directory: URL) throws -> PlaytimeJournalLock? {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let path = directory.appendingPathComponent("\(sessionID.uuidString).lock").path
        let descriptor = open(path, O_CREAT | O_RDWR | O_CLOEXEC, 0o600)
        guard descriptor >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        if flock(descriptor, LOCK_EX | LOCK_NB) != 0 {
            let code = errno; close(descriptor)
            if code == EWOULDBLOCK { return nil }
            throw POSIXError(POSIXErrorCode(rawValue: code) ?? .EIO)
        }
        return .init(descriptor: descriptor)
    }
}

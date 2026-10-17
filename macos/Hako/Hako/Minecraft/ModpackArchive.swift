import Darwin
import Foundation

/// ZIP entries are streamed to regular files, never extracted with their stored paths or permissions.
nonisolated enum ModpackArchive {
    static let fileLimit: Int64 = 2_147_483_648
    static let totalLimit: Int64 = 21_474_836_480
    struct Contents: Sendable {
        let manifest: ModpackManifest
        /// Destination relative to minecraft/ → archive entry. Client overrides win.
        let overrides: [String: String]
    }

    @concurrent static func inspect(_ archive: URL) async throws -> Contents {
        let listing = try await output(["-Z1", archive.path], limit: 8_388_608)
        guard let text = String(data: listing, encoding: .utf8) else { throw ModpackError.archive }
        let entries = text.split(separator: "\n", omittingEmptySubsequences: true).map(String.init)
        guard entries.count <= 50_000, entries.filter({ $0 == "modrinth.index.json" }).count == 1 else { throw ModpackError.archive }
        var seen: Set<String> = []
        for entry in entries {
            let path = entry.hasSuffix("/") ? String(entry.dropLast()) : entry
            try ModpackPaths.validate(path)
            guard seen.insert(ModpackPaths.key(path)).inserted else { throw ModpackError.unsafePath }
        }
        let bytes = try await output(["-p", archive.path, "modrinth.index.json"], limit: 16_777_216)
        let manifest = try JSONDecoder().decode(ModpackManifest.self, from: bytes)
        try manifest.validate()
        var paths: [String: String] = [:]
        for file in manifest.files where file.installOnClient { try ModpackPaths.insert(file.path, into: &paths) }
        var overrides: [String: String] = [:]
        for layer in ["overrides/", "client-overrides/"] {
            for entry in entries where entry.hasPrefix(layer) && !entry.hasSuffix("/") {
                let path = String(entry.dropFirst(layer.count))
                try ModpackPaths.validate(path)
                try ModpackPaths.insert(path, into: &paths, replacing: true)
                overrides[path] = entry
            }
        }
        return Contents(manifest: manifest, overrides: overrides)
    }

    @concurrent static func extract(_ entry: String, in archive: URL, to destination: URL, limit: Int64 = fileLimit) async throws {
        try ModpackPaths.validate(entry)
        try await run(["-p", archive.path, entry], to: destination, limit: limit)
    }

    private static func output(_ arguments: [String], limit: Int64) async throws -> Data {
        let temporary = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: temporary) }
        try await run(arguments, to: temporary, limit: limit)
        return try Data(contentsOf: temporary)
    }

    private static func run(_ arguments: [String], to destination: URL, limit: Int64) async throws {
        try Task.checkCancellation()
        guard FileManager.default.createFile(atPath: destination.path, contents: nil, attributes: [.posixPermissions: 0o600]) else { throw ModpackError.archive }
        let handle = try FileHandle(forWritingTo: destination)
        defer { try? handle.close() }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/unzip")
        process.arguments = arguments
        process.standardOutput = handle
        process.standardError = FileHandle.nullDevice
        defer { if process.isRunning { kill(process.processIdentifier, SIGKILL); while process.isRunning { usleep(1_000) } } }
        try process.run()
        let deadline = Date().addingTimeInterval(120)
        while process.isRunning {
            let size = try destination.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
            guard Date() < deadline, Int64(size) <= limit else { throw ModpackError.archive }
            try await Task.sleep(for: .milliseconds(20))
        }
        try Task.checkCancellation()
        guard process.terminationStatus == 0,
              Int64(try destination.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0) <= limit else { throw ModpackError.archive }
    }
}

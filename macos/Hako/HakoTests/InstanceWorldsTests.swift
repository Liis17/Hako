import Foundation
import Testing
import zlib

struct InstanceWorldsTests {
    @Test func readsWorldMetadataAndCountsAllDimensions() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let world = root.appendingPathComponent("minecraft/saves/Folder")
        try FileManager.default.createDirectory(at: world.appendingPathComponent("DIM-1/region"), withIntermediateDirectories: true)
        let metadata = try WorldTestFixture.level(name: "Мир 🌸", version: "1.21.1", mode: 1, played: 1_700_000_000_000)
        try metadata.write(to: world.appendingPathComponent("level.dat"))
        try Data(repeating: 1, count: 100).write(to: world.appendingPathComponent("DIM-1/region/chunk.mca"))
        try Data(repeating: 2, count: 20).write(to: world.appendingPathComponent(".hidden"))
        let worlds = try await InstanceWorlds().list(in: root)
        let item = try #require(worlds.first)
        #expect(worlds.count == 1 && item.id == "Folder")
        #expect(item.name == "Мир 🌸" && item.version == "1.21.1" && item.gameType == 1)
        #expect(item.lastPlayed == Date(timeIntervalSince1970: 1_700_000_000))
        #expect(item.size == Int64(metadata.count + 120))
        #expect(item.iconData == nil && item.metadataError == nil)
    }

    @Test func keepsOldAndDamagedWorldsAndUsesBackupMetadata() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let saves = root.appendingPathComponent("minecraft/saves")
        for name in ["Old", "Broken", "Recovered", "Unrelated"] {
            try FileManager.default.createDirectory(at: saves.appendingPathComponent(name), withIntermediateDirectories: true)
        }
        try WorldTestFixture.level(name: "Legacy", version: nil, played: 1000).write(to: saves.appendingPathComponent("Old/level.dat"))
        try Data([1, 2, 3]).write(to: saves.appendingPathComponent("Broken/level.dat"))
        try Data([1, 2, 3]).write(to: saves.appendingPathComponent("Recovered/level.dat"))
        try WorldTestFixture.level(name: "Backup", played: 2000).write(to: saves.appendingPathComponent("Recovered/level.dat_old"))
        try Data("image".utf8).write(to: saves.appendingPathComponent("Recovered/icon.png"))
        let worlds = try await InstanceWorlds().list(in: root)
        #expect(worlds.map(\.id) == ["Recovered", "Old", "Broken"])
        #expect(worlds[0].name == "Backup" && worlds[0].metadataError == nil && worlds[0].iconData == Data("image".utf8))
        #expect(worlds[1].version == nil && worlds[1].gameType == 0)
        #expect(worlds[2].name == "Broken" && worlds[2].metadataError != nil)
    }

    @Test func ignoresLinksAndRejectsInvalidOrMissingInstallTargets() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let world = root.appendingPathComponent("minecraft/saves/Real")
        try FileManager.default.createDirectory(at: world, withIntermediateDirectories: true)
        let metadata = try WorldTestFixture.level()
        try metadata.write(to: world.appendingPathComponent("level.dat"))
        let outside = root.appendingPathComponent("Outside")
        try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: true)
        try Data(repeating: 0, count: 1000).write(to: outside.appendingPathComponent("chunk"))
        try FileManager.default.createSymbolicLink(at: world.appendingPathComponent("linked"), withDestinationURL: outside)
        try FileManager.default.createSymbolicLink(at: world.appendingPathComponent("icon.png"), withDestinationURL: outside.appendingPathComponent("chunk"))
        try FileManager.default.createSymbolicLink(at: world.deletingLastPathComponent().appendingPathComponent("Alias"), withDestinationURL: world)
        let worlds = try await InstanceWorlds().list(in: root)
        #expect(worlds.count == 1 && worlds[0].size == Int64(metadata.count) && worlds[0].iconData == nil)
        #expect(try InstanceWorlds.datapacksFolder(world: "Real", in: root) == world.appendingPathComponent("datapacks"))
        for name in ["Alias", "Missing", "../Outside", ""] {
            #expect(throws: InstanceFileError.self) { try InstanceWorlds.datapacksFolder(world: name, in: root) }
        }
        try FileManager.default.createSymbolicLink(at: world.appendingPathComponent("datapacks"), withDestinationURL: outside)
        #expect(throws: InstanceFileError.self) { try InstanceWorlds.datapacksFolder(world: "Real", in: root) }
        try FileManager.default.removeItem(at: world)
        #expect(throws: InstanceFileError.self) { try InstanceWorlds.datapacksFolder(world: "Real", in: root) }
        #expect(!FileManager.default.fileExists(atPath: world.path))
    }

    @Test func missingSavesIsAnEmptyList() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        #expect(try await InstanceWorlds().list(in: root).isEmpty)
        #expect(!FileManager.default.fileExists(atPath: root.path))
    }

    @Test func skipsUnknownTagsAndDecodesJavaSurrogatePairs() throws {
        let extra = Data([8, 0, 9]) + Data("LevelName".utf8) + Data([0, 6, 0xED, 0xA0, 0xBC, 0xED, 0xBC, 0xB8])
        let arrays = Data([11, 0, 1, 97, 0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0, 2])
        let metadata = try WorldMetadata.read(WorldTestFixture.level(extra: arrays + extra))
        #expect(metadata.name == "🌸" && metadata.version == "1.21.1")
    }

    @Test func rejectsInvalidLengthsTruncationAndExcessiveNesting() throws {
        let invalid = Data([7, 0, 1, 97, 255, 255, 255, 255])
        #expect(throws: CocoaError.self) { try WorldMetadata.read(WorldTestFixture.level(extra: invalid)) }
        let truncated = Data([12, 0, 1, 97, 0, 0, 0, 10])
        #expect(throws: CocoaError.self) { try WorldMetadata.read(WorldTestFixture.level(extra: truncated)) }
        let deep = Data(Array(repeating: [UInt8(10), 0, 1, 97], count: 70).flatMap { $0 }) + Data(repeating: 0, count: 70)
        #expect(throws: CocoaError.self) { try WorldMetadata.read(WorldTestFixture.level(extra: deep)) }
        let gzip = try WorldTestFixture.level()
        #expect(throws: CocoaError.self) { try WorldMetadata.read(Data(gzip.dropLast(8))) }
        #expect(throws: CocoaError.self) { try WorldMetadata.read(WorldTestFixture.gzip(Data(repeating: 0, count: 16_777_217))) }
    }
}

nonisolated enum WorldTestFixture {
    static func level(name: String = "World", version: String? = "1.21.1", mode: Int32 = 0, played: Int64 = 0, extra: Data = Data()) throws -> Data {
        var data = Data([10, 0, 0, 10, 0, 4]); data.append(Data("Data".utf8))
        func text(_ value: String) -> Data {
            let bytes = Data(value.utf8), count = UInt16(bytes.count)
            return Data([UInt8(count >> 8), UInt8(count & 255)]) + bytes
        }
        func integer<T: FixedWidthInteger>(_ value: T) -> Data { withUnsafeBytes(of: value.bigEndian) { Data($0) } }
        data += Data([8]) + text("LevelName") + text(name)
        data += Data([3]) + text("GameType") + integer(mode)
        data += Data([4]) + text("LastPlayed") + integer(played)
        if let version { data += Data([10]) + text("Version") + Data([8]) + text("Name") + text(version) + Data([0]) }
        data += extra + Data([0, 0])
        return try gzip(data)
    }

    static func gzip(_ data: Data) throws -> Data {
        var stream = z_stream()
        guard deflateInit2_(&stream, Z_DEFAULT_COMPRESSION, Z_DEFLATED, 31, 8, Z_DEFAULT_STRATEGY, ZLIB_VERSION, Int32(MemoryLayout<z_stream>.size)) == Z_OK else { throw CocoaError(.fileReadCorruptFile) }
        defer { deflateEnd(&stream) }
        var output = [UInt8](repeating: 0, count: Int(compressBound(uLong(data.count))) + 32)
        return try data.withUnsafeBytes { input in
            try output.withUnsafeMutableBytes { buffer in
                stream.next_in = UnsafeMutablePointer(mutating: input.bindMemory(to: UInt8.self).baseAddress)
                stream.avail_in = uInt(data.count)
                stream.next_out = buffer.bindMemory(to: UInt8.self).baseAddress
                stream.avail_out = uInt(buffer.count)
                guard deflate(&stream, Z_FINISH) == Z_STREAM_END else { throw CocoaError(.fileReadCorruptFile) }
                return Data(buffer.prefix(Int(stream.total_out)))
            }
        }
    }
}

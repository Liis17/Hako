import Foundation
import Testing

struct InstanceStorageUsageTests {
    private func temporary() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }

    private func write(_ blocks: Int, to path: String, in root: URL) throws {
        let url = root.appendingPathComponent(path)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(repeating: 1, count: blocks * 32768).write(to: url)
    }

    @Test func splitsInstanceFilesIntoCategoriesAndSubtractsDatapacksFromWorlds() throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        try write(1, to: "A/java/bin/java", in: root)
        try write(2, to: "A/minecraft/assets/index.json", in: root)
        try write(3, to: "A/minecraft/mods/mod.jar", in: root)
        try write(4, to: "A/minecraft/resourcepacks/pack.zip", in: root)
        try write(5, to: "A/minecraft/.hako-disabled-resourcepacks/old.zip", in: root)
        try write(6, to: "A/minecraft/screenshots/shot.png", in: root)
        try write(7, to: "A/minecraft/saves/W/level.dat", in: root)
        try write(8, to: "A/minecraft/saves/W/region/r.0.0.mca", in: root)
        try write(9, to: "A/minecraft/saves/W/datapacks/pack.zip", in: root)
        try write(10, to: "A/minecraft/saves/W/.hako-disabled-datapacks/off.zip", in: root)
        try write(11, to: "A/icon.png", in: root)
        try write(12, to: "backups/A.hakobackup", in: root)
        try write(13, to: "worlds/W.hakoworld", in: root)
        try FileManager.default.createSymbolicLink(at: root.appendingPathComponent("A/minecraft/mods/link.jar"), withDestinationURL: root.appendingPathComponent("A/minecraft/mods/mod.jar"))

        let usage = try InstanceStorage(root: root).usage()
        let block: Int64 = 32768
        #expect(usage[.java] == 1 * block)
        #expect(usage[.minecraft] == (2 + 11) * block)
        #expect(usage[.mods] == 3 * block)
        #expect(usage[.resourcepacks] == (4 + 5) * block)
        #expect(usage[.screenshots] == 6 * block)
        #expect(usage[.worlds] == (7 + 8) * block)
        #expect(usage[.datapacks] == (9 + 10) * block)
        #expect(usage[.backups] == (12 + 13) * block)
        #expect(usage.total == 91 * block)
    }

    @Test func missingRootIsEmpty() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        #expect(try InstanceStorage(root: root).usage().total == 0)
    }
}

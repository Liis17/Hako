import Foundation
import Testing

struct InstanceContentTests {
    private func temporary() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }

    @Test func importsAreIndependentAndReplacementRequiresConsent() async throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("Pack.zip")
        let first = root.appendingPathComponent("First/resourcepacks")
        let second = root.appendingPathComponent("Second/resourcepacks")
        let content = InstanceContent()
        try Data("original".utf8).write(to: source)
        try await content.importPack(from: source, into: first)
        try await content.importPack(from: source, into: second)
        try FileManager.default.removeItem(at: source)
        #expect(try Data(contentsOf: first.appendingPathComponent("Pack.zip")) == Data("original".utf8))
        let replacement = root.appendingPathComponent("pack.ZIP")
        try Data("new".utf8).write(to: replacement)
        await #expect(throws: PackImportError.self) { try await content.importPack(from: replacement, into: first) }
        try await content.importPack(from: replacement, into: first, replace: true)
        let items = try await content.list(at: first, mods: false)
        #expect(items.count == 1)
        #expect(try Data(contentsOf: items[0].url) == Data("new".utf8))
        #expect(try Data(contentsOf: second.appendingPathComponent("Pack.zip")) == Data("original".utf8))
    }

    @Test func foldersCopyRecursivelyAndListsIgnoreLinksAndUnrelatedFiles() async throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("Folder Pack")
        let folder = root.appendingPathComponent("Instance/resourcepacks")
        try FileManager.default.createDirectory(at: source.appendingPathComponent("assets"), withIntermediateDirectories: true)
        try Data(repeating: 7, count: 32768).write(to: source.appendingPathComponent("assets/texture.png"))
        let content = InstanceContent()
        try await content.importPack(from: source, into: folder)
        try FileManager.default.removeItem(at: source)
        #expect(try Data(contentsOf: folder.appendingPathComponent("Folder Pack/assets/texture.png")).count == 32768)
        try Data().write(to: folder.appendingPathComponent("mod.JAR"))
        try Data().write(to: folder.appendingPathComponent("readme.txt"))
        try FileManager.default.createSymbolicLink(at: folder.appendingPathComponent("linked.zip"), withDestinationURL: folder.appendingPathComponent("Folder Pack"))
        #expect(try await content.list(at: folder, mods: false).map(\.name) == ["Folder Pack"])
        #expect(try await content.list(at: folder, mods: true).map(\.name) == ["mod.JAR"])
        #expect(try InstanceStorage(root: root.appendingPathComponent("Instance")).allocatedSize() >= 32768)
    }

    @Test func importRejectsLinkedFilesAndLinkedDirectoryContents() async throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("Unsafe Pack")
        let target = root.appendingPathComponent("Instance/resourcepacks")
        try FileManager.default.createDirectory(at: source, withIntermediateDirectories: false)
        try FileManager.default.createSymbolicLink(at: source.appendingPathComponent("escape"), withDestinationURL: root.deletingLastPathComponent())
        let content = InstanceContent()
        await #expect(throws: InstanceFileError.self) { try await content.importPack(from: source, into: target) }
        let link = root.appendingPathComponent("linked.zip")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: source)
        await #expect(throws: InstanceFileError.self) { try await content.importPack(from: link, into: target) }
        #expect(!FileManager.default.fileExists(atPath: target.path))
    }

    @Test func importCannotCopyParentDirectoryIntoItself() async throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("minecraft")
        let target = source.appendingPathComponent("resourcepacks")
        try FileManager.default.createDirectory(at: source, withIntermediateDirectories: false)
        try Data("unchanged".utf8).write(to: source.appendingPathComponent("marker"))
        await #expect(throws: InstanceFileError.self) { try await InstanceContent().importPack(from: source, into: target) }
        #expect(!FileManager.default.fileExists(atPath: target.path))
        #expect(try Data(contentsOf: source.appendingPathComponent("marker")) == Data("unchanged".utf8))
    }
}

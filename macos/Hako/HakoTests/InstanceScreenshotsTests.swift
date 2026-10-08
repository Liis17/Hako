import Foundation
import ImageIO
import Testing
import UniformTypeIdentifiers

struct InstanceScreenshotsTests {
    @Test func listsOnlyRegularPNGFilesNewestFirst() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        #expect(try await InstanceScreenshots().list(in: root).isEmpty)
        let folder = root.appendingPathComponent("minecraft/screenshots")
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("Folder.png"), withIntermediateDirectories: true)
        for (name, created) in [("old.png", 1000.0), ("new.PNG", 3000), ("b.png", 2000), ("a.png", 2000), ("notes.txt", 4000), (".hidden.png", 5000)] {
            let url = folder.appendingPathComponent(name)
            try Data("image".utf8).write(to: url)
            try FileManager.default.setAttributes([.creationDate: Date(timeIntervalSince1970: created)], ofItemAtPath: url.path)
        }
        try FileManager.default.createSymbolicLink(at: folder.appendingPathComponent("link.png"), withDestinationURL: folder.appendingPathComponent("old.png"))
        let screenshots = try await InstanceScreenshots().list(in: root)
        #expect(screenshots.map(\.url.lastPathComponent) == ["new.PNG", "a.png", "b.png", "old.png"])
        #expect(screenshots.first?.created == Date(timeIntervalSince1970: 3000))
    }

    @Test func rejectsLinkedFolderAndForeignFilesOnTrash() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let outside = root.appendingPathComponent("Outside")
        try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: true)
        try Data("image".utf8).write(to: outside.appendingPathComponent("shot.png"))
        let instance = root.appendingPathComponent("Instance")
        try FileManager.default.createDirectory(at: instance.appendingPathComponent("minecraft/screenshots"), withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: instance.appendingPathComponent("minecraft/screenshots/link.png"), withDestinationURL: outside.appendingPathComponent("shot.png"))
        let screenshots = InstanceScreenshots()
        await #expect(throws: InstanceFileError.self) {
            try await screenshots.trash([.init(url: outside.appendingPathComponent("shot.png"), created: nil)], in: instance)
        }
        let folder = try InstanceScreenshots.folder(in: instance)
        await #expect(throws: InstanceFileError.self) {
            try await screenshots.trash([.init(url: folder.appendingPathComponent("link.png"), created: nil)], in: instance)
        }
        #expect(FileManager.default.fileExists(atPath: outside.appendingPathComponent("shot.png").path))

        let linked = root.appendingPathComponent("Linked")
        try FileManager.default.createDirectory(at: linked.appendingPathComponent("minecraft/shots"), withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: linked.appendingPathComponent("minecraft/screenshots"), withDestinationURL: linked.appendingPathComponent("minecraft/shots"))
        await #expect(throws: InstanceFileError.self) { try await screenshots.list(in: linked) }
    }

    @Test func createsBoundedThumbnail() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let url = root.appendingPathComponent("shot.png")
        let context = try #require(CGContext(data: nil, width: 1920, height: 1080, bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        let destination = try #require(CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil))
        CGImageDestinationAddImage(destination, try #require(context.makeImage()), nil)
        #expect(CGImageDestinationFinalize(destination))
        let thumbnail = try #require(await InstanceScreenshots().thumbnail(url, maxPixelSize: 512))
        #expect(thumbnail.width == 512 && thumbnail.height == 288)
    }
}

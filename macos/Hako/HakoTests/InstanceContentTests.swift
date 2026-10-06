import Foundation
import Testing
import CryptoKit

struct InstanceContentTests {
    private func temporary() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }

    @Test(arguments: [false, true]) func readsModIconFromEnabledAndDisabledJAR(sized: Bool) async throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let assets = root.appendingPathComponent("assets")
        try FileManager.default.createDirectory(at: assets, withIntermediateDirectories: true)
        let small = Data("small icon".utf8), large = Data("large icon".utf8)
        try small.write(to: assets.appendingPathComponent("small.png"))
        try large.write(to: assets.appendingPathComponent("large.png"))
        let icon: Any = sized ? ["16": "assets/small.png", "128": "assets/large.png"] : "assets/small.png"
        try JSONSerialization.data(withJSONObject: ["icon": icon]).write(to: root.appendingPathComponent("fabric.mod.json"))
        let archive = root.appendingPathComponent(sized ? "mod.jar.disabled" : "mod.jar")
        let process = Process(); process.executableURL = URL(fileURLWithPath: "/usr/bin/zip")
        process.currentDirectoryURL = root; process.arguments = ["-q", "-r", archive.path, "fabric.mod.json", "assets"]
        try process.run(); process.waitUntilExit()
        #expect(process.terminationStatus == 0)
        let item = InstanceContentItem(url: archive, isDirectory: false)
        #expect(try await InstanceContent().modIconData(item) == (sized ? large : small))
        await #expect(throws: MojangError.self) { try await FabricClient.archiveEntry("../small.png", in: archive, limit: 1024) }
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

    @Test func jarImportPreservesDisabledStateAndRejectsOtherTypes() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("Local.JAR"), folder = root.appendingPathComponent("Instance/minecraft/mods")
        try Data("old".utf8).write(to: source)
        let content = InstanceContent()
        try await content.importItem(from: source, into: folder, mods: true)
        let item = try #require(await content.list(at: folder, mods: true).first)
        #expect(item.source == .local)
        try await content.setEnabled(item, in: folder, enabled: false)
        let disabled = try #require(await content.list(at: folder, mods: true).first)
        #expect(!disabled.enabled)
        #expect(disabled.logicalName == "Local.JAR")
        try Data("new".utf8).write(to: source)
        await #expect(throws: PackImportError.self) { try await content.importItem(from: source, into: folder, mods: true) }
        try await content.importItem(from: source, into: folder, mods: true, replace: true)
        #expect(try Data(contentsOf: disabled.url) == Data("new".utf8))
        #expect(try await content.list(at: folder, mods: true).count == 1)
        let zip = root.appendingPathComponent("wrong.zip"); try Data().write(to: zip)
        await #expect(throws: InstanceFileError.self) { try await content.importItem(from: zip, into: folder, mods: true) }
        await #expect(throws: InstanceFileError.self) { try await content.importItem(from: root, into: folder, mods: true) }
    }

    private func descriptor(_ data: Data, filename: String = "api.jar", version: String = "old") -> FabricAPIDescriptor {
        .init(projectID: "P7dR8mSH", versionID: version, version: version, channel: "release", filename: filename, url: URL(string: "https://fixtures.test/\(filename)")!, size: Int64(data.count), sha1: Insecure.SHA1.hash(data: data).map { String(format: "%02x", $0) }.joined(), sha512: FabricClient.hash(data))
    }

    @Test func provenanceSurvivesToggleRenameAndDisabledUpdateButNotLocalReplacement() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let cache = root.appendingPathComponent("cached.jar"), folder = root.appendingPathComponent("Instance/minecraft/mods")
        let content = InstanceContent(), bytes = Data("old".utf8), next = Data("new".utf8)
        try bytes.write(to: cache)
        try await content.provisionAPI(descriptor(bytes), from: cache, in: folder)
        let item = try #require(await content.list(at: folder, mods: true).first)
        #expect(item.source == .modrinth)
        try await content.setEnabled(item, in: folder, enabled: false)
        let renamed = root.appendingPathComponent("Renamed")
        try FileManager.default.moveItem(at: root.appendingPathComponent("Instance"), to: renamed)
        let movedFolder = renamed.appendingPathComponent("minecraft/mods")
        let disabled = try #require(await content.list(at: movedFolder, mods: true).first)
        #expect(disabled.source == .modrinth)
        try next.write(to: cache)
        try await content.updateAPI(disabled, to: descriptor(next, filename: "new-api.jar", version: "new"), from: cache, in: movedFolder)
        let updated = try #require(await content.list(at: movedFolder, mods: true).first)
        #expect(!updated.enabled && updated.source == .modrinth)
        #expect(updated.name == "new-api.jar.disabled")
        #expect(updated.origin?.versionID == "new")
        #expect(!FileManager.default.fileExists(atPath: disabled.url.path))
        let local = root.appendingPathComponent("new-api.jar"); try next.write(to: local)
        try await content.importItem(from: local, into: movedFolder, mods: true, replace: true)
        #expect(try await content.list(at: movedFolder, mods: true).first?.source == .local)
        #expect(try await content.apiWasProvisioned(in: movedFolder))
    }

    @Test func externalChangesAndBrokenRegistryDisableRemoteUpdatesWithoutLosingFiles() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let cache = root.appendingPathComponent("cached.jar"), folder = root.appendingPathComponent("Instance/minecraft/mods")
        let content = InstanceContent(), bytes = Data("old".utf8)
        try bytes.write(to: cache); try await content.provisionAPI(descriptor(bytes), from: cache, in: folder)
        let item = try #require(await content.list(at: folder, mods: true).first)
        try Data("changed".utf8).write(to: item.url)
        #expect(try await content.list(at: folder, mods: true).first?.source == .local)
        await #expect(throws: InstanceFileError.self) { try await content.updateAPI(item, to: descriptor(bytes), from: cache, in: folder) }
        try bytes.write(to: item.url)
        let registry = folder.deletingLastPathComponent().appendingPathComponent(".hako-mods.json")
        try Data("broken".utf8).write(to: registry)
        await #expect(throws: DecodingError.self) { try await content.list(at: folder, mods: true) }
        #expect(try await content.list(at: folder, mods: true, readOrigins: false).first?.source == .local)
        await #expect(throws: DecodingError.self) { try await content.updateAPI(item, to: descriptor(bytes), from: cache, in: folder) }
        #expect(try Data(contentsOf: item.url) == bytes)
        #expect(try Data(contentsOf: registry) == Data("broken".utf8))
    }

    @Test func toggleDoesNotOverwriteOppositeStateAndImportRejectsDestinationLinks() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appendingPathComponent("minecraft/mods")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let active = folder.appendingPathComponent("mod.jar"), disabled = folder.appendingPathComponent("mod.jar.disabled")
        try Data("active".utf8).write(to: active); try Data("disabled".utf8).write(to: disabled)
        let content = InstanceContent()
        await #expect(throws: PackImportError.self) { try await content.setEnabled(.init(url: active, isDirectory: false), in: folder, enabled: false) }
        #expect(try Data(contentsOf: disabled) == Data("disabled".utf8))
        let source = root.appendingPathComponent("link.jar"); try Data("source".utf8).write(to: source)
        try FileManager.default.createSymbolicLink(at: folder.appendingPathComponent("link.jar"), withDestinationURL: active)
        await #expect(throws: InstanceFileError.self) { try await content.importItem(from: source, into: folder, mods: true, replace: true) }
        #expect(try Data(contentsOf: active) == Data("active".utf8))
    }
}

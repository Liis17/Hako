import Foundation
import SwiftData
import Testing

@MainActor struct InstanceManagementTests {
    private func container() throws -> ModelContainer {
        try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
    }

    private func temporary() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    private let api = FabricAPIDescriptor(projectID: FabricAPIDescriptor.project, versionID: "api", version: "0.100.0", channel: "release", filename: "fabric-api.jar", url: URL(string: "https://example.test/fabric-api.jar")!, size: 3, sha1: "sha1", sha512: "sha512")

    private func draft(_ name: String) -> InstanceDraft {
        var draft = InstanceDraft()
        draft.name = name
        return draft
    }

    @Test func deleteErasesFolderAndRecord() throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let store = InstanceStore(context: container.mainContext, storage: .init(root: root))
        let instance = try store.create(draft("Doomed Pack"), versionID: "1.19", metadataURL: "url", metadataSHA1: "hash")
        instance.state = .ready
        try container.mainContext.save()
        try Data("world".utf8).write(to: root.appendingPathComponent("Doomed_Pack/minecraft/level.dat"))
        try store.delete(instance)
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("Doomed_Pack").path))
        #expect(try container.mainContext.fetch(FetchDescriptor<GameInstance>()).isEmpty)
    }

    @Test func deleteRestoresFolderWhenSaveFails() throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        var fail = false
        let store = InstanceStore(context: container.mainContext, storage: .init(root: root), persist: {
            if fail { throw InstanceFileError.message("Test save failure") }
            try container.mainContext.save()
        })
        let instance = try store.create(draft("Kept"), versionID: "1.19", metadataURL: "url", metadataSHA1: "hash")
        instance.state = .failed
        try container.mainContext.save()
        fail = true
        #expect(throws: InstanceFileError.self) { try store.delete(instance) }
        #expect(FileManager.default.fileExists(atPath: root.appendingPathComponent("Kept/minecraft").path))
        #expect(try container.mainContext.fetch(FetchDescriptor<GameInstance>()).count == 1)
    }

    @Test func busyInstancesCannotBeDeleted() throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let store = InstanceStore(context: container.mainContext, storage: .init(root: root))
        let instance = try store.create(draft("Running"), versionID: "1.19", metadataURL: "url", metadataSHA1: "hash")
        instance.state = .ready
        store.launchBusy.insert(instance.id)
        #expect(throws: InstanceFileError.self) { try store.delete(instance) }
        store.launchBusy.removeAll()
        instance.state = .installing
        #expect(throws: InstanceFileError.self) { try store.delete(instance) }
        #expect(FileManager.default.fileExists(atPath: root.appendingPathComponent("Running").path))
    }

    @Test func duplicateNamesTakeNextFreeNumber() throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let store = InstanceStore(context: container.mainContext, storage: .init(root: root))
        let instance = try store.create(draft("Pack"), versionID: "1.19", metadataURL: "url", metadataSHA1: "hash")
        #expect(try store.duplicateName(for: instance) == "Pack 2")
        try FileManager.default.createDirectory(at: root.appendingPathComponent("pack_2"), withIntermediateDirectories: false)
        #expect(try store.duplicateName(for: instance) == "Pack 3")
        let long = try store.create(draft(String(repeating: "A", count: 59) + "B"), versionID: "1.19", metadataURL: "url", metadataSHA1: "hash")
        #expect(try store.duplicateName(for: long) == String(repeating: "A", count: 58) + " 2")
    }

    @Test func duplicateCopiesFilesAndProfileWithNewIdentity() async throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let store = InstanceStore(context: container.mainContext, storage: .init(root: root))
        var source = draft("Fabric Pack")
        source.iconSymbol = ""
        source.iconData = Data("icon".utf8)
        source.offlineMode = true
        source.offlineUsername = "Tester"
        source.argumentSource = .custom
        source.parameters.javaArguments = "-Dtest=1"
        source.modLoader = .fabric
        source.fabricConfiguration = FabricConfiguration(loaderVersion: "0.16.5", api: api)
        let instance = try store.create(source, versionID: "1.21.1", metadataURL: "https://example.test/v.json", metadataSHA1: "hash", javaMajorVersion: 21)
        instance.state = .ready
        instance.javaExecutable = "custom/bin/java"
        instance.fabricProfileSHA1 = "profile"
        try container.mainContext.save()
        let folder = root.appendingPathComponent("Fabric_Pack")
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("java/custom/bin"), withIntermediateDirectories: true)
        try Data("java".utf8).write(to: folder.appendingPathComponent("java/custom/bin/java"))
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("minecraft/mods"), withIntermediateDirectories: true)
        try Data("mod".utf8).write(to: folder.appendingPathComponent("minecraft/mods/mod.jar"))
        try Data("{}".utf8).write(to: folder.appendingPathComponent("minecraft/.hako-running.json"))

        let copy = try await store.duplicate(instance)
        let copied = root.appendingPathComponent("Fabric_Pack_2")
        #expect(copy.name == "Fabric Pack 2" && copy.folderName == "Fabric_Pack_2")
        #expect(copy.id != instance.id)
        #expect(try Data(contentsOf: copied.appendingPathComponent("java/custom/bin/java")) == Data("java".utf8))
        #expect(try Data(contentsOf: copied.appendingPathComponent("minecraft/mods/mod.jar")) == Data("mod".utf8))
        #expect(try Data(contentsOf: copied.appendingPathComponent("icon.png")) == Data("icon".utf8))
        #expect(!FileManager.default.fileExists(atPath: copied.appendingPathComponent("minecraft/.hako-running.json").path))
        #expect(copy.state == .ready && copy.versionID == "1.21.1" && copy.metadataURL == instance.metadataURL)
        #expect(copy.modLoader == .fabric)
        #expect(try copy.fabricConfiguration()?.loaderVersion == "0.16.5")
        #expect(copy.javaMajorVersion == 21 && copy.javaExecutable == "custom/bin/java" && copy.fabricProfileSHA1 == "profile")
        #expect(copy.iconSymbol.isEmpty && copy.offlineMode && copy.offlineUsername == "Tester")
        #expect(copy.argumentSource == .custom && copy.parameters == instance.parameters)
        #expect(store.contentBusy.isEmpty)
        instance.state = .paused
        await #expect(throws: InstanceFileError.self) { _ = try await store.duplicate(instance) }
    }

    @Test func backupArchivesFolderWithManifestAtRoot() async throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let store = InstanceStore(context: container.mainContext, storage: .init(root: root))
        var source = draft("Backup Pack")
        source.iconSymbol = ""
        source.iconData = Data("png".utf8)
        source.modLoader = .fabric
        source.fabricConfiguration = FabricConfiguration(loaderVersion: "0.16.5", api: api)
        let instance = try store.create(source, versionID: "1.21.1", metadataURL: "https://example.test/v.json", metadataSHA1: "hash", javaMajorVersion: 21)
        instance.state = .ready
        try container.mainContext.save()
        let folder = root.appendingPathComponent("Backup_Pack")
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("java/bin"), withIntermediateDirectories: true)
        try Data("java".utf8).write(to: folder.appendingPathComponent("java/bin/java"))
        try FileManager.default.createSymbolicLink(atPath: folder.appendingPathComponent("java/link").path, withDestinationPath: "bin/java")
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("minecraft/mods"), withIntermediateDirectories: true)
        try Data("one".utf8).write(to: folder.appendingPathComponent("minecraft/mods/one.jar"))
        try Data("two".utf8).write(to: folder.appendingPathComponent("minecraft/mods/two.jar.disabled"))
        try Data("{}".utf8).write(to: folder.appendingPathComponent("minecraft/.hako-running.json"))

        let archive = try await store.backup(instance, content: InstanceContent())
        #expect(archive.deletingLastPathComponent().standardizedFileURL == root.appendingPathComponent("backups").standardizedFileURL)
        #expect(archive.lastPathComponent.hasPrefix("Backup_Pack_") && archive.pathExtension == "hakobackup")
        #expect(try FileManager.default.contentsOfDirectory(atPath: archive.deletingLastPathComponent().path) == [archive.lastPathComponent])
        #expect(store.contentBusy.isEmpty)

        let listing = Process()
        let output = Pipe()
        listing.executableURL = URL(fileURLWithPath: "/usr/bin/unzip")
        listing.arguments = ["-Z1", archive.path]
        listing.standardOutput = output
        try listing.run()
        let entries = Set(String(decoding: output.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self).split(separator: "\n").map(String.init))
        listing.waitUntilExit()
        #expect(entries.isSuperset(of: ["data.json", "icon.png", "java/bin/java", "java/link", "minecraft/mods/one.jar", "minecraft/mods/two.jar.disabled"]))
        #expect(!entries.contains("minecraft/.hako-running.json"))

        let data = try await FabricClient.archiveEntry("data.json", in: archive, limit: 1_048_576)
        let manifest = try InstanceBackupManifest.decoder().decode(InstanceBackupManifest.self, from: data)
        #expect(manifest.formatVersion == 1 && manifest.name == "Backup Pack" && manifest.folderName == "Backup_Pack")
        #expect(manifest.minecraftVersion == "1.21.1" && manifest.modLoader == "fabric" && manifest.loaderVersion == "0.16.5" && manifest.javaMajorVersion == 21)
        #expect(manifest.iconPNG == Data("png".utf8) && manifest.iconSymbol.isEmpty)
        #expect(manifest.modCount == 2 && manifest.mods.map(\.file) == ["one.jar", "two.jar"] && manifest.mods.map(\.enabled) == [true, false])
        #expect(manifest.profile.metadataURL == instance.metadataURL && manifest.profile.fabricConfiguration?.api == api)
        #expect(String(decoding: data, as: UTF8.self).contains("\"iconPNG\" : \"\(Data("png".utf8).base64EncodedString())\""))
    }

    @Test func backupsFolderNameIsReserved() throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let store = InstanceStore(context: container.mainContext, storage: .init(root: root))
        #expect(throws: InstanceFileError.self) { try store.validateName("Backups") }
    }
}

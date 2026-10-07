import Foundation
import SwiftData
import Testing

@MainActor struct WorldManagementTests {
    private func fixture() throws -> (ModelContainer, URL, GameInstance, InstanceContentController) {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let installations = InstallationCoordinator(context: container.mainContext, storage: .init(root: root))
        var draft = InstanceDraft(); draft.name = "Source"
        let instance = try installations.store.create(draft, versionID: "1.21.1", metadataURL: "https://fixtures.test/version", metadataSHA1: "sha")
        instance.state = .ready
        let world = root.appendingPathComponent("Source/minecraft/saves/World")
        try FileManager.default.createDirectory(at: world.appendingPathComponent("datapacks/Folder"), withIntermediateDirectories: true)
        try WorldTestFixture.level(name: "Мир 🌸", mode: 1, played: 1700000000000).write(to: world.appendingPathComponent("level.dat"))
        try Data("icon".utf8).write(to: world.appendingPathComponent("icon.png"))
        try Data("{}".utf8).write(to: world.appendingPathComponent("datapacks/Folder/pack.mcmeta"))
        try Data("pack image".utf8).write(to: world.appendingPathComponent("datapacks/Folder/pack.png"))
        return (container, root, instance, InstanceContentController(installations: installations))
    }

    private func wait(_ controller: InstanceContentController, _ instance: GameInstance) async throws {
        for _ in 0..<500 where controller.installations.contentBusy.contains(instance.id) { try await Task.sleep(for: .milliseconds(10)) }
        #expect(!controller.installations.contentBusy.contains(instance.id))
    }

    @Test func datapackDisablingIsReversibleAndWorldScoped() async throws {
        let (container, root, instance, controller) = try fixture()
        defer { withExtendedLifetime(container) { try? FileManager.default.removeItem(at: root) } }
        let source = root.appendingPathComponent("Source"), world = source.appendingPathComponent("minecraft/saves/World")
        try FileManager.default.copyItem(at: world, to: world.deletingLastPathComponent().appendingPathComponent("Other"))
        let original = try Data(contentsOf: world.appendingPathComponent("level.dat"))
        await controller.reload(instance, target: .worldDatapacks("World"))
        let item = try #require(controller.datapacks[instance.id]?["World"]?.first)
        controller.setDatapackEnabled(item, world: "World", in: instance, enabled: false)
        try await wait(controller, instance)
        let disabled = try #require(controller.datapacks[instance.id]?["World"]?.first)
        #expect(!disabled.enabled)
        #expect(FileManager.default.fileExists(atPath: world.appendingPathComponent(".hako-disabled-datapacks/Folder/pack.png").path))
        #expect(!FileManager.default.fileExists(atPath: item.url.path))
        #expect(FileManager.default.fileExists(atPath: world.deletingLastPathComponent().appendingPathComponent("Other/datapacks/Folder").path))
        controller.setDatapackEnabled(disabled, world: "World", in: instance, enabled: true)
        try await wait(controller, instance)
        #expect(controller.datapacks[instance.id]?["World"]?.first?.enabled == true)
        #expect(try Data(contentsOf: world.appendingPathComponent("level.dat")) == original)
        try FileManager.default.removeItem(at: world)
        await #expect(throws: InstanceFileError.self) { try await controller.installations.content.setDatapackEnabled(disabled, world: "World", in: source, enabled: true) }
        #expect(!FileManager.default.fileExists(atPath: world.path))
    }

    @Test func duplicateRenamesCopyAndPreservesOriginalAndUnknownNBT() async throws {
        let (container, root, instance, controller) = try fixture()
        defer { withExtendedLifetime(container) { try? FileManager.default.removeItem(at: root) } }
        let source = root.appendingPathComponent("Source"), folder = source.appendingPathComponent("minecraft/saves/World")
        let extra = Data([8, 0, 6]) + Data("Custom".utf8) + Data([0, 4]) + Data("keep".utf8)
        let original = try WorldTestFixture.level(name: "Мир 🌸", extra: extra)
        try original.write(to: folder.appendingPathComponent("level.dat"))
        try original.write(to: folder.appendingPathComponent("level.dat_old"))
        let world = try #require(try await controller.worlds.list(in: source).first)
        controller.manageWorld(world, in: instance, action: .duplicate)
        try await wait(controller, instance)
        #expect(controller.errors[instance.id] == nil)
        let worlds = try await controller.worlds.list(in: source)
        #expect(Set(worlds.map(\.name)) == ["Мир 🌸", "Мир 🌸 2"])
        #expect(try Data(contentsOf: folder.appendingPathComponent("level.dat")) == original)
        #expect(try WorldMetadata.read(Data(contentsOf: folder.deletingLastPathComponent().appendingPathComponent("World 2/level.dat_old"))).name == "Мир 🌸 2")
        #expect(try await controller.worlds.duplicate(world, in: source) == "World 3")
        let renamed = try WorldMetadata.renamed(original, to: "New")
        // Независимо распаковываем gzip системной утилитой и проверяем неизвестный тег.
        let file = root.appendingPathComponent("renamed.gz"); try renamed.write(to: file)
        let process = Process(), pipe = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/gzip"); process.arguments = ["-dc", file.path]; process.standardOutput = pipe
        try process.run()
        let bytes = pipe.fileHandleForReading.readDataToEndOfFile(); process.waitUntilExit()
        #expect(bytes.range(of: extra) != nil)
    }

    @Test func backupContainsFullWorldAndReadableManifest() async throws {
        let (container, root, instance, controller) = try fixture()
        defer { withExtendedLifetime(container) { try? FileManager.default.removeItem(at: root) } }
        let source = root.appendingPathComponent("Source"), folder = source.appendingPathComponent("minecraft/saves/World")
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("DIM-1/region"), withIntermediateDirectories: true)
        try Data("region".utf8).write(to: folder.appendingPathComponent("DIM-1/region/r.0.0.mca"))
        try Data("world json".utf8).write(to: folder.appendingPathComponent("data.json"))
        try await InstanceBackup.zip(["-q", "-r", "../Zip.zip", "."], in: folder.appendingPathComponent("datapacks/Folder"))
        await controller.reload(instance, target: .worldDatapacks("World"))
        let item = try #require(controller.datapacks[instance.id]?["World"]?.first { $0.name == "Folder" })
        controller.setDatapackEnabled(item, world: "World", in: instance, enabled: false)
        try await wait(controller, instance)
        let world = try #require(try await controller.worlds.list(in: source).first)
        controller.manageWorld(world, in: instance, action: .backup)
        try await wait(controller, instance)
        #expect(controller.errors[instance.id] == nil)
        let archive = try #require(controller.worldBackupURLs[instance.id])
        #expect(archive.pathExtension == "hakoworld" && archive.deletingLastPathComponent() == root.appendingPathComponent("worlds"))
        let data = try await FabricClient.archiveEntry("data.json", in: archive, limit: 1_048_576)
        let manifest = try WorldBackupManifest.decoder().decode(WorldBackupManifest.self, from: data)
        #expect(manifest.name == "Мир 🌸" && manifest.folderName == "World" && manifest.worldPath == "world" && manifest.formatVersion == 1)
        #expect(manifest.sourceInstance.id == instance.id && manifest.sourceInstance.name == "Source" && manifest.sourceInstance.minecraftVersion == "1.21.1")
        #expect(manifest.iconPNG == Data("icon".utf8) && manifest.datapacks.count == 2)
        let disabled = try #require(manifest.datapacks.first { $0.file == "Folder" })
        #expect(!disabled.enabled && disabled.path == ".hako-disabled-datapacks/Folder" && disabled.isDirectory && disabled.iconPNG == Data("pack image".utf8))
        #expect(manifest.datapacks.first { $0.file == "Zip.zip" }?.sha512?.count == 128)
        #expect(manifest.datapacks.first { $0.file == "Zip.zip" }?.iconPNG == Data("pack image".utf8))
        #expect(try await FabricClient.archiveEntry("world/DIM-1/region/r.0.0.mca", in: archive, limit: 100) == Data("region".utf8))
        #expect(try await FabricClient.archiveEntry("world/data.json", in: archive, limit: 100) == Data("world json".utf8))
        #expect(try await FabricClient.archiveEntry("world/.hako-disabled-datapacks/Folder/pack.png", in: archive, limit: 100) == Data("pack image".utf8))
        #expect(try FileManager.default.contentsOfDirectory(atPath: archive.deletingLastPathComponent().path) == [archive.lastPathComponent])
        await #expect(throws: InstanceFileError.self) { _ = try await WorldBackup.archive(world: "World", in: source, manifest: manifest, into: archive.deletingLastPathComponent()) }
        try FileManager.default.removeItem(at: folder)
        await #expect(throws: InstanceFileError.self) { _ = try await WorldBackup.archive(world: "World", in: source, manifest: manifest, into: archive.deletingLastPathComponent()) }
        #expect(!FileManager.default.fileExists(atPath: folder.path))
    }

    @Test func deletionRequiresConfirmationAndRevalidatesWorld() async throws {
        let (container, root, instance, controller) = try fixture()
        defer { withExtendedLifetime(container) { try? FileManager.default.removeItem(at: root) } }
        let source = root.appendingPathComponent("Source"), folder = source.appendingPathComponent("minecraft/saves/World")
        let world = try #require(try await controller.worlds.list(in: source).first)
        controller.manageWorld(world, in: instance, action: .delete)
        for _ in 0..<100 where controller.confirmation == nil { try await Task.sleep(for: .milliseconds(10)) }
        let confirmation = try #require(controller.confirmation)
        #expect(confirmation.destructive && confirmation.title == "Удалить мир?")
        #expect(controller.installations.contentBusy.contains(instance.id))
        #expect(throws: InstanceFileError.self) { try controller.installations.enqueue(instance) }
        controller.resolveConfirmation(confirmation.id, accepted: false)
        try await wait(controller, instance)
        #expect(FileManager.default.fileExists(atPath: folder.path))
        controller.manageWorld(world, in: instance, action: .delete)
        for _ in 0..<100 where controller.confirmation == nil { try await Task.sleep(for: .milliseconds(10)) }
        let second = try #require(controller.confirmation)
        try FileManager.default.removeItem(at: folder)
        controller.resolveConfirmation(second.id, accepted: true)
        try await wait(controller, instance)
        #expect(controller.errors[instance.id] != nil && !FileManager.default.fileExists(atPath: folder.path))
    }

    @Test func operationsRejectRunningGameLinksAndReservedFolder() async throws {
        let (container, root, instance, controller) = try fixture()
        defer { withExtendedLifetime(container) { try? FileManager.default.removeItem(at: root) } }
        let source = root.appendingPathComponent("Source"), folder = source.appendingPathComponent("minecraft/saves/World")
        let world = try #require(try await controller.worlds.list(in: source).first)
        controller.installations.store.launchBusy.insert(instance.id)
        for action in [InstanceContentController.WorldAction.delete, .duplicate, .backup] { controller.manageWorld(world, in: instance, action: action) }
        #expect(controller.confirmation == nil && controller.worldBackupURLs[instance.id] == nil && controller.installations.contentBusy.isEmpty)
        controller.installations.store.launchBusy.remove(instance.id)
        #expect(throws: InstanceFileError.self) { try controller.installations.store.validateName("WORLDS") }
        try FileManager.default.createSymbolicLink(at: folder.appendingPathComponent("link"), withDestinationURL: root)
        for action in [InstanceContentController.WorldAction.duplicate, .backup] {
            controller.manageWorld(world, in: instance, action: action)
            try await wait(controller, instance)
            #expect(controller.errors[instance.id] != nil)
        }
        #expect(!FileManager.default.fileExists(atPath: folder.deletingLastPathComponent().appendingPathComponent("World 2").path))
        #expect(controller.worldBackupURLs[instance.id] == nil)
    }

    @Test func disabledZipReplacementKeepsItsStateAndDetectsEnableCollision() async throws {
        let (container, root, instance, controller) = try fixture()
        defer { withExtendedLifetime(container) { try? FileManager.default.removeItem(at: root) } }
        let source = root.appendingPathComponent("Source"), world = source.appendingPathComponent("minecraft/saves/World")
        let zip = root.appendingPathComponent("pack.zip"); try Data("first".utf8).write(to: zip)
        try await controller.installations.content.importDatapack(from: zip, world: "World", in: source)
        await controller.reload(instance, target: .worldDatapacks("World"))
        let item = try #require(controller.datapacks[instance.id]?["World"]?.first { $0.name == "pack.zip" })
        controller.setDatapackEnabled(item, world: "World", in: instance, enabled: false)
        try await wait(controller, instance)
        try Data("replacement".utf8).write(to: zip)
        await #expect(throws: PackImportError.self) { try await controller.installations.content.importDatapack(from: zip, world: "World", in: source) }
        try await controller.installations.content.importDatapack(from: zip, world: "World", in: source, replace: true)
        #expect(try Data(contentsOf: world.appendingPathComponent(".hako-disabled-datapacks/pack.zip")) == Data("replacement".utf8))
        await controller.reload(instance, target: .worldDatapacks("World"))
        let disabled = try #require(controller.datapacks[instance.id]?["World"]?.first { $0.name == "pack.zip" })
        try Data("collision".utf8).write(to: world.appendingPathComponent("datapacks/PACK.zip"))
        await #expect(throws: PackImportError.self) { try await controller.installations.content.setDatapackEnabled(disabled, world: "World", in: source, enabled: true) }
        #expect(FileManager.default.fileExists(atPath: disabled.url.path))
        try FileManager.default.removeItem(at: world.appendingPathComponent("datapacks/PACK.zip"))
        controller.setDatapackEnabled(disabled, world: "World", in: instance, enabled: true)
        try await wait(controller, instance)
        #expect(controller.datapacks[instance.id]?["World"]?.first { $0.name == "pack.zip" }?.enabled == true)
    }
}

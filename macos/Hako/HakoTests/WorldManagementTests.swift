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

    private func datapackArchive(in root: URL, description: String) throws -> Data {
        let folder = root.appendingPathComponent(UUID().uuidString), archive = root.appendingPathComponent("\(UUID().uuidString).zip")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try JSONSerialization.data(withJSONObject: ["pack": ["pack_format": 48, "description": description]]).write(to: folder.appendingPathComponent("pack.mcmeta"))
        let process = Process(); process.executableURL = URL(fileURLWithPath: "/usr/bin/zip")
        process.currentDirectoryURL = folder; process.arguments = ["-q", archive.path, "pack.mcmeta"]
        try process.run(); process.waitUntilExit()
        #expect(process.terminationStatus == 0)
        defer { try? FileManager.default.removeItem(at: folder); try? FileManager.default.removeItem(at: archive) }
        return try Data(contentsOf: archive)
    }

    private func backupArchive(in root: URL, instance: GameInstance) async throws -> URL {
        let source = WorldBackupManifest.SourceInstance(id: instance.id, name: instance.name, folderName: instance.folderName,
                                                        minecraftVersion: instance.versionID, modLoader: instance.modLoaderRaw,
                                                        loaderVersion: try instance.fabricConfiguration()?.loaderVersion)
        let folder = root.appendingPathComponent("\(instance.folderName)/minecraft/saves/World", isDirectory: true)
        let metadata = try WorldMetadata.read(Data(contentsOf: folder.appendingPathComponent("level.dat")))
        let manifest = WorldBackupManifest(backupCreatedAt: Date(), name: try #require(metadata.name), folderName: "World", saveVersion: metadata.version,
                                           gameType: metadata.gameType, lastPlayed: metadata.lastPlayed, size: nil, iconPNG: nil,
                                           sourceInstance: source, datapacks: [])
        return try await WorldBackup.archive(world: "World", in: root.appendingPathComponent(instance.folderName), manifest: manifest,
                                             into: root.appendingPathComponent(InstanceStorage.worldsFolder))
    }

    private func waitForRestoreStage(_ coordinator: BackupRestoreCoordinator) async throws {
        for _ in 0..<500 where coordinator.staged == nil && coordinator.errorMessage == nil {
            try await Task.sleep(for: .milliseconds(10))
        }
        #expect(coordinator.staged != nil || coordinator.errorMessage != nil)
    }

    private func waitForRestoreCommit(_ coordinator: BackupRestoreCoordinator) async throws {
        for _ in 0..<500 where coordinator.isCommitting { try await Task.sleep(for: .milliseconds(10)) }
        #expect(!coordinator.isCommitting)
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
        let staged = try await RestoreArchive.prepare(archive, under: root)
        defer { RestoreArchive.discardStage(staged.directory) }
        guard case .world(let restoredManifest) = staged.kind else { Issue.record("Expected a world archive."); return }
        #expect(restoredManifest.folderName == "World" && restoredManifest.worldPath == "world")
        #expect(try Data(contentsOf: staged.directory.appendingPathComponent("world/DIM-1/region/r.0.0.mca")) == Data("region".utf8))
        #expect(try WorldMetadata.read(Data(contentsOf: staged.directory.appendingPathComponent("world/level.dat"))).name == manifest.name)
        #expect(FileManager.default.fileExists(atPath: staged.directory.appendingPathComponent("world/.hako-disabled-datapacks/Folder/pack.mcmeta").path))
        await #expect(throws: InstanceFileError.self) { _ = try await WorldBackup.archive(world: "World", in: source, manifest: manifest, into: archive.deletingLastPathComponent()) }
        try FileManager.default.removeItem(at: folder)
        await #expect(throws: InstanceFileError.self) { _ = try await WorldBackup.archive(world: "World", in: source, manifest: manifest, into: archive.deletingLastPathComponent()) }
        #expect(!FileManager.default.fileExists(atPath: folder.path))
    }

    @Test func restoresWorldCopyWithRenamedNBTAndInvalidatesWorldCaches() async throws {
        let (container, root, instance, content) = try fixture()
        defer { withExtendedLifetime(container) { try? FileManager.default.removeItem(at: root) } }
        let sourceWorld = root.appendingPathComponent("Source/minecraft/saves/World", isDirectory: true)
        try WorldTestFixture.level(name: "Мир 🌸").write(to: sourceWorld.appendingPathComponent("level.dat_old"))
        try Data("archived region".utf8).write(to: sourceWorld.appendingPathComponent("region.dat"))
        let archive = try await backupArchive(in: root, instance: instance)
        try Data("modified live world".utf8).write(to: sourceWorld.appendingPathComponent("live-only.txt"))

        let cached = InstanceContentItem(url: sourceWorld.appendingPathComponent("stale.zip"), isDirectory: false)
        content.datapacks[instance.id] = ["Imported World": [cached]]
        content.worldBackupURLs[instance.id] = archive
        let oldRevision = content.worldRevisions[instance.id, default: 0]
        let coordinator = BackupRestoreCoordinator(installations: content.installations, content: content)
        coordinator.enqueue([archive])
        try await waitForRestoreStage(coordinator)
        coordinator.selectWorldTarget(instance.id)
        content.installations.store.launchBusy.insert(instance.id)
        #expect(coordinator.worldTargetError != nil)
        content.installations.store.launchBusy.remove(instance.id)
        coordinator.worldName = "Imported Realm"
        coordinator.worldFolderName = "Imported World"
        coordinator.worldAction = .copy
        coordinator.requestCommit()
        try await waitForRestoreCommit(coordinator)

        let copy = root.appendingPathComponent("Source/minecraft/saves/Imported World", isDirectory: true)
        #expect(coordinator.errorMessage == nil && coordinator.currentURL == nil)
        #expect(try WorldMetadata.read(Data(contentsOf: copy.appendingPathComponent("level.dat"))).name == "Imported Realm")
        #expect(try WorldMetadata.read(Data(contentsOf: copy.appendingPathComponent("level.dat_old"))).name == "Imported Realm")
        #expect(try Data(contentsOf: copy.appendingPathComponent("region.dat")) == Data("archived region".utf8))
        #expect(FileManager.default.fileExists(atPath: sourceWorld.appendingPathComponent("live-only.txt").path))
        #expect(content.worldRevisions[instance.id] == oldRevision + 1)
        #expect(content.datapacks[instance.id]?["Imported World"] == nil && content.worldBackupURLs[instance.id] == nil)
        #expect(coordinator.navigation?.world == "Imported World" && coordinator.navigation?.instanceID == instance.id)
        #expect(content.installations.store.contentBusy.isEmpty)

        try Data("replace-only".utf8).write(to: sourceWorld.appendingPathComponent("replace-only.txt"))
        let replacement = BackupRestoreCoordinator(installations: content.installations, content: content)
        replacement.enqueue([archive])
        try await waitForRestoreStage(replacement)
        replacement.selectWorldTarget(instance.id)
        #expect(replacement.worldAction == .replace)
        replacement.worldName = "Replacement Realm"
        replacement.requestCommit()
        #expect(replacement.awaitingConfirmation)
        replacement.commitConfirmed()
        try await waitForRestoreCommit(replacement)
        #expect(replacement.errorMessage == nil)
        #expect(try WorldMetadata.read(Data(contentsOf: sourceWorld.appendingPathComponent("level.dat"))).name == "Replacement Realm")
        #expect(!FileManager.default.fileExists(atPath: sourceWorld.appendingPathComponent("replace-only.txt").path))
        #expect(try Data(contentsOf: sourceWorld.appendingPathComponent("region.dat")) == Data("archived region".utf8))
        #expect(content.worldRevisions[instance.id] == oldRevision + 2)
        #expect(content.installations.store.contentBusy.isEmpty)
    }

    @Test func worldRestoreConfirmationCoversSameUUIDConfigurationChangesAndOtherInstances() async throws {
        let (container, root, source, content) = try fixture()
        defer { withExtendedLifetime(container) { try? FileManager.default.removeItem(at: root) } }
        let exactArchive = try await backupArchive(in: root, instance: source)
        let exactCoordinator = BackupRestoreCoordinator(installations: content.installations, content: content)
        exactCoordinator.enqueue([exactArchive])
        try await waitForRestoreStage(exactCoordinator)
        exactCoordinator.selectWorldTarget(source.id)
        source.versionID = "1.20.4"
        #expect(exactCoordinator.needsConfirmation)
        #expect(exactCoordinator.confirmationMessage.contains("1.21.1") && exactCoordinator.confirmationMessage.contains("1.20.4"))
        exactCoordinator.cancelOrDismiss()
        try FileManager.default.removeItem(at: exactArchive)
        source.versionID = "1.21.1"

        let worldRoot = root.appendingPathComponent("\(source.folderName)/minecraft/saves/World", isDirectory: true)
        let metadata = try WorldMetadata.read(Data(contentsOf: worldRoot.appendingPathComponent("level.dat")))
        let unknownSourceID = UUID()
        let sourceInfo = WorldBackupManifest.SourceInstance(id: unknownSourceID, name: source.name, folderName: source.folderName,
                                                            minecraftVersion: source.versionID, modLoader: source.modLoaderRaw,
                                                            loaderVersion: try source.fabricConfiguration()?.loaderVersion)
        let manifest = WorldBackupManifest(backupCreatedAt: Date(), name: try #require(metadata.name), folderName: "World",
                                           saveVersion: metadata.version, gameType: metadata.gameType, lastPlayed: metadata.lastPlayed,
                                           size: nil, iconPNG: nil, sourceInstance: sourceInfo, datapacks: [])
        let archive = try await WorldBackup.archive(world: "World", in: root.appendingPathComponent(source.folderName), manifest: manifest,
                                                    into: root.appendingPathComponent(InstanceStorage.worldsFolder))
        let coordinator = BackupRestoreCoordinator(installations: content.installations, content: content)
        coordinator.enqueue([archive])
        try await waitForRestoreStage(coordinator)

        #expect(coordinator.worldTargetID == source.id)
        coordinator.selectWorldTarget(source.id)
        source.versionID = "1.20.4"
        #expect(coordinator.needsConfirmation)
        #expect(coordinator.confirmationMessage.contains("Мир будет установлен в другую сборку."))
        #expect(coordinator.confirmationMessage.contains("1.21.1") && coordinator.confirmationMessage.contains("1.20.4"))

        let other = GameInstance(name: "Other", folderName: "Other", versionID: "1.21.1",
                                 metadataURL: source.metadataURL, metadataSHA1: source.metadataSHA1)
        other.state = .ready
        container.mainContext.insert(other)
        coordinator.selectWorldTarget(other.id)
        #expect(coordinator.needsConfirmation)
        #expect(coordinator.confirmationMessage.contains("Мир будет установлен в другую сборку."))
        #expect(!coordinator.confirmationMessage.contains("Версия Minecraft отличается"))
        coordinator.cancelOrDismiss()
    }

    @Test func failedWorldReplacementRestoresPreviousFolderAndKeepsStageForDismissal() async throws {
        let (container, root, instance, content) = try fixture()
        defer { withExtendedLifetime(container) { try? FileManager.default.removeItem(at: root) } }
        let world = root.appendingPathComponent("Source/minecraft/saves/World", isDirectory: true)
        let archive = try await backupArchive(in: root, instance: instance)
        try Data("old-only".utf8).write(to: world.appendingPathComponent("old-only.txt"))
        var moveCount = 0
        let coordinator = BackupRestoreCoordinator(installations: content.installations, content: content, worldMove: { source, destination in
            moveCount += 1
            if moveCount == 2 { throw InstanceFileError.message("Injected world swap failure") }
            try FileManager.default.moveItem(at: source, to: destination)
        })
        coordinator.enqueue([archive])
        try await waitForRestoreStage(coordinator)
        coordinator.selectWorldTarget(instance.id)
        #expect(coordinator.worldAction == .replace)
        coordinator.requestCommit()
        #expect(coordinator.awaitingConfirmation)
        coordinator.commitConfirmed()
        try await waitForRestoreCommit(coordinator)

        #expect(coordinator.errorMessage != nil && coordinator.staged != nil)
        #expect(FileManager.default.fileExists(atPath: world.appendingPathComponent("old-only.txt").path))
        #expect(!FileManager.default.fileExists(atPath: world.appendingPathComponent("region.dat").path))
        #expect(content.installations.store.contentBusy.isEmpty)
        #expect(moveCount == 3)
        coordinator.cancelOrDismiss()
        #expect(coordinator.staged == nil && coordinator.currentURL == nil)
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
        let zip = root.appendingPathComponent("pack.zip"), original = try datapackArchive(in: root, description: "first")
        try original.write(to: zip)
        try await controller.installations.content.importDatapack(from: zip, world: "World", in: source)
        await controller.reload(instance, target: .worldDatapacks("World"))
        let item = try #require(controller.datapacks[instance.id]?["World"]?.first { $0.name == "pack.zip" })
        controller.setDatapackEnabled(item, world: "World", in: instance, enabled: false)
        try await wait(controller, instance)
        let replacement = try datapackArchive(in: root, description: "replacement")
        try replacement.write(to: zip)
        await #expect(throws: PackImportError.self) { try await controller.installations.content.importDatapack(from: zip, world: "World", in: source) }
        try await controller.installations.content.importDatapack(from: zip, world: "World", in: source, replace: true)
        #expect(try Data(contentsOf: world.appendingPathComponent(".hako-disabled-datapacks/pack.zip")) == replacement)
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

    @Test func localDatapackFolderRequiresRootMetadataAndCopiesItsContents() async throws {
        let (container, root, instance, controller) = try fixture()
        defer { withExtendedLifetime(container) { try? FileManager.default.removeItem(at: root) } }
        let source = root.appendingPathComponent("Source"), world = source.appendingPathComponent("minecraft/saves/World")
        let folder = root.appendingPathComponent("LocalPack")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try JSONSerialization.data(withJSONObject: ["pack": ["pack_format": 48, "description": "local"]]).write(to: folder.appendingPathComponent("pack.mcmeta"))
        try Data("data".utf8).write(to: folder.appendingPathComponent("data.json"))
        try await controller.installations.content.importDatapack(from: folder, world: "World", in: source)
        await controller.reload(instance, target: .worldDatapacks("World"))
        let imported = try #require(controller.datapacks[instance.id]?["World"]?.first { $0.name == "LocalPack" })
        #expect(imported.isDirectory && FileManager.default.fileExists(atPath: imported.url.appendingPathComponent("data.json").path))

        let invalid = root.appendingPathComponent("InvalidPack")
        try FileManager.default.createDirectory(at: invalid, withIntermediateDirectories: true)
        await #expect(throws: InstanceFileError.self) {
            try await controller.installations.content.importDatapack(from: invalid, world: "World", in: source)
        }
        #expect(FileManager.default.fileExists(atPath: world.appendingPathComponent("datapacks/LocalPack/data.json").path))
        #expect(!FileManager.default.fileExists(atPath: world.appendingPathComponent("datapacks/InvalidPack").path))
    }

    @Test func datapackReplacementDoesNotRecreateWorldRemovedBeforePublication() async throws {
        let (container, root, _, controller) = try fixture()
        defer { withExtendedLifetime(container) { try? FileManager.default.removeItem(at: root) } }
        let source = root.appendingPathComponent("Source"), world = source.appendingPathComponent("minecraft/saves/World")
        let replacement = root.appendingPathComponent("replacement.zip")
        try datapackArchive(in: root, description: "replacement").write(to: replacement)
        let item = InstanceContentItem(url: world.appendingPathComponent("datapacks/pack.zip"), isDirectory: false)
        try FileManager.default.removeItem(at: world)
        await #expect(throws: InstanceFileError.self) {
            try await controller.installations.content.replaceDatapack(item, from: replacement, filename: "pack.zip", world: "World", in: source, expectedSHA512: String(repeating: "0", count: 128), origin: nil)
        }
        #expect(!FileManager.default.fileExists(atPath: world.path))
    }
}

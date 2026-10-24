import Foundation
import CryptoKit
import SwiftData
import Testing
import Darwin
import zlib

@MainActor struct InstanceManagementTests {
    private struct ZIPFixtureEntry {
        let path: String
        let data: Data
        var mode: UInt32 = 0o100644
        var flags: UInt16 = 0
        var method: UInt16 = 0
        var wrongCRC = false
        var rawName: Data?
        var extra = Data()
        var compressedData: Data?
    }

    private struct RawDeflateBitWriter {
        var data = Data()
        private var byte: UInt8 = 0
        private var bitCount = 0

        mutating func bits(_ value: UInt32, count: Int) {
            for index in 0..<count {
                byte |= UInt8((value >> index) & 1) << bitCount
                bitCount += 1
                if bitCount == 8 { data.append(byte); byte = 0; bitCount = 0 }
            }
        }

        mutating func huffman(_ code: UInt32, count: Int) {
            var reversed: UInt32 = 0
            for index in 0..<count { reversed = (reversed << 1) | ((code >> index) & 1) }
            bits(reversed, count: count)
        }

        mutating func storedBlock(_ contents: Data, final: Bool) {
            precondition(contents.count <= UInt16.max)
            bits(final ? 1 : 0, count: 1)
            bits(0, count: 2)
            while bitCount != 0 { bits(0, count: 1) }
            let length = UInt16(contents.count)
            bits(UInt32(length), count: 16)
            bits(UInt32(length ^ UInt16.max), count: 16)
            data.append(contents)
        }
    }

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

    private func sha1(_ data: Data) -> String {
        Insecure.SHA1.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    private func crc32(_ data: Data) -> UInt32 {
        var value = UInt32.max
        for byte in data {
            value ^= UInt32(byte)
            for _ in 0..<8 { value = (value >> 1) ^ (0xedb8_8320 & (0 &- (value & 1))) }
        }
        return ~value
    }

    private func append(_ value: UInt16, to data: inout Data) {
        data.append(UInt8(value & 0xff)); data.append(UInt8(value >> 8))
    }

    private func append(_ value: UInt32, to data: inout Data) {
        append(UInt16(value & 0xffff), to: &data); append(UInt16(value >> 16), to: &data)
    }

    private func append(_ value: UInt64, to data: inout Data) {
        append(UInt32(value & 0xffff_ffff), to: &data); append(UInt32(value >> 32), to: &data)
    }

    private func storedZIP(_ entries: [ZIPFixtureEntry]) -> Data {
        var archive = Data()
        var central = Data()
        for entry in entries {
            let name = entry.rawName ?? Data(entry.path.utf8)
            let payload = entry.method == 8 ? (entry.compressedData ?? (try! rawDeflate(entry.data))) : entry.data
            let checksum = crc32(entry.data) ^ (entry.wrongCRC ? 1 : 0)
            let offset = UInt32(archive.count)
            append(UInt32(0x0403_4b50), to: &archive)
            append(UInt16(20), to: &archive); append(entry.flags, to: &archive); append(entry.method, to: &archive)
            append(UInt16(0), to: &archive); append(UInt16(0), to: &archive)
            append(checksum, to: &archive); append(UInt32(payload.count), to: &archive); append(UInt32(entry.data.count), to: &archive)
            append(UInt16(name.count), to: &archive); append(UInt16(entry.extra.count), to: &archive)
            archive.append(name); archive.append(entry.extra); archive.append(payload)

            append(UInt32(0x0201_4b50), to: &central)
            append(UInt16(0x0314), to: &central); append(UInt16(20), to: &central)
            append(entry.flags, to: &central); append(entry.method, to: &central)
            append(UInt16(0), to: &central); append(UInt16(0), to: &central)
            append(checksum, to: &central); append(UInt32(payload.count), to: &central); append(UInt32(entry.data.count), to: &central)
            append(UInt16(name.count), to: &central); append(UInt16(entry.extra.count), to: &central); append(UInt16(0), to: &central)
            append(UInt16(0), to: &central); append(UInt16(0), to: &central)
            append(entry.mode << 16, to: &central); append(offset, to: &central)
            central.append(name); central.append(entry.extra)
        }
        let centralOffset = UInt32(archive.count)
        archive.append(central)
        append(UInt32(0x0605_4b50), to: &archive)
        append(UInt16(0), to: &archive); append(UInt16(0), to: &archive)
        append(UInt16(entries.count), to: &archive); append(UInt16(entries.count), to: &archive)
        append(UInt32(central.count), to: &archive); append(centralOffset, to: &archive); append(UInt16(0), to: &archive)
        return archive
    }

    private func rawDeflate(_ data: Data) throws -> Data {
        var stream = z_stream()
        guard deflateInit2_(&stream, Z_BEST_SPEED, Z_DEFLATED, -MAX_WBITS, 8, Z_DEFAULT_STRATEGY,
                            ZLIB_VERSION, Int32(MemoryLayout<z_stream>.size)) == Z_OK else {
            throw InstanceFileError.message("Fixture deflate initialization failed")
        }
        defer { deflateEnd(&stream) }
        var compressed = Data()
        try data.withUnsafeBytes { input in
            stream.next_in = UnsafeMutablePointer(mutating: input.bindMemory(to: Bytef.self).baseAddress)
            stream.avail_in = uInt(input.count)
            var buffer = [UInt8](repeating: 0, count: 64 * 1024)
            while true {
                let status = buffer.withUnsafeMutableBytes { output -> Int32 in
                    stream.next_out = output.bindMemory(to: Bytef.self).baseAddress
                    stream.avail_out = uInt(output.count)
                    return deflate(&stream, Z_FINISH)
                }
                compressed.append(contentsOf: buffer.prefix(buffer.count - Int(stream.avail_out)))
                if status == Z_STREAM_END { break }
                guard status == Z_OK else { throw InstanceFileError.message("Fixture deflate failed") }
            }
        }
        return compressed
    }

    private func boundaryDeflate(_ data: Data) -> Data {
        precondition(data.count > 65_520)
        var writer = RawDeflateBitWriter()
        writer.bits(0, count: 1) // non-final fixed-Huffman block
        writer.bits(1, count: 2)
        for _ in 0..<3 { writer.huffman(0x30 + 65, count: 8) }
        writer.huffman(266, count: 7) // length 13
        writer.bits(0, count: 1)
        writer.bits(0, count: 5) // distance 1
        writer.huffman(0, count: 7) // end of fixed block
        writer.storedBlock(Data(), final: false)
        writer.storedBlock(data.prefix(65_520), final: false)
        writer.storedBlock(data.dropFirst(65_520), final: true)
        return writer.data
    }

    private func consumesExactChunkBoundary(_ compressed: Data) -> Bool {
        let chunkSize = 64 * 1024
        guard compressed.count > chunkSize else { return false }
        var stream = z_stream()
        guard inflateInit2_(&stream, -MAX_WBITS, ZLIB_VERSION, Int32(MemoryLayout<z_stream>.size)) == Z_OK else { return false }
        defer { inflateEnd(&stream) }
        var input = Array(compressed.prefix(chunkSize))
        var output = [UInt8](repeating: 0, count: chunkSize)
        let status = input.withUnsafeMutableBufferPointer { inputBuffer in
            output.withUnsafeMutableBufferPointer { outputBuffer in
                stream.next_in = inputBuffer.baseAddress
                stream.avail_in = uInt(inputBuffer.count)
                stream.next_out = outputBuffer.baseAddress
                stream.avail_out = uInt(outputBuffer.count)
                return inflate(&stream, Z_NO_FLUSH)
            }
        }
        return status == Z_OK && stream.avail_in == 0 && stream.avail_out == 0
    }

    private func unicodePathExtra(rawName: Data, path: String) -> Data {
        var extra = Data()
        let pathData = Data(path.utf8)
        append(UInt16(0x7075), to: &extra)
        append(UInt16(pathData.count + 5), to: &extra)
        extra.append(1)
        append(crc32(rawName), to: &extra)
        extra.append(pathData)
        return extra
    }

    private func storedZIP64World(_ manifest: WorldBackupManifest, worldData: Data) throws -> Data {
        let inputs: [ZIPFixtureEntry] = [
            .init(path: "data.json", data: try WorldBackupManifest.encoder().encode(manifest)),
            .init(path: "world/", data: Data(), mode: 0o040755),
            .init(path: "world/level.dat", data: worldData)
        ]
        var archive = Data()
        var central = Data()
        for entry in inputs {
            let name = Data(entry.path.utf8), checksum = crc32(entry.data), offset = UInt64(archive.count)
            append(UInt32(0x0403_4b50), to: &archive)
            append(UInt16(45), to: &archive); append(entry.flags, to: &archive); append(entry.method, to: &archive)
            append(UInt16(0), to: &archive); append(UInt16(0), to: &archive)
            append(checksum, to: &archive); append(UInt32.max, to: &archive); append(UInt32.max, to: &archive)
            append(UInt16(name.count), to: &archive); append(UInt16(20), to: &archive)
            archive.append(name)
            append(UInt16(1), to: &archive); append(UInt16(16), to: &archive)
            append(UInt64(entry.data.count), to: &archive); append(UInt64(entry.data.count), to: &archive)
            archive.append(entry.data)

            append(UInt32(0x0201_4b50), to: &central)
            append(UInt16(0x032d), to: &central); append(UInt16(45), to: &central)
            append(entry.flags, to: &central); append(entry.method, to: &central)
            append(UInt16(0), to: &central); append(UInt16(0), to: &central)
            append(checksum, to: &central); append(UInt32.max, to: &central); append(UInt32.max, to: &central)
            append(UInt16(name.count), to: &central); append(UInt16(28), to: &central); append(UInt16(0), to: &central)
            append(UInt16(0), to: &central); append(UInt16(0), to: &central)
            append(entry.mode << 16, to: &central); append(UInt32(offset), to: &central)
            central.append(name)
            append(UInt16(1), to: &central); append(UInt16(24), to: &central)
            append(UInt64(entry.data.count), to: &central); append(UInt64(entry.data.count), to: &central); append(offset, to: &central)
        }
        let centralOffset = UInt64(archive.count), centralSize = UInt64(central.count)
        archive.append(central)
        let zip64Offset = UInt64(archive.count)
        append(UInt32(0x0606_4b50), to: &archive); append(UInt64(44), to: &archive)
        append(UInt16(45), to: &archive); append(UInt16(45), to: &archive)
        append(UInt32(0), to: &archive); append(UInt32(0), to: &archive)
        append(UInt64(inputs.count), to: &archive); append(UInt64(inputs.count), to: &archive)
        append(centralSize, to: &archive); append(centralOffset, to: &archive)
        append(UInt32(0x0706_4b50), to: &archive); append(UInt32(0), to: &archive)
        append(zip64Offset, to: &archive); append(UInt32(1), to: &archive)
        append(UInt32(0x0605_4b50), to: &archive)
        append(UInt16(0), to: &archive); append(UInt16(0), to: &archive)
        append(UInt16.max, to: &archive); append(UInt16.max, to: &archive)
        append(UInt32.max, to: &archive); append(UInt32.max, to: &archive); append(UInt16(0), to: &archive)
        return archive
    }

    private func validBackupFixture() async throws -> (ModelContainer, URL, InstanceStore, GameInstance, URL, InstanceBackupManifest) {
        let root = try temporary()
        let container = try container()
        let store = InstanceStore(context: container.mainContext, storage: .init(root: root))
        let versionID = "1.21.1"
        let instance = try store.create(draft("Restore Pack"), versionID: versionID,
                                        metadataURL: "https://example.test/version.json", metadataSHA1: "pending", javaMajorVersion: 21)
        let folder = try store.storage.directory(instance.folderName)
        let javaHome = folder.appendingPathComponent("java/runtime", isDirectory: true)
        let java = javaHome.appendingPathComponent("bin/java")
        try FileManager.default.createDirectory(at: java.deletingLastPathComponent(), withIntermediateDirectories: true)
        var cpu: UInt32 = 0
        #if arch(arm64)
        cpu = UInt32(bitPattern: CPU_TYPE_ARM64)
        #else
        cpu = UInt32(bitPattern: CPU_TYPE_X86_64)
        #endif
        var executable = Data([0xcf, 0xfa, 0xed, 0xfe])
        for shift in stride(from: 0, to: 32, by: 8) { executable.append(UInt8((cpu >> shift) & 0xff)) }
        executable.append(contentsOf: repeatElement(UInt8(0), count: 128))
        try executable.write(to: java)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: java.path)
        try Data("JAVA_VERSION=\"21.0.1\"\n".utf8).write(to: javaHome.appendingPathComponent("release"))

        let client = Data("verified Minecraft client fixture".utf8)
        let metadata = try JSONSerialization.data(withJSONObject: [
            "id": versionID,
            "downloads": ["client": ["url": "https://example.test/client.jar", "sha1": sha1(client), "size": client.count]],
            "libraries": []
        ], options: [.sortedKeys])
        let versionFolder = folder.appendingPathComponent("minecraft/versions/\(versionID)", isDirectory: true)
        try FileManager.default.createDirectory(at: versionFolder, withIntermediateDirectories: true)
        try metadata.write(to: versionFolder.appendingPathComponent("\(versionID).json"))
        try client.write(to: versionFolder.appendingPathComponent("\(versionID).jar"))
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("minecraft/mods"), withIntermediateDirectories: true)
        instance.metadataSHA1 = sha1(metadata)
        instance.javaExecutable = "runtime/bin/java"
        instance.state = .ready
        try container.mainContext.save()

        let archive = try await store.backup(instance, content: InstanceContent())
        let manifestData = try await FabricClient.archiveEntry(InstanceBackupManifest.fileName, in: archive, limit: 1_048_576)
        let manifest = try InstanceBackupManifest.decoder().decode(InstanceBackupManifest.self, from: manifestData)
        return (container, root, store, instance, archive, manifest)
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
        let apiData = Data("fabric api fixture".utf8)
        let restoredAPI = FabricAPIDescriptor(projectID: FabricAPIDescriptor.project, versionID: "api", version: "0.100.0", channel: "release", filename: "fabric-api.jar", url: URL(string: "https://example.test/fabric-api.jar")!, size: Int64(apiData.count), sha1: sha1(apiData), sha512: FabricClient.hash(apiData))
        source.fabricConfiguration = FabricConfiguration(loaderVersion: "0.16.5", api: restoredAPI)
        let instance = try store.create(source, versionID: "1.21.1", metadataURL: "https://example.test/v.json", metadataSHA1: "hash", javaMajorVersion: 21)
        instance.state = .ready
        try container.mainContext.save()
        let folder = root.appendingPathComponent("Backup_Pack")
        let javaFolder = folder.appendingPathComponent("java/runtime", isDirectory: true)
        let java = javaFolder.appendingPathComponent("bin/java")
        try FileManager.default.createDirectory(at: java.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: javaFolder.appendingPathComponent("alias"), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: javaFolder.appendingPathComponent("lib"), withIntermediateDirectories: true)
        var cpu: UInt32 = 0
        #if arch(arm64)
        cpu = UInt32(bitPattern: CPU_TYPE_ARM64)
        #else
        cpu = UInt32(bitPattern: CPU_TYPE_X86_64)
        #endif
        var javaBytes = Data([0xcf, 0xfa, 0xed, 0xfe])
        for shift in stride(from: 0, to: 32, by: 8) { javaBytes.append(UInt8((cpu >> shift) & 0xff)) }
        javaBytes.append(contentsOf: repeatElement(UInt8(0), count: 128))
        try javaBytes.write(to: java)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: java.path)
        try FileManager.default.createSymbolicLink(atPath: javaFolder.appendingPathComponent("alias/java").path, withDestinationPath: "../bin/java")
        try Data("JAVA_VERSION=\"21.0.1\"\n".utf8).write(to: javaFolder.appendingPathComponent("release"))
        let helper = javaFolder.appendingPathComponent("lib/jspawnhelper")
        try Data("helper".utf8).write(to: helper)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: helper.path)
        let clientJar = Data("fixture client jar".utf8)
        let version = try JSONSerialization.data(withJSONObject: [
            "id": "1.21.1",
            "downloads": ["client": ["url": "https://example.test/client.jar", "sha1": sha1(clientJar), "size": clientJar.count]],
            "libraries": []
        ], options: [.sortedKeys])
        let versionFolder = folder.appendingPathComponent("minecraft/versions/1.21.1", isDirectory: true)
        try FileManager.default.createDirectory(at: versionFolder, withIntermediateDirectories: true)
        try version.write(to: versionFolder.appendingPathComponent("1.21.1.json"))
        try clientJar.write(to: versionFolder.appendingPathComponent("1.21.1.jar"))
        let fabricProfile = try JSONSerialization.data(withJSONObject: [
            "id": "fabric-loader-0.16.5-1.21.1",
            "inheritsFrom": "1.21.1",
            "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
            "libraries": []
        ], options: [.sortedKeys])
        try fabricProfile.write(to: folder.appendingPathComponent("minecraft/.hako-fabric.json"))
        instance.metadataSHA1 = sha1(version)
        instance.javaExecutable = "runtime/alias/java"
        instance.fabricProfileSHA1 = sha1(fabricProfile)
        try FileManager.default.createSymbolicLink(atPath: folder.appendingPathComponent("java/link").path, withDestinationPath: "runtime/bin/java")
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("minecraft/mods"), withIntermediateDirectories: true)
        try apiData.write(to: folder.appendingPathComponent("minecraft/mods/fabric-api.jar"))
        try Data("one".utf8).write(to: folder.appendingPathComponent("minecraft/mods/one.jar"))
        try Data("two".utf8).write(to: folder.appendingPathComponent("minecraft/mods/two.jar.disabled"))
        try Data("{}".utf8).write(to: folder.appendingPathComponent("minecraft/.hako-running.json"))
        try Data("stale source manifest".utf8).write(to: folder.appendingPathComponent("data.json"))
        try container.mainContext.save()

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
        let entryNames = String(decoding: output.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self).split(separator: "\n").map(String.init)
        let entries = Set(entryNames)
        listing.waitUntilExit()
        #expect(entries.isSuperset(of: ["data.json", "icon.png", "java/runtime/bin/java", "java/runtime/alias/java", "java/link", "minecraft/mods/fabric-api.jar", "minecraft/mods/one.jar", "minecraft/mods/two.jar.disabled"]))
        #expect(entryNames.filter { $0 == "data.json" }.count == 1)
        #expect(!entries.contains("minecraft/.hako-running.json"))

        let data = try await FabricClient.archiveEntry("data.json", in: archive, limit: 1_048_576)
        let manifest = try InstanceBackupManifest.decoder().decode(InstanceBackupManifest.self, from: data)
        #expect(manifest.formatVersion == 1 && manifest.name == "Backup Pack" && manifest.folderName == "Backup_Pack")
        #expect(manifest.minecraftVersion == "1.21.1" && manifest.modLoader == "fabric" && manifest.loaderVersion == "0.16.5" && manifest.javaMajorVersion == 21)
        #expect(manifest.iconPNG == Data("png".utf8) && manifest.iconSymbol.isEmpty)
        #expect(manifest.modCount == 3 && manifest.mods.map(\.file) == ["fabric-api.jar", "one.jar", "two.jar"] && manifest.mods.map(\.enabled) == [true, true, false])
        #expect(manifest.instanceID == instance.id)
        #expect(manifest.profile.metadataURL == instance.metadataURL && manifest.profile.fabricConfiguration?.api == restoredAPI)
        #expect(String(decoding: data, as: UTF8.self).contains("\"iconPNG\" : \"\(Data("png".utf8).base64EncodedString())\""))

        let staged = try await RestoreArchive.prepare(archive, under: root)
        defer { RestoreArchive.discardStage(staged.directory) }
        var stagingInfo = stat()
        #expect(lstat(staged.directory.path, &stagingInfo) == 0 && stagingInfo.st_mode & 0o777 == 0o700)
        #expect(FileManager.default.fileExists(atPath: staged.directory.appendingPathComponent("minecraft/versions/1.21.1/1.21.1.jar").path))
        #expect(!FileManager.default.fileExists(atPath: staged.directory.appendingPathComponent("data.json").path))
        let executableURL = staged.directory.appendingPathComponent("java/runtime/bin/java")
        var executableInfo = stat()
        #expect(lstat(executableURL.path, &executableInfo) == 0 && executableInfo.st_mode & 0o777 == 0o755)
        let helperURL = staged.directory.appendingPathComponent("java/runtime/lib/jspawnhelper")
        var helperInfo = stat()
        #expect(lstat(helperURL.path, &helperInfo) == 0 && helperInfo.st_mode & 0o777 == 0o755)
        #expect((try staged.directory.appendingPathComponent("java/runtime/alias/java").resourceValues(forKeys: [.isSymbolicLinkKey])).isSymbolicLink == true)
        await #expect(throws: InstanceFileError.self) {
            _ = try await RestoreArchive.prepare(archive, under: root, availableBytes: { _ in 0 })
        }

        let createdAt = instance.createdAt
        let playtime = InstancePlaytime(ownerKey: "guest", instanceID: instance.id)
        playtime.totalSeconds = 731
        container.mainContext.insert(playtime)
        try container.mainContext.save()
        let newStage = try await RestoreArchive.prepare(archive, under: root)
        let copy = try await store.restoreInstance(manifest, stagedDirectory: newStage.directory, name: "Restored Pack", replacing: nil).instance
        #expect(copy.id != manifest.instanceID && copy.id != instance.id)
        #expect(copy.name == "Restored Pack" && copy.folderName == "Restored_Pack")
        var restoredRootInfo = stat()
        #expect(lstat(root.appendingPathComponent("Restored_Pack").path, &restoredRootInfo) == 0 && restoredRootInfo.st_mode & 0o777 == 0o755)
        #expect(abs(copy.createdAt.timeIntervalSince(manifest.instanceCreatedAt)) < 1)
        #expect(FileManager.default.fileExists(atPath: root.appendingPathComponent("Restored_Pack/minecraft/versions/1.21.1/1.21.1.jar").path))

        try store.rename(instance, to: "Current Pack")
        let currentFolder = root.appendingPathComponent("Current_Pack", isDirectory: true)
        try Data("keep only until replacement".utf8).write(to: currentFolder.appendingPathComponent("current-only.txt"))
        instance.metadataURL = "https://example.test/changed.json"
        instance.javaExecutable = "missing/java"
        instance.offlineMode = true
        instance.state = .paused
        try container.mainContext.save()
        let replacementStage = try await RestoreArchive.prepare(archive, under: root)
        let replaced = try await store.restoreInstance(manifest, stagedDirectory: replacementStage.directory, name: "Ignored", replacing: instance)
        #expect(replaced.instance.id == instance.id && replaced.instance.name == "Current Pack" && replaced.instance.folderName == "Current_Pack")
        #expect(replaced.instance.createdAt == createdAt && replaced.instance.state == .ready)
        #expect(replaced.instance.metadataURL == manifest.profile.metadataURL && replaced.instance.javaExecutable == "runtime/alias/java")
        #expect(replaced.instance.modLoader == .fabric && !replaced.instance.offlineMode)
        #expect(!FileManager.default.fileExists(atPath: currentFolder.appendingPathComponent("current-only.txt").path))
        #expect(try Data(contentsOf: currentFolder.appendingPathComponent("minecraft/mods/fabric-api.jar")) == apiData)
        #expect(try container.mainContext.fetch(FetchDescriptor<InstancePlaytime>()).first?.totalSeconds == 731)
        #expect(try FileManager.default.contentsOfDirectory(at: root.appendingPathComponent("backups"), includingPropertiesForKeys: nil).filter { $0.pathExtension == "hakobackup" }.count == 1)
    }

    @Test func restoreMatchingUsesUUIDThenOnlyAnUnambiguousLegacySignature() async throws {
        let (container, root, store, instance, _, manifest) = try await validBackupFixture()
        defer { withExtendedLifetime(container) { try? FileManager.default.removeItem(at: root) } }
        var unknownID = manifest
        unknownID.instanceID = UUID()
        #expect(BackupRestoreCoordinator.matchingInstance(for: unknownID, among: [instance]) == nil)

        var legacy = manifest
        legacy.instanceID = nil
        var legacyJSON = try #require(JSONSerialization.jsonObject(with: InstanceBackupManifest.encoder().encode(legacy)) as? [String: Any])
        legacyJSON["instanceID"] = nil
        let decodedLegacy = try InstanceBackupManifest.decoder().decode(InstanceBackupManifest.self, from: JSONSerialization.data(withJSONObject: legacyJSON))
        #expect(decodedLegacy.instanceID == nil)
        #expect(BackupRestoreCoordinator.matchingInstance(for: legacy, among: [instance])?.id == instance.id)
        let duplicate = GameInstance(name: "Duplicate", folderName: instance.folderName, versionID: instance.versionID,
                                     metadataURL: instance.metadataURL, metadataSHA1: instance.metadataSHA1)
        #expect(BackupRestoreCoordinator.matchingInstance(for: legacy, among: [instance, duplicate]) == nil)
        try store.rename(instance, to: "Renamed Restore")
        #expect(BackupRestoreCoordinator.matchingInstance(for: manifest, among: [instance])?.id == manifest.instanceID)
        #expect(BackupRestoreCoordinator.matchingInstance(for: legacy, among: [instance]) == nil)
    }

    @Test func restoreArchivePreservesCompletedModpackMetadataAndUnicodeFiles() async throws {
        let (container, root, store, instance, oldArchive, _) = try await validBackupFixture()
        defer { withExtendedLifetime(container) { try? FileManager.default.removeItem(at: root) } }
        try FileManager.default.removeItem(at: oldArchive)
        let source = try store.storage.directory(instance.folderName)
        let nfdPack = "Cafe\u{301} 🌸"
        let nfdFile = "Текст é e\u{301}.txt"
        let rootFile = source.appendingPathComponent("Пользовательские данные/\(nfdFile)")
        let resourcePackFile = source.appendingPathComponent("minecraft/resourcepacks/\(nfdPack)/assets/demo/\(nfdFile)")
        try FileManager.default.createDirectory(at: rootFile.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: resourcePackFile.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("root file".utf8).write(to: rootFile)
        try Data("resource pack file".utf8).write(to: resourcePackFile)

        let modpackArchive = try await ModpackTests.archive(in: root, manifest: ModpackTests.manifest())
        var plan = try ModpackTests.plan(archive: modpackArchive, manifest: ModpackTests.manifest())
        plan.completed = true
        try plan.write(in: source)
        let archive = try await store.backup(instance, content: InstanceContent())
        let staged = try await RestoreArchive.prepare(archive, under: root)
        defer { RestoreArchive.discardStage(staged.directory) }
        #expect(try ModpackPlan.read(in: staged.directory)?.completed == true)
        #expect(try Data(contentsOf: staged.directory.appendingPathComponent("Пользовательские данные/Текст é é.txt")) == Data("root file".utf8))
        #expect(try Data(contentsOf: staged.directory.appendingPathComponent("minecraft/resourcepacks/Café 🌸/assets/demo/Текст é é.txt")) == Data("resource pack file".utf8))
    }

    @Test func restoreStaticallyRejectsForeignJavaAndNeverExecutesArchivePrograms() async throws {
        let (container, root, store, instance, originalArchive, _) = try await validBackupFixture()
        defer { withExtendedLifetime(container) { try? FileManager.default.removeItem(at: root) } }
        let java = root.appendingPathComponent("\(instance.folderName)/java/runtime/bin/java")
        let release = root.appendingPathComponent("\(instance.folderName)/java/runtime/release")
        var archiveToRemove = originalArchive
        func repack() async throws -> URL {
            try? FileManager.default.removeItem(at: archiveToRemove)
            archiveToRemove = try await store.backup(instance, content: InstanceContent())
            return archiveToRemove
        }

        let sentinel = root.appendingPathComponent("archive-java-was-run")
        let script = "#!/bin/sh\n/usr/bin/touch \"\(sentinel.path)\"\n"
        try Data(script.utf8).write(to: java)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: java.path)
        let scriptedArchive = try await repack()
        await #expect(throws: InstanceFileError.self) { _ = try await RestoreArchive.prepare(scriptedArchive, under: root) }
        #expect(!FileManager.default.fileExists(atPath: sentinel.path))

        #if arch(arm64)
        let foreignCPU = UInt32(bitPattern: CPU_TYPE_X86_64)
        #else
        let foreignCPU = UInt32(bitPattern: CPU_TYPE_ARM64)
        #endif
        var foreignMachO = Data([0xcf, 0xfa, 0xed, 0xfe])
        for shift in stride(from: 0, to: 32, by: 8) { foreignMachO.append(UInt8((foreignCPU >> shift) & 0xff)) }
        foreignMachO.append(contentsOf: repeatElement(UInt8(0), count: 128))
        try foreignMachO.write(to: java)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: java.path)
        let foreignArchive = try await repack()
        await #expect(throws: InstanceFileError.self) { _ = try await RestoreArchive.prepare(foreignArchive, under: root) }

        #if arch(arm64)
        let nativeCPU = UInt32(bitPattern: CPU_TYPE_ARM64)
        #else
        let nativeCPU = UInt32(bitPattern: CPU_TYPE_X86_64)
        #endif
        var nativeMachO = Data([0xcf, 0xfa, 0xed, 0xfe])
        for shift in stride(from: 0, to: 32, by: 8) { nativeMachO.append(UInt8((nativeCPU >> shift) & 0xff)) }
        nativeMachO.append(contentsOf: repeatElement(UInt8(0), count: 128))
        try nativeMachO.write(to: java)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: java.path)
        try Data("JAVA_VERSION=\"8.0.0\"\n".utf8).write(to: release)
        let oldJavaArchive = try await repack()
        await #expect(throws: InstanceFileError.self) { _ = try await RestoreArchive.prepare(oldJavaArchive, under: root) }
        #expect(!FileManager.default.fileExists(atPath: sentinel.path))
    }

    @Test func restoreQueueDeduplicatesFilesAndCancellationAdvancesThenCleansStaging() async throws {
        let (container, root, _, _, archive, _) = try await validBackupFixture()
        defer { withExtendedLifetime(container) { try? FileManager.default.removeItem(at: root) } }
        let alias = root.appendingPathComponent("alias.hakobackup")
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: archive)
        let second = root.appendingPathComponent("second.hakobackup")
        try FileManager.default.copyItem(at: archive, to: second)
        let installations = InstallationCoordinator(context: container.mainContext, storage: .init(root: root))
        let coordinator = BackupRestoreCoordinator(installations: installations, content: InstanceContentController(installations: installations))

        coordinator.enqueue([archive, alias, second])
        #expect(coordinator.currentURL?.standardizedFileURL == archive.standardizedFileURL && coordinator.queuedCount == 1)
        coordinator.enqueue([alias])
        #expect(coordinator.queuedCount == 1)
        coordinator.cancelOrDismiss()
        #expect(coordinator.currentURL?.standardizedFileURL == second.standardizedFileURL)
        for _ in 0..<500 where coordinator.staged == nil && coordinator.errorMessage == nil { try await Task.sleep(for: .milliseconds(10)) }
        #expect(coordinator.staged?.archive.standardizedFileURL == second.standardizedFileURL)
        #expect(coordinator.instanceName == "Restore Pack 2")
        coordinator.cancelOrDismiss()
        for _ in 0..<100 {
            let stages = (try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil))?.filter { $0.lastPathComponent.hasPrefix(".restore-stage-") } ?? []
            if stages.isEmpty { break }
            try await Task.sleep(for: .milliseconds(10))
        }
        #expect(coordinator.currentURL == nil && coordinator.queuedCount == 0 && coordinator.staged == nil)
        #expect(try FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil).allSatisfy { !$0.lastPathComponent.hasPrefix(".restore-stage-") })
    }

    @Test func restoreFilesystemFailureAndSaveFailureRollBackWithoutMerging() async throws {
        let (container, root, _, instance, archive, manifest) = try await validBackupFixture()
        defer { withExtendedLifetime(container) { try? FileManager.default.removeItem(at: root) } }
        let current = root.appendingPathComponent(instance.folderName, isDirectory: true)
        try Data("old data".utf8).write(to: current.appendingPathComponent("old-only.txt"))
        try Data("old installed client".utf8).write(to: current.appendingPathComponent("minecraft/versions/1.21.1/1.21.1.jar"))
        let originalName = instance.name
        let originalVersion = instance.versionID

        var moves = 0
        let fileFailStore = InstanceStore(context: container.mainContext, storage: .init(root: root), restoreMove: { source, destination in
            moves += 1
            if moves == 2 { throw InstanceFileError.message("Injected rename failure") }
            try FileManager.default.moveItem(at: source, to: destination)
        })
        let fileFailStage = try await RestoreArchive.prepare(archive, under: root)
        await #expect(throws: InstanceFileError.self) {
            try await fileFailStore.restoreInstance(manifest, stagedDirectory: fileFailStage.directory, name: "Ignored", replacing: instance)
        }
        let ownerMarker = root.appendingPathComponent(fileFailStage.directory.lastPathComponent + ".owner")
        #expect(FileManager.default.fileExists(atPath: ownerMarker.path))
        #expect(try String(contentsOf: ownerMarker, encoding: .utf8) == String(fileFailStage.directory.lastPathComponent.dropFirst(".restore-stage-".count)))
        #expect(FileManager.default.fileExists(atPath: current.appendingPathComponent("old-only.txt").path))
        #expect(try Data(contentsOf: current.appendingPathComponent("minecraft/versions/1.21.1/1.21.1.jar")) == Data("old installed client".utf8))
        #expect(instance.name == originalName && instance.versionID == originalVersion)

        let saveFailStore = InstanceStore(context: container.mainContext, storage: .init(root: root), persist: {
            throw InstanceFileError.message("Injected model save failure")
        })
        let saveFailStage = try await RestoreArchive.prepare(archive, under: root)
        await #expect(throws: InstanceFileError.self) {
            try await saveFailStore.restoreInstance(manifest, stagedDirectory: saveFailStage.directory, name: "Ignored", replacing: instance)
        }
        #expect(FileManager.default.fileExists(atPath: current.appendingPathComponent("old-only.txt").path))
        #expect(try Data(contentsOf: current.appendingPathComponent("minecraft/versions/1.21.1/1.21.1.jar")) == Data("old installed client".utf8))
        #expect(instance.name == originalName && instance.versionID == originalVersion)
        #expect(saveFailStore.contentBusy.isEmpty)
    }

    @Test func failedInstanceRollbackRetainsDataAndReportsRecoveryPath() async throws {
        let (container, root, _, instance, archive, manifest) = try await validBackupFixture()
        defer { withExtendedLifetime(container) { try? FileManager.default.removeItem(at: root) } }
        let current = root.appendingPathComponent(instance.folderName, isDirectory: true)
        try Data("only surviving old data".utf8).write(to: current.appendingPathComponent("old-only.txt"))
        var moves = 0
        let store = InstanceStore(context: container.mainContext, storage: .init(root: root), restoreMove: { source, destination in
            moves += 1
            if moves == 2 || moves == 3 { throw InstanceFileError.message("Injected swap/rollback failure") }
            try FileManager.default.moveItem(at: source, to: destination)
        })
        let stage = try await RestoreArchive.prepare(archive, under: root)
        do {
            _ = try await store.restoreInstance(manifest, stagedDirectory: stage.directory, name: "Ignored", replacing: instance)
            Issue.record("Expected an instance rollback failure.")
        } catch let failure as InstanceRestoreCommitFailure {
            #expect(failure.recoveryLocations.count == 1)
            #expect(failure.localizedDescription.contains(failure.recoveryLocations[0].path))
            #expect(try Data(contentsOf: failure.recoveryLocations[0].appendingPathComponent("old-only.txt")) == Data("only surviving old data".utf8))
        }
        #expect(instance.name == manifest.name && instance.folderName == manifest.folderName)
        #expect(moves == 3 && store.contentBusy.isEmpty)
    }

    @Test func restoreZIP64WorldRoundTripAndRejectsMalformedOrUnsafeEntries() async throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let source = WorldBackupManifest.SourceInstance(id: UUID(), name: "Source", folderName: "Source",
                                                        minecraftVersion: "1.21.1", modLoader: "vanilla", loaderVersion: nil)
        let manifest = WorldBackupManifest(backupCreatedAt: Date(), name: "Realm", folderName: "World", saveVersion: "1.21.1",
                                           gameType: 0, lastPlayed: nil, size: nil, iconPNG: nil, sourceInstance: source, datapacks: [])
        let zip64 = root.appendingPathComponent("world.hakoworld")
        try storedZIP64World(manifest, worldData: WorldTestFixture.level(name: "Realm")).write(to: zip64)
        let staged = try await RestoreArchive.prepare(zip64, under: root)
        defer { RestoreArchive.discardStage(staged.directory) }
        guard case .world(let restoredManifest) = staged.kind else { Issue.record("Expected a world archive."); return }
        #expect(restoredManifest.name == "Realm")
        #expect(try WorldMetadata.read(Data(contentsOf: staged.directory.appendingPathComponent("world/level.dat"))).name == "Realm")
        RestoreArchive.discardStage(staged.directory)

        let mismatchedManifest = WorldBackupManifest(backupCreatedAt: Date(), name: "Realm", folderName: "World", saveVersion: "999",
                                                      gameType: 0, lastPlayed: nil, size: nil, iconPNG: nil, sourceInstance: source, datapacks: [])
        let mismatchedArchive = root.appendingPathComponent("mismatched.hakoworld")
        try storedZIP64World(mismatchedManifest, worldData: WorldTestFixture.level(name: "Realm")).write(to: mismatchedArchive)
        await #expect(throws: InstanceFileError.self) { _ = try await RestoreArchive.prepare(mismatchedArchive, under: root) }

        let symlinkMode: UInt32 = 0o120777
        let attacks: [[ZIPFixtureEntry]] = [
            [.init(path: "../escape", data: Data("escape".utf8))],
            [.init(path: "data.json", data: Data("{}".utf8)), .init(path: "java/Item", data: Data()), .init(path: "java/item", data: Data())],
            [.init(path: "data.json", data: Data("{}".utf8)), .init(path: "java/Café", data: Data()), .init(path: "java/Cafe\u{301}", data: Data())],
            [.init(path: "data.json", data: Data("{}".utf8)), .init(path: "java", data: Data()), .init(path: "java/file", data: Data())],
            [.init(path: "data.json", data: Data("{}".utf8)), .init(path: "java/link", data: Data("target".utf8), mode: symlinkMode), .init(path: "java/link/child", data: Data())],
            [.init(path: "data.json", data: Data("{}".utf8)), .init(path: "java/one", data: Data("two".utf8), mode: symlinkMode), .init(path: "java/two", data: Data("real".utf8), mode: symlinkMode), .init(path: "java/real", data: Data())],
            [.init(path: "data.json", data: Data("{}".utf8)), .init(path: "world/link", data: Data("level.dat".utf8), mode: symlinkMode)],
            [.init(path: "data.json", data: Data("{}".utf8)), .init(path: "data.json", data: Data("{}".utf8))],
            [.init(path: "data.json", data: Data("{}".utf8), flags: 1)],
            [.init(path: "data.json", data: Data("{}".utf8), flags: 1 << 11, rawName: Data([0xff]))],
            [.init(path: "data.json", data: Data("{}".utf8), method: 12)],
            [.init(path: "data.json", data: Data("{}".utf8), wrongCRC: true)]
        ]
        for (index, entries) in attacks.enumerated() {
            let archive = root.appendingPathComponent("attack-\(index).hakobackup")
            try storedZIP(entries).write(to: archive)
            await #expect(throws: InstanceFileError.self) { _ = try await RestoreArchive.prepare(archive, under: root) }
        }
        let truncated = root.appendingPathComponent("truncated.hakobackup")
        try Data("not a ZIP".utf8).write(to: truncated)
        await #expect(throws: InstanceFileError.self) { _ = try await RestoreArchive.prepare(truncated, under: root) }
        #expect(!(try FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil).contains { $0.lastPathComponent == "escape" }))
        #expect(try FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil).allSatisfy { !$0.lastPathComponent.hasPrefix(".restore-stage-") })
    }

    @Test func restoreDeflateExactChunkBoundaryAndUnicodeNames() async throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let nfdPack = "Cafe\u{301} 🌸"
        let nfdFile = "Данные é e\u{301}.txt"
        let noise = seededNoise(count: 65_536, seed: 0x5eed_cafe)
        let expected = Data(repeating: 65, count: 16) + noise
        let compressed = boundaryDeflate(noise)
        #expect(consumesExactChunkBoundary(compressed))
        let source = WorldBackupManifest.SourceInstance(id: UUID(), name: "Source", folderName: "Source",
                                                        minecraftVersion: "1.21.1", modLoader: "vanilla", loaderVersion: nil)
        let packPath = "datapacks/\(nfdPack)"
        let manifest = WorldBackupManifest(backupCreatedAt: Date(), name: "Realm", folderName: "World", saveVersion: "1.21.1",
                                           gameType: 0, lastPlayed: nil, size: nil, iconPNG: nil, sourceInstance: source,
                                           datapacks: [.init(file: nfdPack, path: packPath, enabled: true, isDirectory: true, sha512: nil, iconPNG: nil)])
        let rawDirectoryName = Data("world/datapacks/legacy-pack/".utf8)
        let rawFileName = Data("world/datapacks/legacy-pack/pack.mcmeta".utf8)
        let archive = root.appendingPathComponent("unicode.hakoworld")
        let entries = [
            ZIPFixtureEntry(path: "data.json", data: try WorldBackupManifest.encoder().encode(manifest)),
            ZIPFixtureEntry(path: "world/", data: Data(), mode: 0o040755),
            ZIPFixtureEntry(path: "world/level.dat", data: try WorldTestFixture.level(name: "Realm")),
            ZIPFixtureEntry(path: "world/datapacks/", data: Data(), mode: 0o040755),
            ZIPFixtureEntry(path: "world/datapacks/\(nfdPack)/", data: Data(), mode: 0o040755, rawName: rawDirectoryName,
                            extra: unicodePathExtra(rawName: rawDirectoryName, path: "world/datapacks/\(nfdPack)/")),
            ZIPFixtureEntry(path: "world/datapacks/\(nfdPack)/pack.mcmeta", data: Data("{}".utf8), rawName: rawFileName,
                            extra: unicodePathExtra(rawName: rawFileName, path: "world/datapacks/\(nfdPack)/pack.mcmeta")),
            ZIPFixtureEntry(path: "world/\(nfdFile)", data: Data("unicode filename".utf8)),
            ZIPFixtureEntry(path: "world/Флаг 🌸.txt", data: Data("flagged UTF-8 filename".utf8), flags: 1 << 11),
            ZIPFixtureEntry(path: "world/é.txt", data: Data("legacy CP437 filename".utf8),
                            rawName: Data([0x77, 0x6f, 0x72, 0x6c, 0x64, 0x2f, 0x82, 0x2e, 0x74, 0x78, 0x74])),
            ZIPFixtureEntry(path: "world/deflate.bin", data: expected, method: 8, compressedData: compressed)
        ]
        try storedZIP(entries).write(to: archive)

        let staged = try await RestoreArchive.prepare(archive, under: root)
        defer { RestoreArchive.discardStage(staged.directory) }
        #expect(try Data(contentsOf: staged.directory.appendingPathComponent("world/deflate.bin")) == expected)
        #expect(FileManager.default.fileExists(atPath: staged.directory.appendingPathComponent("world/datapacks/Café 🌸/pack.mcmeta").path))
        #expect(FileManager.default.fileExists(atPath: staged.directory.appendingPathComponent("world/Данные é é.txt").path))
        #expect(FileManager.default.fileExists(atPath: staged.directory.appendingPathComponent("world/Флаг 🌸.txt").path))
        #expect(try Data(contentsOf: staged.directory.appendingPathComponent("world/é.txt")) == Data("legacy CP437 filename".utf8))

        var badChecksum = entries
        badChecksum[badChecksum.count - 1].wrongCRC = true
        var truncated = entries
        truncated[truncated.count - 1].compressedData = Data(compressed.dropLast())
        var trailing = entries
        trailing[trailing.count - 1].compressedData = compressed + Data([0xff])
        var oversized = entries
        oversized[oversized.count - 1] = ZIPFixtureEntry(path: "world/deflate.bin", data: Data(expected.dropLast()),
                                                         method: 8, compressedData: compressed)
        for (index, invalidEntries) in [badChecksum, truncated, trailing, oversized].enumerated() {
            let invalidArchive = root.appendingPathComponent("invalid-deflate-\(index).hakoworld")
            try storedZIP(invalidEntries).write(to: invalidArchive)
            await #expect(throws: InstanceFileError.self) { _ = try await RestoreArchive.prepare(invalidArchive, under: root) }
        }
    }

    private func seededNoise(count: Int, seed: UInt32) -> Data {
        var state = seed
        var data = Data(); data.reserveCapacity(count)
        for _ in 0..<count {
            state = state &* 1_664_525 &+ 1_013_904_223
            data.append(UInt8(truncatingIfNeeded: state >> 24))
        }
        return data
    }

    @Test func abandonedRestoreCleanupDeletesOnlyOwnedScratchAndPreservesRecoveryFolders() async throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let deadPID = Int32.max
        let ownedID = UUID()
        let ownedToken = "\(deadPID)-\(ownedID.uuidString)"
        let ownedStage = root.appendingPathComponent(".restore-stage-\(ownedToken)", isDirectory: true)
        try FileManager.default.createDirectory(at: ownedStage, withIntermediateDirectories: false)
        try Data(ownedToken.utf8).write(to: root.appendingPathComponent(ownedStage.lastPathComponent + ".owner"))

        let unownedID = UUID()
        let unownedStage = root.appendingPathComponent(".restore-stage-\(deadPID)-\(unownedID.uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: unownedStage, withIntermediateDirectories: false)
        let retiredInstance = root.appendingPathComponent(".restore-retired-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: retiredInstance, withIntermediateDirectories: false)
        let orphanID = UUID()
        let orphanName = ".restore-stage-\(deadPID)-\(orphanID.uuidString)"
        let orphanMarker = root.appendingPathComponent(orphanName + ".owner")
        try Data("\(deadPID)-\(orphanID.uuidString)".utf8).write(to: orphanMarker)

        let saves = root.appendingPathComponent("Installed/minecraft/saves", isDirectory: true)
        try FileManager.default.createDirectory(at: saves, withIntermediateDirectories: true)
        let worldID = UUID()
        let worldToken = "\(deadPID)-\(worldID.uuidString)"
        let ownedWorld = saves.appendingPathComponent(".hako-restore-world-\(worldToken)", isDirectory: true)
        try FileManager.default.createDirectory(at: ownedWorld, withIntermediateDirectories: false)
        try Data(worldToken.utf8).write(to: saves.appendingPathComponent(ownedWorld.lastPathComponent + ".owner"))
        let unownedWorld = saves.appendingPathComponent(".hako-restore-world-\(deadPID)-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: unownedWorld, withIntermediateDirectories: false)
        let retiredWorld = saves.appendingPathComponent(".hako-restore-retired-world-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: retiredWorld, withIntermediateDirectories: false)
        let orphanWorldID = UUID()
        let orphanWorldName = ".hako-restore-world-\(deadPID)-\(orphanWorldID.uuidString)"
        let orphanWorldMarker = saves.appendingPathComponent(orphanWorldName + ".owner")
        try Data("\(deadPID)-\(orphanWorldID.uuidString)".utf8).write(to: orphanWorldMarker)

        await RestoreArchive.cleanAbandonedStaging(under: root)

        #expect(!FileManager.default.fileExists(atPath: ownedStage.path))
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent(ownedStage.lastPathComponent + ".owner").path))
        #expect(FileManager.default.fileExists(atPath: unownedStage.path))
        #expect(FileManager.default.fileExists(atPath: retiredInstance.path))
        #expect(!FileManager.default.fileExists(atPath: orphanMarker.path))
        #expect(!FileManager.default.fileExists(atPath: ownedWorld.path))
        #expect(FileManager.default.fileExists(atPath: unownedWorld.path))
        #expect(FileManager.default.fileExists(atPath: retiredWorld.path))
        #expect(!FileManager.default.fileExists(atPath: orphanWorldMarker.path))
    }

    @Test func backupsFolderNameIsReserved() throws {
        let root = try temporary()
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let store = InstanceStore(context: container.mainContext, storage: .init(root: root))
        #expect(throws: InstanceFileError.self) { try store.validateName("Backups") }
    }
}

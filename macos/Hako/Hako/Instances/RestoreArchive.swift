import Foundation
import Darwin
import zlib

nonisolated enum RestoreArchiveKind: Sendable {
    case instance(InstanceBackupManifest)
    case world(WorldBackupManifest)
}

nonisolated struct StagedRestoreArchive: Sendable {
    let directory: URL
    let archive: URL
    let kind: RestoreArchiveKind
}

nonisolated enum RestoreArchive {
    fileprivate static let chunkSize = 64 * 1024
    fileprivate static let maxEntries = 250_000
    fileprivate static let maxCentralDirectory = 64 * 1024 * 1024
    fileprivate static let maxManifest = 16 * 1024 * 1024
    private static let maxWorldMetadataBytes = 16 * 1024 * 1024
    private static let ownerMarkerExtension = "owner"

    @concurrent static func removeRetiredDirectory(at url: URL) async throws {
        try FileManager.default.removeItem(at: url)
    }

    @concurrent static func prepare(
        _ archive: URL,
        under root: URL,
        availableBytes capacityCheck: (@Sendable (URL) throws -> UInt64)? = nil
    ) async throws -> StagedRestoreArchive {
        try Task.checkCancellation()
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let token = "\(getpid())-\(UUID().uuidString)"
        let directory = root.appendingPathComponent(".restore-stage-\(token)", isDirectory: true)
        var markerWritten = false
        do {
            try createDirectory(directory, mode: 0o700)
            try writeOwnershipMarker(in: directory, token: token)
            markerWritten = true
            let kind = try extract(archive, to: directory, root: root, capacityCheck: capacityCheck ?? Self.availableBytes)
            try Task.checkCancellation()
            return .init(directory: directory, archive: archive, kind: kind)
        } catch {
            try? FileManager.default.removeItem(at: directory)
            if markerWritten && scratchIsMissing(directory) { try? FileManager.default.removeItem(at: ownershipMarkerURL(for: directory)) }
            throw error
        }
    }

    @concurrent static func cleanAbandonedStaging(under root: URL) async {
        guard let items = try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey]) else { return }
        for item in items where item.lastPathComponent.hasPrefix(".restore-stage-") {
            if item.lastPathComponent.hasSuffix(".owner") {
                let scratchName = String(item.lastPathComponent.dropLast(".owner".count))
                guard let token = abandonedToken(scratchName, prefix: ".restore-stage-"),
                      hasOwnershipMarker(at: item, token: token),
                      scratchIsMissing(root.appendingPathComponent(scratchName)) else { continue }
                try? FileManager.default.removeItem(at: item)
                continue
            }
            guard let token = abandonedToken(item.lastPathComponent, prefix: ".restore-stage-"), hasOwnershipMarker(item, token: token) else { continue }
            guard (try? item.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey]).isDirectory) == true,
                  (try? item.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey]).isSymbolicLink) != true else { continue }
            do {
                try FileManager.default.removeItem(at: item)
                try? FileManager.default.removeItem(at: ownershipMarkerURL(for: item))
            } catch { }
        }
        for instance in items {
            guard (try? instance.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey]).isDirectory) == true,
                  (try? instance.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey]).isSymbolicLink) != true else { continue }
            let minecraft = instance.appendingPathComponent("minecraft", isDirectory: true)
            let saves = instance.appendingPathComponent("minecraft/saves", isDirectory: true)
            var metadata = stat()
            guard lstat(minecraft.path, &metadata) == 0, metadata.st_mode & S_IFMT == S_IFDIR,
                  lstat(saves.path, &metadata) == 0, metadata.st_mode & S_IFMT == S_IFDIR else { continue }
            guard let worlds = try? FileManager.default.contentsOfDirectory(at: saves, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey]) else { continue }
            for world in worlds where world.lastPathComponent.hasPrefix(".hako-restore-world-") {
                if world.lastPathComponent.hasSuffix(".owner") {
                    let scratchName = String(world.lastPathComponent.dropLast(".owner".count))
                    guard let token = abandonedToken(scratchName, prefix: ".hako-restore-world-"),
                          hasOwnershipMarker(at: world, token: token),
                          scratchIsMissing(saves.appendingPathComponent(scratchName)) else { continue }
                    try? FileManager.default.removeItem(at: world)
                    continue
                }
                guard let token = abandonedToken(world.lastPathComponent, prefix: ".hako-restore-world-"), hasOwnershipMarker(world, token: token) else { continue }
                guard (try? world.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey]).isDirectory) == true,
                      (try? world.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey]).isSymbolicLink) != true else { continue }
                do {
                    try FileManager.default.removeItem(at: world)
                    try? FileManager.default.removeItem(at: ownershipMarkerURL(for: world))
                } catch { }
            }
        }
    }

    private static func abandonedToken(_ name: String, prefix: String) -> String? {
        let components = name.dropFirst(prefix.count).split(separator: "-", maxSplits: 1)
        guard name.hasPrefix(prefix), components.count == 2, let ownerPID = pid_t(components[0]), ownerPID > 0,
              let id = UUID(uuidString: String(components[1])), id.uuidString == components[1],
              kill(ownerPID, 0) != 0, errno == ESRCH else { return nil }
        return "\(ownerPID)-\(id.uuidString)"
    }

    private static func writeOwnershipMarker(in directory: URL, token: String) throws {
        let marker = ownershipMarkerURL(for: directory)
        let descriptor = open(marker.path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, 0o600)
        guard descriptor >= 0 else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
        defer { _ = close(descriptor) }
        let bytes = Array(token.utf8)
        let written = bytes.withUnsafeBytes { raw in write(descriptor, raw.baseAddress, raw.count) }
        guard written == bytes.count else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
    }

    private static func hasOwnershipMarker(_ directory: URL, token: String) -> Bool {
        hasOwnershipMarker(at: ownershipMarkerURL(for: directory), token: token)
    }

    private static func hasOwnershipMarker(at marker: URL, token: String) -> Bool {
        var metadata = stat()
        guard lstat(marker.path, &metadata) == 0, metadata.st_mode & S_IFMT == S_IFREG,
              metadata.st_size == token.utf8.count,
              let contents = try? Data(contentsOf: marker), String(data: contents, encoding: .utf8) == token else { return false }
        return true
    }

    private static func scratchIsMissing(_ url: URL) -> Bool {
        var metadata = stat()
        return lstat(url.path, &metadata) != 0 && errno == ENOENT
    }

    private static func ownershipMarkerURL(for directory: URL) -> URL {
        directory.deletingLastPathComponent().appendingPathComponent(directory.lastPathComponent + "." + ownerMarkerExtension)
    }

    static func prepareInstanceStageForCommit(_ directory: URL) throws {
        let prefix = ".restore-stage-"
        guard directory.lastPathComponent.hasPrefix(prefix) else {
            throw InstanceFileError.message(String(appLocalized: "Временная папка сборки не принадлежит восстановлению."))
        }
        let token = String(directory.lastPathComponent.dropFirst(prefix.count))
        guard hasOwnershipMarker(directory, token: token) else {
            throw InstanceFileError.message(String(appLocalized: "Временная папка сборки не принадлежит восстановлению."))
        }
        guard chmod(directory.path, 0o755) == 0 else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
    }

    static func discardStage(_ directory: URL) {
        discardStageContents(directory)
    }

    @concurrent static func discardStageOffActor(_ directory: URL) async {
        discardStageContents(directory)
    }

    private static func discardStageContents(_ directory: URL) {
        guard directory.lastPathComponent.hasPrefix(".restore-stage-") else { return }
        let marker = ownershipMarkerURL(for: directory)
        let token = String(directory.lastPathComponent.dropFirst(".restore-stage-".count))
        guard hasOwnershipMarker(at: marker, token: token) else { return }
        var metadata = stat()
        if lstat(directory.path, &metadata) == 0 {
            guard metadata.st_mode & S_IFMT == S_IFDIR else { return }
            try? FileManager.default.removeItem(at: directory)
        }
        if scratchIsMissing(directory) { try? FileManager.default.removeItem(at: marker) }
    }

    @concurrent static func discardWorldStage(_ directory: URL) async throws {
        try discardWorldStageContents(directory)
    }

    private static func discardWorldStageContents(_ directory: URL) throws {
        guard directory.lastPathComponent.hasPrefix(".hako-restore-world-") else { return }
        let marker = ownershipMarkerURL(for: directory)
        var metadata = stat()
        if lstat(directory.path, &metadata) == 0 {
            guard metadata.st_mode & S_IFMT == S_IFDIR else { throw InstanceFileError.message(String(appLocalized: "Не удалось очистить временную папку восстановления мира.")) }
            try FileManager.default.removeItem(at: directory)
        } else if errno != ENOENT {
            throw POSIXError(.init(rawValue: errno) ?? .EIO)
        }
        if let token = try? String(contentsOf: marker, encoding: .utf8),
           token == String(directory.lastPathComponent.dropFirst(".hako-restore-world-".count)) {
            try? FileManager.default.removeItem(at: marker)
        }
    }

    @concurrent static func stageWorldCopy(_ source: URL, under saves: URL) async throws -> URL {
        let manager = FileManager.default
        let token = "\(getpid())-\(UUID().uuidString)"
        let target = saves.appendingPathComponent(".hako-restore-world-\(token)", isDirectory: true)
        var markerWritten = false
        let entries = try collectWorldCopyEntries(source)
        var total: UInt64 = 0
        for entry in entries {
            try Task.checkCancellation()
            let (sum, overflow) = total.addingReportingOverflow(entry.size)
            guard !overflow else { throw InstanceFileError.message(String(appLocalized: "Размер мира слишком велик для восстановления.")) }
            total = sum
        }
        guard total <= (try availableBytes(at: saves)) else {
            throw InstanceFileError.message(String(appLocalized: "Для восстановления мира на диске назначения недостаточно свободного места."))
        }

        do {
            guard mkdir(target.path, 0o700) == 0 else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
            try writeOwnershipMarker(in: target, token: token)
            markerWritten = true
            var copied: UInt64 = 0
            for entry in entries {
                try Task.checkCancellation()
                let destination = try InstanceStorage.containedURL(entry.relative, in: target)
                if entry.isDirectory {
                    guard mkdir(destination.path, 0o755) == 0 || errno == EEXIST else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
                    guard chmod(destination.path, 0o755) == 0 else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
                    continue
                }
                let parent = destination.deletingLastPathComponent()
                if !manager.fileExists(atPath: parent.path) {
                    try manager.createDirectory(at: parent, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o755])
                }
                var sourceMode = stat()
                guard lstat(entry.source.path, &sourceMode) == 0, sourceMode.st_mode & S_IFMT == S_IFREG else {
                    throw InstanceFileError.message(String(appLocalized: "Временный мир содержит небезопасный объект."))
                }
                let mode: mode_t = sourceMode.st_mode & 0o111 == 0 ? 0o644 : 0o755
                do {
                    let input = try FileHandle(forReadingFrom: entry.source)
                    defer { try? input.close() }
                    let output = open(destination.path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, mode)
                    guard output >= 0 else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
                    defer { _ = close(output) }
                    var fileBytes: UInt64 = 0
                    while let data = try input.read(upToCount: chunkSize), !data.isEmpty {
                        try Task.checkCancellation()
                        try data.withUnsafeBytes { raw in
                            var written = 0
                            while written < raw.count {
                                let result = write(output, raw.baseAddress!.advanced(by: written), raw.count - written)
                                guard result > 0 else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
                                written += result
                            }
                        }
                        fileBytes += UInt64(data.count)
                        copied += UInt64(data.count)
                        if copied % (16 * 1024 * 1024) < UInt64(data.count), try availableBytes(at: saves) < UInt64(data.count) {
                            throw InstanceFileError.message(String(appLocalized: "Для восстановления мира на диске назначения недостаточно свободного места."))
                        }
                    }
                    guard fileBytes == entry.size, fchmod(output, mode) == 0 else { throw InstanceFileError.message(String(appLocalized: "Временный мир не удалось полностью подготовить.")) }
                }
            }
            return target
        } catch {
            try? manager.removeItem(at: target)
            if markerWritten && scratchIsMissing(target) { try? manager.removeItem(at: ownershipMarkerURL(for: target)) }
            throw error
        }
    }

    static func prepareWorldStageForCommit(_ directory: URL) throws {
        let prefix = ".hako-restore-world-"
        guard directory.lastPathComponent.hasPrefix(prefix) else {
            throw InstanceFileError.message(String(appLocalized: "Временная папка мира не принадлежит восстановлению."))
        }
        let token = String(directory.lastPathComponent.dropFirst(prefix.count))
        guard hasOwnershipMarker(directory, token: token) else {
            throw InstanceFileError.message(String(appLocalized: "Временная папка мира не принадлежит восстановлению."))
        }
        guard chmod(directory.path, 0o755) == 0 else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
    }

    private static func collectWorldCopyEntries(_ source: URL) throws -> [RestoreWorldCopyEntry] {
        let manager = FileManager.default
        var failure: Error?
        guard let enumerator = manager.enumerator(at: source, includingPropertiesForKeys: [.isDirectoryKey, .isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey], errorHandler: { _, error in failure = error; return false }) else {
            throw InstanceFileError.message(String(appLocalized: "Не удалось прочитать временный мир для восстановления."))
        }
        var entries: [RestoreWorldCopyEntry] = []
        for case let file as URL in enumerator {
            try Task.checkCancellation()
            let values = try file.resourceValues(forKeys: [.isDirectoryKey, .isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey])
            guard values.isSymbolicLink != true,
                  values.isDirectory == true || values.isRegularFile == true,
                  file.path.hasPrefix(source.path + "/") else {
                throw InstanceFileError.message(String(appLocalized: "Временный мир содержит небезопасный объект."))
            }
            let relative = String(file.path.dropFirst(source.path.count + 1))
            _ = try InstanceStorage.containedURL(relative, in: source)
            let isDirectory = values.isDirectory == true
            let size: UInt64
            if isDirectory { size = 0 }
            else {
                guard let fileSize = values.fileSize, fileSize >= 0 else { throw CocoaError(.fileReadUnknown) }
                size = UInt64(fileSize)
            }
            entries.append(.init(source: file, relative: relative, isDirectory: isDirectory, size: size))
        }
        if let failure { throw failure }
        return entries.sorted { $0.relative < $1.relative }
    }

    private static func extract(
        _ archive: URL,
        to staging: URL,
        root: URL,
        capacityCheck: @Sendable (URL) throws -> UInt64
    ) throws -> RestoreArchiveKind {
        let reader = try ZIPReader(url: archive)
        try reader.validateTree()
        let manifestData = try reader.data(for: "data.json", limit: maxManifest)
        let kind: RestoreArchiveKind
        switch archive.pathExtension.lowercased() {
        case InstanceBackupManifest.fileExtension:
            let manifest = try InstanceBackupManifest.decoder().decode(InstanceBackupManifest.self, from: manifestData)
            try validate(manifest)
            try reader.validateInstanceEntries()
            kind = .instance(manifest)
        case WorldBackupManifest.fileExtension:
            let manifest = try WorldBackupManifest.decoder().decode(WorldBackupManifest.self, from: manifestData)
            try validate(manifest)
            try reader.validateWorldEntries()
            kind = .world(manifest)
        default:
            throw error("Выберите файл .hakobackup или .hakoworld.")
        }
        var expected: UInt64 = 0
        for entry in reader.entries {
            let (sum, overflow) = expected.addingReportingOverflow(entry.uncompressedSize)
            guard !overflow else { throw error("Размер архива слишком велик для восстановления.") }
            expected = sum
        }
        let available = try capacityCheck(root)
        guard expected <= available else { throw error("Для распаковки архива недостаточно свободного места.") }
        try reader.extract(into: staging, availableAt: root, totalExpected: expected, capacityCheck: capacityCheck)
        switch kind {
        case .instance(let manifest):
            try FileManager.default.removeItem(at: staging.appendingPathComponent(InstanceBackupManifest.fileName))
            try validateExtractedInstance(staging, manifest: manifest)
        case .world(let manifest):
            try validateExtractedWorld(staging, manifest: manifest)
        }
        return kind
    }

    private static func validate(_ manifest: InstanceBackupManifest) throws {
        guard manifest.formatVersion == InstanceBackupManifest.currentFormat,
              !manifest.name.isEmpty, !manifest.folderName.isEmpty,
              manifest.minecraftVersion.range(of: #"^[A-Za-z0-9._+-]{1,128}$"#, options: .regularExpression) != nil,
              let loader = ModLoader(rawValue: manifest.modLoader),
              (loader == .fabric) == (manifest.loaderVersion != nil),
              manifest.javaMajorVersion > 0,
              manifest.profile.metadataSHA1.count == 40,
              manifest.profile.metadataSHA1.allSatisfy(\.isHexDigit),
              URL(string: manifest.profile.metadataURL)?.scheme == "https",
              !manifest.profile.javaExecutable.isEmpty,
              !manifest.profile.javaExecutable.hasPrefix("/"),
              !manifest.profile.offlineMode || OfflineUsername.isValid(manifest.profile.offlineUsername),
              manifest.modCount == manifest.mods.count,
              manifest.mods.allSatisfy({ !$0.file.isEmpty && !$0.file.contains("/") && !$0.file.contains("\\") }) else {
            throw error("Манифест резервной копии сборки повреждён или не поддерживается.")
        }
        if loader == .fabric {
            guard let configuration = manifest.profile.fabricConfiguration,
                  configuration.loaderVersion == manifest.loaderVersion,
                  !configuration.loaderVersion.isEmpty,
                  manifest.profile.fabricProfileSHA1?.count == 40,
                  manifest.profile.fabricProfileSHA1?.allSatisfy(\.isHexDigit) == true else {
                throw error("Профиль Fabric в резервной копии не соответствует манифесту.")
            }
            if let api = configuration.api {
                guard api.projectID == FabricAPIDescriptor.project,
                      api.filename.lowercased().hasSuffix(".jar"), !api.filename.contains("/"),
                      !api.filename.contains("\\"), !api.filename.contains(":"),
                      !api.filename.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }),
                      api.sha1.count == 40, api.sha1.allSatisfy(\.isHexDigit),
                      api.sha512.count == 128, api.sha512.allSatisfy(\.isHexDigit), api.size > 0,
                      api.url.scheme == "https" else {
                    throw error("Описание Fabric API в резервной копии повреждено.")
                }
            }
        } else if manifest.profile.fabricConfiguration != nil || manifest.profile.fabricProfileSHA1 != nil {
            throw error("Манифест Vanilla содержит параметры Fabric.")
        }
        guard try InstanceName.validated(manifest.name) == manifest.name,
              try InstanceName.folder(for: manifest.name) == manifest.folderName else {
            throw error("Имя сборки в резервной копии недопустимо.")
        }
    }

    private static func validate(_ manifest: WorldBackupManifest) throws {
        guard manifest.formatVersion == WorldBackupManifest.currentFormat,
              manifest.worldPath == "world",
              !manifest.folderName.isEmpty,
              !manifest.name.isEmpty,
              !manifest.folderName.contains("/"), !manifest.folderName.contains("\\"),
              manifest.folderName != ".", manifest.folderName != "..",
              ModLoader(rawValue: manifest.sourceInstance.modLoader) != nil,
              (manifest.sourceInstance.modLoader == ModLoader.fabric.rawValue) == (manifest.sourceInstance.loaderVersion != nil) else {
            throw error("Манифест резервной копии мира повреждён или не поддерживается.")
        }
        let paths = manifest.datapacks.map(\.path)
        let foldedPaths = Set(paths.map { $0.precomposedStringWithCanonicalMapping.lowercased(with: Locale(identifier: "en_US_POSIX")) })
        guard foldedPaths.count == paths.count,
              manifest.datapacks.allSatisfy({ pack in
                  let path = pack.path
                  let canonicalPath = path.precomposedStringWithCanonicalMapping
                  let components = path.split(separator: "/", omittingEmptySubsequences: false)
                  return (path.hasPrefix("datapacks/") || path.hasPrefix(".hako-disabled-datapacks/")) &&
                      !canonicalPath.contains("\\") && !canonicalPath.contains(":") && !canonicalPath.contains("\0") &&
                      components.allSatisfy({ !$0.isEmpty && $0 != "." && $0 != ".." }) &&
                      URL(fileURLWithPath: canonicalPath).lastPathComponent == pack.file.precomposedStringWithCanonicalMapping && !pack.file.isEmpty
              }) else {
            throw error("Список датапаков в резервной копии мира повреждён.")
        }
    }

    private static func validateExtractedInstance(_ root: URL, manifest: InstanceBackupManifest) throws {
        let minecraft = root.appendingPathComponent("minecraft", isDirectory: true)
        let version = minecraft.appendingPathComponent("versions/\(manifest.minecraftVersion)/\(manifest.minecraftVersion).json")
        let jar = minecraft.appendingPathComponent("versions/\(manifest.minecraftVersion)/\(manifest.minecraftVersion).jar")
        guard FileManager.default.fileExists(atPath: version.path), FileManager.default.fileExists(atPath: jar.path) else {
            throw error("В резервной копии отсутствуют обязательные файлы Minecraft.")
        }
        guard let versionSize = try version.resourceValues(forKeys: [.fileSizeKey]).fileSize,
              versionSize <= 16 * 1024 * 1024 else { throw error("Манифест Minecraft в резервной копии слишком велик.") }
        let fabricURL = root.appendingPathComponent("minecraft/.hako-fabric.json")
        if FileManager.default.fileExists(atPath: fabricURL.path),
           let size = try fabricURL.resourceValues(forKeys: [.fileSizeKey]).fileSize,
           size > 16 * 1024 * 1024 { throw error("Профиль Fabric в резервной копии слишком велик.") }
        if let icon = manifest.iconPNG {
            let iconURL = root.appendingPathComponent("icon.png")
            guard let size = try iconURL.resourceValues(forKeys: [.fileSizeKey]).fileSize,
                  size == icon.count, try Data(contentsOf: iconURL) == icon else {
                throw error("Иконка в резервной копии сборки не соответствует манифесту.")
            }
        } else if manifest.iconSymbol.isEmpty {
            throw error("В резервной копии отсутствует пользовательская иконка сборки.")
        }
        try validateModManifest(manifest, in: root)
        let metadata = try Data(contentsOf: version)
        try MojangIntegrity.check(metadata, download: .init(url: version, sha1: manifest.profile.metadataSHA1))
        let parsed = try JSONDecoder().decode(MinecraftVersionManifest.self, from: metadata)
        guard parsed.id == manifest.minecraftVersion,
              parsed.legacyTexturepacks == manifest.profile.legacyTexturepacks,
              let client = parsed.downloads["client"],
              try MojangIntegrity.validFile(jar, download: client) else {
            throw error("Профиль Minecraft или его JAR не соответствует резервной копии.")
        }
        let java = try InstanceStorage.containedURL("java/\(manifest.profile.javaExecutable)", in: root)
        try validateJava(java, in: root, minimumMajor: max(manifest.javaMajorVersion, parsed.java.majorVersion))
        let fabricProfile: FabricProfile?
        if manifest.modLoader == ModLoader.fabric.rawValue {
            let profileURL = root.appendingPathComponent("minecraft/.hako-fabric.json")
            let profileData = try Data(contentsOf: profileURL)
            guard let expected = manifest.profile.fabricProfileSHA1 else { throw error("Профиль Fabric отсутствует.") }
            try MojangIntegrity.check(profileData, download: .init(url: profileURL, sha1: expected))
            let profile = try JSONDecoder().decode(FabricProfile.self, from: profileData)
            try profile.validate(minecraft: manifest.minecraftVersion)
            guard profile.id == "fabric-loader-\(manifest.loaderVersion!)-\(manifest.minecraftVersion)" else {
                throw error("Профиль Fabric не соответствует загрузчику в манифесте.")
            }
            fabricProfile = profile
        } else {
            fabricProfile = nil
        }
        let fabricOverrides = Set(fabricProfile?.libraries.map(\.key) ?? [])
        for library in parsed.libraries where library.allowed(on: .current) {
            guard let download = library.downloads?.artifact, let path = download.path,
                  !fabricOverrides.contains(FabricProfile.Library.key(library.name)) else { continue }
            let file = try InstanceStorage.containedURL("minecraft/libraries/\(path)", in: root)
            guard try MojangIntegrity.validFile(file, download: download) else {
                throw error("Библиотека Minecraft в резервной копии отсутствует или повреждена.")
            }
        }
        for library in fabricProfile?.libraries ?? [] {
            guard let sha1 = library.sha1 else { continue }
            let file = try InstanceStorage.containedURL("minecraft/libraries/\(library.path)", in: root)
            guard try MojangIntegrity.validFile(file, download: .init(url: file, sha1: sha1, size: library.size)) else {
                throw error("Библиотека Fabric в резервной копии отсутствует или повреждена.")
            }
        }
        if let api = manifest.profile.fabricConfiguration?.api {
            guard let mod = manifest.mods.first(where: { $0.file.caseInsensitiveCompare(api.filename) == .orderedSame }) else {
                throw error("Fabric API из манифеста отсутствует в списке модов.")
            }
            let name = mod.file + (mod.enabled ? "" : ".disabled")
            let file = try InstanceStorage.containedURL("minecraft/mods/\(name)", in: root)
            guard try MojangIntegrity.validFile(file, download: .init(url: file, sha1: api.sha1, size: api.size)),
                  try FabricClient.validAPI(file, api: api) else {
                throw error("Fabric API в резервной копии отсутствует или повреждён.")
            }
        }
    }

    private static func validateModManifest(_ manifest: InstanceBackupManifest, in root: URL) throws {
        var names = Set<String>()
        for mod in manifest.mods {
            let folded = mod.file.lowercased(with: Locale(identifier: "en_US_POSIX"))
            guard mod.file.lowercased().hasSuffix(".jar"), !mod.file.contains(":"),
                  !mod.file.contains("/"), !mod.file.contains("\\"),
                  mod.file.unicodeScalars.allSatisfy({ !CharacterSet.controlCharacters.contains($0) }), names.insert(folded).inserted else {
                throw error("Список модов в манифесте резервной копии повреждён.")
            }
            let archivedName = mod.file.precomposedStringWithCanonicalMapping + (mod.enabled ? "" : ".disabled")
            let url = try InstanceStorage.containedURL("minecraft/mods/\(archivedName)", in: root)
            let values = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
            guard values.isRegularFile == true, values.isSymbolicLink != true else {
                throw error("Мод из манифеста отсутствует или имеет неверный тип.")
            }
        }
    }

    private static func validateExtractedWorld(_ staging: URL, manifest: WorldBackupManifest) throws {
        let world = staging.appendingPathComponent(manifest.worldPath, isDirectory: true)
        var foundMetadata = false
        var authoritativeMetadata: WorldMetadata?
        for file in ["level.dat", "level.dat_old"] {
            let url = world.appendingPathComponent(file)
            guard FileManager.default.fileExists(atPath: url.path) else { continue }
            guard let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize, size <= 16 * 1024 * 1024 else {
                throw error("Метаданные мира в резервной копии слишком велики.")
            }
            let metadata = try WorldMetadata.read(worldMetadataData(at: url))
            guard metadata.name == manifest.name else { throw error("Название мира в манифесте и файлах сохранения не совпадает.") }
            if file == "level.dat" { authoritativeMetadata = metadata }
            else if authoritativeMetadata == nil { authoritativeMetadata = metadata }
            foundMetadata = true
        }
        guard foundMetadata else { throw error("В резервной копии отсутствуют файлы метаданных мира.") }
        guard let metadata = authoritativeMetadata,
              metadata.version == manifest.saveVersion,
              metadata.gameType == manifest.gameType,
              sameDate(metadata.lastPlayed, manifest.lastPlayed) else {
            throw error("Сведения в манифесте мира не совпадают с level.dat.")
        }
        for pack in manifest.datapacks {
            let item = try InstanceStorage.containedURL("\(manifest.worldPath)/\(pack.path.precomposedStringWithCanonicalMapping)", in: staging)
            let values = try item.resourceValues(forKeys: [.isDirectoryKey, .isRegularFileKey, .isSymbolicLinkKey])
            guard values.isSymbolicLink != true,
                  pack.isDirectory ? values.isDirectory == true : values.isRegularFile == true else {
                throw error("Файл датапака из манифеста отсутствует или имеет неверный тип.")
            }
        }
    }

    private static func sameDate(_ lhs: Date?, _ rhs: Date?) -> Bool {
        guard let lhs, let rhs else { return lhs == rhs }
        return abs(lhs.timeIntervalSince(rhs)) < 1
    }

    static func worldMetadataData(at url: URL) throws -> Data {
        var metadata = stat()
        guard lstat(url.path, &metadata) == 0, metadata.st_mode & S_IFMT == S_IFREG,
              metadata.st_size >= 0, metadata.st_size <= maxWorldMetadataBytes else {
            throw error("Метаданные мира в резервной копии слишком велики.")
        }
        return try Data(contentsOf: url, options: .mappedIfSafe)
    }

    private static func validateJava(_ executable: URL, in root: URL, minimumMajor: Int) throws {
        let javaRoot = root.appendingPathComponent("java", isDirectory: true).resolvingSymlinksInPath().standardizedFileURL.path + "/"
        let resolved = executable.resolvingSymlinksInPath().standardizedFileURL
        var metadata = stat()
        guard resolved.path.hasPrefix(javaRoot), lstat(resolved.path, &metadata) == 0,
              metadata.st_mode & S_IFMT == S_IFREG, metadata.st_mode & 0o111 != 0,
              FileManager.default.isExecutableFile(atPath: resolved.path) else {
            throw error("Исполняемый файл Java отсутствует или находится за пределами Java в резервной копии.")
        }
        guard !executable.lastPathComponent.lowercased().hasSuffix(".exe") else {
            throw error("В резервной копии находится Java для Windows. На Mac её восстановить нельзя.")
        }
        let handle = try FileHandle(forReadingFrom: executable)
        defer { try? handle.close() }
        guard let bytes = try handle.read(upToCount: 4096),
              let architecture = MachOArchitecture(bytes: bytes),
              architecture.contains(MinecraftPlatform.current == .appleSilicon ? CPU_TYPE_ARM64 : CPU_TYPE_X86_64) else {
            throw error("Архитектура Java в резервной копии несовместима с этим Mac.")
        }
        let release = resolved.deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("release")
        let releaseResolved = release.resolvingSymlinksInPath().standardizedFileURL
        var releaseMetadata = stat()
        guard releaseResolved.path.hasPrefix(javaRoot), lstat(releaseResolved.path, &releaseMetadata) == 0,
              releaseMetadata.st_mode & S_IFMT == S_IFREG, releaseMetadata.st_size <= 1_048_576,
              let properties = try? String(contentsOf: releaseResolved, encoding: .utf8),
              let version = properties.split(separator: "\n").first(where: { $0.hasPrefix("JAVA_VERSION=") }) else {
            throw error("В резервной копии отсутствуют допустимые метаданные Java.")
        }
        let raw = version.split(separator: "=", maxSplits: 1).last?.trimmingCharacters(in: CharacterSet(charactersIn: "\"")) ?? ""
        let numbers = raw.split(whereSeparator: { !$0.isNumber }).compactMap { Int($0) }
        let major = numbers.first == 1 ? numbers.dropFirst().first : numbers.first
        guard let major, major >= minimumMajor else { throw error("Java в резервной копии старше минимальной версии для этой сборки.") }
    }

    static func availableBytes(at url: URL) throws -> UInt64 {
        let values = try url.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
        guard let available = values.volumeAvailableCapacityForImportantUsage, available >= 0 else {
            throw error("Не удалось проверить свободное место на диске.")
        }
        return UInt64(available)
    }

    private static func createDirectory(_ url: URL, mode: mode_t) throws {
        guard mkdir(url.path, mode) == 0 else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
    }

    private static func error(_ text: LocalizedStringResource) -> InstanceFileError { .message(String(appLocalized: text)) }
}

nonisolated private struct ZIPEntry {
    enum Kind: Equatable { case file, directory, symlink }
    let path: String
    let rawName: Data
    let kind: Kind
    let method: UInt16
    let flags: UInt16
    let crc: UInt32
    let compressedSize: UInt64
    let uncompressedSize: UInt64
    let localOffset: UInt64
    let mode: UInt16
}

nonisolated private struct RestoreWorldCopyEntry: Sendable {
    let source: URL
    let relative: String
    let isDirectory: Bool
    let size: UInt64
}

nonisolated private final class ZIPReader {
    let url: URL
    let handle: FileHandle
    let fileSize: UInt64
    private(set) var centralOffset: UInt64 = 0
    private(set) var centralSize: UInt64 = 0
    private(set) var entries: [ZIPEntry] = []

    init(url: URL) throws {
        self.url = url
        let values = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey])
        guard values.isRegularFile == true, values.isSymbolicLink != true, let fileSizeValue = values.fileSize, fileSizeValue >= 22 else {
            throw InstanceFileError.message(String(appLocalized: "Файл резервной копии недоступен или повреждён."))
        }
        fileSize = UInt64(fileSizeValue)
        handle = try FileHandle(forReadingFrom: url)
        let tailSize = min(fileSize, 65_557)
        let tail = try read(at: fileSize - tailSize, count: Int(tailSize))
        guard let endIndex = tail.endRecordIndex() else { throw badArchive() }
        let endRecordOffset = fileSize - tailSize + UInt64(endIndex)
        var end = BinaryCursor(data: tail, offset: endIndex)
        _ = try end.u32()
        let disk = try end.u16(), centralDisk = try end.u16()
        let diskCount = try end.u16(), totalCount16 = try end.u16()
        let centralSize32 = try end.u32(), centralOffset32 = try end.u32()
        let commentLength = try end.u16()
        guard end.offset + Int(commentLength) == tail.count, disk == 0, centralDisk == 0, diskCount == totalCount16 else { throw badArchive() }

        var totalCount = UInt64(totalCount16), directorySize = UInt64(centralSize32), offset = UInt64(centralOffset32)
        var directoryLimit = endRecordOffset
        if totalCount16 == UInt16.max || centralSize32 == UInt32.max || centralOffset32 == UInt32.max {
            guard endIndex >= 20 else { throw badArchive() }
            let locatorAbsolute = fileSize - tailSize + UInt64(endIndex) - 20
            let locator = try read(at: locatorAbsolute, count: 20)
            var location = BinaryCursor(data: locator)
            guard try location.u32() == 0x07064b50, try location.u32() == 0 else { throw badArchive() }
            let zip64Offset = try location.u64()
            directoryLimit = zip64Offset
            guard try location.u32() == 1 else { throw badArchive() }
            guard zip64Offset <= locatorAbsolute, locatorAbsolute - zip64Offset >= 56 else { throw badArchive() }
            let record = try read(at: zip64Offset, count: 56)
            var zip64 = BinaryCursor(data: record)
            guard try zip64.u32() == 0x06064b50 else { throw badArchive() }
            let zip64RecordSize = try zip64.u64()
            guard zip64RecordSize >= 44, zip64RecordSize <= locatorAbsolute - zip64Offset - 12 else { throw badArchive() }
            _ = try zip64.u16(); _ = try zip64.u16()
            guard try zip64.u32() == 0, try zip64.u32() == 0 else { throw badArchive() }
            let diskEntries = try zip64.u64()
            totalCount = try zip64.u64()
            directorySize = try zip64.u64()
            offset = try zip64.u64()
            guard diskEntries == totalCount else { throw badArchive() }
            guard (diskCount == UInt16.max || UInt64(diskCount) == totalCount),
                  (totalCount16 == UInt16.max || UInt64(totalCount16) == totalCount),
                  (centralSize32 == UInt32.max || UInt64(centralSize32) == directorySize),
                  (centralOffset32 == UInt32.max || UInt64(centralOffset32) == offset) else { throw badArchive() }
        }
        guard totalCount <= UInt64(RestoreArchive.maxEntries), directorySize <= UInt64(RestoreArchive.maxCentralDirectory),
              offset <= fileSize, directorySize <= fileSize - offset, offset + directorySize <= directoryLimit else { throw badArchive() }
        centralOffset = offset
        centralSize = directorySize
        entries = try Self.parseEntries(from: self, count: Int(totalCount))
    }

    func validateTree() throws {
        var exact = Set<String>(), folded = Set<String>(), kinds: [String: ZIPEntry.Kind] = [:]
        var foldedComponents: [String: String] = [:]
        var implicitDirectories = Set<String>()
        for entry in entries {
            let key = entry.path.precomposedStringWithCanonicalMapping
            let foldedKey = key.lowercased(with: Locale(identifier: "en_US_POSIX"))
            guard exact.insert(key).inserted, folded.insert(foldedKey).inserted else { throw badArchive() }
            let components = key.split(separator: "/").map(String.init)
            for index in components.indices {
                let spelling = components.prefix(index + 1).joined(separator: "/")
                let componentKey = spelling.lowercased(with: Locale(identifier: "en_US_POSIX"))
                if let existing = foldedComponents[componentKey], existing != spelling { throw badArchive() }
                foldedComponents[componentKey] = spelling
            }
            for index in 1..<components.count {
                let ancestor = components.prefix(index).joined(separator: "/")
                if let ancestorKind = kinds[ancestor], ancestorKind != .directory { throw badArchive() }
                if kinds[ancestor] == nil { kinds[ancestor] = .directory; implicitDirectories.insert(ancestor) }
            }
            if let prior = kinds[key] {
                guard prior == .directory, entry.kind == .directory, implicitDirectories.remove(key) != nil else { throw badArchive() }
            }
            kinds[key] = entry.kind
        }
        let sortedRanges = try entries.map { entry -> (UInt64, UInt64) in
            let (dataOffset, endOffset) = try localData(for: entry)
            guard entry.localOffset < centralOffset, dataOffset <= endOffset else { throw badArchive() }
            return (entry.localOffset, endOffset)
        }.sorted { $0.0 < $1.0 }
        for pair in zip(sortedRanges, sortedRanges.dropFirst()) where pair.0.1 > pair.1.0 { throw badArchive() }
        for entry in entries where entry.kind == .symlink {
            guard entry.path.hasPrefix("java/"), entry.method == 0, entry.uncompressedSize <= 4096 else { throw badArchive() }
            let targetData = try entryData(for: entry, limit: 4096)
            guard let target = String(data: targetData, encoding: .utf8) else { throw badArchive() }
            let resolved = try Self.resolveJavaLink(path: entry.path, target: target)
            let targetKey = resolved.precomposedStringWithCanonicalMapping.lowercased(with: Locale(identifier: "en_US_POSIX"))
            guard let targetEntry = entries.first(where: { $0.path.precomposedStringWithCanonicalMapping.lowercased(with: Locale(identifier: "en_US_POSIX")) == targetKey }), targetEntry.kind != .symlink else { throw badArchive() }
            guard !entries.contains(where: { $0.path.hasPrefix(entry.path + "/") }) else { throw badArchive() }
        }
    }

    func validateInstanceEntries() throws {
        let roots = Set(entries.compactMap { $0.path.split(separator: "/").first.map(String.init) })
        guard roots.contains("data.json"), roots.contains("java"), roots.contains("minecraft"),
              entries.contains(where: { $0.path == "java" && $0.kind == .directory }),
              entries.contains(where: { $0.path == "minecraft" && $0.kind == .directory }) else { throw badArchive() }
        // Instance backups contain the full profile folder. Root-level modpack metadata and
        // user-added files are part of that contract; tree/path validation above still applies.
    }

    func validateWorldEntries() throws {
        for entry in entries {
            guard entry.path == "data.json" || entry.path == "world" || entry.path.hasPrefix("world/") else { throw badArchive() }
            guard entry.kind != .symlink else { throw badArchive() }
        }
        guard entries.contains(where: { $0.path == "world" && $0.kind == .directory }) else { throw badArchive() }
    }

    func data(for path: String, limit: Int) throws -> Data {
        guard let entry = entries.first(where: { $0.path == path }), entry.kind == .file,
              entry.uncompressedSize <= UInt64(limit) else { throw badArchive() }
        return try entryData(for: entry, limit: limit)
    }

    private func entryData(for entry: ZIPEntry, limit: Int) throws -> Data {
        let (offset, _) = try localData(for: entry)
        let result = try readEntryData(entry, offset: offset, limit: limit)
        guard crc(result) == entry.crc else { throw badArchive() }
        return result
    }

    func extract(
        into staging: URL,
        availableAt root: URL,
        totalExpected: UInt64,
        capacityCheck: @Sendable (URL) throws -> UInt64
    ) throws {
        var links: [(URL, String)] = []
        var actual: UInt64 = 0
        for entry in entries {
            try Task.checkCancellation()
            let target = try InstanceStorage.containedURL(entry.path, in: staging)
            switch entry.kind {
            case .directory:
                try ensureDirectory(target)
            case .file:
                try ensureDirectory(target.deletingLastPathComponent())
                let mode: mode_t = entry.mode & 0o111 == 0 ? 0o644 : 0o755
                let emitted = try extract(entry, to: target, availableAt: root, maxOutput: totalExpected - actual, capacityCheck: capacityCheck, mode: mode)
                actual += emitted
            case .symlink:
                let data = try entryData(for: entry, limit: 4096)
                guard let text = String(data: data, encoding: .utf8) else { throw badArchive() }
                links.append((target, text))
                actual += entry.uncompressedSize
            }
        }
        guard actual == totalExpected else { throw badArchive() }
        for (path, target) in links {
            try Task.checkCancellation()
            guard symlink(target, path.path) == 0 else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
        }
    }

    private func extract(
        _ entry: ZIPEntry,
        to target: URL,
        availableAt root: URL,
        maxOutput: UInt64,
        capacityCheck: @Sendable (URL) throws -> UInt64,
        mode: mode_t = 0o600
    ) throws -> UInt64 {
        let (dataOffset, _) = try localData(for: entry)
        guard entry.uncompressedSize <= maxOutput else { throw notEnoughSpace() }
        if entry.kind == .directory { return 0 }
        if entry.kind == .symlink {
            let bytes = try readEntryData(entry, offset: dataOffset, limit: 4096)
            guard crc(bytes) == entry.crc else { throw badArchive() }
            return UInt64(bytes.count)
        }
        let fd = open(target.path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, mode)
        guard fd >= 0 else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
        defer { _ = close(fd) }
        var outputCount: UInt64 = 0
        var checksum = crc32(0, nil, 0)
        var remaining = entry.compressedSize
        var offset = dataOffset
        var buffer = [UInt8](repeating: 0, count: RestoreArchive.chunkSize)

        func emit(_ bytes: UnsafeBufferPointer<UInt8>, count: Int) throws {
            guard count >= 0, UInt64(count) <= entry.uncompressedSize - outputCount,
                  UInt64(count) <= maxOutput - outputCount else { throw badArchive() }
            if count == 0 { return }
            var written = 0
            while written < count {
                let result = write(fd, bytes.baseAddress!.advanced(by: written), count - written)
                guard result > 0 else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
                written += result
            }
            checksum = bytes.baseAddress!.withMemoryRebound(to: Bytef.self, capacity: count) { crc32(checksum, $0, uInt(count)) }
            outputCount += UInt64(count)
            if outputCount % (16 * 1024 * 1024) < UInt64(count), try capacityCheck(root) < UInt64(count) {
                throw notEnoughSpace()
            }
        }

        if entry.method == 0 {
            guard entry.compressedSize == entry.uncompressedSize else { throw badArchive() }
            while remaining > 0 {
                try Task.checkCancellation()
                let count = Int(min(remaining, UInt64(buffer.count)))
                let bytes = try read(at: offset, count: count)
                try bytes.withUnsafeBytes { raw in
                    try emit(UnsafeBufferPointer(start: raw.bindMemory(to: UInt8.self).baseAddress, count: raw.count), count: raw.count)
                }
                offset += UInt64(count); remaining -= UInt64(count)
            }
        } else if entry.method == 8 {
            var stream = z_stream()
            guard inflateInit2_(&stream, -MAX_WBITS, ZLIB_VERSION, Int32(MemoryLayout<z_stream>.size)) == Z_OK else { throw badArchive() }
            defer { inflateEnd(&stream) }
            var ended = false
            while remaining > 0 {
                try Task.checkCancellation()
                let count = Int(min(remaining, UInt64(RestoreArchive.chunkSize)))
                let bytes = try read(at: offset, count: count)
                offset += UInt64(count); remaining -= UInt64(count)
                try bytes.withUnsafeBytes { input in
                    stream.next_in = UnsafeMutablePointer(mutating: input.bindMemory(to: Bytef.self).baseAddress)
                    stream.avail_in = uInt(input.count)
                    var drain = false
                    repeat {
                        let inputBeforeInflate = stream.avail_in
                        let status = buffer.withUnsafeMutableBytes { output -> Int32 in
                            stream.next_out = output.bindMemory(to: Bytef.self).baseAddress
                            stream.avail_out = uInt(output.count)
                            return inflate(&stream, Z_NO_FLUSH)
                        }
                        let written = buffer.count - Int(stream.avail_out)
                        drain = stream.avail_out == 0
                        try buffer.withUnsafeBufferPointer { try emit($0, count: written) }
                        if status == Z_BUF_ERROR {
                            // A full output buffer can leave inflate needing more compressed input.
                            // Retrying with avail_in == 0 makes zlib report Z_BUF_ERROR again.
                            guard stream.avail_in == 0, written == 0 else { throw badArchive() }
                            drain = false
                            break
                        }
                        guard status == Z_OK || status == Z_STREAM_END else { throw badArchive() }
                        if status == Z_STREAM_END {
                            ended = true
                            guard stream.avail_in == 0, remaining == 0 else { throw badArchive() }
                            break
                        }
                        guard inputBeforeInflate != stream.avail_in || written > 0 || stream.avail_in == 0 else { throw badArchive() }
                    } while stream.avail_in > 0 || drain
                }
                if ended { break }
            }
            guard ended, remaining == 0 else { throw badArchive() }
        } else { throw badArchive() }
        guard outputCount == entry.uncompressedSize, checksum == entry.crc else { throw badArchive() }
        guard fchmod(fd, mode) == 0 else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
        return outputCount
    }

    private func readEntryData(_ entry: ZIPEntry, offset: UInt64, limit: Int) throws -> Data {
        guard entry.compressedSize <= UInt64(limit), entry.uncompressedSize <= UInt64(limit) else { throw badArchive() }
        let data = try read(at: offset, count: Int(entry.compressedSize))
        if entry.method == 0 {
            guard data.count == entry.uncompressedSize else { throw badArchive() }
            return data
        }
        guard entry.method == 8 else { throw badArchive() }
        var stream = z_stream()
        guard inflateInit2_(&stream, -MAX_WBITS, ZLIB_VERSION, Int32(MemoryLayout<z_stream>.size)) == Z_OK else { throw badArchive() }
        defer { inflateEnd(&stream) }
        var result = Data(), output = [UInt8](repeating: 0, count: RestoreArchive.chunkSize)
        try data.withUnsafeBytes { input in
            stream.next_in = UnsafeMutablePointer(mutating: input.bindMemory(to: Bytef.self).baseAddress)
            stream.avail_in = uInt(input.count)
            while true {
                let status = output.withUnsafeMutableBytes { raw -> Int32 in
                    stream.next_out = raw.bindMemory(to: Bytef.self).baseAddress
                    stream.avail_out = uInt(raw.count)
                    return inflate(&stream, Z_NO_FLUSH)
                }
                result.append(contentsOf: output.prefix(output.count - Int(stream.avail_out)))
                guard result.count <= limit else { throw badArchive() }
                if status == Z_STREAM_END {
                    guard stream.avail_in == 0 else { throw badArchive() }
                    break
                }
                guard status == Z_OK else { throw badArchive() }
            }
        }
        guard result.count == entry.uncompressedSize, crc(result) == entry.crc else { throw badArchive() }
        return result
    }

    private func localData(for entry: ZIPEntry) throws -> (UInt64, UInt64) {
        let header = try read(at: entry.localOffset, count: 30)
        var cursor = BinaryCursor(data: header)
        guard try cursor.u32() == 0x04034b50 else { throw badArchive() }
        _ = try cursor.u16()
        let flags = try cursor.u16(), method = try cursor.u16()
        _ = try cursor.u16(); _ = try cursor.u16()
        let localCRC = try cursor.u32(), compressed = try cursor.u32(), uncompressed = try cursor.u32()
        let nameLength = Int(try cursor.u16()), extraLength = Int(try cursor.u16())
        guard flags == entry.flags, method == entry.method else { throw badArchive() }
        let variable = try read(at: entry.localOffset + 30, count: nameLength + extraLength)
        let localName = variable.prefix(nameLength)
        guard Data(localName) == entry.rawName else { throw badArchive() }
        let localExtra = Data(variable.dropFirst(nameLength))
        if entry.flags & 0x0008 == 0 {
            var localCompressed = UInt64(compressed), localUncompressed = UInt64(uncompressed)
            if compressed == UInt32.max || uncompressed == UInt32.max {
                var values = try zip64Extra(localExtra)
                if uncompressed == UInt32.max { localUncompressed = try values.u64() }
                if compressed == UInt32.max { localCompressed = try values.u64() }
            }
            guard localCRC == entry.crc, localCompressed == entry.compressedSize,
                  localUncompressed == entry.uncompressedSize else { throw badArchive() }
        } else {
            guard localCRC == 0 || localCRC == entry.crc,
                  compressed == 0 || compressed == UInt32.max || UInt64(compressed) == entry.compressedSize,
                  uncompressed == 0 || uncompressed == UInt32.max || UInt64(uncompressed) == entry.uncompressedSize else { throw badArchive() }
        }
        let dataOffset = entry.localOffset + 30 + UInt64(nameLength + extraLength)
        guard dataOffset <= centralOffset, entry.compressedSize <= centralOffset - dataOffset else { throw badArchive() }
        var recordEnd = dataOffset + entry.compressedSize
        if entry.flags & 0x0008 != 0 {
            let first = try read(at: recordEnd, count: 4)
            let hasSignature = first.u32LE == 0x08074b50
            if hasSignature { recordEnd += 4 }
            let descriptorSize = entry.compressedSize > UInt32.max || entry.uncompressedSize > UInt32.max ? 20 : 12
            let descriptor = try read(at: recordEnd, count: descriptorSize)
            var values = BinaryCursor(data: descriptor)
            let descriptorCRC = try values.u32()
            let descriptorCompressed = descriptorSize == 12 ? UInt64(try values.u32()) : try values.u64()
            let descriptorUncompressed = descriptorSize == 12 ? UInt64(try values.u32()) : try values.u64()
            guard descriptorCRC == entry.crc, descriptorCompressed == entry.compressedSize,
                  descriptorUncompressed == entry.uncompressedSize,
                  recordEnd + UInt64(descriptorSize) <= centralOffset else { throw badArchive() }
            recordEnd += UInt64(descriptorSize)
        }
        return (dataOffset, recordEnd)
    }

    private func zip64Extra(_ data: Data) throws -> BinaryCursor {
        var extra = BinaryCursor(data: data)
        while extra.remaining >= 4 {
            let tag = try extra.u16(), length = Int(try extra.u16())
            let bytes = try extra.data(length)
            if tag == 1 { return BinaryCursor(data: bytes) }
        }
        throw badArchive()
    }

    private func read(at offset: UInt64, count: Int) throws -> Data {
        guard count >= 0, offset <= fileSize, UInt64(count) <= fileSize - offset else { throw badArchive() }
        try handle.seek(toOffset: offset)
        let data = try handle.read(upToCount: count) ?? Data()
        guard data.count == count else { throw badArchive() }
        return data
    }

    private func ensureDirectory(_ url: URL) throws {
        var current = url
        var missing: [URL] = []
        while !FileManager.default.fileExists(atPath: current.path) {
            missing.append(current); current.deleteLastPathComponent()
        }
        for directory in missing.reversed() {
            guard mkdir(directory.path, 0o755) == 0 || errno == EEXIST else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
            guard chmod(directory.path, 0o755) == 0 else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
        }
    }

    private static func parseEntries(from reader: ZIPReader, count: Int) throws -> [ZIPEntry] {
        var cursor = BinaryCursor(data: try reader.read(at: reader.centralOffset, count: Int(reader.centralSize)))
        var result: [ZIPEntry] = []
        result.reserveCapacity(count)
        for _ in 0..<count {
            guard try cursor.u32() == 0x02014b50 else { throw badArchive() }
            let madeBy = try cursor.u16()
            _ = try cursor.u16()
            let flags = try cursor.u16(), method = try cursor.u16()
            _ = try cursor.u16(); _ = try cursor.u16()
            let crc = try cursor.u32(), compressed32 = try cursor.u32(), uncompressed32 = try cursor.u32()
            let nameLength = Int(try cursor.u16()), extraLength = Int(try cursor.u16()), commentLength = Int(try cursor.u16())
            let diskStart16 = try cursor.u16()
            _ = try cursor.u16()
            let external = try cursor.u32(), offset32 = try cursor.u32()
            let rawName = try cursor.data(nameLength)
            let extras = try cursor.data(extraLength)
            try cursor.skip(commentLength)
            guard diskStart16 == 0 else { throw badArchive() }
            let fields = try extraFields(extras)
            let unicodePath = try unicodePath(in: fields, rawName: rawName)
            let flaggedUTF8 = flags & (1 << 11) != 0
            let decodedPath: String?
            if flaggedUTF8 {
                decodedPath = String(data: rawName, encoding: .utf8)
                if let unicodePath, let decodedPath {
                    guard unicodePath == decodedPath else { throw badArchive() }
                }
            } else if let unicodePath {
                decodedPath = unicodePath
            } else if let utf8Path = String(data: rawName, encoding: .utf8) {
                // Apple's zip writes UTF-8 filenames without setting general-purpose bit 11.
                decodedPath = utf8Path
            } else {
                decodedPath = String(data: rawName, encoding: String.Encoding(rawValue: 0x80000400)) // CP437 fallback.
            }
            guard let rawPath = decodedPath, validPath(rawPath) else { throw badArchive() }
            let allowedFlags: UInt16 = method == 8 ? (1 << 1) | (1 << 2) | (1 << 3) | (1 << 11) : (1 << 3) | (1 << 11)
            guard flags & ~allowedFlags == 0,
                  method == 0 || method == 8 else { throw unsupportedArchive() }
            let mode = UInt16((external >> 16) & 0xffff)
            let unixType = mode & 0xf000
            let directoryName = rawPath.hasSuffix("/")
            let kind: ZIPEntry.Kind
            if unixType == 0xa000 { kind = .symlink }
            else if unixType == 0x4000 || directoryName || external & 0x10 != 0 { kind = .directory }
            else if unixType == 0 || unixType == 0x8000 { kind = .file }
            else { throw unsupportedArchive() }
            guard (kind == .directory) == directoryName || kind == .symlink && !directoryName else { throw badArchive() }
            var compressed = UInt64(compressed32), uncompressed = UInt64(uncompressed32), offset = UInt64(offset32)
            if compressed32 == UInt32.max || uncompressed32 == UInt32.max || offset32 == UInt32.max || diskStart16 == UInt16.max {
                guard let zip64 = fields.first(where: { $0.tag == 1 }) else { throw badArchive() }
                var values = BinaryCursor(data: zip64.data)
                if uncompressed32 == UInt32.max { uncompressed = try values.u64() }
                if compressed32 == UInt32.max { compressed = try values.u64() }
                if offset32 == UInt32.max { offset = try values.u64() }
                if diskStart16 == UInt16.max, try values.u32() != 0 { throw badArchive() }
            }
            if kind == .directory { guard uncompressed == 0, compressed == 0, crc == 0 else { throw badArchive() } }
            if kind == .symlink { guard method == 0, uncompressed <= 4096 else { throw unsupportedArchive() } }
            _ = madeBy
            let canonicalPath = rawPath.precomposedStringWithCanonicalMapping
            result.append(.init(path: directoryName ? String(canonicalPath.dropLast()) : canonicalPath, rawName: rawName, kind: kind,
                                method: method, flags: flags, crc: crc, compressedSize: compressed,
                                uncompressedSize: uncompressed, localOffset: offset, mode: mode))
        }
        guard cursor.remaining == 0 else { throw badArchive() }
        return result
    }

    private static func validPath(_ path: String) -> Bool {
        guard !path.isEmpty, !path.contains("\0"), !path.contains("\\"), !path.hasPrefix("/"),
              !path.hasPrefix("~"), !path.contains(":") else { return false }
        let body = path.hasSuffix("/") ? path.dropLast() : Substring(path)
        let parts = body.split(separator: "/", omittingEmptySubsequences: false)
        guard !parts.isEmpty else { return false }
        return parts.allSatisfy { !$0.isEmpty && $0 != "." && $0 != ".." }
    }

    private static func extraFields(_ data: Data) throws -> [(tag: UInt16, data: Data)] {
        var cursor = BinaryCursor(data: data)
        var fields: [(tag: UInt16, data: Data)] = []
        var recognized = Set<UInt16>()
        while cursor.remaining > 0 {
            guard cursor.remaining >= 4 else { throw badArchive() }
            let tag = try cursor.u16(), length = Int(try cursor.u16())
            let value = try cursor.data(length)
            if tag == 1 || tag == 0x7075 {
                guard recognized.insert(tag).inserted else { throw badArchive() }
            }
            fields.append((tag, value))
        }
        return fields
    }

    private static func unicodePath(in fields: [(tag: UInt16, data: Data)], rawName: Data) throws -> String? {
        guard let field = fields.first(where: { $0.tag == 0x7075 }) else { return nil }
        guard field.data.count >= 5 else { throw badArchive() }
        var cursor = BinaryCursor(data: field.data)
        guard try cursor.data(1)[0] == 1 else { return nil }
        guard try cursor.u32() == checksumForData(rawName) else { return nil }
        let path = try cursor.data(cursor.remaining)
        guard let decoded = String(data: path, encoding: .utf8) else { return nil }
        return decoded
    }

    private static func checksumForData(_ data: Data) -> UInt32 {
        var value = UInt32.max
        for byte in data {
            value ^= UInt32(byte)
            for _ in 0..<8 { value = (value >> 1) ^ (0xedb8_8320 & (0 &- (value & 1))) }
        }
        return ~value
    }

    private static func resolveJavaLink(path: String, target: String) throws -> String {
        guard !target.isEmpty, !target.hasPrefix("/"), !target.contains("\\"), !target.contains("\0"), !target.contains(":"),
              !target.hasPrefix("~") else { throw badArchive() }
        var parts = path.split(separator: "/").dropLast().map(String.init)
        for part in target.split(separator: "/", omittingEmptySubsequences: false) {
            if part.isEmpty || part == "." { continue }
            if part == ".." {
                guard parts.count > 1 else { throw badArchive() }
                parts.removeLast()
            } else { parts.append(String(part)) }
        }
        guard parts.first == "java", parts.count > 1 else { throw badArchive() }
        return parts.joined(separator: "/")
    }

    private func badArchive() -> InstanceFileError { Self.badArchive() }
    private func unsupportedArchive() -> InstanceFileError { Self.unsupportedArchive() }
    private func notEnoughSpace() -> InstanceFileError { .message(String(appLocalized: "Для распаковки архива недостаточно свободного места.")) }
    private static func badArchive() -> InstanceFileError { .message(String(appLocalized: "ZIP-архив повреждён или содержит небезопасные файлы.")) }
    private static func unsupportedArchive() -> InstanceFileError { .message(String(appLocalized: "ZIP-архив использует неподдерживаемое сжатие, шифрование или тип файла.")) }
    private func crc(_ data: Data) -> UInt32 { Self.crc(data) }
    private static func crc(_ data: Data) -> UInt32 {
        var result: UInt32 = 0
        data.withUnsafeBytes { raw in
            let bytes = raw.bindMemory(to: Bytef.self)
            let value = crc32(uLong(0), bytes.baseAddress, uInt(bytes.count))
            result = UInt32(truncatingIfNeeded: value)
        }
        return result
    }
}

nonisolated private struct BinaryCursor {
    let data: Data
    var offset = 0
    var remaining: Int { data.count - offset }

    init(data: Data, offset: Int = 0) { self.data = data; self.offset = offset }

    mutating func u16() throws -> UInt16 {
        let bytes = try self.data(2)
        return UInt16(bytes[0]) | UInt16(bytes[1]) << 8
    }

    mutating func u32() throws -> UInt32 {
        let bytes = try self.data(4)
        return UInt32(bytes[0]) | UInt32(bytes[1]) << 8 | UInt32(bytes[2]) << 16 | UInt32(bytes[3]) << 24
    }

    mutating func u64() throws -> UInt64 {
        UInt64(try u32()) | UInt64(try u32()) << 32
    }

    mutating func data(_ count: Int) throws -> Data {
        guard count >= 0, count <= remaining else { throw InstanceFileError.message(String(appLocalized: "ZIP-архив повреждён или содержит небезопасные файлы.")) }
        defer { offset += count }
        return data.subdata(in: offset..<(offset + count))
    }

    mutating func skip(_ count: Int) throws { _ = try data(count) }
}

nonisolated private extension Data {
    var u32LE: UInt32 {
        UInt32(self[0]) | UInt32(self[1]) << 8 | UInt32(self[2]) << 16 | UInt32(self[3]) << 24
    }

    func endRecordIndex() -> Int? {
        guard count >= 22 else { return nil }
        for index in stride(from: count - 22, through: 0, by: -1) {
            guard self[index] == 0x50, self[index + 1] == 0x4b, self[index + 2] == 0x05, self[index + 3] == 0x06 else { continue }
            let commentLength = Int(self[index + 20]) | Int(self[index + 21]) << 8
            if index + 22 + commentLength == count { return index }
        }
        return nil
    }
}

nonisolated private struct MachOArchitecture {
    let types: Set<UInt32>

    init?(bytes: Data) {
        guard bytes.count >= 8 else { return nil }
        let magic = Array(bytes.prefix(4))
        if magic == [0xfe, 0xed, 0xfa, 0xce] || magic == [0xce, 0xfa, 0xed, 0xfe] || magic == [0xfe, 0xed, 0xfa, 0xcf] || magic == [0xcf, 0xfa, 0xed, 0xfe] {
            let is64Bit = magic == [0xfe, 0xed, 0xfa, 0xcf] || magic == [0xcf, 0xfa, 0xed, 0xfe]
            guard bytes.count >= (is64Bit ? 32 : 28) else { return nil }
            let little = magic == [0xce, 0xfa, 0xed, 0xfe] || magic == [0xcf, 0xfa, 0xed, 0xfe]
            let raw = Array(bytes[4..<8])
            let cpu = little ? UInt32(raw[0]) | UInt32(raw[1]) << 8 | UInt32(raw[2]) << 16 | UInt32(raw[3]) << 24 : UInt32(raw[3]) | UInt32(raw[2]) << 8 | UInt32(raw[1]) << 16 | UInt32(raw[0]) << 24
            types = [cpu]
            return
        }
        if magic == [0xca, 0xfe, 0xba, 0xbe] || magic == [0xbe, 0xba, 0xfe, 0xca] || magic == [0xca, 0xfe, 0xba, 0xbf] || magic == [0xbf, 0xba, 0xfe, 0xca] {
            let is64Bit = magic == [0xca, 0xfe, 0xba, 0xbf] || magic == [0xbf, 0xba, 0xfe, 0xca]
            let little = magic == [0xbe, 0xba, 0xfe, 0xca] || magic == [0xbf, 0xba, 0xfe, 0xca]
            let raw = Array(bytes[4..<8])
            let count = little ? UInt32(raw[0]) | UInt32(raw[1]) << 8 | UInt32(raw[2]) << 16 | UInt32(raw[3]) << 24 : UInt32(raw[3]) | UInt32(raw[2]) << 8 | UInt32(raw[1]) << 16 | UInt32(raw[0]) << 24
            let recordSize = is64Bit ? 32 : 20
            guard count > 0, count <= 64, bytes.count >= 8 + Int(count) * recordSize else { return nil }
            var values = Set<UInt32>()
            for index in 0..<Int(count) {
                let start = 8 + index * recordSize, rawCPU = Array(bytes[start..<(start + 4)])
                let cpu = little ? UInt32(rawCPU[0]) | UInt32(rawCPU[1]) << 8 | UInt32(rawCPU[2]) << 16 | UInt32(rawCPU[3]) << 24 : UInt32(rawCPU[3]) | UInt32(rawCPU[2]) << 8 | UInt32(rawCPU[1]) << 16 | UInt32(rawCPU[0]) << 24
                values.insert(cpu)
            }
            guard !values.isEmpty else { return nil }
            types = values
            return
        }
        return nil
    }

    func contains(_ cpu: cpu_type_t) -> Bool { types.contains(UInt32(bitPattern: cpu)) }
}

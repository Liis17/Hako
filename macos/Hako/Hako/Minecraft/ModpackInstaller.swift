import Darwin
import Foundation

actor ModpackInstaller {
    private let session: URLSession
    private let cache: URL

    init(session: URLSession = URLSession(configuration: .ephemeral), cache: URL = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0].appendingPathComponent("Hako/Modpacks")) {
        self.session = session
        self.cache = cache
    }

    func prepare(project: ModrinthProject, version: ModrinthVersion, minecraft: MojangClient, fabric: FabricClient) async throws -> PreparedModpack {
        guard project.projectType == "modpack", version.projectID == project.id, let file = version.modpackFile else { throw ModpackError.invalidManifest }
        let archive = try await cachedArchive(file)
        let contents = try await ModpackArchive.inspect(archive)
        let manifest = contents.manifest
        guard version.gameVersions.contains(manifest.minecraft), version.loaders.contains(manifest.loaderVersion == nil ? "minecraft" : "fabric") else { throw ModpackError.invalidManifest }
        let catalog = try await minecraft.catalog()
        guard let game = catalog.versions.first(where: { $0.id == manifest.minecraft }) else { throw ModpackError.invalidManifest }
        let prepared = try await minecraft.prepare(game)
        if let loader = manifest.loaderVersion {
            guard try await fabric.loaderVersions(minecraft: game.id).contains(where: { $0.version == loader }) else { throw ModpackError.invalidManifest }
            _ = try await fabric.profile(minecraft: game.id, loader: loader)
        }
        try Task.checkCancellation()
        let plan = ModpackPlan(projectID: project.id, versionID: version.id, versionNumber: version.number, pageURL: project.pageURL, archive: file, manifest: manifest)
        return PreparedModpack(plan: plan, minecraft: prepared)
    }

    func icon(_ url: URL?) async -> Data? {
        guard let url, url.scheme == "https" else { return nil }
        do {
            let (temporary, response) = try await session.download(from: url)
            defer { try? FileManager.default.removeItem(at: temporary) }
            guard let response = response as? HTTPURLResponse, (200..<300).contains(response.statusCode),
                  (try temporary.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0) <= 8_388_608 else { return nil }
            try Task.checkCancellation()
            return try InstanceIconImage.normalized(temporary)
        } catch { return nil }
    }

    func validate(in root: URL, minecraft: String, fabric: FabricConfiguration?) throws {
        guard let plan = try ModpackPlan.read(in: root) else { return }
        guard plan.manifest.minecraft == minecraft, plan.manifest.loaderVersion == fabric?.loaderVersion,
              fabric?.api == nil else { throw ModpackError.invalidManifest }
    }

    func install(in root: URL, progress: @escaping @Sendable (InstallationProgress) async -> Void) async throws {
        guard var plan = try ModpackPlan.read(in: root), !plan.completed else { return }
        await progress(.init(stage: String(appLocalized: "Подготавливаем модпак…")))
        let archive = try await cachedArchive(plan.archive)
        let contents = try await ModpackArchive.inspect(archive)
        guard contents.manifest == plan.manifest else { throw ModpackError.invalidManifest }
        let stage = try InstanceStorage.containedURL(".hako-modpack-" + UUID().uuidString, in: root)
        try FileManager.default.createDirectory(at: stage, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: stage) }
        let files = plan.manifest.files.filter(\.installOnClient)
        let total = files.reduce(Int64(0)) { $0 + $1.fileSize }
        var completed: Int64 = 0
        let fileCache = cache.appendingPathComponent("files")
        try FileManager.default.createDirectory(at: fileCache, withIntermediateDirectories: true)
        await progress(.init(stage: String(appLocalized: "Скачиваем файлы модпака…"), totalBytes: total))
        try await withThrowingTaskGroup(of: Int64.self) { group in
            var iterator = files.makeIterator()
            func schedule(_ file: ModpackManifest.File) {
                group.addTask { [session] in
                    try await Self.stage(file, at: stage, cache: fileCache, session: session)
                    return file.fileSize
                }
            }
            for _ in 0..<4 { if let file = iterator.next() { schedule(file) } }
            while let size = try await group.next() {
                completed += size
                await progress(.init(stage: String(appLocalized: "Скачиваем файлы модпака…"), completedBytes: completed, totalBytes: total))
                try Task.checkCancellation()
                if let file = iterator.next() { schedule(file) }
            }
        }
        var stagedPaths = Set(files.map(\.path))
        var extractedBytes: Int64 = 0
        for (path, entry) in contents.overrides.sorted(by: { $0.key < $1.key }) {
            let destination = try InstanceStorage.containedURL(path, in: stage)
            try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
            try await ModpackArchive.extract(entry, in: archive, to: destination, limit: min(ModpackArchive.fileLimit, ModpackArchive.totalLimit - total - extractedBytes))
            extractedBytes += Int64(try destination.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0)
            stagedPaths.insert(path)
        }
        try Task.checkCancellation()
        await progress(.init(stage: String(appLocalized: "Устанавливаем содержимое модпака…"), completedBytes: completed, totalBytes: total))
        let minecraft = try InstanceStorage.containedURL("minecraft", in: root)
        // Validate every destination before publishing any content.
        for path in stagedPaths { _ = try Self.destination(path, in: minecraft) }
        for path in stagedPaths.sorted() {
            try Task.checkCancellation()
            let source = try InstanceStorage.containedURL(path, in: stage)
            let destination = try Self.destination(path, in: minecraft)
            try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
            if FileManager.default.fileExists(atPath: destination.path) {
                _ = try FileManager.default.replaceItemAt(destination, withItemAt: source)
            } else { try FileManager.default.moveItem(at: source, to: destination) }
        }
        try Task.checkCancellation()
        plan.completed = true
        try plan.write(in: root)
    }

    nonisolated private static func destination(_ path: String, in root: URL) throws -> URL {
        try ModpackPaths.validate(path)
        guard (try? root.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) != true else { throw ModpackError.unsafePath }
        var raw = root
        let parts = path.split(separator: "/")
        for (index, part) in parts.enumerated() {
            raw.appendPathComponent(String(part))
            let values: URLResourceValues
            do { values = try raw.resourceValues(forKeys: [.isSymbolicLinkKey, .isDirectoryKey, .isRegularFileKey]) }
            catch let error as CocoaError where error.code == .fileReadNoSuchFile || error.code == .fileNoSuchFile { continue }
            guard values.isSymbolicLink != true,
                  index == parts.count - 1 ? values.isRegularFile == true : values.isDirectory == true else { throw ModpackError.unsafePath }
        }
        return try InstanceStorage.containedURL(path, in: root)
    }

    @concurrent private static func stage(_ file: ModpackManifest.File, at root: URL, cache: URL, session: URLSession) async throws {
        let sha1 = file.hashes["sha1"]!, sha512 = file.hashes["sha512"]!.lowercased()
        let cached = try InstanceStorage.containedURL(sha512, in: cache)
        if try !valid(cached, size: file.fileSize, sha1: sha1, sha512: sha512) {
            let temporary = try await download(file.downloads, size: file.fileSize, sha1: sha1, sha512: sha512, session: session)
            defer { try? FileManager.default.removeItem(at: temporary) }
            // Two paths may reference the same content hash concurrently.
            let partial = cache.appendingPathComponent(UUID().uuidString)
            defer { try? FileManager.default.removeItem(at: partial) }
            try FileManager.default.copyItem(at: temporary, to: partial)
            if link(partial.path, cached.path) != 0 {
                guard errno == EEXIST else { throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
                if try !valid(cached, size: file.fileSize, sha1: sha1, sha512: sha512) {
                    _ = try FileManager.default.replaceItemAt(cached, withItemAt: partial)
                }
            }
        }
        try Task.checkCancellation()
        let target = try InstanceStorage.containedURL(file.path, in: root)
        try FileManager.default.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.copyItem(at: cached, to: target)
    }

    private func cachedArchive(_ file: ModrinthVersion.File) async throws -> URL {
        guard file.isInstallable(extension: ".mrpack"), file.size <= ModpackArchive.fileLimit else { throw ModpackError.invalidManifest }
        let target = try InstanceStorage.containedURL(file.sha512 + ".mrpack", in: cache)
        if try Self.valid(target, size: file.size, sha1: file.sha1, sha512: file.sha512) { return target }
        try FileManager.default.createDirectory(at: cache, withIntermediateDirectories: true)
        let temporary = try await Self.download([file.url], size: file.size, sha1: file.sha1, sha512: file.sha512, session: session)
        defer { try? FileManager.default.removeItem(at: temporary) }
        if FileManager.default.fileExists(atPath: target.path) { try FileManager.default.removeItem(at: target) }
        try FileManager.default.moveItem(at: temporary, to: target)
        return target
    }

    @concurrent private static func download(_ urls: [URL], size: Int64, sha1: String, sha512: String, session: URLSession) async throws -> URL {
        var lastError: any Error = ModpackError.damagedFile(urls.first?.lastPathComponent ?? "")
        for url in urls {
            try Task.checkCancellation()
            do {
                guard url.scheme == "https" else { throw ModpackError.invalidManifest }
                var request = URLRequest(url: url)
                request.timeoutInterval = 120
                request.setValue("Hako/\(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0") (https://github.com/Liis17/Hako)", forHTTPHeaderField: "User-Agent")
                let (temporary, response) = try await session.download(for: request)
                do {
                    guard let response = response as? HTTPURLResponse, response.url?.scheme == "https", (200..<300).contains(response.statusCode),
                          try valid(temporary, size: size, sha1: sha1, sha512: sha512) else { throw ModpackError.damagedFile(url.lastPathComponent) }
                    try Task.checkCancellation()
                    return temporary
                } catch {
                    try? FileManager.default.removeItem(at: temporary)
                    throw error
                }
            } catch {
                try Task.checkCancellation()
                lastError = error
            }
        }
        throw lastError
    }

    nonisolated private static func valid(_ file: URL, size: Int64, sha1: String, sha512: String) throws -> Bool {
        guard FileManager.default.fileExists(atPath: file.path),
              try file.resourceValues(forKeys: [.fileSizeKey]).fileSize.map(Int64.init) == size else { return false }
        return try MojangIntegrity.validFile(file, download: .init(url: file, sha1: sha1, size: size)) && FabricClient.hashFile(file) == sha512.lowercased()
    }
}

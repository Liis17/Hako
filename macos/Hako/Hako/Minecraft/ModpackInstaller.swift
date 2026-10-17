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

    private func cachedArchive(_ file: ModrinthVersion.File) async throws -> URL {
        guard file.isInstallable(extension: ".mrpack") else { throw ModpackError.invalidManifest }
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

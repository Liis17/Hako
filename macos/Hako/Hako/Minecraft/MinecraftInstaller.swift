import Foundation
import CryptoKit

nonisolated struct InstallationProgress: Sendable {
    var stage: String
    var completedBytes: Int64 = 0
    var totalBytes: Int64 = 0
    var fraction: Double { totalBytes > 0 ? min(1, Double(completedBytes) / Double(totalBytes)) : 0 }
}

nonisolated struct InstallationResult: Sendable {
    let javaMajorVersion: Int
    let javaExecutable: String
    let legacyTexturepacks: Bool
    var fabricProfileSHA1: String? = nil
}

actor MinecraftInstaller {
    private struct File: Sendable {
        let download: MojangDownload
        let path: String
        var executable = false
    }
    private let client: MojangClient
    private let session: URLSession
    private let assetBaseURL: URL
    private let fabricClient: FabricClient
    private let content: InstanceContent

    init(client: MojangClient = MojangClient(), session: URLSession = URLSession(configuration: .ephemeral), assetBaseURL: URL = URL(string: "https://resources.download.minecraft.net")!, fabricClient: FabricClient = .init(), content: InstanceContent = .init()) {
        self.client = client
        self.session = session
        self.assetBaseURL = assetBaseURL
        self.fabricClient = fabricClient; self.content = content
    }

    func install(_ version: MinecraftVersion, at root: URL, platform: MinecraftPlatform = .current, fabric: FabricConfiguration? = nil, progress: @escaping @Sendable (InstallationProgress) async -> Void) async throws -> InstallationResult {
        await progress(.init(stage: String(appLocalized: "Проверяем файлы версии…")))
        let prepared = try await client.prepare(version, platform: platform)
        let runtimeData = try await client.data(for: prepared.runtime.manifest)
        let runtime = try JSONDecoder().decode(JavaRuntimeManifest.self, from: runtimeData)
        let javaRoot = try InstanceStorage.containedURL("java", in: root)
        var files: [File] = []
        for (path, entry) in runtime.files {
            _ = try InstanceStorage.containedURL(path, in: javaRoot)
            switch entry.type {
            case "file":
                guard let raw = entry.downloads?["raw"] else { throw MojangError.invalid(String(appLocalized: "В описании Java отсутствует файл загрузки.")) }
                files.append(File(download: raw, path: "java/\(path)", executable: entry.executable == true))
            case "directory": break
            case "link":
                guard let target = entry.target else { throw MojangError.invalid(String(appLocalized: "В описании Java отсутствует цель ссылки.")) }
                _ = try Self.linkDestination(path: path, target: target, javaRoot: javaRoot)
            default: throw MojangError.invalid(String(appLocalized: "Неизвестный формат файла Java."))
            }
        }
        guard let executable = runtime.files.keys.sorted().first(where: { $0.hasSuffix("/bin/java") || $0 == "bin/java" }) else {
            throw MojangError.invalid(String(appLocalized: "В комплекте Java отсутствует исполняемый файл."))
        }
        let manifest = prepared.manifest
        let versionDirectory = "minecraft/versions/\(version.id)"
        let clientFile = manifest.downloads["client"]!
        files.append(.init(download: clientFile, path: "\(versionDirectory)/\(version.id).jar"))
        for library in prepared.libraries { files.append(.init(download: library.download, path: "minecraft/libraries/\(library.path)")) }
        var fabricSHA1: String?
        if let fabric {
            await progress(.init(stage: String(appLocalized: "Подготавливаем Fabric…")))
            let (profile, data) = try await fabricClient.profile(minecraft: version.id, loader: fabric.loaderVersion)
            for library in try await fabricClient.libraries(profile) { files.append(.init(download: library.download, path: "minecraft/libraries/\(library.path)")) }
            try Self.write(data, relativePath: "minecraft/.hako-fabric.json", root: root)
            fabricSHA1 = Insecure.SHA1.hash(data: data).map { String(format: "%02x", $0) }.joined()
        }
        if let logging = manifest.logging?["client"] {
            files.append(.init(download: logging.file, path: "minecraft/assets/log_configs/\(logging.file.id ?? logging.file.url.lastPathComponent)"))
        }
        var assets: MinecraftAssetIndex?
        if let index = manifest.assetIndex {
            await progress(.init(stage: String(appLocalized: "Получаем список ресурсов…")))
            let data = try await client.data(for: index.download)
            assets = try JSONDecoder().decode(MinecraftAssetIndex.self, from: data)
            for (name, object) in assets!.objects {
                guard object.hash.count == 40, object.hash.allSatisfy({ $0.isHexDigit }), object.size >= 0 else { throw MojangError.invalid(String(appLocalized: "Некорректный ресурс в описании игры.")) }
                let path = "\(object.hash.prefix(2))/\(object.hash)"
                files.append(.init(download: .init(url: assetBaseURL.appendingPathComponent(path), sha1: object.hash, size: object.size), path: "minecraft/assets/objects/\(path)"))
                if assets?.virtual == true { _ = try InstanceStorage.containedURL("minecraft/assets/virtual/\(index.id)/\(name)", in: root) }
                if assets?.map_to_resources == true { _ = try InstanceStorage.containedURL("minecraft/resources/\(name)", in: root) }
            }
            try Self.write(data, relativePath: "minecraft/assets/indexes/\(index.id).json", root: root)
        }
        var unique: [String: File] = [:]
        for file in files {
            _ = try InstanceStorage.containedURL(file.path, in: root)
            if let previous = unique[file.path], previous.download.sha1 != file.download.sha1 { throw MojangError.invalid(String(appLocalized: "Конфликт файлов в описании версии.")) }
            unique[file.path] = file
        }
        try Self.write(prepared.manifestData, relativePath: "\(versionDirectory)/\(version.id).json", root: root)
        try Self.write(runtimeData, relativePath: "java/runtime-manifest.json", root: root)
        let ordered = unique.values.sorted { $0.path < $1.path }
        let total = ordered.reduce(Int64(0)) { $0 + ($1.download.size ?? 0) }
        var completed: Int64 = 0
        await progress(.init(stage: String(appLocalized: "Скачиваем Java и Minecraft…"), totalBytes: total))
        try await withThrowingTaskGroup(of: Int64.self) { group in
            var iterator = ordered.makeIterator()
            func schedule(_ file: File) {
                group.addTask { [session] in
                    try await Self.download(file, root: root, session: session)
                    return file.download.size ?? 0
                }
            }
            for _ in 0..<4 { if let file = iterator.next() { schedule(file) } }
            while let size = try await group.next() {
                completed += size
                await progress(.init(stage: String(appLocalized: "Скачиваем Java и Minecraft…"), completedBytes: completed, totalBytes: total))
                try Task.checkCancellation()
                if let file = iterator.next() { schedule(file) }
            }
        }
        await progress(.init(stage: String(appLocalized: "Подготавливаем Java и библиотеки…"), completedBytes: completed, totalBytes: total))
        for (path, entry) in runtime.files.sorted(by: { $0.key < $1.key }) {
            try Task.checkCancellation()
            let url = try InstanceStorage.containedURL(path, in: javaRoot)
            if entry.type == "directory" { try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true) }
            if entry.type == "link", let target = entry.target {
                _ = try Self.linkDestination(path: path, target: target, javaRoot: javaRoot)
                try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
                // Работать с самой ссылкой, а не с её разрешённой целью.
                let link = javaRoot.appendingPathComponent(path)
                if (try? FileManager.default.destinationOfSymbolicLink(atPath: link.path)) == target { continue }
                if FileManager.default.fileExists(atPath: link.path) || (try? FileManager.default.destinationOfSymbolicLink(atPath: link.path)) != nil { try FileManager.default.removeItem(at: link) }
                try FileManager.default.createSymbolicLink(atPath: link.path, withDestinationPath: target)
            }
        }
        for library in prepared.libraries {
            if let excludes = library.extractionExcludes {
                let archive = try InstanceStorage.containedURL("minecraft/libraries/\(library.path)", in: root)
                try Self.extractNatives(archive, root: root, excludes: excludes)
            }
        }
        if let assets, let index = manifest.assetIndex {
            await progress(.init(stage: String(appLocalized: "Подготавливаем ресурсы…"), completedBytes: completed, totalBytes: total))
            for (name, object) in assets.objects {
                try Task.checkCancellation()
                let source = try InstanceStorage.containedURL("minecraft/assets/objects/\(object.hash.prefix(2))/\(object.hash)", in: root)
                var destinations: [String] = []
                if assets.virtual == true { destinations.append("minecraft/assets/virtual/\(index.id)/\(name)") }
                if assets.map_to_resources == true { destinations.append("minecraft/resources/\(name)") }
                for path in destinations {
                    let destination = try InstanceStorage.containedURL(path, in: root)
                    if try MojangIntegrity.validFile(destination, download: .init(url: assetBaseURL, sha1: object.hash, size: object.size)) { continue }
                    try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
                    if FileManager.default.fileExists(atPath: destination.path) { try FileManager.default.removeItem(at: destination) }
                    try FileManager.default.copyItem(at: source, to: destination)
                }
            }
        }
        for path in ["minecraft/mods", "minecraft/\(manifest.legacyTexturepacks ? "texturepacks" : "resourcepacks")"] {
            try FileManager.default.createDirectory(at: try InstanceStorage.containedURL(path, in: root), withIntermediateDirectories: true)
        }
        guard FileManager.default.isExecutableFile(atPath: try InstanceStorage.containedURL(executable, in: javaRoot).path) else { throw MojangError.invalid(String(appLocalized: "Не удалось подготовить Java к запуску.")) }
        if let fabric {
            let folder = try InstanceStorage.containedURL("minecraft/mods", in: root)
            if try await !content.apiWasProvisioned(in: folder) {
                await progress(.init(stage: String(appLocalized: "Устанавливаем Fabric API…"), completedBytes: completed, totalBytes: total))
                let metadata = try await fabricClient.metadata(for: fabric.api)
                guard try metadata.supports(loader: fabric.loaderVersion, java: manifest.java.majorVersion) else { throw MojangError.unsupported(String(appLocalized: "Fabric API несовместим с выбранным Loader или Java.")) }
                let cached = try await fabricClient.cachedAPI(fabric.api)
                try Task.checkCancellation()
                try await content.provisionAPI(fabric.api, from: cached, in: folder)
            }
        }
        await progress(.init(stage: String(appLocalized: "Готово"), completedBytes: total, totalBytes: total))
        return InstallationResult(javaMajorVersion: manifest.java.majorVersion, javaExecutable: executable, legacyTexturepacks: manifest.legacyTexturepacks, fabricProfileSHA1: fabricSHA1)
    }

    @concurrent private static func download(_ file: File, root: URL, session: URLSession) async throws {
        try Task.checkCancellation()
        let target = try InstanceStorage.containedURL(file.path, in: root)
        if try MojangIntegrity.validFile(target, download: file.download) {
            if file.executable { try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: target.path) }
            return
        }
        guard file.download.url.scheme == "https" else { throw MojangError.invalid(String(appLocalized: "Ссылка загрузки должна использовать HTTPS.")) }
        let (temporary, response) = try await session.download(from: file.download.url)
        defer { try? FileManager.default.removeItem(at: temporary) }
        guard let response = response as? HTTPURLResponse else { throw MojangError.invalid(String(appLocalized: "Некорректный ответ сервера.")) }
        guard (200..<300).contains(response.statusCode) else { throw MojangError.http(response.statusCode) }
        guard try MojangIntegrity.validFile(temporary, download: file.download) else { throw MojangError.invalid(String(appLocalized: "Загруженный файл повреждён. Повторите загрузку.")) }
        try Task.checkCancellation()
        let destination = try InstanceStorage.containedURL(file.path, in: root)
        try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
        let staged = destination.deletingLastPathComponent().appendingPathComponent(".hako-download-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: staged) }
        try FileManager.default.copyItem(at: temporary, to: staged)
        if FileManager.default.fileExists(atPath: destination.path) {
            _ = try FileManager.default.replaceItemAt(destination, withItemAt: staged)
        } else { try FileManager.default.moveItem(at: staged, to: destination) }
        if file.executable { try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: destination.path) }
    }

    private static func write(_ data: Data, relativePath: String, root: URL) throws {
        let url = try InstanceStorage.containedURL(relativePath, in: root)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: url, options: .atomic)
    }

    private static func linkDestination(path: String, target: String, javaRoot: URL) throws -> URL {
        guard !target.hasPrefix("/"), !target.contains("\0"), !target.contains("\\") else { throw MojangError.invalid(String(appLocalized: "Некорректная ссылка в комплекте Java.")) }
        _ = try InstanceStorage.containedURL(path, in: javaRoot)
        let link = javaRoot.appendingPathComponent(path)
        let destination = link.deletingLastPathComponent().appendingPathComponent(target).standardizedFileURL
        let base = javaRoot.standardizedFileURL.path + "/"
        guard destination.path.hasPrefix(base) else { throw MojangError.invalid(String(appLocalized: "Ссылка выходит за пределы папки Java.")) }
        return try InstanceStorage.containedURL(String(destination.path.dropFirst(base.count)), in: javaRoot)
    }

    private static func run(_ executable: String, _ arguments: [String]) throws -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        try process.run()
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        try Task.checkCancellation()
        guard process.terminationStatus == 0 else { throw MojangError.invalid(String(appLocalized: "Не удалось распаковать библиотеки игры.")) }
        return String(decoding: data, as: UTF8.self)
    }

    private static func extractNatives(_ archive: URL, root: URL, excludes: [String]) throws {
        let listing = try run("/usr/bin/unzip", ["-Z1", archive.path])
        let temporary = try InstanceStorage.containedURL("minecraft/.natives-\(UUID().uuidString)", in: root)
        defer { try? FileManager.default.removeItem(at: temporary) }
        for entry in listing.split(separator: "\n") {
            let path = String(entry).trimmingCharacters(in: CharacterSet(charactersIn: "/"))
            guard !entry.hasPrefix("/") else { throw MojangError.invalid(String(appLocalized: "Некорректный путь в библиотеке игры.")) }
            if !path.isEmpty { _ = try InstanceStorage.containedURL(path, in: temporary) }
        }
        _ = try run("/usr/bin/ditto", ["-x", "-k", archive.path, temporary.path])
        guard let files = FileManager.default.enumerator(at: temporary, includingPropertiesForKeys: [.isRegularFileKey, .isSymbolicLinkKey]) else { return }
        for case let file as URL in files {
            let values = try file.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
            guard values.isSymbolicLink != true else { throw MojangError.invalid(String(appLocalized: "В библиотеке игры обнаружена недопустимая ссылка.")) }
            guard values.isRegularFile == true else { continue }
            let path = String(file.path.dropFirst(temporary.path.count + 1))
            if excludes.contains(where: { path.hasPrefix($0) }) { continue }
            let destination = try InstanceStorage.containedURL("minecraft/natives/\(path)", in: root)
            try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
            if FileManager.default.fileExists(atPath: destination.path) { try FileManager.default.removeItem(at: destination) }
            try FileManager.default.copyItem(at: file, to: destination)
        }
    }
}

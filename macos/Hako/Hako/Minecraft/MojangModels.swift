import Foundation
import Darwin

nonisolated enum MinecraftPlatform: String, Sendable {
    case intel, appleSilicon

    static var current: Self {
        #if arch(arm64)
        .appleSilicon
        #else
        var translated: Int32 = 0
        var size = MemoryLayout<Int32>.size
        return sysctlbyname("sysctl.proc_translated", &translated, &size, nil, 0) == 0 && translated == 1 ? .appleSilicon : .intel
        #endif
    }
    var runtimeKey: String { self == .appleSilicon ? "mac-os-arm64" : "mac-os" }
    var architecture: String { self == .appleSilicon ? "arm64" : "x86_64" }
}

nonisolated struct MinecraftVersion: Codable, Hashable, Identifiable, Sendable {
    let id: String
    let type: String
    let url: URL
    let sha1: String
}

nonisolated struct MinecraftCatalog: Decodable, Sendable {
    let latest: [String: String]
    let versions: [MinecraftVersion]
}

nonisolated struct MojangDownload: Codable, Sendable {
    let url: URL
    let sha1: String
    var size: Int64?
    var path: String?
    var id: String?
}

nonisolated struct MinecraftVersionManifest: Decodable, Sendable {
    let id: String
    let downloads: [String: MojangDownload]
    let libraries: [MinecraftLibrary]
    let javaVersion: JavaRequirement?
    let assetIndex: AssetIndexReference?
    let assets: String?
    let logging: [String: MinecraftLogging]?
    let mainClass: String?
    let arguments: [String: [MinecraftArgument]]?
    let minecraftArguments: String?
    let type: String?

    var java: JavaRequirement { javaVersion ?? JavaRequirement(component: "jre-legacy", majorVersion: 8) }
    var legacyTexturepacks: Bool { assets == "pre-1.6" || assetIndex?.id == "pre-1.6" }
}

nonisolated struct JavaRequirement: Decodable, Sendable {
    let component: String
    let majorVersion: Int
}

nonisolated struct MinecraftLogging: Decodable, Sendable {
    let file: MojangDownload
    let argument: String?
}

nonisolated struct MinecraftArgument: Decodable, Sendable {
    let rules: [MinecraftRule]?
    let values: [String]
    private enum CodingKeys: String, CodingKey { case rules, value }

    init(from decoder: Decoder) throws {
        if let string = try? decoder.singleValueContainer().decode(String.self) { rules = nil; values = [string]; return }
        let container = try decoder.container(keyedBy: CodingKeys.self)
        rules = try container.decodeIfPresent([MinecraftRule].self, forKey: .rules)
        if let strings = try? container.decode([String].self, forKey: .value) { values = strings }
        else { values = [try container.decode(String.self, forKey: .value)] }
    }

    func allowed(on platform: MinecraftPlatform, features: [String: Bool]) -> Bool {
        guard let rules, !rules.isEmpty else { return true }
        var allowed = false
        for rule in rules where rule.matches(platform, features: features) { allowed = rule.action == "allow" }
        return allowed
    }
}

nonisolated struct AssetIndexReference: Decodable, Sendable {
    let id: String
    let url: URL
    let sha1: String
    let size: Int64
    var download: MojangDownload { .init(url: url, sha1: sha1, size: size) }
}

nonisolated struct MinecraftAssetIndex: Decodable, Sendable {
    struct Object: Decodable, Sendable { let hash: String; let size: Int64 }
    let objects: [String: Object]
    let virtual: Bool?
    let map_to_resources: Bool?
}

nonisolated struct MinecraftRule: Decodable, Sendable {
    struct OS: Decodable, Sendable {
        struct VersionRange: Decodable, Sendable { let min: String?; let max: String? }
        let name: String?; let arch: String?; let version: String?; let versionRange: VersionRange?
    }
    let action: String
    let os: OS?
    let features: [String: Bool]?

    func matches(_ platform: MinecraftPlatform, features enabled: [String: Bool] = [:]) -> Bool {
        if let name = os?.name, name != "osx" { return false }
        if let arch = os?.arch {
            let alias = platform == .appleSilicon ? "aarch64" : "amd64"
            if arch != alias && platform.architecture.range(of: "^(?:\(arch))$", options: .regularExpression) == nil { return false }
        }
        if let version = os?.version {
            let os = ProcessInfo.processInfo.operatingSystemVersion
            if "\(os.majorVersion).\(os.minorVersion).\(os.patchVersion)".range(of: version, options: .regularExpression) == nil { return false }
        }
        if let range = os?.versionRange {
            let os = ProcessInfo.processInfo.operatingSystemVersion
            let current = "\(os.majorVersion).\(os.minorVersion).\(os.patchVersion)"
            if let minimum = range.min, current.compare(minimum, options: .numeric) == .orderedAscending { return false }
            if let maximum = range.max, current.compare(maximum, options: .numeric) == .orderedDescending { return false }
        }
        return (features ?? [:]).allSatisfy { enabled[$0.key, default: false] == $0.value }
    }
}

nonisolated struct MinecraftLibrary: Decodable, Sendable {
    struct Downloads: Decodable, Sendable {
        let artifact: MojangDownload?
        let classifiers: [String: MojangDownload]?
    }
    struct Extraction: Decodable, Sendable { let exclude: [String]? }
    let name: String
    let downloads: Downloads?
    let natives: [String: String]?
    let extract: Extraction?
    let rules: [MinecraftRule]?

    var coordinates: [String] { name.components(separatedBy: ":") }
    var nativeGroup: String { coordinates.prefix(3).joined(separator: ":") }
    var classifier: String? { coordinates.count > 3 ? coordinates[3] : nil }
    var isMacNativeArtifact: Bool { classifier?.hasPrefix("natives-macos") == true || classifier?.hasPrefix("natives-osx") == true }

    func allowed(on platform: MinecraftPlatform) -> Bool {
        guard let rules, !rules.isEmpty else { return true }
        var allowed = false
        for rule in rules where rule.matches(platform) { allowed = rule.action == "allow" }
        return allowed
    }

    func nativeMatches(_ platform: MinecraftPlatform) -> Bool {
        guard let classifier else { return false }
        let arm = classifier.contains("arm64") || classifier.contains("aarch64")
        return platform == .appleSilicon ? arm : !arm && (!classifier.contains("x86") || classifier.contains("x86_64"))
    }
}

nonisolated struct JavaRuntimePackage: Decodable, Sendable {
    struct Version: Decodable, Sendable { let name: String; let released: String }
    let manifest: MojangDownload
    let version: Version

    var majorVersion: Int? {
        let parts = version.name.split(separator: ".")
        if parts.first == "1", parts.count > 1 { return Int(parts[1].prefix(while: \.isNumber)) }
        return Int(version.name.prefix(while: \.isNumber))
    }
}

nonisolated struct JavaRuntimeManifest: Decodable, Sendable {
    struct Entry: Decodable, Sendable {
        let type: String
        let downloads: [String: MojangDownload]?
        let executable: Bool?
        let target: String?
    }
    let files: [String: Entry]
}

nonisolated struct LibraryInstallation: Sendable {
    let download: MojangDownload
    let path: String
    let extractionExcludes: [String]?
}

nonisolated enum MojangError: LocalizedError {
    case unsupported(String)
    case invalid(String)
    case http(Int)

    var errorDescription: String? {
        switch self {
        case .unsupported(let text), .invalid(let text): text
        case .http(let code): String(appLocalized: "Сервер загрузки вернул ошибку \(code). Попробуйте ещё раз.")
        }
    }
}

nonisolated struct PreparedInstallation: Sendable {
    let version: MinecraftVersion
    let manifest: MinecraftVersionManifest
    let manifestData: Data
    let runtime: JavaRuntimePackage
    let libraries: [LibraryInstallation]
}

nonisolated enum MinecraftCompatibility {
    static func libraries(_ manifest: MinecraftVersionManifest, platform: MinecraftPlatform) throws -> [LibraryInstallation] {
        var result: [LibraryInstallation] = []
        let eligible = manifest.libraries.filter { $0.allowed(on: platform) }
        let nativeGroups = Set(manifest.libraries.filter { $0.isMacNativeArtifact && ($0.allowed(on: .intel) || $0.allowed(on: .appleSilicon)) }.map(\.nativeGroup))
        for group in nativeGroups {
            guard eligible.contains(where: { $0.nativeGroup == group && $0.isMacNativeArtifact && $0.nativeMatches(platform) && $0.downloads?.artifact != nil }) else {
                throw MojangError.unsupported(String(appLocalized: "Эта версия не поддерживает \(platform == .appleSilicon ? "Apple Silicon" : "Intel Mac"): подходящие библиотеки игры отсутствуют."))
            }
        }
        for library in eligible {
            if library.isMacNativeArtifact && !library.nativeMatches(platform) { continue }
            if let artifact = library.downloads?.artifact {
                guard let path = artifact.path else { throw MojangError.invalid(String(appLocalized: "В описании библиотеки отсутствует путь.")) }
                result.append(.init(download: artifact, path: path, extractionExcludes: nil))
            }
            if let classifier = library.natives?["osx"] {
                let key = classifier.replacingOccurrences(of: "${arch}", with: "64")
                if platform == .appleSilicon && !key.contains("arm64") && !key.contains("aarch64") {
                    throw MojangError.unsupported(String(appLocalized: "Эта версия не поддерживает Apple Silicon: библиотеки игры доступны только для Intel."))
                }
                guard let native = library.downloads?.classifiers?[key], let path = native.path else {
                    throw MojangError.unsupported(String(appLocalized: "Для этой версии отсутствуют подходящие библиотеки macOS."))
                }
                result.append(.init(download: native, path: path, extractionExcludes: library.extract?.exclude ?? []))
            }
            if library.downloads?.artifact == nil && library.natives?["osx"] == nil && !library.isMacNativeArtifact {
                throw MojangError.unsupported(String(appLocalized: "Mojang не предоставляет файлы библиотеки \(library.name) для этой версии."))
            }
        }
        return result
    }
}

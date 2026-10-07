import Foundation

nonisolated enum ModLoader: String, Codable, Sendable, CaseIterable {
    case vanilla, fabric
    var title: String { self == .fabric ? "Fabric" : "Vanilla" }
}

nonisolated enum ModSource: String, Codable, Sendable {
    case local, modrinth, curseForge
    var title: String {
        switch self { case .local: String(appLocalized: "Локальный"); case .modrinth: "Modrinth"; case .curseForge: "CurseForge" }
    }
    var symbol: String {
        switch self { case .local: "folder"; case .modrinth: "shippingbox"; case .curseForge: "flame" }
    }
}

nonisolated struct FabricLoaderVersion: Decodable, Identifiable, Sendable {
    let version: String
    let stable: Bool
    var id: String { version }
}

nonisolated struct FabricAPIDescriptor: Codable, Equatable, Sendable {
    static let project = "P7dR8mSH"
    let projectID: String
    let versionID: String
    let version: String
    let channel: String
    let filename: String
    let url: URL
    let size: Int64
    let sha1: String
    let sha512: String
    var pageURL: URL { URL(string: "https://modrinth.com/mod/fabric-api/version/\(versionID)")! }
}

nonisolated struct FabricConfiguration: Codable, Sendable {
    let loaderVersion: String
    let api: FabricAPIDescriptor
}

nonisolated struct FabricProfile: Decodable, Sendable {
    struct Library: Decodable, Sendable {
        let name: String
        let url: URL
        let sha1: String?
        let size: Int64?

        var path: String {
            get throws {
                let parts = name.components(separatedBy: ":")
                guard (3...4).contains(parts.count), parts.allSatisfy({ !$0.isEmpty && $0.range(of: #"^[A-Za-z0-9_.+\-]+$"#, options: .regularExpression) != nil }) else {
                    throw MojangError.invalid(String(appLocalized: "Некорректная Maven-библиотека Fabric."))
                }
                let suffix = parts.count == 4 ? "-\(parts[3])" : ""
                return "\(parts[0].replacingOccurrences(of: ".", with: "/"))/\(parts[1])/\(parts[2])/\(parts[1])-\(parts[2])\(suffix).jar"
            }
        }
        var key: String { Self.key(name) }
        static func key(_ name: String) -> String {
            let parts = name.components(separatedBy: ":")
            return parts.prefix(2).joined(separator: ":") + (parts.count > 3 ? ":\(parts[3])" : "")
        }
    }
    let id: String
    let inheritsFrom: String
    let mainClass: String
    let arguments: [String: [MinecraftArgument]]?
    let libraries: [Library]

    func validate(minecraft: String) throws {
        guard inheritsFrom == minecraft, !mainClass.isEmpty else { throw MojangError.invalid(String(appLocalized: "Профиль Fabric не соответствует версии Minecraft.")) }
        _ = try InstanceStorage.containedURL(id, in: FileManager.default.temporaryDirectory)
    }

    static func installed(root: URL, minecraft: String, sha1: String?) throws -> Self {
        guard let sha1 else { throw InstanceFileError.message(String(appLocalized: "Профиль Fabric отсутствует. Повторите установку сборки.")) }
        let url = try InstanceStorage.containedURL("minecraft/.hako-fabric.json", in: root)
        let bytes = try Data(contentsOf: url)
        try MojangIntegrity.check(bytes, download: .init(url: url, sha1: sha1))
        let profile = try JSONDecoder().decode(Self.self, from: bytes)
        try profile.validate(minecraft: minecraft)
        return profile
    }

    func resolvedLibraries(_ base: [LibraryInstallation], manifest: MinecraftVersionManifest, root: URL) throws -> [LibraryInstallation] {
        let overridden = Set(libraries.map(\.key))
        let excluded = Set(manifest.libraries.filter { overridden.contains(Library.key($0.name)) }.compactMap { $0.downloads?.artifact?.path })
        var result = try libraries.map { library in
            let path = try library.path
            let file = try InstanceStorage.containedURL("minecraft/libraries/\(path)", in: root)
            return LibraryInstallation(download: .init(url: file, sha1: "", size: library.size), path: path, extractionExcludes: nil)
        }
        result += base.filter { !excluded.contains($0.path) }
        return result
    }
}

/// Fabric predicates use OR between array members and AND between space-separated terms.
nonisolated struct FabricVersionPredicate: Decodable, Sendable {
    let alternatives: [String]
    init(_ value: String = "*") { alternatives = [value] }
    init(from decoder: Decoder) throws {
        let value = try decoder.singleValueContainer()
        if let string = try? value.decode(String.self) { alternatives = [string] }
        else { alternatives = try value.decode([String].self) }
    }

    func matches(_ value: String) throws -> Bool {
        for alternative in alternatives {
            let terms = alternative.split(whereSeparator: \.isWhitespace)
            guard !terms.isEmpty else { throw MojangError.invalid(String(appLocalized: "Пустое требование версии Fabric API.")) }
            var allowed = true
            for term in terms { if try !Self.matches(String(term), value) { allowed = false } }
            if allowed { return true }
        }
        return false
    }

    private static func matches(_ term: String, _ value: String) throws -> Bool {
        if term == "*" || term.lowercased() == "x" { return true }
        let operators = [">=", "<=", ">", "<", "=", "~", "^"]
        let operation = operators.first(where: { term.hasPrefix($0) }) ?? "="
        let text = operators.contains(where: { term.hasPrefix($0) }) ? String(term.dropFirst(operation.count)) : term
        let pieces = text.split(separator: ".", omittingEmptySubsequences: false)
        let wildcard = pieces.firstIndex(where: { $0 == "*" || $0.lowercased() == "x" })
        let reference = try Version(wildcard.map { pieces.prefix($0).joined(separator: ".") } ?? text)
        let current = try Version(value)
        let comparison = current.compare(reference)
        var upper = reference
        if let wildcard {
            guard wildcard > 0, pieces.dropFirst(wildcard).allSatisfy({ $0 == "*" || $0.lowercased() == "x" }) else { throw MojangError.invalid(String(appLocalized: "Некорректное требование версии Fabric API.")) }
            upper.increment(wildcard - 1)
        } else if operation == "~" { upper.increment(reference.numbers.count > 1 ? 1 : 0) }
        else if operation == "^" { upper.increment(0) }
        switch operation {
        case ">=": return comparison >= 0
        case ">": return wildcard == nil ? comparison > 0 : current.compare(upper) >= 0
        case "<=": return wildcard == nil ? comparison <= 0 : current.compare(upper) < 0
        case "<": return comparison < 0
        case "~", "^": return comparison >= 0 && current.compare(upper) < 0
        default: return wildcard == nil ? comparison == 0 : comparison >= 0 && current.compare(upper) < 0
        }
    }

    private struct Version {
        var numbers: [Int]
        var prerelease: [String]
        init(_ text: String) throws {
            let value = text.components(separatedBy: "+")[0].split(separator: "-", maxSplits: 1, omittingEmptySubsequences: false)
            let parts = value.first?.split(separator: ".", omittingEmptySubsequences: false) ?? []
            numbers = try parts.map { part in
                guard let number = Int(part), number >= 0 else { throw MojangError.invalid(String(appLocalized: "Не удалось прочитать требование версии Fabric API: \(text).")) }
                return number
            }
            guard !numbers.isEmpty else { throw MojangError.invalid(String(appLocalized: "Не удалось прочитать версию Fabric.")) }
            prerelease = value.count > 1 ? String(value[1]).components(separatedBy: ".") : []
        }
        mutating func increment(_ index: Int) {
            while numbers.count <= index { numbers.append(0) }
            numbers[index] += 1
            for next in numbers.indices where next > index { numbers[next] = 0 }
            prerelease = []
        }
        func compare(_ other: Self) -> Int {
            for index in 0..<max(numbers.count, other.numbers.count) {
                let lhs = index < numbers.count ? numbers[index] : 0, rhs = index < other.numbers.count ? other.numbers[index] : 0
                if lhs != rhs { return lhs < rhs ? -1 : 1 }
            }
            if prerelease.isEmpty != other.prerelease.isEmpty { return prerelease.isEmpty ? 1 : -1 }
            for index in 0..<min(prerelease.count, other.prerelease.count) {
                let lhs = prerelease[index], rhs = other.prerelease[index]
                if lhs == rhs { continue }
                if let left = Int(lhs), let right = Int(rhs) { return left < right ? -1 : 1 }
                if Int(lhs) != nil || Int(rhs) != nil { return Int(lhs) != nil ? -1 : 1 }
                return lhs < rhs ? -1 : 1
            }
            return prerelease.count == other.prerelease.count ? 0 : prerelease.count < other.prerelease.count ? -1 : 1
        }
    }
}

nonisolated struct FabricModMetadata: Decodable, Sendable {
    let id: String
    let depends: [String: FabricVersionPredicate]?
    func supports(loader: String, java: Int) throws -> Bool {
        try (depends?["fabricloader"] ?? .init()).matches(loader) && (depends?["java"] ?? .init()).matches(String(java))
    }
}

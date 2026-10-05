import Foundation
import SwiftData

nonisolated struct InstanceParameters: Codable, Equatable, Sendable {
    var javaArguments = ""
    var minecraftArguments = ""
    var fullscreen = false
    var windowWidth = 1280
    var windowHeight = 720

    init(defaults: GameLaunchDefaults = .standard) {
        javaArguments = defaults.javaArguments
        minecraftArguments = defaults.minecraftArguments
        fullscreen = defaults.fullscreen
        windowWidth = defaults.windowWidth
        windowHeight = defaults.windowHeight
    }
}

nonisolated enum InstallationState: String, Codable, Sendable {
    case queued, installing, paused, ready, failed

    var title: String {
        switch self {
        case .queued: "В очереди"
        case .installing: "Установка"
        case .paused: "Загрузка остановлена"
        case .ready: "Готова"
        case .failed: "Ошибка установки"
        }
    }
}

@Model final class GameInstance {
    @Attribute(.unique) var id = UUID()
    var name = ""
    var folderName = ""
    var createdAt = Date()
    var versionID = ""
    var metadataURL = ""
    var metadataSHA1 = ""
    var iconSymbol = "shippingbox.fill"
    var iconRevision = UUID()
    var usesGlobalParameters = true
    var javaArguments = ""
    var minecraftArguments = ""
    var fullscreen = false
    var windowWidth = 1280
    var windowHeight = 720
    var installationState = InstallationState.queued.rawValue
    var pauseRequested = false
    var installationError: String?
    var javaMajorVersion = 0
    var javaExecutable = "jre.bundle/Contents/Home/bin/java"
    var legacyTexturepacks = false

    init(name: String, folderName: String, versionID: String, metadataURL: String, metadataSHA1: String) {
        self.name = name
        self.folderName = folderName
        self.versionID = versionID
        self.metadataURL = metadataURL
        self.metadataSHA1 = metadataSHA1
    }

    var state: InstallationState {
        get { InstallationState(rawValue: installationState) ?? .failed }
        set { installationState = newValue.rawValue }
    }

    var parameters: InstanceParameters {
        get {
            var value = InstanceParameters()
            value.javaArguments = javaArguments
            value.minecraftArguments = minecraftArguments
            value.fullscreen = fullscreen
            value.windowWidth = windowWidth
            value.windowHeight = windowHeight
            return value
        }
        set {
            javaArguments = newValue.javaArguments
            minecraftArguments = newValue.minecraftArguments
            fullscreen = newValue.fullscreen
            windowWidth = newValue.windowWidth
            windowHeight = newValue.windowHeight
        }
    }

    func effectiveParameters(from defaults: UserDefaults = .standard) -> InstanceParameters {
        usesGlobalParameters ? InstanceParameters(defaults: .load(from: defaults)) : parameters
    }
}

struct InstanceDraft {
    var name = ""
    var iconSymbol = "shippingbox.fill"
    var iconData: Data?
    var usesGlobalParameters = true
    var parameters = InstanceParameters(defaults: .load())

    init() {}

    init(instance: GameInstance) {
        name = instance.name
        iconSymbol = instance.iconSymbol
        usesGlobalParameters = instance.usesGlobalParameters
        parameters = instance.parameters
    }
}

nonisolated enum InstanceName {
    static func validated(_ input: String) throws -> String {
        let name = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty, name.count <= 60,
              name.unicodeScalars.allSatisfy({ (65...90).contains($0.value) || (97...122).contains($0.value) || (48...57).contains($0.value) || $0.value == 32 }) else {
            throw InstanceFileError.message("Введите от 1 до 60 символов: латинские буквы, цифры и пробелы.")
        }
        return name
    }

    static func folder(for name: String) throws -> String {
        try validated(name).replacingOccurrences(of: " ", with: "_")
    }
}

nonisolated enum InstanceFileError: LocalizedError {
    case message(String)
    var errorDescription: String? {
        switch self { case .message(let text): text }
    }
}

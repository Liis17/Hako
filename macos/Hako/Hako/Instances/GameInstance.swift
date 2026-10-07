import Foundation
import SwiftData

nonisolated enum LaunchArgumentSource: String, Codable, Sendable, CaseIterable {
    case global, mojang, custom
}

nonisolated struct InstanceParameters: Codable, Equatable, Sendable {
    var javaArguments = ""
    var minecraftArguments = ""
    var fullscreen = false
    var windowWidth = 1280
    var windowHeight = 720
    var maximumMemoryMiB = JavaMemoryPolicy.current.initialMiB

    init(defaults: GameLaunchDefaults = .standard) {
        javaArguments = defaults.javaArguments
        minecraftArguments = defaults.minecraftArguments
        fullscreen = defaults.fullscreen
        windowWidth = defaults.windowWidth
        windowHeight = defaults.windowHeight
        maximumMemoryMiB = defaults.maximumMemoryMiB
    }
}

nonisolated enum InstallationState: String, Codable, Sendable {
    case queued, installing, paused, ready, failed

    var title: String {
        switch self {
        case .queued: String(appLocalized: "В очереди")
        case .installing: String(appLocalized: "Установка")
        case .paused: String(appLocalized: "Загрузка остановлена")
        case .ready: String(appLocalized: "Готова")
        case .failed: String(appLocalized: "Ошибка установки")
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
    var argumentSourceRaw: String?
    var offlineMode = false
    var offlineUsername = "Player"
    var maximumMemoryMiB = 0
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
    var modLoaderRaw = "vanilla"
    var fabricConfigurationData: Data?
    var fabricProfileSHA1: String?

    var modLoader: ModLoader { ModLoader(rawValue: modLoaderRaw) ?? .vanilla }
    var loaderTitle: String {
        guard modLoader == .fabric else { return modLoaderRaw == "vanilla" ? "Vanilla" : modLoaderRaw }
        return "Fabric \((try? fabricConfiguration())?.loaderVersion ?? "")"
    }
    func fabricConfiguration() throws -> FabricConfiguration? {
        guard let loader = ModLoader(rawValue: modLoaderRaw) else { throw InstanceFileError.message(String(appLocalized: "Неизвестный загрузчик модов: \(modLoaderRaw).")) }
        guard loader == .fabric else { return nil }
        guard let fabricConfigurationData else { throw InstanceFileError.message(String(appLocalized: "Конфигурация Fabric отсутствует. Повторите установку сборки.")) }
        return try JSONDecoder().decode(FabricConfiguration.self, from: fabricConfigurationData)
    }

    init(name: String, folderName: String, versionID: String, metadataURL: String, metadataSHA1: String) {
        self.name = name
        self.folderName = folderName
        self.versionID = versionID
        self.metadataURL = metadataURL
        self.metadataSHA1 = metadataSHA1
        argumentSourceRaw = LaunchArgumentSource.mojang.rawValue
        maximumMemoryMiB = JavaMemoryPolicy.current.initialMiB
    }

    var argumentSource: LaunchArgumentSource {
        get { argumentSourceRaw.flatMap(LaunchArgumentSource.init(rawValue:)) ?? (usesGlobalParameters ? .global : .custom) }
        set { argumentSourceRaw = newValue.rawValue; usesGlobalParameters = newValue == .global }
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
            value.maximumMemoryMiB = maximumMemoryMiB
            return value
        }
        set {
            javaArguments = newValue.javaArguments
            minecraftArguments = newValue.minecraftArguments
            fullscreen = newValue.fullscreen
            windowWidth = newValue.windowWidth
            windowHeight = newValue.windowHeight
            maximumMemoryMiB = newValue.maximumMemoryMiB
        }
    }

    func effectiveParameters(from defaults: UserDefaults = .standard) -> InstanceParameters {
        var value = parameters
        if argumentSource == .global {
            let global = GameLaunchDefaults.load(from: defaults)
            value.javaArguments = global.javaArguments
            value.minecraftArguments = global.minecraftArguments
            value.maximumMemoryMiB = global.maximumMemoryMiB
        } else if argumentSource == .mojang {
            value.javaArguments = ""; value.minecraftArguments = ""
        }
        value.maximumMemoryMiB = JavaMemoryPolicy.current.normalize(value.maximumMemoryMiB)
        return value
    }
}

struct InstanceDraft {
    var modLoader = ModLoader.vanilla
    var fabricConfiguration: FabricConfiguration?
    var name = ""
    var iconSymbol = "shippingbox.fill"
    var iconData: Data?
    var argumentSource = LaunchArgumentSource.mojang
    var offlineMode = false
    var offlineUsername = "Player"
    var parameters = InstanceParameters(defaults: .load())

    init() {}

    init(instance: GameInstance) {
        modLoader = instance.modLoader
        fabricConfiguration = try? instance.fabricConfiguration()
        name = instance.name
        iconSymbol = instance.iconSymbol
        argumentSource = instance.argumentSource
        offlineMode = instance.offlineMode
        offlineUsername = instance.offlineUsername
        parameters = instance.parameters
    }
}

nonisolated enum OfflineUsername {
    static func isValid(_ name: String) -> Bool {
        (3...16).contains(name.count) && name.unicodeScalars.allSatisfy {
            (65...90).contains($0.value) || (97...122).contains($0.value) || (48...57).contains($0.value) || $0.value == 95
        }
    }
}

enum LaunchSettingsMigration {
    static func run(context: ModelContext, defaults: UserDefaults = .standard, memory: JavaMemoryPolicy = .current) throws {
        if defaults.object(forKey: GameLaunchDefaults.Key.maximumMemoryMiB) == nil {
            let previous = LaunchArguments.maximumHeapMiB(in: defaults.string(forKey: GameLaunchDefaults.Key.javaArguments) ?? "")
            defaults.set(memory.normalize(previous ?? memory.initialMiB), forKey: GameLaunchDefaults.Key.maximumMemoryMiB)
        }
        let global = GameLaunchDefaults.load(from: defaults)
        do {
            for instance in try context.fetch(FetchDescriptor<GameInstance>()) where instance.argumentSourceRaw == nil {
                if instance.usesGlobalParameters {
                    instance.fullscreen = global.fullscreen
                    instance.windowWidth = global.windowWidth; instance.windowHeight = global.windowHeight
                }
                let arguments = instance.usesGlobalParameters ? global.javaArguments : instance.javaArguments
                instance.maximumMemoryMiB = memory.normalize(LaunchArguments.maximumHeapMiB(in: arguments) ?? memory.initialMiB)
                instance.argumentSource = instance.usesGlobalParameters ? .global : .custom
            }
            try context.save()
        } catch { context.rollback(); throw error }
    }
}

nonisolated enum InstanceName {
    static func validated(_ input: String) throws -> String {
        let name = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty, name.count <= 60,
              name.unicodeScalars.allSatisfy({ (65...90).contains($0.value) || (97...122).contains($0.value) || (48...57).contains($0.value) || $0.value == 32 }) else {
            throw InstanceFileError.message(String(appLocalized: "Введите от 1 до 60 символов: латинские буквы, цифры и пробелы."))
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

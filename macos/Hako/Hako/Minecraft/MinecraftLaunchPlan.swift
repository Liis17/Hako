import Foundation
import CryptoKit

nonisolated struct MinecraftLaunchIdentity: Sendable {
    let name: String
    let uuid: String
    let accessToken: String
    var xuid = ""
    var userType = "msa"

    static func offline(name: String) throws -> Self {
        guard OfflineUsername.isValid(name) else { throw InstanceFileError.message(String(appLocalized: "Ник: от 3 до 16 латинских букв, цифр или _.")) }
        var bytes = Array(Insecure.MD5.hash(data: Data("OfflinePlayer:\(name)".utf8)))
        bytes[6] = (bytes[6] & 0x0f) | 0x30
        bytes[8] = (bytes[8] & 0x3f) | 0x80
        return Self(name: name, uuid: bytes.map { String(format: "%02x", $0) }.joined(), accessToken: "0", userType: "legacy")
    }
}

nonisolated struct MinecraftLaunchPlan: Sendable {
    let executable: URL
    let arguments: [String]
    let workingDirectory: URL

    static func build(manifest: MinecraftVersionManifest, root: URL, executable: URL, identity: MinecraftLaunchIdentity, source: LaunchArgumentSource, parameters: InstanceParameters, assetIndex: MinecraftAssetIndex? = nil, platform: MinecraftPlatform = .current, clientID: String = "", launcherVersion: String = "1.0", fabric: FabricProfile? = nil) throws -> Self {
        try fabric?.validate(minecraft: manifest.id)
        guard let mainClass = fabric?.mainClass ?? manifest.mainClass, !mainClass.isEmpty else { throw MojangError.invalid(String(appLocalized: "В описании версии отсутствует главный класс Minecraft.")) }
        let game = try InstanceStorage.containedURL("minecraft", in: root)
        let assets = try InstanceStorage.containedURL("assets", in: game)
        let indexID = manifest.assetIndex?.id ?? manifest.assets ?? "legacy"
        let gameAssets: URL
        if assetIndex?.virtual == true { gameAssets = try InstanceStorage.containedURL("assets/virtual/\(indexID)", in: game) }
        else if assetIndex?.map_to_resources == true { gameAssets = try InstanceStorage.containedURL("resources", in: game) }
        else { gameAssets = assets }
        let baseLibraries = try MinecraftCompatibility.libraries(manifest, platform: platform)
        let libraries = try fabric?.resolvedLibraries(baseLibraries, manifest: manifest, root: root) ?? baseLibraries
        var classpath = try libraries.filter { $0.extractionExcludes == nil }.map { try InstanceStorage.containedURL("libraries/\($0.path)", in: game).path }
        classpath.append(try InstanceStorage.containedURL("versions/\(manifest.id)/\(manifest.id).jar", in: game).path)
        var substitutions = [
            "auth_player_name": identity.name, "auth_uuid": identity.uuid, "auth_access_token": identity.accessToken,
            "auth_session": identity.accessToken == "0" ? "0" : "token:\(identity.accessToken):\(identity.uuid)",
            "auth_xuid": identity.xuid, "user_type": identity.userType, "user_properties": "{}", "profile_properties": "{}",
            "clientid": clientID, "version_name": fabric?.id ?? manifest.id, "version_type": manifest.type ?? "release",
            "game_directory": game.path, "assets_root": assets.path, "assets_index_name": indexID, "game_assets": gameAssets.path,
            "natives_directory": try InstanceStorage.containedURL("natives", in: game).path,
            "library_directory": try InstanceStorage.containedURL("libraries", in: game).path,
            "classpath": classpath.joined(separator: ":"), "classpath_separator": ":",
            "launcher_name": "Hako", "launcher_version": launcherVersion,
            "resolution_width": String(parameters.windowWidth), "resolution_height": String(parameters.windowHeight)
        ]
        if let logging = manifest.logging?["client"], logging.argument != nil {
            substitutions["logging_configuration"] = try InstanceStorage.containedURL("assets/log_configs/\(logging.file.id ?? logging.file.url.lastPathComponent)", in: game).path
        }
        let templates = try argumentTemplates(manifest: manifest, source: source, parameters: parameters, platform: platform, fabric: fabric)
        return Self(executable: executable, arguments: try (templates.java + [mainClass] + templates.minecraft).map { try substitute($0, values: substitutions) }, workingDirectory: game)
    }

    static func argumentTemplates(manifest: MinecraftVersionManifest, source: LaunchArgumentSource, parameters: InstanceParameters, platform: MinecraftPlatform = .current, fabric: FabricProfile? = nil) throws -> (java: [String], minecraft: [String]) {
        let features = ["has_custom_resolution": !parameters.fullscreen, "is_demo_user": false]
        func group(_ name: String) -> [String] {
            (manifest.arguments?[name] ?? []).filter { $0.allowed(on: platform, features: features) }.flatMap(\.values)
        }
        var jvm = source == .mojang ? group("default-user-jvm") : try LaunchArguments.parse(parameters.javaArguments)
        if manifest.arguments != nil { jvm += group("jvm") }
        else { jvm += ["-XstartOnFirstThread", "-Djava.library.path=${natives_directory}", "-cp", "${classpath}"] }
        jvm += (fabric?.arguments?["jvm"] ?? []).filter { $0.allowed(on: platform, features: features) }.flatMap(\.values)
        if let argument = manifest.logging?["client"]?.argument {
            jvm.append(argument.replacingOccurrences(of: "${path}", with: "${logging_configuration}"))
        }
        jvm = LaunchArguments.applyingMemory(jvm, maximumMiB: parameters.maximumMemoryMiB)
        var gameArguments = manifest.arguments != nil ? group("game") : try LaunchArguments.parse(manifest.minecraftArguments ?? "")
        gameArguments += (fabric?.arguments?["game"] ?? []).filter { $0.allowed(on: platform, features: features) }.flatMap(\.values)
        if parameters.fullscreen { gameArguments.append("--fullscreen") }
        else if !gameArguments.contains("--width") { gameArguments += ["--width", String(parameters.windowWidth), "--height", String(parameters.windowHeight)] }
        let extra = source == .mojang ? [] : try LaunchArguments.parse(parameters.minecraftArguments)
        let reserved = Set(["--username", "--uuid", "--accessToken", "--session", "--clientId", "--xuid", "--userType", "--userProperties", "--version", "--versionType", "--gameDir", "--assetsDir", "--assetIndex", "--width", "--height", "--fullscreen"])
        if let argument = extra.first(where: { reserved.contains($0.components(separatedBy: "=")[0]) }) {
            throw InstanceFileError.message(String(appLocalized: "Параметр \(argument.components(separatedBy: "=")[0]) задаётся отдельной настройкой или лаунчером."))
        }
        gameArguments += extra
        gameArguments = gameArguments.map { $0.replacingOccurrences(of: "${resolution_width}", with: String(parameters.windowWidth)).replacingOccurrences(of: "${resolution_height}", with: String(parameters.windowHeight)) }
        return (jvm, gameArguments)
    }

    private static func substitute(_ text: String, values: [String: String]) throws -> String {
        let expression = try NSRegularExpression(pattern: #"\$\{([^}]+)\}"#)
        var result = text
        for match in expression.matches(in: text, range: NSRange(text.startIndex..., in: text)).reversed() {
            let key = String(text[Range(match.range(at: 1), in: text)!])
            guard let value = values[key] else { throw MojangError.invalid(String(appLocalized: "Неизвестный параметр запуска Mojang: \(key).")) }
            result.replaceSubrange(Range(match.range, in: result)!, with: value)
        }
        return result
    }
}

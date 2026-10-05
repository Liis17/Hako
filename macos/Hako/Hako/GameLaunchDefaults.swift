//
//  GameLaunchDefaults.swift
//  Hako
//

import Foundation

/// Снимок глобальных параметров, который новая сборка копирует при создании.
struct GameLaunchDefaults {
    let javaPath: String
    let javaArguments: String
    let minecraftArguments: String
    let fullscreen: Bool
    let windowWidth: Int
    let windowHeight: Int

    static let standard = GameLaunchDefaults(
        javaPath: "", javaArguments: "", minecraftArguments: "",
        fullscreen: false, windowWidth: 1280, windowHeight: 720
    )

    enum Key {
        static let javaPath = "gameDefaults.javaPath"
        static let javaArguments = "gameDefaults.javaArguments"
        static let minecraftArguments = "gameDefaults.minecraftArguments"
        static let fullscreen = "gameDefaults.fullscreen"
        static let windowWidth = "gameDefaults.windowWidth"
        static let windowHeight = "gameDefaults.windowHeight"
    }

    static func load(from defaults: UserDefaults = .standard) -> GameLaunchDefaults {
        let width = defaults.integer(forKey: Key.windowWidth)
        let height = defaults.integer(forKey: Key.windowHeight)
        return GameLaunchDefaults(
            javaPath: defaults.string(forKey: Key.javaPath) ?? standard.javaPath,
            javaArguments: defaults.string(forKey: Key.javaArguments) ?? standard.javaArguments,
            minecraftArguments: defaults.string(forKey: Key.minecraftArguments) ?? standard.minecraftArguments,
            fullscreen: defaults.object(forKey: Key.fullscreen) as? Bool ?? standard.fullscreen,
            windowWidth: width > 0 ? width : standard.windowWidth,
            windowHeight: height > 0 ? height : standard.windowHeight
        )
    }

    static func windowDimension(from text: String) -> Int? {
        guard let value = Int(text), value > 0 else { return nil }
        return value
    }
}

//
//  MinecraftAppearanceStore.swift
//  Hako
//

import Observation

/// Authenticated profile snapshots take precedence over public sessionserver metadata. The
/// revision also invalidates in-flight head and full-body rendering requests after a save.
@MainActor @Observable
final class MinecraftAppearanceStore {
    private(set) var revisions: [String: Int] = [:]
    private(set) var profiles: [String: MinecraftProfileResponse] = [:]

    func revision(for xuid: String) -> Int { revisions[xuid, default: 0] }
    func profile(for xuid: String) -> MinecraftProfileResponse? { profiles[xuid] }

    func record(_ profile: MinecraftProfileResponse, for xuid: String) {
        profiles[xuid] = profile
        revisions[xuid, default: 0] += 1
    }

    func clear(_ xuid: String) {
        profiles[xuid] = nil
        revisions[xuid, default: 0] += 1
    }
}

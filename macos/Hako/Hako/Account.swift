//
//  Account.swift
//  Hako
//

import Foundation
import SwiftData

/// Вошедший пользователь: аккаунт Microsoft (профиль Xbox) и, если подключён, профиль Minecraft.
/// Токены хранятся отдельно в `TokenKeychain` под ключом `xuid`.
@Model
final class Account {
    @Attribute(.unique) var xuid: String = ""
    var gamertag: String = ""
    var email: String?
    var xboxAvatarURL: URL?

    var minecraftUUID: String?
    var minecraftName: String?
    var minecraftSkinURL: URL?

    init(xbox: XboxProfile, email: String?) {
        xuid = xbox.xuid
        gamertag = xbox.gamertag
        self.email = email
        xboxAvatarURL = xbox.avatarURL
    }

    func connect(_ minecraft: MinecraftProfile) {
        minecraftUUID = minecraft.uuid
        minecraftName = minecraft.name
        minecraftSkinURL = minecraft.skinURL
    }
}

//
//  Account.swift
//  Hako
//

import Foundation
import SwiftData

/// Профиль Minecraft вошедшего пользователя. Токены хранятся отдельно в `TokenKeychain`.
@Model
final class Account {
    @Attribute(.unique) var uuid: String
    var name: String

    init(uuid: String, name: String) {
        self.uuid = uuid
        self.name = name
    }
}

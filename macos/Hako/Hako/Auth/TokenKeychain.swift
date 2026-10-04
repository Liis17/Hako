//
//  TokenKeychain.swift
//  Hako
//

import Foundation
import Security

struct AccountTokens: Codable {
    var microsoftRefreshToken: String
    var minecraftAccessToken: String
    var minecraftTokenExpiration: Date
}

struct KeychainError: LocalizedError {
    let status: OSStatus

    var errorDescription: String? {
        "Не удалось сохранить данные входа в Keychain (код \(status))."
    }
}

/// Токены аккаунта в связке ключей входа: одна запись на UUID профиля Minecraft.
/// Data Protection Keychain не используется: без `application-identifier` (provisioning profile)
/// он возвращает `errSecMissingEntitlement`.
enum TokenKeychain {
    private static let service = "com.Launcher.Hako.auth"

    static func save(_ tokens: AccountTokens, for uuid: String) throws {
        delete(for: uuid)
        var query = baseQuery(for: uuid)
        query[kSecValueData as String] = try JSONEncoder().encode(tokens)
        let status = SecItemAdd(query as CFDictionary, nil)
        guard status == errSecSuccess else { throw KeychainError(status: status) }
    }

    static func delete(for uuid: String) {
        SecItemDelete(baseQuery(for: uuid) as CFDictionary)
    }

    private static func baseQuery(for uuid: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: uuid,
        ]
    }
}

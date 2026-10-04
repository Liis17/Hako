//
//  TokenKeychain.swift
//  Hako
//

import Foundation
import Security

struct AccountTokens: Codable {
    var microsoftRefreshToken: String
    var minecraftAccessToken: String?
    var minecraftTokenExpiration: Date?
}

struct KeychainError: LocalizedError {
    let status: OSStatus

    var errorDescription: String? {
        "Не удалось сохранить данные входа в Keychain (код \(status))."
    }
}

/// Токены аккаунта в связке ключей входа: одна запись на XUID аккаунта Xbox.
/// Data Protection Keychain не используется: без `application-identifier` (provisioning profile)
/// он возвращает `errSecMissingEntitlement`.
enum TokenKeychain {
    private static let service = "com.Launcher.Hako.auth"

    static func save(_ tokens: AccountTokens, for xuid: String) throws {
        delete(for: xuid)
        var query = baseQuery(for: xuid)
        query[kSecValueData as String] = try JSONEncoder().encode(tokens)
        let status = SecItemAdd(query as CFDictionary, nil)
        guard status == errSecSuccess else { throw KeychainError(status: status) }
    }

    static func delete(for xuid: String) {
        SecItemDelete(baseQuery(for: xuid) as CFDictionary)
    }

    private static func baseQuery(for xuid: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: xuid,
        ]
    }
}

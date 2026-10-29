import Foundation
import Security

nonisolated protocol HakoSessionKeychainBackend: Sendable {
    func copySessionData() throws -> Data?
    func updateOrAddSessionData(_ data: Data) throws
    func deleteSessionData() throws
}

nonisolated struct HakoSessionStoreError: Error, Sendable {
    let status: OSStatus?

    init(status: OSStatus? = nil) { self.status = status }
}

/// Separate Keychain item for the Hako Telegram session. All Security.framework calls run on
/// a private serial queue, and a missing item is the only condition represented by nil.
nonisolated final class HakoSessionKeychainStore: HakoSessionStore, @unchecked Sendable {
    static let service = "com.Launcher.Hako.telegram-session"
    fileprivate static let account = "current"

    private let backend: any HakoSessionKeychainBackend
    private let queue = DispatchQueue(label: "com.Launcher.Hako.telegram-session-keychain")

    init(backend: any HakoSessionKeychainBackend = SecurityHakoSessionKeychainBackend()) {
        self.backend = backend
    }

    func load() async throws -> HakoSession? {
        try await onKeychainQueue {
            guard let data = try self.backend.copySessionData() else { return nil }
            do {
                let session = try JSONDecoder().decode(HakoSession.self, from: data)
                guard Self.isValid(session) else { throw HakoSessionStoreError() }
                return session
            } catch let error as HakoSessionStoreError {
                throw error
            } catch {
                throw HakoSessionStoreError()
            }
        }
    }

    func save(_ session: HakoSession) async throws {
        guard Self.isValid(session) else { throw HakoSessionStoreError() }
        try await onKeychainQueue {
            do {
                try self.backend.updateOrAddSessionData(JSONEncoder().encode(session))
            } catch let error as HakoSessionStoreError {
                throw error
            } catch {
                throw HakoSessionStoreError()
            }
        }
    }

    func clear(ifMatching token: String) async throws -> Bool {
        try await onKeychainQueue {
            guard let data = try self.backend.copySessionData() else { return true }
            let stored: HakoSession
            do {
                stored = try JSONDecoder().decode(HakoSession.self, from: data)
            } catch {
                throw HakoSessionStoreError()
            }
            guard stored.token == token else { return false }
            try self.backend.deleteSessionData()
            return true
        }
    }

    private func onKeychainQueue<Value: Sendable>(
        _ operation: @escaping @Sendable () throws -> Value
    ) async throws -> Value {
        try await withCheckedThrowingContinuation { continuation in
            queue.async {
                do { continuation.resume(returning: try operation()) }
                catch { continuation.resume(throwing: error) }
            }
        }
    }

    private static func isValid(_ session: HakoSession) -> Bool {
        !session.token.isEmpty &&
            UUID(uuidString: session.account.id) != nil &&
            (try? HakoUsername.normalize(session.account.username)) == session.account.username &&
            !session.deviceName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty &&
            session.deviceName.unicodeScalars.count <= 128 &&
            session.expiresAt != .distantPast
    }
}

nonisolated private final class SecurityHakoSessionKeychainBackend: HakoSessionKeychainBackend, @unchecked Sendable {
    func copySessionData() throws -> Data? {
        var query = Self.baseQuery
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data else {
            throw HakoSessionStoreError(status: status)
        }
        return data
    }

    func updateOrAddSessionData(_ data: Data) throws {
        let updateStatus = SecItemUpdate(
            Self.baseQuery as CFDictionary,
            [kSecValueData as String: data] as CFDictionary
        )
        if updateStatus == errSecItemNotFound {
            var add = Self.baseQuery
            add[kSecValueData as String] = data
            let addStatus = SecItemAdd(add as CFDictionary, nil)
            guard addStatus == errSecSuccess else { throw HakoSessionStoreError(status: addStatus) }
        } else if updateStatus != errSecSuccess {
            throw HakoSessionStoreError(status: updateStatus)
        }
    }

    func deleteSessionData() throws {
        let status = SecItemDelete(Self.baseQuery as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw HakoSessionStoreError(status: status)
        }
    }

    private static var baseQuery: [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: HakoSessionKeychainStore.service,
            kSecAttrAccount as String: HakoSessionKeychainStore.account,
        ]
    }
}

import Foundation

nonisolated enum HakoAuthError: Error, Equatable, Sendable, LocalizedError {
    case network
    case timeout
    case invalidResponse
    case redirectRejected
    case rateLimited
    case serviceUnavailable
    case botUnavailable
    case invalidUsername
    case invalidRequest
    case accountNotFound
    case denied
    case requestExpired
    case accountExists
    case requestConsumed
    case unauthorized
    case persistence
    case storageUnavailable

    var errorDescription: String? {
        switch self {
        case .network: String(appLocalized: "Не удалось подключиться к Hako. Проверьте интернет и попробуйте снова.")
        case .timeout: String(appLocalized: "Запрос к Hako занял слишком много времени. Попробуйте снова.")
        case .invalidResponse: String(appLocalized: "Сервер Hako вернул некорректный ответ.")
        case .redirectRejected: String(appLocalized: "Сервер Hako перенаправил запрос на другой адрес.")
        case .rateLimited: String(appLocalized: "Слишком много запросов. Подождите и попробуйте снова.")
        case .serviceUnavailable: String(appLocalized: "Сервис Hako временно недоступен.")
        case .botUnavailable: String(appLocalized: "Telegram-бот Hako временно недоступен.")
        case .invalidUsername: String(appLocalized: "Введите Telegram username длиной от 5 до 32 символов: латинские буквы, цифры и _.")
        case .invalidRequest: String(appLocalized: "Не удалось отправить запрос Hako.")
        case .accountNotFound: String(appLocalized: "Аккаунт с таким Telegram username не найден.")
        case .denied: String(appLocalized: "Вход отклонён в Telegram.")
        case .requestExpired: String(appLocalized: "Запрос истёк. Начните вход заново.")
        case .accountExists: String(appLocalized: "Этот Telegram-аккаунт уже зарегистрирован.")
        case .requestConsumed: String(appLocalized: "Запрос уже использован. Начните новый вход по username.")
        case .unauthorized: String(appLocalized: "Сессия Hako истекла. Войдите снова.")
        case .persistence: String(appLocalized: "Не удалось сохранить сессию Hako в Keychain.")
        case .storageUnavailable: String(appLocalized: "Не удалось загрузить сессию Hako. Проверьте доступ к Keychain и перезапустите приложение.")
        }
    }
}

nonisolated enum HakoAuthorizationMode: Equatable, Sendable {
    case login
    case register
}

nonisolated enum HakoAttemptPhase: Equatable, Sendable {
    case idle
    case requesting
    case pending
    case exchanging
    case failed
}

nonisolated enum HakoRequestStatus: String, Equatable, Sendable {
    case pending
    case approved
    case denied
    case expired
    case accountExists = "account_exists"
    case consumed
}

nonisolated enum HakoSessionStatus: Equatable, Sendable {
    case signedOut
    case unverified
    case verifying
    case verified
    case expired
    case logoutFailed
}

nonisolated enum HakoLogoutResult: Equatable, Sendable {
    case signedOut
    case remoteLogoutFailed
    case localClearFailed
}

nonisolated struct HakoAccount: Codable, Equatable, Sendable {
    let id: String
    let username: String
    let createdAt: Date
}

nonisolated struct HakoSession: Codable, Equatable, Sendable, CustomStringConvertible, CustomDebugStringConvertible {
    let token: String
    let account: HakoAccount
    let deviceName: String
    let expiresAt: Date
    let verifiedAt: Date?

    var description: String { "HakoSession(account: \(account.username), deviceName: \(deviceName), expiresAt: \(expiresAt))" }
    var debugDescription: String { description }
}

nonisolated struct HakoPendingAuthRequest: Sendable, CustomStringConvertible, CustomDebugStringConvertible {
    let requestID: UUID
    let exchangeSecret: String
    let expiresAt: Date
    let botLink: URL?

    var description: String { "HakoPendingAuthRequest(requestID: \(requestID), expiresAt: \(expiresAt))" }
    var debugDescription: String { description }
}

nonisolated struct HakoAuthRequestStatus: Sendable {
    let status: HakoRequestStatus
    let expiresAt: Date
}

nonisolated struct HakoExchangeResult: Sendable, CustomStringConvertible, CustomDebugStringConvertible {
    let token: String
    let tokenExpiresAt: Date
    let account: HakoAccount

    var description: String { "HakoExchangeResult(account: \(account), tokenExpiresAt: \(tokenExpiresAt))" }
    var debugDescription: String { description }
}

nonisolated struct HakoMeResult: Sendable {
    let account: HakoAccount
    let deviceName: String
    let sessionExpiresAt: Date
}

nonisolated protocol HakoAuthAPI: Sendable {
    func register(deviceName: String) async throws -> HakoPendingAuthRequest
    func login(username: String, deviceName: String) async throws -> HakoPendingAuthRequest
    func requestStatus(_ request: HakoPendingAuthRequest) async throws -> HakoAuthRequestStatus
    func exchange(_ request: HakoPendingAuthRequest) async throws -> HakoExchangeResult
    func me(token: String) async throws -> HakoMeResult
    func logout(token: String, timeout: Duration) async throws
}

nonisolated protocol HakoSessionStore: Sendable {
    /// Returns nil only when the Keychain item is absent. Read and decoding failures throw.
    func load() async throws -> HakoSession?
    func save(_ session: HakoSession) async throws
    /// Removes only a stored session with this token. A different token is left untouched.
    func clear(ifMatching token: String) async throws -> Bool
}

nonisolated enum HakoUsername {
    static func normalize(_ value: String) throws -> String {
        var username = value.trimmingCharacters(in: .whitespacesAndNewlines)
        if username.first == "@" { username.removeFirst() }
        let scalars = username.unicodeScalars
        guard (5...32).contains(scalars.count), scalars.allSatisfy({
            (65...90).contains($0.value) || (97...122).contains($0.value) || (48...57).contains($0.value) || $0.value == 95
        }) else {
            throw HakoAuthError.invalidUsername
        }
        return username
    }
}

nonisolated enum HakoTelegramLink {
    static func validate(_ value: String) -> URL? {
        guard let components = URLComponents(string: value),
              components.scheme?.lowercased() == "https",
              components.host?.lowercased() == "t.me",
              components.port == nil || components.port == 443,
              components.user == nil,
              components.password == nil,
              components.fragment == nil,
              components.path.hasPrefix("/"),
              !components.path.dropFirst().isEmpty,
              !components.path.dropFirst().contains("/"),
              components.path.dropFirst() != ".",
              components.path.dropFirst() != "..",
              components.queryItems?.contains(where: { $0.name == "start" && !($0.value ?? "").isEmpty }) == true,
              let url = components.url else {
            return nil
        }
        return url
    }
}

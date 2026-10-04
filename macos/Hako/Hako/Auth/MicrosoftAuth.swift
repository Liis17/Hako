//
//  MicrosoftAuth.swift
//  Hako
//

import Foundation

struct DeviceCode: Decodable {
    let userCode: String
    let deviceCode: String
    let verificationUri: URL
    let expiresIn: Int
    let interval: Int
}

struct MicrosoftToken: Decodable {
    let accessToken: String
    let refreshToken: String
    let idToken: String?

    /// Email из `id_token`. Подпись не проверяется: токен получен напрямую от Microsoft по TLS
    /// и используется только для отображения.
    var email: String? {
        guard let payload = idToken?.split(separator: ".").dropFirst().first else { return nil }
        var base64 = payload.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        base64 += String(repeating: "=", count: (4 - base64.count % 4) % 4)
        guard let data = Data(base64Encoded: base64),
              let claims = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { return nil }
        return (claims["email"] ?? claims["preferred_username"]) as? String
    }
}

struct XboxProfile {
    let xuid: String
    let gamertag: String
    let avatarURL: URL?
}

struct MinecraftProfile {
    let uuid: String
    let name: String
    let skinURL: URL?
}

struct MinecraftSession {
    let profile: MinecraftProfile
    let accessToken: String
    let expiration: Date
}

enum MicrosoftAuthError: LocalizedError {
    case missingClientID
    case codeExpired
    case declined
    case oauth(String)
    case xbox(code: Int)
    case appNotApproved
    case noMinecraft
    case unexpectedResponse(status: Int)

    var errorDescription: String? {
        switch self {
        case .missingClientID:
            "Укажите Client ID приложения Azure в MicrosoftAuth.clientID."
        case .codeExpired:
            "Код истёк. Получите новый и попробуйте снова."
        case .declined:
            "Вход отклонён на странице Microsoft."
        case .oauth(let description):
            description
        case .xbox(2148916233):
            "У этого аккаунта нет профиля Xbox. Создайте его на xbox.com и попробуйте снова."
        case .xbox(2148916235):
            "Xbox Live недоступен в вашей стране."
        case .xbox(2148916236), .xbox(2148916237):
            "Аккаунту нужно подтверждение возраста на xbox.com."
        case .xbox(2148916238):
            "Детский аккаунт: взрослый должен добавить его в семью Microsoft."
        case .xbox(let code):
            "Xbox Live отклонил вход (код \(code))."
        case .appNotApproved:
            "Client ID не одобрен Mojang для Minecraft API. Заявка: aka.ms/mce-reviewappid"
        case .noMinecraft:
            "На этом аккаунте нет Minecraft: Java Edition."
        case .unexpectedResponse(let status):
            "Сервер ответил с ошибкой \(status). Попробуйте ещё раз."
        }
    }
}

/// Вход через Microsoft device code flow: код для microsoft.com/link → токен Microsoft → Xbox Live →
/// профиль Xbox (XSTS `http://xboxlive.com`) и, если доступен, Minecraft (XSTS `rp://api.minecraftservices.com/`).
enum MicrosoftAuth {
    /// Client ID приложения Azure (Entra ID) с включёнными public client flows.
    static let clientID = "5ca0e2a1-52ce-4ba5-afab-da8048e7b124"

    private static let oauthURL = URL(string: "https://login.microsoftonline.com/consumers/oauth2/v2.0")!

    private static let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        return decoder
    }()

    static func requestDeviceCode() async throws -> DeviceCode {
        guard !clientID.isEmpty else { throw MicrosoftAuthError.missingClientID }
        let (data, status) = try await send(formRequest(oauthURL.appending(path: "devicecode"), [
            "client_id": clientID,
            "scope": "XboxLive.signin openid profile email offline_access",
        ]))
        guard status == 200 else { throw oauthError(data, status: status) }
        return try decoder.decode(DeviceCode.self, from: data)
    }

    /// Опрашивает Microsoft, пока пользователь не введёт код или код не истечёт.
    static func waitForToken(_ code: DeviceCode) async throws -> MicrosoftToken {
        let deadline = Date.now.addingTimeInterval(TimeInterval(code.expiresIn))
        var interval = code.interval
        while Date.now < deadline {
            try await Task.sleep(for: .seconds(interval))
            let (data, status) = try await send(formRequest(oauthURL.appending(path: "token"), [
                "grant_type": "urn:ietf:params:oauth:grant-type:device_code",
                "client_id": clientID,
                "device_code": code.deviceCode,
            ]))
            if status == 200 {
                return try decoder.decode(MicrosoftToken.self, from: data)
            }
            switch (try? decoder.decode(OAuthError.self, from: data))?.error {
            case "authorization_pending":
                continue
            case "slow_down":
                interval += 5
            case "authorization_declined":
                throw MicrosoftAuthError.declined
            case "expired_token":
                throw MicrosoftAuthError.codeExpired
            default:
                throw oauthError(data, status: status)
            }
        }
        throw MicrosoftAuthError.codeExpired
    }

    /// Новый токен Microsoft по refresh token — без повторного ввода кода.
    static func refresh(_ refreshToken: String) async throws -> MicrosoftToken {
        let (data, status) = try await send(formRequest(oauthURL.appending(path: "token"), [
            "grant_type": "refresh_token",
            "client_id": clientID,
            "refresh_token": refreshToken,
            "scope": "XboxLive.signin offline_access",
        ]))
        guard status == 200 else { throw oauthError(data, status: status) }
        return try decoder.decode(MicrosoftToken.self, from: data)
    }

    /// Профиль Xbox и, если доступна, сессия Minecraft. Любая ошибка на шаге Minecraft
    /// (Client ID не одобрен, нет игры, сбой) не прерывает вход: аккаунт остаётся только Microsoft.
    static func signIn(with token: MicrosoftToken) async throws -> (XboxProfile, MinecraftSession?) {
        let userToken = try await xboxUserToken(token)
        let xbox = try await xboxProfile(userToken)
        let minecraft = try? await minecraftSession(userToken)
        try Task.checkCancellation()
        return (xbox, minecraft)
    }

    /// Только Minecraft — для подключения к уже сохранённому аккаунту после `refresh`.
    static func signInToMinecraft(with token: MicrosoftToken) async throws -> MinecraftSession {
        try await minecraftSession(xboxUserToken(token))
    }

    // MARK: - Xbox Live и Minecraft

    private static func xboxUserToken(_ token: MicrosoftToken) async throws -> XboxToken {
        try await xboxToken(URL(string: "https://user.auth.xboxlive.com/user/authenticate")!, [
            "Properties": [
                "AuthMethod": "RPS",
                "SiteName": "user.auth.xboxlive.com",
                "RpsTicket": "d=\(token.accessToken)",
            ],
            "RelyingParty": "http://auth.xboxlive.com",
            "TokenType": "JWT",
        ])
    }

    private static func xstsToken(_ userToken: XboxToken, relyingParty: String) async throws -> XboxToken {
        try await xboxToken(URL(string: "https://xsts.auth.xboxlive.com/xsts/authorize")!, [
            "Properties": [
                "SandboxId": "RETAIL",
                "UserTokens": [userToken.token],
            ],
            "RelyingParty": relyingParty,
            "TokenType": "JWT",
        ])
    }

    private static func xboxProfile(_ userToken: XboxToken) async throws -> XboxProfile {
        let xsts = try await xstsToken(userToken, relyingParty: "http://xboxlive.com")
        let claims = xsts.displayClaims.xui.first ?? [:]
        return XboxProfile(
            xuid: claims["xid"] ?? "",
            gamertag: claims["gtg"] ?? "",
            // Аватар необязателен: без него показываются инициалы.
            avatarURL: try? await xboxAvatar(xsts)
        )
    }

    private static func xboxAvatar(_ xsts: XboxToken) async throws -> URL? {
        var request = URLRequest(url: URL(string: "https://profile.xboxlive.com/users/me/profile/settings?settings=GameDisplayPicRaw")!)
        request.setValue("XBL3.0 x=\(xsts.userHash);\(xsts.token)", forHTTPHeaderField: "Authorization")
        request.setValue("3", forHTTPHeaderField: "x-xbl-contract-version")
        let (data, status) = try await send(request)
        guard status == 200 else { throw MicrosoftAuthError.unexpectedResponse(status: status) }
        let settings = try decoder.decode(XboxProfileSettings.self, from: data)
        let picture = settings.profileUsers.first?.settings.first { $0.id == "GameDisplayPicRaw" }
        return picture.flatMap { URL(string: $0.value) }
    }

    private static func minecraftSession(_ userToken: XboxToken) async throws -> MinecraftSession {
        let xsts = try await xstsToken(userToken, relyingParty: "rp://api.minecraftservices.com/")

        let (loginData, loginStatus) = try await send(jsonRequest(
            URL(string: "https://api.minecraftservices.com/authentication/login_with_xbox")!,
            ["identityToken": "XBL3.0 x=\(xsts.userHash);\(xsts.token)"]
        ))
        if loginStatus == 403 { throw MicrosoftAuthError.appNotApproved }
        guard loginStatus == 200 else { throw MicrosoftAuthError.unexpectedResponse(status: loginStatus) }
        let login = try decoder.decode(MinecraftLogin.self, from: loginData)

        var profileRequest = URLRequest(url: URL(string: "https://api.minecraftservices.com/minecraft/profile")!)
        profileRequest.setValue("Bearer \(login.accessToken)", forHTTPHeaderField: "Authorization")
        let (profileData, profileStatus) = try await send(profileRequest)
        if profileStatus == 404 { throw MicrosoftAuthError.noMinecraft }
        guard profileStatus == 200 else { throw MicrosoftAuthError.unexpectedResponse(status: profileStatus) }
        let profile = try decoder.decode(MinecraftProfileResponse.self, from: profileData)
        let skins = profile.skins ?? []
        let skin = skins.first { $0.state == "ACTIVE" } ?? skins.first

        return MinecraftSession(
            profile: MinecraftProfile(uuid: profile.id, name: profile.name, skinURL: skin?.secureURL),
            accessToken: login.accessToken,
            expiration: .now.addingTimeInterval(TimeInterval(login.expiresIn))
        )
    }

    // MARK: - Запросы

    private static func xboxToken(_ url: URL, _ body: [String: Any]) async throws -> XboxToken {
        let (data, status) = try await send(jsonRequest(url, body))
        if status == 401, let error = try? decoder.decode(XboxError.self, from: data) {
            throw MicrosoftAuthError.xbox(code: error.code)
        }
        guard status == 200 else { throw MicrosoftAuthError.unexpectedResponse(status: status) }
        return try decoder.decode(XboxToken.self, from: data)
    }

    private static func oauthError(_ data: Data, status: Int) -> MicrosoftAuthError {
        guard let description = (try? decoder.decode(OAuthError.self, from: data))?.errorDescription else {
            return .unexpectedResponse(status: status)
        }
        return .oauth(description)
    }

    private static func send(_ request: URLRequest) async throws -> (Data, Int) {
        let (data, response) = try await URLSession.shared.data(for: request)
        return (data, (response as? HTTPURLResponse)?.statusCode ?? 0)
    }

    private static func formRequest(_ url: URL, _ fields: [String: String]) -> URLRequest {
        var components = URLComponents()
        components.queryItems = fields.map { URLQueryItem(name: $0.key, value: $0.value) }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.httpBody = components.percentEncodedQuery?.data(using: .utf8)
        return request
    }

    private static func jsonRequest(_ url: URL, _ body: [String: Any]) throws -> URLRequest {
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        return request
    }
}

// MARK: - Ответы серверов

private struct OAuthError: Decodable {
    let error: String
    let errorDescription: String?
}

private struct XboxToken: Decodable {
    struct Claims: Decodable {
        let xui: [[String: String]]
    }

    let token: String
    let displayClaims: Claims

    var userHash: String { displayClaims.xui.first?["uhs"] ?? "" }

    enum CodingKeys: String, CodingKey {
        case token = "Token"
        case displayClaims = "DisplayClaims"
    }
}

private struct XboxError: Decodable {
    let code: Int

    enum CodingKeys: String, CodingKey {
        case code = "XErr"
    }
}

private struct MinecraftLogin: Decodable {
    let accessToken: String
    let expiresIn: Int
}

private struct MinecraftProfileResponse: Decodable {
    struct Skin: Decodable {
        let url: URL
        let state: String

        /// textures.minecraft.net отдаёт ссылки http; App Transport Security пропускает только https.
        var secureURL: URL? {
            var components = URLComponents(url: url, resolvingAgainstBaseURL: false)
            components?.scheme = "https"
            return components?.url
        }
    }

    let id: String
    let name: String
    let skins: [Skin]?
}

private struct XboxProfileSettings: Decodable {
    struct User: Decodable {
        let settings: [Setting]
    }

    struct Setting: Decodable {
        let id: String
        let value: String
    }

    let profileUsers: [User]
}

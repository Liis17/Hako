//
//  MinecraftProfileAPI.swift
//  Hako
//

import Foundation

enum MinecraftProfileAPIError: LocalizedError, Equatable {
    case unauthorized
    case httpStatus(Int)
    case invalidResponse

    var errorDescription: String? {
        switch self {
        case .unauthorized:
            String(appLocalized: "Сессия Minecraft истекла. Подключите аккаунт повторно.")
        case .httpStatus(let status):
            String(appLocalized: "Minecraft ответил с ошибкой \(status). Попробуйте ещё раз.")
        case .invalidResponse:
            String(appLocalized: "Не удалось прочитать ответ Minecraft.")
        }
    }
}

@MainActor
protocol MinecraftProfileAPI: AnyObject {
    func profile(accessToken: String) async throws -> MinecraftProfileResponse
    func uploadSkin(accessToken: String, png: Data, variant: MinecraftSkinVariant) async throws
    func activateCape(accessToken: String, capeID: String) async throws
    func unequipCape(accessToken: String) async throws
    func texture(at url: URL) async throws -> Data
}

/// Authenticated calls to the Minecraft Services profile API. Session replacement and 401 retry
/// are owned by `MinecraftCharacterEditorCoordinator`, keeping each HTTP attempt explicit.
@MainActor
final class MinecraftProfileAPIClient: MinecraftProfileAPI {
    private let session: URLSession
    private let profileURL = URL(string: "https://api.minecraftservices.com/minecraft/profile")!

    init(session: URLSession = .shared) {
        self.session = session
    }

    func profile(accessToken: String) async throws -> MinecraftProfileResponse {
        var request = URLRequest(url: profileURL)
        request.httpMethod = "GET"
        authorize(&request, token: accessToken)
        let data = try await send(request)
        do { return try JSONDecoder().decode(MinecraftProfileResponse.self, from: data) }
        catch { throw MinecraftProfileAPIError.invalidResponse }
    }

    func uploadSkin(accessToken: String, png: Data, variant: MinecraftSkinVariant) async throws {
        let boundary = "Hako-\(UUID().uuidString)"
        var request = URLRequest(url: profileURL.appending(path: "skins"))
        request.httpMethod = "POST"
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        authorize(&request, token: accessToken)
        request.httpBody = Self.multipartBody(png: png, variant: variant, boundary: boundary)
        _ = try await send(request)
    }

    func activateCape(accessToken: String, capeID: String) async throws {
        var request = URLRequest(url: profileURL.appending(path: "capes/active"))
        request.httpMethod = "PUT"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        authorize(&request, token: accessToken)
        do { request.httpBody = try JSONSerialization.data(withJSONObject: ["capeId": capeID]) }
        catch { throw MinecraftProfileAPIError.invalidResponse }
        _ = try await send(request)
    }

    func unequipCape(accessToken: String) async throws {
        var request = URLRequest(url: profileURL.appending(path: "capes/active"))
        request.httpMethod = "DELETE"
        authorize(&request, token: accessToken)
        _ = try await send(request)
    }

    func texture(at url: URL) async throws -> Data {
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        let (data, response) = try await session.data(for: request)
        try Task.checkCancellation()
        guard let response = response as? HTTPURLResponse else { throw MinecraftProfileAPIError.invalidResponse }
        guard (200..<300).contains(response.statusCode) else { throw MinecraftProfileAPIError.httpStatus(response.statusCode) }
        return data
    }

    private func send(_ request: URLRequest) async throws -> Data {
        let (data, response) = try await session.data(for: request)
        try Task.checkCancellation()
        guard let response = response as? HTTPURLResponse else { throw MinecraftProfileAPIError.invalidResponse }
        if response.statusCode == 401 { throw MinecraftProfileAPIError.unauthorized }
        guard (200..<300).contains(response.statusCode) else { throw MinecraftProfileAPIError.httpStatus(response.statusCode) }
        return data
    }

    private func authorize(_ request: inout URLRequest, token: String) {
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
    }

    static func multipartBody(png: Data, variant: MinecraftSkinVariant, boundary: String) -> Data {
        var body = Data()
        body.appendUTF8("--\(boundary)\r\nContent-Disposition: form-data; name=\"variant\"\r\n\r\n\(variant.rawValue.lowercased())\r\n")
        body.appendUTF8("--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"skin.png\"\r\nContent-Type: image/png\r\n\r\n")
        body.append(png)
        body.appendUTF8("\r\n--\(boundary)--\r\n")
        return body
    }
}

private extension Data {
    mutating func appendUTF8(_ value: String) {
        append(contentsOf: value.utf8)
    }
}

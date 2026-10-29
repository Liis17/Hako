import Foundation

nonisolated final class HakoAuthAPIClient: HakoAuthAPI, @unchecked Sendable {
    static let serviceURL = URL(string: "https://hako.liis17.com:443/")!

    private static let maximumResponseBytes = 64 * 1024
    private let session: URLSession
    private let baseURL: URL
    private let requestTimeout: Duration

    init(
        session: URLSession? = nil,
        baseURL: URL = HakoAuthAPIClient.serviceURL,
        requestTimeout: Duration = .seconds(20)
    ) {
        self.session = session ?? Self.makeSession()
        self.baseURL = baseURL
        self.requestTimeout = requestTimeout
    }

    func register(deviceName: String) async throws -> HakoPendingAuthRequest {
        let name = try Self.validateDeviceName(deviceName)
        let response: HakoCreateRequestResponseDTO = try await sendJSON(
            method: "POST", path: "v1/auth/register", body: HakoRegisterRequestDTO(deviceName: name)
        )
        return try Self.parsePending(response, requiresBotLink: true)
    }

    func login(username: String, deviceName: String) async throws -> HakoPendingAuthRequest {
        let normalizedUsername = try HakoUsername.normalize(username)
        let name = try Self.validateDeviceName(deviceName)
        let response: HakoCreateRequestResponseDTO = try await sendJSON(
            method: "POST", path: "v1/auth/login",
            body: HakoLoginRequestDTO(username: normalizedUsername, deviceName: name)
        )
        return try Self.parsePending(response, requiresBotLink: false)
    }

    func requestStatus(_ request: HakoPendingAuthRequest) async throws -> HakoAuthRequestStatus {
        let response: HakoRequestStatusResponseDTO = try await sendJSON(
            method: "GET", path: "v1/auth/requests/\(request.requestID.uuidString.lowercased())",
            bearer: request.exchangeSecret
        )
        guard let status = HakoRequestStatus(rawValue: response.status), response.expiresAt != .distantPast else {
            throw HakoAuthError.invalidResponse
        }
        return HakoAuthRequestStatus(status: status, expiresAt: response.expiresAt)
    }

    func exchange(_ request: HakoPendingAuthRequest) async throws -> HakoExchangeResult {
        let response: HakoExchangeResponseDTO = try await sendJSON(
            method: "POST", path: "v1/auth/exchange",
            body: HakoExchangeRequestDTO(requestID: request.requestID), bearer: request.exchangeSecret
        )
        guard !response.token.isEmpty,
              response.tokenExpiresAt != .distantPast,
              let account = Self.parseAccount(response.account) else {
            throw HakoAuthError.invalidResponse
        }
        return HakoExchangeResult(token: response.token, tokenExpiresAt: response.tokenExpiresAt, account: account)
    }

    func me(token: String) async throws -> HakoMeResult {
        let response: HakoMeResponseDTO = try await sendJSON(
            method: "GET", path: "v1/auth/me", bearer: token
        )
        guard let account = Self.parseAccount(response.account),
              !response.session.deviceName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              response.session.expiresAt != .distantPast else {
            throw HakoAuthError.invalidResponse
        }
        return HakoMeResult(
            account: account,
            deviceName: response.session.deviceName,
            sessionExpiresAt: response.session.expiresAt
        )
    }

    func logout(token: String, timeout: Duration = .seconds(5)) async throws {
        let request = try makeRequest(method: "POST", path: "v1/auth/logout", bearer: token)
        let response = try await send(request, timeout: min(timeout, requestTimeout))
        if response.statusCode == 204 { return }
        throw Self.parseError(response)
    }

    private func sendJSON<Response: Decodable & Sendable>(
        method: String,
        path: String,
        bearer: String? = nil
    ) async throws -> Response {
        let request = try makeRequest(method: method, path: path, bearer: bearer)
        return try decodeJSON(from: try await send(request, timeout: requestTimeout))
    }

    private func sendJSON<Body: Encodable & Sendable, Response: Decodable & Sendable>(
        method: String,
        path: String,
        body: Body,
        bearer: String? = nil
    ) async throws -> Response {
        let encoded = try JSONEncoder().encode(body)
        let request = try makeRequest(method: method, path: path, body: encoded, bearer: bearer)
        return try decodeJSON(from: try await send(request, timeout: requestTimeout))
    }

    private func decodeJSON<Response: Decodable & Sendable>(from response: HakoHTTPResponse) throws -> Response {
        guard (200..<300).contains(response.statusCode) else { throw Self.parseError(response) }
        guard Self.isJSON(response.contentType), response.data.count <= Self.maximumResponseBytes else {
            throw HakoAuthError.invalidResponse
        }
        do {
            let decoder = JSONDecoder()
            decoder.dateDecodingStrategy = .custom { decoder in
                let container = try decoder.singleValueContainer()
                let value = try container.decode(String.self)
                guard let date = Self.parseDate(value) else { throw HakoAuthError.invalidResponse }
                return date
            }
            return try decoder.decode(Response.self, from: response.data)
        } catch let error as HakoAuthError {
            throw error
        } catch {
            throw HakoAuthError.invalidResponse
        }
    }

    private func makeRequest(
        method: String,
        path: String,
        body: Data? = nil,
        bearer: String? = nil
    ) throws -> URLRequest {
        guard let url = URL(string: path, relativeTo: baseURL)?.absoluteURL,
              url.scheme == "https",
              url.host == baseURL.host,
              url.port == 443 else {
            throw HakoAuthError.invalidRequest
        }
        var request = URLRequest(url: url, timeoutInterval: 20)
        request.httpMethod = method
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let bearer {
            request.setValue("Bearer \(bearer)", forHTTPHeaderField: "Authorization")
        }
        if let body {
            request.httpBody = body
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        return request
    }

    private func send(_ request: URLRequest, timeout: Duration) async throws -> HakoHTTPResponse {
        try Task.checkCancellation()
        do {
            return try await withThrowingTaskGroup(of: HakoHTTPResponse.self) { group in
                group.addTask {
                    let (data, response) = try await self.session.data(for: request, delegate: HakoAuthRedirectDelegate())
                    guard let http = response as? HTTPURLResponse else { throw HakoAuthError.invalidResponse }
                    return HakoHTTPResponse(
                        data: data,
                        statusCode: http.statusCode,
                        contentType: http.value(forHTTPHeaderField: "Content-Type")
                    )
                }
                group.addTask {
                    try await ContinuousClock().sleep(for: timeout)
                    throw HakoAuthError.timeout
                }
                defer { group.cancelAll() }
                guard let result = try await group.next() else { throw HakoAuthError.invalidResponse }
                return result
            }
        } catch let error as HakoAuthError {
            throw error
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            if Task.isCancelled { throw CancellationError() }
            if let urlError = error as? URLError, urlError.code == .timedOut { throw HakoAuthError.timeout }
            throw HakoAuthError.network
        }
    }

    private static func parseError(_ response: HakoHTTPResponse) -> HakoAuthError {
        if [300, 301, 302, 303, 307, 308].contains(response.statusCode) {
            return .redirectRejected
        }
        var code: String?
        if isJSON(response.contentType), response.data.count <= maximumResponseBytes,
           let envelope = try? JSONDecoder().decode(HakoAPIErrorResponseDTO.self, from: response.data) {
            code = envelope.error.code
        }
        return switch code {
        case "rate_limited": .rateLimited
        case "bot_unavailable": .botUnavailable
        case "account_not_found": .accountNotFound
        case "request_denied": .denied
        case "request_expired": .requestExpired
        case "account_exists": .accountExists
        case "request_consumed": .requestConsumed
        case "unauthorized": .unauthorized
        case "invalid_request": .invalidRequest
        default:
            switch response.statusCode {
            case 401: .unauthorized
            case 429: .rateLimited
            case 500...: .serviceUnavailable
            default: .invalidResponse
            }
        }
    }

    private static func parsePending(_ response: HakoCreateRequestResponseDTO, requiresBotLink: Bool) throws -> HakoPendingAuthRequest {
        guard let requestID = UUID(uuidString: response.requestID),
              !response.exchangeSecret.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              response.expiresAt != .distantPast else {
            throw HakoAuthError.invalidResponse
        }
        let botLink = response.botLink.flatMap(HakoTelegramLink.validate)
        if requiresBotLink && botLink == nil { throw HakoAuthError.invalidResponse }
        return HakoPendingAuthRequest(
            requestID: requestID,
            exchangeSecret: response.exchangeSecret,
            expiresAt: response.expiresAt,
            botLink: botLink
        )
    }

    private static func parseAccount(_ value: HakoAPIAccountDTO) -> HakoAccount? {
        guard UUID(uuidString: value.id) != nil,
              (try? HakoUsername.normalize(value.username)) == value.username,
              value.createdAt != .distantPast else { return nil }
        return HakoAccount(id: value.id, username: value.username, createdAt: value.createdAt)
    }

    private static func validateDeviceName(_ value: String) throws -> String {
        let normalized = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard (1...128).contains(normalized.unicodeScalars.count) else { throw HakoAuthError.invalidRequest }
        return normalized
    }

    private static func parseDate(_ value: String) -> Date? {
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = fractional.date(from: value) { return date }
        let standard = ISO8601DateFormatter()
        standard.formatOptions = [.withInternetDateTime]
        return standard.date(from: value)
    }

    private static func isJSON(_ contentType: String?) -> Bool {
        guard let mediaType = contentType?.split(separator: ";", maxSplits: 1).first?.trimmingCharacters(in: .whitespacesAndNewlines) else {
            return false
        }
        return mediaType.caseInsensitiveCompare("application/json") == .orderedSame ||
            mediaType.lowercased().hasSuffix("+json")
    }

    private static func makeSession() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 20
        configuration.timeoutIntervalForResource = 20
        configuration.httpShouldSetCookies = false
        configuration.httpCookieStorage = nil
        configuration.urlCache = nil
        return URLSession(configuration: configuration, delegate: HakoAuthRedirectDelegate(), delegateQueue: nil)
    }
}

nonisolated private struct HakoHTTPResponse: Sendable {
    let data: Data
    let statusCode: Int
    let contentType: String?
}

nonisolated private final class HakoAuthRedirectDelegate: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest,
        completionHandler: @escaping (URLRequest?) -> Void
    ) {
        completionHandler(nil)
    }
}

nonisolated private struct HakoRegisterRequestDTO: Encodable, Sendable {
    let deviceName: String
    enum CodingKeys: String, CodingKey { case deviceName = "device_name" }
}

nonisolated private struct HakoLoginRequestDTO: Encodable, Sendable {
    let username: String
    let deviceName: String
    enum CodingKeys: String, CodingKey { case username; case deviceName = "device_name" }
}

nonisolated private struct HakoExchangeRequestDTO: Encodable, Sendable {
    let requestID: UUID
    enum CodingKeys: String, CodingKey { case requestID = "request_id" }
}

nonisolated private struct HakoCreateRequestResponseDTO: Decodable, Sendable, CustomStringConvertible, CustomDebugStringConvertible {
    let requestID: String
    let botLink: String?
    let exchangeSecret: String
    let expiresAt: Date
    enum CodingKeys: String, CodingKey { case requestID = "request_id"; case botLink = "bot_link"; case exchangeSecret = "exchange_secret"; case expiresAt = "expires_at" }
    var description: String { "HakoCreateRequestResponse(requestID: \(requestID), expiresAt: \(expiresAt))" }
    var debugDescription: String { description }
}

nonisolated private struct HakoRequestStatusResponseDTO: Decodable, Sendable {
    let status: String
    let expiresAt: Date
    enum CodingKeys: String, CodingKey { case status; case expiresAt = "expires_at" }
}

nonisolated private struct HakoExchangeResponseDTO: Decodable, Sendable, CustomStringConvertible, CustomDebugStringConvertible {
    let token: String
    let tokenExpiresAt: Date
    let account: HakoAPIAccountDTO
    enum CodingKeys: String, CodingKey { case token; case tokenExpiresAt = "token_expires_at"; case account }
    var description: String { "HakoExchangeResponse(account: \(account), tokenExpiresAt: \(tokenExpiresAt))" }
    var debugDescription: String { description }
}

nonisolated private struct HakoMeResponseDTO: Decodable, Sendable {
    let account: HakoAPIAccountDTO
    let session: HakoAPISessionDTO
}

nonisolated private struct HakoAPIAccountDTO: Decodable, Sendable, CustomStringConvertible, CustomDebugStringConvertible {
    let id: String
    let username: String
    let createdAt: Date
    enum CodingKeys: String, CodingKey { case id; case username; case createdAt = "created_at" }
    var description: String { "HakoAPIAccount(id: \(id), username: \(username))" }
    var debugDescription: String { description }
}

nonisolated private struct HakoAPISessionDTO: Decodable, Sendable {
    let deviceName: String
    let expiresAt: Date
    enum CodingKeys: String, CodingKey { case deviceName = "device_name"; case expiresAt = "expires_at" }
}

nonisolated private struct HakoAPIErrorResponseDTO: Decodable, Sendable {
    let error: HakoAPIErrorDTO
}

nonisolated private struct HakoAPIErrorDTO: Decodable, Sendable {
    let code: String
    let message: String?
}

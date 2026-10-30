import Foundation

nonisolated enum HakoPlaytimeAPIError: Error, Equatable, Sendable, LocalizedError {
    case conflict

    var errorDescription: String? {
        switch self {
        case .conflict: String(appLocalized: "Сервер Hako отклонил запись игрового времени. Она сохранена для повтора.")
        }
    }
}

nonisolated protocol HakoPlaytimeAPI: Sendable {
    func postSession(sessionID: UUID, durationMS: Int64, token: String) async throws -> UUID
    func totalDuration(token: String) async throws -> Int64
}

nonisolated final class HakoPlaytimeAPIClient: HakoPlaytimeAPI, @unchecked Sendable {
    private static let maximumResponseBytes = 64 * 1024
    private let session: URLSession
    private let baseURL: URL

    init(session: URLSession? = nil, baseURL: URL = HakoAuthAPIClient.serviceURL) {
        self.session = session ?? Self.makeSession()
        self.baseURL = baseURL
    }

    func postSession(sessionID: UUID, durationMS: Int64, token: String) async throws -> UUID {
        guard durationMS >= 0 else { throw HakoAuthError.invalidRequest }
        let body = HakoPlaytimeSessionRequestDTO(sessionID: sessionID, durationMS: durationMS)
        let request = try makeRequest(method: "POST", path: "v1/playtime/sessions", token: token, body: try JSONEncoder().encode(body))
        let response = try await send(request)
        if response.statusCode == 409 { throw HakoPlaytimeAPIError.conflict }
        guard response.statusCode == 200 else { throw Self.mapError(statusCode: response.statusCode) }
        let result: HakoPlaytimeSessionResponseDTO = try decode(response)
        guard let returnedID = UUID(uuidString: result.sessionID), returnedID == sessionID else {
            throw HakoAuthError.invalidResponse
        }
        return returnedID
    }

    func totalDuration(token: String) async throws -> Int64 {
        let request = try makeRequest(method: "GET", path: "v1/playtime", token: token)
        let response = try await send(request)
        guard response.statusCode == 200 else { throw Self.mapError(statusCode: response.statusCode) }
        let result: HakoPlaytimeTotalResponseDTO = try decode(response)
        guard result.totalDurationMS >= 0 else { throw HakoAuthError.invalidResponse }
        return result.totalDurationMS
    }

    private func makeRequest(method: String, path: String, token: String, body: Data? = nil) throws -> URLRequest {
        guard !token.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              let url = URL(string: path, relativeTo: baseURL)?.absoluteURL,
              url.scheme == "https", url.host == baseURL.host, url.port == 443 else {
            throw HakoAuthError.invalidRequest
        }
        var request = URLRequest(url: url, timeoutInterval: 20)
        request.httpMethod = method
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        if let body {
            request.httpBody = body
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        return request
    }

    private func send(_ request: URLRequest) async throws -> HakoPlaytimeHTTPResponse {
        try Task.checkCancellation()
        do {
            let (data, response) = try await session.data(for: request, delegate: HakoPlaytimeRedirectDelegate())
            guard let http = response as? HTTPURLResponse else { throw HakoAuthError.invalidResponse }
            return HakoPlaytimeHTTPResponse(
                data: data,
                statusCode: http.statusCode,
                contentType: http.value(forHTTPHeaderField: "Content-Type")
            )
        } catch let error as HakoAuthError {
            throw error
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            if let urlError = error as? URLError, urlError.code == .timedOut { throw HakoAuthError.timeout }
            throw HakoAuthError.network
        }
    }

    private func decode<Response: Decodable>(
        _ response: HakoPlaytimeHTTPResponse
    ) throws -> Response {
        guard Self.isJSON(response.contentType), response.data.count <= Self.maximumResponseBytes else {
            throw HakoAuthError.invalidResponse
        }
        do { return try JSONDecoder().decode(Response.self, from: response.data) }
        catch { throw HakoAuthError.invalidResponse }
    }

    private static func mapError(statusCode: Int) -> Error {
        if (300...399).contains(statusCode) { return HakoAuthError.redirectRejected }
        switch statusCode {
        case 401: return HakoAuthError.unauthorized
        case 429: return HakoAuthError.rateLimited
        case 500...: return HakoAuthError.serviceUnavailable
        default: return HakoAuthError.invalidResponse
        }
    }

    private static func isJSON(_ contentType: String?) -> Bool {
        guard let mediaType = contentType?.split(separator: ";", maxSplits: 1).first?.trimmingCharacters(in: .whitespacesAndNewlines) else {
            return false
        }
        return mediaType.caseInsensitiveCompare("application/json") == .orderedSame || mediaType.lowercased().hasSuffix("+json")
    }

    private static func makeSession() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 20
        configuration.timeoutIntervalForResource = 20
        configuration.httpShouldSetCookies = false
        configuration.httpCookieStorage = nil
        configuration.urlCache = nil
        return URLSession(configuration: configuration, delegate: HakoPlaytimeRedirectDelegate(), delegateQueue: nil)
    }
}

nonisolated private struct HakoPlaytimeSessionRequestDTO: Encodable, Sendable {
    let sessionID: UUID
    let durationMS: Int64
    enum CodingKeys: String, CodingKey { case sessionID = "session_id"; case durationMS = "duration_ms" }
}

nonisolated private struct HakoPlaytimeSessionResponseDTO: Decodable, Sendable {
    let sessionID: String
    enum CodingKeys: String, CodingKey { case sessionID = "session_id" }
}

nonisolated private struct HakoPlaytimeTotalResponseDTO: Decodable, Sendable {
    let totalDurationMS: Int64
    enum CodingKeys: String, CodingKey { case totalDurationMS = "total_duration_ms" }
}

nonisolated private struct HakoPlaytimeHTTPResponse: Sendable {
    let data: Data
    let statusCode: Int
    let contentType: String?
}

nonisolated private final class HakoPlaytimeRedirectDelegate: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
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

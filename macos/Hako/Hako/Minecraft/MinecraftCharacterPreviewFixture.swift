//
//  MinecraftCharacterPreviewFixture.swift
//  Hako
//

import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

/// In-process HTTP fixture for the isolated native character editor preview. It only intercepts
/// requests made by the preview's dedicated URLSession and never reaches Minecraft Services.
nonisolated final class MinecraftCharacterPreviewFixture: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private nonisolated(unsafe) static var skinPNG = Data()
    private nonisolated(unsafe) static var capePNGByID: [String: Data] = [:]
    private nonisolated(unsafe) static var activeCapeID: String? = "preview-emerald"
    private nonisolated(unsafe) static var skinVariant = "CLASSIC"
    private nonisolated(unsafe) static var skinID = "preview-skin-1"
    private nonisolated(unsafe) static var requests: [URLRequest] = []

    private static let skinURL = URL(string: "https://textures.minecraft.net/texture/hako-preview-skin")!
    private static let emeraldCapeURL = URL(string: "https://textures.minecraft.net/texture/hako-preview-emerald")!
    private static let festivalCapeURL = URL(string: "https://textures.minecraft.net/texture/hako-preview-festival")!

    @MainActor
    static func reset() {
        let skin: Data?
        if let url = Bundle.main.url(forResource: "Steve", withExtension: "png") {
            skin = try? Data(contentsOf: url)
        } else {
            skin = nil
        }
        let generatedSkin = makePNG(width: 64, height: 64, base: (82, 136, 181))
        let emerald = makePNG(width: 64, height: 32, base: (32, 122, 83))
        let festival = makePNG(width: 64, height: 32, base: (211, 104, 132))
        lock.withLock {
            skinPNG = skin ?? generatedSkin
            capePNGByID = ["preview-emerald": emerald, "preview-festival": festival]
            activeCapeID = "preview-emerald"
            skinVariant = "CLASSIC"
            skinID = "preview-skin-1"
            requests = []
        }
    }

    static func makeSession() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [Self.self]
        return URLSession(configuration: configuration)
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let request = Self.materializeBody(in: self.request)
        let (status, data) = Self.lock.withLock { () -> (Int, Data) in
            Self.requests.append(request)
            guard request.value(forHTTPHeaderField: "Authorization")?.hasPrefix("Bearer preview-") == true
                    || !(request.url?.host == "api.minecraftservices.com")
            else { return (401, Data()) }

            let path = request.url?.path ?? ""
            switch (request.httpMethod ?? "GET", path) {
            case ("GET", "/minecraft/profile"):
                return (200, Self.profileData())
            case ("POST", "/minecraft/profile/skins"):
                guard let (png, variant) = Self.skinFromMultipart(request),
                      variant == "classic" || variant == "slim"
                else { return (400, Data()) }
                Self.skinPNG = png
                Self.skinVariant = variant.uppercased()
                Self.skinID = "preview-skin-\(UUID().uuidString)"
                return (204, Data())
            case ("PUT", "/minecraft/profile/capes/active"):
                guard let data = request.httpBody,
                      let body = try? JSONSerialization.jsonObject(with: data) as? [String: String],
                      let id = body["capeId"], Self.capePNGByID[id] != nil
                else { return (403, Data()) }
                Self.activeCapeID = id
                return (204, Data())
            case ("DELETE", "/minecraft/profile/capes/active"):
                Self.activeCapeID = nil
                return (204, Data())
            case ("GET", _) where request.url == Self.skinURL:
                return (200, Self.skinPNG)
            case ("GET", _) where request.url == Self.emeraldCapeURL:
                return (200, Self.capePNGByID["preview-emerald"] ?? Data())
            case ("GET", _) where request.url == Self.festivalCapeURL:
                return (200, Self.capePNGByID["preview-festival"] ?? Data())
            default:
                return (404, Data())
            }
        }
        let response = HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil, headerFields: nil)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        if !data.isEmpty { client?.urlProtocol(self, didLoad: data) }
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}

    private static func profileData() -> Data {
        let capes: [[String: String]] = [
            ["id": "preview-emerald", "state": activeCapeID == "preview-emerald" ? "ACTIVE" : "INACTIVE", "alias": "Emerald", "url": emeraldCapeURL.absoluteString],
            ["id": "preview-festival", "state": activeCapeID == "preview-festival" ? "ACTIVE" : "INACTIVE", "alias": "Festival", "url": festivalCapeURL.absoluteString]
        ]
        let skin: [String: String] = [
            "id": skinID, "state": "ACTIVE", "variant": skinVariant, "url": skinURL.absoluteString
        ]
        return (try? JSONSerialization.data(withJSONObject: [
            "id": "00000000000000000000000000000001", "name": "PreviewPlayer",
            "skins": [skin], "capes": capes
        ])) ?? Data()
    }

    private static func skinFromMultipart(_ request: URLRequest) -> (Data, String)? {
        guard let contentType = request.value(forHTTPHeaderField: "Content-Type"),
              let boundary = contentType.components(separatedBy: "boundary=").last,
              let body = request.httpBody,
              let variant = multipartField("variant", body: body, boundary: boundary),
              let png = multipartFile(body: body, boundary: boundary)
        else { return nil }
        return (png, variant)
    }

    private static func multipartField(_ name: String, body: Data, boundary: String) -> String? {
        let marker = Data("name=\"\(name)\"".utf8)
        guard let markerRange = body.range(of: marker),
              let separator = body.range(of: Data("\r\n\r\n".utf8), in: markerRange.upperBound..<body.endIndex)
        else { return nil }
        let start = separator.upperBound
        guard let end = body.range(of: Data("\r\n".utf8), in: start..<body.endIndex) else { return nil }
        return String(data: body[start..<end.lowerBound], encoding: .utf8)
    }

    private static func multipartFile(body: Data, boundary: String) -> Data? {
        let marker = Data("name=\"file\"".utf8)
        guard let markerRange = body.range(of: marker),
              let separator = body.range(of: Data("\r\n\r\n".utf8), in: markerRange.upperBound..<body.endIndex)
        else { return nil }
        let start = separator.upperBound
        let endMarker = Data("\r\n--\(boundary)".utf8)
        guard let end = body.range(of: endMarker, in: start..<body.endIndex) else { return nil }
        return Data(body[start..<end.lowerBound])
    }

    private static func materializeBody(in request: URLRequest) -> URLRequest {
        guard request.httpBody == nil, let stream = request.httpBodyStream else { return request }
        stream.open()
        defer { stream.close() }
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 4096)
        while stream.hasBytesAvailable {
            let count = buffer.withUnsafeMutableBufferPointer { stream.read($0.baseAddress!, maxLength: $0.count) }
            guard count > 0 else { break }
            data.append(contentsOf: buffer.prefix(count))
        }
        var copy = request
        copy.httpBody = data
        return copy
    }

    @MainActor
    private static func makePNG(width: Int, height: Int, base: (UInt8, UInt8, UInt8)) -> Data {
        var pixels: [UInt8] = []
        for y in 0..<height {
            for x in 0..<width {
                let stripe = (x / 8 + y / 8).isMultiple(of: 2) ? 0 : 24
                pixels += [base.0 &+ UInt8(stripe), base.1 &+ UInt8(stripe), base.2 &+ UInt8(stripe), 255]
            }
        }
        guard let provider = CGDataProvider(data: Data(pixels) as CFData),
              let image = CGImage(
                width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32,
                bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.last.rawValue),
                provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent
              )
        else { return Data() }
        let output = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(output, UTType.png.identifier as CFString, 1, nil) else { return Data() }
        CGImageDestinationAddImage(destination, image, nil)
        return CGImageDestinationFinalize(destination) ? output as Data : Data()
    }
}

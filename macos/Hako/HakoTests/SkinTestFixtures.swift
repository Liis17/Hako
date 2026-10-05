import Foundation
import ImageIO
import UniformTypeIdentifiers

final class SkinTestBundle: NSObject {}

enum SkinTestFixtures {
    static var bundle: Bundle { Bundle(for: SkinTestBundle.self) }

    static func png(width: Int = 64, height: Int = 64, transparentPixel: (Int, Int)? = nil) throws -> Data {
        var pixels: [UInt8] = []
        for y in 0..<height {
            for x in 0..<width {
                let alpha: UInt8 = transparentPixel?.0 == x && transparentPixel?.1 == y ? 0 : 255
                pixels += [UInt8(x), UInt8(y), 100, alpha]
            }
        }
        let provider = CGDataProvider(data: Data(pixels) as CFData)!
        let image = CGImage(
            width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32,
            bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.last.rawValue),
            provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent
        )!
        let data = NSMutableData()
        let destination = CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil)!
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination) else { throw MinecraftSkin.DecodingError.invalidTexture }
        return data as Data
    }

    static func pixel(_ image: CGImage, x: Int, y: Int) -> [UInt8] {
        let data = image.dataProvider!.data! as Data
        let index = y * image.bytesPerRow + x * 4
        return Array(data[index..<index + 4])
    }

    static func profile(textureURL: URL?, slim: Bool = false) throws -> Data {
        var textures: [String: Any] = [:]
        if let textureURL {
            var skin: [String: Any] = ["url": textureURL.absoluteString]
            if slim { skin["metadata"] = ["model": "slim"] }
            textures["SKIN"] = skin
        }
        let payload = try JSONSerialization.data(withJSONObject: ["textures": textures]).base64EncodedString()
        return try JSONSerialization.data(withJSONObject: ["properties": [["name": "textures", "value": payload]]])
    }
}

nonisolated final class SkinURLProtocol: URLProtocol, @unchecked Sendable {
    struct Response: Sendable {
        var status = 200
        let data: Data
        var delay: TimeInterval = 0
        var error: URLError? = nil
    }

    private static let lock = NSLock()
    private nonisolated(unsafe) static var responses: [URL: Response] = [:]
    private nonisolated(unsafe) static var recordedRequests: [URLRequest] = []
    private var delivery: DispatchWorkItem?

    static var requests: [URLRequest] { lock.withLock { recordedRequests } }

    static func prepare(_ responses: [URL: Response]) {
        lock.withLock {
            self.responses = responses
            recordedRequests = []
        }
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let response = Self.lock.withLock {
            Self.recordedRequests.append(request)
            return Self.responses[request.url!] ?? Response(status: 404, data: Data())
        }
        let delivery = DispatchWorkItem { [weak self] in
            guard let self else { return }
            if let error = response.error {
                client?.urlProtocol(self, didFailWithError: error)
                return
            }
            let http = HTTPURLResponse(url: request.url!, statusCode: response.status, httpVersion: nil, headerFields: nil)!
            client?.urlProtocol(self, didReceive: http, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: response.data)
            client?.urlProtocolDidFinishLoading(self)
        }
        self.delivery = delivery
        DispatchQueue.global().asyncAfter(deadline: .now() + response.delay, execute: delivery)
    }

    override func stopLoading() { delivery?.cancel() }
}

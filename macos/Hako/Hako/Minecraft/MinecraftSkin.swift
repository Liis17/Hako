//
//  MinecraftSkin.swift
//  Hako
//

import Foundation
import ImageIO

enum MinecraftSkinVariant: String {
    case classic = "CLASSIC"
    case slim = "SLIM"
}

/// Текстура в едином формате 64×64, с восстановленными конечностями старых скинов.
struct MinecraftSkin {
    let image: CGImage
    let variant: MinecraftSkinVariant

    enum DecodingError: Error {
        case invalidTexture
    }

    init(data: Data, variant: MinecraftSkinVariant) throws {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let original = CGImageSourceCreateImageAtIndex(source, 0, nil),
              original.width == 64, original.height == 64 || original.height == 32
        else { throw DecodingError.invalidTexture }

        let legacy = original.height == 32
        var pixels = [UInt8](repeating: 0, count: 64 * 64 * 4)
        let didDraw = pixels.withUnsafeMutableBytes { buffer -> Bool in
            guard let context = CGContext(
                data: buffer.baseAddress, width: 64, height: 64,
                bitsPerComponent: 8, bytesPerRow: 64 * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
            ) else { return false }
            context.draw(original, in: CGRect(x: 0, y: 64 - original.height, width: 64, height: original.height))
            return true
        }
        guard didDraw else { throw DecodingError.invalidTexture }

        if legacy {
            // В старом формате левая конечность — зеркальная копия правой.
            for (sourceX, destinationX, sourceY, destinationY, height) in [
                (4, 20, 16, 48, 4), (8, 24, 16, 48, 4),
                (0, 24, 20, 52, 12), (4, 20, 20, 52, 12),
                (8, 16, 20, 52, 12), (12, 28, 20, 52, 12),
                (44, 36, 16, 48, 4), (48, 40, 16, 48, 4),
                (40, 40, 20, 52, 12), (44, 36, 20, 52, 12),
                (48, 32, 20, 52, 12), (52, 44, 20, 52, 12)
            ] {
                for y in 0..<height {
                    for x in 0..<4 {
                        let source = ((sourceY + y) * 64 + sourceX + 3 - x) * 4
                        let destination = ((destinationY + y) * 64 + destinationX + x) * 4
                        pixels.replaceSubrange(destination..<destination + 4, with: pixels[source..<source + 4])
                    }
                }
            }

            let hasTransparentOverlay = (0..<32).contains { y in
                (32..<64).contains { x in pixels[(y * 64 + x) * 4 + 3] < 128 }
            }
            if !hasTransparentOverlay {
                for y in 0..<16 {
                    for x in 32..<64 {
                        let start = (y * 64 + x) * 4
                        pixels.replaceSubrange(start..<start + 4, with: [0, 0, 0, 0])
                    }
                }
            }
        }

        // Основные части тела в Minecraft всегда непрозрачны.
        for (xRange, yRange) in [(0..<32, 0..<16), (0..<64, 16..<32), (16..<48, 48..<64)] {
            for y in yRange {
                for x in xRange { pixels[(y * 64 + x) * 4 + 3] = 255 }
            }
        }

        guard let provider = CGDataProvider(data: Data(pixels) as CFData),
              let normalized = CGImage(
                width: 64, height: 64, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: 64 * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
                provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent
              )
        else { throw DecodingError.invalidTexture }
        image = normalized
        self.variant = legacy ? .classic : variant
    }

    static func steve() throws -> MinecraftSkin {
        guard let url = Bundle.main.url(forResource: "Steve", withExtension: "png")
        else { throw DecodingError.invalidTexture }
        return try MinecraftSkin(data: Data(contentsOf: url), variant: .classic)
    }
}

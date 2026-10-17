import Foundation
import ImageIO
import UniformTypeIdentifiers

nonisolated enum InstanceIconImage {
    static func normalized(_ url: URL) throws -> Data {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let thumbnail = CGImageSourceCreateThumbnailAtIndex(source, 0, [kCGImageSourceCreateThumbnailFromImageAlways: true, kCGImageSourceThumbnailMaxPixelSize: 512, kCGImageSourceCreateThumbnailWithTransform: true] as CFDictionary),
              let context = CGContext(data: nil, width: 256, height: 256, bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw InstanceFileError.message(String(appLocalized: "Не удалось прочитать картинку."))
        }
        let side = min(thumbnail.width, thumbnail.height)
        guard let crop = thumbnail.cropping(to: CGRect(x: (thumbnail.width - side) / 2, y: (thumbnail.height - side) / 2, width: side, height: side)) else { throw InstanceFileError.message(String(appLocalized: "Не удалось подготовить иконку.")) }
        context.interpolationQuality = .high
        context.draw(crop, in: CGRect(x: 0, y: 0, width: 256, height: 256))
        let data = NSMutableData()
        guard let image = context.makeImage(), let destination = CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil) else { throw InstanceFileError.message(String(appLocalized: "Не удалось сохранить иконку.")) }
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination) else { throw InstanceFileError.message(String(appLocalized: "Не удалось сохранить иконку.")) }
        return data as Data
    }
}

//
//  MinecraftSkinUpload.swift
//  Hako
//

import Foundation

enum MinecraftSkinUploadError: LocalizedError, Equatable {
    case invalidPNG
    case unsupportedDimensions

    var errorDescription: String? {
        switch self {
        case .invalidPNG:
            String(appLocalized: "Выберите корректный PNG-файл скина.")
        case .unsupportedDimensions:
            String(appLocalized: "Размер PNG должен быть 64 × 64 или 64 × 32 пикселя.")
        }
    }
}

/// Immutable, validated upload bytes plus their decoded appearance. Header and dimensions are
/// checked before ImageIO decodes the image so arbitrary PNGs never enter the preview pipeline.
struct MinecraftSkinUpload {
    let png: Data
    let skin: MinecraftSkin
    let isLegacy: Bool

    var variant: MinecraftSkinVariant { skin.variant }

    init(png: Data, variant: MinecraftSkinVariant) throws {
        let bytes = [UInt8](png.prefix(24))
        guard bytes.count == 24,
              bytes[0..<8].elementsEqual([137, 80, 78, 71, 13, 10, 26, 10]),
              String(bytes: bytes[12..<16], encoding: .ascii) == "IHDR"
        else { throw MinecraftSkinUploadError.invalidPNG }

        let width = Self.uint32(bytes, at: 16)
        let height = Self.uint32(bytes, at: 20)
        guard width == 64, height == 64 || height == 32 else {
            throw MinecraftSkinUploadError.unsupportedDimensions
        }

        self.png = png
        self.isLegacy = height == 32
        self.skin = try MinecraftSkin(data: png, variant: height == 32 ? .classic : variant)
    }

    private static func uint32(_ bytes: [UInt8], at offset: Int) -> UInt32 {
        UInt32(bytes[offset]) << 24
            | UInt32(bytes[offset + 1]) << 16
            | UInt32(bytes[offset + 2]) << 8
            | UInt32(bytes[offset + 3])
    }
}

//
//  MinecraftSkinUpload.swift
//  Hako
//

import Foundation

enum MinecraftSkinUploadError: LocalizedError, Equatable {
    case invalidPNG
    case unsupportedDimensions
    case fileTooLarge

    var errorDescription: String? {
        switch self {
        case .invalidPNG:
            String(appLocalized: "Выберите корректный PNG-файл скина.")
        case .unsupportedDimensions:
            String(appLocalized: "Размер PNG должен быть 64 × 64 или 64 × 32 пикселя.")
        case .fileTooLarge:
            String(appLocalized: "Размер PNG-файла не должен превышать 4 МиБ.")
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
        guard png.count <= Self.maximumFileSize else { throw MinecraftSkinUploadError.fileTooLarge }
        let height = try Self.validateHeader(png)

        self.png = png
        self.isLegacy = height == 32
        self.skin = try MinecraftSkin(data: png, variant: height == 32 ? .classic : variant)
    }

    nonisolated static let maximumFileSize = 4 * 1024 * 1024

    /// Runs off the UI actor. Read only the header before allocating the bounded file body;
    /// keep the security scope alive until all reads and the handle close have completed.
    nonisolated static func readPNGFile(at url: URL) throws -> Data {
        let hasAccess = url.startAccessingSecurityScopedResource()
        defer { if hasAccess { url.stopAccessingSecurityScopedResource() } }
        let file = try FileHandle(forReadingFrom: url)
        defer { try? file.close() }
        let size = try file.seekToEnd()
        guard size <= UInt64(maximumFileSize) else { throw MinecraftSkinUploadError.fileTooLarge }
        try file.seek(toOffset: 0)
        _ = try validateHeader(file.read(upToCount: 24) ?? Data())
        try file.seek(toOffset: 0)
        // The extra byte also detects a file growing after the size check.
        let data = try file.read(upToCount: maximumFileSize + 1) ?? Data()
        guard data.count <= maximumFileSize else { throw MinecraftSkinUploadError.fileTooLarge }
        return data
    }

    nonisolated private static func validateHeader(_ data: Data) throws -> UInt32 {
        let bytes = [UInt8](data.prefix(24))
        guard bytes.count == 24,
              bytes[0..<8].elementsEqual([137, 80, 78, 71, 13, 10, 26, 10]),
              String(bytes: bytes[12..<16], encoding: .ascii) == "IHDR"
        else { throw MinecraftSkinUploadError.invalidPNG }

        let width = Self.uint32(bytes, at: 16)
        let height = Self.uint32(bytes, at: 20)
        guard width == 64, height == 64 || height == 32 else {
            throw MinecraftSkinUploadError.unsupportedDimensions
        }

        return height
    }

    nonisolated private static func uint32(_ bytes: [UInt8], at offset: Int) -> UInt32 {
        UInt32(bytes[offset]) << 24
            | UInt32(bytes[offset + 1]) << 16
            | UInt32(bytes[offset + 2]) << 8
            | UInt32(bytes[offset + 3])
    }
}

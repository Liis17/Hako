import CoreTransferable
import Foundation
import ImageIO
import UniformTypeIdentifiers

nonisolated struct InstanceScreenshot: Identifiable, Hashable, Sendable, Transferable {
    let url: URL
    let created: Date?
    var id: URL { url }

    // Finder получает копию файла, оригинал в сборке не перемещается.
    static var transferRepresentation: some TransferRepresentation {
        FileRepresentation(exportedContentType: .png) { SentTransferredFile($0.url) }
    }
}

/// Скриншоты из minecraft/screenshots одной сборки. Обход и чтение выполняются вне MainActor.
actor InstanceScreenshots {
    func list(in instanceRoot: URL) throws -> [InstanceScreenshot] {
        let folder = try Self.folder(in: instanceRoot)
        guard FileManager.default.fileExists(atPath: folder.path) else { return [] }
        let keys: [URLResourceKey] = [.isRegularFileKey, .isSymbolicLinkKey, .creationDateKey, .contentModificationDateKey]
        var screenshots: [InstanceScreenshot] = []
        for file in try FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: keys, options: .skipsHiddenFiles) {
            try Task.checkCancellation()
            guard file.pathExtension.lowercased() == "png" else { continue }
            let values = try file.resourceValues(forKeys: Set(keys))
            guard values.isRegularFile == true, values.isSymbolicLink != true else { continue }
            screenshots.append(.init(url: file, created: values.creationDate ?? values.contentModificationDate))
        }
        return screenshots.sorted {
            let lhs = $0.created ?? .distantPast, rhs = $1.created ?? .distantPast
            return lhs == rhs ? $0.url.lastPathComponent.localizedStandardCompare($1.url.lastPathComponent) == .orderedAscending : lhs > rhs
        }
    }

    func thumbnail(_ url: URL, maxPixelSize: Int) -> CGImage? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixelSize,
        ]
        return CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary)
    }

    func trash(_ screenshots: [InstanceScreenshot], in instanceRoot: URL) throws {
        let folder = try Self.folder(in: instanceRoot)
        var failures: [String] = []
        for screenshot in screenshots {
            let name = screenshot.url.lastPathComponent
            do {
                let file = folder.appendingPathComponent(name)
                let values = try file.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
                guard !name.contains("/"), file.standardizedFileURL == screenshot.url.standardizedFileURL,
                      values.isRegularFile == true, values.isSymbolicLink != true else {
                    throw InstanceFileError.message(String(appLocalized: "Файл не является скриншотом этой сборки."))
                }
                try FileManager.default.trashItem(at: file, resultingItemURL: nil)
            } catch { failures.append("\(name): \(error.localizedDescription)") }
        }
        if !failures.isEmpty { throw InstanceFileError.message(failures.joined(separator: "\n")) }
    }

    nonisolated static func folder(in instanceRoot: URL) throws -> URL {
        let folder = try InstanceStorage.containedURL("minecraft/screenshots", in: instanceRoot)
        for path in ["minecraft", "minecraft/screenshots"] where (try? instanceRoot.appendingPathComponent(path).resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true {
            throw InstanceFileError.message(String(appLocalized: "Папка скриншотов не может быть символической ссылкой."))
        }
        return folder
    }
}

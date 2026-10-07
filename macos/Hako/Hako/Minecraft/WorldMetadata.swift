import Foundation
import zlib

/// Только сведения для списка миров; неизвестные NBT-теги не сохраняются в памяти.
nonisolated struct WorldMetadata: Sendable {
    var name: String?
    var version: String?
    var gameType: Int?
    var lastPlayed: Date?

    static func read(_ compressed: Data) throws -> Self {
        var reader = NBTReader(bytes: Array(try decompress(compressed)))
        guard try reader.number(1) == 10 else { throw CocoaError(.fileReadCorruptFile) }
        _ = try reader.string()
        try reader.compound(path: "", depth: 0)
        guard reader.hasData else { throw CocoaError(.fileReadCorruptFile) }
        return reader.metadata
    }

    private static func decompress(_ data: Data) throws -> Data {
        guard !data.isEmpty, data.count <= 16_777_216 else { throw CocoaError(.fileReadCorruptFile) }
        var stream = z_stream()
        guard inflateInit2_(&stream, 31, ZLIB_VERSION, Int32(MemoryLayout<z_stream>.size)) == Z_OK else { throw CocoaError(.fileReadCorruptFile) }
        defer { inflateEnd(&stream) }
        return try data.withUnsafeBytes { input in
            stream.next_in = UnsafeMutablePointer(mutating: input.bindMemory(to: UInt8.self).baseAddress)
            stream.avail_in = uInt(input.count)
            var result = Data(), buffer = [UInt8](repeating: 0, count: 32_768)
            while true {
                try Task.checkCancellation()
                let (status, count) = buffer.withUnsafeMutableBytes { output in
                    stream.next_out = output.bindMemory(to: UInt8.self).baseAddress
                    stream.avail_out = uInt(output.count)
                    let status = inflate(&stream, Z_NO_FLUSH)
                    return (status, output.count - Int(stream.avail_out))
                }
                guard result.count + count <= 16_777_216, status == Z_OK || status == Z_STREAM_END else { throw CocoaError(.fileReadCorruptFile) }
                result.append(contentsOf: buffer.prefix(count))
                if status == Z_STREAM_END { return result }
            }
        }
    }
}

nonisolated private struct NBTReader {
    let bytes: [UInt8]
    var offset = 0
    var metadata = WorldMetadata()
    var hasData = false

    mutating func skip(_ count: Int) throws {
        guard count >= 0, count <= bytes.count - offset else { throw CocoaError(.fileReadCorruptFile) }
        offset += count
    }

    mutating func number(_ width: Int) throws -> UInt64 {
        let start = offset
        try skip(width)
        return bytes[start..<offset].reduce(0) { ($0 << 8) | UInt64($1) }
    }

    mutating func string() throws -> String {
        let length = Int(try number(2)), start = offset
        try skip(length)
        let data = Data(bytes[start..<offset])
        if let text = String(data: data, encoding: .utf8) { return text }
        // Java writeUTF записывает суррогатные пары как modified UTF-8.
        let encoded = Array(data)
        var units: [UInt16] = [], index = 0
        while index < encoded.count {
            let first = encoded[index]; index += 1
            if first < 128 { units.append(UInt16(first)); continue }
            let count = first & 0xE0 == 0xC0 ? 1 : first & 0xF0 == 0xE0 ? 2 : -1
            guard count > 0, index + count <= encoded.count else { throw CocoaError(.fileReadCorruptFile) }
            var unit = UInt16(first & (count == 1 ? 0x1F : 0x0F))
            for _ in 0..<count {
                let next = encoded[index]; index += 1
                guard next & 0xC0 == 0x80 else { throw CocoaError(.fileReadCorruptFile) }
                unit = (unit << 6) | UInt16(next & 0x3F)
            }
            units.append(unit)
        }
        return String(decoding: units, as: UTF16.self)
    }

    mutating func compound(path: String, depth: Int) throws {
        guard depth < 64 else { throw CocoaError(.fileReadCorruptFile) }
        while true {
            try Task.checkCancellation()
            let type = Int(try number(1))
            if type == 0 { return }
            let name = try string(), child = path.isEmpty ? name : "\(path)/\(name)"
            if child == "Data", type == 10 { hasData = true }
            try value(type, path: child, depth: depth + 1)
        }
    }

    mutating func value(_ type: Int, path: String, depth: Int) throws {
        guard depth < 64 else { throw CocoaError(.fileReadCorruptFile) }
        switch type {
        case 1, 2, 3, 4:
            let width = [1: 1, 2: 2, 3: 4, 4: 8][type]!
            let raw = try number(width)
            if path == "Data/GameType", type == 3 { metadata.gameType = Int(Int32(bitPattern: UInt32(raw))) }
            if path == "Data/LastPlayed", type == 4 {
                let timestamp = Int64(bitPattern: raw)
                if timestamp > 0 { metadata.lastPlayed = Date(timeIntervalSince1970: Double(timestamp) / 1000) }
            }
        case 5: try skip(4)
        case 6: try skip(8)
        case 7, 11, 12:
            let count = Int(Int32(bitPattern: UInt32(try number(4))))
            guard count >= 0 else { throw CocoaError(.fileReadCorruptFile) }
            try skip(count * (type == 7 ? 1 : type == 11 ? 4 : 8))
        case 8:
            let text = try string()
            if path == "Data/LevelName", !text.isEmpty { metadata.name = text }
            if path == "Data/Version/Name", !text.isEmpty { metadata.version = text }
        case 9:
            let element = Int(try number(1)), count = Int(Int32(bitPattern: UInt32(try number(4))))
            guard count >= 0, count <= bytes.count - offset, (1...12).contains(element) || element == 0 && count == 0 else { throw CocoaError(.fileReadCorruptFile) }
            for _ in 0..<count { try value(element, path: "\(path)/[]", depth: depth + 1) }
        case 10: try compound(path: path, depth: depth)
        default: throw CocoaError(.fileReadCorruptFile)
        }
    }
}

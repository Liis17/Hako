import Foundation

nonisolated enum LaunchArguments {
    static func format(_ arguments: [String]) -> String {
        arguments.map { argument in
            if !argument.isEmpty && !argument.contains(where: { $0.isWhitespace || $0 == "\"" || $0 == "'" || $0 == "\\" }) { return argument }
            return "\"" + argument.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") + "\""
        }.joined(separator: " ")
    }

    static func parse(_ text: String) throws -> [String] {
        var result: [String] = [], value = ""
        var quote: Character?, escaped = false, started = false
        for character in text {
            if escaped { value.append(character); escaped = false; started = true; continue }
            if character == "\\" && quote != "'" { escaped = true; started = true; continue }
            if let current = quote {
                if character == current { quote = nil } else { value.append(character) }
            } else if character == "\"" || character == "'" {
                quote = character; started = true
            } else if character.isWhitespace {
                if started { result.append(value); value = ""; started = false }
            } else { value.append(character); started = true }
        }
        guard quote == nil, !escaped else { throw InstanceFileError.message("Проверьте кавычки и экранирование в аргументах запуска.") }
        if started { result.append(value) }
        return result
    }

    static func heapBytes(_ value: String) -> Int64? {
        var text = value
        let multiplier: Int64
        switch text.last?.lowercased() {
        case "k": multiplier = 1024; text.removeLast()
        case "m": multiplier = 1024 * 1024; text.removeLast()
        case "g": multiplier = 1024 * 1024 * 1024; text.removeLast()
        default: multiplier = 1
        }
        guard let number = Int64(text), number > 0 else { return nil }
        let (bytes, overflow) = number.multipliedReportingOverflow(by: multiplier)
        return overflow ? nil : bytes
    }

    static func maximumHeapMiB(in text: String) -> Int? {
        guard let arguments = try? parse(text) else { return nil }
        var result: Int?
        for (index, argument) in arguments.enumerated() {
            let size: String?
            if argument == "-Xmx" { size = index + 1 < arguments.count ? arguments[index + 1] : nil }
            else if argument.hasPrefix("-Xmx") { size = String(argument.dropFirst(4)) }
            else if argument.hasPrefix("-XX:MaxHeapSize=") { size = String(argument.dropFirst(16)) }
            else { size = nil }
            if let size, let bytes = heapBytes(size) { result = Int(bytes / (1024 * 1024)) }
        }
        return result
    }

    static func applyingMemory(_ arguments: [String], maximumMiB: Int) -> [String] {
        let maximumBytes = Int64(maximumMiB) * 1024 * 1024
        var result: [String] = [], index = 0
        while index < arguments.count {
            let argument = arguments[index]
            index += 1
            if argument == "-Xmx" {
                if index < arguments.count, !arguments[index].hasPrefix("-") { index += 1 }
                continue
            }
            if argument.hasPrefix("-Xmx") || argument.hasPrefix("-XX:MaxHeapSize=") { continue }
            if argument == "-Xms", index < arguments.count {
                let size = arguments[index]; index += 1
                result.append(heapBytes(size).map { $0 > maximumBytes } == true ? "-Xms\(maximumMiB)M" : "-Xms\(size)")
            } else if argument.hasPrefix("-Xms"), let bytes = heapBytes(String(argument.dropFirst(4))), bytes > maximumBytes {
                result.append("-Xms\(maximumMiB)M")
            } else if argument.hasPrefix("-XX:InitialHeapSize="), let bytes = heapBytes(String(argument.dropFirst(20))), bytes > maximumBytes {
                result.append("-XX:InitialHeapSize=\(maximumMiB)M")
            } else { result.append(argument) }
        }
        result.append("-Xmx\(maximumMiB)M")
        return result
    }
}

nonisolated struct JavaMemoryPolicy: Sendable {
    let physicalBytes: UInt64
    static var current: Self { .init(physicalBytes: ProcessInfo.processInfo.physicalMemory) }
    var maximumMiB: Int { max(512, Int(physicalBytes / (1024 * 1024)) / 256 * 256) }
    var initialMiB: Int { normalize(min(4096, maximumMiB / 4)) }
    func normalize(_ value: Int) -> Int { min(maximumMiB, max(512, value / 256 * 256)) }
    func warns(_ value: Int) -> Bool { Double(value) * 1024 * 1024 > Double(physicalBytes) * 0.7 }
}

import Foundation
import SystemConfiguration

nonisolated enum HakoDeviceName {
    static func current() -> String {
        var encoding: CFStringEncoding = 0
        let suppliedName = SCDynamicStoreCopyComputerName(nil, &encoding) as String?
        let candidate = (suppliedName ?? ProcessInfo.processInfo.hostName)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !candidate.isEmpty else { return "Hako" }
        let limited = String(String.UnicodeScalarView(candidate.unicodeScalars.prefix(128)))
        return limited.isEmpty ? "Hako" : limited
    }
}

import Foundation

nonisolated enum PlaytimeFormatter {
    static func string(_ seconds: TimeInterval) -> String {
        guard seconds.isFinite, seconds > 0 else { return "0 мин" }
        let minutes = Int(seconds / 60)
        if minutes == 0 { return "меньше минуты" }
        if minutes < 60 { return "\(minutes) мин" }
        return "\(minutes / 60) ч \(minutes % 60) мин"
    }
}

import Foundation

nonisolated enum PlaytimeFormatter {
    static func string(_ seconds: TimeInterval) -> String {
        guard seconds.isFinite, seconds > 0 else { return String(appLocalized: "0 мин") }
        let minutes = Int(seconds / 60)
        if minutes == 0 { return String(appLocalized: "меньше минуты") }
        if minutes < 60 { return String(appLocalized: "\(minutes) мин") }
        return String(appLocalized: "\(minutes / 60) ч \(minutes % 60) мин")
    }
}

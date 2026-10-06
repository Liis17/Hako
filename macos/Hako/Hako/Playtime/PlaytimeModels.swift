import Foundation
import SwiftData

@Model final class PlayerPlaytime {
    @Attribute(.unique) var ownerKey = ""
    var totalSeconds: TimeInterval = 0

    init(ownerKey: String) { self.ownerKey = ownerKey }
}

@Model final class InstancePlaytime {
    @Attribute(.unique) var key = ""
    var ownerKey = ""
    var instanceID = UUID()
    var totalSeconds: TimeInterval = 0

    init(ownerKey: String, instanceID: UUID) {
        self.ownerKey = ownerKey; self.instanceID = instanceID
        key = Self.key(ownerKey: ownerKey, instanceID: instanceID)
    }

    static func key(ownerKey: String, instanceID: UUID) -> String { "\(ownerKey):\(instanceID.uuidString)" }
}

@Model final class PlaytimeSession {
    @Attribute(.unique) var id = UUID()
    var ownerKey = ""
    var instanceID = UUID()
    var creditedSeconds: TimeInterval = 0

    init(instanceID: UUID, ownerKey: String) { self.instanceID = instanceID; self.ownerKey = ownerKey }
}

enum HakoSchema {
    static var schema: Schema { Schema([Account.self, GameInstance.self, PlayerPlaytime.self, InstancePlaytime.self, PlaytimeSession.self]) }
}

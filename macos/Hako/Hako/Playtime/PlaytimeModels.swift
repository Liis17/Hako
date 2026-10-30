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
    var hakoOwnerID: String? = nil
    var rotationPending = false
    var rotationTargetOwnerKey: String? = nil
    var rotationTargetHakoOwnerID: String? = nil
    var rotationContinuationID: UUID? = nil
    var rotationRequestedUptime: TimeInterval? = nil

    init(instanceID: UUID, ownerKey: String, hakoOwnerID: String? = nil) {
        self.instanceID = instanceID
        self.ownerKey = ownerKey
        self.hakoOwnerID = hakoOwnerID
    }

    var hasPendingRotation: Bool { rotationPending }
}

@Model final class HakoPlaytimeSubmission {
    @Attribute(.unique) var sessionID = UUID()
    var hakoOwnerID = ""
    var durationMS: Int64 = 0
    var localAllocationsJSON = "{}"

    init(sessionID: UUID, hakoOwnerID: String, durationMS: Int64, localAllocations: [String: Int64]) throws {
        self.sessionID = sessionID
        self.hakoOwnerID = hakoOwnerID
        self.durationMS = durationMS
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        localAllocationsJSON = String(decoding: try encoder.encode(localAllocations), as: UTF8.self)
    }

    func localAllocations() throws -> [String: Int64] {
        try JSONDecoder().decode([String: Int64].self, from: Data(localAllocationsJSON.utf8))
    }
}

enum HakoSchema {
    static var schema: Schema {
        Schema([Account.self, GameInstance.self, PlayerPlaytime.self, InstancePlaytime.self, PlaytimeSession.self, HakoPlaytimeSubmission.self])
    }
}

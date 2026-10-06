import Foundation
import SwiftData
import Testing

@Suite(.serialized) @MainActor struct PlaytimeTests {
    private func container() throws -> ModelContainer {
        try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
    }

    @Test func sessionCreditsOnlyNewTimeToItsOwnerAndInstance() throws {
        let container = try container()
        let playtime = try PlaytimeCoordinator(context: container.mainContext)
        let instanceID = UUID()
        let sessionID = try playtime.beginSession(instanceID: instanceID, xuid: "first")
        try playtime.credit(sessionID: sessionID, elapsedSeconds: 90)
        try playtime.credit(sessionID: sessionID, elapsedSeconds: 90)
        try playtime.credit(sessionID: sessionID, elapsedSeconds: 150)
        #expect(playtime.totalSeconds(xuid: "first") == 150)
        #expect(playtime.instanceSeconds(instanceID, xuid: "first") == 150)
        #expect(playtime.totalSeconds(xuid: "second") == 0)
        #expect(playtime.totalSeconds(xuid: nil) == 0)
        #expect(playtime.instanceSeconds(UUID(), xuid: "first") == 0)
    }

    @Test func signingInMovesGuestCountersAndKeepsRunningSessionsWithTheirNewOwner() throws {
        let container = try container()
        let playtime = try PlaytimeCoordinator(context: container.mainContext)
        let first = UUID(), second = UUID()
        let existing = try playtime.beginSession(instanceID: first, xuid: "first")
        try playtime.credit(sessionID: existing, elapsedSeconds: 30)
        let guest = try playtime.beginSession(instanceID: first, xuid: nil)
        let anotherGuest = try playtime.beginSession(instanceID: second, xuid: nil)
        let preparingGuest = try playtime.beginSession(instanceID: first, xuid: nil)
        try playtime.credit(sessionID: guest, elapsedSeconds: 90)
        try playtime.credit(sessionID: anotherGuest, elapsedSeconds: 60)
        try playtime.transferGuest(to: "first")
        #expect(playtime.totalSeconds(xuid: nil) == 0)
        #expect(playtime.totalSeconds(xuid: "first") == 180)
        #expect(playtime.instanceSeconds(first, xuid: "first") == 120)
        #expect(playtime.instanceSeconds(second, xuid: "first") == 60)
        try playtime.credit(sessionID: guest, elapsedSeconds: 150)
        try playtime.credit(sessionID: preparingGuest, elapsedSeconds: 15)
        try playtime.transferGuest(to: "first")
        #expect(playtime.totalSeconds(xuid: "first") == 255)
        let newGuest = try playtime.beginSession(instanceID: first, xuid: nil)
        try playtime.credit(sessionID: newGuest, elapsedSeconds: 20)
        try playtime.transferGuest(to: "second")
        try playtime.credit(sessionID: guest, elapsedSeconds: 160)
        #expect(playtime.totalSeconds(xuid: "first") == 265)
        #expect(playtime.totalSeconds(xuid: "second") == 20)
        #expect(playtime.totalSeconds(xuid: nil) == 0)
    }

    @Test func failedSavePreservesCountersAndUnrelatedEditsUntilRetry() throws {
        let container = try container(), context = container.mainContext
        var fail = false
        let playtime = try PlaytimeCoordinator(context: context, persist: {
            if fail { throw InstanceFileError.message("Test save failure") }
            try context.save()
        })
        let instance = GameInstance(name: "Pack", folderName: "Pack", versionID: "v", metadataURL: "url", metadataSHA1: "sha")
        context.insert(instance)
        let session = try playtime.beginSession(instanceID: instance.id, xuid: nil)
        try playtime.credit(sessionID: session, elapsedSeconds: 90)
        instance.name = "Unsaved edit"
        fail = true
        #expect(throws: InstanceFileError.self) { try playtime.credit(sessionID: session, elapsedSeconds: 150) }
        #expect(playtime.totalSeconds(xuid: nil) == 90)
        #expect(playtime.instanceSeconds(instance.id, xuid: nil) == 90)
        #expect(instance.name == "Unsaved edit")
        #expect(throws: InstanceFileError.self) { try playtime.transferGuest(to: "first") }
        #expect(playtime.totalSeconds(xuid: nil) == 90 && playtime.totalSeconds(xuid: "first") == 0)
        fail = false
        try playtime.credit(sessionID: session, elapsedSeconds: 150)
        try playtime.transferGuest(to: "first")
        #expect(playtime.totalSeconds(xuid: "first") == 150 && playtime.totalSeconds(xuid: nil) == 0)
    }

    @Test func migrationAndDeletingLoginOrInstanceKeepIndependentAccountTotal() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let url = root.appendingPathComponent("default.store")
        var id = UUID()
        do {
            let old = try ModelContainer(for: Account.self, GameInstance.self, configurations: ModelConfiguration(url: url))
            old.mainContext.insert(Account(xbox: .init(xuid: "preserved", gamertag: "Player", avatarURL: nil), email: nil))
            let instance = GameInstance(name: "Pack", folderName: "Pack", versionID: "v", metadataURL: "url", metadataSHA1: "sha")
            id = instance.id; old.mainContext.insert(instance)
            try old.mainContext.save()
        }
        let updated = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(url: url))
        let context = updated.mainContext
        let account = try #require(context.fetch(FetchDescriptor<Account>()).first)
        let instance = try #require(context.fetch(FetchDescriptor<GameInstance>()).first)
        #expect(account.xuid == "preserved" && instance.id == id && instance.folderName == "Pack")
        let playtime = try PlaytimeCoordinator(context: context)
        #expect(playtime.totalSeconds(xuid: "preserved") == 0)
        let session = try playtime.beginSession(instanceID: id, xuid: "preserved")
        try playtime.credit(sessionID: session, elapsedSeconds: 180)
        context.delete(account); context.delete(instance); try context.save()
        let restored = try PlaytimeCoordinator(context: ModelContext(updated))
        #expect(restored.totalSeconds(xuid: "preserved") == 180)
        context.insert(Account(xbox: .init(xuid: "preserved", gamertag: "Player", avatarURL: nil), email: nil))
        let recreated = GameInstance(name: "Pack", folderName: "Pack", versionID: "v", metadataURL: "url", metadataSHA1: "sha")
        context.insert(recreated); try context.save()
        #expect(restored.totalSeconds(xuid: "preserved") == 180)
        #expect(restored.instanceSeconds(recreated.id, xuid: "preserved") == 0)
    }
}

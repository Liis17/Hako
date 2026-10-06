import Foundation
import SwiftData
import Testing
import Darwin

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
        try restored.credit(sessionID: session, elapsedSeconds: 240)
        #expect(restored.totalSeconds(xuid: "preserved") == 240)
        context.insert(Account(xbox: .init(xuid: "preserved", gamertag: "Player", avatarURL: nil), email: nil))
        let recreated = GameInstance(name: "Pack", folderName: "Pack", versionID: "v", metadataURL: "url", metadataSHA1: "sha")
        context.insert(recreated); try context.save()
        #expect(restored.totalSeconds(xuid: "preserved") == 240)
        #expect(restored.instanceSeconds(recreated.id, xuid: "preserved") == 0)
    }

    @Test func journalsResumeWithoutDuplicateCreditAndSurviveSaveFailure() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        var fail = false
        let playtime = try PlaytimeCoordinator(context: container.mainContext, journalDirectory: root, persist: {
            if fail { throw InstanceFileError.message("Test save failure") }
            try container.mainContext.save()
        })
        let instanceID = UUID(), sessionID = try playtime.beginSession(instanceID: instanceID, xuid: nil)
        let process = try #require(PlaytimeProcessIdentity.read(pid: getpid()))
        var journal = PlaytimeJournal(sessionID: sessionID, process: process, startedUptime: 1000)
        journal.helper = process; journal.elapsedSeconds = 90
        try journal.save(in: root)
        try playtime.reconcile()
        #expect(playtime.totalSeconds(xuid: nil) == 90)
        let restored = try PlaytimeCoordinator(context: ModelContext(container), journalDirectory: root)
        try restored.reconcile()
        #expect(restored.totalSeconds(xuid: nil) == 90)
        journal.elapsedSeconds = 150; journal.isFinished = true
        try journal.save(in: root)
        fail = true
        #expect(throws: InstanceFileError.self) { try playtime.reconcile() }
        #expect(FileManager.default.fileExists(atPath: journal.url(in: root).path))
        #expect(playtime.totalSeconds(xuid: nil) == 90)
        fail = false
        try playtime.reconcile()
        #expect(playtime.totalSeconds(xuid: nil) == 150)
        #expect(!FileManager.default.fileExists(atPath: journal.url(in: root).path))
    }

    @Test func helperTracksAnIndependentProcessAndDoesNotDuplicateItsWatcher() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let game = Process(); game.executableURL = URL(fileURLWithPath: "/bin/sleep"); game.arguments = ["20"]
        try game.run()
        defer { if game.isRunning { game.terminate() } }
        let identity = try #require(PlaytimeProcessIdentity.read(pid: game.processIdentifier))
        let journal = PlaytimeJournal(sessionID: UUID(), process: identity, startedUptime: ProcessInfo.processInfo.systemUptime)
        try journal.save(in: root)
        let executable = SkinTestFixtures.bundle.bundleURL.deletingLastPathComponent().appendingPathComponent("HakoPlaytimeHelper")
        let client = PlaytimeHelperClient(executable: executable)
        try await client.attach(to: journal.url(in: root))
        let first = try PlaytimeJournal.load(from: journal.url(in: root))
        #expect(first.helper?.isRunning == true)
        try await PlaytimeHelperClient(executable: executable).attach(to: journal.url(in: root))
        #expect(try PlaytimeJournal.load(from: journal.url(in: root)).helper == first.helper)
        try await Task.sleep(for: .milliseconds(80))
        game.terminate()
        for _ in 0..<100 {
            if try PlaytimeJournal.load(from: journal.url(in: root)).isFinished { break }
            try await Task.sleep(for: .milliseconds(20))
        }
        let finished = try PlaytimeJournal.load(from: journal.url(in: root))
        #expect(finished.isFinished && finished.elapsedSeconds > 0)
    }

    @Test func helperOutlivesItsLaunchingProcessAndFinalTimeCanBeImportedLater() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let playtime = try PlaytimeCoordinator(context: container.mainContext, journalDirectory: root)
        let session = try playtime.beginSession(instanceID: UUID(), xuid: "first")
        let game = Process(); game.executableURL = URL(fileURLWithPath: "/bin/sleep"); game.arguments = ["20"]
        try game.run()
        defer { if game.isRunning { game.terminate() } }
        let process = try #require(PlaytimeProcessIdentity.read(pid: game.processIdentifier))
        let journal = PlaytimeJournal(sessionID: session, process: process, startedUptime: ProcessInfo.processInfo.systemUptime)
        try journal.save(in: root)
        let executable = SkinTestFixtures.bundle.bundleURL.deletingLastPathComponent().appendingPathComponent("HakoPlaytimeHelper")
        let parent = Process(); parent.executableURL = URL(fileURLWithPath: "/bin/sh")
        parent.arguments = ["-c", "\"$1\" \"$2\" </dev/null >/dev/null 2>&1 &", "Hako-test", executable.path, journal.url(in: root).path]
        parent.standardOutput = FileHandle.nullDevice; parent.standardError = FileHandle.nullDevice
        try parent.run(); parent.waitUntilExit()
        for _ in 0..<100 {
            if try PlaytimeJournal.load(from: journal.url(in: root)).helper?.isRunning == true { break }
            try await Task.sleep(for: .milliseconds(20))
        }
        #expect(!parent.isRunning && parent.terminationStatus == 0)
        let running = try PlaytimeJournal.load(from: journal.url(in: root))
        let helper = try #require(running.helper)
        defer { if helper.isRunning { kill(helper.pid, SIGTERM) } }
        #expect(helper.isRunning && game.isRunning)
        try await Task.sleep(for: .milliseconds(100))
        game.terminate()
        for _ in 0..<100 {
            if try PlaytimeJournal.load(from: journal.url(in: root)).isFinished { break }
            try await Task.sleep(for: .milliseconds(20))
        }
        let finished = try PlaytimeJournal.load(from: journal.url(in: root))
        #expect(finished.isFinished && finished.elapsedSeconds > 0)
        let reopened = try PlaytimeCoordinator(context: ModelContext(container), journalDirectory: root)
        try reopened.reconcile()
        #expect(reopened.totalSeconds(xuid: "first") == finished.elapsedSeconds)
    }

    @Test func uptimeClockExcludesSleepAndCannotMoveDurationBackwards() throws {
        let process = try #require(PlaytimeProcessIdentity.read(pid: getpid()))
        var journal = PlaytimeJournal(sessionID: UUID(), process: process, startedUptime: 1000)
        journal.checkpoint(atUptime: 1120)
        #expect(journal.elapsedSeconds == 120)
        // За время системного сна uptime не растёт; календарное время не участвует в расчёте.
        journal.checkpoint(atUptime: 1120)
        #expect(journal.elapsedSeconds == 120)
        journal.checkpoint(atUptime: 1130)
        #expect(journal.elapsedSeconds == 130)
        journal.checkpoint(atUptime: 900)
        #expect(journal.elapsedSeconds == 130)
        let wrongBirth = PlaytimeProcessIdentity(pid: process.pid, startSeconds: process.startSeconds - 1, startMicroseconds: process.startMicroseconds)
        #expect(!wrongBirth.isRunning)
    }

    @Test func simultaneousInstancesAccumulateIndependentlyForOneAccount() throws {
        let container = try container()
        let playtime = try PlaytimeCoordinator(context: container.mainContext)
        let first = UUID(), second = UUID()
        let a = try playtime.beginSession(instanceID: first, xuid: "first")
        let b = try playtime.beginSession(instanceID: second, xuid: "first")
        try playtime.credit(sessionID: a, elapsedSeconds: 120)
        try playtime.credit(sessionID: b, elapsedSeconds: 90)
        #expect(playtime.totalSeconds(xuid: "first") == 210)
        #expect(playtime.instanceSeconds(first, xuid: "first") == 120)
        #expect(playtime.instanceSeconds(second, xuid: "first") == 90)
    }

    @Test(arguments: [
        (0.0, "0 мин"), (0.1, "меньше минуты"), (59.9, "меньше минуты"),
        (60.0, "1 мин"), (2099.9, "34 мин"), (3600.0, "1 ч 0 мин"),
        (45240.0, "12 ч 34 мин"), (176520.0, "49 ч 2 мин"),
    ]) func durationUsesWholeMinutesAndNeverTurnsHoursIntoDays(seconds: Double, expected: String) {
        #expect(PlaytimeFormatter.string(seconds) == expected)
    }
}

import Foundation
import SwiftData
import Testing
import Darwin

private enum LegacyHakoPlaytimeSchema: VersionedSchema {
    static var versionIdentifier = Schema.Version(1, 0, 0)
    static var models: [any PersistentModel.Type] {
        [Account.self, GameInstance.self, PlayerPlaytime.self, InstancePlaytime.self, PlaytimeSession.self]
    }

    @Model final class PlaytimeSession {
        @Attribute(.unique) var id = UUID()
        var ownerKey = ""
        var instanceID = UUID()
        var creditedSeconds: TimeInterval = 0

        init(instanceID: UUID, ownerKey: String) {
            self.instanceID = instanceID
            self.ownerKey = ownerKey
        }
    }
}

private enum CurrentHakoPlaytimeSchema: VersionedSchema {
    static var versionIdentifier = Schema.Version(2, 0, 0)
    static var models: [any PersistentModel.Type] {
        [Account.self, GameInstance.self, PlayerPlaytime.self, InstancePlaytime.self, PlaytimeSession.self, HakoPlaytimeSubmission.self]
    }
}

private enum HakoPlaytimeMigrationPlan: SchemaMigrationPlan {
    static var schemas: [any VersionedSchema.Type] { [LegacyHakoPlaytimeSchema.self, CurrentHakoPlaytimeSchema.self] }
    static var stages: [MigrationStage] {
        [.lightweight(fromVersion: LegacyHakoPlaytimeSchema.self, toVersion: CurrentHakoPlaytimeSchema.self)]
    }
}

@Suite(.serialized) @MainActor struct HakoPlaytimeTests {
    private struct RestartSeed {
        let submissionID: UUID
        let guestInstanceID: UUID
        let microsoftInstanceID: UUID
    }

    private struct RestartObservation {
        let posts: [HakoPlaytimeAPIFixture.Request]
        let remoteTotalMS: Int64
        let queueIsEmpty: Bool
        let guestTotal: TimeInterval
        let microsoftTotal: TimeInterval
        let guestInstanceTotal: TimeInterval
        let microsoftInstanceTotal: TimeInterval
    }

    private let ownerA = "11111111-1111-4111-8111-111111111111"
    private let ownerB = "22222222-2222-4222-8222-222222222222"

    private func container() throws -> ModelContainer {
        try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
    }

    private func session(_ ownerID: String, token: String? = nil, expiry: Date = .now.addingTimeInterval(3600)) -> HakoSession {
        let account = HakoAccount(id: ownerID, username: "Player_123", createdAt: .now)
        return HakoSession(
            token: token ?? "token-\(ownerID.prefix(4))",
            account: account,
            deviceName: "Test Mac",
            expiresAt: expiry,
            verifiedAt: .now
        )
    }

    private func coordinator(
        _ container: ModelContainer,
        root: URL,
        api: HakoPlaytimeAPIFixture,
        helper: PlaytimeHelperClient = PlaytimeHelperClient(),
        retryDelays: [Duration] = [.milliseconds(5), .milliseconds(10)],
        rotationPollInterval: Duration = .milliseconds(20)
    ) throws -> PlaytimeCoordinator {
        try PlaytimeCoordinator(
            context: container.mainContext,
            journalDirectory: root,
            api: api,
            helper: helper,
            retryDelays: retryDelays,
            rotationPollInterval: rotationPollInterval
        )
    }

    private func persistentContainer(at url: URL) throws -> ModelContainer {
        try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(url: url))
    }

    private func finishLocalSession(
        _ playtime: PlaytimeCoordinator,
        sessionID: UUID,
        elapsed: TimeInterval,
        directory: URL
    ) throws {
        try playtime.credit(sessionID: sessionID, elapsedSeconds: elapsed)
        let process = try #require(PlaytimeProcessIdentity.read(pid: getpid()))
        var journal = PlaytimeJournal(
            sessionID: sessionID,
            process: process,
            startedUptime: ProcessInfo.processInfo.systemUptime - elapsed
        )
        journal.elapsedSeconds = elapsed
        journal.isFinished = true
        try journal.save(in: directory)
        try playtime.reconcile()
    }

    private func eventually(_ condition: @MainActor () -> Bool) async throws {
        for _ in 0..<300 {
            if condition() { return }
            try await Task.sleep(for: .milliseconds(5))
        }
        #expect(condition())
    }

    @Test(arguments: ["guest", "account:first"])
    func sourceCleanupMustCommitBeforeDelivery(ownerKey: String) async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let api = HakoPlaytimeAPIFixture()
        var failCleanup = true
        var sourceID: UUID?
        let playtime = try PlaytimeCoordinator(
            context: container.mainContext, journalDirectory: root, api: api,
            persist: {
                if failCleanup, let sourceID,
                   container.mainContext.deletedModelsArray.contains(where: {
                       ($0 as? PlaytimeSession)?.id == sourceID
                   }) { throw InstanceFileError.message("cleanup failed") }
                try container.mainContext.save()
            }
        )
        await playtime.updateHakoSession(session(ownerA))
        let xuid = ownerKey == "guest" ? nil : "first"
        let id = try playtime.beginSession(instanceID: UUID(), xuid: xuid)
        sourceID = id
        try playtime.credit(sessionID: id, elapsedSeconds: 10)
        // Another local session's time must survive this submission's acknowledgement.
        let other = try playtime.beginSession(instanceID: UUID(), xuid: xuid)
        try playtime.credit(sessionID: other, elapsedSeconds: 7)
        var journal = PlaytimeJournal(
            sessionID: id, process: .init(pid: -1, startSeconds: 0, startMicroseconds: 0),
            startedUptime: 0, hakoOwnerID: ownerA
        )
        journal.elapsedSeconds = 10
        journal.isFinished = true
        try journal.save(in: root)
        #expect(throws: (any Error).self) { try playtime.reconcile() }
        await playtime.retryHakoSync()
        #expect(await api.posts.isEmpty)
        #expect(playtime.totalSeconds(xuid: xuid) == 17)
        failCleanup = false
        try playtime.reconcile()
        await playtime.retryHakoSync()
        try await eventually { (try? container.mainContext.fetch(FetchDescriptor<HakoPlaytimeSubmission>()).isEmpty) == true }
        #expect(playtime.totalSeconds(xuid: xuid) == 7)
        // Replaying the orphan journal after a successful acknowledgement must be harmless.
        try journal.save(in: root)
        try playtime.reconcile()
        await playtime.retryHakoSync()
        #expect(playtime.totalSeconds(xuid: xuid) == 7)
        #expect(await api.posts.filter { $0.sessionID == id }.count == 1)
    }

    @Test func wireContractUsesBearerSnakeCaseAndMatchingUUID() async throws {
        let session = HakoPlaytimeTestURLProtocol.session()
        defer { session.invalidateAndCancel() }
        let sessionID = UUID(uuidString: "33333333-3333-4333-8333-333333333333")!
        HakoPlaytimeTestURLProtocol.prepare([
            "hako.test/v1/playtime/sessions": .json(["session_id": sessionID.uuidString.lowercased()]),
            "hako.test/v1/playtime": .json(["total_duration_ms": 654321]),
        ])
        let api = HakoPlaytimeAPIClient(session: session, baseURL: URL(string: "https://hako.test:443/")!)

        #expect(try await api.postSession(sessionID: sessionID, durationMS: 12345, token: "hako-token") == sessionID)
        #expect(try await api.totalDuration(token: "hako-token") == 654321)
        let requests = HakoPlaytimeTestURLProtocol.requests
        #expect(requests.map(\.httpMethod) == ["POST", "GET"])
        #expect(requests.allSatisfy { $0.value(forHTTPHeaderField: "Authorization") == "Bearer hako-token" })
        let body = try #require(HakoPlaytimeTestURLProtocol.bodies[0].flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] })
        #expect(body["session_id"] as? String == sessionID.uuidString)
        #expect((body["duration_ms"] as? NSNumber)?.int64Value == 12345)
        #expect(requests[0].url?.path == "/v1/playtime/sessions")
        #expect(requests[1].url?.path == "/v1/playtime")
    }

    @Test func wireRejectsConflictMismatchedUUIDAndNegativeDuration() async throws {
        let urlSession = HakoPlaytimeTestURLProtocol.session()
        defer { urlSession.invalidateAndCancel() }
        let api = HakoPlaytimeAPIClient(session: urlSession, baseURL: URL(string: "https://hako.test:443/")!)
        let sessionID = UUID()

        HakoPlaytimeTestURLProtocol.prepare([
            "hako.test/v1/playtime/sessions": .init(data: Data(), statusCode: 409),
        ])
        await #expect(throws: HakoPlaytimeAPIError.conflict) {
            try await api.postSession(sessionID: sessionID, durationMS: 1, token: "token")
        }

        HakoPlaytimeTestURLProtocol.prepare([
            "hako.test/v1/playtime/sessions": .json(["session_id": UUID().uuidString]),
        ])
        await #expect(throws: HakoAuthError.invalidResponse) {
            try await api.postSession(sessionID: sessionID, durationMS: 1, token: "token")
        }

        await #expect(throws: HakoAuthError.invalidRequest) {
            try await api.postSession(sessionID: sessionID, durationMS: -1, token: "token")
        }
    }

    @Test func historyReservationRetriesSameUUIDAcrossRestartAndSubtractsOnlyItsLocalTotals() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let storeURL = root.appendingPathComponent("playtime.store")
        let api = HakoPlaytimeAPIFixture()
        await api.loseNextPostResponse()
        let seed = try await persistHistoryBeforeLostResponse(storeURL: storeURL, root: root, api: api)
        let observation = try await deliverPersistedSubmissionAfterRestart(
            seed,
            storeURL: storeURL,
            root: root,
            api: api
        )
        #expect(observation.posts.map(\.sessionID) == [seed.submissionID, seed.submissionID])
        #expect(observation.posts.map(\.durationMS) == [300_000, 300_000])
        #expect(observation.remoteTotalMS == 300_000)
        #expect(observation.queueIsEmpty)
        #expect(observation.guestTotal == 0 && observation.microsoftTotal == 0)
        #expect(observation.guestInstanceTotal == 100 && observation.microsoftInstanceTotal == 200)
    }

    private func persistHistoryBeforeLostResponse(
        storeURL: URL,
        root: URL,
        api: HakoPlaytimeAPIFixture
    ) async throws -> RestartSeed {
        let container = try persistentContainer(at: storeURL)
        let playtime = try coordinator(container, root: root, api: api, retryDelays: [.seconds(30)])
        let guestInstanceID = UUID()
        let microsoftInstanceID = UUID()
        let guestSession = try playtime.beginSession(instanceID: guestInstanceID, xuid: nil)
        let microsoftSession = try playtime.beginSession(instanceID: microsoftInstanceID, xuid: "first")
        try finishLocalSession(playtime, sessionID: guestSession, elapsed: 100, directory: root)
        try finishLocalSession(playtime, sessionID: microsoftSession, elapsed: 200, directory: root)
        let instanceRows = try container.mainContext.fetch(FetchDescriptor<InstancePlaytime>())
        #expect(instanceRows.first(where: { $0.ownerKey == "guest" })?.totalSeconds == 100)
        #expect(instanceRows.first(where: { $0.ownerKey == "account:first" })?.totalSeconds == 200)

        await playtime.updateHakoSession(session(ownerA, token: "profile-a"))
        await api.waitForCompletedPostCount(1)
        let queued = try #require(try container.mainContext.fetch(FetchDescriptor<HakoPlaytimeSubmission>()).first)
        #expect(queued.hakoOwnerID == ownerA)
        #expect(queued.durationMS == 300_000)
        #expect(try queued.localAllocations() == ["guest": 100_000, "account:first": 200_000])
        await playtime.updateHakoSession(nil)
        return RestartSeed(
            submissionID: queued.sessionID,
            guestInstanceID: guestInstanceID,
            microsoftInstanceID: microsoftInstanceID
        )
    }

    private func deliverPersistedSubmissionAfterRestart(
        _ seed: RestartSeed,
        storeURL: URL,
        root: URL,
        api: HakoPlaytimeAPIFixture
    ) async throws -> RestartObservation {
        let container = try persistentContainer(at: storeURL)
        let playtime = try coordinator(container, root: root, api: api, retryDelays: [.milliseconds(5)])
        let queued = try #require(try container.mainContext.fetch(FetchDescriptor<HakoPlaytimeSubmission>()).first)
        #expect(queued.sessionID == seed.submissionID)
        #expect(queued.hakoOwnerID == ownerA)
        #expect(queued.durationMS == 300_000)
        #expect(try queued.localAllocations() == ["guest": 100_000, "account:first": 200_000])

        // Reopening the on-disk store retries the same UUID after the server accepted the first request.
        await playtime.updateHakoSession(session(ownerA, token: "profile-a-reloaded"))
        await api.waitForCompletedPostCount(2)
        try await eventually { playtime.totalSeconds(xuid: nil) == 0 && playtime.totalSeconds(xuid: "first") == 0 }
        let observation = RestartObservation(
            posts: await api.posts,
            remoteTotalMS: await api.remoteTotalMS,
            queueIsEmpty: try ModelContext(container).fetch(FetchDescriptor<HakoPlaytimeSubmission>()).isEmpty,
            guestTotal: playtime.totalSeconds(xuid: nil),
            microsoftTotal: playtime.totalSeconds(xuid: "first"),
            guestInstanceTotal: playtime.instanceSeconds(seed.guestInstanceID, xuid: nil),
            microsoftInstanceTotal: playtime.instanceSeconds(seed.microsoftInstanceID, xuid: "first")
        )
        await playtime.updateHakoSession(nil)
        return observation
    }

    @Test func newAccrualDuringHistoryImportRemainsPendingAfterAtomicAcknowledgement() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let api = HakoPlaytimeAPIFixture()
        await api.blockNextPost()
        let playtime = try coordinator(container, root: root, api: api)
        let old = try playtime.beginSession(instanceID: UUID(), xuid: "first")
        try finishLocalSession(playtime, sessionID: old, elapsed: 100, directory: root)

        await playtime.updateHakoSession(session(ownerA, token: "profile-a"))
        await api.waitUntilPostIsBlocked()
        let newSession = try playtime.beginSession(instanceID: UUID(), xuid: "first")
        try playtime.credit(sessionID: newSession, elapsedSeconds: 25)
        await api.releaseBlockedPost()

        try await eventually { playtime.totalSeconds(xuid: "first") == 25 }
        #expect(playtime.hakoPendingSeconds(ownerID: ownerA) == 25)
        #expect(try container.mainContext.fetch(FetchDescriptor<HakoPlaytimeSubmission>()).isEmpty)
    }

    @Test func historyReservationExcludesActiveSegmentsOwnedByOtherHakoProfilesAfterRotationFailure() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let game = Process()
        game.executableURL = URL(fileURLWithPath: "/bin/sleep")
        game.arguments = ["60"]
        try game.run()
        defer { if game.isRunning { game.terminate() } }
        let fakeHelper = Process()
        fakeHelper.executableURL = URL(fileURLWithPath: "/bin/sleep")
        fakeHelper.arguments = ["60"]
        try fakeHelper.run()
        defer { if fakeHelper.isRunning { fakeHelper.terminate() } }
        let player = PlayerPlaytime(ownerKey: "guest")
        player.totalSeconds = 90
        let active = PlaytimeSession(instanceID: UUID(), ownerKey: "guest", hakoOwnerID: ownerB)
        active.creditedSeconds = 90
        let gameIdentity = try #require(PlaytimeProcessIdentity.read(pid: game.processIdentifier))
        let helperIdentity = try #require(PlaytimeProcessIdentity.read(pid: fakeHelper.processIdentifier))
        container.mainContext.insert(player)
        container.mainContext.insert(active)
        try container.mainContext.save()
        var journal = PlaytimeJournal(
            sessionID: active.id,
            process: gameIdentity,
            startedUptime: ProcessInfo.processInfo.systemUptime - 90,
            hakoOwnerID: ownerB
        )
        journal.elapsedSeconds = 90
        journal.helper = helperIdentity
        try journal.save(in: root)
        let playtime = try coordinator(
            container,
            root: root,
            api: HakoPlaytimeAPIFixture(),
            rotationPollInterval: .milliseconds(1)
        )

        await playtime.updateHakoSession(session(ownerA))

        #expect(playtime.hakoPendingSeconds(ownerID: ownerA) == 0)
        #expect(try container.mainContext.fetch(FetchDescriptor<HakoPlaytimeSubmission>()).isEmpty)
        #expect(playtime.totalSeconds(xuid: nil) == 90)
        fakeHelper.terminate()
        fakeHelper.waitUntilExit()
        game.terminate()
        game.waitUntilExit()
    }

    @Test func concurrentReconcilePreservesTheFinishedJournalBeingRotated() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let api = HakoPlaytimeAPIFixture()
        let helperExecutable = SkinTestFixtures.bundle.bundleURL.deletingLastPathComponent().appendingPathComponent("HakoPlaytimeHelper")
        let playtime = try coordinator(
            container,
            root: root,
            api: api,
            helper: PlaytimeHelperClient(executable: helperExecutable)
        )
        await playtime.updateHakoSession(session(ownerA))
        let game = Process()
        game.executableURL = URL(fileURLWithPath: "/bin/sleep")
        game.arguments = ["30"]
        try game.run()
        defer { if game.isRunning { game.terminate() } }
        let process = try #require(PlaytimeProcessIdentity.read(pid: game.processIdentifier))
        let oldID = try playtime.beginSession(instanceID: UUID(), xuid: nil)
        var journal = PlaytimeJournal(sessionID: oldID, process: process, startedUptime: ProcessInfo.processInfo.systemUptime - 5, hakoOwnerID: ownerA)
        journal.elapsedSeconds = 5
        try journal.save(in: root)

        let rotation = Task { await playtime.updateHakoSession(session(ownerB)) }
        try await eventually { (try? PlaytimeJournal.load(from: oldID, directory: root).isFinished) == true }
        try playtime.reconcile()
        await rotation.value

        let continued = try container.mainContext.fetch(FetchDescriptor<PlaytimeSession>())
        #expect(continued.count == 1)
        #expect(continued.first?.id != oldID)
        #expect(continued.first?.hakoOwnerID == ownerB)
        let continuedID = try #require(continued.first).id
        #expect(try PlaytimeJournal.load(from: continuedID, directory: root).isFinished == false)
        game.terminate()
        game.waitUntilExit()
        try await eventually { (try? PlaytimeJournal.load(from: continuedID, directory: root).isFinished) == true }
        try await eventually { (try? PlaytimeJournal.load(from: continuedID, directory: root).helper?.isRunning) == false }
        await playtime.updateHakoSession(nil)
    }

    @Test func finishedJournalCannotUploadTimeAfterItsOwnershipBoundary() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let api = HakoPlaytimeAPIFixture()
        let helperExecutable = SkinTestFixtures.bundle.bundleURL.deletingLastPathComponent().appendingPathComponent("HakoPlaytimeHelper")
        let playtime = try coordinator(
            container,
            root: root,
            api: api,
            helper: PlaytimeHelperClient(executable: helperExecutable)
        )
        await playtime.updateHakoSession(session(ownerA))
        let game = Process()
        game.executableURL = URL(fileURLWithPath: "/bin/sleep")
        game.arguments = ["30"]
        try game.run()
        defer { if game.isRunning { game.terminate() } }
        let process = try #require(PlaytimeProcessIdentity.read(pid: game.processIdentifier))
        let startedUptime = ProcessInfo.processInfo.systemUptime - 10
        let sessionID = try playtime.beginSession(instanceID: UUID(), xuid: nil)
        var journal = PlaytimeJournal(
            sessionID: sessionID,
            process: process,
            startedUptime: startedUptime,
            hakoOwnerID: ownerA
        )
        journal.elapsedSeconds = 10.8
        journal.isFinished = true
        try journal.save(in: root)
        try await Task.sleep(for: .milliseconds(200))

        let beforeTransition = ProcessInfo.processInfo.systemUptime
        await playtime.updateHakoSession(session(ownerB))
        let afterTransition = ProcessInfo.processInfo.systemUptime

        let submission = try #require(try container.mainContext.fetch(FetchDescriptor<HakoPlaytimeSubmission>()).first)
        let earliestAllowedMS = Int64(((beforeTransition - startedUptime) * 1000).rounded(.down))
        let latestAllowedMS = Int64(((afterTransition - startedUptime) * 1000).rounded(.up))
        #expect(submission.durationMS >= earliestAllowedMS)
        #expect(submission.durationMS <= latestAllowedMS)

        game.terminate()
        game.waitUntilExit()
        await playtime.updateHakoSession(nil)
    }

    @Test func failedLogoutSplitRecoversDurablyAfterRestartWithoutRestoringHakoLogin() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let storeURL = root.appendingPathComponent("playtime.store")
        let game = Process()
        game.executableURL = URL(fileURLWithPath: "/bin/sleep")
        game.arguments = ["60"]
        try game.run()
        defer { if game.isRunning { game.terminate() } }
        let fakeHelper = Process()
        fakeHelper.executableURL = URL(fileURLWithPath: "/bin/sleep")
        fakeHelper.arguments = ["60"]
        try fakeHelper.run()
        defer { if fakeHelper.isRunning { fakeHelper.terminate() } }
        let gameIdentity = try #require(PlaytimeProcessIdentity.read(pid: game.processIdentifier))
        let helperIdentity = try #require(PlaytimeProcessIdentity.read(pid: fakeHelper.processIdentifier))
        let oldID: UUID
        let blockedSplitURL: URL
        let expectedDurationMS: Int64
        let expectedContinuationStart: TimeInterval
        do {
            let container = try persistentContainer(at: storeURL)
            let api = HakoPlaytimeAPIFixture()
            let playtime = try coordinator(container, root: root, api: api, rotationPollInterval: .milliseconds(1))
            await playtime.updateHakoSession(session(ownerA))
            oldID = try playtime.beginSession(instanceID: UUID(), xuid: nil)
            let startedUptime = ProcessInfo.processInfo.systemUptime - 5
            var journal = PlaytimeJournal(
                sessionID: oldID,
                process: gameIdentity,
                startedUptime: startedUptime,
                hakoOwnerID: ownerA
            )
            journal.elapsedSeconds = 5
            journal.helper = helperIdentity
            try journal.save(in: root)
            blockedSplitURL = journal.splitRequestURL(in: root)
            try FileManager.default.createDirectory(at: blockedSplitURL, withIntermediateDirectories: false)

            await playtime.updateHakoSession(nil)

            #expect(playtime.hakoOwnerID(sessionID: oldID) == ownerA)
            #expect(playtime.hakoSyncErrorMessage != nil)
            #expect(try container.mainContext.fetch(FetchDescriptor<PlaytimeSession>()).count == 1)
            let pendingRotation = try #require(try container.mainContext.fetch(FetchDescriptor<PlaytimeSession>()).first)
            #expect(pendingRotation.rotationPending)
            let requestedUptime = try #require(pendingRotation.rotationRequestedUptime)
            expectedDurationMS = Int64(((requestedUptime - startedUptime) * 1000).rounded(.down))
            expectedContinuationStart = requestedUptime
        }
        fakeHelper.terminate()
        fakeHelper.waitUntilExit()
        try FileManager.default.removeItem(at: blockedSplitURL)

        let restartedContainer = try persistentContainer(at: storeURL)
        let helperExecutable = SkinTestFixtures.bundle.bundleURL.deletingLastPathComponent().appendingPathComponent("HakoPlaytimeHelper")
        let recoveryAPI = HakoPlaytimeAPIFixture()
        let restarted = try coordinator(
            restartedContainer,
            root: root,
            api: recoveryAPI,
            helper: PlaytimeHelperClient(executable: helperExecutable)
        )
        await restarted.updateHakoSession(nil)
        try await eventually {
            guard let rows = try? restartedContainer.mainContext.fetch(FetchDescriptor<PlaytimeSession>()) else { return false }
            return rows.count == 1 && rows.first?.id != oldID && FileManager.default.fileExists(atPath: root.appendingPathComponent("\(rows[0].id.uuidString).json").path)
        }
        let active = try #require(try restartedContainer.mainContext.fetch(FetchDescriptor<PlaytimeSession>()).first)
        #expect(active.ownerKey == "guest")
        #expect(active.hakoOwnerID == nil)
        let continuationJournal = try PlaytimeJournal.load(from: active.id, directory: root)
        #expect(abs(continuationJournal.startedUptime - expectedContinuationStart) < 0.001)
        #expect(restarted.hakoServerSeconds(ownerID: ownerA) == nil)
        let oldSubmission = try #require(try restartedContainer.mainContext.fetch(FetchDescriptor<HakoPlaytimeSubmission>()).first)
        #expect(oldSubmission.sessionID == oldID)
        #expect(oldSubmission.hakoOwnerID == ownerA)
        #expect(oldSubmission.durationMS == expectedDurationMS)
        #expect(await recoveryAPI.posts.isEmpty)
        game.terminate()
        game.waitUntilExit()
        try await eventually {
            guard let row = try? restartedContainer.mainContext.fetch(FetchDescriptor<PlaytimeSession>()).first else { return true }
            return (try? PlaytimeJournal.load(from: row.id, directory: root).isFinished) == true
        }
        try restarted.reconcile()
    }

    @Test func failedProfileSplitStaysVisibleAfterServerRefreshAndClearsOnRecovery() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let container = try container()
        let api = HakoPlaytimeAPIFixture()
        let game = Process()
        game.executableURL = URL(fileURLWithPath: "/bin/sleep")
        game.arguments = ["60"]
        try game.run()
        defer { if game.isRunning { game.terminate() } }
        let fakeHelper = Process()
        fakeHelper.executableURL = URL(fileURLWithPath: "/bin/sleep")
        fakeHelper.arguments = ["60"]
        try fakeHelper.run()
        defer { if fakeHelper.isRunning { fakeHelper.terminate() } }
        let gameIdentity = try #require(PlaytimeProcessIdentity.read(pid: game.processIdentifier))
        let helperIdentity = try #require(PlaytimeProcessIdentity.read(pid: fakeHelper.processIdentifier))
        let helperExecutable = SkinTestFixtures.bundle.bundleURL.deletingLastPathComponent().appendingPathComponent("HakoPlaytimeHelper")
        let playtime = try coordinator(
            container,
            root: root,
            api: api,
            helper: PlaytimeHelperClient(executable: helperExecutable),
            rotationPollInterval: .milliseconds(1)
        )
        await playtime.updateHakoSession(session(ownerA))
        let oldID = try playtime.beginSession(instanceID: UUID(), xuid: nil)
        let startedUptime = ProcessInfo.processInfo.systemUptime - 5
        var journal = PlaytimeJournal(
            sessionID: oldID,
            process: gameIdentity,
            startedUptime: startedUptime,
            hakoOwnerID: ownerA
        )
        journal.elapsedSeconds = 5
        journal.helper = helperIdentity
        try journal.save(in: root)

        await playtime.updateHakoSession(session(ownerB))

        #expect(playtime.hakoServerSeconds(ownerID: ownerB) == 0)
        #expect(playtime.hakoOwnerID(sessionID: oldID) == ownerA)
        #expect(playtime.hakoSyncErrorMessage != nil)
        fakeHelper.terminate()
        fakeHelper.waitUntilExit()
        await playtime.updateHakoSession(session(ownerB))

        #expect(playtime.hakoSyncErrorMessage == nil)
        let continuation = try #require(try container.mainContext.fetch(FetchDescriptor<PlaytimeSession>()).first)
        #expect(continuation.id != oldID && continuation.hakoOwnerID == ownerB)
        let originalSubmission = try #require(try container.mainContext.fetch(FetchDescriptor<HakoPlaytimeSubmission>()).first)
        #expect(originalSubmission.sessionID == oldID && originalSubmission.hakoOwnerID == ownerA)
        let oldSegmentRequests = await api.posts.filter { $0.sessionID == oldID }
        #expect(oldSegmentRequests.allSatisfy { $0.token == session(ownerA).token })

        game.terminate()
        game.waitUntilExit()
        await playtime.updateHakoSession(nil)
    }

    @Test func permanentConflictDoesNotStarveLaterReservationsAndSurvivesRestart() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let storeURL = root.appendingPathComponent("playtime.store")
        let blockedID = UUID(uuidString: "00000000-0000-4000-8000-000000000001")!
        let deliverableID = UUID(uuidString: "00000000-0000-4000-8000-000000000002")!
        let api = HakoPlaytimeAPIFixture()
        await api.conflict(sessionID: blockedID)
        do {
            let container = try persistentContainer(at: storeURL)
            let guest = PlayerPlaytime(ownerKey: "guest")
            guest.totalSeconds = 10
            let microsoft = PlayerPlaytime(ownerKey: "account:first")
            microsoft.totalSeconds = 20
            container.mainContext.insert(guest)
            container.mainContext.insert(microsoft)
            container.mainContext.insert(try HakoPlaytimeSubmission(sessionID: blockedID, hakoOwnerID: ownerA, durationMS: 10_000, localAllocations: ["guest": 10_000]))
            container.mainContext.insert(try HakoPlaytimeSubmission(sessionID: deliverableID, hakoOwnerID: ownerA, durationMS: 20_000, localAllocations: ["account:first": 20_000]))
            try container.mainContext.save()
            let playtime = try coordinator(container, root: root, api: api, retryDelays: [.milliseconds(5)])

            await playtime.updateHakoSession(session(ownerA))
            await api.waitForPostCount(2)
            try await eventually { playtime.totalSeconds(xuid: "first") == 0 }
            #expect(await api.posts.contains(where: { $0.sessionID == deliverableID }))
            #expect(try container.mainContext.fetch(FetchDescriptor<HakoPlaytimeSubmission>()).map(\.sessionID) == [blockedID])
            #expect(playtime.totalSeconds(xuid: nil) == 10)
            #expect(playtime.totalSeconds(xuid: "first") == 0)
            await playtime.updateHakoSession(nil)
        }

        do {
            let restartedContainer = try persistentContainer(at: storeURL)
            let restarted = try coordinator(restartedContainer, root: root, api: api, retryDelays: [.milliseconds(5)])
            await restarted.updateHakoSession(session(ownerA))
            await api.waitForPostCount(3)
            #expect(await api.posts.last?.sessionID == blockedID)
            #expect(try restartedContainer.mainContext.fetch(FetchDescriptor<HakoPlaytimeSubmission>()).map(\.sessionID) == [blockedID])
            #expect(restarted.totalSeconds(xuid: nil) == 10)
            await restarted.updateHakoSession(nil)
        }
    }

    @Test func failedAtomicAcknowledgementRetainsQueueAndLocalTotal() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let context = container.mainContext
        let api = HakoPlaytimeAPIFixture()
        await api.blockNextPost()
        var failSave = false
        let playtime = try PlaytimeCoordinator(
            context: context,
            journalDirectory: root,
            api: api,
            retryDelays: [.seconds(30)],
            persist: {
                try context.save()
            },
            saveAcknowledgement: { acknowledgementContext in
                if failSave { throw InstanceFileError.message("Test save failure") }
                try acknowledgementContext.save()
            }
        )
        let sessionID = try playtime.beginSession(instanceID: UUID(), xuid: "first")
        try finishLocalSession(playtime, sessionID: sessionID, elapsed: 80, directory: root)

        await playtime.updateHakoSession(session(ownerA, token: "profile-a"))
        await api.waitUntilPostIsBlocked()
        let submissionID = try #require(try context.fetch(FetchDescriptor<HakoPlaytimeSubmission>()).first?.sessionID)
        failSave = true
        await api.releaseBlockedPost()
        await api.waitForCompletedPostCount(1)
        try await eventually { playtime.hakoSyncErrorMessage != nil }

        #expect(playtime.totalSeconds(xuid: "first") == 80)
        #expect(try ModelContext(container).fetch(FetchDescriptor<HakoPlaytimeSubmission>()).first?.sessionID == submissionID)
        failSave = false
        await playtime.retryHakoSync()
        try await eventually { playtime.totalSeconds(xuid: "first") == 0 }
        #expect(try ModelContext(container).fetch(FetchDescriptor<HakoPlaytimeSubmission>()).isEmpty)
        #expect(await api.posts.map(\.sessionID) == [submissionID, submissionID])
    }

    @Test func expiredOfflineAndDifferentHakoProfilesNeverConsumeAnotherOwnersQueue() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let context = container.mainContext
        let player = PlayerPlaytime(ownerKey: "guest")
        player.totalSeconds = 40
        context.insert(player)
        let reservedID = UUID()
        context.insert(try HakoPlaytimeSubmission(
            sessionID: reservedID,
            hakoOwnerID: ownerA,
            durationMS: 40_000,
            localAllocations: ["guest": 40_000]
        ))
        try context.save()
        let api = HakoPlaytimeAPIFixture()
        await api.failNextPost(count: 1)
        let playtime = try coordinator(container, root: root, api: api, retryDelays: [.seconds(30)])

        await playtime.updateHakoSession(session(ownerA, token: "expired", expiry: .now.addingTimeInterval(-1)))
        #expect(await api.posts.isEmpty)
        await playtime.updateHakoSession(session(ownerB, token: "profile-b"))
        #expect(await api.posts.isEmpty)
        #expect(playtime.hakoPendingSeconds(ownerID: ownerA) == 40)

        await playtime.updateHakoSession(session(ownerA, token: "profile-a"))
        await api.waitForCompletedPostCount(1)
        #expect(await api.posts.first?.token == "profile-a")
        #expect(try context.fetch(FetchDescriptor<HakoPlaytimeSubmission>()).first?.sessionID == reservedID)
        #expect(playtime.totalSeconds(xuid: nil) == 40)
        await playtime.updateHakoSession(nil)
    }

    @Test func restoredAuthAndProfileEntryRetryPlaytimeQueue() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let api = HakoPlaytimeAPIFixture()
        let playtime = try coordinator(container, root: root, api: api)
        let sessionID = try playtime.beginSession(instanceID: UUID(), xuid: "first")
        try finishLocalSession(playtime, sessionID: sessionID, elapsed: 75, directory: root)

        let restoredSession = session(ownerA, token: "restored-profile")
        let authAPI = HakoAuthTestService(now: .now)
        let auth = HakoAccountCoordinator(
            api: authAPI,
            store: HakoAuthTestSessionStore(session: restoredSession),
            playtime: playtime
        )
        await auth.initialize()
        await api.waitForCompletedPostCount(1)
        try await eventually { playtime.totalSeconds(xuid: "first") == 0 }
        #expect(await api.posts.first?.token == restoredSession.token)

        let previousTotalRefreshCount = await api.fetchedTotalTokens.filter { $0 == restoredSession.token }.count
        await auth.verifyOnProfileEntry()
        #expect(await api.fetchedTotalTokens.filter { $0 == restoredSession.token }.count > previousTotalRefreshCount)
        auth.stop()
    }

    @Test func successfulLoginStartsDeliveryAndLogoutLeavesFailedSubmissionQueued() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let api = HakoPlaytimeAPIFixture()
        await api.failNextPost(count: 1)
        let playtime = try coordinator(container, root: root, api: api, retryDelays: [.seconds(30)])
        let sessionID = try playtime.beginSession(instanceID: UUID(), xuid: nil)
        try finishLocalSession(playtime, sessionID: sessionID, elapsed: 50, directory: root)

        let authAPI = HakoAuthTestService(now: .now)
        let auth = HakoAccountCoordinator(
            api: authAPI,
            store: HakoAuthTestSessionStore(),
            playtime: playtime,
            sleep: { duration in
                if duration > .seconds(60) { try await Task.sleep(for: .seconds(86_400)) }
            }
        )
        await auth.initialize()
        auth.beginLogin(username: "Player_123")
        try await eventually { auth.session != nil }
        await api.waitForCompletedPostCount(1)

        #expect(auth.session?.account.id == ownerA)
        let submission = try #require(try container.mainContext.fetch(FetchDescriptor<HakoPlaytimeSubmission>()).first)
        #expect(await api.posts.first?.sessionID == submission.sessionID)
        #expect(await api.posts.first?.durationMS == 50_000)
        #expect(try submission.localAllocations() == ["guest": 50_000])
        #expect(playtime.totalSeconds(xuid: nil) == 50)
        #expect(await auth.logout() == .signedOut)
        #expect(auth.session == nil)
        #expect(try container.mainContext.fetch(FetchDescriptor<HakoPlaytimeSubmission>()).first?.sessionID == submission.sessionID)
        auth.stop()
    }

    @Test func accountAndHakoChangesSplitAHelperTrackedGameAndPinEverySegmentOwner() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let api = HakoPlaytimeAPIFixture()
        await api.conflictEveryPost(count: 20)
        let helperExecutable = SkinTestFixtures.bundle.bundleURL.deletingLastPathComponent().appendingPathComponent("HakoPlaytimeHelper")
        let helper = PlaytimeHelperClient(executable: helperExecutable)
        let playtime = try coordinator(container, root: root, api: api, helper: helper, retryDelays: [.seconds(30)])
        await playtime.updateHakoSession(session(ownerA, token: "profile-a"))

        let game = Process()
        game.executableURL = URL(fileURLWithPath: "/bin/sleep")
        game.arguments = ["30"]
        try game.run()
        let process = try #require(PlaytimeProcessIdentity.read(pid: game.processIdentifier))
        defer { if game.isRunning { game.terminate() } }

        let firstID = try playtime.beginSession(instanceID: UUID(), xuid: nil)
        var firstJournal = PlaytimeJournal(
            sessionID: firstID,
            process: process,
            startedUptime: ProcessInfo.processInfo.systemUptime - 10,
            hakoOwnerID: ownerA
        )
        firstJournal.elapsedSeconds = 10
        try firstJournal.save(in: root)
        try await helper.attach(to: firstJournal, in: root)

        try await playtime.transferGuest(to: "first")
        let afterMicrosoftLogin = try #require(try container.mainContext.fetch(FetchDescriptor<PlaytimeSession>()).first)
        #expect(afterMicrosoftLogin.id != firstID)
        #expect(afterMicrosoftLogin.ownerKey == "account:first")
        #expect(afterMicrosoftLogin.hakoOwnerID == ownerA)
        let guestQueueItem = try #require(try container.mainContext.fetch(FetchDescriptor<HakoPlaytimeSubmission>()).first(where: { $0.sessionID == firstID }))
        #expect(guestQueueItem.hakoOwnerID == ownerA)
        #expect((try guestQueueItem.localAllocations())["guest", default: 0] >= 10_000)

        await playtime.updateHakoSession(session(ownerB, token: "profile-b"))
        let afterHakoSwitch = try #require(try container.mainContext.fetch(FetchDescriptor<PlaytimeSession>()).first)
        #expect(afterHakoSwitch.id != afterMicrosoftLogin.id)
        #expect(afterHakoSwitch.ownerKey == "account:first")
        #expect(afterHakoSwitch.hakoOwnerID == ownerB)
        #expect(await api.posts.allSatisfy { $0.token != "profile-b" || $0.sessionID != firstID })

        try await playtime.signOutMicrosoft(xuid: "first")
        let afterMicrosoftLogout = try #require(try container.mainContext.fetch(FetchDescriptor<PlaytimeSession>()).first)
        #expect(afterMicrosoftLogout.ownerKey == "guest")
        #expect(afterMicrosoftLogout.hakoOwnerID == ownerB)

        await playtime.updateHakoSession(nil)
        let afterHakoLogout = try #require(try container.mainContext.fetch(FetchDescriptor<PlaytimeSession>()).first)
        #expect(afterHakoLogout.ownerKey == "guest")
        #expect(afterHakoLogout.hakoOwnerID == nil)

        game.terminate()
        game.waitUntilExit()
        for _ in 0..<100 {
            let journal = try? PlaytimeJournal.load(from: afterHakoLogout.id, directory: root)
            if journal?.isFinished == true { break }
            try await Task.sleep(for: .milliseconds(20))
        }
        try playtime.reconcile()
        let queue = try container.mainContext.fetch(FetchDescriptor<HakoPlaytimeSubmission>())
        #expect(queue.contains(where: { $0.sessionID == firstID && $0.hakoOwnerID == ownerA }))
        #expect(queue.allSatisfy { $0.hakoOwnerID == ownerA || $0.hakoOwnerID == ownerB })
    }

    @Test func hakoLoginAndLogoutDuringPlaySplitTheLocalSession() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try container()
        let api = HakoPlaytimeAPIFixture()
        await api.conflictEveryPost(count: 10)
        let executable = SkinTestFixtures.bundle.bundleURL.deletingLastPathComponent().appendingPathComponent("HakoPlaytimeHelper")
        let playtime = try coordinator(
            container,
            root: root,
            api: api,
            helper: PlaytimeHelperClient(executable: executable),
            retryDelays: [.seconds(30)]
        )

        let game = Process()
        game.executableURL = URL(fileURLWithPath: "/bin/sleep")
        game.arguments = ["30"]
        try game.run()
        let process = try #require(PlaytimeProcessIdentity.read(pid: game.processIdentifier))
        defer { if game.isRunning { game.terminate() } }

        let beforeLoginID = try playtime.beginSession(instanceID: UUID(), xuid: nil)
        var beforeLogin = PlaytimeJournal(
            sessionID: beforeLoginID,
            process: process,
            startedUptime: ProcessInfo.processInfo.systemUptime - 2
        )
        beforeLogin.elapsedSeconds = 2
        try beforeLogin.save(in: root)
        try await PlaytimeHelperClient(executable: executable).attach(to: beforeLogin, in: root)

        await playtime.updateHakoSession(session(ownerA, token: "profile-a"))
        let afterLogin = try #require(try container.mainContext.fetch(FetchDescriptor<PlaytimeSession>()).first)
        #expect(afterLogin.id != beforeLoginID)
        #expect(afterLogin.hakoOwnerID == ownerA)
        #expect(try container.mainContext.fetch(FetchDescriptor<HakoPlaytimeSubmission>()).allSatisfy { $0.sessionID != beforeLoginID })

        try await Task.sleep(for: .milliseconds(100))
        await playtime.updateHakoSession(nil)
        let afterLogout = try #require(try container.mainContext.fetch(FetchDescriptor<PlaytimeSession>()).first)
        #expect(afterLogout.id != afterLogin.id)
        #expect(afterLogout.hakoOwnerID == nil)

        game.terminate()
        game.waitUntilExit()
        for _ in 0..<100 {
            let journal = try? PlaytimeJournal.load(from: afterLogout.id, directory: root)
            if journal?.isFinished == true { break }
            try await Task.sleep(for: .milliseconds(20))
        }
        try playtime.reconcile()
        let queue = try container.mainContext.fetch(FetchDescriptor<HakoPlaytimeSubmission>())
        #expect(queue.contains(where: { $0.hakoOwnerID == ownerA && $0.sessionID == afterLogin.id }))
        #expect(queue.allSatisfy { $0.hakoOwnerID == ownerA })
    }

    @Test func oldPlaytimeSessionSchemaMigratesWithoutLosingItsSavedSession() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let storeURL = root.appendingPathComponent("legacy.store")
        try Self.verifyAdditiveMigration(at: storeURL)
    }

    private static func verifyAdditiveMigration(at storeURL: URL) throws {
        do {
            let old = try ModelContainer(
                for: Schema(versionedSchema: LegacyHakoPlaytimeSchema.self),
                configurations: [ModelConfiguration(url: storeURL)]
            )
            let context = old.mainContext
            context.insert(Account(xbox: .init(xuid: "legacy", gamertag: "Player", avatarURL: nil), email: nil))
            let instance = GameInstance(name: "Pack", folderName: "Pack", versionID: "v", metadataURL: "url", metadataSHA1: "sha")
            context.insert(instance)
            let player = PlayerPlaytime(ownerKey: "guest")
            player.totalSeconds = 90
            context.insert(player)
            let instanceTime = InstancePlaytime(ownerKey: "guest", instanceID: instance.id)
            instanceTime.totalSeconds = 75
            context.insert(instanceTime)
            let session = LegacyHakoPlaytimeSchema.PlaytimeSession(instanceID: instance.id, ownerKey: "guest")
            session.creditedSeconds = 30
            context.insert(session)
            try context.save()
        }
        let migrated = try ModelContainer(
            for: Schema(versionedSchema: CurrentHakoPlaytimeSchema.self),
            migrationPlan: HakoPlaytimeMigrationPlan.self,
            configurations: [ModelConfiguration(url: storeURL)]
        )
        #expect(try migrated.mainContext.fetch(FetchDescriptor<Account>()).first?.xuid == "legacy")
        #expect(try migrated.mainContext.fetch(FetchDescriptor<PlayerPlaytime>()).first?.totalSeconds == 90)
        #expect(try migrated.mainContext.fetch(FetchDescriptor<InstancePlaytime>()).first?.totalSeconds == 75)
        let migratedSession = try #require(try migrated.mainContext.fetch(FetchDescriptor<PlaytimeSession>()).first)
        #expect(migratedSession.creditedSeconds == 30)
        #expect(migratedSession.hakoOwnerID == nil)
        #expect(!migratedSession.rotationPending)
        #expect(migratedSession.rotationTargetOwnerKey == nil && migratedSession.rotationTargetHakoOwnerID == nil)
        #expect(migratedSession.rotationContinuationID == nil)
        #expect(migratedSession.rotationRequestedUptime == nil)
        #expect(try migrated.mainContext.fetch(FetchDescriptor<HakoPlaytimeSubmission>()).isEmpty)
    }
}

private actor HakoPlaytimeAPIFixture: HakoPlaytimeAPI {
    struct Request: Equatable, Sendable {
        let sessionID: UUID
        let durationMS: Int64
        let token: String
    }

    private var posted: [Request] = []
    private var completedPostCount = 0
    private var remoteSessions: [UUID: Int64] = [:]
    private var lostResponses = 0
    private var failedPosts = 0
    private var conflicts = 0
    private var conflictSessionIDs: Set<UUID> = []
    private var blockNext = false
    private var blockedStarted = false
    private var blockedRelease: CheckedContinuation<Void, Never>?
    private var postCountWaiters: [(Int, CheckedContinuation<Void, Never>)] = []
    private var completionWaiters: [(Int, CheckedContinuation<Void, Never>)] = []
    private var blockWaiter: CheckedContinuation<Void, Never>?
    private var totalTokens: [String] = []

    var posts: [Request] { posted }
    var remoteTotalMS: Int64 { remoteSessions.values.reduce(0, +) }
    var fetchedTotalTokens: [String] { totalTokens }

    func totalDuration(token: String) async throws -> Int64 {
        totalTokens.append(token)
        return remoteTotalMS
    }

    func postSession(sessionID: UUID, durationMS: Int64, token: String) async throws -> UUID {
        posted.append(.init(sessionID: sessionID, durationMS: durationMS, token: token))
        notify(&postCountWaiters, observed: posted.count)
        if blockNext {
            blockNext = false
            blockedStarted = true
            blockWaiter?.resume()
            blockWaiter = nil
            await withCheckedContinuation { blockedRelease = $0 }
        }
        if conflicts > 0 || conflictSessionIDs.contains(sessionID) {
            if conflicts > 0 { conflicts -= 1 }
            markPostCompleted()
            throw HakoPlaytimeAPIError.conflict
        }
        if failedPosts > 0 {
            failedPosts -= 1
            markPostCompleted()
            throw HakoAuthError.network
        }
        if lostResponses > 0 {
            lostResponses -= 1
            if remoteSessions[sessionID] == nil { remoteSessions[sessionID] = durationMS }
            markPostCompleted()
            throw HakoAuthError.network
        }
        if remoteSessions[sessionID] == nil { remoteSessions[sessionID] = durationMS }
        markPostCompleted()
        return sessionID
    }

    func waitForPostCount(_ count: Int) async {
        if posted.count >= count { return }
        await withCheckedContinuation { postCountWaiters.append((count, $0)) }
    }

    func waitForCompletedPostCount(_ count: Int) async {
        if completedPostCount >= count { return }
        await withCheckedContinuation { completionWaiters.append((count, $0)) }
    }

    func blockNextPost() { blockNext = true; blockedStarted = false }

    func waitUntilPostIsBlocked() async {
        if blockedStarted { return }
        await withCheckedContinuation { blockWaiter = $0 }
    }

    func releaseBlockedPost() {
        blockedRelease?.resume()
        blockedRelease = nil
    }

    func loseNextPostResponse() { lostResponses += 1 }
    func failNextPost(count: Int) { failedPosts += count }
    func conflictEveryPost(count: Int) { conflicts += count }
    func conflict(sessionID: UUID) { conflictSessionIDs.insert(sessionID) }

    private func markPostCompleted() {
        completedPostCount += 1
        notify(&completionWaiters, observed: completedPostCount)
    }

    private func notify(_ waiters: inout [(Int, CheckedContinuation<Void, Never>)], observed: Int) {
        let ready = waiters.filter { $0.0 <= observed }
        waiters.removeAll { $0.0 <= observed }
        for (_, continuation) in ready { continuation.resume() }
    }
}

private extension PlaytimeJournal {
    static func load(from sessionID: UUID, directory: URL) throws -> Self {
        try load(from: directory.appendingPathComponent("\(sessionID.uuidString).json"))
    }
}

nonisolated private final class HakoPlaytimeTestURLProtocol: URLProtocol, @unchecked Sendable {
    struct StubResponse {
        let data: Data
        var statusCode = 200
        var headers = ["Content-Type": "application/json"]

        static func json(_ object: [String: Any]) -> StubResponse {
            StubResponse(data: try! JSONSerialization.data(withJSONObject: object))
        }
    }

    private static let lock = NSLock()
    private nonisolated(unsafe) static var routes: [String: StubResponse] = [:]
    private nonisolated(unsafe) static var recorded: [URLRequest] = []
    private nonisolated(unsafe) static var recordedBodies: [Data?] = []
    static var requests: [URLRequest] { lock.withLock { recorded } }
    static var bodies: [Data?] { lock.withLock { recordedBodies } }

    static func prepare(_ routes: [String: StubResponse]) {
        lock.withLock { self.routes = routes; recorded = []; recordedBodies = [] }
    }

    static func session() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [HakoPlaytimeTestURLProtocol.self]
        return URLSession(configuration: configuration)
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        guard let url = request.url else { return }
        let result = Self.lock.withLock { () -> StubResponse in
            Self.recorded.append(request)
            Self.recordedBodies.append(Self.body(of: request))
            return Self.routes["\(url.host ?? "")\(url.path)"] ?? StubResponse(data: Data(), statusCode: 404)
        }
        let response = HTTPURLResponse(url: url, statusCode: result.statusCode, httpVersion: nil, headerFields: result.headers)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: result.data)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}

    private static func body(of request: URLRequest) -> Data? {
        if let body = request.httpBody { return body }
        guard let stream = request.httpBodyStream else { return nil }
        stream.open()
        defer { stream.close() }
        var result = Data()
        var buffer = [UInt8](repeating: 0, count: 4096)
        while stream.hasBytesAvailable {
            let count = stream.read(&buffer, maxLength: buffer.count)
            if count <= 0 { break }
            result.append(buffer, count: count)
        }
        return result.isEmpty ? nil : result
    }
}

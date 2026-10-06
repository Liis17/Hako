import Foundation
import SwiftData
import Darwin
import Testing

@Suite(.serialized) @MainActor struct MinecraftLaunchTests {
    private let root = URL(fileURLWithPath: "/tmp/Hako Launch Fixtures")
    private func manifest(_ version: String) throws -> MinecraftVersionManifest {
        let url = try #require(SkinTestFixtures.bundle.url(forResource: version, withExtension: "json"))
        return try JSONDecoder().decode(MinecraftVersionManifest.self, from: Data(contentsOf: url))
    }
    private func plan(_ version: String, source: LaunchArgumentSource = .mojang, parameters: InstanceParameters = .init(), platform: MinecraftPlatform = .appleSilicon, assets: MinecraftAssetIndex? = nil) throws -> MinecraftLaunchPlan {
        try .build(manifest: manifest(version), root: root, executable: root.appendingPathComponent("java/bin/java"), identity: .offline(name: "Player"), source: source, parameters: parameters, assetIndex: assets, platform: platform)
    }
    private func eventually(_ condition: () -> Bool) async throws {
        for _ in 0..<100 { if condition() { return }; try await Task.sleep(for: .milliseconds(20)) }
        #expect(condition())
    }

    @Test func officialArgumentsUseNativeLibrariesRecommendedJvmAndCustomResolution() throws {
        var parameters = InstanceParameters(); parameters.maximumMemoryMiB = 1024; parameters.windowWidth = 960; parameters.windowHeight = 540
        let arguments = try plan("26.3", parameters: parameters).arguments
        #expect(arguments.contains("-XX:+UseZGC") && arguments.contains("-XX:+UseCompactObjectHeaders"))
        #expect(arguments.contains("-Xms1024M") && arguments.contains("-Xmx1024M") && !arguments.contains("-Xmx4G"))
        #expect(arguments.contains("-XstartOnFirstThread") && !arguments.contains("-Xss1M"))
        #expect(!arguments.contains("--demo") && !arguments.contains("--quickPlayPath"))
        let cp = arguments[try #require(arguments.firstIndex(of: "-cp")) + 1]
        #expect(cp.contains("natives-macos-arm64.jar") && !cp.contains("natives-macos.jar"))
        #expect(arguments[try #require(arguments.firstIndex(of: "--width")) + 1] == "960")
        #expect(!arguments.contains(where: { $0.contains("${") }))
    }

    @Test func argumentPreviewSharesLaunchRulesMemoryAndWindowWithoutSessionValues() throws {
        var parameters = InstanceParameters(); parameters.maximumMemoryMiB = 1024; parameters.windowWidth = 960; parameters.windowHeight = 540
        let modern = try MinecraftLaunchPlan.argumentTemplates(manifest: manifest("26.3"), source: .mojang, parameters: parameters, platform: .appleSilicon)
        #expect(modern.java.contains("-XX:+UseZGC") && modern.java.contains("-Xms1024M") && modern.java.contains("-Xmx1024M"))
        #expect(modern.minecraft.contains("${auth_access_token}") && modern.minecraft.contains("${game_directory}"))
        #expect(modern.minecraft[try #require(modern.minecraft.firstIndex(of: "--width")) + 1] == "960")
        #expect(!modern.minecraft.contains("--demo") && !modern.minecraft.contains("--quickPlayPath"))
        #expect(try LaunchArguments.parse(LaunchArguments.format(modern.java)) == modern.java)
        parameters.fullscreen = true
        let legacy = try MinecraftLaunchPlan.argumentTemplates(manifest: manifest("1.6.4"), source: .mojang, parameters: parameters, platform: .intel)
        #expect(legacy.java.contains("-XstartOnFirstThread") && legacy.java.contains("-Xmx1024M") && !legacy.java.contains("-XX:+UseZGC"))
        #expect(legacy.minecraft.contains("--fullscreen") && !legacy.minecraft.contains("--width"))
    }

    @Test func argumentFormattingPreservesQuotedValues() throws {
        let arguments = ["-Dos.name=Mac OS X", "-Dquote=\"value\"", "a\\b", "it's", "", "--version", "26.3"]
        #expect(try LaunchArguments.parse(LaunchArguments.format(arguments)) == arguments)
    }

    @Test func customArgumentsKeepMandatoryLaunchStructureAndWindowWins() throws {
        var parameters = InstanceParameters(); parameters.javaArguments = "-Dmessage='hello world' -Xmx8G"; parameters.minecraftArguments = "--server localhost"; parameters.fullscreen = true; parameters.maximumMemoryMiB = 2048
        let custom = try plan("1.19", source: .custom, parameters: parameters).arguments
        #expect(custom.contains("-Dmessage=hello world") && custom.contains("-Xmx2048M") && custom.contains("-cp"))
        #expect(custom.contains("--fullscreen") && !custom.contains("--width"))
        #expect(custom.suffix(2) == ["--server", "localhost"])
        let official = try plan("26.3", parameters: parameters).arguments
        #expect(!official.contains("-Dmessage=hello world"))
        parameters.minecraftArguments = "--username SomeoneElse"
        #expect(throws: InstanceFileError.self) { try plan("1.19", source: .global, parameters: parameters) }
    }

    @Test func legacyArgumentsUseVirtualAssetsAndExtractedNativeDirectory() throws {
        let assets = try JSONDecoder().decode(MinecraftAssetIndex.self, from: Data(#"{"objects":{},"virtual":true}"#.utf8))
        let legacy = try plan("1.6.4", platform: .intel, assets: assets)
        #expect(legacy.arguments.contains("-XstartOnFirstThread"))
        #expect(legacy.arguments.contains(root.appendingPathComponent("minecraft/assets/virtual/legacy").path))
        #expect(legacy.arguments[try #require(legacy.arguments.firstIndex(of: "--session")) + 1] == "0")
        let cp = legacy.arguments[try #require(legacy.arguments.firstIndex(of: "-cp")) + 1]
        #expect(!cp.contains("natives-osx") && cp.contains("1.6.4.jar"))
    }

    @Test func offlineIdentityMatchesJavaNameUuidAndRemainsPerName() throws {
        #expect(try MinecraftLaunchIdentity.offline(name: "Notch").uuid == "b50ad385829d3141a2167e7d7539ba7f")
        #expect(try MinecraftLaunchIdentity.offline(name: "Player").uuid == "a01e3843e5213998958af459800e4d11")
        #expect(try MinecraftLaunchIdentity.offline(name: "Player").uuid != MinecraftLaunchIdentity.offline(name: "player").uuid)
    }

    @Test func sessionRefreshKeepsNewRefreshTokenAfterMinecraftFailureThenCanRetry() async throws {
        let container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let account = Account(xbox: .init(xuid: "test", gamertag: "Steve", avatarURL: nil), email: nil)
        container.mainContext.insert(account)
        var saved = AccountTokens(microsoftRefreshToken: "old"), refreshCalls = 0, fail = true
        let clock = Date(timeIntervalSince1970: 1000)
        let sessions = MinecraftSessionCoordinator(context: container.mainContext, dependencies: .init(load: { _ in saved }, save: { value, _ in saved = value }, refresh: { value in
            #expect(value == saved.microsoftRefreshToken); refreshCalls += 1
            return .init(accessToken: "ms-secret", refreshToken: "new-\(refreshCalls)", idToken: nil)
        }, signIn: { _ in
            if fail { throw MicrosoftAuthError.appNotApproved }
            return .init(profile: .init(uuid: "uuid", name: "Steve", skinURL: nil), accessToken: "mc-secret", expiration: clock.addingTimeInterval(3600))
        }, now: { clock }))
        await sessions.connect(account)
        #expect(saved.microsoftRefreshToken == "new-1" && sessions.identity(for: account) == nil)
        await sessions.connect(account)
        #expect(refreshCalls == 1)
        fail = false
        await sessions.connect(account, force: true)
        #expect(sessions.identity(for: account)?.accessToken == "mc-secret" && account.minecraftName == "Steve")
        await sessions.connect(account)
        #expect(refreshCalls == 2)
    }

    @Test func expiredTokensNeverAuthorizeAndConcurrentRefreshIsShared() async throws {
        let container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let account = Account(xbox: .init(xuid: "test", gamertag: "Steve", avatarURL: nil), email: nil)
        account.minecraftUUID = "uuid"; account.minecraftName = "Steve"; container.mainContext.insert(account)
        var date = Date(timeIntervalSince1970: 1000), calls = 0
        var saved = AccountTokens(microsoftRefreshToken: "old", minecraftAccessToken: "expired", minecraftTokenExpiration: date.addingTimeInterval(-1))
        let sessions = MinecraftSessionCoordinator(context: container.mainContext, dependencies: .init(load: { _ in saved }, save: { value, _ in saved = value }, refresh: { _ in
            calls += 1; try await Task.sleep(for: .milliseconds(30)); return .init(accessToken: "ms", refreshToken: "new", idToken: nil)
        }, signIn: { _ in .init(profile: .init(uuid: "uuid", name: "Steve", skinURL: nil), accessToken: "valid", expiration: date.addingTimeInterval(3600)) }, now: { date }))
        #expect(sessions.identity(for: account) == nil)
        async let first: Void = sessions.connect(account)
        async let second: Void = sessions.connect(account)
        _ = await (first, second)
        #expect(calls == 1 && sessions.identity(for: account)?.accessToken == "valid")
        date = date.addingTimeInterval(7200)
        #expect(sessions.identity(for: account) == nil)
        sessions.signOut()
        #expect(sessions.identity(for: account) == nil)
    }

    @Test func coordinatorStartsOfflineOnceRestoresProcessAndBlocksRename() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let store = InstanceStore(context: container.mainContext, storage: .init(root: root))
        var draft = InstanceDraft(); draft.name = "Pack"; draft.offlineMode = true
        let instance = try store.create(draft, versionID: "v", metadataURL: "url", metadataSHA1: "sha")
        instance.state = .ready
        let sessions = MinecraftSessionCoordinator(context: container.mainContext, dependencies: .init(load: { _ in Issue.record("Offline requested auth"); return nil }))
        let probe = LaunchProbe()
        let playtime = try PlaytimeCoordinator(context: container.mainContext, journalDirectory: root.appendingPathComponent("sessions"))
        let runner = GameProcessRunner(helperURL: SkinTestFixtures.bundle.bundleURL.deletingLastPathComponent().appendingPathComponent("HakoPlaytimeHelper"))
        let games = GameLaunchCoordinator(store: store, sessions: sessions, playtime: playtime, runner: runner, prepare: { request in
            await probe.add(request.identity); try await Task.sleep(for: .milliseconds(40))
            return .init(executable: URL(fileURLWithPath: "/bin/sleep"), arguments: ["20"], workingDirectory: request.root.appendingPathComponent("minecraft"))
        })
        #expect(games.disabledReason(instance, account: nil) == nil)
        games.launch(instance, account: nil); games.launch(instance, account: nil)
        try await eventually { games.states[instance.id] == .running }
        #expect(await probe.count == 1)
        let loaded = try GameProcessRecord.load(in: root.appendingPathComponent("Pack"))
        let record = try #require(loaded)
        defer { if record.isRunning { kill(record.pid, SIGTERM) } }
        #expect(record.isRunning)
        let wrongBirth = GameProcessRecord(instanceID: instance.id, pid: record.pid, startSeconds: record.startSeconds - 1, startMicroseconds: record.startMicroseconds)
        #expect(!wrongBirth.isRunning)
        let restored = GameLaunchCoordinator(store: store, sessions: sessions, playtime: playtime, runner: GameProcessRunner(helperURL: SkinTestFixtures.bundle.bundleURL.deletingLastPathComponent().appendingPathComponent("HakoPlaytimeHelper"))); restored.start()
        #expect(restored.states[instance.id] == .running)
        var edit = InstanceDraft(instance: instance); edit.name = "Renamed"
        #expect(throws: InstanceFileError.self) { try store.update(instance, with: edit) }
        kill(record.pid, SIGTERM)
        try await eventually { !store.launchBusy.contains(instance.id) && restored.states[instance.id] == nil }
        #expect(try GameProcessRecord.load(in: root.appendingPathComponent("Pack")) == nil)
        #expect(games.disabledReason(instance, account: nil) == nil)
        instance.offlineMode = false
        #expect(games.disabledReason(instance, account: nil) != nil)
    }

    @Test func processReportsExitErrorAndDoesNotLeaveLiveRecord() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root.appendingPathComponent("minecraft"), withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sink = ExitProbe(), runner = GameProcessRunner()
        _ = try await runner.start(.init(executable: URL(fileURLWithPath: "/usr/bin/false"), arguments: [], workingDirectory: root.appendingPathComponent("minecraft")), id: UUID(), root: root) { status in await sink.add(status) }
        for _ in 0..<100 { if await sink.count > 0 { break }; try await Task.sleep(for: .milliseconds(20)) }
        #expect(await sink.status == 1)
        #expect(try GameProcessRecord.load(in: root)?.isRunning != true)
    }

    @Test func trackedProcessKeepsItsSessionAndCreditsAfterExit() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let playtime = try PlaytimeCoordinator(context: container.mainContext, journalDirectory: root.appendingPathComponent("sessions"))
        let instanceID = UUID(), sessionID = try playtime.beginSession(instanceID: instanceID, xuid: "first")
        let helper = SkinTestFixtures.bundle.bundleURL.deletingLastPathComponent().appendingPathComponent("HakoPlaytimeHelper")
        let runner = GameProcessRunner(helperURL: helper)
        let sink = ExitProbe()
        let record = try await runner.start(.init(executable: URL(fileURLWithPath: "/bin/sleep"), arguments: ["20"], workingDirectory: root), id: instanceID, root: root, tracking: .init(sessionID: sessionID, directory: playtime.journalDirectory)) { status in await sink.add(status) }
        defer { if record.isRunning { kill(record.pid, SIGTERM) } }
        #expect(record.sessionID == sessionID && record.isRunning)
        let loaded = try GameProcessRecord.load(in: root)
        let restored = try #require(loaded)
        #expect(restored.sessionID == sessionID && restored.isRunning)
        try await Task.sleep(for: .milliseconds(80))
        kill(record.pid, SIGTERM)
        let journalURL = playtime.journalDirectory.appendingPathComponent("\(sessionID.uuidString).json")
        try await eventually { (try? PlaytimeJournal.load(from: journalURL).isFinished) == true }
        try playtime.reconcile()
        #expect(playtime.totalSeconds(xuid: "first") > 0)
        #expect(playtime.instanceSeconds(instanceID, xuid: "first") == playtime.totalSeconds(xuid: "first"))
    }

    @Test func unavailableHelperStopsOnlyTheProcessStartedForThisAttempt() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("sessions"), session = UUID()
        let runner = GameProcessRunner(helperURL: root.appendingPathComponent("missing-helper"))
        await #expect(throws: InstanceFileError.self) {
            try await runner.start(.init(executable: URL(fileURLWithPath: "/bin/sleep"), arguments: ["20"], workingDirectory: root), id: UUID(), root: root, tracking: .init(sessionID: session, directory: directory)) { _ in }
        }
        let journal = try PlaytimeJournal.load(from: directory.appendingPathComponent("\(session.uuidString).json"))
        #expect(!journal.process.isRunning && journal.isFinished)
    }

    @Test func veryShortTrackedProcessStillRecordsTimeAndItsExitStatus() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let session = UUID(), directory = root.appendingPathComponent("sessions"), sink = ExitProbe()
        let helper = SkinTestFixtures.bundle.bundleURL.deletingLastPathComponent().appendingPathComponent("HakoPlaytimeHelper")
        let runner = GameProcessRunner(helperURL: helper)
        _ = try await runner.start(.init(executable: URL(fileURLWithPath: "/usr/bin/false"), arguments: [], workingDirectory: root), id: UUID(), root: root, tracking: .init(sessionID: session, directory: directory)) { status in await sink.add(status) }
        for _ in 0..<100 { if await sink.count > 0 { break }; try await Task.sleep(for: .milliseconds(20)) }
        let journal = try PlaytimeJournal.load(from: directory.appendingPathComponent("\(session.uuidString).json"))
        #expect(await sink.status == 1)
        #expect(journal.isFinished && journal.elapsedSeconds > 0)
    }

    @Test(arguments: [false, true]) func failedPreparationOrProcessStartDoesNotAccumulateTime(missingExecutable: Bool) async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let store = InstanceStore(context: container.mainContext, storage: .init(root: root))
        var draft = InstanceDraft(); draft.name = "Pack"; draft.offlineMode = true
        let instance = try store.create(draft, versionID: "v", metadataURL: "url", metadataSHA1: "sha")
        instance.state = .ready
        let playtime = try PlaytimeCoordinator(context: container.mainContext, journalDirectory: root.appendingPathComponent("sessions"))
        let games = GameLaunchCoordinator(store: store, sessions: MinecraftSessionCoordinator(context: container.mainContext), playtime: playtime, prepare: { request in
            if !missingExecutable { throw InstanceFileError.message("Test preparation failure") }
            return .init(executable: root.appendingPathComponent("missing-java"), arguments: [], workingDirectory: request.root)
        })
        games.launch(instance, account: nil)
        try await eventually { if case .failed = games.states[instance.id] { true } else { false } }
        #expect(playtime.totalSeconds(xuid: nil) == 0 && playtime.instanceSeconds(instance.id, xuid: nil) == 0)
        #expect(!store.launchBusy.contains(instance.id))
    }

    @Test func legacyRunningRecordStartsTrackingAtRecovery() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let store = InstanceStore(context: container.mainContext, storage: .init(root: root))
        var draft = InstanceDraft(); draft.name = "Pack"
        let instance = try store.create(draft, versionID: "v", metadataURL: "url", metadataSHA1: "sha")
        let gameRoot = root.appendingPathComponent("Pack")
        let runner = GameProcessRunner(helperURL: SkinTestFixtures.bundle.bundleURL.deletingLastPathComponent().appendingPathComponent("HakoPlaytimeHelper"))
        let original = try await runner.start(.init(executable: URL(fileURLWithPath: "/bin/sleep"), arguments: ["20"], workingDirectory: gameRoot), id: instance.id, root: gameRoot) { _ in }
        defer { if original.isRunning { kill(original.pid, SIGTERM) } }
        #expect(original.sessionID == nil)
        let playtime = try PlaytimeCoordinator(context: container.mainContext, journalDirectory: root.appendingPathComponent("sessions"))
        let recoveryUptime = ProcessInfo.processInfo.systemUptime
        let restoredRunner = GameProcessRunner(helperURL: SkinTestFixtures.bundle.bundleURL.deletingLastPathComponent().appendingPathComponent("HakoPlaytimeHelper"))
        let games = GameLaunchCoordinator(store: store, sessions: MinecraftSessionCoordinator(context: container.mainContext), playtime: playtime, runner: restoredRunner)
        games.start()
        try await eventually { (try? GameProcessRecord.load(in: gameRoot)?.sessionID) != nil }
        let loaded = try GameProcessRecord.load(in: gameRoot)
        let record = try #require(loaded)
        let session = try #require(record.sessionID)
        let url = playtime.journalDirectory.appendingPathComponent("\(session.uuidString).json")
        try await eventually { (try? PlaytimeJournal.load(from: url).helper?.isRunning) == true }
        let journal = try PlaytimeJournal.load(from: url)
        #expect(record.isRunning && journal.startedUptime >= recoveryUptime)
        kill(original.pid, SIGTERM)
        try await eventually { !store.launchBusy.contains(instance.id) }
    }

    @Test func javaOverrideRequiresExecutableVersionAndNativeArchitecture() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let java = root.appendingPathComponent("java")
        func write(version: String, architecture: String) throws {
            try Data("#!/bin/sh\nprintf 'java.version = \(version)\\nos.arch = \(architecture)\\n'\n".utf8).write(to: java)
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: java.path)
        }
        try write(version: "25.0.1", architecture: "aarch64")
        try await JavaLaunchValidation.validate(java, minimumMajor: 25, platform: .appleSilicon)
        await #expect(throws: InstanceFileError.self) { try await JavaLaunchValidation.validate(java, minimumMajor: 25, platform: .intel) }
        try write(version: "1.8.0_74", architecture: "x86_64")
        await #expect(throws: InstanceFileError.self) { try await JavaLaunchValidation.validate(java, minimumMajor: 17, platform: .intel) }
        try await JavaLaunchValidation.validate(java, minimumMajor: 8, platform: .intel)
        try FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: java.path)
        await #expect(throws: InstanceFileError.self) { try await JavaLaunchValidation.validate(java, minimumMajor: 8) }
    }

    @Test func javaProbeTimeoutStopsExecutableIgnoringTermination() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let java = root.appendingPathComponent("java"), pidFile = root.appendingPathComponent("pid")
        try Data("#!/bin/sh\necho $$ > '\(pidFile.path)'\ntrap '' TERM\nwhile :; do :; done\n".utf8).write(to: java)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: java.path)
        let started = ContinuousClock.now
        // Учитываем задержку запуска shell при параллельном прогоне, чтобы проверить уже работающий процесс.
        await #expect(throws: InstanceFileError.self) { try await JavaLaunchValidation.validate(java, minimumMajor: 8, timeout: .seconds(3)) }
        #expect(ContinuousClock.now - started < .seconds(5))
        let pid = try #require(Int32(String(contentsOf: pidFile, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)))
        #expect(kill(pid, 0) == -1 && errno == ESRCH)
    }
}

private actor LaunchProbe { var count = 0; func add(_ identity: MinecraftLaunchIdentity) { count += 1 } }
private actor ExitProbe { var count = 0; var status: Int32?; func add(_ status: Int32?) { count += 1; self.status = status } }

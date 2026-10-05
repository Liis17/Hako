import Foundation
import SwiftData
import Testing

@MainActor struct LaunchSettingsTests {
    private func instance() -> GameInstance { GameInstance(name: "Pack", folderName: "Pack", versionID: "v", metadataURL: "url", metadataSHA1: "hash") }

    @Test func sourceKeepsWindowAndLocalDraftIndependent() throws {
        let suite = "hako.launch.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let instance = instance()
        instance.javaArguments = "-Dlocal=true"; instance.windowWidth = 960
        defaults.set("-Dglobal=true", forKey: GameLaunchDefaults.Key.javaArguments)
        defaults.set(2048, forKey: GameLaunchDefaults.Key.maximumMemoryMiB)
        defaults.set(1920, forKey: GameLaunchDefaults.Key.windowWidth)
        #expect(instance.argumentSource == .mojang)
        instance.argumentSource = .global
        #expect(instance.effectiveParameters(from: defaults).windowWidth == 960)
        #expect(instance.effectiveParameters(from: defaults).javaArguments == "-Dglobal=true")
        #expect(instance.effectiveParameters(from: defaults).maximumMemoryMiB == 2048)
        defaults.set("-Dchanged=true", forKey: GameLaunchDefaults.Key.javaArguments)
        #expect(instance.effectiveParameters(from: defaults).javaArguments == "-Dchanged=true")
        instance.argumentSource = .mojang
        #expect(instance.effectiveParameters(from: defaults).javaArguments.isEmpty)
        instance.argumentSource = .custom
        #expect(instance.effectiveParameters(from: defaults).javaArguments == "-Dlocal=true")
        #expect(instance.windowWidth == 960)
    }

    @Test func migrationPreservesProfilesAndSnapshotsOldGlobalWindowOnce() throws {
        let suite = "hako.migration.\(UUID().uuidString)", defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let container = try ModelContainer(for: Account.self, GameInstance.self, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let global = instance(), custom = instance()
        global.argumentSourceRaw = nil; global.usesGlobalParameters = true
        custom.argumentSourceRaw = nil; custom.usesGlobalParameters = false; custom.javaArguments = "-Xmx3G"; custom.windowWidth = 800
        container.mainContext.insert(global); container.mainContext.insert(custom)
        container.mainContext.insert(Account(xbox: .init(xuid: "preserved", gamertag: "Steve", avatarURL: nil), email: nil))
        defaults.set("-Xmx6G", forKey: GameLaunchDefaults.Key.javaArguments)
        defaults.set(1920, forKey: GameLaunchDefaults.Key.windowWidth)
        try LaunchSettingsMigration.run(context: container.mainContext, defaults: defaults, memory: .init(physicalBytes: 16 * 1073741824))
        #expect(global.argumentSource == .global && global.windowWidth == 1920)
        #expect(custom.argumentSource == .custom && custom.windowWidth == 800 && custom.maximumMemoryMiB == 3072)
        #expect(defaults.integer(forKey: GameLaunchDefaults.Key.maximumMemoryMiB) == 6144)
        #expect(try container.mainContext.fetch(FetchDescriptor<Account>()).first?.xuid == "preserved")
        defaults.set(1280, forKey: GameLaunchDefaults.Key.windowWidth)
        try LaunchSettingsMigration.run(context: container.mainContext, defaults: defaults)
        #expect(global.windowWidth == 1920)
    }

    @Test func memoryFollowsHardwareAndWarnsOnlyAboveSeventyPercent() {
        let small = JavaMemoryPolicy(physicalBytes: 8 * 1073741824), large = JavaMemoryPolicy(physicalBytes: 32 * 1073741824)
        #expect(small.initialMiB == 2048 && large.initialMiB == 4096)
        #expect(small.maximumMiB == 8192 && large.maximumMiB == 32768)
        #expect(small.normalize(20000) == 8192 && small.normalize(1) == 512)
        #expect(!small.warns(5632) && small.warns(5888))
    }

    @Test func memoryOverridesEveryMaximumAndBoundsInitialHeap() throws {
        let text = "-Xmx8G -XX:MaxHeapSize=6g -Xms4G -XX:InitialHeapSize=3g -Dlabel='hello world' -Xmx 7G"
        let parsed = try LaunchArguments.parse(text)
        #expect(LaunchArguments.maximumHeapMiB(in: text) == 7168)
        #expect(LaunchArguments.applyingMemory(parsed, maximumMiB: 2048) == ["-Xms2048M", "-XX:InitialHeapSize=2048M", "-Dlabel=hello world", "-Xmx2048M"])
        #expect(try LaunchArguments.parse("-Xms512M -Dmax=-Xmx8G") == ["-Xms512M", "-Dmax=-Xmx8G"])
    }

    @Test func argumentsAreTokensWithoutShellExpansion() throws {
        #expect(try LaunchArguments.parse("'$(touch file)' \"two words\" empty='' path\\ name") == ["$(touch file)", "two words", "empty=", "path name"])
        #expect(throws: InstanceFileError.self) { try LaunchArguments.parse("'unfinished") }
        #expect(throws: InstanceFileError.self) { try LaunchArguments.parse("trailing\\") }
    }

    @Test func offlineNamesRespectMinecraftBoundaries() {
        for name in ["abc", "Player_123", String(repeating: "A", count: 16)] { #expect(OfflineUsername.isValid(name)) }
        for name in ["ab", "Steve Smith", "Игрок", "Player-1", String(repeating: "A", count: 17)] { #expect(!OfflineUsername.isValid(name)) }
    }

    @Test func oldDiskSchemaRetainsInstanceIdentityAndAccount() throws {
        let suite = "hako.diskmigration.\(UUID().uuidString)", defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let url = root.appendingPathComponent("default.store"), id = UUID()
        do {
            let old = try ModelContainer(for: LegacyLaunchSchema.GameInstance.self, Account.self, configurations: ModelConfiguration(url: url))
            let instance = LegacyLaunchSchema.GameInstance(); instance.id = id; instance.name = "Preserved"; instance.folderName = "Preserved"
            old.mainContext.insert(instance)
            old.mainContext.insert(Account(xbox: .init(xuid: "old-account", gamertag: "Steve", avatarURL: nil), email: nil))
            try old.mainContext.save()
        }
        let updated = try ModelContainer(for: GameInstance.self, Account.self, configurations: ModelConfiguration(url: url))
        let preserved = try #require(updated.mainContext.fetch(FetchDescriptor<GameInstance>()).first)
        #expect(preserved.id == id && preserved.folderName == "Preserved" && preserved.argumentSourceRaw == nil)
        try LaunchSettingsMigration.run(context: updated.mainContext, defaults: defaults)
        #expect(preserved.argumentSource == .global && !preserved.offlineMode)
        #expect(try updated.mainContext.fetch(FetchDescriptor<Account>()).first?.xuid == "old-account")
    }
}

private enum LegacyLaunchSchema {
    @Model final class GameInstance {
        @Attribute(.unique) var id = UUID()
        var name = ""
        var folderName = ""
        var versionID = ""
        var metadataURL = ""
        var metadataSHA1 = ""
        var createdAt = Date()
        var iconSymbol = "shippingbox.fill"
        var iconRevision = UUID()
        var usesGlobalParameters = true
        var javaArguments = ""
        var minecraftArguments = ""
        var fullscreen = false
        var windowWidth = 1280
        var windowHeight = 720
        var installationState = "queued"
        var pauseRequested = false
        var installationError: String?
        var javaMajorVersion = 0
        var javaExecutable = "jre.bundle/Contents/Home/bin/java"
        var legacyTexturepacks = false
        init() {}
    }
}

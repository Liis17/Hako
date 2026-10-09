import CryptoKit
import Foundation
import SwiftData
import Testing

@Suite(.serialized) @MainActor struct ModrinthTests {
    private let api = "api.modrinth.com/v2"

    @Test func searchUsesFacetsForModsAndPacksAndKeepsPlusInQuery() async throws {
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let page = #"{"hits":[{"project_id":"abc","slug":"abc","title":"A","description":"d","icon_url":"","project_type":"mod"}],"total_hits":1}"#
        ModrinthTestProtocol.prepare(["\(api)/search": Data(page.utf8)])
        let client = ModrinthClient(session: session)
        let result = try await client.search("c++", mods: true, minecraft: "1.21.1", sort: .downloads, offset: 20)
        #expect(result.total == 1 && result.hits.first?.iconURL == nil)
        #expect(result.hits.first?.pageURL.absoluteString == "https://modrinth.com/mod/abc")
        _ = try await client.search(" ", mods: false, minecraft: "1.21.1", sort: .relevance, offset: 0)
        let requests = ModrinthTestProtocol.requests.compactMap { URLComponents(url: $0.url!, resolvingAgainstBaseURL: false) }
        #expect(requests.count == 2)
        func item(_ components: URLComponents, _ name: String) -> String? { components.queryItems?.first { $0.name == name }?.value }
        func facets(_ components: URLComponents) throws -> [[String]] { try JSONDecoder().decode([[String]].self, from: Data(try #require(item(components, "facets")).utf8)) }
        #expect(try facets(requests[0]) == [["project_type:mod"], ["categories:fabric"], ["versions:1.21.1"], ["environment!=dedicated_server_only"]])
        #expect(item(requests[0], "query") == "c++" && requests[0].percentEncodedQuery?.contains("c%2B%2B") == true)
        #expect(item(requests[0], "index") == "downloads" && item(requests[0], "offset") == "20" && item(requests[0], "limit") == "20")
        #expect(try facets(requests[1]) == [["project_type:resourcepack"], ["versions:1.21.1"]])
        #expect(item(requests[1], "query") == nil)
    }

    @Test func latestVersionPrefersReleaseAndSkipsForeignVersionsAndBrokenFiles() async throws {
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let bytes = Data("x".utf8)
        var broken = Self.version("broken", project: "root", date: "2026-10-09", file: ("broken.jar", bytes))
        broken["files"] = [["filename": "broken.jar", "url": "https://fixtures.test/broken.jar", "size": 1, "primary": true, "hashes": ["sha1": "a", "sha512": "b"]]]
        let versions = [
            Self.version("beta", project: "root", channel: "beta", date: "2026-10-08", file: ("beta.jar", bytes)),
            Self.version("release", project: "root", date: "2026-10-01", file: ("release.jar", bytes)),
            Self.version("other-minecraft", project: "root", date: "2026-10-07", minecraft: "1.20", file: ("other.jar", bytes)),
            Self.version("other-loader", project: "root", date: "2026-10-07", loader: "quilt", file: ("quilt.jar", bytes)),
            broken
        ]
        ModrinthTestProtocol.prepare(["\(api)/project/root/version": try JSONSerialization.data(withJSONObject: versions)])
        let client = ModrinthClient(session: session)
        #expect(try await client.latestVersion(project: "root", mods: true, minecraft: "test")?.id == "release")
        #expect(try await client.latestVersion(project: "root", mods: true, minecraft: "test", channel: "beta")?.id == "beta")
        #expect(try await client.latestVersion(project: "root", mods: true, minecraft: "test", channel: "alpha") == nil)
        #expect(try await client.compatibleVersions(project: "root", mods: true, minecraft: "test").map(\.id) == ["release", "beta"])
        let packs = [Self.version("pack", project: "root", loader: "minecraft", file: ("pack.zip", bytes)), Self.version("jar", project: "root", loader: "minecraft", file: ("pack.jar", bytes))]
        ModrinthTestProtocol.prepare(["\(api)/project/root/version": try JSONSerialization.data(withJSONObject: packs)])
        #expect(try await client.latestVersion(project: "root", mods: false, minecraft: "test")?.id == "pack")
        await #expect(throws: MojangError.self) { try await client.latestVersion(project: "../root", mods: true, minecraft: "test") }
    }

    @Test func datapacksSearchAllProjectTypesAndSelectOnlyCompatibleZIPs() async throws {
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let bytes = Data("datapack".utf8)
        ModrinthTestProtocol.prepare([
            "\(api)/search": Data(#"{"hits":[{"project_id":"root","title":"Root","project_type":"mod","all_project_types":["mod","datapack"]}],"total_hits":1}"#.utf8),
            "\(api)/project/root/version": try JSONSerialization.data(withJSONObject: [
                Self.version("release", project: "root", loader: "datapack", file: ("pack.zip", bytes)),
                Self.version("beta", project: "root", channel: "beta", loader: "datapack", file: ("beta.zip", bytes)),
                Self.version("mod", project: "root", loader: "fabric", file: ("mod.jar", bytes)),
                Self.version("jar", project: "root", loader: "datapack", file: ("pack.jar", bytes)),
                Self.version("wrong-game", project: "root", minecraft: "other", loader: "datapack", file: ("wrong.zip", bytes))
            ])
        ])
        let client = ModrinthClient(session: session)
        let result = try await client.search("", kind: .datapack, minecraft: "test", sort: .relevance, offset: 0)
        #expect(result.hits.first?.projectType == "mod")
        #expect(try await client.compatibleVersions(project: "root", kind: .datapack, minecraft: "test").map(\.id) == ["release", "beta"])
        #expect(try await client.latestVersion(project: "root", kind: .datapack, minecraft: "test", channel: "beta")?.id == "beta")
        let urls = ModrinthTestProtocol.requests.compactMap { URLComponents(url: $0.url!, resolvingAgainstBaseURL: false) }
        let facets = try #require(urls[0].queryItems?.first { $0.name == "facets" }?.value)
        #expect(try JSONDecoder().decode([[String]].self, from: Data(facets.utf8)) == [["all_project_types:datapack"], ["categories:datapack"], ["versions:test"]])
        #expect(urls[1].queryItems?.first { $0.name == "loaders" }?.value == "[\"datapack\"]")
    }

    @Test func fileVersionsAreCachedOnDiskAndRefreshedAfterChange() async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let file = root.appendingPathComponent("mod.jar"), cache = root.appendingPathComponent("cache")
        try Data("one".utf8).write(to: file)
        let match = [FabricClient.hash(Data("one".utf8)): ["id": "v1", "project_id": "p1", "date_published": "2026-10-01"]]
        ModrinthTestProtocol.prepare(["\(api)/version_files": try JSONSerialization.data(withJSONObject: match)])
        var client = ModrinthClient(session: session, cache: cache)
        #expect(try await client.versions(of: [file])[file]?.projectID == "p1")
        _ = try await client.versions(of: [file])
        #expect(ModrinthTestProtocol.requests.count == 1)
        client = ModrinthClient(session: session, cache: cache)
        #expect(try await client.versions(of: [file])[file]?.versionID == "v1")
        #expect(ModrinthTestProtocol.requests.count == 1)
        try Data("changed".utf8).write(to: file)
        #expect(try await client.versions(of: [file])[file] == nil)
        _ = try await client.versions(of: [file])
        #expect(ModrinthTestProtocol.requests.count == 2)
    }

    @Test func missingDependencyIsOfferedAndInstalledWithOrigins() async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let (container, installations, controller, instance) = try Self.instance(root: root, session: session)
        _ = container
        let rootBytes = Data("root".utf8), depBytes = Data("dep".utf8)
        ModrinthTestProtocol.prepare([
            "\(api)/project/root/version": try JSONSerialization.data(withJSONObject: [Self.version("root-v", project: "root", file: ("root.jar", rootBytes), dependencies: [("dep", "required"), ("opt", "optional")])]),
            "\(api)/projects": Data(#"[{"id":"dep","slug":"dep","title":"Dep","description":"","icon_url":null,"project_type":"mod"}]"#.utf8),
            "\(api)/project/dep/version": try JSONSerialization.data(withJSONObject: [Self.version("dep-v", project: "dep", file: ("dep.jar", depBytes))]),
            "fixtures.test/root.jar": rootBytes, "fixtures.test/dep.jar": depBytes
        ])
        controller.install(try Self.project("root"), in: instance, mods: true)
        #expect(controller.catalogInstalling[instance.id] == "root")
        let confirmation = try await Self.confirmation(controller, installations, instance)
        #expect(confirmation.projects.map(\.title) == ["Dep"])
        #expect(confirmation.action == "Добавить с зависимостями" && confirmation.alternative == "Только мод")
        controller.resolveConfirmation(confirmation.id, choice: .primary)
        try await Self.settle(installations, instance)
        #expect(controller.errors[instance.id] == nil)
        #expect(controller.catalogInstalling[instance.id] == nil)
        let mods = try #require(controller.mods[instance.id])
        #expect(mods.map(\.name) == ["dep.jar", "root.jar"])
        #expect(mods.allSatisfy { $0.source == .modrinth })
        #expect(Set(mods.compactMap(\.origin?.projectID)) == ["dep", "root"])
        #expect(Set(try await controller.installedProjects(instance, mods: true).keys) == ["dep", "root"])
    }

    @Test func registeredFabricAPICountsAsInstalledAndOnlyModSkipsDependencies() async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let (container, installations, controller, instance) = try Self.instance(root: root, session: session)
        _ = container
        let apiBytes = try Data(contentsOf: #require(SkinTestFixtures.bundle.url(forResource: "fabric-api-test", withExtension: "zip")))
        let descriptor = FabricAPIDescriptor(projectID: FabricAPIDescriptor.project, versionID: "api", version: "api", channel: "release", filename: "api.jar", url: URL(string: "https://fixtures.test/api.jar")!, size: Int64(apiBytes.count), sha1: Self.sha1(apiBytes), sha512: FabricClient.hash(apiBytes))
        let cached = root.appendingPathComponent("api.jar"); try apiBytes.write(to: cached)
        try await installations.content.provisionAPI(descriptor, from: cached, in: controller.folder(instance, mods: true))
        let rootBytes = Data("root".utf8), depBytes = Data("dep".utf8)
        ModrinthTestProtocol.prepare([
            "\(api)/project/root/version": try JSONSerialization.data(withJSONObject: [Self.version("root-v", project: "root", file: ("root.jar", rootBytes), dependencies: [(FabricAPIDescriptor.project, "required"), ("dep", "required")])]),
            "\(api)/projects": Data(#"[{"id":"P7dR8mSH","slug":"fabric-api","title":"Fabric API","description":"","icon_url":null,"project_type":"mod"},{"id":"dep","slug":"dep","title":"Dep","description":"","icon_url":null,"project_type":"mod"}]"#.utf8),
            "\(api)/project/dep/version": try JSONSerialization.data(withJSONObject: [Self.version("dep-v", project: "dep", file: ("dep.jar", depBytes))]),
            "fixtures.test/root.jar": rootBytes, "fixtures.test/dep.jar": depBytes
        ])
        controller.install(try Self.project("root"), in: instance, mods: true)
        let confirmation = try await Self.confirmation(controller, installations, instance)
        #expect(confirmation.projects.map(\.title) == ["Dep"])
        controller.resolveConfirmation(confirmation.id, choice: .alternative)
        try await Self.settle(installations, instance)
        #expect(controller.errors[instance.id] == nil)
        #expect(controller.mods[instance.id]?.map(\.name) == ["api.jar", "root.jar"])
        #expect(controller.mods[instance.id]?.first?.origin?.api != nil)
    }

    @Test func corruptedDownloadLeavesInstanceUnchanged() async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let (container, installations, controller, instance) = try Self.instance(root: root, session: session)
        _ = container
        ModrinthTestProtocol.prepare([
            "\(api)/project/root/version": try JSONSerialization.data(withJSONObject: [Self.version("root-v", project: "root", file: ("root.jar", Data("root".utf8)))]),
            "fixtures.test/root.jar": Data("tampered".utf8)
        ])
        controller.install(try Self.project("root"), in: instance, mods: true)
        try await Self.settle(installations, instance)
        #expect(controller.errors[instance.id]?.contains("повреждён") == true)
        #expect(controller.mods[instance.id]?.isEmpty != false)
    }

    @Test func newerLoaderRequirementWarnsAndInstallsOnlyWhenConfirmed() async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let (container, installations, controller, instance) = try Self.instance(root: root, session: session)
        _ = container
        let source = root.appendingPathComponent("jar"); try FileManager.default.createDirectory(at: source, withIntermediateDirectories: true)
        try Data(#"{"schemaVersion":1,"id":"root","depends":{"fabricloader":">=0.20.0"}}"#.utf8).write(to: source.appendingPathComponent("fabric.mod.json"))
        let jar = root.appendingPathComponent("root.jar")
        let zip = Process(); zip.executableURL = URL(fileURLWithPath: "/usr/bin/zip"); zip.currentDirectoryURL = source
        zip.arguments = ["-q", jar.path, "fabric.mod.json"]; try zip.run(); zip.waitUntilExit()
        let bytes = try Data(contentsOf: jar)
        ModrinthTestProtocol.prepare([
            "\(api)/project/root/version": try JSONSerialization.data(withJSONObject: [Self.version("root-v", project: "root", file: ("root.jar", bytes))]),
            "fixtures.test/root.jar": bytes
        ])
        controller.install(try Self.project("root"), in: instance, mods: true)
        var confirmation = try await Self.confirmation(controller, installations, instance)
        #expect(confirmation.title == "Возможна несовместимость" && confirmation.destructive)
        #expect(confirmation.projects.first?.note == "Нужен Fabric Loader >=0.20.0, в сборке 0.19.5")
        controller.resolveConfirmation(confirmation.id, choice: .cancel)
        try await Self.settle(installations, instance)
        #expect(controller.errors[instance.id] == nil)
        #expect(controller.mods[instance.id]?.isEmpty != false)
        controller.install(try Self.project("root"), in: instance, mods: true)
        confirmation = try await Self.confirmation(controller, installations, instance)
        controller.resolveConfirmation(confirmation.id, choice: .primary)
        try await Self.settle(installations, instance)
        #expect(controller.mods[instance.id]?.map(\.name) == ["root.jar"])
    }

    @Test func declaredIncompatibilityWithInstalledModWarns() async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let (container, installations, controller, instance) = try Self.instance(root: root, session: session)
        _ = container
        let other = root.appendingPathComponent("other.jar"); try Data("other".utf8).write(to: other)
        try await installations.content.importItem(from: other, into: controller.folder(instance, mods: true), mods: true)
        let rootBytes = Data("root".utf8)
        ModrinthTestProtocol.prepare([
            "\(api)/project/root/version": try JSONSerialization.data(withJSONObject: [Self.version("root-v", project: "root", file: ("root.jar", rootBytes), dependencies: [("other", "incompatible")])]),
            "\(api)/version_files": try JSONSerialization.data(withJSONObject: [FabricClient.hash(Data("other".utf8)): ["id": "other-v", "project_id": "other", "date_published": "2026-10-01"]]),
            "\(api)/projects": Data(#"[{"id":"other","slug":"other","title":"Other","description":"","icon_url":null,"project_type":"mod"}]"#.utf8),
            "fixtures.test/root.jar": rootBytes
        ])
        controller.install(try Self.project("root"), in: instance, mods: true)
        let confirmation = try await Self.confirmation(controller, installations, instance)
        #expect(confirmation.projects.map(\.title) == ["Other"])
        #expect(confirmation.projects.first?.note == "Несовместим с «Root»")
        controller.resolveConfirmation(confirmation.id, choice: .cancel)
        try await Self.settle(installations, instance)
        #expect(controller.mods[instance.id]?.map(\.name) == ["other.jar"])
    }

    @Test func latestVersionsPreferReleaseAndAskLowerChannelsOnlyForRemainingHashes() async throws {
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let bytes = Data("x".utf8), first = String(repeating: "1", count: 128), second = String(repeating: "2", count: 128)
        let release = Self.version("release", project: "a", date: "2026-10-01", file: ("a.jar", bytes))
        let beta = Self.version("beta", project: "b", channel: "beta", date: "2026-10-05", file: ("b.jar", bytes))
        let bodies = BodyLog()
        ModrinthTestProtocol.prepare([:]) { url, body in
            guard url.path.hasSuffix("version_files/update"), let json = try? JSONSerialization.jsonObject(with: body) as? [String: Any] else { return nil }
            bodies.append(json)
            let channel = (json["version_types"] as? [String])?.first
            return try? JSONSerialization.data(withJSONObject: channel == "release" ? [first: release] : channel == "beta" ? [second: beta] : [:])
        }
        let found = try await ModrinthClient(session: session).latestVersions(for: [first, second], mods: true, minecraft: "test")
        #expect(found[first]?.id == "release" && found[second]?.id == "beta")
        #expect(bodies.values.count == 2)
        #expect(bodies.values.last?["hashes"] as? [String] == [second])
    }

    @Test func modUpdateReplacesFileKeepsItDisabledAndRecordsOrigin() async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let (container, installations, controller, instance) = try Self.instance(root: root, session: session)
        _ = container
        let folder = try controller.folder(instance, mods: true)
        let old = root.appendingPathComponent("old.jar"), oldBytes = Data("old".utf8), newBytes = Data("new".utf8)
        try oldBytes.write(to: old)
        try await installations.content.importItem(from: old, into: folder, mods: true)
        await controller.reload(instance, mods: true)
        try await installations.content.setEnabled(try #require(controller.mods[instance.id]?.first), in: folder, enabled: false)
        let newer = Self.version("new-v", project: "p", date: "2026-10-05", file: ("new.jar", newBytes))
        ModrinthTestProtocol.prepare([
            "\(api)/version_files": try JSONSerialization.data(withJSONObject: [FabricClient.hash(oldBytes): ["id": "old-v", "project_id": "p", "date_published": "2026-10-01"]]),
            "\(api)/version_files/update": try JSONSerialization.data(withJSONObject: [FabricClient.hash(oldBytes): newer]),
            "\(api)/projects": Data(#"[{"id":"p","slug":"p","title":"P","description":"","icon_url":null,"project_type":"mod"}]"#.utf8),
            "fixtures.test/new.jar": newBytes
        ])
        await controller.reload(instance, mods: true)
        await controller.checkModrinthUpdates(instance, mods: true)
        let item = try #require(controller.mods[instance.id]?.first)
        #expect(controller.modrinthUpdate(for: item, in: instance, mods: true)?.version.id == "new-v")
        controller.updateFromModrinth(item, in: instance, mods: true)
        try await Self.settle(installations, instance)
        #expect(controller.errors[instance.id] == nil)
        let updated = try #require(controller.mods[instance.id]?.first)
        #expect(controller.mods[instance.id]?.count == 1 && updated.name == "new.jar.disabled" && !updated.enabled)
        #expect(updated.origin?.projectID == "p" && updated.origin?.versionID == "new-v")
        #expect(controller.modrinthUpdate(for: updated, in: instance, mods: true) == nil)
    }

    @Test func packUpdateIgnoresOlderVersionsAndKeepsPackDisabled() async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let (container, installations, controller, instance) = try Self.instance(root: root, session: session)
        _ = container
        let folder = try controller.folder(instance, mods: false)
        let old = root.appendingPathComponent("old.zip"), oldBytes = Data("old".utf8), newBytes = Data("new".utf8)
        try oldBytes.write(to: old)
        try await installations.content.importItem(from: old, into: folder, mods: false)
        await controller.reload(instance, mods: false)
        try await installations.content.setEnabled(try #require(controller.packs[instance.id]?.first), in: folder, enabled: false, mods: false)
        let match = try JSONSerialization.data(withJSONObject: [FabricClient.hash(oldBytes): ["id": "old-v", "project_id": "p", "date_published": "2026-10-01"]])
        let older = Self.version("older-v", project: "p", date: "2026-09-01", loader: "minecraft", file: ("older.zip", newBytes))
        ModrinthTestProtocol.prepare(["\(api)/version_files": match, "\(api)/version_files/update": try JSONSerialization.data(withJSONObject: [FabricClient.hash(oldBytes): older])])
        await controller.reload(instance, mods: false)
        await controller.checkModrinthUpdates(instance, mods: false)
        let item = try #require(controller.packs[instance.id]?.first)
        #expect(controller.modrinthUpdate(for: item, in: instance, mods: false) == nil)
        let newer = Self.version("new-v", project: "p", date: "2026-10-05", loader: "minecraft", file: ("new.zip", newBytes))
        ModrinthTestProtocol.prepare([
            "\(api)/version_files": match, "\(api)/version_files/update": try JSONSerialization.data(withJSONObject: [FabricClient.hash(oldBytes): newer]),
            "\(api)/projects": Data(#"[{"id":"p","slug":"p","title":"P","description":"","icon_url":null,"project_type":"resourcepack"}]"#.utf8),
            "fixtures.test/new.zip": newBytes
        ])
        await controller.checkModrinthUpdates(instance, mods: false)
        controller.updateFromModrinth(item, in: instance, mods: false)
        try await Self.settle(installations, instance)
        #expect(controller.errors[instance.id] == nil)
        let updated = try #require(controller.packs[instance.id]?.first)
        #expect(controller.packs[instance.id]?.count == 1 && updated.name == "new.zip" && !updated.enabled)
    }

    @Test func updateAllReplacesEveryAvailableFile() async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let (container, installations, controller, instance) = try Self.instance(root: root, session: session)
        _ = container
        let (oldA, oldB) = (Data("old-a".utf8), Data("old-b".utf8))
        try await Self.importMods(["a.jar": oldA, "b.jar": oldB], root: root, controller: controller, installations: installations, instance: instance)
        try Self.prepareUpdates(oldA: oldA, oldB: oldB, dependencyOfA: false)
        await controller.checkModrinthUpdates(instance, mods: true)
        #expect(controller.hasUpdates(instance, mods: true))
        controller.updateAll(instance, mods: true)
        try await Self.settle(installations, instance)
        #expect(controller.errors[instance.id] == nil)
        #expect(Set((controller.mods[instance.id] ?? []).map(\.name)) == ["new-a.jar", "new-b.jar"])
        #expect(!controller.hasUpdates(instance, mods: true))
    }

    @Test func updateAllSkipsUpdateNeedingConfirmation() async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let (container, installations, controller, instance) = try Self.instance(root: root, session: session)
        _ = container
        let (oldA, oldB) = (Data("old-a".utf8), Data("old-b".utf8))
        try await Self.importMods(["a.jar": oldA, "b.jar": oldB], root: root, controller: controller, installations: installations, instance: instance)
        try Self.prepareUpdates(oldA: oldA, oldB: oldB, dependencyOfA: true)
        await controller.checkModrinthUpdates(instance, mods: true)
        controller.updateAll(instance, mods: true)
        try await Self.settle(installations, instance)
        #expect(controller.confirmation == nil)
        #expect(Set((controller.mods[instance.id] ?? []).map(\.name)) == ["a.jar", "new-b.jar"])
        #expect(controller.errors[instance.id]?.hasPrefix("a.jar") == true)
        #expect(controller.hasUpdates(instance, mods: true))
    }

    private static func importMods(_ files: [String: Data], root: URL, controller: InstanceContentController, installations: InstallationCoordinator, instance: GameInstance) async throws {
        let folder = try controller.folder(instance, mods: true)
        for (name, bytes) in files {
            let source = root.appendingPathComponent(name)
            try bytes.write(to: source)
            try await installations.content.importItem(from: source, into: folder, mods: true)
        }
        await controller.reload(instance, mods: true)
    }

    /// Проекты `p` (a.jar) и `q` (b.jar) получают новые версии; с `dependencyOfA` новой версии `p` нужен отсутствующий мод `dep`.
    private static func prepareUpdates(oldA: Data, oldB: Data, dependencyOfA: Bool) throws {
        let (newA, newB) = (Data("new-a".utf8), Data("new-b".utf8))
        let matches = try JSONSerialization.data(withJSONObject: [
            FabricClient.hash(oldA): ["id": "old-a", "project_id": "p", "date_published": "2026-10-01"],
            FabricClient.hash(oldB): ["id": "old-b", "project_id": "q", "date_published": "2026-10-01"]
        ])
        let latest = try JSONSerialization.data(withJSONObject: [
            FabricClient.hash(oldA): version("new-a-v", project: "p", date: "2026-10-05", file: ("new-a.jar", newA), dependencies: dependencyOfA ? [(project: "dep", type: "required")] : []),
            FabricClient.hash(oldB): version("new-b-v", project: "q", date: "2026-10-05", file: ("new-b.jar", newB))
        ])
        let projects = ["p", "q", "dep"].map { ["id": $0, "slug": $0, "title": $0.uppercased(), "description": "", "icon_url": NSNull(), "project_type": "mod"] }
        ModrinthTestProtocol.prepare(["\(Self.apiHost)/version_files": matches, "\(Self.apiHost)/version_files/update": latest, "fixtures.test/new-a.jar": newA, "fixtures.test/new-b.jar": newB]) { url, _ in
            guard url.path.hasSuffix("/projects"),
                  let ids = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first(where: { $0.name == "ids" })?.value,
                  let requested = try? JSONDecoder().decode([String].self, from: Data(ids.utf8)) else { return nil }
            return try? JSONSerialization.data(withJSONObject: projects.filter { requested.contains($0["id"] as? String ?? "") })
        }
    }

    private static let apiHost = "api.modrinth.com/v2"

    private static func temporaryRoot() -> URL { FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString) }

    private static func sha1(_ data: Data) -> String { Insecure.SHA1.hash(data: data).map { String(format: "%02x", $0) }.joined() }

    private static func version(_ id: String, project: String, channel: String = "release", date: String = "2026-10-06", minecraft: String = "test", loader: String = "fabric", file: (name: String, bytes: Data), dependencies: [(project: String, type: String)] = []) -> [String: Any] {
        ["id": id, "project_id": project, "version_number": id, "version_type": channel, "date_published": date, "game_versions": [minecraft], "loaders": [loader],
         "files": [["filename": file.name, "url": "https://fixtures.test/\(file.name)", "size": file.bytes.count, "primary": true, "hashes": ["sha1": sha1(file.bytes), "sha512": FabricClient.hash(file.bytes)]]],
         "dependencies": dependencies.map { ["project_id": $0.project, "version_id": NSNull(), "file_name": NSNull(), "dependency_type": $0.type] }]
    }

    @Test func catalogChannelInstallsOnlyThatChannel() async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let (container, installations, controller, instance) = try Self.instance(root: root, session: session)
        _ = container
        let bytes = Data("alpha".utf8)
        ModrinthTestProtocol.prepare([
            "\(api)/project/root/version": try JSONSerialization.data(withJSONObject: [Self.version("root-alpha", project: "root", channel: "alpha", file: ("root-alpha.jar", bytes))]),
            "fixtures.test/root-alpha.jar": bytes
        ])
        controller.install(try Self.project("root"), in: instance, mods: true)
        try await Self.settle(installations, instance)
        #expect(controller.errors[instance.id] == "У «Root» нет релиза для Minecraft test и Fabric.")
        #expect(controller.mods[instance.id]?.isEmpty != false)
        controller.install(try Self.project("root"), in: instance, mods: true, channel: "alpha")
        try await Self.settle(installations, instance)
        #expect(controller.errors[instance.id] == nil)
        #expect(controller.mods[instance.id]?.map(\.name) == ["root-alpha.jar"])
    }

    @Test func installsDatapackOnlyInSelectedWorldAndRecognizesItThere() async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let (container, installations, controller, instance) = try Self.instance(root: root, session: session)
        _ = container
        instance.modLoaderRaw = ModLoader.vanilla.rawValue
        let world = try Self.world("First", in: instance, installations: installations)
        _ = try Self.world("Second", in: instance, installations: installations)
        let bytes = try Self.datapackZIP(in: root)
        ModrinthTestProtocol.prepare([
            "\(api)/project/root/version": try JSONSerialization.data(withJSONObject: [Self.version("v", project: "root", loader: "datapack", file: ("pack.zip", bytes))]),
            "fixtures.test/pack.zip": bytes,
            "\(api)/version_files": try JSONSerialization.data(withJSONObject: [FabricClient.hash(bytes): ["id": "v", "project_id": "root", "date_published": "2026-10-01"]])
        ])
        let level = try Data(contentsOf: world.appendingPathComponent("level.dat"))
        controller.install(try Self.project("root"), in: instance, target: .worldDatapacks("First"))
        try await Self.settle(installations, instance)
        #expect(controller.errors[instance.id] == nil)
        #expect(try Data(contentsOf: world.appendingPathComponent("datapacks/pack.zip")) == bytes)
        #expect(try Data(contentsOf: world.appendingPathComponent("level.dat")) == level)
        #expect(controller.worldInstallMessages[instance.id]?["First"] != nil)
        #expect(controller.items(instance, target: .worldDatapacks("First")).map(\.name) == ["pack.zip"])
        #expect(try await controller.installedProjects(instance, target: .worldDatapacks("First")).keys.sorted() == ["root"])
        #expect(try await controller.installedProjects(instance, target: .worldDatapacks("Second")).isEmpty)
        #expect(!FileManager.default.fileExists(atPath: world.deletingLastPathComponent().appendingPathComponent("Second/datapacks").path))
        var draft = InstanceDraft(); draft.name = "Other"
        let other = try installations.store.create(draft, versionID: "test", metadataURL: "https://fixtures.test/version", metadataSHA1: "sha")
        _ = try Self.world("First", in: other, installations: installations)
        #expect(try await controller.installedProjects(other, target: .worldDatapacks("First")).isEmpty)
        #expect(try await installations.content.list(at: controller.folder(instance, mods: true), mods: true).isEmpty)
        #expect(try await installations.content.list(at: controller.folder(instance, mods: false), mods: false).isEmpty)
    }

    @Test(arguments: ["primary", "alternative", "cancel"])
    func offersDatapackDependenciesAndRoutesEachKind(choice: String) async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let (container, installations, controller, instance) = try Self.instance(root: root, session: session)
        _ = container
        let world = try Self.world("First", in: instance, installations: installations)
        let bytes = try Self.datapackZIP(in: root), dependency = try Self.datapackZIP(in: root, name: "dep.zip"), mod = Data("mod".utf8), pack = Data("pack".utf8)
        ModrinthTestProtocol.prepare([
            "\(api)/project/root/version": try JSONSerialization.data(withJSONObject: [Self.version("root-v", project: "root", loader: "datapack", file: ("pack.zip", bytes), dependencies: [("data", "required"), ("resource", "required"), ("mod", "required")])]),
            "\(api)/projects": Data(#"[{"id":"data","title":"Data","project_type":"mod","all_project_types":["mod","datapack"]},{"id":"resource","title":"Resource","project_type":"resourcepack"},{"id":"mod","title":"Mod","project_type":"mod"}]"#.utf8),
            "\(api)/project/data/version": try JSONSerialization.data(withJSONObject: [Self.version("data-v", project: "data", loader: "datapack", file: ("dep.zip", dependency))]),
            "\(api)/project/resource/version": try JSONSerialization.data(withJSONObject: [Self.version("resource-v", project: "resource", loader: "minecraft", file: ("resource.zip", pack))]),
            "\(api)/project/mod/version": try JSONSerialization.data(withJSONObject: [Self.version("mod-v", project: "mod", loader: "fabric", file: ("dep.jar", mod))]),
            "fixtures.test/pack.zip": bytes, "fixtures.test/dep.zip": dependency, "fixtures.test/resource.zip": pack, "fixtures.test/dep.jar": mod
        ])
        controller.install(try Self.project("root"), in: instance, target: .worldDatapacks("First"))
        let confirmation = try await Self.confirmation(controller, installations, instance)
        #expect(confirmation.projects.map(\.title) == ["Data", "Resource", "Mod"])
        #expect(confirmation.alternative == "Только датапак" && confirmation.message.contains("First"))
        controller.resolveConfirmation(confirmation.id, choice: choice == "primary" ? .primary : choice == "alternative" ? .alternative : .cancel)
        try await Self.settle(installations, instance)
        #expect(controller.errors[instance.id] == nil)
        #expect(FileManager.default.fileExists(atPath: world.appendingPathComponent("datapacks/pack.zip").path) == (choice != "cancel"))
        #expect(FileManager.default.fileExists(atPath: world.appendingPathComponent("datapacks/dep.zip").path) == (choice == "primary"))
        #expect(try await installations.content.list(at: controller.folder(instance, mods: true), mods: true).count == (choice == "primary" ? 1 : 0))
        #expect(try await installations.content.list(at: controller.folder(instance, mods: false), mods: false).count == (choice == "primary" ? 1 : 0))
        if choice == "cancel" { #expect(controller.worldInstallMessages[instance.id]?["First"] == nil) }
    }

    @Test(arguments: [true, false]) func replacingDatapackRequiresConsent(accepted: Bool) async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let (container, installations, controller, instance) = try Self.instance(root: root, session: session)
        _ = container
        let world = try Self.world("First", in: instance, installations: installations)
        let folder = world.appendingPathComponent("datapacks")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false)
        let old = Data("old".utf8), bytes = try Self.datapackZIP(in: root)
        try old.write(to: folder.appendingPathComponent("pack.zip"))
        ModrinthTestProtocol.prepare([
            "\(api)/project/root/version": try JSONSerialization.data(withJSONObject: [Self.version("v", project: "root", loader: "datapack", file: ("pack.zip", bytes))]),
            "fixtures.test/pack.zip": bytes
        ])
        controller.install(try Self.project("root"), in: instance, target: .worldDatapacks("First"))
        let confirmation = try await Self.confirmation(controller, installations, instance)
        #expect(confirmation.title == "Заменить датапак?" && confirmation.message.contains("First"))
        controller.resolveConfirmation(confirmation.id, accepted: accepted)
        try await Self.settle(installations, instance)
        #expect(controller.errors[instance.id] == nil)
        #expect(try Data(contentsOf: folder.appendingPathComponent("pack.zip")) == (accepted ? bytes : old))
        #expect((controller.worldInstallMessages[instance.id]?["First"] != nil) == accepted)
    }

    @Test(arguments: ["hash", "metadata", "missing-world", "running"])
    func failedDatapackInstallLeavesWorldUntouched(failure: String) async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let (container, installations, controller, instance) = try Self.instance(root: root, session: session)
        _ = container
        let world = try Self.world("First", in: instance, installations: installations)
        let bytes = try Self.datapackZIP(in: root, valid: failure != "metadata")
        ModrinthTestProtocol.prepare([
            "\(api)/project/root/version": try JSONSerialization.data(withJSONObject: [Self.version("v", project: "root", loader: "datapack", file: ("pack.zip", bytes))]),
            "fixtures.test/pack.zip": failure == "hash" ? Data("bad".utf8) : bytes
        ])
        if failure == "missing-world" { try FileManager.default.removeItem(at: world) }
        if failure == "running" { installations.store.launchBusy.insert(instance.id) }
        controller.install(try Self.project("root"), in: instance, target: .worldDatapacks("First"))
        try await Self.settle(installations, instance)
        #expect(controller.errors[instance.id] != nil)
        #expect(!FileManager.default.fileExists(atPath: world.appendingPathComponent("datapacks").path))
        #expect(controller.worldInstallMessages[instance.id]?["First"] == nil)
        if failure == "missing-world" { #expect(!FileManager.default.fileExists(atPath: world.path)) }
        if failure == "running" { #expect(ModrinthTestProtocol.requests.isEmpty) }
        if failure == "metadata" { #expect(controller.errors[instance.id]?.contains("pack.mcmeta") == true) }
    }

    @Test func disappearingWorldDuringConfirmationIsNotRecreated() async throws {
        let root = Self.temporaryRoot(); defer { try? FileManager.default.removeItem(at: root) }
        let session = ModrinthTestProtocol.session(); defer { session.invalidateAndCancel() }
        let (container, installations, controller, instance) = try Self.instance(root: root, session: session)
        _ = container
        let world = try Self.world("First", in: instance, installations: installations)
        let folder = world.appendingPathComponent("datapacks")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false)
        try Data("old".utf8).write(to: folder.appendingPathComponent("pack.zip"))
        let bytes = try Self.datapackZIP(in: root)
        ModrinthTestProtocol.prepare([
            "\(api)/project/root/version": try JSONSerialization.data(withJSONObject: [Self.version("v", project: "root", loader: "datapack", file: ("pack.zip", bytes))]),
            "fixtures.test/pack.zip": bytes
        ])
        controller.install(try Self.project("root"), in: instance, target: .worldDatapacks("First"))
        let confirmation = try await Self.confirmation(controller, installations, instance)
        try FileManager.default.removeItem(at: world)
        controller.resolveConfirmation(confirmation.id, accepted: true)
        try await Self.settle(installations, instance)
        #expect(controller.errors[instance.id] != nil)
        #expect(!FileManager.default.fileExists(atPath: world.path))
    }

    private static func world(_ name: String, in instance: GameInstance, installations: InstallationCoordinator) throws -> URL {
        let root = try installations.store.storage.directory(instance.folderName)
        let world = root.appendingPathComponent("minecraft/saves/\(name)")
        try FileManager.default.createDirectory(at: world, withIntermediateDirectories: true)
        try WorldTestFixture.level(name: name).write(to: world.appendingPathComponent("level.dat"))
        return world
    }

    private static func datapackZIP(in root: URL, name: String = "pack.zip", valid: Bool = true) throws -> Data {
        let source = root.appendingPathComponent(UUID().uuidString), archive = root.appendingPathComponent(name)
        try FileManager.default.createDirectory(at: source, withIntermediateDirectories: true)
        let file = valid ? "pack.mcmeta" : "wrong.json"
        try JSONSerialization.data(withJSONObject: ["pack": ["pack_format": 48, "description": name]]).write(to: source.appendingPathComponent(file))
        let process = Process(); process.executableURL = URL(fileURLWithPath: "/usr/bin/zip")
        process.currentDirectoryURL = source; process.arguments = ["-q", archive.path, file]
        try process.run(); process.waitUntilExit()
        #expect(process.terminationStatus == 0)
        return try Data(contentsOf: archive)
    }

    private static func project(_ id: String) throws -> ModrinthProject {
        try JSONDecoder().decode(ModrinthProject.self, from: Data(#"{"project_id":"\#(id)","slug":"\#(id)","title":"Root","description":"","icon_url":null,"project_type":"mod"}"#.utf8))
    }

    private static func instance(root: URL, session: URLSession) throws -> (ModelContainer, InstallationCoordinator, InstanceContentController, GameInstance) {
        let container = try ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        let installations = InstallationCoordinator(context: container.mainContext, storage: .init(root: root.appendingPathComponent("instances")))
        let api = FabricAPIDescriptor(projectID: FabricAPIDescriptor.project, versionID: "api", version: "api", channel: "release", filename: "api.jar", url: URL(string: "https://fixtures.test/api.jar")!, size: 1, sha1: String(repeating: "a", count: 40), sha512: String(repeating: "b", count: 128))
        var draft = InstanceDraft(); draft.name = "Catalog"; draft.modLoader = .fabric; draft.fabricConfiguration = .init(loaderVersion: "0.19.5", api: api)
        let instance = try installations.store.create(draft, versionID: "test", metadataURL: "https://fixtures.test/version", metadataSHA1: "sha", javaMajorVersion: 21)
        instance.state = .ready
        return (container, installations, InstanceContentController(installations: installations, modrinth: ModrinthClient(session: session, cache: root.appendingPathComponent("modrinth-cache"))), instance)
    }

    private static func confirmation(_ controller: InstanceContentController, _ installations: InstallationCoordinator, _ instance: GameInstance) async throws -> ContentConfirmation {
        for _ in 0..<500 where controller.confirmation == nil && installations.contentBusy.contains(instance.id) { try await Task.sleep(for: .milliseconds(10)) }
        return try #require(controller.confirmation, "\(controller.errors[instance.id] ?? "нет подтверждения")")
    }

    private static func settle(_ installations: InstallationCoordinator, _ instance: GameInstance) async throws {
        for _ in 0..<500 where installations.contentBusy.contains(instance.id) { try await Task.sleep(for: .milliseconds(10)) }
        #expect(!installations.contentBusy.contains(instance.id))
    }
}

nonisolated final class BodyLog: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [[String: Any]] = []
    var values: [[String: Any]] { lock.withLock { stored } }
    func append(_ value: [String: Any]) { lock.withLock { stored.append(value) } }
}

/// Отвечает по «хост + путь», без учёта query: запросы Modrinth различаются путём.
nonisolated final class ModrinthTestProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private nonisolated(unsafe) static var routes: [String: Data] = [:]
    private nonisolated(unsafe) static var recorded: [URLRequest] = []
    private nonisolated(unsafe) static var handler: (@Sendable (URL, Data) -> Data?)?
    static var requests: [URLRequest] { lock.withLock { recorded } }
    /// `handler` отвечает раньше `routes` и видит тело запроса.
    static func prepare(_ routes: [String: Data], handler: (@Sendable (URL, Data) -> Data?)? = nil) { lock.withLock { self.routes = routes; self.handler = handler; recorded = [] } }
    static func session() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral; configuration.protocolClasses = [ModrinthTestProtocol.self]
        return URLSession(configuration: configuration)
    }
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let url = request.url!
        var body = request.httpBody ?? Data()
        if let stream = request.httpBodyStream {
            stream.open(); defer { stream.close() }
            var buffer = [UInt8](repeating: 0, count: 65_536)
            while stream.hasBytesAvailable { let count = stream.read(&buffer, maxLength: buffer.count); if count <= 0 { break }; body.append(buffer, count: count) }
        }
        let data = Self.lock.withLock { Self.recorded.append(request); return Self.handler?(url, body) ?? Self.routes[(url.host ?? "") + url.path] }
        client?.urlProtocol(self, didReceive: HTTPURLResponse(url: url, statusCode: data == nil ? 404 : 200, httpVersion: nil, headerFields: nil)!, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: data ?? Data())
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

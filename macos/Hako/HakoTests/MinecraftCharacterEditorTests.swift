import Foundation
import SwiftData
import Testing

@Suite(.serialized)
@MainActor
struct MinecraftCharacterEditorTests {
    @Test func draftChangeImmediatelyRejectsPreviousRenderReadiness() async throws {
        let harness = try makeHarness()
        await open(harness)
        let oldRevision = harness.editor.previewRevision
        harness.editor.selectVariant(.slim)
        harness.editor.setPreviewReady(true, revision: oldRevision)
        #expect(!harness.editor.previewReady)
        #expect(!harness.editor.canSave)
        await waitForPreview(harness.editor, after: oldRevision)
        harness.editor.setPreviewReady(true, revision: harness.editor.previewRevision)
        #expect(harness.editor.canSave)
    }

    @Test func lateUnauthorizedFromClosedEditorCannotReplaceSameXuidSession() async throws {
        let harness = try makeHarness()
        harness.api.pauseNextProfile = true
        let opening = Task { await harness.editor.open(harness.account) }
        await waitUntil { harness.api.profileIsPaused }
        harness.editor.close()
        harness.sessions.signOut()
        let replacement = Account(xbox: .init(xuid: harness.account.xuid, gamertag: "Replacement", avatarURL: nil), email: nil)
        harness.container.mainContext.insert(replacement)
        try harness.container.mainContext.save()
        harness.sessions.installFixtureIdentity(
            .init(name: "Replacement", uuid: "replacement", accessToken: "replacement-token", xuid: replacement.xuid),
            for: replacement, expiration: .now.addingTimeInterval(3600)
        )
        harness.api.resumePausedProfile(error: MinecraftProfileAPIError.unauthorized)
        await opening.value
        #expect(harness.sessionFixture.refreshCount == 0)
        #expect(harness.sessions.identity(for: replacement)?.accessToken == "replacement-token")
    }

    @Test func importBeforeInitialProfileIsIgnored() async throws {
        let harness = try makeHarness()
        harness.api.profileErrors = [MinecraftProfileAPIError.httpStatus(503)]
        await harness.editor.open(harness.account)
        harness.editor.importSkin(try SkinTestFixtures.png(transparentPixel: (40, 8)))
        #expect(harness.editor.selectedSkin == nil)
        await harness.editor.loadProfile()
        #expect(harness.editor.profile != nil)
        #expect(!harness.editor.hasUnsavedChanges)
    }

    @Test func importingSkinDoesNotMutateAccountUntilSave() async throws {
        let harness = try makeHarness()
        await open(harness)
        let oldURL = harness.account.minecraftSkinURL
        let replacement = try SkinTestFixtures.png(transparentPixel: (40, 8))
        let previousRevision = harness.editor.previewRevision

        harness.editor.importSkin(replacement)
        await waitForPreview(harness.editor, after: previousRevision)

        #expect(harness.account.minecraftSkinURL == oldURL)
        #expect(harness.account.minecraftSkinVariant == MinecraftSkinVariant.classic.rawValue)
        #expect(harness.api.uploadedSkins.isEmpty)
        #expect(harness.editor.hasUnsavedChanges)
    }

    @Test func revertingDraftAndImportingIdenticalPixelsDoesNotEnableSave() async throws {
        let harness = try makeHarness()
        await open(harness)
        let current = harness.api.skinPNG

        var revision = harness.editor.previewRevision
        harness.editor.importSkin(try SkinTestFixtures.png(transparentPixel: (40, 8)))
        await waitForPreview(harness.editor, after: revision)
        revision = harness.editor.previewRevision
        harness.editor.importSkin(current)
        await waitForPreview(harness.editor, after: revision)
        harness.editor.setPreviewReady(true, revision: harness.editor.previewRevision)

        #expect(!harness.editor.hasUnsavedChanges)
        #expect(!harness.editor.canSave)
        #expect(harness.api.uploadedSkins.isEmpty)
    }

    @Test func savesChangedSkinAndUpdatesAccountAndAuthoritativeCache() async throws {
        let harness = try makeHarness()
        await open(harness)
        let replacement = try SkinTestFixtures.png(transparentPixel: (40, 8))
        let revision = harness.editor.previewRevision
        harness.editor.importSkin(replacement)
        await waitForPreview(harness.editor, after: revision)
        harness.editor.setPreviewReady(true, revision: harness.editor.previewRevision)

        #expect(await harness.editor.save())
        #expect(harness.api.uploadedSkins.count == 1)
        #expect(harness.api.uploadedSkins.first?.png == replacement)
        #expect(harness.account.minecraftSkinURL == harness.api.skinURL)
        #expect(harness.appearances.profile(for: harness.account.xuid)?.activeSkin?.id == harness.api.skinID)
        #expect(harness.editor.skinWriteState == .unchanged)
    }

    @Test func variantOnlyChangeReuploadsOriginalPngBytes() async throws {
        let harness = try makeHarness()
        await open(harness)
        let originalPNG = harness.api.skinPNG
        let revision = harness.editor.previewRevision
        harness.editor.selectVariant(.slim)
        await waitForPreview(harness.editor, after: revision)
        harness.editor.setPreviewReady(true, revision: harness.editor.previewRevision)

        #expect(await harness.editor.save())
        #expect(harness.api.uploadedSkins.count == 1)
        #expect(harness.api.uploadedSkins[0].png == originalPNG)
        #expect(harness.api.uploadedSkins[0].variant == .slim)
        #expect(harness.account.minecraftSkinVariant == MinecraftSkinVariant.slim.rawValue)
    }

    @Test func capeOnlySaveAndUnequipDoNotUploadSkin() async throws {
        let harness = try makeHarness()
        await open(harness)
        let revision = harness.editor.previewRevision
        harness.editor.selectCape("cape-festival")
        await waitForPreview(harness.editor, after: revision)
        harness.editor.setPreviewReady(true, revision: harness.editor.previewRevision)

        #expect(await harness.editor.save())
        #expect(harness.api.uploadedSkins.isEmpty)
        #expect(harness.api.capeChanges == ["PUT:cape-festival"])
        #expect(harness.account.minecraftSkinURL == harness.api.skinURL)

        let secondRevision = harness.editor.previewRevision
        harness.editor.selectCape(nil)
        await waitForPreview(harness.editor, after: secondRevision)
        harness.editor.setPreviewReady(true, revision: harness.editor.previewRevision)
        #expect(await harness.editor.save())
        #expect(harness.api.uploadedSkins.isEmpty)
        #expect(harness.api.capeChanges == ["PUT:cape-festival", "DELETE"])
        #expect(harness.appearances.profile(for: harness.account.xuid)?.activeCape == nil)
    }

    @Test func capeOnlySaveRebasesExternallyChangedSkinVariant() async throws {
        let harness = try makeHarness()
        await open(harness)
        let revision = harness.editor.previewRevision
        harness.editor.selectCape("cape-festival")
        await waitForPreview(harness.editor, after: revision)
        harness.api.skinVariant = "SLIM"
        harness.editor.setPreviewReady(true, revision: harness.editor.previewRevision)

        #expect(await harness.editor.save())
        #expect(harness.api.uploadedSkins.isEmpty)
        #expect(harness.api.capeChanges == ["PUT:cape-festival"])
        #expect(harness.account.minecraftSkinVariant == MinecraftSkinVariant.slim.rawValue)
    }

    @Test func skinOnlySaveKeepsExternallyChangedCape() async throws {
        let harness = try makeHarness()
        await open(harness)
        let revision = harness.editor.previewRevision
        harness.editor.importSkin(try SkinTestFixtures.png(transparentPixel: (40, 8)))
        await waitForPreview(harness.editor, after: revision)
        harness.api.activeCapeID = "cape-festival"
        harness.editor.setPreviewReady(true, revision: harness.editor.previewRevision)

        #expect(await harness.editor.save())
        #expect(harness.api.uploadedSkins.count == 1)
        #expect(harness.api.capeChanges.isEmpty)
        #expect(harness.api.activeCapeID == "cape-festival")
    }

    @Test(arguments: [true, false])
    func uncertainSkinWriteIsReconciledBeforeAnyRetry(appliedRemotely: Bool) async throws {
        let harness = try makeHarness()
        await open(harness)
        let replacement = try SkinTestFixtures.png(transparentPixel: (40, 8))
        let revision = harness.editor.previewRevision
        harness.editor.importSkin(replacement)
        await waitForPreview(harness.editor, after: revision)
        harness.editor.setPreviewReady(true, revision: harness.editor.previewRevision)
        harness.api.skinPlans = [.init(applies: appliedRemotely, error: URLError(.timedOut))]
        harness.api.profileErrors = [nil, MinecraftProfileAPIError.httpStatus(503)]

        #expect(!(await harness.editor.save()))
        #expect(harness.editor.skinWriteState == .unknown)
        let callsAfterUncertainWrite = harness.api.calls.count

        #expect(await harness.editor.save())
        #expect(harness.api.calls[callsAfterUncertainWrite].hasPrefix("GET:"))
        #expect(harness.api.uploadedSkins.count == (appliedRemotely ? 1 : 2))
        #expect(harness.api.calls.last(where: { $0.hasPrefix("POST:") }) != nil)
        #expect(harness.account.minecraftSkinURL == harness.api.skinURL)
    }

    @Test func skinSuccessCapeFailureRetriesOnlyCape() async throws {
        let harness = try makeHarness()
        await open(harness)
        let revision = harness.editor.previewRevision
        harness.editor.importSkin(try SkinTestFixtures.png(transparentPixel: (40, 8)))
        harness.editor.selectCape("cape-festival")
        await waitForPreview(harness.editor, after: revision)
        harness.editor.setPreviewReady(true, revision: harness.editor.previewRevision)
        harness.api.capePlans = [.init(applies: false, error: MinecraftProfileAPIError.httpStatus(403))]

        #expect(!(await harness.editor.save()))
        #expect(harness.editor.skinWriteState == .confirmed)
        #expect(harness.editor.capeWriteState == .pending)
        #expect(harness.api.uploadedSkins.count == 1)

        #expect(await harness.editor.save())
        #expect(harness.api.uploadedSkins.count == 1)
        #expect(harness.api.capeChanges == ["PUT:cape-festival", "PUT:cape-festival"])
    }

    @Test func localPersistenceRetryDoesNotRepeatRemoteSkinWrite() async throws {
        let harness = try makeHarness()
        await open(harness)
        let revision = harness.editor.previewRevision
        harness.editor.importSkin(try SkinTestFixtures.png(transparentPixel: (40, 8)))
        await waitForPreview(harness.editor, after: revision)
        harness.editor.setPreviewReady(true, revision: harness.editor.previewRevision)
        harness.persistence.failNextSave = true

        #expect(!(await harness.editor.save()))
        #expect(harness.editor.localPersistencePending)
        #expect(harness.api.uploadedSkins.count == 1)
        let expectedSaveError = String(appLocalized: "Изменения персонажа сохранены на сервере, но не удалось сохранить их в Hako: \(FixturePersistenceError.failed.localizedDescription)")
        #expect(harness.editor.saveError == expectedSaveError)
        #expect(await harness.editor.save())
        #expect(harness.api.uploadedSkins.count == 1)
        #expect(!harness.editor.localPersistencePending)
    }

    @Test func one401RefreshesAndRetriesWithReplacementToken() async throws {
        let harness = try makeHarness()
        harness.api.unauthorizedTokens = ["fixture-token"]

        await harness.editor.open(harness.account)

        #expect(harness.api.calls.contains("GET:fixture-token"))
        #expect(harness.api.calls.contains("GET:refreshed-token"))
        #expect(harness.sessionFixture.refreshCount == 1)
        #expect(harness.sessions.identity(for: harness.account)?.accessToken == "refreshed-token")
        #expect(harness.editor.profile?.id == harness.api.playerID)
    }

    @Test func sessionRecognizesSameSavedAccountFromSwiftUIContext() async throws {
        let harness = try makeHarness()
        let uiContext = ModelContext(harness.container)
        let sameAccount = try #require(uiContext.fetch(FetchDescriptor<Account>()).first)
        #expect(sameAccount !== harness.account)
        #expect(sameAccount.persistentModelID == harness.account.persistentModelID)
        #expect(harness.sessions.identity(for: sameAccount)?.accessToken == "fixture-token")
        await harness.editor.open(sameAccount)
        #expect(harness.editor.profile != nil)
        #expect(harness.editor.previewSkin != nil)
    }

    @Test func concurrentUnauthorizedRefreshesShareOneReplacementAndRejectOldAccount() async throws {
        let harness = try makeHarness()
        harness.sessionFixture.pauseNextSignIn = true
        let first = Task { try await harness.sessions.refreshAfterUnauthorized(harness.account, rejectedToken: "fixture-token") }
        await waitUntil { harness.sessionFixture.signInIsPaused }
        let second = Task { try await harness.sessions.refreshAfterUnauthorized(harness.account, rejectedToken: "fixture-token") }
        await Task.yield()
        harness.sessionFixture.resumeSignIn()
        #expect(try await first.value.accessToken == "refreshed-token")
        #expect(try await second.value.accessToken == "refreshed-token")
        #expect(harness.sessionFixture.refreshCount == 1)

        harness.sessions.signOut()
        let replacement = Account(xbox: .init(xuid: harness.account.xuid, gamertag: "Replacement", avatarURL: nil), email: nil)
        harness.sessions.installFixtureIdentity(.init(name: "Replacement", uuid: "new", accessToken: "new-token", xuid: replacement.xuid),
                                                for: replacement, expiration: .now.addingTimeInterval(3600))
        await #expect(throws: MinecraftSessionError.accountChanged) {
            try await harness.sessions.refreshAfterUnauthorized(harness.account, rejectedToken: "refreshed-token")
        }
        #expect(harness.sessions.identity(for: replacement)?.accessToken == "new-token")
        #expect(harness.sessionFixture.refreshCount == 1)
    }

    @Test func refreshFailureDoesNotRetryProfileWithRejectedToken() async throws {
        let harness = try makeHarness()
        harness.api.unauthorizedTokens = ["fixture-token"]
        harness.sessionFixture.refreshError = URLError(.notConnectedToInternet)
        await harness.editor.open(harness.account)
        #expect(harness.api.calls.filter { $0.hasPrefix("GET:") } == ["GET:fixture-token"])
        #expect(harness.sessionFixture.refreshCount == 1)
        #expect(harness.editor.profile == nil)
        #expect(harness.editor.previewError != nil)
    }

    @Test func repeated401StopsAfterOneReplacementRefresh() async throws {
        let harness = try makeHarness()
        harness.api.unauthorizedTokens = ["fixture-token", "refreshed-token"]
        await harness.editor.open(harness.account)
        #expect(harness.api.calls.filter { $0.hasPrefix("GET:") } == ["GET:fixture-token", "GET:refreshed-token"])
        #expect(harness.sessionFixture.refreshCount == 1)
        #expect(harness.editor.profile == nil)
    }

    @Test(arguments: [true, false])
    func uncertainCapeWriteRequiresSuccessfulReconciliationBeforeRetry(appliedRemotely: Bool) async throws {
        let harness = try makeHarness()
        await open(harness)
        let revision = harness.editor.previewRevision
        harness.editor.selectCape("cape-festival")
        await waitForPreview(harness.editor, after: revision)
        harness.editor.setPreviewReady(true, revision: harness.editor.previewRevision)
        harness.api.capePlans = [.init(applies: appliedRemotely, error: URLError(.timedOut))]
        harness.api.profileErrors = [nil, MinecraftProfileAPIError.httpStatus(503)]
        #expect(!(await harness.editor.save()))
        #expect(harness.editor.capeWriteState == .unknown)
        #expect(harness.api.capeChanges.count == 1)
        harness.api.profileErrors = [MinecraftProfileAPIError.httpStatus(503)]
        #expect(!(await harness.editor.save()))
        #expect(harness.api.capeChanges.count == 1)
        let beforeRetry = harness.api.calls.count
        #expect(await harness.editor.save())
        #expect(harness.api.calls[beforeRetry].hasPrefix("GET:"))
        #expect(harness.api.capeChanges.count == (appliedRemotely ? 1 : 2))
        #expect(harness.api.uploadedSkins.isEmpty)
    }

    @Test func authoritativeAppearanceRevisionBlocksLateRefreshProfileWrite() async throws {
        let harness = try makeHarness()
        harness.sessionFixture.signInSkinURL = URL(string: "https://textures.minecraft.net/texture/stale-refresh")!
        harness.sessionFixture.pauseNextSignIn = true
        let refreshing = Task {
            try await harness.sessions.refreshAfterUnauthorized(harness.account, rejectedToken: "fixture-token")
        }
        await waitUntil { harness.sessionFixture.signInIsPaused }

        let current = MinecraftProfileResponse(
            id: harness.account.minecraftUUID!,
            name: "FixturePlayer",
            skins: [.init(id: "authoritative-skin", url: URL(string: "https://textures.minecraft.net/texture/current")!, state: "ACTIVE", variant: "SLIM")],
            capes: []
        )
        harness.appearances.record(current, for: harness.account.xuid)
        harness.sessionFixture.resumeSignIn()
        _ = try await refreshing.value

        #expect(harness.account.minecraftSkinURL == harness.api.skinURL)
        #expect(harness.appearances.profile(for: harness.account.xuid)?.activeSkin?.id == "authoritative-skin")
        #expect(harness.sessions.identity(for: harness.account)?.accessToken == "refreshed-token")
    }

    @Test(arguments: [403, 429])
    func forbiddenAndRateLimitedRequestsDoNotRefresh(status: Int) async throws {
        let harness = try makeHarness()
        harness.api.profileErrors = [MinecraftProfileAPIError.httpStatus(status)]

        await harness.editor.open(harness.account)

        #expect(harness.sessionFixture.refreshCount == 0)
        #expect(harness.editor.profile == nil)
        #expect(harness.editor.previewError != nil)
    }

    @Test func temporaryIdentityLossKeepsDraftForReconnect() async throws {
        let harness = try makeHarness()
        await open(harness)
        let draft = try SkinTestFixtures.png(transparentPixel: (40, 8))
        let revision = harness.editor.previewRevision
        harness.editor.importSkin(draft)
        await waitForPreview(harness.editor, after: revision)
        harness.sessionFixture.now = .now.addingTimeInterval(7200)

        #expect(harness.sessions.identity(for: harness.account) == nil)
        #expect(!harness.editor.canSave)
        #expect(harness.editor.selectedSkin?.png == draft)
        #expect(harness.editor.hasUnsavedChanges)

        let reconnectRevision = harness.editor.previewRevision
        await harness.editor.reconnect()
        await waitForPreview(harness.editor, after: reconnectRevision)
        #expect(harness.editor.selectedSkin?.png == draft)
        #expect(harness.api.uploadedSkins.isEmpty)
        harness.editor.setPreviewReady(true, revision: harness.editor.previewRevision)
        #expect(harness.editor.canSave)
    }

    @Test func profileRetryLoadsCapesAndExplicitEmptyCapeInventory() async throws {
        let failed = try makeHarness()
        failed.api.profileErrors = [MinecraftProfileAPIError.httpStatus(503)]
        await failed.editor.open(failed.account)
        if case .failed = failed.editor.capeLoadState {} else { Issue.record("expected profile load failure") }
        await failed.editor.loadProfile()
        #expect(failed.editor.capeLoadState == .loaded)
        #expect(failed.editor.capes.count == 2)

        let empty = try makeHarness()
        empty.api.ownsCapes = false
        await open(empty)
        #expect(empty.editor.capeLoadState == .loaded)
        #expect(empty.editor.capes.isEmpty)
        #expect(empty.editor.selectedCapeID == nil)
    }

    @Test func lateProfileLoadCannotReplaceNewAccountAppearance() async throws {
        let harness = try makeHarness()
        harness.api.pauseNextProfile = true
        let oldLoad = Task { await harness.editor.open(harness.account) }
        await waitUntil { harness.api.profileIsPaused }

        let nextAccount = Account(xbox: XboxProfile(xuid: "second-xuid", gamertag: "Second", avatarURL: nil), email: nil)
        nextAccount.minecraftUUID = "00000000000000000000000000000002"
        nextAccount.minecraftName = "Second"
        harness.container.mainContext.insert(nextAccount)
        try harness.container.mainContext.save()
        harness.sessions.installFixtureIdentity(
            .init(name: "Second", uuid: nextAccount.minecraftUUID!, accessToken: "second-token", xuid: nextAccount.xuid),
            for: nextAccount, expiration: .now.addingTimeInterval(3600)
        )
        harness.api.playerID = "00000000000000000000000000000002"
        harness.api.playerName = "Second"
        await harness.editor.open(nextAccount)
        harness.api.resumePausedProfile()
        await oldLoad.value

        #expect(harness.editor.account?.xuid == nextAccount.xuid)
        #expect(harness.editor.profile?.id == nextAccount.minecraftUUID)
        #expect(harness.appearances.profile(for: harness.account.xuid) == nil)
        #expect(harness.appearances.profile(for: nextAccount.xuid)?.id == nextAccount.minecraftUUID)
    }

    @Test func accountReplacementCancelsSaveAndPreventsLateCacheMutation() async throws {
        let harness = try makeHarness()
        await open(harness)
        let revision = harness.editor.previewRevision
        harness.editor.selectCape("cape-festival")
        await waitForPreview(harness.editor, after: revision)
        harness.editor.setPreviewReady(true, revision: harness.editor.previewRevision)
        harness.api.pauseNextCapeMutation = true
        let saving = Task { await harness.editor.save() }
        await waitUntil { harness.api.capeMutationIsPaused }
        #expect(harness.editor.isSaving)
        #expect(!(await harness.editor.save()))
        let appearanceRevision = harness.appearances.revision(for: harness.account.xuid)

        harness.editor.invalidateForAccountChange()
        harness.api.resumePausedCapeMutation()
        _ = await saving.value

        #expect(harness.editor.account == nil)
        #expect(harness.appearances.revision(for: harness.account.xuid) == appearanceRevision)
        #expect(harness.account.minecraftSkinURL == harness.api.skinURL)
    }

    @Test func invalidImportPreservesPreviousValidDraftAndPreviewFailureBlocksSave() async throws {
        let harness = try makeHarness()
        let emeraldURL = URL(string: "https://textures.minecraft.net/cape/cape-emerald")!
        harness.api.capeData[emeraldURL] = Data("invalid cape".utf8)
        await open(harness)
        let valid = try SkinTestFixtures.png(transparentPixel: (40, 8))
        let revision = harness.editor.previewRevision
        harness.editor.importSkin(valid)
        await waitForPreview(harness.editor, after: revision)
        harness.editor.importSkin(Data("invalid".utf8))
        #expect(harness.editor.selectedSkin?.png == valid)
        #expect(harness.editor.inputError != nil)

        #expect(harness.editor.previewError != nil)
        harness.editor.setPreviewReady(true, revision: harness.editor.previewRevision)
        #expect(harness.editor.previewError != nil)
        #expect(!harness.editor.canSave)
    }

    @Test func retryPreviewReloadsFailedCapeAndRetainsDraft() async throws {
        let harness = try makeHarness()
        let capeURL = URL(string: "https://textures.minecraft.net/cape/cape-emerald")!
        harness.api.capeData[capeURL] = Data("broken cape".utf8)
        await harness.editor.open(harness.account)
        await waitUntil { harness.editor.capeImageErrors["cape-emerald"] != nil }
        #expect(harness.editor.previewError != nil)
        let draft = try SkinTestFixtures.png(transparentPixel: (40, 8))
        var revision = harness.editor.previewRevision
        harness.editor.importSkin(draft)
        await waitForPreview(harness.editor, after: revision)
        harness.api.capeData[capeURL] = try SkinTestFixtures.png(height: 32)
        revision = harness.editor.previewRevision
        harness.editor.retryPreview()
        await waitForPreview(harness.editor, after: revision)
        #expect(harness.editor.previewError == nil)
        #expect(harness.editor.previewSkin?.capeImage != nil)
        #expect(harness.editor.selectedSkin?.png == draft)
        harness.editor.setPreviewReady(true, revision: harness.editor.previewRevision)
        #expect(harness.editor.canSave)
    }

    @Test func renderFailureCannotBeClearedByLateReadyCallback() async throws {
        let harness = try makeHarness()
        await open(harness)
        let revision = harness.editor.previewRevision
        harness.editor.selectVariant(.slim)
        await waitForPreview(harness.editor, after: revision)
        let failedRevision = harness.editor.previewRevision
        harness.editor.reportPreviewFailure("Render failed", revision: failedRevision)
        harness.editor.setPreviewReady(true, revision: failedRevision)
        #expect(harness.editor.previewError == "Render failed")
        #expect(!harness.editor.canSave)
        harness.editor.retryPreview()
        await waitForPreview(harness.editor, after: failedRevision)
        harness.editor.reportPreviewFailure("Old render failed", revision: failedRevision)
        harness.editor.setPreviewReady(true, revision: harness.editor.previewRevision)
        #expect(harness.editor.previewError == nil)
        #expect(harness.editor.canSave)
    }

    @Test func failedFileImportRetainsValidDraft() async throws {
        let harness = try makeHarness()
        await open(harness)
        let draft = try SkinTestFixtures.png(transparentPixel: (40, 8))
        let revision = harness.editor.previewRevision
        harness.editor.importSkin(draft)
        await waitForPreview(harness.editor, after: revision)
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".png")
        defer { try? FileManager.default.removeItem(at: url) }
        try Data("broken PNG".utf8).write(to: url)
        let validRevision = harness.editor.previewRevision
        await harness.editor.importSkin(from: url)
        #expect(harness.editor.selectedSkin?.png == draft)
        #expect(harness.editor.previewRevision == validRevision)
        #expect(harness.editor.inputError != nil)
    }

    private func open(_ harness: Harness) async {
        await harness.editor.open(harness.account)
        #expect(harness.editor.profile != nil)
        harness.editor.setPreviewReady(true, revision: harness.editor.previewRevision)
    }

    private func waitForPreview(_ editor: MinecraftCharacterEditorCoordinator, after revision: Int) async {
        for _ in 0..<500 where editor.previewRevision <= revision || editor.isPreparingPreview { await Task.yield() }
        #expect(!editor.isPreparingPreview)
        #expect(editor.previewRevision > revision)
    }

    private func waitUntil(_ condition: @MainActor () -> Bool) async {
        for _ in 0..<500 where !condition() { await Task.yield() }
        #expect(condition())
    }

    private func makeHarness() throws -> Harness {
        let schema = HakoSchema.schema
        let container = try ModelContainer(for: schema, configurations: [ModelConfiguration(schema: schema, isStoredInMemoryOnly: true)])
        let account = Account(xbox: XboxProfile(xuid: "fixture-xuid", gamertag: "Fixture", avatarURL: nil), email: nil)
        account.minecraftUUID = "00000000000000000000000000000001"
        account.minecraftName = "FixturePlayer"
        let api = ScriptedMinecraftProfileAPI()
        account.minecraftSkinURL = api.skinURL
        account.minecraftSkinVariant = MinecraftSkinVariant.classic.rawValue
        container.mainContext.insert(account)
        try container.mainContext.save()

        let appearanceStore = MinecraftAppearanceStore()
        let sessionFixture = SessionRefreshFixture()
        let dependencies = MinecraftSessionCoordinator.Dependencies(
            load: { _ in AccountTokens(microsoftRefreshToken: "refresh", minecraftAccessToken: "fixture-token", minecraftTokenExpiration: sessionFixture.now.addingTimeInterval(3600)) },
            save: { _, _ in },
            refresh: { _ in
                sessionFixture.refreshCount += 1
                if let error = sessionFixture.refreshError { throw error }
                return MicrosoftToken(accessToken: "microsoft-access", refreshToken: "refresh", idToken: nil)
            },
            signIn: { _ in
                try await sessionFixture.signIn(
                    uuid: account.minecraftUUID!, name: account.minecraftName!, skinURL: sessionFixture.signInSkinURL ?? api.skinURL
                )
            },
            now: { sessionFixture.now }
        )
        let sessions = MinecraftSessionCoordinator(context: container.mainContext, dependencies: dependencies, appearances: appearanceStore)
        sessions.installFixtureIdentity(
            .init(name: "FixturePlayer", uuid: account.minecraftUUID!, accessToken: "fixture-token", xuid: account.xuid),
            for: account, expiration: sessionFixture.now.addingTimeInterval(3600)
        )
        let persistence = PersistenceSwitch()
        let editor = MinecraftCharacterEditorCoordinator(
            api: api,
            sessions: sessions,
            context: container.mainContext,
            appearances: appearanceStore,
            saveContext: {
                if persistence.failNextSave {
                    persistence.failNextSave = false
                    throw FixturePersistenceError.failed
                }
                try container.mainContext.save()
            }
        )
        return Harness(
            container: container, account: account, api: api, appearances: appearanceStore,
            sessions: sessions, sessionFixture: sessionFixture, persistence: persistence, editor: editor
        )
    }

    private struct Harness {
        let container: ModelContainer
        let account: Account
        let api: ScriptedMinecraftProfileAPI
        let appearances: MinecraftAppearanceStore
        let sessions: MinecraftSessionCoordinator
        let sessionFixture: SessionRefreshFixture
        let persistence: PersistenceSwitch
        let editor: MinecraftCharacterEditorCoordinator
    }
}

@MainActor
private final class SessionRefreshFixture {
    var refreshCount = 0
    var refreshError: Error?
    var replacementToken = "refreshed-token"
    var now = Date.now
    var signInSkinURL: URL?
    var pauseNextSignIn = false
    private(set) var signInIsPaused = false
    private var signInContinuation: CheckedContinuation<MinecraftSession, Error>?

    func signIn(uuid: String, name: String, skinURL: URL) async throws -> MinecraftSession {
        let session = MinecraftSession(
            profile: MinecraftProfile(uuid: uuid, name: name, skinURL: skinURL, skinVariant: .classic),
            accessToken: replacementToken,
            expiration: now.addingTimeInterval(3600)
        )
        guard pauseNextSignIn else { return session }
        pauseNextSignIn = false
        signInIsPaused = true
        return try await withCheckedThrowingContinuation { signInContinuation = $0 }
    }

    func resumeSignIn() {
        guard let signInContinuation else { return }
        self.signInContinuation = nil
        signInIsPaused = false
        signInContinuation.resume(returning: MinecraftSession(
            profile: MinecraftProfile(uuid: "00000000000000000000000000000001", name: "FixturePlayer", skinURL: signInSkinURL, skinVariant: .classic),
            accessToken: replacementToken,
            expiration: now.addingTimeInterval(3600)
        ))
    }
}

@MainActor
private final class PersistenceSwitch {
    var failNextSave = false
}

private enum FixturePersistenceError: Error {
    case failed
}

@MainActor
private final class ScriptedMinecraftProfileAPI: MinecraftProfileAPI {
    struct MutationPlan {
        let applies: Bool
        let error: Error?
    }

    let skinURL = URL(string: "https://textures.minecraft.net/texture/fixture-skin")!
    var playerID = "00000000000000000000000000000001"
    var playerName = "FixturePlayer"
    private(set) var skinID = "skin-initial"
    var skinVariant = "CLASSIC"
    private(set) var skinPNG = try! SkinTestFixtures.png()
    var activeCapeID: String? = "cape-emerald"
    private(set) var calls: [String] = []
    private(set) var uploadedSkins: [(png: Data, variant: MinecraftSkinVariant)] = []
    private(set) var capeChanges: [String] = []
    var capeData: [URL: Data] = [:]
    var profileErrors: [Error?] = []
    var skinPlans: [MutationPlan] = []
    var capePlans: [MutationPlan] = []
    var unauthorizedTokens: Set<String> = []
    var pauseNextProfile = false
    private(set) var profileIsPaused = false
    private var pausedProfile: MinecraftProfileResponse?
    private var profileContinuation: CheckedContinuation<MinecraftProfileResponse, Error>?
    var pauseNextCapeMutation = false
    private(set) var capeMutationIsPaused = false
    private var capeContinuation: CheckedContinuation<Void, Error>?
    var ownsCapes = true

    init() {
        capeData = [
            URL(string: "https://textures.minecraft.net/cape/cape-emerald")!: (try! SkinTestFixtures.png(height: 32)),
            URL(string: "https://textures.minecraft.net/cape/cape-festival")!: (try! SkinTestFixtures.png(height: 32, transparentPixel: (40, 8)))
        ]
    }

    func profile(accessToken: String) async throws -> MinecraftProfileResponse {
        calls.append("GET:\(accessToken)")
        if unauthorizedTokens.remove(accessToken) != nil { throw MinecraftProfileAPIError.unauthorized }
        if !profileErrors.isEmpty {
            let error = profileErrors.removeFirst()
            if let error { throw error }
        }
        let response = currentProfile()
        if pauseNextProfile {
            pauseNextProfile = false
            profileIsPaused = true
            return try await withCheckedThrowingContinuation { continuation in
                pausedProfile = response
                profileContinuation = continuation
            }
        }
        return response
    }

    func uploadSkin(accessToken: String, png: Data, variant: MinecraftSkinVariant) async throws {
        calls.append("POST:\(accessToken)")
        uploadedSkins.append((png, variant))
        let plan = skinPlans.isEmpty ? MutationPlan(applies: true, error: nil) : skinPlans.removeFirst()
        if plan.applies {
            skinPNG = png
            skinVariant = variant.rawValue
            skinID = "skin-\(uploadedSkins.count)"
        }
        if let error = plan.error { throw error }
    }

    func activateCape(accessToken: String, capeID: String) async throws {
        calls.append("PUT:\(accessToken):\(capeID)")
        capeChanges.append("PUT:\(capeID)")
        if pauseNextCapeMutation {
            pauseNextCapeMutation = false
            capeMutationIsPaused = true
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                capeContinuation = continuation
            }
        }
        let plan = capePlans.isEmpty ? MutationPlan(applies: true, error: nil) : capePlans.removeFirst()
        if plan.applies { activeCapeID = capeID }
        if let error = plan.error { throw error }
    }

    func unequipCape(accessToken: String) async throws {
        calls.append("DELETE:\(accessToken)")
        capeChanges.append("DELETE")
        let plan = capePlans.isEmpty ? MutationPlan(applies: true, error: nil) : capePlans.removeFirst()
        if plan.applies { activeCapeID = nil }
        if let error = plan.error { throw error }
    }

    func texture(at url: URL) async throws -> Data {
        calls.append("TEXTURE:\(url.path)")
        if url == skinURL { return skinPNG }
        guard let data = capeData[url] else { throw MinecraftProfileAPIError.httpStatus(404) }
        return data
    }

    func resumePausedProfile(error: Error? = nil) {
        guard let response = pausedProfile else { return }
        pausedProfile = nil
        profileIsPaused = false
        if let error { profileContinuation?.resume(throwing: error) }
        else { profileContinuation?.resume(returning: response) }
        profileContinuation = nil
    }

    func resumePausedCapeMutation() {
        capeMutationIsPaused = false
        capeContinuation?.resume()
        capeContinuation = nil
    }

    private func currentProfile() -> MinecraftProfileResponse {
        let skins: [MinecraftProfileResponse.Skin] = [
            .init(id: skinID, url: skinURL, state: "ACTIVE", variant: skinVariant)
        ]
        let ownedCapes: [MinecraftProfileResponse.Cape] = [
            MinecraftProfileResponse.Cape(id: "cape-emerald", state: activeCapeID == "cape-emerald" ? "ACTIVE" : "INACTIVE", url: URL(string: "https://textures.minecraft.net/cape/cape-emerald")!, alias: "Emerald"),
            MinecraftProfileResponse.Cape(id: "cape-festival", state: activeCapeID == "cape-festival" ? "ACTIVE" : "INACTIVE", url: URL(string: "https://textures.minecraft.net/cape/cape-festival")!, alias: "Festival")
        ]
        return MinecraftProfileResponse(id: playerID, name: playerName, skins: skins, capes: ownsCapes ? ownedCapes : [])
    }
}

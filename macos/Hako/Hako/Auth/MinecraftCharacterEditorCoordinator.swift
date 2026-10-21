//
//  MinecraftCharacterEditorCoordinator.swift
//  Hako
//

import Foundation
import Observation
import SwiftData

enum MinecraftCapeLoadState: Equatable {
    case loading, loaded, failed(String)
}

enum MinecraftCharacterWriteState: Equatable {
    case unchanged, pending, confirmed, failed(String), unknown
}

@MainActor @Observable
final class MinecraftCharacterEditorCoordinator {
    private struct ServerAppearance {
        let profile: MinecraftProfileResponse
        let skin: MinecraftSkinUpload?
    }

    private let api: any MinecraftProfileAPI
    private let sessions: MinecraftSessionCoordinator
    private let context: ModelContext
    private let appearances: MinecraftAppearanceStore
    private let saveContext: () throws -> Void

    private var generation = UUID()
    private var previewGeneration = UUID()
    private var capeTasks: [String: Task<Data, Error>] = [:]
    private var saveTask: Task<Bool, Never>?
    private var activeAccountID: PersistentIdentifier?

    private(set) var account: Account?
    private(set) var profile: MinecraftProfileResponse?
    private(set) var capes: [MinecraftProfileResponse.Cape] = []
    private(set) var capeImages: [String: Data] = [:]
    private(set) var capeImageErrors: [String: String] = [:]
    private(set) var capeLoadState: MinecraftCapeLoadState = .loading
    private(set) var originalSkin: MinecraftSkinUpload?
    private(set) var selectedSkin: MinecraftSkinUpload?
    private(set) var selectedVariant: MinecraftSkinVariant = .classic
    private(set) var selectedCapeID: String?
    private(set) var previewSkin: MinecraftSkin?
    private(set) var previewRevision = 0
    private(set) var previewReady = false
    private(set) var previewError: String?
    private(set) var inputError: String?
    private(set) var saveError: String?
    private(set) var isLoading = false
    private(set) var isSaving = false
    private(set) var skinWriteState: MinecraftCharacterWriteState = .unchanged
    private(set) var capeWriteState: MinecraftCharacterWriteState = .unchanged
    private(set) var localPersistencePending = false

    private var baselineCapeID: String?
    private var baselineVariant: MinecraftSkinVariant = .classic

    init(
        api: any MinecraftProfileAPI,
        sessions: MinecraftSessionCoordinator,
        context: ModelContext,
        appearances: MinecraftAppearanceStore,
        saveContext: (() throws -> Void)? = nil
    ) {
        self.api = api
        self.sessions = sessions
        self.context = context
        self.appearances = appearances
        self.saveContext = saveContext ?? { try context.save() }
    }

    var canChooseVariant: Bool {
        guard !isSaving, let skin = selectedSkin ?? originalSkin else { return false }
        return !skin.isLegacy
    }

    var hasUnsavedChanges: Bool {
        localPersistencePending || skinNeedsChange || selectedCapeID != baselineCapeID
    }

    var canSave: Bool {
        hasUnsavedChanges && previewReady && previewError == nil && !isSaving
            && profile != nil && account.flatMap { sessions.identity(for: $0) } != nil
    }

    private var skinNeedsChange: Bool {
        guard let candidate = selectedCandidate else { return false }
        guard let originalSkin else { return true }
        return candidate.variant != originalSkin.variant || !candidate.skin.hasSamePixels(as: originalSkin.skin)
    }

    private var selectedCandidate: MinecraftSkinUpload? {
        guard let data = selectedSkin?.png ?? originalSkin?.png else { return nil }
        return try? MinecraftSkinUpload(png: data, variant: selectedVariant)
    }

    func open(_ account: Account) async {
        guard activeAccountID != account.persistentModelID else { return }
        invalidateRequests()
        activeAccountID = account.persistentModelID
        self.account = account
        profile = nil; capes = []; capeImages = [:]; capeImageErrors = [:]
        originalSkin = nil; selectedSkin = nil; selectedVariant = .classic
        selectedCapeID = nil; baselineCapeID = nil; baselineVariant = .classic
        skinWriteState = .unchanged; capeWriteState = .unchanged; localPersistencePending = false
        inputError = nil; saveError = nil
        await loadProfile()
    }

    func loadProfile() async {
        guard let account, !isSaving else { return }
        let requestGeneration = generation
        isLoading = true
        capeLoadState = .loading
        saveError = nil
        do {
            let server = try await readServerAppearance(account, generation: requestGeneration)
            try checkCurrent(account, generation: requestGeneration)
            let isFirstProfile = profile == nil
            profile = server.profile
            capes = server.profile.capes ?? []
            appearances.record(server.profile, for: account.xuid)
            originalSkin = server.skin
            if isFirstProfile {
                selectedSkin = nil
                baselineCapeID = server.profile.activeCape?.id
                selectedCapeID = baselineCapeID
                baselineVariant = server.skin?.variant ?? .classic
                selectedVariant = baselineVariant
            }
            capeLoadState = .loaded
            isLoading = false
            for cape in capes { loadCapeImage(cape, generation: requestGeneration) }
            await rebuildPreview(generation: requestGeneration)
        } catch is CancellationError {
            return
        } catch {
            guard isCurrent(account, generation: requestGeneration) else { return }
            isLoading = false
            capeLoadState = .failed(error.localizedDescription)
            previewReady = false
            previewError = error.localizedDescription
        }
    }

    func importSkin(_ data: Data) {
        guard !isSaving else { return }
        do {
            let upload = try MinecraftSkinUpload(png: data, variant: selectedVariant)
            selectedSkin = upload
            selectedVariant = upload.variant
            inputError = nil
            saveError = nil
            skinWriteState = .pending
            Task { await rebuildPreview(generation: generation) }
        } catch {
            inputError = error.localizedDescription
        }
    }

    func reportInputError(_ message: String) {
        guard !isSaving else { return }
        inputError = message
    }

    func selectVariant(_ variant: MinecraftSkinVariant) {
        guard canChooseVariant, variant != selectedVariant else { return }
        selectedVariant = variant
        inputError = nil
        saveError = nil
        skinWriteState = .pending
        Task { await rebuildPreview(generation: generation) }
    }

    func selectCape(_ id: String?) {
        guard !isSaving, id == nil || capes.contains(where: { $0.id == id }) else { return }
        selectedCapeID = id
        capeWriteState = id == baselineCapeID ? .unchanged : .pending
        saveError = nil
        Task { await rebuildPreview(generation: generation) }
    }

    func retryPreview() {
        guard !isSaving else { return }
        Task { await rebuildPreview(generation: generation) }
    }

    func setPreviewReady(_ ready: Bool, revision: Int) {
        guard revision == previewRevision else { return }
        previewReady = ready
        if ready { previewError = nil }
    }

    func discardDraft() {
        guard !isSaving else { return }
        selectedSkin = nil
        selectedVariant = baselineVariant
        selectedCapeID = baselineCapeID
        inputError = nil; saveError = nil
        skinWriteState = .unchanged; capeWriteState = .unchanged
        Task { await rebuildPreview(generation: generation) }
    }

    func close() {
        saveTask?.cancel()
        saveTask = nil
        isSaving = false
        invalidateRequests()
        account = nil; activeAccountID = nil
        profile = nil; capes = []; capeImages = [:]; capeImageErrors = [:]
        selectedSkin = nil; originalSkin = nil; selectedCapeID = nil
        previewSkin = nil; previewReady = false; previewError = nil
        skinWriteState = .unchanged; capeWriteState = .unchanged; localPersistencePending = false
        inputError = nil; saveError = nil
    }

    func invalidateForAccountChange() {
        close()
    }

    func save() async -> Bool {
        guard canSave, let account else { return false }
        isSaving = true
        saveError = nil
        let saveGeneration = generation
        let task = Task { await performSave(account: account, generation: saveGeneration) }
        saveTask = task
        let succeeded = await task.value
        if generation == saveGeneration {
            saveTask = nil
            isSaving = false
        }
        return succeeded
    }

    private func performSave(account: Account, generation saveGeneration: UUID) async -> Bool {
        if localPersistencePending {
            do {
                try persist(profile, on: account, generation: saveGeneration)
                localPersistencePending = false
            } catch {
                guard isCurrent(account, generation: saveGeneration) else { return false }
                saveError = String(appLocalized: "Изменения персонажа сохранены на сервере, но не удалось сохранить их в Hako: \(error.localizedDescription)")
                return false
            }
        }

        do {
            var server = try await readServerAppearance(account, generation: saveGeneration)
            try checkCurrent(account, generation: saveGeneration)
            apply(server, to: account, generation: saveGeneration)
            reconcile(server)

            if skinNeedsChange {
                guard let upload = selectedCandidate else { throw MinecraftSkinUploadError.invalidPNG }
                skinWriteState = .pending
                do {
                    try await authorized(account, generation: saveGeneration) { try await self.api.uploadSkin(accessToken: $0, png: upload.png, variant: upload.variant) }
                    skinWriteState = .unknown
                } catch {
                    guard isCurrent(account, generation: saveGeneration) else { return false }
                    skinWriteState = Self.isUncertain(error) ? .unknown : .failed(error.localizedDescription)
                    await reconcileAfterFailure(account, generation: saveGeneration, error: error)
                    return false
                }
                do {
                    server = try await readServerAppearance(account, generation: saveGeneration)
                    try checkCurrent(account, generation: saveGeneration)
                    apply(server, to: account, generation: saveGeneration)
                    reconcile(server)
                    guard skinWriteState == .confirmed else {
                        saveError = String(appLocalized: "Скин отправлен, но Minecraft пока не подтвердил его. Повторите сохранение.")
                        persistConfirmedMutationIfNeeded(server, account: account, generation: saveGeneration)
                        return false
                    }
                } catch {
                    guard isCurrent(account, generation: saveGeneration) else { return false }
                    skinWriteState = .unknown
                    saveError = String(appLocalized: "Не удалось проверить результат загрузки скина. Повторите сохранение, чтобы сверить его с сервером.")
                    return false
                }
            }

            if selectedCapeID != server.profile.activeCape?.id {
                capeWriteState = .pending
                do {
                    try await authorized(account, generation: saveGeneration) { token in
                        if let capeID = self.selectedCapeID { try await self.api.activateCape(accessToken: token, capeID: capeID) }
                        else { try await self.api.unequipCape(accessToken: token) }
                    }
                    capeWriteState = .unknown
                } catch {
                    guard isCurrent(account, generation: saveGeneration) else { return false }
                    capeWriteState = Self.isUncertain(error) ? .unknown : .failed(error.localizedDescription)
                    await reconcileAfterFailure(account, generation: saveGeneration, error: error)
                    return false
                }
                do {
                    server = try await readServerAppearance(account, generation: saveGeneration)
                    try checkCurrent(account, generation: saveGeneration)
                    apply(server, to: account, generation: saveGeneration)
                    reconcile(server)
                    guard capeWriteState == .confirmed else {
                        saveError = String(appLocalized: "Плащ отправлен, но Minecraft пока не подтвердил его. Повторите сохранение.")
                        persistConfirmedMutationIfNeeded(server, account: account, generation: saveGeneration)
                        return false
                    }
                } catch {
                    guard isCurrent(account, generation: saveGeneration) else { return false }
                    capeWriteState = .unknown
                    saveError = String(appLocalized: "Не удалось проверить результат изменения плаща. Повторите сохранение, чтобы сверить его с сервером.")
                    return false
                }
            }

            try persist(server.profile, on: account, generation: saveGeneration)
            localPersistencePending = false
            selectedSkin = nil
            selectedVariant = server.skin?.variant ?? .classic
            baselineVariant = selectedVariant
            baselineCapeID = server.profile.activeCape?.id
            selectedCapeID = baselineCapeID
            originalSkin = server.skin
            skinWriteState = .unchanged; capeWriteState = .unchanged
            await rebuildPreview(generation: saveGeneration)
            saveError = nil
            return true
        } catch is CancellationError {
            return false
        } catch {
            guard isCurrent(account, generation: saveGeneration) else { return false }
            saveError = error.localizedDescription
            return false
        }
    }

    private func reconcileAfterFailure(_ account: Account, generation: UUID, error: Error) async {
        do {
            let server = try await readServerAppearance(account, generation: generation)
            try checkCurrent(account, generation: generation)
            apply(server, to: account, generation: generation)
            reconcile(server)
            if skinWriteState == .confirmed || capeWriteState == .confirmed {
                persistConfirmedMutationIfNeeded(server, account: account, generation: generation)
            }
            if skinWriteState == .unknown || capeWriteState == .unknown {
                saveError = String(appLocalized: "Результат запроса неизвестен. Состояние сверено с Minecraft; повторное сохранение сначала проверит сервер.")
            } else {
                saveError = error.localizedDescription
            }
        } catch {
            guard isCurrent(account, generation: generation) else { return }
            saveError = String(appLocalized: "Не удалось сверить изменения с Minecraft. Повторное сохранение сначала проверит сервер.")
        }
    }

    private func persistConfirmedMutationIfNeeded(_ server: ServerAppearance, account: Account, generation: UUID) {
        guard skinWriteState == .confirmed || capeWriteState == .confirmed else { return }
        do {
            try persist(server.profile, on: account, generation: generation)
            localPersistencePending = false
        } catch {
            localPersistencePending = true
            saveError = String(appLocalized: "Часть изменений уже сохранена в Minecraft, но не в Hako: \(error.localizedDescription)")
        }
    }

    private func persist(_ profile: MinecraftProfileResponse?, on account: Account, generation: UUID) throws {
        try checkCurrent(account, generation: generation)
        guard let profile else { throw MinecraftProfileAPIError.invalidResponse }
        account.connect(profile.minecraftProfile)
        do { try saveContext() }
        catch {
            localPersistencePending = true
            throw error
        }
    }

    private func reconcile(_ server: ServerAppearance) {
        let hadSkinDraft = skinWriteState != .unchanged
        let hadCapeDraft = capeWriteState != .unchanged
        if hadSkinDraft {
            skinWriteState = hasMatchingSkin(server) ? .confirmed : .pending
        } else {
            skinWriteState = .unchanged
        }

        if selectedCapeID == server.profile.activeCape?.id {
            capeWriteState = hadCapeDraft ? .confirmed : .unchanged
        } else {
            capeWriteState = .pending
        }
    }

    private func apply(_ server: ServerAppearance, to account: Account, generation: UUID) {
        guard isCurrent(account, generation: generation) else { return }
        let hasSkinDraft = skinNeedsChange
        let hasCapeDraft = selectedCapeID != baselineCapeID
        profile = server.profile
        capes = server.profile.capes ?? []
        originalSkin = server.skin
        baselineCapeID = server.profile.activeCape?.id
        baselineVariant = server.skin?.variant ?? .classic
        if !hasSkinDraft { selectedVariant = baselineVariant }
        if !hasCapeDraft { selectedCapeID = baselineCapeID }
        appearances.record(server.profile, for: account.xuid)
        capeLoadState = .loaded
        if let cape = server.profile.activeCape {
            loadCapeImage(cape, generation: generation)
        }
    }

    private func hasMatchingSkin(_ server: ServerAppearance) -> Bool {
        guard let target = selectedCandidate,
              let active = server.profile.activeSkin,
              active.id != nil,
              let activeVariant = active.variant.flatMap({ MinecraftSkinVariant(rawValue: $0.uppercased()) }),
              activeVariant == target.variant,
              let serverSkin = server.skin
        else { return false }
        return serverSkin.skin.hasSamePixels(as: target.skin)
    }

    private func readServerAppearance(_ account: Account, generation: UUID) async throws -> ServerAppearance {
        let profile = try await authorized(account, generation: generation) { try await self.api.profile(accessToken: $0) }
        try checkCurrent(account, generation: generation)
        let skin: MinecraftSkinUpload?
        if let active = profile.activeSkin {
            guard let url = active.secureURL else { throw MinecraftProfileAPIError.invalidResponse }
            let data = try await api.texture(at: url)
            try checkCurrent(account, generation: generation)
            skin = try MinecraftSkinUpload(
                png: data,
                variant: active.variant.flatMap { MinecraftSkinVariant(rawValue: $0.uppercased()) } ?? .classic
            )
        } else {
            skin = nil
        }
        return ServerAppearance(profile: profile, skin: skin)
    }

    private func authorized<Value>(
        _ account: Account,
        generation: UUID,
        operation: (String) async throws -> Value
    ) async throws -> Value {
        try checkCurrent(account, generation: generation)
        guard let identity = sessions.identity(for: account) else { throw MinecraftSessionError.unavailable }
        do {
            return try await operation(identity.accessToken)
        } catch MinecraftProfileAPIError.unauthorized {
            let refreshed = try await sessions.refreshAfterUnauthorized(account, rejectedToken: identity.accessToken)
            try checkCurrent(account, generation: generation)
            guard sessions.identity(for: account)?.accessToken == refreshed.accessToken else { throw MinecraftSessionError.accountChanged }
            return try await operation(refreshed.accessToken)
        }
    }

    private func checkCurrent(_ account: Account, generation: UUID) throws {
        try Task.checkCancellation()
        guard isCurrent(account, generation: generation) else { throw CancellationError() }
        guard sessions.identity(for: account) != nil else { throw MinecraftSessionError.unavailable }
    }

    private func isCurrent(_ account: Account, generation: UUID) -> Bool {
        self.generation == generation && activeAccountID == account.persistentModelID
    }

    private func loadCapeImage(_ cape: MinecraftProfileResponse.Cape, generation: UUID) {
        guard capeImages[cape.id] == nil, capeTasks[cape.id] == nil else { return }
        guard let url = cape.secureURL else {
            capeImageErrors[cape.id] = MinecraftProfileAPIError.invalidResponse.localizedDescription
            return
        }
        let task = Task { try await api.texture(at: url) }
        capeTasks[cape.id] = task
        Task { [weak self] in
            guard let self else { return }
            defer { capeTasks[cape.id] = nil }
            do {
                let data = try await task.value
                guard self.generation == generation else { return }
                capeImages[cape.id] = data
                capeImageErrors[cape.id] = nil
            } catch {
                guard self.generation == generation, !Task.isCancelled else { return }
                capeImageErrors[cape.id] = error.localizedDescription
            }
        }
    }

    private func loadCapeData(id: String, generation: UUID) async throws -> Data {
        if let data = capeImages[id] { return data }
        guard let cape = capes.first(where: { $0.id == id }) else { throw MinecraftProfileAPIError.invalidResponse }
        if let task = capeTasks[id] {
            let data = try await task.value
            try Task.checkCancellation()
            guard self.generation == generation else { throw CancellationError() }
            capeImages[id] = data
            return data
        }
        guard let url = cape.secureURL else { throw MinecraftProfileAPIError.invalidResponse }
        let task = Task { try await api.texture(at: url) }
        capeTasks[id] = task
        defer { capeTasks[id] = nil }
        let data = try await task.value
        try Task.checkCancellation()
        guard self.generation == generation else { throw CancellationError() }
        capeImages[id] = data
        capeImageErrors[id] = nil
        return data
    }

    private func rebuildPreview(generation: UUID) async {
        let request = UUID()
        previewGeneration = request
        previewReady = false
        previewError = nil
        do {
            let capeData: Data?
            if let selectedCapeID {
                capeData = try await loadCapeData(id: selectedCapeID, generation: generation)
            } else {
                capeData = nil
            }
            try Task.checkCancellation()
            guard self.generation == generation, previewGeneration == request else { return }
            let skin: MinecraftSkin
            if let candidate = selectedCandidate {
                skin = try MinecraftSkin(data: candidate.png, variant: candidate.variant, capeData: capeData)
            } else {
                skin = try MinecraftSkin(data: Self.bundledSteveData(), variant: .classic, capeData: capeData)
            }
            previewSkin = skin
            previewRevision += 1
        } catch is CancellationError {
            return
        } catch {
            guard self.generation == generation, previewGeneration == request else { return }
            previewSkin = nil
            previewRevision += 1
            previewError = error.localizedDescription
        }
    }

    private static func bundledSteveData() throws -> Data {
        guard let url = Bundle.main.url(forResource: "Steve", withExtension: "png") else {
            throw MinecraftSkin.DecodingError.invalidTexture
        }
        return try Data(contentsOf: url)
    }

    private func invalidateRequests() {
        generation = UUID()
        previewGeneration = UUID()
        for task in capeTasks.values { task.cancel() }
        capeTasks.removeAll()
        isLoading = false
    }

    private static func isUncertain(_ error: Error) -> Bool {
        if error is URLError { return true }
        if case MinecraftProfileAPIError.httpStatus(let status) = error { return status >= 500 }
        return false
    }
}

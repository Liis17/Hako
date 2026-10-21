//
//  MinecraftSkinLoader.swift
//  Hako
//

import Foundation
import Observation

struct MinecraftSkinSource: Hashable {
    let uuid: String?
    let skinURL: URL?
    let variant: MinecraftSkinVariant?
    let loadCape: Bool
    let appearanceRevision: Int
    let isAuthoritative: Bool
    let capeURL: URL?
    let capeIsKnown: Bool

    init(
        uuid: String?, skinURL: URL?, variant: MinecraftSkinVariant?, loadCape: Bool = false,
        appearanceRevision: Int = 0, isAuthoritative: Bool = false,
        capeURL: URL? = nil, capeIsKnown: Bool = false
    ) {
        self.uuid = uuid
        self.skinURL = skinURL
        self.variant = variant
        self.loadCape = loadCape
        self.appearanceRevision = appearanceRevision
        self.isAuthoritative = isAuthoritative
        self.capeURL = capeURL
        self.capeIsKnown = capeIsKnown
    }
}

/// Загрузка скина без токенов; старым аккаунтам восстанавливает метаданные по UUID.
@MainActor
@Observable
final class MinecraftSkinLoader {
    static let shared = MinecraftSkinLoader()

    private let session: URLSession
    private var skins: [MinecraftSkinSource: MinecraftSkin] = [:]
    private var profiles: [String: ResolvedProfile] = [:]

    enum LoadingError: Error {
        case httpStatus(Int)
        case invalidProfile
    }

    init(session: URLSession = .shared) {
        self.session = session
    }

    func load(_ source: MinecraftSkinSource) async throws -> MinecraftSkin? {
        try Task.checkCancellation()
        guard let uuid = source.uuid else { return nil }
        if let cached = skins[source] { return cached }

        let needsProfile = !source.isAuthoritative && (source.loadCape || source.skinURL == nil || source.variant == nil)
        let profile: ResolvedProfile?
        if !needsProfile {
            profile = nil
        } else if let cached = profiles[uuid] {
            profile = cached
        } else {
            do {
                let resolved = try await resolveProfile(uuid: uuid)
                try Task.checkCancellation()
                profiles[uuid] = resolved
                profile = resolved
            } catch {
                try Task.checkCancellation()
                guard source.skinURL != nil, source.variant != nil else { throw error }
                profile = nil
            }
        }

        let url = source.isAuthoritative
            ? source.skinURL.flatMap(Self.secureURL)
            : profile?.skinURL ?? source.skinURL.flatMap(Self.secureURL)
        guard let url else { return nil }
        let variant = source.variant ?? profile?.variant ?? .classic
        let capeData: Data?
        if source.capeIsKnown, let capeURL = source.capeURL {
            capeData = try? await data(from: capeURL)
        } else if source.capeIsKnown {
            capeData = nil
        } else if source.loadCape, let capeURL = profile?.capeURL {
            capeData = try? await data(from: capeURL)
        } else {
            capeData = nil
        }
        try Task.checkCancellation()
        let skinData = try await data(from: url)
        let skin: MinecraftSkin
        do {
            skin = try MinecraftSkin(data: skinData, variant: variant, capeData: capeData)
        } catch MinecraftSkin.DecodingError.invalidCape {
            skin = try MinecraftSkin(data: skinData, variant: variant)
        }
        try Task.checkCancellation()
        let expectsCape = source.capeIsKnown ? source.capeURL != nil : source.loadCape && profile?.capeURL != nil
        if !expectsCape || skin.capeImage != nil { skins[source] = skin }
        return skin
    }

    func imageData(from url: URL) async throws -> Data {
        try await data(from: url)
    }

    private func data(from url: URL) async throws -> Data {
        let (data, response) = try await session.data(from: url)
        try Task.checkCancellation()
        guard let response = response as? HTTPURLResponse else { throw LoadingError.invalidProfile }
        guard response.statusCode == 200 else { throw LoadingError.httpStatus(response.statusCode) }
        return data
    }

    private func resolveProfile(uuid: String) async throws -> ResolvedProfile {
        let url = URL(string: "https://sessionserver.mojang.com/session/minecraft/profile/")!
            .appending(path: uuid.replacingOccurrences(of: "-", with: ""))
        let profile = try JSONDecoder().decode(SessionProfile.self, from: await data(from: url))
        guard let encoded = profile.properties.first(where: { $0.name == "textures" })?.value,
              let decoded = Data(base64Encoded: encoded)
        else { throw LoadingError.invalidProfile }
        let textures = try JSONDecoder().decode(TexturePayload.self, from: decoded)
        return ResolvedProfile(
            skinURL: textures.textures.skin.flatMap { Self.secureURL($0.url) },
            variant: textures.textures.skin?.metadata?.model == "slim" ? .slim : .classic,
            capeURL: textures.textures.cape.flatMap { Self.secureURL($0.url) }
        )
    }

    private static func secureURL(_ url: URL) -> URL? {
        guard url.scheme == "https" || url.scheme == "http",
              var components = URLComponents(url: url, resolvingAgainstBaseURL: false)
        else { return nil }
        components.scheme = "https"
        return components.url
    }

    private struct ResolvedProfile {
        let skinURL: URL?
        let variant: MinecraftSkinVariant
        let capeURL: URL?
    }

    private struct SessionProfile: Decodable {
        struct Property: Decodable {
            let name: String
            let value: String
        }
        let properties: [Property]
    }

    private struct TexturePayload: Decodable {
        struct Textures: Decodable {
            struct Skin: Decodable {
                struct Metadata: Decodable {
                    let model: String?
                }
                let url: URL
                let metadata: Metadata?
            }
            struct Cape: Decodable {
                let url: URL
            }
            let skin: Skin?
            let cape: Cape?
            enum CodingKeys: String, CodingKey {
                case skin = "SKIN"
                case cape = "CAPE"
            }
        }
        let textures: Textures
    }
}

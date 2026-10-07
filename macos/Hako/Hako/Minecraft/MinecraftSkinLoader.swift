//
//  MinecraftSkinLoader.swift
//  Hako
//

import Foundation

struct MinecraftSkinSource: Hashable {
    let uuid: String?
    let skinURL: URL?
    let variant: MinecraftSkinVariant?
    let loadCape: Bool

    init(uuid: String?, skinURL: URL?, variant: MinecraftSkinVariant?, loadCape: Bool = false) {
        self.uuid = uuid
        self.skinURL = skinURL
        self.variant = variant
        self.loadCape = loadCape
    }
}

/// Загрузка скина без токенов; старым аккаунтам восстанавливает метаданные по UUID.
@MainActor
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

        let needsProfile = source.loadCape || source.skinURL == nil || source.variant == nil
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

        guard let url = profile?.skinURL ?? source.skinURL.flatMap(Self.secureURL) else { return nil }
        let variant = source.variant ?? profile?.variant ?? .classic
        let capeData: Data?
        if source.loadCape, let capeURL = profile?.capeURL {
            capeData = try? await data(from: capeURL)
        } else {
            capeData = nil
        }
        try Task.checkCancellation()
        let skin = try MinecraftSkin(data: await data(from: url), variant: variant, capeData: capeData)
        try Task.checkCancellation()
        skins[source] = skin
        return skin
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

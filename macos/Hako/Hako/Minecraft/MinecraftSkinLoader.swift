//
//  MinecraftSkinLoader.swift
//  Hako
//

import Foundation

struct MinecraftSkinSource: Hashable {
    let uuid: String?
    let skinURL: URL?
    let variant: MinecraftSkinVariant?
}

/// Загрузка скина без токенов; старым аккаунтам восстанавливает метаданные по UUID.
@MainActor
final class MinecraftSkinLoader {
    static let shared = MinecraftSkinLoader()

    private let session: URLSession
    private var skins: [MinecraftSkinSource: MinecraftSkin] = [:]
    private var profiles: [String: ResolvedSkin] = [:]

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

        let resolved: ResolvedSkin
        if let url = source.skinURL, let variant = source.variant {
            resolved = ResolvedSkin(url: Self.secureURL(url), variant: variant)
        } else if let cached = profiles[uuid] {
            resolved = cached
        } else {
            let url = URL(string: "https://sessionserver.mojang.com/session/minecraft/profile/")!
                .appending(path: uuid.replacingOccurrences(of: "-", with: ""))
            let profile = try JSONDecoder().decode(SessionProfile.self, from: await data(from: url))
            guard let encoded = profile.properties.first(where: { $0.name == "textures" })?.value,
                  let decoded = Data(base64Encoded: encoded)
            else { throw LoadingError.invalidProfile }
            let textures = try JSONDecoder().decode(TexturePayload.self, from: decoded)
            resolved = ResolvedSkin(
                url: textures.textures.skin.flatMap { Self.secureURL($0.url) },
                variant: textures.textures.skin?.metadata?.model == "slim" ? .slim : .classic
            )
            try Task.checkCancellation()
            profiles[uuid] = resolved
        }

        guard let url = resolved.url else { return nil }
        let skin = try MinecraftSkin(data: await data(from: url), variant: resolved.variant)
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

    private static func secureURL(_ url: URL) -> URL? {
        guard url.scheme == "https" || url.scheme == "http",
              var components = URLComponents(url: url, resolvingAgainstBaseURL: false)
        else { return nil }
        components.scheme = "https"
        return components.url
    }

    private struct ResolvedSkin {
        let url: URL?
        let variant: MinecraftSkinVariant
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
            let skin: Skin?
            enum CodingKeys: String, CodingKey {
                case skin = "SKIN"
            }
        }
        let textures: Textures
    }
}

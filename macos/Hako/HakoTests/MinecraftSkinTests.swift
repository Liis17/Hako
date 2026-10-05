import Foundation
import SwiftData
import Testing

@MainActor
struct MinecraftSkinTests {
    @Test func bundledSteve() throws {
        let skin = try MinecraftSkin.steve(bundle: SkinTestFixtures.bundle)
        #expect(skin.image.width == 64 && skin.image.height == 64)
        #expect(skin.variant == .classic)
    }

    @Test(arguments: [(32, 32), (128, 128), (64, 48)])
    func rejectsUnsupportedSize(size: (Int, Int)) throws {
        let data = try SkinTestFixtures.png(width: size.0, height: size.1)
        #expect(throws: MinecraftSkin.DecodingError.self) {
            try MinecraftSkin(data: data, variant: .classic)
        }
    }

    @Test func rejectsDamagedTexture() {
        #expect(throws: MinecraftSkin.DecodingError.self) {
            try MinecraftSkin(data: Data("not a skin".utf8), variant: .classic)
        }
    }

    @Test func legacyMirrorsAllLimbFaces() throws {
        let skin = try MinecraftSkin(data: SkinTestFixtures.png(height: 32), variant: .slim)
        #expect(skin.variant == .classic)
        // Цвет каждого пикселя кодирует его исходные координаты: проверяем и стороны, и разворот.
        for (x, y, red, green) in [
            (20, 48, 7, 16), (24, 48, 11, 16),
            (16, 52, 11, 20), (20, 52, 7, 20), (24, 52, 3, 20), (28, 52, 15, 20),
            (19, 63, 8, 31), (23, 63, 4, 31), (27, 63, 0, 31), (31, 63, 12, 31),
            (36, 48, 47, 16), (40, 48, 51, 16),
            (32, 52, 51, 20), (36, 52, 47, 20), (40, 52, 43, 20), (44, 52, 55, 20),
            (35, 63, 48, 31), (39, 63, 44, 31), (43, 63, 40, 31), (47, 63, 52, 31)
        ] {
            #expect(SkinTestFixtures.pixel(skin.image, x: x, y: y) == [UInt8(red), UInt8(green), 100, 255])
        }
        #expect(SkinTestFixtures.pixel(skin.image, x: 20, y: 36)[3] == 0)
    }

    @Test func hidesOpaqueLegacyHat() throws {
        let skin = try MinecraftSkin(data: SkinTestFixtures.png(height: 32), variant: .classic)
        #expect(SkinTestFixtures.pixel(skin.image, x: 40, y: 8) == [0, 0, 0, 0])
        #expect(SkinTestFixtures.pixel(skin.image, x: 44, y: 20)[3] == 255)
    }

    @Test func retainsLegacyHatWithTransparency() throws {
        let data = try SkinTestFixtures.png(height: 32, transparentPixel: (32, 0))
        let skin = try MinecraftSkin(data: data, variant: .classic)
        #expect(SkinTestFixtures.pixel(skin.image, x: 40, y: 8) == [40, 8, 100, 255])
    }

    @Test func modernOverlayStaysTransparent() throws {
        let skin = try MinecraftSkin(data: SkinTestFixtures.png(transparentPixel: (40, 8)), variant: .slim)
        #expect(skin.variant == .slim)
        #expect(SkinTestFixtures.pixel(skin.image, x: 40, y: 8)[3] == 0)
        #expect(SkinTestFixtures.pixel(skin.image, x: 8, y: 8)[3] == 255)
    }

    @Test(arguments: ["CLASSIC", "SLIM", "unexpected"])
    func activeProfileVariantIsStored(variant: String) throws {
        let data = try JSONSerialization.data(withJSONObject: [
            "id": "test-player", "name": "TestPlayer",
            "skins": [
                ["state": "INACTIVE", "url": "http://textures.minecraft.net/texture/old", "variant": "CLASSIC"],
                ["state": "ACTIVE", "url": "http://textures.minecraft.net/texture/current", "variant": variant]
            ]
        ])
        let profile = try JSONDecoder().decode(MinecraftProfileResponse.self, from: data).minecraftProfile
        let account = Account(xbox: XboxProfile(xuid: "test", gamertag: "Test", avatarURL: nil), email: nil)
        account.connect(profile)
        #expect(account.minecraftName == "TestPlayer")
        #expect(account.minecraftSkinURL?.absoluteString == "https://textures.minecraft.net/texture/current")
        #expect(account.minecraftSkinVariant == MinecraftSkinVariant(rawValue: variant)?.rawValue)
    }

    @Test func oldProfileWithoutVariantStillDecodes() throws {
        let data = Data(#"{"id":"old","name":"OldPlayer","skins":[{"state":"ACTIVE","url":"https://textures.minecraft.net/texture/old"}]}"#.utf8)
        let profile = try JSONDecoder().decode(MinecraftProfileResponse.self, from: data).minecraftProfile
        #expect(profile.skinVariant == nil)
        #expect(profile.skinURL != nil)
    }
}

@Suite(.serialized)
@MainActor
struct MinecraftSkinLoaderTests {
    private let textureURL = URL(string: "https://textures.minecraft.net/texture/test")!
    private let uuid = "00112233445566778899aabbccddeeff"
    private var source: MinecraftSkinSource { .init(uuid: uuid, skinURL: textureURL, variant: .slim) }
    private var profileURL: URL { URL(string: "https://sessionserver.mojang.com/session/minecraft/profile/\(uuid)")! }

    private func session() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [SkinURLProtocol.self]
        return URLSession(configuration: configuration)
    }

    @Test func downloadsAndCachesSkinWithoutToken() async throws {
        SkinURLProtocol.prepare([textureURL: .init(data: try SkinTestFixtures.png())])
        let session = session()
        defer { session.invalidateAndCancel() }
        let loader = MinecraftSkinLoader(session: session)
        let skin = try #require(await loader.load(source))
        #expect(skin.variant == .slim)
        _ = try await loader.load(source)
        #expect(SkinURLProtocol.requests.count == 1)
        #expect(SkinURLProtocol.requests.first?.value(forHTTPHeaderField: "Authorization") == nil)
    }

    @Test func noMinecraftNeedsNoRequest() async throws {
        SkinURLProtocol.prepare([:])
        let session = session()
        defer { session.invalidateAndCancel() }
        let skin = try await MinecraftSkinLoader(session: session).load(.init(uuid: nil, skinURL: nil, variant: nil))
        #expect(skin == nil)
        #expect(SkinURLProtocol.requests.isEmpty)
    }

    @Test(arguments: [true, false])
    func resolvesMissingVariantAndCachesProfile(slim: Bool) async throws {
        let httpURL = URL(string: "http://textures.minecraft.net/texture/test")!
        SkinURLProtocol.prepare([
            profileURL: .init(data: try SkinTestFixtures.profile(textureURL: httpURL, slim: slim)),
            textureURL: .init(data: try SkinTestFixtures.png())
        ])
        let session = session()
        defer { session.invalidateAndCancel() }
        let loader = MinecraftSkinLoader(session: session)
        let old = MinecraftSkinSource(uuid: uuid, skinURL: URL(string: "https://textures.minecraft.net/texture/stale"), variant: nil)
        let skin = try #require(await loader.load(old))
        #expect(skin.variant == (slim ? .slim : .classic))
        _ = try await loader.load(old)
        #expect(SkinURLProtocol.requests.map(\.url) == [profileURL, textureURL])
    }

    @Test func profileWithoutSkinIsCached() async throws {
        SkinURLProtocol.prepare([profileURL: .init(data: try SkinTestFixtures.profile(textureURL: nil))])
        let session = session()
        defer { session.invalidateAndCancel() }
        let loader = MinecraftSkinLoader(session: session)
        let old = MinecraftSkinSource(uuid: uuid, skinURL: nil, variant: nil)
        #expect(try await loader.load(old) == nil)
        #expect(try await loader.load(old) == nil)
        #expect(SkinURLProtocol.requests.count == 1)
    }

    @Test func rejectsHTTPFailure() async throws {
        SkinURLProtocol.prepare([textureURL: .init(status: 503, data: try SkinTestFixtures.png())])
        let session = session()
        defer { session.invalidateAndCancel() }
        await #expect(throws: MinecraftSkinLoader.LoadingError.self) {
            try await MinecraftSkinLoader(session: session).load(source)
        }
    }

    @Test func rejectsCorruptDownloadedTexture() async throws {
        SkinURLProtocol.prepare([textureURL: .init(data: Data("corrupt".utf8))])
        let session = session()
        defer { session.invalidateAndCancel() }
        await #expect(throws: MinecraftSkin.DecodingError.self) {
            try await MinecraftSkinLoader(session: session).load(source)
        }
    }

    @Test func offlineConnectionFailsWithoutCaching() async throws {
        SkinURLProtocol.prepare([textureURL: .init(data: Data(), error: URLError(.notConnectedToInternet))])
        let session = session()
        defer { session.invalidateAndCancel() }
        let loader = MinecraftSkinLoader(session: session)
        await #expect(throws: URLError.self) { try await loader.load(source) }
        SkinURLProtocol.prepare([textureURL: .init(data: try SkinTestFixtures.png())])
        #expect(try await loader.load(source) != nil)
        #expect(SkinURLProtocol.requests.count == 1)
    }

    @Test func cancellationDoesNotCacheTheResult() async throws {
        SkinURLProtocol.prepare([textureURL: .init(data: try SkinTestFixtures.png(), delay: 5)])
        let session = session()
        defer { session.invalidateAndCancel() }
        let loader = MinecraftSkinLoader(session: session)
        let task = Task { try await loader.load(source) }
        for _ in 0..<100 {
            if !SkinURLProtocol.requests.isEmpty { break }
            try await Task.sleep(for: .milliseconds(10))
        }
        try #require(SkinURLProtocol.requests.count == 1)
        task.cancel()
        await #expect(throws: (any Error).self) { try await task.value }

        SkinURLProtocol.prepare([textureURL: .init(data: try SkinTestFixtures.png())])
        let skin = try #require(await loader.load(source))
        #expect(skin.variant == .slim)
        #expect(SkinURLProtocol.requests.count == 1)
    }
}

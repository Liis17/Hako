//
//  Avatars.swift
//  Hako
//

import ImageIO
import SwiftUI

/// Аватар аккаунта: голова персонажа Minecraft, если скин известен, иначе аватар Xbox.
struct AccountAvatar: View {
    let account: Account

    var body: some View {
        if let skinURL = account.minecraftSkinURL {
            MinecraftHead(skinURL: skinURL)
        } else {
            XboxAvatar(url: account.xboxAvatarURL, name: account.gamertag)
        }
    }
}

/// Картинка профиля Xbox; пока её нет — первая буква gamertag на розовом фоне.
struct XboxAvatar: View {
    let url: URL?
    let name: String

    var body: some View {
        AsyncImage(url: url) { image in
            image
                .resizable()
                .scaledToFill()
        } placeholder: {
            Text(verbatim: name.prefix(1).uppercased())
                .font(.system(size: 20, weight: .bold))
                .foregroundStyle(.white)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(Color.sakuraDeep.gradient)
        }
    }
}

/// Голова персонажа из текстуры скина: лицо (8×8 в точке 8,8) и поверх него слой шапки (40,8).
/// Скин 64×64 или старый 64×32.
struct MinecraftHead: View {
    let skinURL: URL

    @State private var layers: [CGImage] = []

    var body: some View {
        ZStack {
            Color.secondary.opacity(0.15)
            ForEach(layers.indices, id: \.self) { index in
                Image(decorative: layers[index], scale: 1)
                    .resizable()
                    .interpolation(.none)
            }
        }
        .task(id: skinURL) { layers = await Self.headLayers(from: skinURL) }
    }

    private static func headLayers(from url: URL) async -> [CGImage] {
        guard let data = try? await URLSession.shared.data(from: url).0,
              let source = CGImageSourceCreateWithData(data as CFData, nil),
              let skin = CGImageSourceCreateImageAtIndex(source, 0, nil),
              let face = skin.cropping(to: CGRect(x: 8, y: 8, width: 8, height: 8))
        else { return [] }
        // Как в игре: у старых скинов 64×32 слой одежды без прозрачных пикселей — это фон, а не шапка.
        let overlay = skin.cropping(to: CGRect(x: 32, y: 0, width: 32, height: 32))
        guard skin.height == 64 || overlay.map(hasTransparency) == true,
              let hat = skin.cropping(to: CGRect(x: 40, y: 8, width: 8, height: 8))
        else { return [face] }
        return [face, hat]
    }

    private static func hasTransparency(_ image: CGImage) -> Bool {
        var pixels = [UInt8](repeating: 0, count: image.width * image.height * 4)
        pixels.withUnsafeMutableBytes { buffer in
            let context = CGContext(
                data: buffer.baseAddress,
                width: image.width,
                height: image.height,
                bitsPerComponent: 8,
                bytesPerRow: image.width * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
            )
            context?.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        }
        return stride(from: 3, to: pixels.count, by: 4).contains { pixels[$0] < 128 }
    }
}

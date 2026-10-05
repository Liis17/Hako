import AppKit
import ImageIO
import SwiftUI
import UniformTypeIdentifiers

struct InstanceIcon: View {
    let symbol: String
    var data: Data?
    var url: URL?

    private var image: NSImage? {
        if let data { return NSImage(data: data) }
        if symbol.isEmpty, let url { return NSImage(contentsOf: url) }
        return nil
    }

    var body: some View {
        GeometryReader { geometry in
            Group {
                if let image {
                    Image(nsImage: image).resizable().scaledToFill()
                } else {
                    Image(systemName: symbol.isEmpty ? "shippingbox.fill" : symbol)
                        .font(.system(size: geometry.size.width * 0.43, weight: .semibold))
                        .foregroundStyle(.white)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .background(Color.sakuraDeep)
                }
            }
            .frame(width: geometry.size.width, height: geometry.size.height)
            .clipShape(.rect(cornerRadius: geometry.size.width * 0.24))
        }
        .accessibilityHidden(true)
    }
}

extension View {
    func instanceSurface() -> some View {
        padding(20)
            .background(Color.sakuraDeep.opacity(0.1), in: .rect(cornerRadius: 22))
            .background(.regularMaterial, in: .rect(cornerRadius: 22))
            .overlay { RoundedRectangle(cornerRadius: 22).strokeBorder(.white.opacity(0.5), lineWidth: 1) }
    }
}

struct InstanceIconPicker: View {
    @Binding var draft: InstanceDraft
    var existingIcon: URL?
    @State private var importing = false
    @State private var error: String?

    private let symbols = [("shippingbox.fill", "Коробка"), ("cube.fill", "Куб"), ("leaf.fill", "Лист"), ("flame.fill", "Пламя"), ("bolt.fill", "Молния"), ("moon.stars.fill", "Луна")]

    var body: some View {
        HStack(alignment: .top, spacing: 18) {
            InstanceIcon(symbol: draft.iconSymbol, data: draft.iconData, url: existingIcon).frame(width: 72, height: 72)
            VStack(alignment: .leading, spacing: 10) {
                HStack(spacing: 8) {
                    ForEach(symbols, id: \.0) { symbol, title in
                        Button {
                            draft.iconSymbol = symbol
                            draft.iconData = nil
                        } label: {
                            Image(systemName: symbol).frame(width: 30, height: 30)
                                .foregroundStyle(draft.iconSymbol == symbol ? .white : Color.sakuraDeep)
                                .background(draft.iconSymbol == symbol ? Color.sakuraDeep : .clear, in: .rect(cornerRadius: 8))
                        }
                        .buttonStyle(.plain).help(title).accessibilityLabel("Иконка: \(title)")
                    }
                }
                Button("Своя картинка…") { importing = true }.buttonStyle(.glass)
                if let error { Text(error).font(.caption).foregroundStyle(Color.shu) }
            }
        }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.png, .jpeg]) { result in
            do {
                let url = try result.get()
                let access = url.startAccessingSecurityScopedResource()
                defer { if access { url.stopAccessingSecurityScopedResource() } }
                draft.iconData = try Self.normalizedIcon(url)
                draft.iconSymbol = ""
                error = nil
            } catch { self.error = "Не удалось открыть картинку. Выберите PNG или JPEG." }
        }
    }

    private static func normalizedIcon(_ url: URL) throws -> Data {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let thumbnail = CGImageSourceCreateThumbnailAtIndex(source, 0, [kCGImageSourceCreateThumbnailFromImageAlways: true, kCGImageSourceThumbnailMaxPixelSize: 512, kCGImageSourceCreateThumbnailWithTransform: true] as CFDictionary),
              let context = CGContext(data: nil, width: 256, height: 256, bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw InstanceFileError.message("Не удалось прочитать картинку.")
        }
        let side = min(thumbnail.width, thumbnail.height)
        guard let crop = thumbnail.cropping(to: CGRect(x: (thumbnail.width - side) / 2, y: (thumbnail.height - side) / 2, width: side, height: side)) else { throw InstanceFileError.message("Не удалось подготовить иконку.") }
        context.interpolationQuality = .high
        context.draw(crop, in: CGRect(x: 0, y: 0, width: 256, height: 256))
        let data = NSMutableData()
        guard let image = context.makeImage(), let destination = CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil) else { throw InstanceFileError.message("Не удалось сохранить иконку.") }
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination) else { throw InstanceFileError.message("Не удалось сохранить иконку.") }
        return data as Data
    }
}

struct InstanceParametersEditor: View {
    @Binding var usesGlobal: Bool
    @Binding var parameters: InstanceParameters
    @Binding var isValid: Bool
    @AppStorage(GameLaunchDefaults.Key.javaArguments) private var globalJava = ""
    @AppStorage(GameLaunchDefaults.Key.minecraftArguments) private var globalMinecraft = ""
    @AppStorage(GameLaunchDefaults.Key.fullscreen) private var globalFullscreen = false
    @AppStorage(GameLaunchDefaults.Key.windowWidth) private var globalWidth = 1280
    @AppStorage(GameLaunchDefaults.Key.windowHeight) private var globalHeight = 720
    @State private var width: String
    @State private var height: String

    init(usesGlobal: Binding<Bool>, parameters: Binding<InstanceParameters>, isValid: Binding<Bool>) {
        _usesGlobal = usesGlobal; _parameters = parameters; _isValid = isValid
        _width = State(initialValue: String(parameters.wrappedValue.windowWidth))
        _height = State(initialValue: String(parameters.wrappedValue.windowHeight))
    }

    private var global: InstanceParameters {
        var value = InstanceParameters()
        value.javaArguments = globalJava; value.minecraftArguments = globalMinecraft
        value.fullscreen = globalFullscreen
        value.windowWidth = globalWidth > 0 ? globalWidth : 1280
        value.windowHeight = globalHeight > 0 ? globalHeight : 720
        return value
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Toggle("Использовать глобальные параметры", isOn: $usesGlobal).tint(.sakuraDeep)
            VStack(alignment: .leading, spacing: 16) {
                parameterField("Аргументы Java", text: usesGlobal ? .constant(global.javaArguments) : $parameters.javaArguments)
                parameterField("Аргументы Minecraft", text: usesGlobal ? .constant(global.minecraftArguments) : $parameters.minecraftArguments)
                Picker("Режим отображения", selection: usesGlobal ? .constant(global.fullscreen) : $parameters.fullscreen) {
                    Text("Оконный").tag(false)
                    Text("Полноэкранный").tag(true)
                }.pickerStyle(.segmented)
                HStack(spacing: 18) {
                    dimension("Ширина", text: usesGlobal ? .constant(String(global.windowWidth)) : $width)
                    dimension("Высота", text: usesGlobal ? .constant(String(global.windowHeight)) : $height)
                }.disabled(usesGlobal ? global.fullscreen : parameters.fullscreen)
                if !isValid { Text("Введите целые размеры окна больше 0.").font(.caption).foregroundStyle(Color.shu) }
            }.disabled(usesGlobal)
            if usesGlobal { Text("Применяются текущие параметры из настроек лаунчера.").font(.caption).foregroundStyle(.secondary) }
        }
        .onChange(of: usesGlobal) { old, new in
            if old && !new {
                parameters = global
                width = String(global.windowWidth); height = String(global.windowHeight)
            }
            validate()
        }
        .onChange(of: width) { _, new in if let value = GameLaunchDefaults.windowDimension(from: new) { parameters.windowWidth = value }; validate() }
        .onChange(of: height) { _, new in if let value = GameLaunchDefaults.windowDimension(from: new) { parameters.windowHeight = value }; validate() }
        .onChange(of: parameters.fullscreen) { validate() }
        .onAppear { validate() }
    }

    private func validate() {
        isValid = usesGlobal || parameters.fullscreen || (GameLaunchDefaults.windowDimension(from: width) != nil && GameLaunchDefaults.windowDimension(from: height) != nil)
    }
    private func parameterField(_ title: String, text: Binding<String>) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.callout.weight(.medium))
            TextField(title, text: text, axis: .vertical).textFieldStyle(.roundedBorder).font(.system(.body, design: .monospaced)).lineLimit(2...3)
        }
    }
    private func dimension(_ title: String, text: Binding<String>) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.callout.weight(.medium))
            HStack { TextField(title, text: text).textFieldStyle(.roundedBorder); Text("px").foregroundStyle(.secondary) }
        }.accessibilityElement(children: .contain)
    }
}

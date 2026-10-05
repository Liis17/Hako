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
    @Binding var draft: InstanceDraft
    @Binding var isValid: Bool
    var manifest: MinecraftVersionManifest?
    @AppStorage(GameLaunchDefaults.Key.javaArguments) private var globalJava = ""
    @AppStorage(GameLaunchDefaults.Key.minecraftArguments) private var globalMinecraft = ""
    @AppStorage(GameLaunchDefaults.Key.javaPath) private var globalJavaPath = ""
    @AppStorage(GameLaunchDefaults.Key.maximumMemoryMiB) private var globalMemory = JavaMemoryPolicy.current.initialMiB
    @State private var width: String
    @State private var height: String

    init(draft: Binding<InstanceDraft>, isValid: Binding<Bool>, manifest: MinecraftVersionManifest? = nil) {
        _draft = draft; _isValid = isValid; self.manifest = manifest
        _width = State(initialValue: String(draft.wrappedValue.parameters.windowWidth))
        _height = State(initialValue: String(draft.wrappedValue.parameters.windowHeight))
    }

    private var usesGlobal: Bool { draft.argumentSource == .global }
    private var globalJavaArguments: String {
        guard let arguments = try? LaunchArguments.parse(globalJava) else { return globalJava }
        return LaunchArguments.format(LaunchArguments.applyingMemory(arguments, maximumMiB: JavaMemoryPolicy.current.normalize(globalMemory)))
    }
    private var mojangArguments: (java: String, minecraft: String) {
        var parameters = draft.parameters
        parameters.maximumMemoryMiB = JavaMemoryPolicy.current.normalize(parameters.maximumMemoryMiB)
        guard let manifest, let arguments = try? MinecraftLaunchPlan.argumentTemplates(manifest: manifest, source: .mojang, parameters: parameters) else { return ("", "") }
        return (LaunchArguments.format(arguments.java), LaunchArguments.format(arguments.minecraft))
    }
    private func source(_ source: LaunchArgumentSource) -> Binding<Bool> {
        Binding(get: { draft.argumentSource == source }, set: { enabled in
            if enabled { draft.argumentSource = source }
            else if draft.argumentSource == source { draft.argumentSource = .custom }
        })
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(alignment: .top, spacing: 20) {
                Toggle("Использовать глобальные параметры", isOn: source(.global))
                Toggle("Использовать параметры Mojang", isOn: source(.mojang))
            }.tint(.sakuraDeep)
            if usesGlobal {
                Text("Аргументы и память наследуются из текущих настроек лаунчера.").font(.caption).foregroundStyle(.secondary)
                if globalJava.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && globalMinecraft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    warning("Глобальные аргументы не заданы. Рекомендуется использовать параметры Mojang.")
                }
                if !globalJavaPath.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    warning("Эта сборка использует пользовательскую Java из глобальных настроек. Это может привести к проблемам в игре.")
                }
            } else if draft.argumentSource == .mojang {
                Text("Используются обязательные и рекомендуемые аргументы из описания версии Mojang.").font(.caption).foregroundStyle(.secondary)
                Text("Пути и значения аккаунта в ${…} будут подставлены при запуске.").font(.caption).foregroundStyle(.secondary)
            }
            VStack(alignment: .leading, spacing: 16) {
                parameterField("Аргументы Java", text: usesGlobal ? .constant(globalJavaArguments) : draft.argumentSource == .mojang ? .constant(mojangArguments.java) : $draft.parameters.javaArguments)
                parameterField("Аргументы Minecraft", text: usesGlobal ? .constant(globalMinecraft) : draft.argumentSource == .mojang ? .constant(mojangArguments.minecraft) : $draft.parameters.minecraftArguments)
            }.disabled(draft.argumentSource != .custom)
            JavaMemorySlider(value: usesGlobal ? .constant(JavaMemoryPolicy.current.normalize(globalMemory)) : $draft.parameters.maximumMemoryMiB, inherited: usesGlobal)
            Text("Значения -Xmx и MaxHeapSize в аргументах заменяются лимитом ползунка при запуске.").font(.caption).foregroundStyle(.secondary)
            Divider()
            Picker("Режим отображения", selection: $draft.parameters.fullscreen) {
                Text("Оконный").tag(false)
                Text("Полноэкранный").tag(true)
            }.pickerStyle(.segmented)
            HStack(spacing: 18) { dimension("Ширина", text: $width); dimension("Высота", text: $height) }.disabled(draft.parameters.fullscreen)
            if !draft.parameters.fullscreen && (GameLaunchDefaults.windowDimension(from: width) == nil || GameLaunchDefaults.windowDimension(from: height) == nil) {
                Text("Введите целые размеры окна больше 0.").font(.caption).foregroundStyle(Color.shu)
            }
            Divider()
            HStack(spacing: 20) {
                Toggle("offline-mode", isOn: $draft.offlineMode).tint(.sakuraDeep)
                if draft.offlineMode { TextField("Имя в игре", text: $draft.offlineUsername).textFieldStyle(.roundedBorder).accessibilityLabel("Имя в offline-mode") }
            }
            if draft.offlineMode {
                warning("Offline-mode не рекомендуется. Часть серверов может быть недоступна. Рекомендуется вход с аккаунтом Minecraft.")
                if !OfflineUsername.isValid(draft.offlineUsername) { Text("Ник: от 3 до 16 латинских букв, цифр или _.").font(.caption).foregroundStyle(Color.shu) }
            }
        }
        .onChange(of: width) { _, new in if let value = GameLaunchDefaults.windowDimension(from: new) { draft.parameters.windowWidth = value }; validate() }
        .onChange(of: height) { _, new in if let value = GameLaunchDefaults.windowDimension(from: new) { draft.parameters.windowHeight = value }; validate() }
        .onChange(of: draft.parameters) { validate() }
        .onChange(of: draft.offlineMode) { validate() }
        .onChange(of: draft.offlineUsername) { validate() }
        .onAppear { validate() }
    }

    private func validate() {
        isValid = (draft.parameters.fullscreen || (GameLaunchDefaults.windowDimension(from: width) != nil && GameLaunchDefaults.windowDimension(from: height) != nil)) && (!draft.offlineMode || OfflineUsername.isValid(draft.offlineUsername))
    }
    private func warning(_ text: String) -> some View { Label(text, systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(Color.shu).fixedSize(horizontal: false, vertical: true) }
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

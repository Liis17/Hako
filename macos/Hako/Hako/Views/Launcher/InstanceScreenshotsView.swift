import AppKit
import QuickLook
import SwiftUI

struct InstanceScreenshotsView: View {
    let instance: GameInstance
    @Environment(InstanceContentController.self) private var content
    @Environment(\.scenePhase) private var scenePhase
    @State private var screenshots: [InstanceScreenshot] = []
    @State private var selection: Set<URL> = []
    @State private var anchor: URL?
    @State private var preview: URL?
    @State private var pendingDeletion: [InstanceScreenshot]?
    @State private var deleting = false
    @State private var loading = true
    @State private var error: String?
    @State private var request = UUID()
    @FocusState private var focused: Bool

    private var gameBusy: Bool { content.installations.store.launchBusy.contains(instance.id) }
    private var selected: [InstanceScreenshot] { screenshots.filter { selection.contains($0.id) } }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack {
                Text("Скриншоты").font(.title3.weight(.semibold))
                if !screenshots.isEmpty { Text(screenshots.count, format: .number).font(.caption.monospacedDigit()).foregroundStyle(.secondary) }
                Spacer()
                if !selection.isEmpty {
                    Text("Выбрано: \(selection.count)").font(.callout.monospacedDigit()).foregroundStyle(.secondary)
                    Button("Удалить", systemImage: "trash") { pendingDeletion = selected }
                        .buttonStyle(.glass).disabled(deleting)
                }
                Button("Открыть папку", systemImage: "folder", action: openFolder).buttonStyle(.glass)
            }
            if let error {
                HStack(alignment: .top) {
                    Text(error).font(.callout).foregroundStyle(Color.shu).textSelection(.enabled)
                    Spacer()
                    Button("Повторить") { Task { await reload() } }.buttonStyle(.glass).disabled(loading)
                }
            }
            if loading && screenshots.isEmpty {
                ProgressView("Читаем скриншоты…").frame(maxWidth: .infinity).padding(.vertical, 32)
            } else if screenshots.isEmpty && error == nil {
                ContentUnavailableView("Скриншотов пока нет", systemImage: "camera", description: Text("Нажмите F2 в игре — снимок появится здесь."))
            } else if !screenshots.isEmpty {
                Text("Перетащите скриншоты в Finder, чтобы скопировать их. Двойной щелчок или пробел — просмотр.")
                    .font(.caption).foregroundStyle(.secondary)
                grid
            }
        }
        .instanceSurface()
        .contentShape(.rect)
        .onTapGesture { selection = []; anchor = nil }
        .task(id: "\(instance.folderName):\(instance.state.rawValue)") { await reload() }
        .onChange(of: scenePhase) { _, phase in if phase == .active { Task { await reload() } } }
        .onChange(of: gameBusy) { wasBusy, busy in if wasBusy && !busy { Task { await reload() } } }
        .onDisappear { request = UUID() }
        .alert((pendingDeletion?.count ?? 0) == 1 ? "Удалить скриншот?" : "Удалить скриншоты?", isPresented: Binding(get: { pendingDeletion != nil }, set: { if !$0 { pendingDeletion = nil } }), presenting: pendingDeletion) { items in
            Button("Удалить", role: .destructive) { delete(items) }
            Button("Отмена", role: .cancel) {}
        } message: { items in
            if items.count == 1, let item = items.first { Text("Скриншот «\(item.url.lastPathComponent)» будет перемещён в корзину.") }
            else { Text("Выбранные скриншоты (\(items.count)) будут перемещены в корзину.") }
        }
    }

    private var grid: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 200), spacing: 12)], spacing: 12) {
            ForEach(screenshots) { screenshot in
                ScreenshotCell(screenshot: screenshot, selected: selection.contains(screenshot.id))
                    .onTapGesture { click(screenshot) }
                    .contextMenu {
                        Button("Просмотр", systemImage: "eye") { preview = screenshot.url }
                        Button("Показать в Finder", systemImage: "folder") { NSWorkspace.shared.activateFileViewerSelecting(targets(screenshot).map(\.url)) }
                        Divider()
                        Button("Удалить…", systemImage: "trash", role: .destructive) { pendingDeletion = targets(screenshot) }.disabled(deleting)
                    }
                    .draggable(containerItemID: screenshot.id)
            }
        }
        // Перетаскивание выделенного скриншота уносит всё выделение, невыделенного — только его.
        .dragContainer(for: InstanceScreenshot.self) { ids in screenshots.filter { ids.contains($0.id) } }
        .dragContainerSelection(Array(selection))
        .dragConfiguration(DragConfiguration(operationsOutsideApp: .init(allowCopy: true, allowMove: false, allowDelete: false)))
        .focusable()
        .focusEffectDisabled()
        .focused($focused)
        .onKeyPress(.space) {
            if preview != nil { preview = nil; return .handled }
            guard let first = selected.first else { return .ignored }
            preview = first.url
            return .handled
        }
        .onDeleteCommand { if !selection.isEmpty && !deleting { pendingDeletion = selected } }
        .quickLookPreview($preview, in: screenshots.map(\.url))
    }

    /// Контекстное меню действует на всё выделение, если скриншот в нём.
    private func targets(_ screenshot: InstanceScreenshot) -> [InstanceScreenshot] {
        selection.contains(screenshot.id) ? selected : [screenshot]
    }

    private func click(_ screenshot: InstanceScreenshot) {
        focused = true
        let flags = NSEvent.modifierFlags
        if flags.contains(.shift), let anchor, let from = screenshots.firstIndex(where: { $0.id == anchor }), let to = screenshots.firstIndex(where: { $0.id == screenshot.id }) {
            selection.formUnion(screenshots[min(from, to)...max(from, to)].map(\.id))
        } else if flags.contains(.command) {
            if selection.remove(screenshot.id) == nil { selection.insert(screenshot.id) }
            anchor = screenshot.id
        } else {
            selection = [screenshot.id]
            anchor = screenshot.id
            // Двойной щелчок определяется по событию, чтобы одиночный щелчок не ждал второго.
            if (NSApp.currentEvent?.clickCount ?? 1) >= 2 { preview = screenshot.url }
        }
    }

    private func openFolder() {
        do {
            let folder = try InstanceScreenshots.folder(in: content.installations.store.storage.directory(instance.folderName))
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            NSWorkspace.shared.open(folder)
        } catch { self.error = error.localizedDescription }
    }

    private func delete(_ items: [InstanceScreenshot]) {
        deleting = true
        Task {
            defer { deleting = false }
            do { try await content.screenshots.trash(items, in: content.installations.store.storage.directory(instance.folderName)) }
            catch { self.error = error.localizedDescription }
            selection.subtract(items.map(\.id))
            await reload(clearError: false)
        }
    }

    private func reload(clearError: Bool = true) async {
        let current = UUID(), folderName = instance.folderName
        request = current; loading = true
        if clearError { error = nil }
        do {
            let root = try content.installations.store.storage.directory(folderName)
            let result = try await content.screenshots.list(in: root)
            guard !Task.isCancelled, request == current, instance.folderName == folderName else { return }
            screenshots = result
            let ids = Set(result.map(\.id))
            selection.formIntersection(ids)
            if let anchor, !ids.contains(anchor) { self.anchor = nil }
            if let preview, !ids.contains(preview) { self.preview = nil }
        } catch {
            guard !Task.isCancelled, request == current, instance.folderName == folderName else { return }
            self.error = error.localizedDescription
        }
        loading = false
    }
}

private struct ScreenshotCell: View {
    let screenshot: InstanceScreenshot
    let selected: Bool
    @Environment(InstanceContentController.self) private var content
    @Environment(\.locale) private var locale
    @State private var image: NSImage?

    var body: some View {
        Color.clear
            .aspectRatio(16 / 9, contentMode: .fit)
            .overlay {
                if let image { Image(nsImage: image).resizable().scaledToFill() }
                else { Image(systemName: "photo").font(.title2).foregroundStyle(Color.sakuraDeep) }
            }
            .background(.white.opacity(0.35))
            .clipShape(.rect(cornerRadius: 12))
            .overlay { RoundedRectangle(cornerRadius: 12).strokeBorder(selected ? Color.sakuraDeep : .white.opacity(0.5), lineWidth: selected ? 3 : 1) }
            .overlay(alignment: .topTrailing) {
                if selected { Image(systemName: "checkmark.circle.fill").font(.title3).foregroundStyle(.white, Color.sakuraDeep).padding(8) }
            }
            .contentShape(.rect(cornerRadius: 12))
            .help(Text(verbatim: screenshot.url.lastPathComponent))
            .accessibilityElement()
            .accessibilityLabel(Text(verbatim: screenshot.url.lastPathComponent))
            .accessibilityValue(Text(verbatim: screenshot.created?.formatted(.dateTime.day().month().year().hour().minute().locale(locale)) ?? ""))
            .accessibilityAddTraits(selected ? .isSelected : [])
            .task(id: screenshot) {
                image = nil
                let thumbnail = await content.screenshots.thumbnail(screenshot.url, maxPixelSize: 512)
                if let thumbnail, !Task.isCancelled { image = NSImage(cgImage: thumbnail, size: .zero) }
            }
    }
}

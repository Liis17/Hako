//
//  MinecraftSkinView.swift
//  Hako
//

import RealityKit
import SwiftUI

/// Персонаж в полный рост на прозрачном фоне: ходьба на месте и осмотр мышью.
struct MinecraftSkinView: View {
    var source = MinecraftSkinSource(uuid: nil, skinURL: nil, variant: nil)
    var previewSkin: MinecraftSkin? = nil
    var previewRevision = 0
    var isLocalPreview = false
    var previewError: String? = nil
    var onReadinessChange: ((Bool, Int) -> Void)? = nil
    var onPreviewFailure: ((String, Int) -> Void)? = nil

    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.scenePhase) private var scenePhase
    @Environment(MinecraftSkinLoader.self) private var skinLoader
    @State private var scene = MinecraftSkinScene()
    @State private var subscription: EventSubscription?
    @State private var rotation = SIMD2<Float>(30, 21)
    @State private var dragStart: SIMD2<Float>?
    @State private var renderError: String?
    @State private var renderGeneration = UUID()

    private struct PreviewRequest: Hashable {
        let source: MinecraftSkinSource
        let revision: Int
    }

    var body: some View {
        GeometryReader { geometry in
            RealityView { content in
                content.camera = .virtual
                content.environment = .default
                content.renderingEffects.motionBlur = .disabled
                content.add(scene.root)
                content.add(scene.camera)
                scene.fit(in: geometry.size)
                subscription = content.subscribe(to: SceneEvents.Update.self) { event in
                    scene.update(deltaTime: event.deltaTime)
                }
            } update: { _ in
                scene.rotate(yaw: rotation.x, pitch: rotation.y)
                scene.fit(in: geometry.size)
                scene.isWalking = scenePhase == .active && !reduceMotion
                if reduceMotion { scene.rest() }
            }
            .background(Color.clear)
            .contentShape(.rect)
            .gesture(
                DragGesture()
                    .onChanged { value in
                        let start = dragStart ?? rotation
                        dragStart = start
                        rotation = .init(
                            start.x + Float(value.translation.width / max(geometry.size.width, 1)) * 360,
                            min(45, max(-45, start.y + Float(value.translation.height) * 0.3))
                        )
                    }
                    .onEnded { _ in dragStart = nil }
            )
            .accessibilityElement()
            .accessibilityLabel("3D-модель скина Minecraft")
            .accessibilityHint("Перетащите, чтобы повернуть персонажа")
            .accessibilityAdjustableAction { direction in
                rotation.x += direction == .increment ? 15 : -15
            }
            .overlay {
                if let message = previewError ?? renderError {
                    Label(message, systemImage: "exclamationmark.triangle.fill")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .padding(20)
                        .frame(maxWidth: 340)
                        .background(.regularMaterial, in: .rect(cornerRadius: 16))
                }
            }
            .task(id: PreviewRequest(source: source, revision: previewRevision)) {
                let generation = UUID()
                let requestedRevision = previewRevision
                renderGeneration = generation
                renderError = nil
                onReadinessChange?(false, requestedRevision)
                do {
                    if isLocalPreview {
                        guard let previewSkin else { return }
                        try await scene.display(previewSkin)
                    } else {
                        try await scene.display(MinecraftSkin.steve())
                        if let skin = try await skinLoader.load(source) {
                            try await scene.display(skin)
                        }
                    }
                    try Task.checkCancellation()
                    guard generation == renderGeneration else { return }
                    onReadinessChange?(true, requestedRevision)
                } catch {
                    guard !Task.isCancelled, generation == renderGeneration else { return }
                    renderError = error.localizedDescription
                    onReadinessChange?(false, requestedRevision)
                    onPreviewFailure?(error.localizedDescription, requestedRevision)
                }
            }
            .onDisappear {
                subscription?.cancel()
                subscription = nil
                scene.isWalking = false
            }
        }
    }
}

#Preview("Steve") {
    MinecraftSkinView()
        .environment(MinecraftSkinLoader.shared)
        .frame(width: 400, height: 600)
        .background { SakuraBackground() }
}

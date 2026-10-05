//
//  MinecraftSkinView.swift
//  Hako
//

import RealityKit
import SwiftUI

/// Персонаж в полный рост на прозрачном фоне: ходьба на месте и осмотр мышью.
struct MinecraftSkinView: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.scenePhase) private var scenePhase
    @State private var scene = MinecraftSkinScene()
    @State private var subscription: EventSubscription?
    @State private var rotation = SIMD2<Float>(30, 21)
    @State private var dragStart: SIMD2<Float>?

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
            .task {
                do { try await scene.display(MinecraftSkin.steve()) }
                catch { /* Встроенная текстура проверяется при сборке и в тестах. */ }
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
        .frame(width: 400, height: 600)
        .background { SakuraBackground() }
}

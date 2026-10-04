//
//  SakuraBackground.swift
//  Hako
//

import SwiftUI

/// Белый фон с сильно размытыми пятнами цвета сакуры, медленно дрейфующими по краям окна.
struct SakuraBackground: View {
    private struct Blob {
        let color: Color
        let diameter: CGFloat
        /// Позиция центра в долях размера окна.
        let position: UnitPoint
        let drift: CGSize
    }

    private let blobs = [
        Blob(color: Color(hex: 0xFFB7C5), diameter: 640, position: UnitPoint(x: 0.05, y: 0.1), drift: CGSize(width: 80, height: 50)),
        Blob(color: Color(hex: 0xE8B4C8), diameter: 560, position: UnitPoint(x: 0.95, y: 0.2), drift: CGSize(width: -70, height: 60)),
        Blob(color: Color(hex: 0xF8C8D4), diameter: 700, position: UnitPoint(x: 0.8, y: 0.95), drift: CGSize(width: -60, height: -50)),
        Blob(color: Color(hex: 0xFADADD), diameter: 520, position: UnitPoint(x: 0.2, y: 0.9), drift: CGSize(width: 70, height: -40)),
    ]

    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var isDrifting = false

    var body: some View {
        GeometryReader { proxy in
            ZStack {
                Color.white
                ForEach(blobs.indices, id: \.self) { index in
                    let blob = blobs[index]
                    Circle()
                        .fill(blob.color)
                        .frame(width: blob.diameter, height: blob.diameter)
                        .position(
                            x: proxy.size.width * blob.position.x + (isDrifting ? blob.drift.width : 0),
                            y: proxy.size.height * blob.position.y + (isDrifting ? blob.drift.height : 0)
                        )
                }
                .opacity(0.75)
                .blur(radius: 140)
            }
        }
        .ignoresSafeArea()
        .onAppear {
            guard !reduceMotion else { return }
            withAnimation(.easeInOut(duration: 20).repeatForever(autoreverses: true)) {
                isDrifting = true
            }
        }
    }
}

#Preview {
    SakuraBackground()
        .frame(width: 1280, height: 720)
}

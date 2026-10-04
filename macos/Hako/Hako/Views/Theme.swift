//
//  Theme.swift
//  Hako
//

import SwiftUI

extension Color {
    init(hex: UInt32) {
        self.init(
            red: Double((hex >> 16) & 0xFF) / 255,
            green: Double((hex >> 8) & 0xFF) / 255,
            blue: Double(hex & 0xFF) / 255
        )
    }

    static let sakuraDeep = Color(hex: 0xE0607E)
    static let shu = Color(hex: 0xD9433B)
}

extension Text {
    /// Крупный жирный заголовок экрана.
    func heroTitle() -> Text {
        font(.system(size: 72, weight: .heavy)).tracking(-1.5)
    }
}

/// Декоративная японская подпись над заголовком.
struct JapaneseCaption: View {
    private let text: String

    init(_ text: String) {
        self.text = text
    }

    var body: some View {
        Text(verbatim: text)
            .font(.custom("HiraMinProN-W3", size: 20))
            .tracking(8)
            .foregroundStyle(Color.sakuraDeep)
    }
}

/// Печать-ханко с иероглифом 箱 («хако» — коробка).
struct HankoSeal: View {
    var size: CGFloat = 56

    var body: some View {
        Text(verbatim: "箱")
            .font(.custom("HiraMinProN-W6", size: size * 0.62))
            .foregroundStyle(.white)
            .frame(width: size, height: size)
            .background(Color.shu, in: .rect(cornerRadius: size * 0.22))
            .rotationEffect(.degrees(-4))
    }
}

private struct Reveal: ViewModifier {
    let isVisible: Bool
    let order: Int
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    func body(content: Content) -> some View {
        content
            .opacity(isVisible ? 1 : 0)
            .offset(y: isVisible || reduceMotion ? 0 : 16)
            .animation(.smooth(duration: 0.8).delay(Double(order) * 0.08), value: isVisible)
    }
}

extension View {
    /// Поочерёдное появление элементов экрана: fade и сдвиг вверх.
    func reveal(_ isVisible: Bool, order: Int) -> some View {
        modifier(Reveal(isVisible: isVisible, order: order))
    }
}

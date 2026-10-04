//
//  WelcomeView.swift
//  Hako
//

import SwiftUI

/// Экран приветствия, который видит пользователь без сохранённого аккаунта.
struct WelcomeView: View {
    let onStart: () -> Void

    @State private var isVisible = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HankoSeal()
                .reveal(isVisible, order: 0)

            JapaneseCaption("ようこそ")
                .padding(.top, 40)
                .reveal(isVisible, order: 1)

            Text("Добро пожаловать\nв Hako")
                .heroTitle()
                .padding(.top, 12)
                .reveal(isVisible, order: 2)

            Text("Лаунчер Minecraft для macOS")
                .font(.title2)
                .foregroundStyle(.secondary)
                .padding(.top, 16)
                .reveal(isVisible, order: 3)

            Button(action: onStart) {
                Text("Начать")
                    .padding(.horizontal, 12)
            }
            .buttonStyle(.glassProminent)
            .tint(.sakuraDeep)
            .controlSize(.extraLarge)
            .keyboardShortcut(.defaultAction)
            .padding(.top, 48)
            .reveal(isVisible, order: 4)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
        .padding(.horizontal, 96)
        .onAppear { isVisible = true }
    }
}

#Preview {
    WelcomeView(onStart: {})
        .background { SakuraBackground() }
        .frame(width: 1280, height: 720)
}

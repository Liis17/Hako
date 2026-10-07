//
//  WelcomeView.swift
//  Hako
//

import SwiftUI

/// Экран приветствия, который видит пользователь без сохранённого аккаунта.
struct WelcomeView: View {
    let onStart: () -> Void
    var onContinueWithoutAccount: () -> Void = {}

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

            HStack(spacing: 18) {
                Button(action: onStart) {
                    Text("Начать").padding(.horizontal, 12)
                }
                .buttonStyle(.glassProminent).tint(.sakuraDeep)
                .keyboardShortcut(.defaultAction)
                Button("Продолжить без аккаунта", action: onContinueWithoutAccount)
                    .buttonStyle(.glass)
            }
            .controlSize(.extraLarge)
            .padding(.top, 48)
            .reveal(isVisible, order: 4)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
        .padding(.horizontal, 96)
        .overlay(alignment: .topTrailing) {
            AppLanguagePicker()
                .padding(.top, 20)
                .padding(.trailing, 24)
                .reveal(isVisible, order: 5)
        }
        .overlay(alignment: .bottomTrailing) {
            Text("Hako is not affiliated with or endorsed by Mojang or Microsoft. Minecraft is a trademark of Microsoft.")
                .font(.caption)
                .foregroundStyle(.tertiary)
                .padding(.trailing, 24)
                .padding(.bottom, 20)
                .reveal(isVisible, order: 5)
        }
        .onAppear { isVisible = true }
    }
}

#Preview {
    WelcomeView(onStart: {})
        .background { SakuraBackground() }
        .frame(width: 1280, height: 720)
}

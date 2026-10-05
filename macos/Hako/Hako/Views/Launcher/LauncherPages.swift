//
//  LauncherPages.swift
//  Hako
//

import SwiftUI

/// Каркас вкладки лаунчера: японская подпись, крупный заголовок и содержимое с появлением через `reveal`.
struct LauncherPage<Content: View>: View {
    let caption: String
    let title: LocalizedStringKey
    @ViewBuilder var content: Content

    @State private var isVisible = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            JapaneseCaption(caption)
                .reveal(isVisible, order: 0)

            Text(title)
                .heroTitle()
                .padding(.top, 12)
                .reveal(isVisible, order: 1)

            content
                .padding(.top, 32)
                .reveal(isVisible, order: 2)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .onAppear { isVisible = true }
    }
}

/// Сборки пока не реализованы — только заглушка.
struct InstancesView: View {
    var body: some View {
        LauncherPage(caption: "パック", title: "Сборки") {
            Text("Сборок пока нет. Создание сборок появится в следующих версиях.")
                .font(.title2)
                .foregroundStyle(.secondary)
        }
    }
}

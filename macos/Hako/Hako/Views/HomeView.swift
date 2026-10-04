//
//  HomeView.swift
//  Hako
//

import SwiftData
import SwiftUI

/// Главное окно вошедшего пользователя: пока только ник и выход.
struct HomeView: View {
    let account: Account

    @Environment(\.modelContext) private var modelContext
    @State private var isVisible = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HankoSeal()
                .reveal(isVisible, order: 0)

            JapaneseCaption("おかえり")
                .padding(.top, 40)
                .reveal(isVisible, order: 1)

            Text("Привет, \(account.name)")
                .heroTitle()
                .padding(.top, 12)
                .reveal(isVisible, order: 2)

            Button("Выйти", action: signOut)
                .buttonStyle(.glass)
                .controlSize(.extraLarge)
                .padding(.top, 48)
                .reveal(isVisible, order: 3)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
        .padding(.horizontal, 96)
        .onAppear { isVisible = true }
    }

    private func signOut() {
        TokenKeychain.delete(for: account.uuid)
        modelContext.delete(account)
        try? modelContext.save()
    }
}

#Preview {
    let container = try! ModelContainer(for: Account.self, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
    let account = Account(uuid: "preview", name: "Steve")
    container.mainContext.insert(account)

    return HomeView(account: account)
        .modelContainer(container)
        .background { SakuraBackground() }
        .frame(width: 1280, height: 720)
}

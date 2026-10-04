//
//  ContentView.swift
//  Hako
//
//  Created by Li_is on 04/10/2026.
//

import SwiftData
import SwiftUI

/// Корень окна: экран выбирается по наличию сохранённого аккаунта.
struct ContentView: View {
    @Query private var accounts: [Account]
    @State private var isSigningIn = false

    var body: some View {
        ZStack {
            SakuraBackground()

            if let account = accounts.first {
                LauncherView(account: account)
                    .transition(.blurReplace)
            } else if isSigningIn {
                LoginView(onBack: { isSigningIn = false })
                    .transition(.blurReplace)
            } else {
                WelcomeView(onStart: { isSigningIn = true })
                    .transition(.blurReplace)
            }
        }
        .animation(.smooth, value: isSigningIn)
        .animation(.smooth, value: accounts.isEmpty)
        // После входа и после выхода следующий экран без аккаунта — приветствие.
        .onChange(of: accounts.isEmpty) { isSigningIn = false }
        .frame(minWidth: 960, minHeight: 540)
        .preferredColorScheme(.light)
    }
}

#Preview {
    ContentView()
        .modelContainer(for: Account.self, inMemory: true)
        .frame(width: 1280, height: 720)
}

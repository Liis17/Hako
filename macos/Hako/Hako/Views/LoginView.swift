//
//  LoginView.swift
//  Hako
//

import AppKit
import SwiftData
import SwiftUI

/// Вход в Microsoft по коду: пользователь вводит код на microsoft.com/link,
/// приложение опрашивает Microsoft и проходит цепочку входа в Minecraft.
struct LoginView: View {
    let onBack: () -> Void

    private enum Phase {
        case requestingCode
        case waiting(DeviceCode)
        case signingIn
        case failed(String)
    }

    @Environment(\.modelContext) private var modelContext
    @Environment(\.openURL) private var openURL
    @Environment(PlaytimeCoordinator.self) private var playtime
    @State private var phase = Phase.requestingCode
    @State private var attempt = 0
    @State private var isCopied = false
    @State private var isVisible = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Button("Назад", systemImage: "chevron.left", action: onBack)
                .buttonStyle(.glass)
                .padding(.bottom, 32)
                .reveal(isVisible, order: 0)

            JapaneseCaption("サインイン")
                .reveal(isVisible, order: 1)

            Text("Вход в Microsoft")
                .heroTitle()
                .padding(.top, 12)
                .reveal(isVisible, order: 2)

            Text("Откройте microsoft.com/link и введите код.")
                .font(.title2)
                .foregroundStyle(.secondary)
                .padding(.top, 16)
                .reveal(isVisible, order: 3)

            content
                .padding(.top, 40)
                .reveal(isVisible, order: 4)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
        .padding(.horizontal, 96)
        .onAppear { isVisible = true }
        .task(id: attempt) { await signIn() }
    }

    @ViewBuilder
    private var content: some View {
        switch phase {
        case .requestingCode:
            status("Получаем код…")
        case .waiting(let code):
            VStack(alignment: .leading, spacing: 24) {
                Text(verbatim: code.userCode)
                    .font(.system(size: 56, weight: .bold, design: .monospaced))
                    .tracking(6)
                    .textSelection(.enabled)
                    .padding(.horizontal, 40)
                    .padding(.vertical, 24)
                    .glassEffect(.regular, in: .rect(cornerRadius: 28))

                HStack(spacing: 12) {
                    Button("Открыть microsoft.com/link") { openURL(code.verificationUri) }
                        .buttonStyle(.glassProminent)
                        .tint(.sakuraDeep)
                        .keyboardShortcut(.defaultAction)
                    Button(
                        isCopied ? "Скопировано" : "Скопировать код",
                        systemImage: isCopied ? "checkmark" : "doc.on.doc"
                    ) {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(code.userCode, forType: .string)
                        isCopied = true
                    }
                    .buttonStyle(.glass)
                }
                .controlSize(.extraLarge)

                status("Ждём подтверждения…")
            }
        case .signingIn:
            status("Входим в Xbox Live и Minecraft…")
        case .failed(let message):
            VStack(alignment: .leading, spacing: 20) {
                Label(message, systemImage: "exclamationmark.triangle.fill")
                    .font(.title3)
                    .foregroundStyle(Color.shu)
                    .textSelection(.enabled)
                    .frame(maxWidth: 720, alignment: .leading)
                Button("Попробовать снова") { attempt += 1 }
                    .buttonStyle(.glassProminent)
                    .tint(.sakuraDeep)
                    .controlSize(.extraLarge)
                    .keyboardShortcut(.defaultAction)
            }
        }
    }

    private func status(_ text: LocalizedStringKey) -> some View {
        HStack(spacing: 10) {
            ProgressView()
                .controlSize(.small)
            Text(text)
                .foregroundStyle(.secondary)
        }
        .font(.title3)
    }

    private func signIn() async {
        phase = .requestingCode
        isCopied = false
        do {
            let code = try await MicrosoftAuth.requestDeviceCode()
            phase = .waiting(code)
            let token = try await MicrosoftAuth.waitForToken(code)
            phase = .signingIn
            let (xbox, minecraft) = try await MicrosoftAuth.signIn(with: token)
            try TokenKeychain.save(
                AccountTokens(
                    microsoftRefreshToken: token.refreshToken,
                    minecraftAccessToken: minecraft?.accessToken,
                    minecraftTokenExpiration: minecraft?.expiration
                ),
                for: xbox.xuid
            )
            try playtime.reconcile()
            let account = Account(xbox: xbox, email: token.email)
            if let minecraft {
                account.connect(minecraft.profile)
            }
            modelContext.insert(account)
            do { try playtime.transferGuest(to: account.xuid) }
            catch { modelContext.delete(account); throw error }
        } catch {
            // Отмена — экран закрыт кнопкой «Назад»; показывать нечего.
            guard !Task.isCancelled else { return }
            phase = .failed(error.localizedDescription)
        }
    }
}

#Preview {
    let container = try! ModelContainer(for: HakoSchema.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
    return LoginView(onBack: {})
        .modelContainer(container)
        .environment(try! PlaytimeCoordinator(context: container.mainContext))
        .background { SakuraBackground() }
        .frame(width: 1280, height: 720)
}

import AppKit
import SwiftUI

struct HakoLoginView: View {
    let onBack: () -> Void

    @Environment(HakoAccountCoordinator.self) private var hakoAccount
    @Environment(\.openURL) private var openURL
    @State private var username = ""
    @State private var isCopied = false
    @State private var isVisible = false

    private var isWorking: Bool {
        hakoAccount.attemptPhase == .requesting || hakoAccount.attemptPhase == .pending || hakoAccount.attemptPhase == .exchanging
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Button("Назад", systemImage: "chevron.left", action: leave)
                .buttonStyle(.glass)
                .padding(.bottom, 32)
                .reveal(isVisible, order: 0)

            JapaneseCaption("テレグラム")
                .reveal(isVisible, order: 1)

            Text("Вход через Telegram")
                .heroTitle()
                .padding(.top, 12)
                .reveal(isVisible, order: 2)

            Text("Войдите по Telegram username или создайте аккаунт Hako через бота.")
                .font(.title2)
                .foregroundStyle(.secondary)
                .padding(.top, 16)
                .reveal(isVisible, order: 3)

            if isWorking {
                activeRequest
                    .padding(.top, 40)
                    .reveal(isVisible, order: 4)
            } else {
                entryForm
                    .padding(.top, 36)
                    .reveal(isVisible, order: 4)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
        .padding(.horizontal, 96)
        .onAppear { isVisible = true }
        .onDisappear {
            if isWorking { hakoAccount.cancelAttempt() }
        }
    }

    private var entryForm: some View {
        VStack(alignment: .leading, spacing: 20) {
            if let error = hakoAccount.attemptError {
                Label(error.localizedDescription, systemImage: "exclamationmark.triangle.fill")
                    .font(.title3)
                    .foregroundStyle(Color.shu)
                    .textSelection(.enabled)
                    .frame(maxWidth: 720, alignment: .leading)
            }

            if hakoAccount.requiresFreshLogin {
                Text("Ответ на обмен потерян или запрос уже использован. Начните новый вход по Telegram username.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: 620, alignment: .leading)
            }

            VStack(alignment: .leading, spacing: 12) {
                Text("Telegram username")
                    .font(.headline)
                TextField("Например, Player_123", text: $username)
                    .textFieldStyle(.roundedBorder)
                    .frame(maxWidth: 460)
                    .autocorrectionDisabled()
                    .textContentType(.username)
                    .onSubmit { startLogin() }
                HStack(spacing: 12) {
                    Button("Войти") { startLogin() }
                        .buttonStyle(.glassProminent)
                        .tint(.sakuraDeep)
                        .keyboardShortcut(.defaultAction)
                    Button("Создать аккаунт") { hakoAccount.beginRegistration() }
                        .buttonStyle(.glass)
                }
                .controlSize(.extraLarge)
                Text("Для регистрации username не нужен. Подтверждение выполняется в Telegram.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }

            if hakoAccount.attemptPhase == .failed,
               !hakoAccount.requiresFreshLogin,
               hakoAccount.attemptError != .accountExists {
                Button("Попробовать снова") { retry() }
                    .buttonStyle(.glass)
                    .controlSize(.large)
            }
        }
    }

    private var activeRequest: some View {
        VStack(alignment: .leading, spacing: 22) {
            HStack(spacing: 12) {
                ProgressView().controlSize(.small)
                Text(statusText).font(.title3).foregroundStyle(.secondary)
            }

            if hakoAccount.attemptPhase == .pending {
                TimelineView(.periodic(from: .now, by: 1)) { _ in
                    Text(String(format: String(appLocalized: "Запрос истекает через %@"), Self.formatRemaining(hakoAccount.pendingSecondsRemaining)))
                        .font(.callout.monospacedDigit())
                        .foregroundStyle(.secondary)
                }

                if let link = hakoAccount.botLink {
                    VStack(alignment: .leading, spacing: 14) {
                        Text("Откройте бота и отправьте команду /start.")
                            .foregroundStyle(.secondary)
                        Text(verbatim: link.absoluteString)
                            .font(.callout.monospaced())
                            .textSelection(.enabled)
                            .lineLimit(2)
                            .frame(maxWidth: 650, alignment: .leading)
                        HStack(spacing: 12) {
                            Button("Открыть Telegram") { openURL(link) }
                                .buttonStyle(.glassProminent)
                                .tint(.sakuraDeep)
                                .keyboardShortcut(.defaultAction)
                            Button(isCopied ? "Скопировано" : "Скопировать ссылку", systemImage: isCopied ? "checkmark" : "doc.on.doc") {
                                NSPasteboard.general.clearContents()
                                NSPasteboard.general.setString(link.absoluteString, forType: .string)
                                isCopied = true
                            }
                            .buttonStyle(.glass)
                        }
                        .controlSize(.extraLarge)
                    }
                } else {
                    Text("Откройте чат с ботом Hako в Telegram и подтвердите запрос.")
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: 650, alignment: .leading)
                }
            }

            Button("Отмена", action: leave)
                .buttonStyle(.glass)
                .controlSize(.large)
                .keyboardShortcut(.cancelAction)
        }
    }

    private var statusText: LocalizedStringKey {
        switch hakoAccount.attemptPhase {
        case .requesting: "Создаём запрос…"
        case .pending: "Ожидаем подтверждение в Telegram…"
        case .exchanging: "Сохраняем сессию Hako…"
        case .idle, .failed: "Вход в Hako"
        }
    }

    private func startLogin() {
        isCopied = false
        hakoAccount.beginLogin(username: username)
    }

    private func retry() {
        isCopied = false
        if hakoAccount.authorizationMode == .register { hakoAccount.beginRegistration() }
        else { startLogin() }
    }

    private func leave() {
        hakoAccount.cancelAttempt()
        onBack()
    }

    private static func formatRemaining(_ seconds: Int) -> String {
        let minutes = seconds / 60
        let remainder = seconds % 60
        return String(format: "%02d:%02d", minutes, remainder)
    }
}

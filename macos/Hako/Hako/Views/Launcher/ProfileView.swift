//
//  ProfileView.swift
//  Hako
//

import SwiftData
import SwiftUI

/// Вкладка профиля: аккаунт Xbox, аккаунт Minecraft: Java Edition и выход.
struct ProfileView: View {
    let account: Account
    let minecraftStatus: MinecraftStatus

    @Environment(\.modelContext) private var modelContext
    @Environment(MinecraftSessionCoordinator.self) private var sessions
    @State private var isConfirmingSignOut = false

    var body: some View {
        HStack(alignment: .top, spacing: 32) {
            accountDetails
                .frame(minWidth: 400, maxWidth: 600)

            MinecraftSkinView(source: MinecraftSkinSource(
                uuid: account.minecraftUUID,
                skinURL: account.minecraftSkinURL,
                variant: account.minecraftSkinVariant.flatMap(MinecraftSkinVariant.init(rawValue:))
            ))
            .frame(minWidth: 240, maxWidth: .infinity, maxHeight: .infinity)
            .padding(.vertical, 16)
        }
    }

    private var accountDetails: some View {
        LauncherPage(caption: "プロフィール", title: "Профиль") {
            VStack(alignment: .leading, spacing: 12) {
                AccountRow(title: account.gamertag, subtitle: account.email, badge: "Xbox") {
                    XboxAvatar(url: account.xboxAvatarURL, name: account.gamertag)
                        .clipShape(.circle)
                }

                AccountRow(
                    title: account.minecraftName ?? "Minecraft не подключён",
                    subtitle: minecraftSubtitle,
                    badge: "Java Edition"
                ) {
                    if let skinURL = account.minecraftSkinURL {
                        MinecraftHead(skinURL: skinURL)
                            .clipShape(.rect(cornerRadius: 10))
                    } else {
                        Image(systemName: "cube.transparent")
                            .font(.system(size: 22))
                            .foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                            .background(.quaternary, in: .rect(cornerRadius: 10))
                    }
                }

                if sessions.identity(for: account) == nil || minecraftSubtitle != nil {
                    Button("Повторить подключение Minecraft") { Task { await sessions.connect(account, force: true) } }
                        .buttonStyle(.glass).disabled(isConnecting)
                }

                Button("Выйти") { isConfirmingSignOut = true }
                    .buttonStyle(.glass)
                    .controlSize(.extraLarge)
                    .padding(.top, 20)
                    .alert("Выйти из аккаунта?", isPresented: $isConfirmingSignOut) {
                        Button("Выйти", role: .destructive, action: signOut)
                        Button("Отмена", role: .cancel) {}
                    } message: {
                        Text("Данные входа будут удалены с этого Mac. Чтобы вернуться, понадобится снова войти по коду.")
                    }
            }
            .frame(maxWidth: 600, alignment: .leading)
        }
    }

    private var minecraftStatusText: String {
        switch minecraftStatus {
        case .idle: "Minecraft: Java Edition недоступен для этого аккаунта."
        case .connecting: "Подключаем Minecraft…"
        case .failed(let message): message
        }
    }

    private var isConnecting: Bool { if case .connecting = minecraftStatus { true } else { false } }
    private var minecraftSubtitle: String? {
        if case .idle = minecraftStatus { return account.minecraftName == nil ? minecraftStatusText : nil }
        return minecraftStatusText
    }

    private func signOut() {
        sessions.signOut()
        TokenKeychain.delete(for: account.xuid)
        modelContext.delete(account)
        try? modelContext.save()
    }
}

struct GuestProfileView: View {
    let onSignIn: () -> Void
    var body: some View {
        LauncherPage(caption: "プロフィール", title: "Профиль") {
            VStack(alignment: .leading, spacing: 20) {
                Label("Вы продолжили без аккаунта", systemImage: "person.crop.circle").font(.title3.weight(.semibold))
                Text("Для онлайн-запуска подключите аккаунт Microsoft с Minecraft: Java Edition. Offline-mode включается отдельно в настройках каждой сборки.")
                    .foregroundStyle(.secondary)
                Button("Войти в Microsoft", action: onSignIn).buttonStyle(.glassProminent).tint(.sakuraDeep).controlSize(.large)
            }.frame(maxWidth: 600, alignment: .leading).instanceSurface()
        }
    }
}

private struct AccountRow<Avatar: View>: View {
    let title: String
    let subtitle: String?
    let badge: String
    @ViewBuilder var avatar: Avatar

    var body: some View {
        HStack(spacing: 16) {
            avatar
                .frame(width: 56, height: 56)

            VStack(alignment: .leading, spacing: 4) {
                Text(title)
                    .font(.title3.weight(.semibold))
                if let subtitle {
                    Text(subtitle)
                        .foregroundStyle(.secondary)
                        .lineLimit(2)
                        .textSelection(.enabled)
                }
            }

            Spacer(minLength: 16)

            Text(badge)
                .font(.callout.weight(.medium))
                .foregroundStyle(.secondary)
        }
        .padding(16)
        .glassEffect(.regular, in: .rect(cornerRadius: 22))
    }
}

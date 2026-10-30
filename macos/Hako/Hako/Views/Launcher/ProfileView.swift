import SwiftData
import SwiftUI

/// Microsoft/Minecraft profile details remain tied to the Xbox account. Hako is an independent provider card.
struct ProfileView: View {
    let account: Account
    let minecraftStatus: MinecraftStatus
    var onEditCharacter: () -> Void = {}
    var onHakoSignIn: () -> Void = {}

    @Environment(\.modelContext) private var modelContext
    @Environment(MinecraftSessionCoordinator.self) private var sessions
    @Environment(MinecraftAppearanceStore.self) private var appearances
    @Environment(PlaytimeCoordinator.self) private var playtime
    @State private var isConfirmingSignOut = false
    @State private var signOutError: String?

    var body: some View {
        HStack(alignment: .top, spacing: 32) {
            ScrollView { accountDetails }
                .frame(minWidth: 400, maxWidth: 600)

            VStack(spacing: 16) {
                MinecraftSkinView(source: minecraftSkinSource)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                if canEditCharacter {
                    Button(action: onEditCharacter) {
                        Label("Изменить персонажа", systemImage: "person.crop.square")
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .buttonStyle(.glass)
                    .controlSize(.large)
                }
            }
            .frame(minWidth: 240, maxWidth: .infinity, maxHeight: .infinity)
            .padding(.vertical, 16)
        }
        .alert("Не удалось разделить игровую сессию", isPresented: Binding(
            get: { signOutError != nil },
            set: { if !$0 { signOutError = nil } }
        )) {
            Button("ОК", role: .cancel) { signOutError = nil }
        } message: {
            Text(signOutError ?? "")
        }
    }

    private var minecraftSkinSource: MinecraftSkinSource {
        let profile = appearances.profile(for: account.xuid)
        return MinecraftSkinSource(
            uuid: account.minecraftUUID,
            skinURL: profile == nil ? account.minecraftSkinURL : profile?.minecraftProfile.skinURL,
            variant: profile == nil
                ? account.minecraftSkinVariant.flatMap(MinecraftSkinVariant.init(rawValue:))
                : profile?.minecraftProfile.skinVariant,
            loadCape: true,
            appearanceRevision: appearances.revision(for: account.xuid),
            isAuthoritative: profile != nil,
            capeURL: profile?.activeCape?.secureURL,
            capeIsKnown: profile != nil
        )
    }

    private var accountDetails: some View {
        LauncherPage(caption: "プロフィール", title: "Профиль") {
            VStack(alignment: .leading, spacing: 18) {
                ProviderCard(title: "Аккаунт Microsoft", systemImage: "person.crop.circle.fill") {
                    AccountRow(title: account.gamertag, subtitle: account.email, badge: "Xbox") {
                        XboxAvatar(url: account.xboxAvatarURL, name: account.gamertag)
                            .clipShape(.circle)
                    }

                    AccountRow(
                        title: account.minecraftName ?? String(appLocalized: "Minecraft не подключён"),
                        subtitle: minecraftSubtitle,
                        badge: "Java Edition"
                    ) {
                        if let skinURL = minecraftSkinSource.skinURL {
                            MinecraftHead(skinURL: skinURL, revision: minecraftSkinSource.appearanceRevision)
                                .clipShape(.rect(cornerRadius: 10))
                        } else {
                            Image(systemName: "cube.transparent")
                                .font(.system(size: 22))
                                .foregroundStyle(.secondary)
                                .frame(maxWidth: .infinity, maxHeight: .infinity)
                                .background(.quaternary, in: .rect(cornerRadius: 10))
                        }
                    }

                    PlaytimeSummaryView(xuid: account.xuid)

                    if sessions.identity(for: account) == nil || minecraftSubtitle != nil {
                        Button("Повторить подключение Minecraft") { Task { await sessions.connect(account, force: true) } }
                            .buttonStyle(.glass)
                            .disabled(isConnecting)
                    }

                    Button("Выйти из Microsoft") { isConfirmingSignOut = true }
                        .buttonStyle(.glass)
                        .controlSize(.large)
                        .padding(.top, 8)
                        .confirmationDialog("Выйти из аккаунта Microsoft?", isPresented: $isConfirmingSignOut) {
                    Button("Выйти из Microsoft", role: .destructive) { Task { await signOut() } }
                            Button("Отмена", role: .cancel) {}
                        } message: {
                            Text("Данные Microsoft будут удалены с этого Mac. Сессия Hako останется подключённой.")
                        }
                }

                HakoProviderCard(onSignIn: onHakoSignIn)
            }
            .frame(maxWidth: 600, alignment: .leading)
        }
    }

    private var minecraftStatusText: String {
        switch minecraftStatus {
        case .idle: String(appLocalized: "Minecraft: Java Edition недоступен для этого аккаунта.")
        case .connecting: String(appLocalized: "Подключаем Minecraft…")
        case .failed(let message): message
        }
    }

    private var isConnecting: Bool { if case .connecting = minecraftStatus { true } else { false } }
    private var canEditCharacter: Bool {
        account.minecraftUUID != nil && account.minecraftName != nil && sessions.identity(for: account) != nil
    }
    private var minecraftSubtitle: String? {
        if case .idle = minecraftStatus { return account.minecraftName == nil ? minecraftStatusText : nil }
        return minecraftStatusText
    }

    private func signOut() async {
        do { try await playtime.signOutMicrosoft(xuid: account.xuid) }
        catch {
            signOutError = error.localizedDescription
            return
        }
        sessions.signOut()
        TokenKeychain.delete(for: account.xuid)
        modelContext.delete(account)
        try? modelContext.save()
    }
}

struct GuestProfileView: View {
    let onMicrosoftSignIn: () -> Void
    let onHakoSignIn: () -> Void

    var body: some View {
        LauncherPage(caption: "プロフィール", title: "Профиль") {
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    ProviderCard(title: "Аккаунт Microsoft", systemImage: "person.crop.circle") {
                        Label("Аккаунт Microsoft не подключён", systemImage: "person.crop.circle")
                            .font(.title3.weight(.semibold))
                        Text("Для онлайн-запуска подключите Microsoft с Minecraft: Java Edition. Offline-mode настраивается отдельно для каждой сборки.")
                            .foregroundStyle(.secondary)
                        PlaytimeSummaryView(xuid: nil)
                        Text("Гостевое время переносится в аккаунт Microsoft при входе. Время Hako хранится отдельно.")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                        Button("Войти в Microsoft", action: onMicrosoftSignIn)
                            .buttonStyle(.glassProminent)
                            .tint(.sakuraDeep)
                            .controlSize(.large)
                    }

                    HakoProviderCard(onSignIn: onHakoSignIn)
                }
                .frame(maxWidth: 600, alignment: .leading)
            }
        }
    }
}

private struct HakoProviderCard: View {
    let onSignIn: () -> Void

    @Environment(HakoAccountCoordinator.self) private var hakoAccount
    @State private var isConfirmingLogout = false
    @State private var isShowingLogoutFailure = false

    var body: some View {
        ProviderCard(title: "Аккаунт Hako", systemImage: "paperplane.fill") {
            if let session = hakoAccount.session {
                AccountRow(title: session.account.username, subtitle: session.deviceName, badge: "Telegram") {
                    Image(systemName: "paperplane.fill")
                        .font(.system(size: 24))
                        .foregroundStyle(Color.sakuraDeep)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .background(.quaternary, in: .circle)
                }

                Text(sessionStatusText)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                HakoPlaytimeSummaryView(ownerID: session.account.id)
                if let error = hakoAccount.verificationError {
                    Text(error.localizedDescription)
                        .font(.callout)
                        .foregroundStyle(Color.shu)
                        .textSelection(.enabled)
                }

                HStack(spacing: 12) {
                    Button("Проверить подключение") { Task { await hakoAccount.verifyManually() } }
                        .buttonStyle(.glass)
                        .disabled(hakoAccount.isLoggingOut || hakoAccount.sessionStatus == .verifying)
                    Button(hakoAccount.isLoggingOut ? "Выходим…" : "Выйти из Hako", role: .destructive) {
                        isConfirmingLogout = true
                    }
                    .buttonStyle(.glass)
                    .disabled(hakoAccount.isLoggingOut)
                }
                .controlSize(.large)
                .confirmationDialog("Выйти из Hako?", isPresented: $isConfirmingLogout) {
                    Button("Выйти из Hako", role: .destructive) { Task { await performLogout() } }
                    Button("Отмена", role: .cancel) {}
                } message: {
                    Text("Сессия будет отозвана на сервере и удалена с этого Mac.")
                }
            } else {
                Label("Аккаунт Hako не подключён", systemImage: "person.crop.circle")
                    .font(.title3.weight(.semibold))
                if let error = hakoAccount.startupError {
                    Text(error.localizedDescription)
                        .font(.callout)
                        .foregroundStyle(Color.shu)
                        .textSelection(.enabled)
                }
                Button("Войти через Telegram", action: onSignIn)
                    .buttonStyle(.glassProminent)
                    .tint(.sakuraDeep)
                    .controlSize(.large)
                    .disabled(!hakoAccount.canStartAuthentication)
            }
        }
        .alert("Не удалось выйти из Hako", isPresented: $isShowingLogoutFailure) {
            Button("Повторить") { Task { await performLogout() } }
            Button("Выйти только на этом Mac", role: .destructive) {
                Task { _ = await hakoAccount.clearLocalSessionOnly() }
            }
            Button("Отмена", role: .cancel) {}
        } message: {
            Text(
                (hakoAccount.verificationError?.localizedDescription ?? String(appLocalized: "Не удалось выполнить выход из аккаунта Hako.")) +
                    "\n\n" + String(appLocalized: "Удаление только локальной сессии может оставить серверную сессию активной в Telegram.")
            )
        }
    }

    private var sessionStatusText: String {
        switch hakoAccount.sessionStatus {
        case .signedOut: String(appLocalized: "Аккаунт Hako не подключён")
        case .unverified: String(appLocalized: "Сессия сохранена, но ещё не проверена.")
        case .verifying: String(appLocalized: "Проверяем аккаунт Hako…")
        case .verified: String(appLocalized: "Аккаунт Hako подключён.")
        case .expired: String(appLocalized: "Сохранённая сессия Hako истекла.")
        case .logoutFailed: String(appLocalized: "Не удалось завершить сессию Hako.")
        }
    }

    private func performLogout() async {
        let result = await hakoAccount.logout()
        isShowingLogoutFailure = result != .signedOut
    }
}

private struct ProviderCard<Content: View>: View {
    let title: LocalizedStringKey
    let systemImage: String
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Label(title, systemImage: systemImage)
                .font(.title3.weight(.semibold))
            content
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(18)
        .glassEffect(.regular, in: .rect(cornerRadius: 24))
    }
}

private struct AccountRow<Avatar: View>: View {
    let title: String
    let subtitle: String?
    let badge: String
    @ViewBuilder var avatar: Avatar

    var body: some View {
        HStack(spacing: 16) {
            avatar.frame(width: 56, height: 56)
            VStack(alignment: .leading, spacing: 4) {
                Text(title).font(.title3.weight(.semibold))
                if let subtitle {
                    Text(subtitle)
                        .foregroundStyle(.secondary)
                        .lineLimit(2)
                        .textSelection(.enabled)
                }
            }
            Spacer(minLength: 16)
            Text(badge).font(.callout.weight(.medium)).foregroundStyle(.secondary)
        }
        .padding(16)
        .glassEffect(.regular, in: .rect(cornerRadius: 22))
    }
}

//
//  LauncherRail.swift
//  Hako
//

import SwiftUI

/// Узкая стеклянная лента вкладок: сборки сверху, настройки и профиль снизу.
struct LauncherRail: View {
    let account: Account
    @Binding var selection: LauncherTab

    var body: some View {
        VStack(spacing: 10) {
            RailButton(title: "Сборки", systemImage: "shippingbox.fill", isSelected: selection == .instances) {
                selection = .instances
            }

            Divider()
                .frame(width: 28)

            // Создание сборок появится позже.
            Button {} label: {
                Image(systemName: "plus")
                    .font(.system(size: 18, weight: .semibold))
                    .foregroundStyle(.tertiary)
                    .frame(width: 44, height: 44)
            }
            .buttonStyle(.plain)
            .disabled(true)
            .help("Новая сборка — скоро")

            Spacer()

            RailButton(title: "Настройки", systemImage: "gearshape.fill", isSelected: selection == .settings) {
                selection = .settings
            }

            Button { selection = .profile } label: {
                AccountAvatar(account: account)
                    .frame(width: 40, height: 40)
                    .clipShape(.rect(cornerRadius: 10))
                    .padding(3)
                    .overlay {
                        RoundedRectangle(cornerRadius: 13)
                            .strokeBorder(Color.sakuraDeep, lineWidth: 2.5)
                            .opacity(selection == .profile ? 1 : 0)
                    }
            }
            .buttonStyle(.plain)
            .help("Профиль")
            .accessibilityLabel("Профиль")
        }
        .padding(.vertical, 14)
        .frame(width: 72)
        .frame(maxHeight: .infinity)
        .glassEffect(.regular, in: .rect(cornerRadius: 26))
        // Выбранная вкладка и так подсвечена; системное кольцо фокуса выбивается из дизайна рейла.
        .focusEffectDisabled()
    }
}

private struct RailButton: View {
    let title: LocalizedStringKey
    let systemImage: String
    let isSelected: Bool
    let action: () -> Void

    @State private var isHovered = false

    var body: some View {
        Button(action: action) {
            Image(systemName: systemImage)
                .font(.system(size: 18, weight: .semibold))
                .foregroundStyle(isSelected ? AnyShapeStyle(.white) : AnyShapeStyle(.secondary))
                .frame(width: 44, height: 44)
                .background(background, in: .rect(cornerRadius: 12))
                .contentShape(.rect(cornerRadius: 12))
        }
        .buttonStyle(.plain)
        .onHover { isHovered = $0 }
        .help(title)
        .accessibilityLabel(title)
    }

    private var background: Color {
        if isSelected { return .sakuraDeep }
        return isHovered ? .primary.opacity(0.06) : .clear
    }
}

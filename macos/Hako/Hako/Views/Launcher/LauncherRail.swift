//
//  LauncherRail.swift
//  Hako
//

import SwiftUI

/// Узкая стеклянная лента вкладок: сборки сверху, настройки и профиль снизу.
struct LauncherRail: View {
    let account: Account?
    let instances: [GameInstance]
    @Binding var selection: LauncherTab
    let onCreate: () -> Void
    @Environment(InstallationCoordinator.self) private var installations

    var body: some View {
        VStack(spacing: 10) {
            RailButton(title: "Сборки", systemImage: "shippingbox.fill", isSelected: selection == .instances) {
                selection = .instances
            }

            Divider()
                .frame(width: 28)

            if !instances.isEmpty {
                ScrollView {
                    VStack(spacing: 10) {
                        ForEach(instances) { instance in
                            Button { selection = .instance(instance.id) } label: {
                                InstanceIcon(symbol: instance.iconSymbol, url: try? InstanceStorage.containedURL("icon.png", in: installations.store.storage.directory(instance.folderName)))
                                    .frame(width: 40, height: 40).id(instance.iconRevision)
                                    .padding(3)
                                    .overlay { RoundedRectangle(cornerRadius: 13).strokeBorder(selection == .instance(instance.id) ? Color.sakuraDeep : .clear, lineWidth: 2.5) }
                            }
                            .buttonStyle(.plain).help(instance.name).accessibilityLabel("Открыть сборку \(instance.name)")
                        }
                    }.padding(.horizontal, 3)
                }
                .scrollIndicators(.hidden)
                .frame(maxHeight: CGFloat(instances.count * 56))
            }

            Button(action: onCreate) {
                Image(systemName: "plus")
                    .font(.system(size: 18, weight: .semibold))
                    .foregroundStyle(Color.sakuraDeep)
                    .frame(width: 44, height: 44)
            }
            .buttonStyle(.plain)
            .help("Новая сборка")
            .accessibilityLabel("Новая сборка")

            Spacer()

            RailButton(title: "Настройки", systemImage: "gearshape.fill", isSelected: selection == .settings) {
                selection = .settings
            }

            Button { selection = .profile } label: {
                Group {
                    if let account { AccountAvatar(account: account) }
                    else { Image(systemName: "person.crop.circle").font(.system(size: 28)).foregroundStyle(Color.sakuraDeep) }
                }
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

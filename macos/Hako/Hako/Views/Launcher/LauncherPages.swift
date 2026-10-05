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

struct InstancesView: View {
    let instances: [GameInstance]
    var account: Account?
    let onCreate: () -> Void
    let onOpen: (GameInstance) -> Void
    @Environment(InstallationCoordinator.self) private var installations

    var body: some View {
        LauncherPage(caption: "パック", title: "Сборки") {
            if instances.isEmpty {
                VStack(spacing: 18) {
                    Image(systemName: "shippingbox").font(.system(size: 52, weight: .light)).foregroundStyle(Color.sakuraDeep)
                    Text("Создайте первую сборку").font(.title2.weight(.semibold))
                    Text("Выберите версию Minecraft — Hako загрузит Java и файлы игры.")
                        .foregroundStyle(.secondary).multilineTextAlignment(.center)
                    Button("Создать сборку", action: onCreate).buttonStyle(.glassProminent).tint(.sakuraDeep).controlSize(.large)
                }
                .padding(32).frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                VStack(alignment: .leading, spacing: 18) {
                    HStack {
                        Text("\(instances.count) сборок").foregroundStyle(.secondary)
                        Spacer()
                        Button("Новая сборка", systemImage: "plus", action: onCreate).buttonStyle(.glass)
                    }
                    if let error = installations.queueError {
                        HStack { Text(error).font(.callout).foregroundStyle(Color.shu); Button("Повторить") { installations.queueError = nil; installations.scheduleQueuedInstallations() } }
                    }
                    ScrollView {
                        LazyVGrid(columns: [GridItem(.adaptive(minimum: 240), spacing: 20)], spacing: 20) {
                            ForEach(instances) { instance in
                                VStack(alignment: .leading, spacing: 16) {
                                    Button { onOpen(instance) } label: {
                                        VStack(alignment: .leading, spacing: 16) {
                                            HStack(spacing: 14) {
                                                InstanceIcon(symbol: instance.iconSymbol, url: try? InstanceStorage.containedURL("icon.png", in: installations.store.storage.directory(instance.folderName)))
                                                    .frame(width: 56, height: 56).id(instance.iconRevision)
                                                VStack(alignment: .leading, spacing: 5) {
                                                    Text(instance.name).font(.title3.weight(.semibold)).lineLimit(2).foregroundStyle(.primary)
                                                    Text("\(instance.versionID) · Vanilla").font(.callout).foregroundStyle(.secondary)
                                                }
                                            }
                                            if let progress = installations.progress[instance.id], instance.state == .installing || instance.state == .paused {
                                                ProgressView(value: progress.fraction).tint(.sakuraDeep)
                                                Text(progress.stage).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                                            }
                                        }.frame(maxWidth: .infinity, alignment: .leading).contentShape(.rect)
                                    }.buttonStyle(.plain).accessibilityLabel("Открыть сборку \(instance.name)")
                                    InstancePlayControls(instance: instance, account: account, compact: true)
                                }.frame(maxWidth: .infinity, alignment: .leading).instanceSurface()
                            }
                        }.padding(2).padding(.bottom, 32)
                    }
                }.frame(maxHeight: .infinity)
            }
        }
    }
}

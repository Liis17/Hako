//
//  MinecraftCharacterEditorView.swift
//  Hako
//

import AppKit
import SwiftData
import SwiftUI
import UniformTypeIdentifiers

struct MinecraftCharacterEditorView: View {
    let account: Account
    let onBack: () -> Void
    let onSaved: () -> Void

    @Environment(MinecraftCharacterEditorCoordinator.self) private var editor
    @State private var isChoosingPNG = false

    var body: some View {
        HStack(alignment: .top, spacing: 32) {
            MinecraftSkinView(
                previewSkin: editor.previewSkin,
                previewRevision: editor.previewRevision,
                isLocalPreview: true,
                previewError: editor.previewError,
                onReadinessChange: { ready, revision in editor.setPreviewReady(ready, revision: revision) },
                onPreviewFailure: { message, revision in editor.reportPreviewFailure(message, revision: revision) }
            )
            .frame(minWidth: 280, maxWidth: .infinity, maxHeight: .infinity)
            .padding(.vertical, 16)

            VStack(alignment: .leading, spacing: 20) {
                header
                skinControls
                capeControls
                Spacer(minLength: 0)
                statusMessages
            }
            .frame(width: 430, alignment: .leading)
            .frame(maxHeight: .infinity, alignment: .top)
        }
        .fileImporter(isPresented: $isChoosingPNG, allowedContentTypes: [.png], allowsMultipleSelection: false) { result in
            switch result {
            case .success(let urls):
                guard let url = urls.first else { return }
                Task { await editor.importSkin(from: url) }
            case .failure(let error):
                editor.reportInputError(error.localizedDescription)
            }
        }
        .task(id: account.persistentModelID) { await editor.open(account) }
        .onDisappear { editor.close() }
    }

    private var header: some View {
        HStack(spacing: 16) {
            Button(action: onBack) {
                Label("Профиль", systemImage: "chevron.left")
            }
            .buttonStyle(.glass)
            .disabled(editor.isSaving)

            Spacer(minLength: 8)

            Button(action: save) {
                if editor.isSaving {
                    Label("Сохраняем…", systemImage: "arrow.triangle.2.circlepath")
                } else {
                    Label("Сохранить", systemImage: "checkmark")
                }
            }
            .buttonStyle(.glassProminent)
            .tint(.sakuraDeep)
            .disabled(!editor.canSave)
            .keyboardShortcut(.defaultAction)
        }
        .overlay {
            Text("Изменить персонажа")
                .font(.title2.weight(.semibold))
                .allowsHitTesting(false)
        }
        .padding(.top, 8)
    }

    private var skinControls: some View {
        VStack(alignment: .leading, spacing: 14) {
            Label("Скин", systemImage: "person.crop.square")
                .font(.headline)

            Button {
                isChoosingPNG = true
            } label: {
                Label(editor.selectedSkin == nil ? "Выбрать PNG" : "Заменить PNG", systemImage: "square.and.arrow.up")
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .buttonStyle(.glass)
            .disabled(!editor.canImportSkin)

            Picker("Модель", selection: Binding(
                get: { editor.selectedVariant },
                set: { editor.selectVariant($0) }
            )) {
                Text("Классическая").tag(MinecraftSkinVariant.classic)
                Text("Тонкие руки").tag(MinecraftSkinVariant.slim)
            }
            .pickerStyle(.segmented)
            .disabled(!editor.canChooseVariant)

            if let inputError = editor.inputError {
                Text(inputError).font(.caption).foregroundStyle(.red)
            }
            if editor.selectedSkin?.isLegacy == true {
                Text("Для скина 64 × 32 используется классическая модель.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .padding(18)
        .frame(maxWidth: .infinity, alignment: .leading)
        .glassEffect(.regular, in: .rect(cornerRadius: 22))
    }

    private var capeControls: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Label("Плащ", systemImage: "rectangle.portrait")
                    .font(.headline)
                Spacer()
                if editor.isLoading { ProgressView().controlSize(.small) }
            }

            switch editor.capeLoadState {
            case .loading:
                ContentUnavailableView("Загружаем плащи", systemImage: "arrow.triangle.2.circlepath")
                    .frame(maxWidth: .infinity, minHeight: 145)
            case .failed(let message):
                VStack(spacing: 10) {
                    Text(message).font(.callout).foregroundStyle(.secondary).multilineTextAlignment(.center)
                    Button("Повторить загрузку") { Task { await editor.loadProfile() } }
                        .buttonStyle(.glass)
                }
                .frame(maxWidth: .infinity, minHeight: 145)
            case .loaded:
                if editor.capes.isEmpty {
                    Text("У аккаунта пока нет плащей.")
                        .font(.callout).foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, minHeight: 36, alignment: .leading)
                }
                ScrollView {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 112), spacing: 10)], spacing: 10) {
                        CapeTile(title: String(appLocalized: "Без плаща"), imageData: nil, isSelected: editor.selectedCapeID == nil, isNoCape: true) {
                            editor.selectCape(nil)
                        }
                        ForEach(editor.capes) { cape in
                            CapeTile(
                                title: cape.name,
                                imageData: editor.capeImages[cape.id],
                                isSelected: editor.selectedCapeID == cape.id,
                                error: editor.capeImageErrors[cape.id]
                            ) {
                                if editor.capeImageErrors[cape.id] != nil { editor.retryCapeImage(cape) }
                                editor.selectCape(cape.id)
                            }
                        }
                    }
                    .padding(.vertical, 2)
                }
                .frame(maxHeight: .infinity)
            }
        }
        .padding(18)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .glassEffect(.regular, in: .rect(cornerRadius: 22))
        .disabled(editor.isSaving)
    }

    @ViewBuilder private var statusMessages: some View {
        if editor.sessionUnavailable {
            Text("Minecraft-сессия недоступна. Повторите подключение аккаунта.")
                .font(.callout).foregroundStyle(.secondary)
            Button("Повторить подключение Minecraft") { Task { await editor.reconnect() } }
                .buttonStyle(.glass)
                .disabled(editor.isSaving)
        }
        if editor.previewError != nil {
            Button("Повторить предпросмотр") { editor.retryPreview() }
                .buttonStyle(.glass)
                .disabled(editor.isSaving || editor.isLoading)
        }
        if let saveError = editor.saveError {
            Label(saveError, systemImage: "exclamationmark.triangle.fill")
                .font(.callout).foregroundStyle(.red)
                .fixedSize(horizontal: false, vertical: true)
        } else if editor.hasUnsavedChanges && !editor.previewReady && editor.previewError == nil {
            Label("Подготавливаем предпросмотр…", systemImage: "hourglass")
                .font(.callout).foregroundStyle(.secondary)
        }
    }

    private func save() {
        Task {
            if await editor.save() { onSaved() }
        }
    }
}

private struct CapeTile: View {
    let title: String
    let imageData: Data?
    let isSelected: Bool
    var isNoCape = false
    var error: String? = nil
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(spacing: 8) {
                if isNoCape {
                    Image(systemName: "rectangle.portrait.slash")
                        .font(.title2)
                        .foregroundStyle(.secondary)
                        .frame(height: 54)
                } else if let imageData, let image = NSImage(data: imageData) {
                    Image(nsImage: image)
                        .resizable()
                        .interpolation(.none)
                        .scaledToFit()
                        .frame(height: 54)
                } else if error != nil {
                    Image(systemName: "exclamationmark.triangle")
                        .font(.title2)
                        .foregroundStyle(.secondary)
                        .frame(height: 54)
                } else {
                    ProgressView().controlSize(.small).frame(height: 54)
                }
                Text(title)
                    .font(.caption.weight(.medium))
                    .lineLimit(2)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: .infinity, minHeight: 30)
            }
            .padding(10)
            .frame(maxWidth: .infinity, minHeight: 110)
            .background(isSelected ? Color.sakuraDeep.opacity(0.14) : Color.white.opacity(0.42), in: .rect(cornerRadius: 14))
            .overlay { RoundedRectangle(cornerRadius: 14).strokeBorder(isSelected ? Color.sakuraDeep : .clear, lineWidth: 2) }
        }
        .buttonStyle(.plain)
        .disabled(isNoCape && error != nil)
        .accessibilityLabel(title)
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }
}

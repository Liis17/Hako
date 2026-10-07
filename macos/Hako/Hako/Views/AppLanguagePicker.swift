//
//  AppLanguagePicker.swift
//  Hako
//

import SwiftUI

/// Выбор языка интерфейса: приветствие и «Настройки → Основные».
struct AppLanguagePicker: View {
    @AppStorage(AppLanguage.storageKey) private var language = AppLanguage.current

    var body: some View {
        Picker("Язык", selection: $language) {
            ForEach(AppLanguage.allCases) { language in
                Text(verbatim: language.title).tag(language)
            }
        }
        .pickerStyle(.segmented)
        .labelsHidden()
        .fixedSize()
    }
}

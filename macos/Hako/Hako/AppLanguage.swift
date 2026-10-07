//
//  AppLanguage.swift
//  Hako
//

import Foundation

/// Язык интерфейса Hako. Выбор хранится в `UserDefaults` и применяется без перезапуска.
nonisolated enum AppLanguage: String, CaseIterable, Identifiable, Sendable {
    case russian = "ru"
    case english = "en"

    static let storageKey = "appLanguage"

    /// Выбранный язык; до выбора — русский для русской системы, иначе английский.
    static var current: AppLanguage {
        if let raw = UserDefaults.standard.string(forKey: storageKey), let language = AppLanguage(rawValue: raw) { return language }
        return Locale.preferredLanguages.first?.hasPrefix("ru") == true ? .russian : .english
    }

    var id: Self { self }
    var locale: Locale { Locale(identifier: rawValue) }
    /// Самоназвание языка, не переводится.
    var title: String { self == .russian ? "Русский" : "English" }
}

nonisolated extension String {
    /// Строка интерфейса на языке Hako. Ключ — русский текст из `Localizable.xcstrings`.
    init(appLocalized resource: LocalizedStringResource) {
        var resource = resource
        resource.locale = AppLanguage.current.locale
        self.init(localized: resource)
    }
}

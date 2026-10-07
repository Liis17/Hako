# Localization

Parent: [[Index]]

## Назначение

Русский и английский интерфейс Hako. Язык выбирается на приветствии и в «Настройки → Основные»
и меняется сразу, без перезапуска (решение пользователя).

## Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `macos/Hako/Hako/AppLanguage.swift` | `AppLanguage`, `String(appLocalized:)` | Выбранный язык и строки вне View |
| `macos/Hako/Hako/Views/AppLanguagePicker.swift` | `AppLanguagePicker` | Сегментный выбор «Русский / English» |
| `macos/Hako/Hako/Localizable.xcstrings` | — | Каталог строк: ключи — русский текст, перевод `en` |
| `macos/Hako/Hako/ContentView.swift` | `ContentView` | `.environment(\.locale, language.locale)` для всего окна |

## Публичные контракты

| Контракт | Поведение и условия |
|----------|---------------------|
| `AppLanguage.storageKey` | `"appLanguage"` в `UserDefaults.standard`, значения `ru` и `en`; пишет `@AppStorage` в `AppLanguagePicker` |
| `AppLanguage.current` | Сохранённый выбор; без него — русский, если первый язык системы `ru*`, иначе английский. `nonisolated`, вызывается из любого потока |
| `String(appLocalized:)` | Принимает `LocalizedStringResource` (литерал попадает в каталог), задаёт `locale = AppLanguage.current.locale` и возвращает строку на выбранном языке |

## Как переводится строка

- Литералы SwiftUI с `LocalizedStringKey` (`Text`, `Button`, `Label`, `Toggle`, `Picker`, `.alert`, `.help`,
  `.accessibilityLabel`, параметры вроде `LauncherPage(title:)`) разрешаются по `\.locale` окружения
  и переключаются сразу. Листы и алерты наследуют окружение.
- Строки вне View и `String`-значения для интерфейса создаются через `String(appLocalized:)` в момент
  вычисления: ошибки `InstanceFileError`/`MojangError`, `errorDescription`, этапы установки, причины
  блокировки, `ContentConfirmation`, пункты `NSMenu`, `NSOpenPanel`, `InstallationState.title`, `PlaytimeFormatter`.
- Язык меняется только на приветствии и в «Основных», где видны одни литералы. Страницы `LauncherView`
  создаются заново при переходе (`switch tab` + `.id(tab)`), поэтому значения из `body` берут новый язык;
  пересоздавать дерево через `.id(language)` не нужно, навигация не сбрасывается.

## Правила для новых строк

- В View — литерал в API с `LocalizedStringKey`. Вспомогательный View, который только показывает текст,
  принимает `LocalizedStringKey`, а не `String`.
- Вне View и там, где вывод типов даёт `String` (`x ?? "…"`, `cond ? "…" : value`, `return "…"`
  в `String`-свойстве), — `String(appLocalized: "…")`.
- Фразы не склеиваются из кусков: варианты пишутся целиком (`"Заменить мод?"` / `"Заменить ресурспак?"`),
  перечисления — `.formatted(.list(type: .or).locale(AppLanguage.current.locale))`.
- Целые в `LocalizedStringResource` форматируются с разделителями разрядов, `UInt32` печатается неверно,
  поэтому коды ошибок интерполируются как `String(code)`.
- Английские формы числа — вариации `one`/`other` в каталоге (например, `%lld сборок`).
- После `%` в переводе не должна идти буква спецификатора формата (`70% of` даёт `%o`).
- Японские подписи (`JapaneseCaption`, `HankoSeal`) и самоназвания языков — `Text(verbatim:)`, не переводятся.

## Каталог

- `sourceLanguage = ru`; у проекта `developmentRegion = en`, `knownRegions` содержит `ru`.
  Без `ru.lproj` Bundle отдаёт запросу на русском английскую таблицу `developmentRegion`, поэтому у каждой строки
  есть запись `ru` со статусом `translated` — сборка всегда создаёт `ru.lproj`. Новая строка без записи `ru`
  всё равно показывается по-русски: таблица есть, поиск возвращает ключ.
- Xcode при сборке в IDE добавляет новые строки сам. Из командной строки: собрать проект, затем
  `xcrun xcstringstool sync macos/Hako/Hako/Localizable.xcstrings --stringsdata <DerivedData>/Build/Intermediates.noindex/Hako.build/Debug/Hako.build/Objects-normal/arm64/*.stringsdata`
  и добавить переводы `en`.
- `.stringsdata` хранит файл и строку каждого извлечённого литерала: по ним проверяется, что русский
  литерал не остался обычным `String`.
- `HakoTests` компилирует `AppLanguage.swift`, но каталога не содержит: `String(appLocalized:)` возвращает
  русский ключ, и тесты сверяют русский текст.

## Ограничения

- Меню macOS и системные кнопки (`NSOpenPanel`) следуют языку системы: для `ru` и `en` — своему,
  для остальных — английскому по `developmentRegion`.
- Строки, уже лежащие в состоянии (ошибки, этапы установки, `installationError` в SwiftData, открытые
  подтверждения), не переводятся заново; новые сообщения приходят на выбранном языке.
- Русские формы числа в каталоге не заданы: `%lld сборок` по-русски не склоняется.

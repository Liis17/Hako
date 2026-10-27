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
| `macos/Hako/Hako/AppShortcuts.xcstrings` | — | Фразы запуска через Siri с токенами `${target}` и `${applicationName}` |
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
- App Intents задают метаданные и диалоги через `LocalizedStringResource`/`IntentDialog` без
  `String(appLocalized:)`: их переводит система на язык Siri/Shortcuts. Фразы `AppShortcuts` хранятся
  в отдельном каталоге с сохранением токенов параметра и имени приложения.

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
- Собственные пункты Dock строятся заново через `String(appLocalized:)` на языке Hako.
  Названия сборок не переводятся. Подробности ошибок запуска, переданные Siri, используют язык
  игрового координатора; статические заголовки и ответы intent — язык системы/Siri.
- Строки, уже лежащие в состоянии (ошибки, этапы установки, `installationError` в SwiftData, открытые
  подтверждения), не переводятся заново; новые сообщения приходят на выбранном языке.
- Русские формы числа в каталоге не заданы: `%lld сборок` по-русски не склоняется.

## Windows-клиент

Строки — `windows/Hako/Strings/ru-RU/Resources.resw` и `windows/Hako/Strings/en-US/Resources.resw`
(`<data name="…">`). Чтение — `HakoStrings` (`windows/Hako/HakoStrings.cs`): MRT Core `ResourceManager`,
поддерево `Resources` и `ResourceContext`, в котором `QualifierValues["Language"]` равен `ru-RU` или `en-US`.
Тесты копируют оба файла в выходной каталог через `Link` в `windows/Hako.Tests/Hako.Tests.csproj`.

### Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `windows/Hako.Core/AppLanguage.cs` | `AppLanguage`, `AppLanguages`, `ToTag` | Язык, разбор значения из `settings.json`, системный язык по умолчанию |
| `windows/Hako/HakoStrings.cs` | `HakoStrings.Get`, `SetLanguage`, `LanguageChanged` | Чтение строк и смена языка |
| `windows/Hako.Core/AuthContracts.cs` | `AuthErrorKindExtensions.ResourceKey` | Ключи ошибок входа |
| `windows/Hako.Core/HakoAuthContracts.cs` | `HakoAuthErrorKindExtensions.ResourceKey`, `HakoSessionStatusExtensions.ResourceKey` | Hako auth, проверки сессии и статусы профиля |
| `windows/Hako.Core/AppCoordinator.cs` | `StartupNoticeExtensions.ResourceKey`, `SetLanguageAsync` | Ключи уведомлений запуска и смена языка, включая загрузку Hako-сессии |
| `windows/Hako.Core/Instances/InstallationCoordinator.cs` | `InstallationResourceKeys` | Ключи состояний и стадий установки, кодов ошибок установки, `ModpackException`, `InstanceStorageError.ResourceKey()`, `MojangError.ResourceKey()` и `ErrorResourceKey(Exception)` для ошибок UI |
| `windows/Hako/ContentResourceKeys.cs` | `ContentResourceKeys` | Ключи подтверждений, пояснений и ошибок `InstanceContentController` |
| `windows/Hako/WorldResourceKeys.cs` | `WorldResourceKeys` | Ключи ошибок миров и метаданных, типов игры, неизвестных значений и аргументированных строк |
| `windows/Hako/BackupResourceKeys.cs` | `BackupResourceKeys` | Ключи типизированных причин ошибки архивирования и форматирование диагностики/пути |
| `windows/Hako.Core/Playtime/PlaytimeFormatter.cs` | `PlaytimeFormatter` | Локализованное форматирование дней, часов и минут для русского и английского языков |
| `windows/Hako/BuildsPage.xaml.cs`, `InstanceProfilePage.xaml.cs`, `SettingsPage.xaml.cs` | `RefreshStrings` | Строки учёта времени и локализованная диагностика helper/storage |
| `windows/Hako/InstanceScreenshotsView.xaml.cs` | `InstanceScreenshotsView.RefreshStrings` | Подписи галереи скриншотов и обновление открытого раздела при смене языка |
| `windows/Hako.Tests/LocalizationResourcesTests.cs` | — | Совпадение ключей ru/en и покрытие ошибок Microsoft/Hako, уведомлений, контента, миров, скриншотов, архивов и строк учёта времени |

### Публичные контракты

| Контракт | Поведение и условия |
|----------|---------------------|
| `AppLanguages.Default` | Системный `CultureInfo.CurrentUICulture`: начинается с `ru` → Russian, иначе English |
| `AppLanguages.Parse(string?)` | `ru` или `ru-ru` → Russian, `en` или `en-us` → English, иное или `null` → `Default` |
| `AppLanguage.ToTag()` | Значение, которое пишется в `settings.json`: `ru` или `en` |
| `HakoStrings.Get(key)` | Строка из текущего `ResourceContext` |
| `HakoStrings.SetLanguage(language, force)` | Меняет контекст и вызывает `LanguageChanged`; без `force` при том же языке ничего не делает |
| `AuthErrorKind.ResourceKey()` | Ключ ошибки входа Microsoft; `Persistence` → `AccountSaveFailed` |
| `HakoAuthErrorKind.ResourceKey()` | Локализованные ошибки Hako: сеть, таймаут, 429, terminal status и сохранение |
| `HakoSessionStatus.ResourceKey()` | Статусы Hako-сессии в профиле, включая непроверенную, истёкшую и проблемную при выходе |
| `StartupNotice.ResourceKey()` | `PreferencesUnavailable` → `StartupPreferencesWarning`, `AccountUnavailable` → `StartupAccountWarning`, `HakoAccountUnavailable` → `StartupHakoAccountWarning` |

### Смена языка без перезапуска

1. `AppCoordinator.SetLanguageAsync` меняет `Language` и сохраняет `settings.json`; сохранения сериализованы и пишут текущий язык, поэтому побеждает последний выбор.
2. `MainWindow.Coordinator_PropertyChanged` при изменении `Language` вызывает `HakoStrings.SetLanguage`.
3. `HakoStrings` поднимает `LanguageChanged`. Страницы, которые сейчас открыты (`WelcomePage`, `LoginPage`, `HakoLoginPage`,
   `SettingsPage`, `BuildsPage`, `InstanceCreationPage`, `ModpackCatalogPage`, `ModpackCreationPage`, `InstanceProfilePage`) подписываются на событие в `OnNavigatedTo` и перечитывают строки;
   в `OnNavigatedFrom` подписка снимается. `InstanceContentView` и `ModrinthCatalogView` подписываются при `Loaded`
   и снимаются при `Unloaded`, поэтому видимые списки, действия и ожидающие диалоги обновляют текст без перезапуска.
  `InstanceWorldsView` обновляет подписи карточек и состояния мира; `WorldResourceKeys` локализует каждое значение
  `InstanceWorldFailureReason`, `WorldMetadataError` и известный тип игры. `InstanceScreenshotsView` также обновляет
  свои действия, карточки и состояния при смене языка.

Язык при старте: `AppCoordinator.InitializeAsync` берёт `AppLanguages.Parse(settings?.Language)`.
`MainWindow` вызывает `HakoStrings.SetLanguage(..., force: true)` в конструкторе.

### Правила для новых строк

- Строка добавляется в оба `.resw` с одинаковым именем. `RussianAndEnglishResourcesHaveTheSameNonemptyKeys`
  сверяет наборы ключей.
- Ключи ошибок и уведомлений берутся через `ResourceKey()`. `EveryMappedErrorAndStartupNoticeHasRussianAndEnglishText`
  проверяет, что они есть в обоих файлах. Ключи `CopyCodeFailed` и `OpenVerificationFailed` задаются в `LoginPage` литералами;
  проверки их наличия в `.resw` нет, `RussianAndEnglishResourcesHaveTheSameNonemptyKeys` сверяет только наборы ru/en.
- Ошибки сборок в UI не показывают `Exception.Message` (тексты Core английские): страницы берут ключ через
  `InstallationResourceKeys.ErrorResourceKey(exception)` (хранилище, реестр, Mojang, сеть; иное — `InstallationErrorUnexpected`),
  ошибки запуска — через `GameRunState.ErrorResourceKey`. `UiExceptionsMapToSpecificLocalizedErrors` проверяет покрытие.
- Тексты страниц задаются в коде через `HakoStrings.Get` в методе `RefreshStrings()`; `x:Uid` не используется.
- `ContentResourceKeys` исчерпывающе сопоставляет enum-коды подтверждений, пояснений и ошибок контроллера;
  `EveryContentConfirmationNoteAndFailureCodeMapsToRussianAndEnglishResources` проверяет обе локали.
  `FormatFailure` подставляет аргументы несовместимости Fabric API и числа ошибок; `FormatFailureDetails` сохраняет diagnostic, включая путь оставшейся резервной копии. Эти функции используются и для ответов отдельных действий; форматирование проверяется на обоих ресурсных файлах.
- `WorldResourceKeys` исчерпывающе сопоставляет ошибки мира и метаданных; форматирование версии, даты и размера,
  включая неизвестные значения, проверяется `EveryWorldFailureMetadataAndGameTypeMappingHasRussianAndEnglishText`
  и `WorldMessagesAndUnknownDisplayValuesFormatArguments` в обеих локалях.
- `BackupResourceKeys` сопоставляет каждый `BackupFailureReason` с ru/en строкой, а форматирование сохраняет
  diagnostic и путь оставшейся копии. Ресурсы действий профиля, миров и галереи проверяются тем же тестом локализации.
- Названия языков («Русский», «English») и заголовок окна `Title="Hako"` заданы литералами в XAML. Подписи пунктов
  навигации присваиваются из `BuildsTitle`, `SettingsTitle` и `CreateBuildButton` в `MainWindow.UpdateNavigationLabels`;
  пункты сборок показывают имя сборки без перевода.

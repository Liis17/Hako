# Architecture

Parent: [[Index]]

## Назначение и границы

Hako — нативное macOS-приложение с единственным таргетом `Hako`
(bundle ID `com.Launcher.Hako`).

Назначение (со слов пользователя): лаунчер Minecraft с авторизацией через Microsoft
и интеграцией каталогов Modrinth и CurseForge.

Текущее состояние: реализованы приветствие, вход в Microsoft по коду (device code flow)
с получением профиля Xbox и, если доступен, профиля Minecraft, главная страница с рейлом вкладок
(сборки — заглушка, настройки игры, хранилище, сведения о приложении, профиль с аккаунтами Xbox и Java) и выход.
Запуск игры, Modrinth и CurseForge не реализованы. Сторонних зависимостей (Swift Package Manager,
CocoaPods, Carthage не используются) и тестовых таргетов нет.

## Стек

| Технология | Роль | Источник |
|------------|------|----------|
| Swift, режим языка 5.0 | Язык приложения | `macos/Hako/Hako.xcodeproj/project.pbxproj` (`SWIFT_VERSION`) |
| SwiftUI | UI и жизненный цикл приложения (`@main` `App`) | `macos/Hako/Hako/HakoApp.swift` |
| SwiftData | Профиль вошедшего пользователя | `macos/Hako/Hako/HakoApp.swift`, `macos/Hako/Hako/Account.swift` |
| UserDefaults / `@AppStorage` | Глобальные параметры игры | `macos/Hako/Hako/GameLaunchDefaults.swift`, `macos/Hako/Hako/Views/Launcher/SettingsView.swift` |
| Security (Keychain) | Хранение токенов | `macos/Hako/Hako/Auth/TokenKeychain.swift` |
| URLSession | HTTP-запросы к Microsoft, Xbox Live и Minecraft | `macos/Hako/Hako/Auth/MicrosoftAuth.swift` |
| Xcode 27, macOS SDK | Сборка; минимальная ОС macOS 27.0 (`MACOSX_DEPLOYMENT_TARGET`) | `macos/Hako/Hako.xcodeproj/project.pbxproj` |

## Структура репозитория

| Путь | Содержимое |
|------|------------|
| `macos/Hako/Hako.xcodeproj` | Проект Xcode: таргет и схема `Hako`, конфигурации `Debug` и `Release` |
| `macos/Hako/Hako/` | Исходники и ресурсы приложения |
| `macos/Hako/Hako/AppIcon.icon` | Иконка приложения в формате Icon Composer (`ASSETCATALOG_COMPILER_APPICON_NAME = AppIcon`): слой `Assets/box.png` (коробка с сакурой, 1024×1024, прозрачный фон, Liquid Glass включён) на градиенте от белого к светлой сакуре. Тёмный, прозрачный и тонированный варианты система строит сама. Открывается в Icon Composer из Xcode |
| `macos/Hako/Hako/Assets.xcassets` | `AccentColor` (цвет не задан) |
| `Obsidian/HakoVault/` | Эта база знаний |

## Компоненты

- `HakoApp` (`macos/Hako/Hako/HakoApp.swift`) — точка входа `@main`: создаёт общий
  `ModelContainer` и единственную сцену `WindowGroup` с `ContentView`.
  Устройство контейнера описано в [[Data/Persistence]].
- [[Data/Persistence]] — схема SwiftData, модель `Account`, контейнер хранилища, токены в Keychain,
  глобальные параметры игры и снимок `GameLaunchDefaults` для будущего создания сборок.
- [[Auth/MicrosoftAuth]] — вход через Microsoft device code flow и цепочку Xbox Live → Minecraft.
- [[UI/ContentView]] — корень главного окна и настройки окна.
- [[UI/Screens]] — экраны, фон с сакурой и общий стиль.
- [[UI/Launcher]] — главная страница: рейл вкладок, профиль, аватары, автоподключение Minecraft.

## Основные потоки

### Запуск

1. При инициализации `HakoApp` вычисляется `sharedModelContainer`:
   `Schema([Account.self])` + `ModelConfiguration(isStoredInMemoryOnly: false)` → `ModelContainer`.
   Ошибка создания контейнера завершает приложение через `fatalError`.
2. `WindowGroup { ContentView() }` получает контейнер через
   `.modelContainer(sharedModelContainer)`, который помещает `modelContext` в окружение окна.
3. Окно открывается в 1280×720 без восстановления прошлого размера. `ContentView`
   показывает поверх фона с сакурой `LauncherView`, если в SwiftData есть `Account`, иначе приветствие.
4. `LauncherView` при появлении пробует подключить Minecraft, если он ещё не подключён ([[UI/Launcher]]).
   Настройки окна и выбор экрана — в [[UI/ContentView]].

Источники: `macos/Hako/Hako/HakoApp.swift`, `macos/Hako/Hako/ContentView.swift`.

### Вход

1. «Начать» на приветствии открывает `LoginView`.
2. `LoginView.signIn()` запрашивает код (`MicrosoftAuth.requestDeviceCode`) и показывает его;
   пользователь вводит код на microsoft.com/link.
3. `MicrosoftAuth.waitForToken` опрашивает Microsoft до подтверждения, `signIn` получает профиль Xbox
   и пробует Minecraft ([[Auth/MicrosoftAuth]]). Недоступный Minecraft не прерывает вход.
4. Токены сохраняются в Keychain под XUID, затем вставляется `Account` и сохраняется контекст ([[Data/Persistence]]).
5. `@Query` в `ContentView` видит аккаунт и переключает окно на `LauncherView`.

Источники: `macos/Hako/Hako/Views/LoginView.swift`, `macos/Hako/Hako/Auth/MicrosoftAuth.swift`.

### Выход

`ProfileView.signOut()` (вкладка «Профиль») удаляет запись токенов из Keychain и `Account` из SwiftData,
сохраняет контекст; `ContentView` возвращает приветствие.

Источники: `macos/Hako/Hako/Views/Launcher/ProfileView.swift`, `macos/Hako/Hako/Auth/TokenKeychain.swift`.

## Конфигурация сборки

Настройки таргета `Hako` одинаковы для `Debug` и `Release`:

- App Sandbox (`ENABLE_APP_SANDBOX = YES`), Hardened Runtime (`ENABLE_HARDENED_RUNTIME = YES`),
  доступ только на чтение к файлам, выбранным пользователем (`ENABLE_USER_SELECTED_FILES = readonly`),
  исходящие сетевые соединения (`ENABLE_OUTGOING_NETWORK_CONNECTIONS = YES` →
  `com.apple.security.network.client`). Отдельного `.entitlements`-файла нет.
- Provisioning profile не встраивается, поэтому у подписанной сборки нет entitlement
  `application-identifier` (важно для Keychain — см. [[Data/Persistence]]).
- Info.plist генерируется (`GENERATE_INFOPLIST_FILE = YES`); ключи задаются через
  `INFOPLIST_KEY_*` в настройках таргета.
- `macos/Hako/Hako/PrivacyInfo.xcprivacy` включён в ресурсы приложения и объявляет использование
  `UserDefaults` для собственных настроек (`CA92.1`) и API ёмкости диска для отображения места (`85F4.1`).
- Конкурентность: `SWIFT_DEFAULT_ACTOR_ISOLATION = MainActor`,
  `SWIFT_APPROACHABLE_CONCURRENCY = YES` — код модуля по умолчанию изолирован на главном акторе.
- Подпись автоматическая (`CODE_SIGN_STYLE = Automatic`), команда `DEVELOPMENT_TEAM = 9Y935NYUP9`.
- Локализация: включены String Catalogs (`LOCALIZATION_PREFERS_STRING_CATALOGS`,
  `SWIFT_EMIT_LOC_STRINGS`), но файла `.xcstrings` в проекте пока нет; `developmentRegion = en`.

Сборка из командной строки (проверено без подписи; такая сборка запускается без песочницы
и без entitlements — для проверки сети и Keychain в песочнице нужна подписанная сборка):

```sh
xcodebuild -project macos/Hako/Hako.xcodeproj -scheme Hako -configuration Debug build CODE_SIGNING_ALLOWED=NO
```

Схема `Hako` создаётся Xcode автоматически: общих `.xcscheme` в репозитории нет,
`xcuserdata/` исключён в `.gitignore`.

## Ограничения и инварианты

- Группа `Hako` в проекте — `PBXFileSystemSynchronizedRootGroup`: всё содержимое
  `macos/Hako/Hako/` автоматически входит в таргет. Любой файл, положенный в эту папку,
  попадает в сборку; правка `project.pbxproj` для добавления исходников не нужна.
- Новые `@Model`-типы регистрируются в схеме контейнера — см. [[Data/Persistence]].
- Песочница ограничивает доступ к файловой системе и сети: новые возможности
  (запись в выбранные файлы, входящие соединения и т. п.) требуют включения
  соответствующих capability в настройках таргета. Исходящие соединения включены.
- Вход в Minecraft требует Client ID Azure, одобренного Mojang; Client ID проекта задан в
  `MicrosoftAuth.clientID`, заявка на одобрение подана, но ещё не одобрена — см. [[Auth/MicrosoftAuth]].
- Интерфейс рассчитан на светлую тему и белый фон: `ContentView` принудительно задаёт `.light`.
- Тестовых таргетов нет; проверка изменений сейчас ограничена сборкой и ручным запуском.

## Решения и основания

- SwiftUI + SwiftData, App Sandbox и текущие настройки сборки получены из шаблона Xcode
  при создании проекта. Причины выбора и дальнейшие архитектурные планы не задокументированы.
- Вход как Microsoft, если Minecraft недоступен; в профиле две строки (Xbox: gamertag, аватар, email;
  Java: голова персонажа, ник); аватар в рейле — голова Minecraft, иначе аватар Xbox; узкий рейл
  из иконок; сборки пока заглушка; автоподключение Minecraft при запуске — решения пользователя.
- Вход по коду через microsoft.com/link, токены в Keychain
  и профиль в SwiftData, окно 1280×720 (растягиваемое, минимум 960×540), русский интерфейс
  с японскими акцентами — решения пользователя.
- Обычная связка ключей вместо Data Protection Keychain — из-за отсутствия `application-identifier`
  (см. [[Data/Persistence]]).
- Глобальные параметры игры сохраняются автоматически и задают значения для новых сборок.
  Новая сборка должна получать копию параметров на момент создания — решение пользователя;
  изменения глобальных значений не должны менять существующие сборки.

## Добавление компонента

1. Создать Swift-файл в `macos/Hako/Hako/` (допускаются подпапки) — он автоматически войдёт в таргет.
2. Новую модель SwiftData зарегистрировать по правилам [[Data/Persistence]].
3. Новое представление подключить к иерархии, начиная с `ContentView`, или как новую сцену в `HakoApp.body`.

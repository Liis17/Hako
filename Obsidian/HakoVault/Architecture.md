# Architecture

Parent: [[Index]]

## Назначение и границы

Hako — нативное macOS-приложение с единственным таргетом `Hako`
(bundle ID `com.Launcher.Hako`).

Назначение (со слов пользователя): лаунчер Minecraft с авторизацией через Microsoft
и интеграцией каталогов Modrinth и CurseForge.

Текущее состояние: ни одна из этих функций не реализована. Есть окно с экраном приветствия
(см. [[UI/Screens]]); SwiftData-контейнер и модель `Item` остались от шаблона Xcode.
Сетевого взаимодействия, сторонних зависимостей (Swift Package Manager,
CocoaPods, Carthage не используются) и тестовых таргетов нет. Данные хранятся локально через SwiftData.

## Стек

| Технология | Роль | Источник |
|------------|------|----------|
| Swift, режим языка 5.0 | Язык приложения | `macos/Hako/Hako.xcodeproj/project.pbxproj` (`SWIFT_VERSION`) |
| SwiftUI | UI и жизненный цикл приложения (`@main` `App`) | `macos/Hako/Hako/HakoApp.swift` |
| SwiftData | Локальное хранение моделей | `macos/Hako/Hako/HakoApp.swift`, `macos/Hako/Hako/Item.swift` |
| Xcode 27, macOS SDK | Сборка; минимальная ОС macOS 27.0 (`MACOSX_DEPLOYMENT_TARGET`) | `macos/Hako/Hako.xcodeproj/project.pbxproj` |

## Структура репозитория

| Путь | Содержимое |
|------|------------|
| `macos/Hako/Hako.xcodeproj` | Проект Xcode: таргет и схема `Hako`, конфигурации `Debug` и `Release` |
| `macos/Hako/Hako/` | Исходники и ресурсы приложения |
| `macos/Hako/Hako/Assets.xcassets` | `AppIcon` (слоты без изображений), `AccentColor` (цвет не задан) |
| `Obsidian/HakoVault/` | Эта база знаний |

## Компоненты

- `HakoApp` (`macos/Hako/Hako/HakoApp.swift`) — точка входа `@main`: создаёт общий
  `ModelContainer` и единственную сцену `WindowGroup` с `ContentView`.
  Устройство контейнера описано в [[Data/Persistence]].
- [[Data/Persistence]] — схема SwiftData, модель `Item`, контейнер хранилища.
- [[UI/ContentView]] — корень главного окна и настройки окна.
- [[UI/Screens]] — экраны, фон с сакурой и общий стиль.

## Основные потоки

### Запуск

1. При инициализации `HakoApp` вычисляется `sharedModelContainer`:
   `Schema([Item.self])` + `ModelConfiguration(isStoredInMemoryOnly: false)` → `ModelContainer`.
   Ошибка создания контейнера завершает приложение через `fatalError`.
2. `WindowGroup { ContentView() }` получает контейнер через
   `.modelContainer(sharedModelContainer)`, который помещает `modelContext` в окружение окна.
3. Окно открывается в 1280×720 без восстановления прошлого размера; `ContentView`
   показывает экран приветствия поверх фона с сакурой. Настройки окна — в [[UI/ContentView]].

Источники: `macos/Hako/Hako/HakoApp.swift`, `macos/Hako/Hako/ContentView.swift`.

## Конфигурация сборки

Настройки таргета `Hako` одинаковы для `Debug` и `Release`:

- App Sandbox (`ENABLE_APP_SANDBOX = YES`), Hardened Runtime (`ENABLE_HARDENED_RUNTIME = YES`),
  доступ только на чтение к файлам, выбранным пользователем (`ENABLE_USER_SELECTED_FILES = readonly`).
  Отдельного `.entitlements`-файла нет.
- Info.plist генерируется (`GENERATE_INFOPLIST_FILE = YES`); ключи задаются через
  `INFOPLIST_KEY_*` в настройках таргета.
- Конкурентность: `SWIFT_DEFAULT_ACTOR_ISOLATION = MainActor`,
  `SWIFT_APPROACHABLE_CONCURRENCY = YES` — код модуля по умолчанию изолирован на главном акторе.
- Подпись автоматическая (`CODE_SIGN_STYLE = Automatic`), команда `DEVELOPMENT_TEAM = 9Y935NYUP9`.
- Локализация: включены String Catalogs (`LOCALIZATION_PREFERS_STRING_CATALOGS`,
  `SWIFT_EMIT_LOC_STRINGS`), но файла `.xcstrings` в проекте пока нет; `developmentRegion = en`.

Сборка из командной строки (проверено без подписи):

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
  (исходящие соединения, запись в выбранные файлы и т. п.) требуют включения
  соответствующих capability в настройках таргета. Исходящие сетевые соединения,
  нужные для авторизации Microsoft, Modrinth и CurseForge, сейчас не включены.
- Тестовых таргетов нет; проверка изменений сейчас ограничена сборкой и ручным запуском.

## Решения и основания

- SwiftUI + SwiftData, App Sandbox и текущие настройки сборки получены из шаблона Xcode
  при создании проекта. Причины выбора и дальнейшие архитектурные планы не задокументированы.

## Добавление компонента

1. Создать Swift-файл в `macos/Hako/Hako/` (допускаются подпапки) — он автоматически войдёт в таргет.
2. Новую модель SwiftData зарегистрировать по правилам [[Data/Persistence]].
3. Новое представление подключить к иерархии, начиная с `ContentView`, или как новую сцену в `HakoApp.body`.

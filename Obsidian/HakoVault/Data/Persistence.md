# Persistence

Parent: [[Index]]

## Назначение

Локальное хранение данных приложения: аккаунт вошедшего пользователя (Microsoft/Xbox и, если подключён,
Minecraft) — в SwiftData, его токены — в связке ключей (Keychain), глобальные параметры игры —
в `UserDefaults`. Профили сборок `GameInstance` — в том же SwiftData-store, независимые файлы — в `~/.hako/{folderName}/`. Наличие записи `Account` определяет, вошёл ли пользователь (см. [[UI/ContentView]]).

## Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `macos/Hako/Hako/HakoApp.swift` | `HakoApp.sharedModelContainer` | Схема, конфигурация и создание контейнера; подключение к сцене |
| `macos/Hako/Hako/Instances/GameInstance.swift` | `GameInstance`, `InstanceParameters`, `InstanceDraft` | Профиль сборки, параметры и черновик формы |
| `macos/Hako/Hako/Instances/InstanceStorage.swift` | `InstanceStorage`, `InstanceStore` | Файловые пути, создание и транзакционное редактирование |
| `macos/Hako/Hako/AppDataLocation.swift` | `AppDataLocation.storeURL` | Сохранение прежнего store и перенос глобальных параметров |
| `macos/Hako/Hako/Account.swift` | `Account` | Аккаунт Microsoft/Xbox и профиль Minecraft |
| `macos/Hako/Hako/Auth/TokenKeychain.swift` | `TokenKeychain`, `AccountTokens`, `KeychainError` | Токены аккаунта в Keychain |
| `macos/Hako/Hako/GameLaunchDefaults.swift` | `GameLaunchDefaults` | Ключи, значения по умолчанию и снимок параметров игры |
| `macos/Hako/Hako/Views/Launcher/SettingsView.swift` | `SettingsView` | Редактирование параметров через `@AppStorage` |

## Схема данных

### Account

`@Model final class Account`, инициализатор `init(xbox: XboxProfile, email: String?)`,
метод `connect(_ minecraft: MinecraftProfile)` заполняет поля Minecraft.

| Поле | Тип | Смысл |
|------|-----|-------|
| `xuid` | `String`, `@Attribute(.unique)`, по умолчанию `""` | XUID аккаунта Xbox; ключ записи в Keychain |
| `gamertag` | `String`, по умолчанию `""` | Gamertag Xbox |
| `email` | `String?` | Email Microsoft из `id_token` |
| `xboxAvatarURL` | `URL?` | Картинка профиля Xbox (`GameDisplayPicRaw`) |
| `minecraftUUID` | `String?` | UUID профиля Minecraft без дефисов; `nil` — Minecraft не подключён |
| `minecraftName` | `String?` | Ник Minecraft |
| `minecraftSkinURL` | `URL?` | https-ссылка на текстуру скина |
| `minecraftSkinVariant` | `String?` | `CLASSIC` или `SLIM`; `nil` у аккаунта без Minecraft или записи без метаданных |

Связей и индексов нет. Приложение рассчитано на один аккаунт: UI берёт `accounts.first`.
Значения по умолчанию у обязательных полей нужны для лёгкой миграции SwiftData.
Добавление необязательного `minecraftSkinVariant` поддерживает автоматическую лёгкую миграцию
с сохранением аккаунта. `Account.connect(_:)` сохраняет `MinecraftProfile.skinVariant.rawValue`;
для записи с `nil` 3D-превью получает вариант через публичный профиль по UUID ([[UI/MinecraftSkin]]),
не изменяя запись или токены.

### Токены

`AccountTokens: Codable` — `microsoftRefreshToken`, `minecraftAccessToken?`, `minecraftTokenExpiration?`
(токена Minecraft нет, пока Minecraft не подключён).
Хранится JSON в generic password: `kSecAttrService = "com.Launcher.Hako.auth"`, `kSecAttrAccount = xuid`.

### Глобальные параметры игры

`UserDefaults.standard`, ключи определены в `GameLaunchDefaults.Key`:

| Ключ | Тип | По умолчанию |
|------|-----|--------------|
| `gameDefaults.javaPath` | `String` | `""` — пользовательский путь не задан |
| `gameDefaults.javaArguments` | `String` | `""` |
| `gameDefaults.minecraftArguments` | `String` | `""` |
| `gameDefaults.fullscreen` | `Bool` | `false` |
| `gameDefaults.windowWidth` | `Int` | `1280` |
| `gameDefaults.windowHeight` | `Int` | `720` |

Параметры общие для приложения, сохраняются локально автоматически и не удаляются при выходе.
`GameLaunchDefaults.standard` — единый набор значений по умолчанию для формы и чтения.
`GameLaunchDefaults.load(from: UserDefaults = .standard)` возвращает снимок всех шести полей
как тип-значение с неизменяемыми свойствами. Отсутствующие значения заменяются стандартными;
неположительные размеры — стандартными размерами окна. `GameInstance.effectiveParameters(from:)` читает
актуальные аргументы и настройки окна, пока `usesGlobalParameters` включён; иначе возвращает
собственные значения сборки. Пользовательский путь к Java не отменяет загрузку управляемой Java в сборку.

## Публичные контракты

| Контракт | Поведение и условия |
|----------|---------------------|
| `HakoApp.sharedModelContainer: ModelContainer` | Схема `[Account, GameInstance]`, хранение на диске по явному URL из `AppDataLocation.storeURL()`. Создаётся при инициализации `HakoApp`; при ошибке вызывается `fatalError` |
| `.modelContainer(sharedModelContainer)` на `WindowGroup` | Помещает главный контекст контейнера (`modelContext`) в окружение всех представлений окна |
| `TokenKeychain.save(_:for:) throws` | Удаляет прежнюю запись для XUID и добавляет новую; при ошибке `SecItemAdd` бросает `KeychainError` |
| `TokenKeychain.load(for:) -> AccountTokens?` | Запись для XUID; `nil`, если записи нет или JSON не читается |
| `TokenKeychain.delete(for:)` | Удаляет запись для XUID; результат `SecItemDelete` не проверяется |
| `GameLaunchDefaults.load(from:) -> GameLaunchDefaults` | Не изменяет хранилище; возвращает независимый снимок параметров игры из переданного `UserDefaults` |

## Зависимости и взаимодействия

- [[UI/ContentView]] читает аккаунты через `@Query`.
- `LoginView.signIn()` после успешного [[Auth/MicrosoftAuth]] сохраняет токены
  (`TokenKeychain.save`), затем вставляет `Account` (с Minecraft, если он доступен) и вызывает `modelContext.save()`.
- `LauncherView.connectMinecraftIfNeeded()` читает токены, сохраняет обновлённый refresh token и,
  при успехе, токен Minecraft, затем `account.connect(_:)` и `modelContext.save()` ([[UI/Launcher]]).
- `ProfileView.signOut()` вызывает `TokenKeychain.delete`, удаляет `Account` и сохраняет контекст.

## Ограничения и важные детали

- Используется обычная (файловая) связка ключей входа, а не Data Protection Keychain:
  проверено, что без entitlement `application-identifier` (его даёт только provisioning profile,
  которого у проекта нет) Data Protection Keychain возвращает `errSecMissingEntitlement` (−34018).
  Доступ к записи привязан к подписи приложения; при смене подписи macOS может спросить доступ к связке.
- Версионирования схемы (`VersionedSchema`, `SchemaMigrationPlan`) нет. Изменения полей
  опираются на автоматическую лёгкую миграцию SwiftData; несовместимое изменение схемы
  приведёт к ошибке создания контейнера и падению при запуске через `fatalError`.
- Новую модель нужно добавить в `Schema([...])` в `HakoApp` и в контейнеры превью
  представлений, которые её используют (`.modelContainer(for:inMemory: true)` в `#Preview`).
- App Sandbox отключён: игровые файлы пишутся в настоящий `~/.hako` без выбора папки.
  SwiftData сохраняет прежний URL: приоритет — store в `Library/Containers/com.Launcher.Hako/Data/Library/Application Support`,
  затем прежний несандбоксированный `Library/Application Support/default.store`; для новой установки —
  `Library/Application Support/Hako/default.store`. Старые глобальные параметры импортируются один раз.

## Сборки

`GameInstance` хранит UUID, имя, имя папки, дату создания, ID/URL/SHA-1 версии, выбор иконки,
флаг глобальных параметров, собственные аргументы и размеры, состояние установки и ошибку.
Связи с `Account` нет: выход удаляет только аккаунт и токены.
Состояния — `queued`, `installing`, `paused`, `ready`, `failed`.

Имя: 1–60 латинских букв, цифр и пробелов, пробелы по краям удаляются; в папке пробелы заменены на `_`.
Коллизии проверяются по модели и файловой системе без учёта регистра. Существующие папки не используются
для новой сборки. `InstanceStore.create` создаёт `java/`, `minecraft/` и при необходимости `icon.png`,
затем сохраняет модель. Ошибка удаляет только созданную им папку.
`InstanceStore.update` переносит папку при переименовании, сохраняет UUID и относительные пути,
откатывает папку и иконку при ошибке сохранения. Переименование блокируется для установки и очереди.
`InstanceStorage.containedURL` запрещает traversal и выход через симлинки.

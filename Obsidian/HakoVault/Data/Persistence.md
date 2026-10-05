# Persistence

Parent: [[Index]]

## Назначение

Локальное хранение данных приложения: аккаунт вошедшего пользователя (Microsoft/Xbox и, если подключён,
Minecraft) — в SwiftData, его токены — в связке ключей (Keychain), глобальные параметры игры —
в `UserDefaults`. Наличие записи `Account` определяет, вошёл ли пользователь (см. [[UI/ContentView]]).

## Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `macos/Hako/Hako/HakoApp.swift` | `HakoApp.sharedModelContainer` | Схема, конфигурация и создание контейнера; подключение к сцене |
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

Связей и индексов нет. Приложение рассчитано на один аккаунт: UI берёт `accounts.first`.
Значения по умолчанию у обязательных полей нужны для лёгкой миграции SwiftData.

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
неположительные размеры — стандартными размерами окна. Будущее создание сборки должно копировать
снимок, чтобы последующие изменения глобальных параметров не меняли существующие сборки.
Модель сборок и потребитель снимка пока не реализованы.

## Публичные контракты

| Контракт | Поведение и условия |
|----------|---------------------|
| `HakoApp.sharedModelContainer: ModelContainer` | Схема `[Account]`, хранение на диске (`isStoredInMemoryOnly: false`) в стандартном расположении SwiftData — URL явно не задан. Создаётся при инициализации `HakoApp`; при ошибке вызывается `fatalError` |
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
- Приложение работает в App Sandbox, поэтому файлы хранилища находятся в контейнере
  приложения `com.Launcher.Hako`, а не в общем `~/Library/Application Support`.
  Неподписанная сборка (`CODE_SIGNING_ALLOWED=NO`) запускается без песочницы и пишет хранилище
  в `~/Library/Application Support`.

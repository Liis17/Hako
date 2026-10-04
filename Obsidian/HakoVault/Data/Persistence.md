# Persistence

Parent: [[Index]]

## Назначение

Локальное хранение данных приложения: профиль вошедшего пользователя — в SwiftData,
его токены — в связке ключей (Keychain). Наличие записи `Account` определяет,
вошёл ли пользователь (см. [[UI/ContentView]]).

## Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `macos/Hako/Hako/HakoApp.swift` | `HakoApp.sharedModelContainer` | Схема, конфигурация и создание контейнера; подключение к сцене |
| `macos/Hako/Hako/Account.swift` | `Account` | Профиль Minecraft |
| `macos/Hako/Hako/Auth/TokenKeychain.swift` | `TokenKeychain`, `AccountTokens`, `KeychainError` | Токены аккаунта в Keychain |

## Схема данных

### Account

`@Model final class Account`, инициализатор `init(uuid: String, name: String)`.

| Поле | Тип | Смысл |
|------|-----|-------|
| `uuid` | `String`, `@Attribute(.unique)` | UUID профиля Minecraft (`id` из `/minecraft/profile`, без дефисов); ключ записи в Keychain |
| `name` | `String` | Ник Minecraft |

Связей и индексов нет. Приложение рассчитано на один аккаунт: UI берёт `accounts.first`.

### Токены

`AccountTokens: Codable` — `microsoftRefreshToken`, `minecraftAccessToken`, `minecraftTokenExpiration`.
Хранится JSON в generic password: `kSecAttrService = "com.Launcher.Hako.auth"`, `kSecAttrAccount = uuid`.

## Публичные контракты

| Контракт | Поведение и условия |
|----------|---------------------|
| `HakoApp.sharedModelContainer: ModelContainer` | Схема `[Account]`, хранение на диске (`isStoredInMemoryOnly: false`) в стандартном расположении SwiftData — URL явно не задан. Создаётся при инициализации `HakoApp`; при ошибке вызывается `fatalError` |
| `.modelContainer(sharedModelContainer)` на `WindowGroup` | Помещает главный контекст контейнера (`modelContext`) в окружение всех представлений окна |
| `TokenKeychain.save(_:for:) throws` | Удаляет прежнюю запись для UUID и добавляет новую; при ошибке `SecItemAdd` бросает `KeychainError` |
| `TokenKeychain.delete(for:)` | Удаляет запись для UUID; результат `SecItemDelete` не проверяется |

## Зависимости и взаимодействия

- [[UI/ContentView]] читает аккаунты через `@Query`.
- `LoginView.signIn()` после успешного [[Auth/MicrosoftAuth]] сохраняет токены
  (`TokenKeychain.save`), затем вставляет `Account` и вызывает `modelContext.save()`.
- `HomeView.signOut()` вызывает `TokenKeychain.delete`, удаляет `Account` и сохраняет контекст.

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

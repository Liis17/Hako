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

### Наигранное время

`macos/Hako/Hako/Playtime/PlaytimeModels.swift` определяет `PlayerPlaytime`, `InstancePlaytime`,
`PlaytimeSession` и общую `HakoSchema.schema`. Длительности — `TimeInterval` в секундах, изначально ноль.
`PlayerPlaytime.ownerKey` уникален: `account:<XUID>` либо `guest`; `totalSeconds` — самостоятельное
общее время пользователя. `InstancePlaytime.key` объединяет владельца и UUID сборки; хранит её
`totalSeconds`. `PlaytimeSession.id` — уникальный UUID с владельцем, UUID сборки и `creditedSeconds`.
Отношений с `Account` и `GameInstance` нет: удаление данных входа или сборки не удаляет общее время.
Новая сборка с другим UUID начинает с нуля. Добавление моделей поддерживает лёгкую миграцию прежнего store.

`macos/Hako/Hako/Playtime/PlaytimeCoordinator.swift` — общий `@MainActor @Observable` сервис.
`beginSession(instanceID:xuid:)` сохраняет нулевую сессию; `credit(sessionID:elapsedSeconds:)`
начисляет только положительную разницу с курсором, сохраняет оба счётчика и курсор одной операцией.
Ошибка сохранения восстанавливает затронутые значения, не откатывая чужие несохранённые изменения.
`transferGuest(to:)` прибавляет гостевые счётчики аккаунту, обнуляет гостевые значения и переназначает
его сессии без изменения курсоров. Последующие интервалы этих сессий принадлежат вошедшему аккаунту,
даже после его выхода. Повторный вход переносит только новое гостевое время.
`totalSeconds(xuid:)` и `instanceSeconds(_:xuid:)` возвращают статистику текущего владельца.
`start()` включает чтение журналов при запуске и каждые 10 секунд; `reconcile()` начисляет сохранённые
интервалы и удаляет завершённый журнал только после успешной записи счётчиков и удаления сессии.
Завершённый файл с уже удалённой сессией очищается без повторного начисления. `refresh()` сохраняет
сообщение ошибки в `errorMessage` для UI, не останавливая игры; следующая проверка повторяет чтение.
Файлы и жизненный цикл независимого помощника описаны в [[Minecraft/Launching]].

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
| `gameDefaults.maximumMemoryMiB` | `Int` | 25% физической памяти, максимум 4096 МиБ |

Параметры общие для приложения, сохраняются локально автоматически и не удаляются при выходе.
`GameLaunchDefaults.standard` — единый набор значений по умолчанию для формы и чтения.
`GameLaunchDefaults.load(from: UserDefaults = .standard)` возвращает снимок всех семи полей
как независимый снимок. Отсутствующие значения заменяются стандартными;
неположительные размеры — стандартными размерами окна.
`GameInstance.effectiveParameters(from:)` наследует только аргументы и лимит памяти в режиме `global`;
режим `mojang` не добавляет собственные аргументы, `custom` использует сохранённые поля сборки.
Окно всегда принадлежит сборке: глобальные размеры копируются только при создании.
Пользовательский путь к Java не отменяет загрузку управляемой Java в сборку.

`LaunchArgumentSource` — `global`, `mojang`, `custom`; `argumentSourceRaw` хранит выбор, новые сборки
используют `mojang`. Прежний `usesGlobalParameters` сохраняется для миграции записей с `argumentSourceRaw == nil`.
`LaunchSettingsMigration.run` вызывается после открытия store: сохраняет прежний режим, фиксирует
эффективное окно старых глобальных сборок и переносит существующий максимальный heap в ползунок.
Отсутствующий лимит получает динамическое начальное значение. UUID, папки, аккаунт и Keychain не меняются;
ошибка сохранения откатывает модель и не помечает сборки мигрированными.

`maximumMemoryMiB` хранится в сборке и UserDefaults. `JavaMemoryPolicy` ограничивает его диапазоном
512 МиБ — физическая память Mac с шагом 256 МиБ, предупреждает строго выше 70% физической памяти.
`LaunchArguments` разбирает кавычки и экранирование без shell, заменяет все `-Xmx`/`MaxHeapSize`
одним лимитом и уменьшает превышающий его начальный heap; сохранённый текст не переписывает.
`offlineMode` по умолчанию выключен; `offlineUsername` — `Player`, допустимы 3–16 ASCII букв, цифр или `_`.

## Публичные контракты

| Контракт | Поведение и условия |
|----------|---------------------|
| `HakoApp.sharedModelContainer: ModelContainer` | Схема `[Account, GameInstance, PlayerPlaytime, InstancePlaytime, PlaytimeSession]`, хранение на диске по явному URL из `AppDataLocation.storeURL()`. Создаётся при инициализации `HakoApp`; при ошибке вызывается `fatalError` |
| `.modelContainer(sharedModelContainer)` на `WindowGroup` | Помещает главный контекст контейнера (`modelContext`) в окружение всех представлений окна |
| `TokenKeychain.save(_:for:) throws` | Удаляет прежнюю запись для XUID и добавляет новую; при ошибке `SecItemAdd` бросает `KeychainError` |
| `TokenKeychain.load(for:) -> AccountTokens?` | Запись для XUID; `nil`, если записи нет или JSON не читается |
| `TokenKeychain.delete(for:)` | Удаляет запись для XUID; результат `SecItemDelete` не проверяется |
| `GameLaunchDefaults.load(from:) -> GameLaunchDefaults` | Не изменяет хранилище; возвращает независимый снимок параметров игры из переданного `UserDefaults` |
| `InstanceStore.updateSettings(_:with:) throws` | Сохраняет иконку и параметры сборки, не меняя имя или папку |
| `InstanceStore.rename(_:to:) throws` | Проверяет новое имя, перемещает папку сборки и сохраняет имя и `folderName` одной операцией |

## Зависимости и взаимодействия

- [[UI/ContentView]] читает аккаунты через `@Query`.
- `LoginView.signIn()` после успешного [[Auth/MicrosoftAuth]] сохраняет токены
  (`TokenKeychain.save`), затем вставляет `Account` (с Minecraft, если он доступен) и вызывает `PlaytimeCoordinator.transferGuest(to:)`.
  Данные входа и перенос гостевой статистики сохраняются одной операцией; при ошибке вставленный аккаунт удаляется.
- `MinecraftSessionCoordinator.connect` читает токены, сохраняет обновлённый refresh token и,
  при успехе, токен Minecraft, затем `account.connect(_:)` и `modelContext.save()` ([[UI/Launcher]]).
- `ProfileView.signOut()` отменяет общую Minecraft-сессию, вызывает `TokenKeychain.delete`, удаляет `Account` и сохраняет контекст.

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
  `Library/Application Support/Hako/default.store`. Выбранный URL закрепляется в `hako.modelStorePath`,
  чтобы последующие запуски не переключались между существующими store. Ошибка доступа к прежнему store
  не трактуется как отсутствие файла. Старые глобальные параметры импортируются один раз (`hako.didMigrateGameDefaults`);
  имена сервиса и ключей Keychain сохраняются.

## Сборки

`GameInstance` хранит UUID, имя, имя папки, дату создания, ID/URL/SHA-1 версии, выбор иконки,
источник аргументов, offline-режим и ник, собственные аргументы, память и размеры, состояние установки и ошибку.
Связи с `Account` нет: выход удаляет только аккаунт и токены.
Состояния — `queued`, `installing`, `paused`, `ready`, `failed`; `pauseRequested` сохраняет намерение
остановки до завершения отменённых файловых операций. Относительный `javaExecutable` и версия Java
не меняются при переименовании; `legacyTexturepacks` определяет папку локальных текстурпаков.

Имя: 1–60 латинских букв, цифр и пробелов, пробелы по краям удаляются; в папке пробелы заменены на `_`.
Коллизии проверяются по модели и файловой системе без учёта регистра. Существующие папки не используются
для новой сборки. Корень сборки создаётся эксклюзивно: одновременное появление чужой папки
не приводит к её повторному использованию. `InstanceStore.create` создаёт `java/`, `minecraft/` и при необходимости `icon.png`,
затем сохраняет модель. Ошибка удаляет только созданную им папку.
`InstanceStore.rename` переносит папку при переименовании, сохраняет UUID и относительные пути,
откатывает перемещение при ошибке сохранения и использует временное имя для смены регистра.
Переименование блокируется для установки, очереди и подготовки/работы игры через `InstanceStore.launchBusy`;
экран дополнительно блокирует его на время импорта и удаления файлов. `InstanceStore.updateSettings`
автоматически сохраняет иконку и параметры отдельно от черновика имени. `InstanceStore.update` поддерживает
транзакционное обновление всех полей.
`InstanceStorage.directory` запрещает symlink самого каталога сборки, чтобы одна сборка не писала в другую.
`containedURL` запрещает traversal и выход через симлинки. `allocatedSize()` суммирует фактически
занятый объём обычных файлов без повторного учёта ссылок; сканирование выполняется вне главного потока.

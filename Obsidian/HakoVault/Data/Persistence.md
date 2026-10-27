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
| `macos/Hako/Hako/Instances/InstanceStorage.swift` | `InstanceStorage`, `InstanceStore` | Файловые пути, создание, транзакционное редактирование, дублирование и удаление |
| `macos/Hako/Hako/AppDataLocation.swift` | `AppDataLocation.storeURL` | Перенос прежнего store в `Application Support/Hako` и глобальных параметров |
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

### Язык интерфейса

`UserDefaults.standard`, ключ `appLanguage` (`AppLanguage.storageKey`), значения `ru` и `en`.
Пока ключа нет, язык выбирается по первому языку системы. Не зависит от аккаунта и не удаляется при выходе —
см. [[UI/Localization]].

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
| `InstanceStore.managementBlockedReason(_:) -> String?` | Причина запрета удаления, дублирования и резервной копии: очередь/установка, `launchBusy`, `contentBusy` |
| `InstanceStore.delete(_:) throws` | Стирает папку сборки без корзины и удаляет `GameInstance`; при ошибке сохранения возвращает папку и запись |
| `InstanceStore.duplicateName(for:) throws -> String` | Первое свободное имя «Имя 2», «Имя 3»… (до 999), основа укорачивается до 60 символов |
| `async InstanceStore.duplicate(_:) throws -> GameInstance` | Копирует папку готовой сборки и создаёт профиль с теми же параметрами, новым UUID и `createdAt` |
| `async InstanceStore.backup(_:content:) throws -> URL` | Создаёт `.hakobackup` в `~/.hako/backups` ([[Data/Backups]]) |

Локальные миры и их датапаки не являются SwiftData-моделями: читаются из `minecraft/saves`,
датапаки копируются в папку выбранного мира ([[Minecraft/Worlds]]).

## Зависимости и взаимодействия

- [[UI/ContentView]] читает аккаунты через `@Query`.
- `LoginView.signIn()` после успешного [[Auth/MicrosoftAuth]] сохраняет токены
  (`TokenKeychain.save`), затем вставляет `Account` (с Minecraft, если он доступен) и вызывает `PlaytimeCoordinator.transferGuest(to:)`.
  Данные входа и перенос гостевой статистики сохраняются одной операцией; при ошибке вставленный аккаунт удаляется.
- `MinecraftSessionCoordinator.connect` читает токены, сохраняет обновлённый refresh token и,
  при успехе, токен Minecraft, затем `account.connect(_:)` и `modelContext.save()` ([[UI/Launcher]]).
- `ProfileView.signOut()` отменяет общую Minecraft-сессию, вызывает `TokenKeychain.delete`, удаляет `Account` и сохраняет контекст.

## Ограничения и важные детали

- `InstanceStore` и `PlaytimeCoordinator` удерживают `ModelContainer` вместе с `ModelContext`.
  Фоновые операции индексации и callback завершения игры могут пережить создавшее их окружение;
  освобождение контейнера раньше этих операций уничтожает доступные им модели SwiftData.
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
  Store всегда лежит в `Library/Application Support/Hako/default.store`: если его нет, `AppDataLocation` копирует
  (с `-wal`/`-shm`, оригинал остаётся) прежний store — из `hako.modelStorePath`, затем из
  `Library/Containers/com.Launcher.Hako/Data/Library/Application Support`, затем из `Library/Application Support/default.store`.
  Копия вне контейнера нужна, потому что бандл без подписи разработчика (ad-hoc сборка CI) не может открыть чужой контейнер
  (SQLite 23, `Operation not permitted`) и падал бы при запуске; перенос выполняет сборка, которой контейнер доступен
  (подписанная командой разработчика). Ключ `hako.modelStorePath` больше не используется и удаляется. Ошибка доступа к прежнему store
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
общий `contentBusy` также блокирует файловое переименование, запуск и повторную установку. `InstanceStore.updateSettings`
автоматически сохраняет иконку и параметры отдельно от черновика имени. `InstanceStore.update` поддерживает
транзакционное обновление всех полей.
Удаление, дублирование и резервная копия запрещены для очереди/установки, подготовки/работы игры
и файловых операций; дублирование и копия требуют `ready`, удалить можно и `failed`/`paused`.
`InstanceStore.delete` переносит папку в `~/.hako/.delete-{UUID}`, удаляет запись и сохраняет контекст;
после успеха временная папка стирается в фоне, при ошибке папка и запись восстанавливаются.
Отсутствующая папка не мешает удалить запись. Время игры удалённой сборки сохраняется.
`InstanceStore.duplicate` ставит `contentBusy`, вне главного потока копирует папку в `~/.hako/.duplicate-{UUID}`
(`InstanceStorage.copyInstance`, на APFS — клонирование) без `minecraft/.hako-running.json`, после копирования
выбирает имя и перемещает папку на место. Профиль копии переносит параметры, иконку, offline-режим,
загрузчик, Fabric, Java и состояние `ready`; время игры копии начинается с нуля. Ошибка удаляет скопированные файлы.
Имена `backups` и `worlds` зарезервированы за папками резервных копий сборок и миров ([[Data/Backups]]).
`InstanceStorage.directory` запрещает symlink самого каталога сборки, чтобы одна сборка не писала в другую.
`containedURL` запрещает traversal и выход через симлинки. `allocatedSize()` суммирует фактически
занятый объём обычных файлов без повторного учёта ссылок; сканирование выполняется вне главного потока.

`InstanceStorage.usage()` (`macos/Hako/Hako/Instances/InstanceStorageUsage.swift`) за один обход `~/.hako` с теми же
правилами подсчёта раскладывает объём по `InstanceStorageUsage.Category` по пути относительно корня:
`backups/` → резервные копии сборок, `worlds/` → резервные копии миров;
`<сборка>/java` → Java; `<сборка>/minecraft/…`: `saves/<мир>/datapacks` и
`saves/<мир>/.hako-disabled-datapacks` → датапаки, остальной `saves` → миры, `mods` → моды, `resourcepacks`/`texturepacks`
(и их `.hako-disabled-*`) → ресурспаки, `screenshots` → скриншоты, всё прочее (в том числе `icon.png` и временные папки) → файлы Minecraft.
Датапаки не входят в категорию миров. Результат используется карточками «Хранилища» в настройках.
Тесты: `InstanceStorageUsageTests`.

## Загрузчик сборки

`GameInstance.modLoaderRaw` хранит `vanilla` (значение по умолчанию для прежнего store) или `fabric`.
`fabricConfigurationData` — Codable-снимок выбранного Loader и необязательного закреплённого Fabric API: project/version ID,
версия, канал, URL, имя, размер, SHA-1 и SHA-512. `fabricProfileSHA1` хранит контрольную сумму установленного профиля.
`InstanceDraft` переносит конфигурацию, а `InstanceStore.create` проверяет и сохраняет её до постановки в очередь.
Редактирование параметров не меняет загрузчик. Неизвестный сохранённый загрузчик при чтении конфигурации даёт ошибку.

## Происхождение модов и паков

`Instances/InstanceContent.swift` хранит `ModRegistry` в `minecraft/.hako-mods.json`: флаг первоначальной
установки API и словарь `ModOrigin` по имени JAR без `.disabled`, без учёта регистра. Запись содержит источник,
project/version ID, страницу, SHA-512 и закреплённый descriptor API. Необязательные `versionNumber`,
`iconURL` и `fileType` совместимы с прежними записями. Файлы без подтверждённого хеша локальные.
Локальный импорт снимает внешнее происхождение даже при одинаковых байтах; внешнее редактирование также
переводит файл в локальный. Включение определяется именем файла, отдельного флага в реестре нет.
Реестр перемещается вместе со сборкой. Повреждённый реестр не переписывается молча; список доступен
без происхождения и удалённых обновлений. Файловые замены и реестр публикуются с откатом при обычной ошибке.

Та же схема хранит ресурспаки в `minecraft/.hako-resourcepacks.json` (включая старые `texturepacks`)
и датапаки в `minecraft/saves/<мир>/.hako-datapacks.json`. Ключ — имя ZIP без учёта регистра;
отключение переносит файл в `.hako-disabled-*` и сохраняет происхождение. Папки паков остаются локальными.
Установка и обновление из Modrinth записывают источник, номер версии и иконку; проверка обновлений
распознаёт прежние ZIP по хешу и сохраняет сведения. Связанные ресурспаки датапаков сохраняют тип файла
для безопасного выбора обновления ([[Minecraft/Modrinth]]). SwiftData-схема не меняется.

## Windows-клиент

Хранение в `windows/Hako.Core/` (без WinUI). Microsoft-аккаунт — зашифрованный `session.dat`, отдельный Hako-аккаунт — `hako-session.dat`, настройки —
`settings.json`, профили сборок — `instances.json`. `HakoPaths` задаёт игровой корень, каталог данных и кэш;
производственный игровой корень — `%USERPROFILE%\.hako`, каталог данных по-прежнему задаёт
`windows/Hako/AppDataDirectory.cs`. Экраны и вход — [[UI/Screens#Windows-клиент]],
[[Auth/MicrosoftAuth#Windows-клиент]].

### Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `windows/Hako.Core/AccountSession.cs` | `AccountSession` | Запись аккаунта |
| `windows/Hako.Core/AccountSessionStore.cs` | `AccountSessionStore`, `AccountStoreException` | Шифрование, чтение, атомарная запись и удаление `session.dat` |
| `windows/Hako.Core/HakoSessionStore.cs`, `HakoAuthContracts.cs` | `HakoSessionStore`, `HakoSession` | Отдельная DPAPI-сессия Hako в `hako-session.dat`; формат Microsoft не меняется |
| `windows/Hako.Core/JsonUserSettingsStore.cs`, `UserSettings.cs`, `UserSettingsService.cs` | `JsonUserSettingsStore`, `UserSettings`, `UserSettingsService` | `settings.json`: язык и глобальные параметры игры; единый сериализованный снимок |
| `windows/Hako.Core/HakoPaths.cs`, `HakoRootLock.cs` | `HakoPaths`, `HakoRootLock` | Пути корней, изолированные запуски и блокировки game-root/data-root |
| `windows/Hako.Core/Instances/GameInstance.cs` | `GameInstance`, `InstanceDraft`, `InstanceParameters` | Полный JSON-профиль сборки Windows |
| `windows/Hako.Core/Instances/InstanceStorage.cs`, `InstanceStore.cs` | `InstanceStorage`, `InstanceStore`, `InstanceRegistryFile` | Безопасные пути, `instances.json`, транзакционное создание и управление профилями |
| `windows/Hako.Core/Instances/InstanceScreenshots.cs`, `InstanceFileTree.cs` | `InstanceScreenshots`, `InstanceFileTree` | Безопасный список PNG и корзина скриншотов; проверяемое копирование дерева и потоковая запись ZIP |
| `windows/Hako.Core/Instances/InstanceBackup.cs`, `WorldBackup.cs`, `BackupModels.cs`, `BackupJsonConverters.cs` | `InstanceBackup`, `WorldBackup`, `BackupResult` | Форматы Windows `.hakobackup`/`.hakoworld`, типизированные ошибки, Swift-совместимые даты |
| `windows/Hako.Core/Instances/InstanceContent.cs` | `InstanceContent`, `InstanceContentTarget`, `ModRegistry`, `ModOrigin`, `InstanceContentItem` | Реестры модов/ресурспаков/датапаков мира, локальные импорт, включение и замена; установка Fabric API |
| `windows/Hako.Core/Instances/InstanceContentController.cs` | `InstanceContentController`, `InstanceContentState`, `InstanceContentStateKey`, `ContentConfirmationRequest` | Core-оркестрация локального контента, выбранного мира, зависимостей Modrinth и обновлений; подтверждения без WinUI |
| `windows/Hako.Core/Minecraft/FabricModels.cs`, `FabricClient.cs`, `ModrinthClient.cs` | `FabricConfiguration`, `FabricProfile`, `FabricVersionPredicate`, `FabricClient`, `ModrinthClient` | Fabric Meta; поиск и версии Modrinth, распознавание SHA-512, кэш и проверяемая потоковая загрузка |
| `windows/Hako.Core/JsonContexts.cs` | `HakoJsonContext` | Source-generated JSON-контракты Hako, Fabric, контента Modrinth и его кэша |
| `windows/Hako.Core/Playtime/PlaytimeModels.cs`, `PlaytimeStorage.cs`, `PlaytimeCoordinator.cs` | `PlaytimeLedger`, `PlaytimeLedgerStore`, `PlaytimeCoordinator` | Общий журнал игрового времени Windows, проверяемые сессии и восстановление гостевого переноса |
| `windows/Hako.Core/RecycleBin.cs` | `RecycleBin`, `IFileOperationProgressSink` | Перемещение в Корзину и подтверждённое безвозвратное удаление через API |
| `windows/Hako.Core/AppCoordinator.cs` | `AppCoordinator.InitializeAsync`, `SetLanguageAsync`, `LogoutAsync` | Загрузка при старте, изменение языка через общий сервис настроек, выход |
| `windows/Hako/Program.cs`, `App.xaml.cs` | `Program.Main`, `App` | Проверка аргументов до `AppInstance`, выбор путей, блокировка корня и сервисы приложения |
| `windows/Hako/AppDataDirectory.cs` | `AppDataDirectory.GetLocalPath` | Каталог данных |
| `windows/Hako.Tests/StorageTests.cs` | — | DPAPI без открытого текста токенов, очистка, повреждённый файл, сбой замены |
| `windows/Hako.Tests/InstanceStoreTests.cs`, `UserSettingsServiceTests.cs`, `RecycleBinTests.cs` | — | Изоляция путей, файловые транзакции, настройки, блокировка и корзина |
| `windows/Hako.Tests/ModrinthTests.cs` | — | Фасеты, совместимость версий, распознавание, кэш и проверяемые загрузки Modrinth |
| `windows/Hako.Tests/InstanceContentTests.cs`, `InstanceDatapackContentTests.cs` | — | Реестры и локальные операции модов/паков/датапаков, проверка `pack.mcmeta`, замена, rollback и reparse points |
| `windows/Hako.Tests/InstanceContentControllerTests.cs` | — | Зависимости Modrinth, world-scoped состояния, подтверждения, выбор связанных паков, отмена, обновления и lease |
| `windows/Hako.Tests/RedactionTests.cs` | — | `ToString()` записей `MicrosoftToken` и `AccountSession` без токенов |

### Аккаунт: `session.dat`

- Путь — `session.dat` в каталоге данных. Содержимое — JSON `AccountSession` (camelCase), зашифрованный
  `ProtectedData` с `DataProtectionScope.CurrentUser` и энтропией `Hako.AccountSession.v1`.
- Поля `AccountSession`: `Xuid`, `Gamertag`, `Email`, `XboxAvatarUrl`, `MinecraftProfileId`, `MinecraftName`,
  `MicrosoftRefreshToken`, `MinecraftAccessToken`, `MinecraftTokenExpiresAt`.
- `AccountSession.ToString()` не выводит `MicrosoftRefreshToken` и `MinecraftAccessToken`.
- `LoadAsync`: файла нет → `null`. Ошибка расшифровки или JSON, либо пустые `Xuid`, `Gamertag` или
  `MicrosoftRefreshToken` → `AccountStoreException`. Файл при этом не удаляется. `AppCoordinator.InitializeAsync`
  показывает `Welcome` с `StartupAccountWarning`; следующий успешный вход перезапишет файл.
- `SaveAsync(account, commitIfCurrent)`: пишется `session.dat.<guid>.tmp` (`FileShare.None`, `Flush(true)`),
  затем `File.Move(tmp, session.dat, overwrite: true)` выполняется внутри `commitIfCurrent`. Временный файл
  удаляется в `finally`. Ошибки ввода-вывода и криптографии → `AccountStoreException`.
- `ClearAsync(commitIfCurrent)`: `File.Delete(session.dat)` выполняется внутри `commitIfCurrent`.
- Не Windows: `PlatformNotSupportedException`.

### Аккаунт Hako: `hako-session.dat`

- Путь — `hako-session.dat` в каталоге данных. Содержимое — JSON `HakoSession` (camelCase), зашифрованный
  `ProtectedData` с `DataProtectionScope.CurrentUser` и отдельной энтропией `Hako.Session.v1`.
- Хранятся Hako session token, account username/id/created time, device name, expiry и последнее подтверждённое время.
  `HakoSession.ToString()` скрывает токен; token и одноразовый exchange secret не входят в `AccountSession`.
- Запись идёт во временный `hako-session.dat.<guid>.tmp` и атомарно заменяет целевой файл только внутри generation-guarded
  commit callback. Отмена или устаревшее поколение не фиксирует сессию; временный файл удаляется в `finally`.
- `/me` обновляет account/device name и expiry в этом же файле. `401` очищает только Hako-сессию; ошибки сети,
  таймауты, `429` и `5xx` сохраняют кэш.
- Hako-dev использует отдельный `%LOCALAPPDATA%\Hako-dev\Data`; production-файл не копируется и не читается.
  Подробнее о протоколе — [[Auth/HakoAuth]].

### Настройки: `settings.json`

- Файл `settings.json` в каталоге данных. `UserSettings` хранит `language` и optional-поля `javaPath`,
  `javaArguments`, `minecraftArguments`, `fullscreen`, `windowWidth`, `windowHeight`, `maximumMemoryMiB`
  (camelCase, с отступами, `null` не пишется). Старый файл только с `language` читается без миграции.
- Отсутствующий `language` или файла нет — язык системы (`AppLanguages.Default`).
- `LoadAsync`: файла нет → `null`. Ошибки чтения и `JsonException` пробрасываются; `AppCoordinator.InitializeAsync`
  ставит `StartupNotice.PreferencesUnavailable` и берёт язык по умолчанию (`AppLanguages.Default`).
- `SaveAsync`: запись в `settings.json.<guid>.tmp`, затем `File.Move(..., true)`; временный файл удаляется в `finally`.
- `UserSettingsService` держит единый снимок языка и игровых параметров. Изменения языка и параметров используют общий
  `SemaphoreSlim`, и каждая запись сохраняет полный актуальный снимок. Если первоначальная загрузка завершилась ошибкой,
  перед следующей записью сервис повторяет чтение и объединяет выбранные изменения с данными на диске; при повторной ошибке
  ввода-вывода запись не выполняется, а при `JsonException` повреждённый файл заменяется полным текущим снимком.
  `AppCoordinator.SetLanguageAsync` меняет язык сразу; при ошибке он возвращается к языку из
  последнего успешно сохранённого снимка, если после этого не было нового выбора. Ошибка пробрасывается, UI показывает
  `LanguageSaveError`.

### Профили сборок Windows

- `instances.json` находится в игровом корне и имеет формат `{ "formatVersion": 1, "instances": [...] }`.
  `HakoJsonContext` пишет camelCase; значения источника аргументов, состояния установки и загрузчика — `global`/`mojang`/`custom`,
  `queued`/`installing`/`paused`/`ready`/`failed`, `vanilla`/`fabric`. В записи сохраняются параметры сборки, offline-ник,
  конфигурация Fabric, состояние паузы, Java, совместимость текстурпаков и архитектура.
- `FabricConfiguration` хранит `loaderVersion` и optional `api`; в API-дескрипторе JSON-ключи `projectID`, `versionID`,
  `version`, `channel`, `filename`, `url`, `size`, `sha1`, `sha512` совпадают со Swift. После установки SHA-1 точного
  профиля из Fabric Meta сохраняется в `fabricProfileSHA1`; профиль расположен в `minecraft/.hako-fabric.json`.
  Временные и API JSON-типы зарегистрированы в source-generated контекстах, а сохранённые optional-поля `null` опускаются.
- `InstanceStore.LoadAsync` асинхронно загружает реестр при старте. `Instances` и `List()` возвращают снимок в памяти;
  успешная фиксация записи обновляет его. Ошибка чтения доступна в `LoadError`; повреждённый или неизвестный формат
  остаётся нетронутым, но не блокирует открытие главного окна. Операции создания и изменения профилей блокируются
  `InstanceRegistryException` с кодом ошибки и ключом локализации (`InstanceRegistryDamaged` или
  `InstanceRegistryUnavailable`). Реестр пишется во временный файл, сбрасывается на диск и публикуется через `File.Move`.
- Имя сборки — 1–60 ASCII-букв, цифр и пробелов после trim; пробелы в имени папки становятся `_`. `backups`, `worlds`
  и имена устройств Windows зарезервированы. Коллизии в реестре и файловой системе проверяются без учёта регистра.
  `InstanceStorage.ContainedPath` отклоняет traversal, абсолютные пути, symlink и reparse points ниже игрового корня;
  сам корень может быть junction или symlink, например когда `.hako` вынесена на другой диск.
- Создание эксклюзивно создаёт каталог профиля и только его удаляет при ошибке. Переименование переносит каталог,
  использует временное имя при смене регистра и откатывает перенос при ошибке записи. Удаление сначала переносит каталог
  в `.delete-{instanceId:N}-{randomId:N}`, затем сохраняет реестр и очищает staging в фоне. После публикации снимка
  `LoadAsync` в фоне удаляет только staging-имена этого формата и прежние `.delete-{GUID}` в форматах N/D;
  `.delete-whatever` остаётся нетронутым. При обнаружении нового staging с записью реестра и отсутствующей исходной папкой
  запись атомарно удаляется; если папка уже на месте, staging удаляется без изменения записи. Очистка не переходит по
  вложенным junction и symlink. Сбой очистки записывается в Debug-вывод и повторяется при следующей загрузке.
  При ошибке фиксации реестра каталог восстанавливается. `TryBeginLaunch` и `TryBeginContent` выдают одноразовые
  `IDisposable`-leases; занятость снимается только при Dispose. Начало операций и блокировки Delete/Rename/Update
  сериализованы общим `_gate`; состояния очереди/установки также блокируют эти операции.
- `InstanceStore.DuplicateAsync` требует готовую и свободную сборку. Он копирует её в эксклюзивный staging рядом
  с целевой папкой под content-lease, затем атомарно публикует папку и профиль с новым UUID/датой; сбой записи
  реестра откатывает только папку, созданную этой операцией. Имена вида «Имя 2», «Имя 3» подбираются без учёта
  регистра. Fabric, параметры, иконка, происхождение и completed-план модпака сохраняются; токены и transient
  файлы запуска (`.hako-running.json`, `.hako-launch`, `.hako-game`, журнал `hako-launch.log`) исключены.
- `InstanceScreenshots.ListAsync` читает только обычные видимые PNG в корне `minecraft/screenshots`;
  отсутствующая папка остаётся отсутствующей. Перемещение в Корзину перепроверяет владение и тип каждого файла
  и возвращает частичные статусы. Отдельный screenshot-lease блокирует установку и файловые операции, но разрешает
  удаление скриншотов во время игры, как на macOS.
- `minecraft/.hako-mods.json` повторяет Swift-контракт: `{ "apiProvisioned": bool, "files": { "<lowercase JAR>": ModOrigin } }`.
  `source` записывается как `local`, `modrinth` или `curseForge`; ключи Origin — camelCase, включая `projectID`, `pageURL`,
  `sha512`, `api`, `versionNumber`, `iconURL`, `fileType`. Необязательные null-поля не записываются. Реестр читается и
  пишется через `HakoJsonContext`, публикуется атомарно; повреждённый файл, каталог или reparse point не заменяется.
  `InstanceContent.PublishJarAsync` берёт content-lease, а установка использует lease очереди, поэтому пользовательская
  публикация и установка сериализованы. `apiProvisioned` не сбрасывается при удалении/отключении API пользователем.
- `InstanceContent.ListAsync`, `ImportAsync`, `SetEnabledAsync`, `ReplaceAsync` и `TrashAsync` — локальные операции этапа 6a.
  Каждая публичная мутация берёт `InstanceStore` content-lease; внутренний `WithContentLeaseAsync` позволяет будущему
  контроллеру удерживать один lease для нескольких публикаций без повторного захвата. Моды находятся в
  `minecraft/mods`; отключённый JAR остаётся там же с суффиксом `.disabled`. Ресурспаки находятся в `resourcepacks`
  или в старой папке `texturepacks`; ZIP остаются файлами, папки принимаются только с обычным `pack.mcmeta`.
  Отключённые пакеты переносятся в `.hako-disabled-resourcepacks` или `.hako-disabled-texturepacks` соответственно.
  Импорт локального файла/папки удаляет его прежнее происхождение, ключи файлов в реестрах всегда lowercase.
- `minecraft/.hako-resourcepacks.json` использует ту же форму `{ "apiProvisioned": bool, "files": { ... } }` и поля
  `ModOrigin`, что и реестр модов; флаг `apiProvisioned` сохраняет совместимость, хотя для паков он не используется.
  Замена требует ожидаемый SHA-512 текущего файла и сохраняет его enabled/disabled-состояние. При ошибке публикации
  новый файл удаляется и старый восстанавливается независимо; если откат не завершён, исходная и ошибки отката
  включаются в исключение, а оставшаяся резервная копия не удаляется и указывается в сообщении. Перемещение в Корзину
  удаляет запись происхождения только при статусе `Recycled`;
  отказ или частичная ошибка оставляет реестр нетронутым. Папки паков локальные, их origin не записывается.
- Датапаки мира хранятся в `minecraft/saves/<worldId>/datapacks`, отключённые — в
  `minecraft/saves/<worldId>/.hako-disabled-datapacks`. Реестр `.hako-datapacks.json` лежит в папке мира и содержит
  `files` с ключами lowercase ZIP-имён и тем же `ModOrigin` (Swift-ключи `projectID`, `versionID`, `pageURL`, `sha512`,
  `versionNumber`, `iconURL`, `fileType`). `apiProvisioned` для этого реестра необязателен при чтении и записывается
  Core при сохранении; это сохраняет совместимость с Swift. Реестр повреждённого формата не перезаписывается. ZIP
  требует один обычный корневой `pack.mcmeta` размером до 1 МиБ с JSON object `pack`; архив ограничен 100 000 entries,
  ZIP копируется как файл, папка обходится без перехода по reparse points. Создание datapacks leaf допускается только
  внутри существующего проверенного мира; missing world/level metadata не восстанавливается.
- Реестры повреждённого формата не перезаписываются. Имена проверяются как Windows path segments; импорт и чтение
  папок паков отклоняют reparse points и junction внутри дерева. Поиск записей использует сравнение без учёта регистра,
  чтобы включение, отключение или замена не создали вторую запись того же логического файла. Для обновления проверяется
  ожидаемый старый SHA-512; disabled-состояние и origin сохраняются, публикация и реестр откатываются вместе при ошибке.
- `InstanceContentTarget` предоставляет `.Mods`, `.Packs` и `.WorldDatapacks(worldId)`. Overload-ы `InstanceContent`
  и `InstanceContentController` по старому `ModrinthContentKind` остаются для модов и ресурспаков; datapack-kind без
  явной папки мира отклоняется. Локальные и Modrinth-операции мира входят в Core этапа 7b; Windows UI подключает
  их из профиля по UUID сборки и имени папки мира. Удаление отдельных датапаков не предоставляется.
- `InstanceContentController` — Core-only контроллер этапов 6b/7b; persisted-схема реестров не меняется. `InstalledProjectsAsync`
  группирует записи происхождения и распознаёт остальные файлы по SHA-512, включая отключённые. Узнанный вручную файл
  получает `ModOrigin` под content-lease только после повторной проверки ожидаемого SHA-512. `RecordOriginUnderLeaseAsync`
  обновляет только JSON-реестр и не меняет файл. Обновления связанных ZIP ресурспаков берут совместимую datapack-версию
  и сохраняют `fileType` в origin.
- Снимок `InstanceContentState` адресован `InstanceContentStateKey(instanceId, kind, worldId)`; для модов и ресурспаков
  `worldId` null, для датапаков это ID папки выбранного мира. Так ошибки, обновления, busy, текущий проект и revision
  изолированы между мирами. `StateChanged` не маршалит на UI-поток.
  При обновлении с коллизией подтверждённый файл сохраняется в `.hako-backup-<UUID>` до commit общего registry;
  до commit ошибки/отмена восстанавливают старый файл и коллизию независимо. После commit коллизия отправляется
  в корзину без caller cancellation. При отказе Shell новая версия и registry остаются опубликованы, backup
  сохраняется, `ContentOperationFailed.Arguments["diagnostic"]` сообщает путь для восстановления; JSON-форматы не меняются.
  Обработчик WinUI этапа 6c должен публиковать снимок на `DispatcherQueue`. Подтверждения передаются callback-ом через
  `ContentConfirmationRequest` с enum-кодами и string-аргументами для ru/en ресурсов; сам Core не содержит UI-локализации.
  Для связанных ресурспаков датапака `ContentConfirmationRequest.Files` передаёт имя, `fileType` и required/optional.
  Additive `ContentConfirmationSelectionCallback` возвращает имя выбранного ZIP; прежний callback с `Primary` сохраняет
  поведение и выбирает первый вариант. `Alternative` продолжает без связанного ресурспака, `Cancel` останавливает операцию
  до изменений. `WorldUnavailable` — типизированная ошибка отсутствующего/изменившегося мира для UI 7c.
  Callback получает `CancellationToken`, операции не блокируют поток UI и удерживают один content-lease без повторного
  захвата через `InstanceContent.*UnderLeaseAsync`. Windows UI подключён через app-lifetime `LauncherServices`:
  `InstanceContentView` и `ModrinthCatalogView` применяют актуальный снимок по UUID/kind на `DispatcherQueue`, сверяя
  `Revision`; уход со страницы отменяет её чтение и поиск, но не уже начатую установку. Общий `HakoDialogs` сериализует
  `ContentDialog` одного окна и завершает ожидающее подтверждение отменой при закрытии окна. `UpdateAllAsync` подтверждения не показывает: такие элементы получают
  `ConfirmationRequired`, ошибки собираются по файлам, дальнейшие обновления продолжаются. Публичное подключение контроллера
  не меняет JSON-реестры; профиль Windows подключает миры, датапаки и их резервное копирование без изменения
  этих сохранённых контрактов.
- Кеш хешей Modrinth находится в `%LOCALAPPDATA%\Hako\Cache\Modrinth\files.json` (или в инжектируемом
  `HakoPaths.CacheRoot`); ключ файла содержит нормализованный путь, размер и время изменения. Удалённые пути и
  неиспользуемые версии очищаются при сохранении, неизвестные хеши живут только в памяти клиента. Клиенты в одном
  процессе с одинаковым путём используют общую блокировку и перечитывают последний снимок до сохранения; разные корни
  не блокируют друг друга. Межпроцессная синхронизация не применяется.
- `HakoRootLock` удерживает lock-файлы в игровом и data-root с `FileShare.None` до завершения приложения; lock data-root
  защищает `session.dat` и `settings.json` от другого экземпляра. Сам game-root может быть junction или symlink. Только
  sharing/lock violation считается уже запущенным экземпляром; прочие ошибки остаются обычными ошибками старта. При конфликте
  показывается локализованное сообщение из `Resources.resw`.
- `RecycleBin` применяет `IFileOperation` с `FOF_ALLOWUNDO` и `FOFX_RECYCLEONDELETE`; progress sink возвращает `E_ABORT`,
  когда Shell не указал `TSF_DELETE_RECYCLE_IF_POSSIBLE`. Результат пакетного удаления содержит статус каждого элемента:
  `Recycled`, `NotRecyclable`, `Failed` с сообщением об ошибке или `Cancelled`; после частичного успеха операция не теряет
  уже полученные результаты. Успех Shell подтверждается только при `SUCCEEDED(hr)` и ненулевом `newItem`; иначе элемент
  помечается `Failed`, поскольку он мог удалиться, и нужно проверить его наличие. `PermanentDelete` требует абсолютный путь
  и обязательный `allowedRoot`, принимает только вложенный путь без прохождения через reparse points и удаляет конечную
  ссылку без перехода по ней. Для длинных Win32-путей приложение помечено `longPathAware`; Shell получает обычное абсолютное
  parsing name, поскольку пространство `\\?\` предназначено для файловых API и не разбирается Shell как parsing name.
  COM-обёртки source generation освобождаются через `ComObject.FinalRelease`: после `Unadvise` освобождаются элементы,
  затем операция, и только после этого вызывается `CoUninitialize`.

### Пути и изоляция Windows

- `HakoPaths` объединяет игровой корень, каталог данных и кэш. В production это `%USERPROFILE%\.hako`, текущий каталог
  `AppDataDirectory.GetLocalPath()` и `%LOCALAPPDATA%\Hako\Cache`.
- `Program.Main` проверяет `--hako-root <path>` и `--data-root <path>` до `AppInstance`. Для изоляции требуются оба
  абсолютных пути, которые не могут содержать друг друга; ключ процесса — `Hako.Isolated.<sha256 корней>`. Ошибка разбора
  показывает локализованное сообщение и завершает запуск, не выбирая production-каталоги. `--profile-preview` создаёт
  уникальный временный корень и in-memory account/settings stores; при выходе удаляется только каталог, эксклюзивно
  созданный этим процессом.

### Каталог данных

`AppDataDirectory.GetLocalPath()` проверяет доступность `Package.Current.Id`:

- пакетный запуск → `ApplicationData.Current.LocalFolder.Path`;
- `InvalidOperationException` (unpackaged) → `%LOCALAPPDATA%\Hako`.

Следствия:

- Аккаунт не переносится между MSIX-запуском и unpackaged-запуском: файлы лежат в разных каталогах
  (см. `AppDataDirectory.GetLocalPath` выше).
- Гостевой вход (`AppCoordinator.IsGuest`) хранится только в памяти: после перезапуска приложение возвращается
  к `Welcome` (тест `GuestDoesNotPersistAndRestartReturnsToWelcome`).
- `MinecraftProfileId`, `MinecraftName`, `MinecraftAccessToken` и `MinecraftTokenExpiresAt` — состояние Minecraft-сессии
  в `AccountSession`. `MinecraftSessionCoordinator.ConnectForLaunchAsync` переиспользует access token, если до истечения
  осталось больше минуты; иначе `MicrosoftDeviceAuth.ConnectMinecraftAsync` обновляет сессию через Microsoft refresh token.
  Ротированный refresh token сохраняется до Xbox/XSTS/Minecraft-запросов, а профиль, access token и срок действия
  записываются через `commitIfCurrent`. Параллельные запросы используют один refresh; смена поколения аккаунта и выход
  отменяют его. Подробный контракт — [[Auth/MicrosoftAuth#Обновление сессии для онлайн-запуска]]. Выход удаляет запись целиком.

### Решения и основания

- DPAPI и файлы вместо `ApplicationData.Current`: `ApplicationData.Current` падает в unpackaged-запуске
  (причина из плана пользователя).

### Игровое время Windows

- Общие данные лежат в `HakoPaths.GameRoot/.hako-playtime/` как в MSIX, так и в unpackaged-запуске;
  зашифрованный `session.dat` и настройки остаются в отдельном `DataRoot`. Поэтому игровые счётчики общие между
  вариантами запуска, даже когда каталог данных аккаунта различается.
- `playtime.json` хранит счётчики владельцев `account:<XUID>` и `guest`, независимые суммы по UUID сборок,
  уже начисленное время сессий, ожидающий перенос и маркеры применённых переносов. `sessions/<UUID>.json`
  хранит проверяемое состояние сессии; рядом используется эксклюзивный `.lock`. Записи типизированы в
  `HakoJsonContext`, ограничены по размеру и публикуются атомарной заменой; межпроцессная запись ledger
  дополнительно сериализована file lock. Перед чтением и созданием файлов проверяется вся цепочка родителей,
  существующий `GameRoot`, каталог ledger и конечные ledger/lock/temp: reparse points отклоняются;
  при отсутствии `GameRoot` хранилище его не создаёт.
- Вход выполняет двухфазный перенос. До атомарной записи `session.dat` ledger фиксирует `pendingTransfer` с целевым
  XUID и точными значениями гостевых счётчиков/активных session IDs на границе. После успешной записи аккаунта одна
  атомарная ledger-транзакция добавляет только зафиксированные суммы к аккаунту, вычитает их у гостя и меняет владельца
  перечисленных активных сессий. Время, начисленное гостю после границы, остаётся гостевым. Повтор защищён ID переноса.
- Помощник держит session lock на время наблюдения, поэтому создание transfer boundary не ждёт его освобождения:
  атомарный JSON-снимок читается без записи, а текущий `QueryUnbiasedInterruptTime` задаёт elapsed активных сессий.
  Помощник продолжает checkpoints; следующая reconcile начисляет разницу уже по новому владельцу ledger-сессии.
  Финальный журнал удерживаемой переносом сессии сохраняется до успешного начисления и удаления сессии из ledger.
  Завершение процесса само по себе не завершает сессию: пока журнал не финализирован, фаза A сохраняет session ID,
  даже если helper ещё не опубликовал свою identity или последний checkpoint. Для уже завершившейся игры граница
  включает только сохранённый elapsed; поздний остаток получает владелец этой сессии после применения переноса
  (при отмене — прежний гость). Новые гостевые сессии этим переносом не затрагиваются.
  Отмена запуска после прикрепления процесса также проходит через reconcile и начисление, а не удаляет журнал напрямую.
  Повтор cleanup после ошибки удаления файла не начисляет время заново.
- При запуске незавершённый перенос применяется только если XUID совпадает с загруженным `session.dat`; иначе он
  удаляется без применения. Ошибка после подготовки или сохранения аккаунта оставляет достаточно данных для этой
  проверки/повтора. Выход отменяет ожидающий перенос; он не переписывает `session.dat` или DPAPI-формат.
- При удалении сборки её строка профиля и файлы удаляются обычным `InstanceStore`, но игровые суммы остаются в ledger.
  Неизвестные/потерянные журналы восстанавливаются по последней сохранённой контрольной точке и не начисляются повторно.

## План модпака

На macOS SwiftData-схема не расширяется. На Windows `ModpackPlan` сериализуется через `HakoJsonContext` и
хранится как `.hako-modpack.json` в корне папки сборки: версия формата 1, `projectID`, `versionID`,
`versionNumber`, `pageURL`, проверяемый файл архива, полный манифест и необязательный `completed` (по умолчанию
`false`). Отсутствие файла означает обычную сборку; повреждённый файл — ошибку, а не переход к обычной установке.
Windows `InstanceStore.Create(..., modpackPlan:)` сверяет Minecraft и Loader с черновиком, пишет план до
сохранения модели и удаляет только новую папку при ошибке.

У модпака Fabric-конфигурация содержит Loader без API; прежние JSON с API остаются читаемыми. Windows-очередь
читает план при повторе/перезапуске и сверяет закреплённые Minecraft и Loader. После `completed` исходный состав
больше не применяется. Переименование, дублирование, удаление и `.hakobackup` переносят план вместе с папкой;
архив и файлы лежат в проверяемом изолированном кэше `HakoPaths.CacheRoot/Modpacks` и при необходимости
скачиваются заново по закреплённым ссылкам.

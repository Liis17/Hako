# Architecture

Parent: [[Index]]

## Назначение и границы

Hako — нативное macOS-приложение: таргет `Hako` (bundle ID `com.Launcher.Hako`)
и отдельные таргеты Swift Testing `HakoTests` и CLI-помощника `HakoPlaytimeHelper`.
Windows-клиент — отдельный стек WinUI 3 в каталоге `windows/` ([[#Windows-клиент]]).

Назначение (со слов пользователя): лаунчер Minecraft с независимыми авторизациями через Microsoft
и Telegram/Hako и интеграцией каталогов Modrinth и CurseForge.

Текущее состояние: реализованы приветствие, независимый вход Microsoft по коду (device code flow) и Hako через Telegram
с получением профиля Xbox и, если доступен, профиля Minecraft, главная страница с рейлом вкладок
(карточки и профили сборок, настройки игры, хранилище, сведения о приложении, профиль с аккаунтами Xbox и Java,
редактор PNG-скина/плаща и анимированный 3D-скин) и выход.
Создание сборки запускает независимую установку Java и Minecraft из официальных API Mojang в фоне; для собственной Fabric-сборки дополнительно устанавливаются закреплённый Loader и проверенный Fabric API. Готовые модпаки Modrinth сохраняют версии и состав автора; API берётся только из пакета.
Установленные сборки запускаются онлайн или в offline-mode; гостевой вход открывает лаунчер без аккаунта.
Поддерживаются Vanilla и Fabric, локальные моды и Fabric API из официального проекта Modrinth. Каталог Modrinth устанавливает моды и ресурспаки в сборку и датапаки в выбранный локальный мир ([[Minecraft/Modrinth]]); каталог CurseForge не реализован. В macOS-приложении сторонних зависимостей (Swift Package Manager,
CocoaPods, Carthage не используются) нет; NuGet-пакеты Windows-клиента — в разделе [[#Windows-клиент]].
Игровое время macOS синхронизируется с Hako по закреплённым игровым сегментам; серверный итог, локальные счётчики и сохранённая очередь остаются отдельными состояниями.

## Стек

| Технология | Роль | Источник |
|------------|------|----------|
| Swift, режим языка 5.0 | Язык приложения | `macos/Hako/Hako.xcodeproj/project.pbxproj` (`SWIFT_VERSION`) |
| SwiftUI | UI и жизненный цикл приложения (`@main` `App`) | `macos/Hako/Hako/HakoApp.swift` |
| RealityKit / `RealityView` | Нативное 3D-превью скина | `macos/Hako/Hako/Minecraft/MinecraftSkinScene.swift`, `macos/Hako/Hako/Views/Launcher/MinecraftSkinView.swift` |
| SwiftData | Аккаунт, профили сборок и игровое время | `macos/Hako/Hako/HakoServices.swift`, `macos/Hako/Hako/Playtime/PlaytimeModels.swift` |
| AppKit, App Intents, Core Spotlight | Меню Dock, поиск сборок и запуск через Siri/Shortcuts | [[UI/SystemIntegration]] |
| UserDefaults / `@AppStorage` | Глобальные параметры игры, язык интерфейса | `macos/Hako/Hako/GameLaunchDefaults.swift`, `macos/Hako/Hako/Views/Launcher/SettingsView.swift`, `macos/Hako/Hako/AppLanguage.swift` |
| String Catalog | Русский и английский интерфейс | `macos/Hako/Hako/Localizable.xcstrings` |
| Security (Keychain) | Независимые токены Microsoft и сессия Telegram/Hako | `macos/Hako/Hako/Auth/TokenKeychain.swift`, `HakoSessionKeychainStore.swift` |
| URLSession | Авторизация Microsoft и Hako, синхронизация игрового времени Hako, Minecraft/Java, Fabric Meta/API, Modrinth, Minecraft Java Profile API и обновления | `macos/Hako/Hako/Auth/MicrosoftAuth.swift`, `HakoAuthAPIClient.swift`, `HakoPlaytimeAPIClient.swift`, `MinecraftProfileAPI.swift`, `macos/Hako/Hako/Minecraft/MojangClient.swift`, `macos/Hako/Hako/Minecraft/MinecraftInstaller.swift`, `macos/Hako/Hako/Minecraft/FabricClient.swift`, `macos/Hako/Hako/Minecraft/ModrinthClient.swift` |
| Xcode 27, macOS SDK | Сборка; минимальная ОС macOS 27.0 (`MACOSX_DEPLOYMENT_TARGET`) | `macos/Hako/Hako.xcodeproj/project.pbxproj` |

## Структура репозитория

| Путь | Содержимое |
|------|------------|
| `macos/Hako/Hako.xcodeproj` | Проект Xcode: таргеты `Hako`, `HakoPlaytimeHelper`, `HakoTests`; общая схема `Hako`, конфигурации `Debug` и `Release` |
| `macos/Hako/Hako/` | Исходники и ресурсы приложения |
| `macos/Hako/HakoTests/` | Swift Testing: скины, хранение и миграция сборок, метаданные Mojang, установка, параметры и процессы запуска, сессии, игровое время, импорт текстурпаков и каталог Modrinth |
| `macos/Hako/Hako/AppIcon.icon` | Иконка приложения в формате Icon Composer (`ASSETCATALOG_COMPILER_APPICON_NAME = AppIcon`): слой `Assets/box.png` (коробка с сакурой, 1024×1024, прозрачный фон, Liquid Glass включён) на градиенте от белого к светлой сакуре. Тёмный, прозрачный и тонированный варианты система строит сама. Открывается в Icon Composer из Xcode |
| `macos/Hako/Hako/Assets.xcassets` | `AccentColor` (цвет не задан) |
| `windows/Hako/` | Windows-клиент: WinUI 3-приложение, конфигурация пакета MSIX, страницы, `MainWindow`, точка входа, ресурсы `.resw`, решение `Hako.slnx` |
| `windows/Hako.Core/` | Логика Windows-клиента без WinUI: вход Microsoft, хранение аккаунта и настроек, язык, сборки (`Instances/`), установка и запуск Minecraft (`Minecraft/`) |
| `windows/Hako.Tests/` | MSTest-тесты `Hako.Core` и ресурсов Windows-клиента |
| `Obsidian/HakoVault/` | Эта база знаний |

## Компоненты

- `HakoApp` (`macos/Hako/Hako/HakoApp.swift`) — точка входа `@main`: получает `HakoServices.forCurrentLaunch()`
  (`macos/Hako/Hako/HakoServices.swift`) и создаёт единственную сцену `Window("Hako", id: "main")`.
  Обычный запуск использует `HakoServices.shared`; DEBUG preview-флаги создают отдельные контейнеры и
  координаторы. `HakoServices` владеет `ModelContainer`, `InstallationCoordinator`, `InstanceContentController`,
  `MinecraftSessionCoordinator`, `HakoAccountCoordinator`, `MinecraftAppearanceStore`, `MinecraftSkinLoader`,
  `MinecraftCharacterEditorCoordinator`, `GameLaunchCoordinator`, `PlaytimeCoordinator`,
  `InstanceRenameExitCoordinator`, `QuickLaunchCoordinator` и `AppUpdateCoordinator`. Интерфейс и системные
  команды обычного процесса используют те же объекты.
  Устройство контейнера описано в [[Data/Persistence]], быстрый запуск — в [[UI/SystemIntegration]].
- [[Data/Persistence]] — общая `HakoSchema` с аккаунтом, сборками, локальным игровым временем и сохранённой очередью Hako, контейнер хранилища, отдельные Keychain items Microsoft и Hako,
  глобальные параметры игры, независимые папки сборок и статистика времени по XUID.
- [[Minecraft/Installation]] — официальный каталог, нативная совместимость, независимая установка Java и игры.
- [[Auth/MicrosoftAuth]] — вход через Microsoft device code flow и общий сервис действительной Minecraft-сессии.
- [[Auth/HakoAuth]] — независимый вход через Telegram, отдельный Keychain item, проверка `/me` и выход.
- [[Minecraft/Launching]] — построение аргументов, проверка Java, процессы и восстановление игр.
- [[Minecraft/Worlds]] — локальные сохранения, чтение gzip/NBT через системный zlib, копирование, корзина и управление датапаками; `.hakoworld` описан в [[Data/Backups]].
- [[Minecraft/Modrinth]] — каталог модов, ресурспаков и датапаков мира, зависимости, проверка загрузок и создание сборок из `.mrpack`.
- [[App/Updates]] — стабильные релизы GitHub: проверка и загрузка DMG/MSIX, установка и перезапуск macOS- и Windows-клиентов.
- [[UI/ContentView]] — корень главного окна и настройки окна.
- [[UI/SystemIntegration]] — единые сервисы и быстрый запуск сборок из Dock, Spotlight и Siri.
- [[UI/Screens]] — экраны, фон с сакурой и общий стиль.
- [[UI/Launcher]] — главная страница: рейл вкладок, профиль, аватары, автоподключение Minecraft.
- [[UI/MinecraftSkin]] — модель скина, ходьба, вращение, загрузка и резервный Стив.
- [[UI/Localization]] — каталог строк, выбор языка и правила для новых строк.

## Основные потоки

### Запуск

1. `HakoApp` вызывает `HakoServices.forCurrentLaunch()`. В обычном режиме первый запрос к
   `HakoServices.shared` создаёт `modelContainer`: `HakoSchema.schema` + `ModelConfiguration(schema:url:)`
   с URL из `AppDataLocation` → `ModelContainer`. `LaunchSettingsMigration.run` сохраняет прежние режимы
   аргументов, фиксирует окна и переносит предел heap в ползунки. Ошибка создания контейнера или миграции
   завершает приложение через `fatalError`.
   В Debug при `--character-editor-preview` создаются отдельный in-memory контейнер, временная папка игровых данных,
   фиктивные токены и `URLSession` с `MinecraftCharacterPreviewFixture`; миграция не запускается, а
   production store и Keychain не изменяются. `--character-editor-preview-language=ru|en` задаёт язык только для preview.
   Подробная команда — [[UI/MinecraftSkin#PNG для редактора]].
   Debug `--playtime-profile-preview` открывает профиль Microsoft + Telegram/Hako с fixture API и очередью игрового времени;
   контейнер, временная папка и defaults изолированы, production Keychain/store/settings не используются.
2. `Window("Hako", id: "main") { ContentView() }` получает контейнер через
   `.modelContainer(services.modelContainer)`, который помещает `modelContext` в окружение окна.
3. Окно открывается в 1280×720 без восстановления прошлого размера. Пока Keychain-сессия Hako загружается,
   `ContentView` показывает состояние восстановления; затем показывает `LauncherView`, если в SwiftData есть
   Microsoft `Account`, восстановлена Hako-сессия либо выбран гостевой вход, иначе приветствие. Провайдеры
   независимы, гостевой выбор действует до закрытия Hako, сборки сохраняются независимо.
4. `HakoServices.start()` запускает восстановление Telegram/Hako через `HakoAccountCoordinator.initialize()`
   независимо от SwiftData-аккаунта. При успешном чтении launcher открывается немедленно и в фоне выполняется
   `/me`; повторная проверка идёт раз в сутки и при входе в профиль. Ошибка Keychain оставляет доступ к
   существующей Microsoft/guest-работе, показывает предупреждение и запрещает запись/удаление Hako-сессии.
   Восстановленный Hako-профиль передаётся в `PlaytimeCoordinator` для чтения серверного итога и повтора
   закреплённой очереди; запуск и восстановление игр не ждут сетевого ответа Hako.
5. `ContentView` наблюдает срок Minecraft-токена через `MinecraftSessionCoordinator.monitor` ([[Auth/MicrosoftAuth]]).
   `GameLaunchCoordinator.start` включает `PlaytimeCoordinator`, читает журналы сессий и восстанавливает
   работающие игры с независимым помощником учёта до запуска очереди установки.
   Общий координатор из окружения восстанавливает очередь независимо от аккаунта; сохранённая пауза остаётся паузой.
   Настройки окна и выбор экрана — в [[UI/ContentView]].

Источники: `macos/Hako/Hako/HakoApp.swift`, `macos/Hako/Hako/HakoServices.swift`, `macos/Hako/Hako/ContentView.swift`.

### Вход

1. Приветствие предоставляет отдельные действия Microsoft-входа, Telegram/Hako и гостевого запуска.
   Из гостевого профиля доступны оба provider-входа; отмена возвращает к предыдущему экрану.
2. `LoginView.signIn()` запрашивает код (`MicrosoftAuth.requestDeviceCode`) и показывает его;
   пользователь вводит код на microsoft.com/link.
3. `MicrosoftAuth.waitForToken` опрашивает Microsoft до подтверждения, `signIn` получает профиль Xbox
   и пробует Minecraft ([[Auth/MicrosoftAuth]]). Недоступный Minecraft не прерывает вход.
4. Токены сохраняются в Keychain под XUID, читаются игровые журналы, затем вставляется `Account`.
   `PlaytimeCoordinator.transferGuest(to:)` сохраняет аккаунт и перенос гостевого времени одной операцией ([[Data/Persistence]]).
5. `@Query` в `ContentView` видит аккаунт и переключает окно на `LauncherView`.

Для Hako `HakoLoginView` запрашивает регистрацию без username или вход с нормализованным Telegram username.
После пользовательского открытия или копирования Telegram-ссылки `HakoAccountCoordinator` проверяет статус
каждые две секунды до терминального состояния/срока запроса и однократно обменивает подтверждение. Полный
поток, deadline, отмена, безопасное сохранение, проверка и выход описаны в [[Auth/HakoAuth]]. Hako-сессия не
создаёт SwiftData `Account`; Hako-only запускает игру как гость/offline и не переносит гостевое время.

Источники: `macos/Hako/Hako/Views/LoginView.swift`, `macos/Hako/Hako/Auth/MicrosoftAuth.swift`.

### Выход

`ProfileView.signOut()` (вкладка «Профиль») удаляет запись токенов из Keychain и `Account` из SwiftData,
сохраняет контекст; `ContentView` возвращает приветствие. `GameInstance`, файлы сборок и очередь установки сохраняются.
Hako-выход независимо отзывает Hako-токен и удаляет только совпадающую запись Hako; при ошибке удалённого выхода
профиль предлагает retry, cancel или предупреждённое локальное удаление. Выход из одного провайдера оставляет
лаунчер открытым, пока подключён второй; последний выход возвращает приветствие, если не открыт архив восстановления.
Работающие игры продолжаются независимо от выхода из аккаунта и закрытия Hako.
`HakoPlaytimeHelper` продолжает измерять их длительность; общее время аккаунта хранится отдельно
от удаляемых данных входа и сборок. Завершённые Hako-сегменты попадают в локальную очередь и отправляются
только при доступности исходного Hako-профиля. Жизненный цикл и восстановление описаны в [[Minecraft/Launching]].

Источники: `macos/Hako/Hako/Views/Launcher/ProfileView.swift`, `macos/Hako/Hako/Auth/TokenKeychain.swift`.

## Конфигурация сборки

Настройки таргета `Hako` одинаковы для `Debug` и `Release`:

- App Sandbox отключён (`ENABLE_APP_SANDBOX = NO`), Hardened Runtime (`ENABLE_HARDENED_RUNTIME = YES`),
  доступ только на чтение к файлам, выбранным пользователем (`ENABLE_USER_SELECTED_FILES = readonly`),
  исходящие сетевые соединения (`ENABLE_OUTGOING_NETWORK_CONNECTIONS = YES` →
  `com.apple.security.network.client`). Отдельного `.entitlements`-файла нет.
- Provisioning profile не встраивается, поэтому у подписанной сборки нет entitlement
  `application-identifier` (важно для Keychain — см. [[Data/Persistence]]).
- Info.plist генерируется (`GENERATE_INFOPLIST_FILE = YES`); ключи задаются через
  `INFOPLIST_KEY_*` в настройках таргета. Частичный `macos/Hako/Hako-Info.plist` (`INFOPLIST_FILE`) добавляет
  `HakoCommit = $(HAKO_COMMIT)` — коммит сборки для обновлений ([[App/Updates]]).
- `macos/Hako/Version.xcconfig` — источник `MARKETING_VERSION` и `CURRENT_PROJECT_VERSION` для конфигураций `Debug` и `Release` таргета `Hako`.
- В `windows/Hako/Package.appxmanifest` версия пакета `Identity.Version` имеет вид `X.Y.Z.0`; `windows/Hako/Hako.csproj` получает из неё `AssemblyVersion`, `FileVersion` и `InformationalVersion` через `XmlPeek` перед `GetAssemblyVersion`.
- `macos/Hako/Hako/PrivacyInfo.xcprivacy` включён в ресурсы приложения и объявляет использование
  `UserDefaults` для собственных настроек (`CA92.1`) и API ёмкости диска для отображения места (`85F4.1`).
  System Boot Time (`35F9.1`) используется для длительностей игры без учёта системного сна.
- Конкурентность: `SWIFT_DEFAULT_ACTOR_ISOLATION = MainActor`,
  `SWIFT_APPROACHABLE_CONCURRENCY = YES` — код модуля по умолчанию изолирован на главном акторе.
- Подпись автоматическая (`CODE_SIGN_STYLE = Automatic`), команда `DEVELOPMENT_TEAM = 9Y935NYUP9`.
- Локализация: String Catalog `macos/Hako/Hako/Localizable.xcstrings` (`LOCALIZATION_PREFERS_STRING_CATALOGS`,
  `SWIFT_EMIT_LOC_STRINGS`) с исходным языком `ru` и переводом `en`; `developmentRegion = en`,
  `knownRegions` содержит `ru`. Устройство и правила — [[UI/Localization]].

Сборка из командной строки без подписи:

```sh
xcodebuild -project macos/Hako/Hako.xcodeproj -scheme Hako -configuration Debug build CODE_SIGNING_ALLOWED=NO
```

Общая схема `Hako` хранится в `macos/Hako/Hako.xcodeproj/xcshareddata/xcschemes/Hako.xcscheme`.
Она собирает приложение с зависимым CLI-помощником и запускает `HakoTests`. Помощник копируется
в `Contents/MacOS` с `CodeSignOnCopy`; имеет те же deployment target и Hardened Runtime.
Тестовый таргет без приложения-хоста: компилирует
те же исходники скина, Microsoft- и Hako-авторизации, данных сборок, официальных метаданных, установщика и клиента Modrinth; использует собственную копию `Steve.png`,
фикстуры Mojang и подменённые HTTP-ответы; главное окно и вход в Microsoft не запускаются.

```sh
xcodebuild -project macos/Hako/Hako.xcodeproj -scheme Hako -configuration Debug -destination 'platform=macOS' test CODE_SIGNING_ALLOWED=NO
```

`xcuserdata/` исключён в `.gitignore`.

CI: `.github/workflows/tests.yml` запускает тесты macOS, Windows и релизных скриптов, а также гейт версий на pull request в `main`.
На PR отдельный гейт вызывает `.github/scripts/detect_release.py --require-bump` и требует повысить версию
каждой изменённой платформы. `.github/workflows/release.yml` на push в `main` запускает только рабочие места
платформ с повышенной версией: macOS-тесты и сборка выполняются для macOS-релиза, Windows-сборка — для Windows-релиза;
общий per-push prerelease убран.
Стабильные релизы имеют независимые теги `macos-vX.Y.Z` и `windows-vX.Y.Z`; старые релизы сохраняются.
Платформенные очереди стабильной публикации не отменяют уже запущенные релизы.

macOS собирается после тестов на раннере GitHub `xcode-27` (macOS 27 с Xcode 27), с ad-hoc подписью без Developer ID
и нотаризации. CI сверяет версию и `HakoCommit=$GITHUB_SHA` в собранном `Hako.app`, создаёт `Hako-<версия>-macos.dmg`
и публикует его в релизе `macos-vX.Y.Z` ([[App/Updates]]). Ad-hoc подпись меняется с каждой сборкой, поэтому после
обновления macOS может снова спросить доступ к записи токенов в Keychain.

Windows собирается на `windows-2025` с .NET 8/MSBuild для x64 и ARM64; каждая архитектура получает self-contained MSIX.
Релиз подписывается SHA-256 и RFC 3161 timestamp сертификатом из секретов `WINDOWS_SIGNING_PFX_BASE64` и
`WINDOWS_SIGNING_PFX_PASSWORD` (пустой пароль допустим). PFX и импортированный закрытый ключ удаляются после сборки;
в релиз прикладываются обе архитектуры, публичный `Hako.cer` и нужные runtime-зависимости. В конце описания каждого Windows-релиза workflow добавляет инструкцию импорта `Hako.cer` из того же релиза в «Локальный компьютер → Доверенные лица» через мастер сертификатов или PowerShell от имени администратора.
Для проверки самоподписанной подписи публичный сертификат временно добавляется в
`LocalMachine\TrustedPeople` и `LocalMachine\Root` одноразового раннера и удаляется при очистке вместе с записью в `CurrentUser\My`.
Доверие к корню нужно для Authenticode-проверки в CI; пользователю для установки достаточно `TrustedPeople`.
Создание сертификата `CN=daske` на macOS и загрузка PFX в Repository secrets описаны в `docs/windows-signing.md`.

## Ограничения и инварианты

- Группа `Hako` в проекте — `PBXFileSystemSynchronizedRootGroup`: всё содержимое
  `macos/Hako/Hako/` автоматически входит в таргет. Любой файл, положенный в эту папку,
  попадает в сборку; правка `project.pbxproj` для добавления исходников не нужна. `HakoTests` компилирует
  исходники приложения явным списком: файл, от которого зависят уже включённые исходники, добавляется
  в фазу Sources этого таргета в `project.pbxproj`.
- Новые `@Model`-типы регистрируются в схеме контейнера — см. [[Data/Persistence]].
- `AppDataLocation` переносит SwiftData-store из контейнера Sandbox в `Library/Application Support/Hako/`. Игровые файлы
  каждой сборки находятся в настоящем `~/.hako/{имя}/java` и `minecraft`; общих бинарных файлов нет.
- Вход в Minecraft требует Client ID Azure, одобренного Mojang; Client ID проекта задан в
  `MicrosoftAuth.clientID` и одобрен Mojang — см. [[Auth/MicrosoftAuth]].
- Интерфейс рассчитан на светлую тему и белый фон: `ContentView` принудительно задаёт `.light`.
- `HakoTests` проверяет формат текстур, зеркалирование 64×32, слои, загрузку, кэш, ошибки, отмену
  и сохранение варианта активного скина, миграцию аккаунта, независимость/переименование сборок, совместимость Mojang,
  проверку загрузок, паузу/восстановление очереди, копирование текстурпаков и установку из каталога Modrinth.
  Импорт `.hakobackup`/`.hakoworld` проверяет ZIP-границы, восстановление и откат; детали в [[Data/Backups]].
  Компоновка и рендер проверяются в нативном окне.

## Решения и основания

- SwiftUI + SwiftData получены из шаблона Xcode при создании проекта. Причины выбора не задокументированы.
  Sandbox отключён по решению пользователя для доступа к настоящей папке `~/.hako`.
- Вход как Microsoft, если Minecraft недоступен; в профиле две строки (Xbox: gamertag, аватар, email;
  Java: голова персонажа, ник); аватар в рейле — голова Minecraft, иначе аватар Xbox; узкий рейл
  из иконок; автоподключение Minecraft при запуске — решения пользователя.
- Вход по коду через microsoft.com/link, токены в Keychain
  и профиль в SwiftData, окно 1280×720 (растягиваемое, минимум 960×540), русский и английский интерфейс
  с японскими акцентами — решения пользователя.
- Язык выбирается на приветствии и в разделе настроек «Основные» и меняется без перезапуска;
  меню macOS и системные панели при этом следуют языку системы — решения пользователя ([[UI/Localization]]).
- Анимированный скин справа в профиле, ходьба на месте и вращение мышью; встроенный классический
  Стив при недоступном Minecraft или загрузке — решения пользователя ([[UI/MinecraftSkin]]).
- Обычная связка ключей вместо Data Protection Keychain — из-за отсутствия `application-identifier`
  (см. [[Data/Persistence]]).
- Сборка с `usesGlobalParameters` использует актуальные глобальные аргументы и настройки окна;
  без флага хранит собственные значения. Независимые файлы сборок, путь `~/.hako`, отключение Sandbox
  и сохранение сборок при выходе — решения пользователя.

## Добавление компонента

1. Создать Swift-файл в `macos/Hako/Hako/` (допускаются подпапки) — он автоматически войдёт в таргет.
2. Новую модель SwiftData зарегистрировать по правилам [[Data/Persistence]].
3. Новое представление подключить к иерархии, начиная с `ContentView`, или как новую сцену в `HakoApp.body`.

## Fabric

Сборки выбирают Vanilla либо Fabric при создании. `Minecraft/FabricClient.swift` обращается к Fabric Meta
и официальному проекту Fabric API в Modrinth, кэширует проверенный API для предварительной совместимости.
Установка сохраняет исходный Mojang manifest и отдельный дочерний профиль Fabric; запуск разрешает их
совместно, без запросов к сети. Моды, ресурспаки и датапаки миров из Modrinth ставятся через каталог
и обновляются с сохранением происхождения в файловых реестрах ([[Minecraft/Modrinth]]);
локальные датапаки устанавливаются из ZIP/папок в выбранный мир. Готовые модпаки Fabric/Vanilla создаются отдельным каталогом; `InstallationCoordinator` владеет `ModpackInstaller`, а план хранится файлом без расширения SwiftData. У модпака `FabricConfiguration.api` отсутствует, автоматическое добавление API отключено. Каталога CurseForge нет.

## Windows-клиент

Клиент для Windows в каталоге `windows/` реализует приветствие, независимый вход Hako через Telegram и вход в Microsoft по коду, главную с рейлом
сборок и «Настройками», русский/английский язык, профиль с 3D-превью скина и выход. Vanilla- и Fabric-сборки
создаются и устанавливаются фоновой очередью (пауза, продолжение, повтор), настраиваются, удаляются и запускаются
онлайн или в offline-mode. Есть локальные моды и ресурспаки, миры и датапаки, каталог Modrinth и импорт готовых
модпаков Modrinth `.mrpack`, скриншоты, дублирование сборок и резервные копии `.hakobackup`/`.hakoworld`
— [[UI/Launcher#Windows-клиент]] и [[Data/Backups]]. Также Windows учитывает игровое время по аккаунту/гостю и сборке.
Фоновый помощник продолжает измерение после закрытия лаунчера, а двухфазный перенос сохраняет границу гостевой сессии.
Сервисы сборок живут всё время работы приложения и создаются в `App.OnLaunched` (`LauncherServices`).
`WindowsAppSdkBootstrap` пропускает SDK для `--playtime-helper`. В framework-dependent запуске
сначала вызывает bootstrapper SDK с версией из NuGet (для MSIX — no-op), затем только при наличии
package identity выполняет `DeploymentManager.Initialize`. Unpackaged-запуск использует runtime,
подключённый bootstrapper; self-contained MSIX не требует этих вызовов. Общий автоинициализатор SDK
отключён в `Hako.csproj`, чтобы не загружать WinUI/runtime в фоновом помощнике.
`MainWindow` использует Mica под WinUI `TitleBar`; начальная клиентская область 1280×720 эффективных
пикселей задаётся с масштабированием по DPI окна.
Экраны — [[UI/Screens#Windows-клиент]], вход Microsoft — [[Auth/MicrosoftAuth#Windows-клиент]], вход Hako — [[Auth/HakoAuth]],
хранение — [[Data/Persistence#Windows-клиент]], строки — [[UI/Localization#Windows-клиент]].

### Стек

| Технология | Роль | Источник |
|------------|------|----------|
| .NET 8 | Платформа: `net8.0-windows10.0.19041.0` для `Hako`, `net8.0` для `Hako.Core`, `net8.0-windows10.0.17763.0` для тестов | `windows/Hako/Hako.csproj`, `windows/Hako.Core/Hako.Core.csproj`, `windows/Hako.Tests/Hako.Tests.csproj` |
| WinUI 3, Windows App SDK 2.5.1 (`Microsoft.WindowsAppSDK`), `Microsoft.Windows.SDK.BuildTools` 10.0.28000.2705 | Окна и страницы, `AppInstance`, MSIX-инструменты | `windows/Hako/Hako.csproj` |
| Vortice.Direct3D11, Vortice.D3DCompiler | D3D11 renderer и компиляция встроенного HLSL для skin preview | `windows/Hako/Hako.csproj`, `windows/Hako/Graphics/` |
| `System.Security.Cryptography.ProtectedData` 8.0.0 | DPAPI: `ProtectedData.Protect` и `Unprotect` | `windows/Hako.Core/Hako.Core.csproj` |
| `System.Text.Json` с source generation | Сериализация без рефлексии через `HakoJsonContext` | `windows/Hako.Core/JsonContexts.cs` |
| MSTest 3.10.4, `Microsoft.NET.Test.Sdk` 17.14.1 | Тесты | `windows/Hako.Tests/Hako.Tests.csproj` |

Платформы сборки: x86, x64, ARM64 (`Platforms`, `RuntimeIdentifiers` в `Hako.csproj`). Минимальная версия
Windows — 10 1809 (`TargetPlatformMinVersion` 10.0.17763.0); `app.manifest` объявляет поддержку возможностей Windows 10.

### Структура

- `windows/Hako/` — WinUI-приложение: `Program.cs` (точка входа и ключи единственного экземпляра), `App.xaml.cs`
  (создаёт `AppCoordinator`, app-lifetime `LauncherServices`, скриншоты и сервисы архивов), `MainWindow` (окно с `NavigationView`,
  `Frame` и forwarding состояния окна), страницы `LoadingPage`, `WelcomePage`, `LoginPage`, `HakoLoginPage`, `BuildsPage`, `SettingsPage`,
  профиль и его `InstanceScreenshotsView`, `ScreenshotThumbnails`, `MinecraftSkinView`, `MinecraftSkinImageDecoder`, `MinecraftSkinPreviewService`, `MinecraftSkinHeadComposer`,
  `Graphics/MinecraftSkinRenderer` и `Graphics/MinecraftSkinGeometry`, `HakoStrings`, `AppDataDirectory`,
  `Strings/ru-RU` и `Strings/en-US` (`Resources.resw`), `Package.appxmanifest`, `app.manifest`,
  `Properties/launchSettings.json`, решение `Hako.slnx`.
- `windows/Hako.Core/` — логика без WinUI: `AppCoordinator` (экраны и независимые состояния провайдеров), `Playtime/` (ledger, сессии,
  helper и coordinator), `MicrosoftDeviceAuth`, `HakoAuthApiClient`, `HakoAccountCoordinator`,
  `AuthContracts`, `AccountSessionStore`, `HakoSessionStore`, `UserSettingsService`, `HakoPaths`, `HakoRootLock`, `Instances/`
  (`GameInstance`, `InstanceStorage`, `InstanceStore`, `InstanceScreenshots`, `InstanceFileTree`, `InstanceBackup`,
  `WorldBackup`, `InstallationCoordinator`, `ModpackCatalog`), `Minecraft/`
  (`ModrinthClient`, `ModpackManifest`, `ModpackArchive`, `ModpackInstaller`, Mojang/Fabric install and launch logic),
  source-generated COM-корзина `RecycleBin`, `JsonContexts`.
- `windows/Hako.Tests/` — `AppCoordinatorTests`, `PlaytimeTests`, `MicrosoftDeviceAuthTests`, `StorageTests`,
  `LocalizationResourcesTests`, `RedactionTests`, `MinecraftSkinTests`, `MinecraftSkinImageDecoderTests`,
  `MinecraftSkinGeometryTests`; `TestSupport.cs` содержит фейки времени, таймеров, хранилищ,
  сервиса входа и `TestData`. Decoder/head/geometry исходники Windows UI linked-compiled для тестов.
  `windows/Hako/app.manifest` включает `longPathAware` для файловых операций Windows.

### Запуск и единственный экземпляр

1. `Program.Main` — синхронный `[STAThread] static void Main`. Режим `--playtime-helper <journal>` обрабатывается до
   WinUI, `AppInstance` и root lock; обычный путь затем разбирает `--hako-root` и `--data-root`
   и валидирует абсолютные пути. Для переопределений нужны оба корня; неверные аргументы показывают системное сообщение
   и завершают процесс без выбора production-путей. Изолированный запуск использует ключ
   `Hako.Isolated.<sha256 корней>`, а `--profile-preview` — отдельный `Hako.ProfilePreview`.
   Для изолированного запуска game-root и data-root не могут содержать друг друга. Обычный ключ остаётся `Hako.Main`.
   Если экземпляр не текущий, `RedirectActivationTo` запускает
   `RedirectActivationToAsync` в `Task.Run`, а STA-поток ждёт его в `CoWaitForMultipleObjects`, продолжая обрабатывать
   сообщения: блокирующий `Wait()` может зависнуть, пока целевой экземпляр обрабатывает активацию. Затем перенаправляющий
   процесс вызывает `SetForegroundWindow` для главного окна целевого процесса и выходит. Сбой этого шага (например, целевой
   процесс уже завершился) игнорируется: второй экземпляр просто выходит. Текущий экземпляр при повторной
   активации вызывает `App.ActivateMainWindow` через `DispatcherQueue`: разворачивает свёрнутое окно
   (`OverlappedPresenter.Restore`), затем вызывает `AppWindow.Show()` и `Activate()`. Один экземпляр — решение пользователя.
2. Обычный `App()` удерживает `HakoRootLock` на `.hako-lock` в игровом корне и каталоге данных. При sharing/lock violation
   показывается локализованное сообщение и приложение не читает аккаунт, настройки или `instances.json`; прочие ошибки
   блокировки показываются как ошибка доступа к хранилищу. Игровой корень может быть junction или symlink, а вложенные
   пути сборок остаются защищены от reparse points. `HakoPaths` задаёт
   `%USERPROFILE%\.hako`, каталог данных из `AppDataDirectory.GetLocalPath()` и `%LOCALAPPDATA%\Hako\Cache`.
   App-lifetime сервисы используют эти пути: `InstanceStore`, `PlaytimeCoordinator` (только `GameRoot`),
   `UserSettingsService` с `JsonUserSettingsStore`, `AccountSessionStore` и
   `MicrosoftDeviceAuth(new HttpClient(), TimeProvider.System)`. `instances.json` атомарно
   сохраняет полный профиль сборки; изменения сериализуются внутри `InstanceStore`. `settings.json` хранит язык и
   optional-поля глобальных параметров игры, а `UserSettingsService` пишет единый снимок для обоих видов настроек.
   Skin preview service использует отдельный `HttpClient` с 15-секундным timeout для публичных Mojang texture API,
   без токенов; он не меняет `AccountSession` или формат persistence.
3. `OnLaunched` показывает `MainWindow`, затем `AppCoordinator.InitializeAsync()` загружает настройки и независимо читает Microsoft `session.dat` и Hako `hako-session.dat`. При наличии Hako-сессии `/me` запускается асинхронно после показа сборок; отсутствие сети сохраняет кэшированный аккаунт как непроверенный.
   `PlaytimeCoordinator` сверяет ожидающий перенос с текущим XUID; `GameLaunchCoordinator.RestoreRunningGamesAsync`
   восстанавливает существующие session IDs до reconcile, чтобы живая игра не считалась потерянной. После запуска
   сервисов приложение сверяет журналы при старте и каждые 10 секунд; закрытие окна отменяет app polling, но не detached helper.
   `InitializeAsync()` выбирает `Welcome` или `Builds`; ошибка реестра остаётся в `InstanceStore.LoadError`
   для UI. В preview-режиме `HakoPaths` указывает на уникальный временный корень, создаются fake in-memory store
   и auth-сервис, который запрещает sign-in; production-каталог данных не читается, временный корень удаляется при выходе.
   Режим
   `--profile-preview guest` после гостевого перехода открывает «Настройки → Профиль»; обычный гостевой вход
   по-прежнему начинает на странице сборок.

### Упаковка и сборка

- `EnableMsixTooling=true`, есть `Package.appxmanifest` и два профиля в `launchSettings.json`:
  `Hako (Package)` (`MsixPackage`) и `Hako (Unpackaged)` (`Project`). Тип упаковки (`WindowsPackageType`)
  в `Hako.csproj` не задан; CI выбирает `MSIX` для Windows-релизной сборки.
- `PublishTrimmed` и `PublishReadyToRun` включаются для конфигураций, отличных от `Debug`.
  Известные Release-предупреждения trimming: `IL2059` в сгенерированном `XamlTypeInfo.g.cs` для
  `RunClassConstructor` у `Nullable<bool>` присоединённого свойства `TitleBar.IsDragRegion` и `IL2104`
  в upstream-сборках `SharpGen.Runtime` или `System.Text.Json` (набор зависит от SDK и восстановленных
  версий зависимостей). `Nullable<T>` не имеет статического конструктора; предупреждения библиотек относятся
  к их исходным сборкам. Не подавлять их глобально через `NoWarn`, чтобы не скрыть такие же предупреждения
  в коде Hako.
- Для isolated UI smoke запустить `Hako.exe --profile-preview [account|no-minecraft|guest|fixture]`
  (без режима используется `account`). `--preview-width` принимает 640–2560, `--preview-height` — 480–2560;
  эти аргументы действуют только в preview. Режимы используют fake аккаунт/гостя без реального UUID, сеть
  skin service не обращается к Mojang; `fixture` показывает локальный Steve и тестовый плащ. Команда для
  разработки: `dotnet run --project windows/Hako/Hako.csproj -c Debug -- --profile-preview fixture --preview-width 1000 --preview-height 720`.
- Сборка и тесты:

```sh
dotnet build windows/Hako/Hako.csproj -c Debug -p:Platform=x64
dotnet test windows/Hako.Tests/Hako.Tests.csproj -c Debug
```

- Windows-релизные CI-сборки x64 и ARM64 с подписью MSIX описаны в разделе CI выше.

Для изолированной разработки Windows-клиента `-p:HakoDev=true` генерирует MSIX manifest под `obj/` с identity `productionName-dev` и display/window name `Hako-dev`. Dev manifest не содержит file association; `HakoPaths.CreateDevelopment` использует `%LOCALAPPDATA%\Hako-dev\{Game,Data,Cache}` и отдельный instance lock. Изолированный path отключает автообновление стабильной версии. По умолчанию dev не читает и не копирует production data; пара `--hako-root`/`--data-root` остаётся рабочим override.

### Ограничения Windows-клиента

- Не реализован CurseForge. Самообновление Windows использует подписанный MSIX из GitHub ([[App/Updates#Windows-клиент]]); прежним клиентам без этой функции нужна однократная ручная установка.
  Modrinth-паки устанавливаются из каталога; локальный импорт `.mrpack` и обновление всего модпака
  не поддерживаются.
- Онлайн-запуск обновляет Minecraft-сессию через `MinecraftSessionCoordinator.ConnectForLaunchAsync` (refresh-grant
  с сохранением нового refresh token); без Minecraft-профиля доступен только offline-mode сборки.

### Решения и основания

- Хранение аккаунта через DPAPI и файлы, а не через `ApplicationData`: `ApplicationData.Current` падает
  в unpackaged-запуске (причина из плана пользователя). Подробности — [[Data/Persistence#Windows-клиент]].
- JSON через source generation (`HakoJsonContext`): при `PublishTrimmed` в .NET 8 рефлексия отключена
  (причина из плана пользователя).
- Один экземпляр приложения, сохранение аккаунта между запусками, вход без аккаунта
  (`ContinueWithoutAccount`, не сохраняется между запусками) и профиль внутри раздела «Настройки» — решения пользователя.

## Серверная часть

`server/` — отдельный Rust workspace с пакетами `hako-auth` и `hako-pack`. Общий `server/Dockerfile` собирает каждый пакет в отдельный образ; корневой `compose.yaml` запускает Auth, Pack и PostgreSQL с отдельными базами и ролями. Pack хранит байты `.hakopack` во внешнем S3-совместимом хранилище. Подробности контрактов и размещения — [[Server/Auth]], [[Server/Pack]] и [[Server/Deployment]].

CI job `server` в `.github/workflows/tests.yml` проверяет форматирование, Clippy, Rust-тесты и интеграционные тесты с PostgreSQL. Серверные сервисы пока не интегрированы с macOS- или Windows-клиентом; их сборки, версии и релизы независимы.

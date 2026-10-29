# ContentView

Parent: [[Index]]

## Назначение

Корневое представление главного окна: фон [[UI/Screens#SakuraBackground|SakuraBackground]]
и текущий экран поверх него. Выбирает экран по локальной сессии Microsoft и/или Hako, явному
гостевому режиму и экрану входа; перед приветствием дожидается асинхронного восстановления Hako.
Задаёт минимальный размер контента и светлую тему.

## Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `macos/Hako/Hako/ContentView.swift` | `ContentView` | Корень окна |
| `macos/Hako/Hako/HakoApp.swift` | `HakoApp.body` | Сцена окна и её настройки |
| `macos/Hako/Hako/Instances/BackupRestoreCoordinator.swift` | `LauncherPresentationState` | Состояния provider-входов, гостя, быстрого запуска и восстановления |
| `macos/Hako/Hako/Auth/HakoAccountCoordinator.swift` | `HakoAccountCoordinator` | Независимое восстановление Telegram/Hako-сессии и startup warning |
| `macos/Hako/Hako/Instances/InstanceRenameExitCoordinator.swift` | `InstanceRenameExitCoordinator`, `InstanceRenameWindowCloseGuard`, `HakoApplicationDelegate` | Предупреждение перед закрытием окна или приложения при несохранённом переименовании |

## Публичные контракты

| Контракт | Поведение и условия |
|----------|---------------------|
| `ContentView()` | Без параметров. Требует в окружении `modelContainer` со схемой `[Account, GameInstance, PlayerPlaytime, InstancePlaytime, PlaytimeSession]`, `InstallationCoordinator`, `MinecraftSessionCoordinator`, `HakoAccountCoordinator`, `GameLaunchCoordinator`, `PlaytimeCoordinator` и `InstanceRenameExitCoordinator`, а также `QuickLaunchCoordinator` и `AppUpdateCoordinator`: в приложении их создаёт `HakoServices.shared`, превью получает отдельные контейнер и координаторы |

## Выбор экрана

`hasProviderSession` объединяет `accounts.first != nil` и `hakoAccount.session != nil`; provider-сессии
хранятся независимо. Гостевой выбор хранится отдельно в `LauncherPresentationState` и не сохраняется
между запусками. До завершения загрузки Keychain показывается состояние восстановления — сбой чтения
показывает предупреждение и блокирует операции, способные перезаписать или удалить неизвестную сессию.

| Условие | Экран |
|---------|-------|
| Hako ещё восстанавливается | Экран загрузки; окно не показывает приветствие до проверки доступности Keychain |
| выбран Microsoft-вход | `LoginView(onBack:)` — «Назад» закрывает Microsoft-вход |
| выбран Hako-вход | `HakoLoginView(onBack:)` — «Назад» отменяет запрос Telegram и возвращает на предыдущий корневой экран |
| экран входа закрыт, есть Microsoft- или Hako-сессия либо гостевой режим | `LauncherView(account:onMicrosoftSignIn:onHakoSignIn:)` с необязательным Microsoft-аккаунтом — см. [[UI/Launcher]] |
| экран входа закрыт, нет provider-сессии и гостевого режима | `WelcomeView` с отдельными кнопками Microsoft, Telegram/Hako и гостевого входа |

Изменения Microsoft или Hako-сессии закрывают открытый экран входа. Выход из одного provider оставляет
лаунчер открытым, если другой остаётся подключён; выход из последнего возвращает на приветствие,
кроме активного восстановления архива. Minecraft-сессию отслеживает `.task(id: accounts.first?.xuid)`;
Hako-сессия восстанавливается независимо и не создаёт `Account`.
Смена экрана — `.transition(.blurReplace)` с анимацией `.smooth` по состояниям входа, provider-сессий и гостя.
`.task(id: accounts.first?.xuid)` наблюдает и обновляет Minecraft-сессию через общий сервис.
Быстрый запуск переключает окно в лаунчер, в том числе гостевой, и открывает нужный профиль через
`QuickLaunchCoordinator.presentation`. Презентер использует существующее окно или `openWindow(id: "main")`;
запрос сохраняется до готовности интерфейса. Ошибки быстрого запуска показывает корневой alert.
Подробности экранов — [[UI/Screens]], системного запуска — [[UI/SystemIntegration]].

## Окно

Настройки единственной сцены `Window("Hako", id: "main")` в `HakoApp.body`:

| Модификатор | Эффект |
|-------------|--------|
| `.defaultSize(width: 1280, height: 720)` | Начальный размер окна |
| `.restorationBehavior(.disabled)` | Размер и положение не восстанавливаются — каждый запуск открывается в 1280×720 |
| `.windowResizability(.contentMinSize)` + `ContentView.frame(minWidth: 960, minHeight: 540)` | Окно растягивается, минимум 960×540 |
| `.windowStyle(.hiddenTitleBar)` | Заголовок скрыт, контент уходит под него, кнопки окна остаются |
| `.windowBackgroundDragBehavior(.enabled)` | Окно перетаскивается за фон |

Если в настройках сборки введено новое имя, `InstanceRenameWindowCloseGuard` перехватывает закрытие окна,
а `HakoApplicationDelegate.applicationShouldTerminate` — завершение приложения. Согласие переименовывает
сборку и затем продолжает закрытие; отказ закрывает окно или приложение без изменения имени.

Закрытие последнего окна не завершает Hako: `applicationShouldTerminateAfterLastWindowClosed` возвращает `false`.
Закрытие окна отменяет ещё не разрешённые запросы навигации; уже начавшаяся подготовка игры продолжается.

## Ограничения и важные детали

- `.preferredColorScheme(.light)`: интерфейс рассчитан на белый фон; в тёмной теме
  системные цвета текста стали бы светлыми на белом.
- `.environment(\.locale, language.locale)` по `@AppStorage(AppLanguage.storageKey)` задаёт язык интерфейса
  всему окну; смена языка не пересоздаёт экраны — см. [[UI/Localization]].

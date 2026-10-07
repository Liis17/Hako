# ContentView

Parent: [[Index]]

## Назначение

Корневое представление главного окна: фон [[UI/Screens#SakuraBackground|SakuraBackground]]
и текущий экран поверх него. Выбирает экран по наличию сохранённого аккаунта и гостевому входу,
задаёт минимальный размер контента и светлую тему.

## Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `macos/Hako/Hako/ContentView.swift` | `ContentView` | Корень окна |
| `macos/Hako/Hako/HakoApp.swift` | `HakoApp.body` | Сцена окна и её настройки |
| `macos/Hako/Hako/Instances/InstanceRenameExitCoordinator.swift` | `InstanceRenameExitCoordinator`, `InstanceRenameWindowCloseGuard`, `HakoApplicationDelegate` | Предупреждение перед закрытием окна или приложения при несохранённом переименовании |

## Публичные контракты

| Контракт | Поведение и условия |
|----------|---------------------|
| `ContentView()` | Без параметров. Требует в окружении `modelContainer` со схемой `[Account, GameInstance, PlayerPlaytime, InstancePlaytime, PlaytimeSession]`, `InstallationCoordinator`, `MinecraftSessionCoordinator`, `GameLaunchCoordinator`, `PlaytimeCoordinator` и `InstanceRenameExitCoordinator`: в приложении их создаёт `HakoApp`, превью получает отдельные контейнер и координаторы |

## Выбор экрана

Состояние «вошёл / не вошёл» выводится из SwiftData (`@Query private var accounts: [Account]`),
Гостевой выбор хранится отдельно в `isGuestLauncher` и не сохраняется между запусками.

| Условие | Экран |
|---------|-------|
| `isSigningIn == true` | `LoginView(onBack:)` — «Назад» сбрасывает `isSigningIn`; гостевой выбор сохраняется |
| вход не открыт, есть аккаунт либо `isGuestLauncher == true` | `LauncherView(account:onSignIn:)` с необязательным аккаунтом — см. [[UI/Launcher]] |
| вход не открыт, нет аккаунта и гостевого выбора | `WelcomeView(onStart:onContinueWithoutAccount:)` — «Начать» открывает вход, «Продолжить без аккаунта» выбирает гостевой лаунчер |

`.onChange(of: accounts.isEmpty)` сбрасывает `isSigningIn`: успешный вход возвращает в лаунчер.
После выхода очищаются гостевой выбор и сервис сессий, показывается приветствие.
Смена экрана — `.transition(.blurReplace)` с анимацией `.smooth` по состояниям входа, аккаунта и гостя.
`.task(id: accounts.first?.xuid)` наблюдает и обновляет Minecraft-сессию через общий сервис.
Подробности экранов — [[UI/Screens]].

## Окно

Настройки сцены `WindowGroup` в `HakoApp.body`:

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

## Ограничения и важные детали

- `.preferredColorScheme(.light)`: интерфейс рассчитан на белый фон; в тёмной теме
  системные цвета текста стали бы светлыми на белом.
- `.environment(\.locale, language.locale)` по `@AppStorage(AppLanguage.storageKey)` задаёт язык интерфейса
  всему окну; смена языка не пересоздаёт экраны — см. [[UI/Localization]].

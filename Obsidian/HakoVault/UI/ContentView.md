# ContentView

Parent: [[Index]]

## Назначение

Корневое представление главного окна: фон [[UI/Screens#SakuraBackground|SakuraBackground]]
и текущий экран поверх него. Выбирает экран по наличию сохранённого аккаунта,
задаёт минимальный размер контента и светлую тему.

## Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `macos/Hako/Hako/ContentView.swift` | `ContentView` | Корень окна |
| `macos/Hako/Hako/HakoApp.swift` | `HakoApp.body` | Сцена окна и её настройки |

## Публичные контракты

| Контракт | Поведение и условия |
|----------|---------------------|
| `ContentView()` | Без параметров. Требует в окружении `modelContainer` со схемой `[Account, GameInstance]` и `InstallationCoordinator`: в приложении их создаёт `HakoApp`, превью получает отдельные контейнер и координатор |

## Выбор экрана

Состояние «вошёл / не вошёл» выводится из SwiftData (`@Query private var accounts: [Account]`),
отдельного флага нет. «Первый запуск» означает отсутствие аккаунта.

| Условие | Экран |
|---------|-------|
| `accounts.first != nil` | `LauncherView(account:)` — см. [[UI/Launcher]] |
| аккаунта нет, `isSigningIn == true` | `LoginView(onBack:)` — «Назад» сбрасывает `isSigningIn` |
| аккаунта нет, `isSigningIn == false` | `WelcomeView(onStart:)` — «Начать» ставит `isSigningIn` |

`.onChange(of: accounts.isEmpty)` сбрасывает `isSigningIn`, поэтому после выхода снова
показывается приветствие, а не экран входа. Смена экрана — `.transition(.blurReplace)`
с анимацией `.smooth` по `isSigningIn` и `accounts.isEmpty`.
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

## Ограничения и важные детали

- `.preferredColorScheme(.light)`: интерфейс рассчитан на белый фон; в тёмной теме
  системные цвета текста стали бы светлыми на белом.
- Строки интерфейса — русские литералы; файла String Catalog в проекте нет.

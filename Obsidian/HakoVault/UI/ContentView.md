# ContentView

Parent: [[Index]]

## Назначение

Корневое представление главного окна: фон [[UI/Screens#SakuraBackground|SakuraBackground]]
и текущий экран поверх него. Задаёт минимальный размер контента и светлую тему.

## Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `macos/Hako/Hako/ContentView.swift` | `ContentView` | Корень окна |
| `macos/Hako/Hako/HakoApp.swift` | `HakoApp.body` | Сцена окна и её настройки |

## Публичные контракты

| Контракт | Поведение и условия |
|----------|---------------------|
| `ContentView()` | Без параметров. Показывает `WelcomeView`; кнопка «Начать» пока ничего не делает |

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

# ContentView

Parent: [[Index]]

## Назначение

Корневое представление главного окна: двухколоночный `NavigationSplitView`
со списком записей `Item` в боковой панели и детальной панелью.

## Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `macos/Hako/Hako/ContentView.swift` | `ContentView` | Отображение, добавление и удаление записей |

## Публичные контракты

| Контракт | Поведение и условия |
|----------|---------------------|
| `ContentView()` | Без параметров. Требует в окружении `modelContainer`, схема которого включает `Item`: в приложении — `HakoApp.sharedModelContainer`, в `#Preview` — `.modelContainer(for: Item.self, inMemory: true)` |

## Поведение

- Боковая панель: `List` с `ForEach(items)`; каждая строка — `NavigationLink` с `timestamp`
  в формате `Date.FormatStyle(date: .numeric, time: .standard)`. Ширина колонки — `min: 180, ideal: 200`.
- Детальная панель: «Item at …» для выбранной записи, иначе «Select an item».
- Тулбар: кнопка «Add Item» (`systemImage: "plus"`) вызывает `addItem()` — вставку `Item(timestamp: Date())`.
- `.onDelete(perform: deleteItems)` удаляет записи по индексам результата `@Query`.
- Вставка и удаление выполняются внутри `withAnimation`.

## Зависимости и взаимодействия

- Использует [[Data/Persistence]] — `@Query private var items: [Item]` и `@Environment(\.modelContext)`.
- Создаётся в `HakoApp.body` как содержимое `WindowGroup`.

## Ограничения и важные детали

- `@Query` объявлен без `sort` — порядок записей явно не задан.
- Строки интерфейса — английские литералы; файла String Catalog в проекте пока нет.

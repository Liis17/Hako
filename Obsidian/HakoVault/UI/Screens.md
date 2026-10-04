# Screens

Parent: [[Index]]

## Назначение

Экраны главного окна и общий визуальный стиль: Apple-дизайн с японскими акцентами —
белый фон, размытые пятна цвета сакуры, крупные жирные заголовки.
Экраны показывает [[UI/ContentView]].

## Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `macos/Hako/Hako/Views/Theme.swift` | `Color.sakuraDeep`, `Color.shu`, `Color(hex:)`, `Text.heroTitle()`, `JapaneseCaption`, `HankoSeal`, `View.reveal(_:order:)` | Общие цвета, типографика, декоративные элементы, анимация появления |
| `macos/Hako/Hako/Views/SakuraBackground.swift` | `SakuraBackground` | Фон окна |
| `macos/Hako/Hako/Views/WelcomeView.swift` | `WelcomeView` | Экран приветствия |

## Стиль

- Заголовки — `Text.heroTitle()`: SF Pro 72 pt `.heavy`, tracking −1.5. Подзаголовки — `.title2`, `.secondary`.
- `JapaneseCaption` — декоративная подпись над заголовком: Hiragino Mincho ProN W3, 20 pt,
  tracking 8, цвет `sakuraDeep` (#E0607E). Текст через `Text(verbatim:)` — не локализуется.
- `HankoSeal` — красная печать (#D9433B, `Color.shu`) с иероглифом 箱 (Hiragino Mincho ProN W6),
  повёрнута на −4°. «Хако» по-японски — «коробка».
- Основная кнопка — `.buttonStyle(.glassProminent)`, `.tint(.sakuraDeep)`, `.controlSize(.extraLarge)`.
- `reveal(_:order:)` — поочерёдное появление: opacity + сдвиг на 16 pt, задержка 0.08 с × `order`.
  При Reduce Motion сдвига нет, остаётся только fade.

## SakuraBackground

`Color.white` и четыре круга (#FFB7C5, #E8B4C8, #F8C8D4, #FADADD) диаметром 520–700 pt
у краёв окна; позиции заданы в долях размера окна через `GeometryReader`.
Общие `opacity(0.75)` и `blur(radius: 140)`. Пятна медленно дрейфуют
(`easeInOut` 20 с, `repeatForever(autoreverses: true)`); при Reduce Motion анимация не запускается.
Фон игнорирует safe area и заходит под скрытый заголовок окна.

## WelcomeView

`WelcomeView(onStart: () -> Void)` — печать 箱, подпись «ようこそ», заголовок
«Добро пожаловать в Hako», подзаголовок «Лаунчер Minecraft для macOS», кнопка «Начать»
(`.keyboardShortcut(.defaultAction)` — срабатывает по Return) вызывает `onStart`.
Элементы выровнены по левому краю с отступом 96 pt и появляются через `reveal`.

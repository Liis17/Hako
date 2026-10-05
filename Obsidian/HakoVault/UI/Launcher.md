# Launcher

Parent: [[Index]]

## Назначение

Главная страница лаунчера для вошедшего пользователя: узкий стеклянный рейл вкладок слева
и содержимое выбранной вкладки справа. При открытии тихо пробует подключить Minecraft
к аккаунту Microsoft. Показывается [[UI/ContentView]], когда в SwiftData есть `Account`.

## Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `macos/Hako/Hako/Views/Launcher/LauncherView.swift` | `LauncherView`, `LauncherTab`, `MinecraftStatus` | Каркас страницы, выбор вкладки, автоподключение Minecraft |
| `macos/Hako/Hako/Views/Launcher/LauncherRail.swift` | `LauncherRail` | Рейл вкладок |
| `macos/Hako/Hako/Views/Launcher/LauncherPages.swift` | `LauncherPage`, `InstancesView` | Каркас вкладки и заглушка сборок |
| `macos/Hako/Hako/Views/Launcher/SettingsView.swift` | `SettingsView` | Разделы настроек и глобальные параметры игры |
| `macos/Hako/Hako/Views/Launcher/ProfileView.swift` | `ProfileView` | Вкладка профиля и выход |
| `macos/Hako/Hako/Views/Avatars.swift` | `AccountAvatar`, `XboxAvatar`, `MinecraftHead` | Аватары аккаунтов |

## Публичные контракты

| Контракт | Поведение и условия |
|----------|---------------------|
| `LauncherView(account: Account)` | Требует `modelContext` в окружении. Начальная вкладка — `.instances` |
| `LauncherPage(caption:title:content:)` | Японская подпись, `heroTitle`, содержимое; выравнивание по верхнему левому краю, появление через `reveal` |
| `AccountAvatar(account:)` | Голова Minecraft, если известен `minecraftSkinURL`, иначе `XboxAvatar` |
| `XboxAvatar(url:name:)` | `AsyncImage`; пока картинки нет или она не загрузилась — первая буква `name` на градиенте `sakuraDeep` |
| `MinecraftHead(skinURL:)` | Лицо (8×8 в 8,8) и слой шапки (8×8 в 40,8) из текстуры скина 64×64 или 64×32, `interpolation(.none)` |

Форму (круг, скруглённый квадрат) и размер аватару задаёт вызывающий.

## Рейл

`LauncherRail` — колонка 72 pt, `glassEffect` со скруглением 26, отступ 12 pt от краёв окна;
сверху вниз:

1. «Сборки» (`shippingbox.fill`) → `.instances`.
2. Разделитель и неактивная кнопка «+» с подсказкой «Новая сборка — скоро».
3. Внизу «Настройки» (`gearshape.fill`) → `.settings` и аватар аккаунта (`AccountAvatar` 40 pt) → `.profile`.

Выбранная иконка — белая на `sakuraDeep`, при наведении — лёгкая подсветка; у выбранного аватара
обводка `sakuraDeep`. Подписи вкладок — в подсказках (`.help`) и `accessibilityLabel`.
Системное кольцо фокуса отключено (`.focusEffectDisabled()`). Смена вкладки — `.id(tab)` +
`.transition(.blurReplace)` с анимацией `.smooth`.

## Вкладки

| Вкладка | Подпись | Содержимое |
|---------|---------|------------|
| `InstancesView` | パック | «Сборки» и текст-заглушка: сборок пока нет, модели данных для них нет |
| `SettingsView` | 設定 | Разделы «Игра», «Хранилище», «О приложении»; глобальные настройки игры |
| `ProfileView` | プロフィール | «Профиль»: строка Xbox, строка Java Edition, кнопка «Выйти» |

`ProfileView(account:minecraftStatus:)` — две стеклянные строки (ширина до 600 pt):

- Xbox: круглый `XboxAvatar`, gamertag, email (если есть), метка «Xbox».
- Java Edition: `MinecraftHead` и ник Minecraft; если Minecraft не подключён — иконка-заглушка,
  «Minecraft не подключён» и текст по `MinecraftStatus` (`idle` — недоступен для аккаунта,
  `connecting` — «Подключаем Minecraft…», `failed` — сообщение ошибки).

«Выйти»: `TokenKeychain.delete(for: xuid)`, `modelContext.delete(account)`, `modelContext.save()`;
[[UI/ContentView]] возвращает приветствие.

## Настройки

`SettingsView` использует `LauncherPage`; под заголовком — сегментированный переключатель
«Игра», «Хранилище», «О приложении» и прокручиваемое содержимое. Начальный раздел — «Игра».
«Хранилище» и «О приложении» пока содержат заглушки.

Параметры игры сгруппированы в стеклянные карточки «Java», «Minecraft», «Окно»:
путь к Java, аргументы Java и Minecraft, полноэкранный режим, ширина и высота окна.
`@AppStorage` сохраняет изменения автоматически, без привязки к аккаунту.
Ключи, значения по умолчанию и снимок `GameLaunchDefaults` — в [[Data/Persistence]].

Путь вводится вручную или выбирается через `NSOpenPanel`: один файл, папки выбирать нельзя,
пакеты JDK открываются как папки. Отмена не меняет путь. Сохраняется строка пути;
Java не запускается, security-scoped bookmark не создаётся.
Аргументы сохраняются как введённый текст, без разбора или исполнения.

Размеры окна — положительные целые числа в пикселях. Поле хранит черновик строки;
неверный ввод помечается сообщением и не заменяет последнее сохранённое значение.
В полноэкранном режиме поля отключены, сохранённые размеры остаются прежними.
Превью использует отдельный `UserDefaults` suite `com.Launcher.Hako.settings.preview`.

## Автоподключение Minecraft

`LauncherView.connectMinecraftIfNeeded()` в `.task` при появлении страницы (запуск приложения
с сохранённым аккаунтом и сразу после входа). Если `minecraftUUID == nil` и в Keychain есть токены:

1. `MicrosoftAuth.refresh` и сразу сохранение нового refresh token — даже если Minecraft
   снова недоступен (Microsoft заменяет refresh token при каждом обновлении).
2. `MicrosoftAuth.signInToMinecraft`; при успехе — токен Minecraft в Keychain, `account.connect(profile)`,
   `modelContext.save()`.
3. Ошибка → `MinecraftStatus.failed(message)` для вкладки профиля; отмена задачи (выход) не показывается.

Подробности запросов — [[Auth/MicrosoftAuth]], хранение — [[Data/Persistence]].

## Ограничения и важные детали

- Скины старого формата 64×32 без прозрачных пикселей в области одежды (32,0–64,32) рисуются
  без слоя шапки — так же поступает игра; иначе непрозрачный фон закрыл бы лицо.
- `MinecraftHead` загружает текстуру при каждом появлении, кэша нет.
- Создание сборок и запуск игры не реализованы; глобальные параметры пока только сохраняются.

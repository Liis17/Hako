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
| `macos/Hako/Hako/Views/AppLanguagePicker.swift` | `AppLanguagePicker` | Выбор языка на приветствии и в настройках |
| `macos/Hako/Hako/Views/LoginView.swift` | `LoginView` | Вход в Microsoft по коду |

## Стиль

- Заголовки — `Text.heroTitle()`: SF Pro 72 pt `.heavy`, tracking −1.5. Подзаголовки — `.title2`, `.secondary`.
- `JapaneseCaption` — декоративная подпись над заголовком: Hiragino Mincho ProN W3, 20 pt,
  tracking 8, цвет `sakuraDeep` (#E0607E). Текст через `Text(verbatim:)` — не локализуется.
- `HankoSeal` — красная печать (#D9433B, `Color.shu`) с иероглифом 箱 (Hiragino Mincho ProN W6),
  повёрнута на −4°. «Хако» по-японски — «коробка».
- Основная кнопка — `.buttonStyle(.glassProminent)`, `.tint(.sakuraDeep)`, `.controlSize(.extraLarge)`;
  второстепенные — `.buttonStyle(.glass)`. Ошибки — цвет `Color.shu`.
- Акцент окна — `.tint(.sakuraDeep)` на корне `ContentView` и цвет #E0607E в `AccentColor.colorset`:
  segmented-пикеры, переключатели и меню остаются розовыми без точечного `.tint`.
- Экраны выровнены по левому краю с горизонтальным отступом 96 pt.
- `reveal(_:order:)` — поочерёдное появление: opacity + сдвиг на 16 pt, задержка 0.08 с × `order`.
  При Reduce Motion сдвига нет, остаётся только fade.

## SakuraBackground

`Color.white` и четыре круга (#FFB7C5, #E8B4C8, #F8C8D4, #FADADD) диаметром 520–700 pt
у краёв окна; позиции заданы в долях размера окна через `GeometryReader`.
Общие `opacity(0.75)` и `blur(radius: 140)`. Пятна медленно дрейфуют
(`easeInOut` 20 с, `repeatForever(autoreverses: true)`); при Reduce Motion анимация не запускается.
Фон игнорирует safe area и заходит под скрытый заголовок окна.

## WelcomeView

`WelcomeView(onStart:onContinueWithoutAccount:)` — печать 箱, подпись «ようこそ», заголовок
«Добро пожаловать в Hako», подзаголовок «Лаунчер Minecraft для macOS», кнопка «Начать»
(`.keyboardShortcut(.defaultAction)` — срабатывает по Return) вызывает `onStart`.
Рядом «Продолжить без аккаунта» вызывает `onContinueWithoutAccount` и открывает гостевой лаунчер.
В правом верхнем углу — `AppLanguagePicker` «Русский / English»: язык меняется сразу ([[UI/Localization]]).
В правом нижнем углу — дисклеймер «Hako is not affiliated with or endorsed by Mojang or Microsoft.
Minecraft is a trademark of Microsoft.» (`.caption`, `.tertiary`). Элементы появляются через `reveal`.

## LoginView

`LoginView(onBack: () -> Void)` — кнопка «Назад», подпись «サインイン», заголовок «Вход в Microsoft»,
подсказка «Откройте microsoft.com/link и введите код.» и блок по фазе входа:

| Фаза | Содержимое |
|------|------------|
| `requestingCode` | «Получаем код…» со спиннером |
| `waiting(DeviceCode)` | Код моноширинным 56 pt в стеклянной карточке (`glassEffect`), кнопки «Открыть microsoft.com/link» (`openURL(verificationUri)`, Return) и «Скопировать код» (`NSPasteboard`, после нажатия — «Скопировано»), статус «Ждём подтверждения…» |
| `signingIn` | «Входим в Xbox Live и Minecraft…» |
| `failed(String)` | Текст ошибки и «Попробовать снова» (увеличивает `attempt`) |

Вход выполняет `signIn()` в `.task(id: attempt)`: SwiftUI отменяет задачу, когда экран исчезает
(«Назад»), и перезапускает при смене `attempt`. Ошибки после отмены не показываются.
Шаги входа — [[Auth/MicrosoftAuth]]; если Minecraft недоступен, вход завершается только с аккаунтом
Microsoft. При успехе токены сохраняются в `TokenKeychain`, затем вставляется `Account` и одним сохранением переносится гостевое игровое время ([[Data/Persistence]]) — [[UI/ContentView]]
сам переключается на главную страницу [[UI/Launcher]].

## Главная страница

После входа или выбора «Продолжить без аккаунта» показывается главная страница лаунчера — см. [[UI/Launcher]].

## Windows-клиент

Экраны WinUI 3 в `windows/Hako/`. `MainWindow` (`windows/Hako/MainWindow.xaml.cs`) показывает страницу по
`LauncherScreen` из `AppCoordinator.Screen` (`windows/Hako.Core/AppCoordinator.cs`). Фон окна — системный
`MicaBackdrop` в `windows/Hako/MainWindow.xaml`; WinUI `TitleBar` показывает Hako, а экраны и `NavigationView` расположены под заголовком.
Иконки Windows в `windows/Hako/Assets/` используют коробку с сакурой из `macos/Hako/Hako/AppIcon.icon/Assets/box.png`
на бело-розовом градиенте: `AppIcon.ico` встроена в EXE и задаётся через `AppWindow.SetIcon`, `AppIcon.png` используется
в `TitleBar`, PNG-ресурсы логотипов и заставки подключены через `Package.appxmanifest`.
Начальный целевой размер клиентской области — 1280×720 эффективных пикселей с учётом DPI; итоговый размер окна ограничивается рабочей областью монитора и центрируется в ней.

### Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `windows/Hako/MainWindow.xaml`, `windows/Hako/MainWindow.xaml.cs` | `MainWindow`, `ShowScreen`, `RootNavigation_SelectionChanged` | Окно, переключение страниц, `NavigationView` и `Frame` |
| `windows/Hako/WelcomePage.xaml`, `windows/Hako/WelcomePage.xaml.cs` | `WelcomePage` | Приветствие, выбор языка, варианты Hako/Microsoft и гостевой вход |
| `windows/Hako/LoginPage.xaml`, `windows/Hako/LoginPage.xaml.cs` | `LoginPage` | Фазы входа в Microsoft |
| `windows/Hako/HakoLoginPage.xaml`, `windows/Hako/HakoLoginPage.xaml.cs` | `HakoLoginPage` | Вход и регистрация Hako через Telegram, ожидание подтверждения, countdown, повтор и отмена |
| `windows/Hako/LoadingPage.xaml`, `windows/Hako/LoadingPage.xaml.cs` | `LoadingPage` | Индикатор до завершения `InitializeAsync` |
| `windows/Hako/BuildsPage.xaml`, `windows/Hako/BuildsPage.xaml.cs` | `BuildsPage`, `BuildCardModel` | Раздел «Сборки»: пустое состояние или карточки сборок — [[UI/Launcher#Windows-клиент]] |
| `windows/Hako/InstanceCreationPage.xaml(.cs)`, `InstanceCreationChoiceDialog.cs` | `InstanceCreationPage`, `InstanceCreationChoiceDialog` | Создание своей сборки: выбор Minecraft, Vanilla/Fabric и параметров запуска |
| `windows/Hako/InstanceProfilePage.xaml(.cs)` | `InstanceProfilePage` | Профиль сборки: запуск, установка, разделы контента, скриншоты, настройки, дублирование и резервная копия |
| `windows/Hako/InstanceScreenshotsView.xaml(.cs)`, `ScreenshotThumbnails.cs` | `InstanceScreenshotsView`, `ScreenshotThumbnails` | Сетка скриншотов с ограниченными миниатюрами, выбором, drag-copy и перемещением в Корзину |
| `windows/Hako/InstanceWorldsView.xaml(.cs)` | `InstanceWorldsView`, `InstanceWorldRow` | Миры, метаданные, открытие saves, дублирование, резервная копия, корзина и датапаки |
| `windows/Hako/InstanceContentView.xaml(.cs)` | `InstanceContentView`, `InstanceContentRow` | Локальный контент по `InstanceContentTarget`: моды, ресурспаки или датапаки выбранного мира |
| `windows/Hako/ModrinthCatalogView.xaml(.cs)` | `ModrinthCatalogView`, `ModrinthCatalogRow` | Поиск, сортировка, пагинация и установка контента Modrinth в сборку или выбранный мир |
| `windows/Hako/ContentResourceKeys.cs` | `ContentResourceKeys` | Сопоставление UI-кодов подтверждений и ошибок Core с локализованными ресурсами |
| `windows/Hako/WorldResourceKeys.cs` | `WorldResourceKeys` | Маппинг ошибок мира, типов игры, неизвестных значений и форматирования размера/даты |
| `windows/Hako/BackupResourceKeys.cs` | `BackupResourceKeys` | Локализация типизированных ошибок архивов и диагностических путей |
| `windows/Hako/LauncherServices.cs` | `LauncherServices`, `INavigationGuard`, `CoalescedRefresh`, `HakoDialogs` | App-lifetime сервисы, защита ухода со страницы, маршалинг событий Core, очередь диалогов и отмена подтверждения при закрытии окна |
| `windows/Hako.Core/Playtime/PlaytimeCoordinator.cs` | `PlaytimeCoordinator` | Живые итоги аккаунта/гостя и сборки; UI получает обновления через `DispatcherQueue` |
| `windows/Hako/SettingsPage.xaml`, `windows/Hako/SettingsPage.xaml.cs` | `SettingsPage` | Разделы «Основные», «Игра» и «Профиль», выход |
| `windows/Hako/MinecraftSkinView.xaml`, `windows/Hako/MinecraftSkinView.xaml.cs` | `MinecraftSkinView` | Скин, камера, кадры ходьбы, отмена и владение renderer по видимости профиля |
| `windows/Hako/MinecraftSkinPreviewService.cs`, `MinecraftSkinImageDecoder.cs` | `MinecraftSkinPreviewService`, `MinecraftSkinImageDecoder` | Mojang skin PNG, проверка контейнера/размера, straight RGBA, нормализованный cache по UUID |
| `windows/Hako/Graphics/MinecraftSkinRenderer.cs`, `MinecraftSkinGeometry.cs` | `MinecraftSkinRenderer`, `MinecraftSkinGeometry` | D3D11 Composition surface и независимое построение UV/pose geometry |

### Экраны

| `LauncherScreen` | Страница | Содержимое |
|------------------|----------|------------|
| `Loading` | `LoadingPage` | Кольцо прогресса на время чтения настроек и аккаунта |
| `Welcome` | `WelcomePage` | Заголовок и подзаголовок, `LanguageBox` (Русский / English), варианты входа Microsoft (`StartButton`), Hako (`HakoButton`) и явный гостевой вход (`GuestButton`), `InfoBar` со `StartupNotice`, дисклеймер |
| `Login` | `LoginPage` | Независимый Microsoft device-code flow: получение кода, ожидание, Xbox/Minecraft и ошибка с повтором; Hako-состояние его не заменяет |
| `HakoLogin` | `HakoLoginPage` | Username для входа, отдельная регистрация, ожидание Telegram-подтверждения с countdown, открытие/копирование проверенной ссылки только по нажатию; отмена возвращает на предыдущий экран. Потерянный ответ одноразового exchange требует нового входа и не повторяет регистрацию |
| `Builds` | `BuildsPage` | Пустое состояние с кнопкой «Создать сборку» или сетка карточек; ошибка реестра `instances.json` — `InfoBar` |
| `Instance` | `InstanceProfilePage` | Профиль сборки `AppCoordinator.SelectedInstanceId`: время игры для выбранной сборки, моды только для Fabric, ресурспаки, миры, скриншоты и настройки; меню действий дублирует сборку и создаёт резервную копию |
| `InstanceCreation` | `InstanceCreationPage` | Форма «Своя сборка» |
| `Settings` | `SettingsPage` | Слева `ListView` из четырёх разделов (`SettingsSection.General`, `Game`, `Storage`, `Profile`), справа панель выбранного; профиль независимо показывает Microsoft, Hako и guest/offline playtime, Hako status/verify/logout и Microsoft Minecraft profile; «Хранилище» показывает место на диске и размер сборок по девяти категориям |
| `ModpackCatalog` | `ModpackCatalogPage` | Поиск готовых сборок Modrinth, фильтр Minecraft, версии и ссылка на проект |
| `ModpackCreation` | `ModpackCreationPage` | Форма выбранной закреплённой версии `.mrpack`, асинхронная проверка и постановка в очередь |

### Навигация

- Для `Builds`, `Instance`, `InstanceCreation` и `Settings` `MainWindow` показывает `RootNavigation`
  (`NavigationView`, `PaneDisplayMode=LeftCompact`) — аналог рейла macOS: «Сборки», разделитель, пункты сборок
  (иконка, имя, подсказка с версией) и «+» (`SelectsOnInvoked=False`, открывает `InstanceCreationChoiceDialog`);
  в `FooterMenuItems` — «Настройки» и аккаунт (Minecraft name, Xbox gamertag, затем Hako username или «Гость»), который открывает раздел «Профиль».
  Пункты сборок обновляются на месте по `InstanceStore.InstancesChanged`; если выбранная сборка исчезла,
  `AppCoordinator.CloseInstance` возвращает к «Сборкам». Содержимое — `ShellFrame`; для остальных экранов видим только `FlowFrame`.
  При скрытии корневой навигации страница Settings может
  остаться текущей страницей `ShellFrame`, поэтому `MainWindow` отдельно передаёт ей активность экрана и окна.
- Кнопка «+» и пустое состояние сборок открывают `InstanceCreationChoiceDialog`: «Своя сборка» ведёт в
  `InstanceCreationPage`, «Готовая с Modrinth» — в `ModpackCatalogPage`. Выбор версии открывает `ModpackCreationPage`;
  после подготовки она сохраняет сборку через очередь и возвращает к списку карточек. Обе страницы используют
  тот же `RootNavigation`, локализацию ru/en и `INavigationGuard` для отмены формы.
- Переход по рейлу и закрытие окна сначала спрашивают текущую страницу через `INavigationGuard.ConfirmLeaveAsync`:
  форма создания подтверждает отмену, профиль сборки дописывает настройки и спрашивает о несохранённом имени.
  Закрытие перехватывается в `AppWindow.Closing`, затем `App.ShutdownServicesAsync` освобождает
  `InstallationCoordinator` (выход не ставит установку на паузу), `InstanceStore` и `MojangClient`.
- Все `ContentDialog`, включая выход и ошибки Settings/Welcome, показываются через очередь `HakoDialogs`: WinUI допускает один открытый диалог на `XamlRoot`.
  Ожидание асинхронное; закрытие окна отменяет показанные и ожидающие диалоги. Settings перечитывает строки при показе из очереди и смене языка, сохраняя исходную кнопку по умолчанию.
- `InstanceContentController.StateChanged` приходит на потоке операции. `InstanceContentView` ставит обновление
  в `DispatcherQueue`, заново читает актуальный снимок по UUID/kind/worldId и отбрасывает ревизии старше уже применённой.
  При уходе со страницы отменяются её чтение и поиск; установка контента продолжается в app-lifetime контроллере.
- `InstanceWorldsView` при входе и возврате окна читает список через Core; generation + cancellation отбрасывают
  результаты прежней сборки или загрузки. `GameSessionEnded`, изменения списка сборок и busy-состояния обновляют
  список/доступность. Раскрытая карточка одна; её `InstanceContentView` адресует датапаки UUID сборки и worldId.
  `DuplicateAsync` и `TrashAsync` вызываются через Core после подтверждения в общей очереди `HakoDialogs`.
- Подтверждения замены, удаления, зависимостей и несовместимости локализует `HakoDialogs.ConfirmContentAsync`.
  Для связанного ресурспака показывает варианты из `request.Files` с required/optional notes и возвращает выбранный
  filename; закрытие окна отменяет ожидающее подтверждение и завершает callback выбором «Отмена».
- Закрытие окна вызывает `CancelSignIn()` и завершает preview service; текущая страница сначала отменяет
  загрузку и останавливает frame callback, затем освобождает renderer. Деактивация или сворачивание окна,
  переход на другой корневой экран и выбор «Основные» выключают skin control без сброса камеры.

### Настройки

- «Основные»: `LanguageBox` меняет язык сразу. Ошибка сохранения — диалог `LanguageSaveError`.
- «Игра»: путь к Java (`TextBox` и `Microsoft.Windows.Storage.Pickers.FileOpenPicker` для `.exe`), аргументы
  Java и Minecraft, `JavaMemorySlider`, полноэкранный режим, ширина и высота. Изменения сохраняются через
  `UserSettingsService.SetGameLaunchDefaultsAsync` с задержкой 500 мс и при уходе со страницы; неверный размер
  окна остаётся черновиком с ошибкой и не затирает сохранённое значение. Подробности — [[UI/Launcher#Windows-клиент]].
- «Хранилище» содержит карточку места на томе с занятым, свободным и общим объёмом и долей сборок,
  а также карточку размера всех сборок с разбивкой на миры, файлы Minecraft, скриншоты, Java, моды,
  датапаки, ресурспаки, резервные копии сборок и резервные копии миров. Два вида резервных копий показаны
  отдельными строками; размер считается по логической длине файлов, reparse points пропускаются, обход
  выполняется в фоне и отменяется при уходе из раздела.
- «Профиль» для Microsoft-аккаунта показывает слева две оформленные карточки: Xbox (аватар
  `AccountAvatar`, при ошибке загрузки — `FontIcon`, gamertag и email) и Minecraft (пиксельная голова
  с лицом/шапкой, ник и бейдж `Java Edition`). Если Minecraft не подключён, карточка объясняет это
  и показывает Steve; гостевое состояние остаётся прежним и 3D-модель не показывается. Кнопка выхода
  находится под карточками.
- При фактической ширине `ProfilePanel` от 800 DIP карточки занимают левую колонку минимум 360 DIP,
  модель — правую минимум 240 DIP, между ними 24 DIP; область модели минимум 400 DIP по высоте.
  Ниже 800 DIP модель становится строкой под карточками высотой 360 DIP. В коротком окне содержимое
  Settings прокручивается. Смена языка обновляет текст без смены UUID или повторной загрузки.
- Выход открывает `ContentDialog` с подтверждением (`SignOutButton_Click`). Подтверждение вызывает
  `AppCoordinator.LogoutAsync`; при ошибке удаления показывается `LogoutFailed`.

`MinecraftSkinView` обновляет камеру по мышиному drag и стрелкам. `CompositionTarget.Rendering`
подписан только при доступном 3D preview и разрешённых системных анимациях; reduced motion оставляет
статичную позу. `MainWindow` передаёт состояние активации/видимости окна, в том числе при logout, когда
`ShellFrame` не навигирует с текущей Settings-страницы. Страница и control снимают собственные события
при навигации/скрытии и освобождении.

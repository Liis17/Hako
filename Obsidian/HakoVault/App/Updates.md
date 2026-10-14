# Updates

Parent: [[Index]]

## Назначение

macOS-клиент проверяет стабильные macOS-релизы `macos-vX.Y.Z` в GitHub Releases, скачивает `Hako.dmg`, заменяет свой `.app` и перезапускается. Проверка выполняется при запуске и раз в 6 часов с переключателем и кнопкой «Проверить сейчас»; точка на «Настройках» в рейле показывается при доступном обновлении.

Переход уже установленных клиентов из прежнего pre-release `nightly` выполняется вручную. Автоматической совместимости или моста с `nightly` нет. Обновление Windows-клиента не реализовано.

## Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `.github/workflows/release.yml` | — | Запускает независимые платформенные релизы после проверки повышения версии |
| `.github/scripts/detect_release.py` | — | Сравнивает версии каждой платформы между `github.event.before` и текущим коммитом |
| `.github/scripts/publish_release.py` | — | Создаёт или возобновляет draft GitHub Release, загружает ассеты и публикует его |
| `macos/Hako/Version.xcconfig` | — | Источник `MARKETING_VERSION` и `CURRENT_PROJECT_VERSION` для Debug и Release |
| `macos/Hako/Hako-Info.plist` | `HakoCommit` | Частичный Info.plist; CI передаёт SHA собранного коммита |
| `macos/Hako/Hako/Updates/AppRelease.swift` | `AppRelease`, `AppReleaseVersion`, `AppReleaseClient` | Чтение стабильных версий macOS, выбор максимальной версии и загрузка DMG с проверкой размера и SHA-256 |
| `macos/Hako/Hako/Updates/AppUpdateInstaller.swift` | `AppUpdateInstaller` | Проверка метаданных приложения, извлечение из DMG, замена бандла и перезапуск |
| `macos/Hako/Hako/Updates/AppUpdateCoordinator.swift` | `AppUpdateCoordinator` | Состояние UI, периодическая проверка и установка |
| `macos/Hako/Hako/Views/Launcher/SettingsView.swift` | `AppUpdateCard` | Карточка обновления показывает новую семантическую версию |
| `macos/Hako/Hako/Views/Launcher/LauncherRail.swift` | `RailButton.showsBadge` | Точка на «Настройках», пока `release != nil` |

## Версия приложения

`macos/Hako/Version.xcconfig` содержит текущую исходную версию `MARKETING_VERSION = 1.0.0` и номер сборки `CURRENT_PROJECT_VERSION = 1`; Debug и Release таргета `Hako` используют этот файл. Настройка `HAKO_COMMIT` пустая в локальной конфигурации и CI задаёт её равной `$GITHUB_SHA`.

Стабильный macOS-релиз имеет тег ровно вида `macos-vX.Y.Z`, без ведущих нулей и дополнительных суффиксов. Текущая версия берётся из `CFBundleShortVersionString`; историческая версия `1.0` сравнивается как `1.0.0`. Сравнение числовое по компонентам, поэтому `1.10.0` новее `1.9.9`.

`AppUpdateCoordinator` получает текущую версию отдельно от коммита. `currentCommit == nil` по-прежнему обозначает локальную сборку: координатор не проверяет сеть, а раздел «О приложении» показывает версию, номер сборки и «локальная сборка». Коммит используется только как происхождение релиза и для проверки установленного приложения, не для определения новизны.

## Публичные контракты

| Контракт | Поведение |
|----------|-----------|
| `AppReleaseClient.latest()` | `GET api.github.com/repos/Liis17/Hako/releases?per_page=100&page=1`; следует по `Link: rel="next"` до конца списка и выбирает максимальную числовую опубликованную стабильную macOS-версию |
| Кандидат `AppRelease` | Тег `macos-vX.Y.Z`, `draft == false`, `prerelease == false`, есть `published_at`, `target_commitish` содержит полный SHA из 40 hex-символов и приложен `Hako.dmg`; релизы Windows, `nightly` и malformed tags игнорируются |
| Пустой список / HTTP 404 | `latest()` возвращает `nil`; проверка записывает успешный результат без сообщения об ошибке и без предложения обновления |
| Ошибки API | Сетевые ошибки, не-2xx статусы кроме 404 и некорректный JSON остаются ошибками ручной проверки; автоматическая проверка их не показывает |
| `AppRelease.isUpdate(for:)` | `true`, только если релиз подходит macOS-клиенту и его числовая версия новее текущей версии бандла |
| `AppRelease.commit` | SHA коммита сборки для проверки происхождения релиза и извлечённого приложения |
| `AppRelease.Asset.sha256` | Из поля `digest` вида `sha256:<hex>`; без него загрузка отклоняется |
| `AppUpdateCoordinator.automaticCheckKey` | `"checkUpdatesAutomatically"` в `UserDefaults.standard`, по умолчанию `true` |

## Установка

1. Отказ, если `InstanceStore.contentBusy` не пуст (повторяется после загрузки): перезапуск прервал бы файловые операции сборок. Очередь установки Minecraft продолжается после перезапуска, игры от лаунчера не зависят ([[Minecraft/Launching]]).
2. `preflight`: путь не содержит `/AppTranslocation/`, папка `.app` доступна на запись — иначе просьба переместить Hako в «Программы».
3. Загрузка во временную папку, проверка размера и SHA-256.
4. `hdiutil attach -nobrowse -readonly -noautoopen` во временную папку `itemReplacementDirectory` на томе приложения, копирование `Hako.app`, `hdiutil detach -force`.
5. Копия должна иметь тот же `CFBundleIdentifier`, числовую `CFBundleShortVersionString`, что версия релиза (старое `1.0` эквивалентно `1.0.0`), и `HakoCommit == release.commit`; затем выполняется `codesign --verify --deep --strict`.
6. `FileManager.replaceItemAt` атомарно заменяет запущенный бандл; `/bin/sh` ждёт выхода текущего PID и вызывает `open`; затем `NSApp.terminate`. Отложенное переименование сборки может отменить выход — тогда новая версия откроется после выхода.

## Ограничения

- Обновление обслуживает только macOS-клиент; Windows-клиент пока начальная оболочка WinUI 3.
- Сборки CI подписаны ad-hoc, без Developer ID и нотаризации. Загрузка через `URLSession` не ставит карантин, поэтому заменённое приложение открывается без подтверждения Gatekeeper.
- Анонимный GitHub API ограничен 60 запросами в час с IP; проверка раз в 6 часов в него укладывается.
- В macOS 27 `hdiutil attach/detach` выводит предупреждение об устаревании, но работает.

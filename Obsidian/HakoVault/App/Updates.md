# Updates

Parent: [[Index]]

## Назначение

Hako проверяет pre-release `nightly` на GitHub, скачивает `Hako.dmg`, заменяет свой `.app` и перезапускается.
Установка одной кнопкой, проверка при запуске и раз в 6 часов с переключателем и кнопкой «Проверить сейчас»,
точка на «Настройках» в рейле — решения пользователя.

## Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `.github/workflows/release.yml` | — | Сборка Release с `HAKO_COMMIT=$GITHUB_SHA`, публикация `Hako.dmg` в `nightly` |
| `macos/Hako/Hako-Info.plist` | `HakoCommit` | Частичный Info.plist (`INFOPLIST_FILE`), остальные ключи генерируются; лежит вне синхронизированной папки `Hako/`, чтобы не попасть в ресурсы |
| `macos/Hako/Hako/Updates/AppRelease.swift` | `AppRelease`, `AppReleaseClient` | Ответ GitHub API, проверка «есть ли обновление», загрузка с проверкой размера и SHA-256 |
| `macos/Hako/Hako/Updates/AppUpdateInstaller.swift` | `AppUpdateInstaller` | Коммит приложения, предварительные проверки, извлечение из DMG, замена бандла, перезапуск |
| `macos/Hako/Hako/Updates/AppUpdateCoordinator.swift` | `AppUpdateCoordinator` | Состояние для UI, периодическая проверка, установка |
| `macos/Hako/Hako/Views/Launcher/SettingsView.swift` | `AppUpdateCard` | Карточка «Обновления» в «О приложении» |
| `macos/Hako/Hako/Views/Launcher/LauncherRail.swift` | `RailButton.showsBadge` | Точка на «Настройках», пока `release != nil` |

## Версия приложения

`MARKETING_VERSION`/`CURRENT_PROJECT_VERSION` не меняются между сборками, поэтому версию определяет коммит.
Настройка `HAKO_COMMIT` (пустая в проекте) подставляется в ключ `HakoCommit`; CI передаёт `$GITHUB_SHA`.
`AppUpdateInstaller.commit(of:)` возвращает `nil` для пустого значения — это локальная сборка:
она не проверяет обновления, «О приложении» показывает «локальная сборка» и ссылку на релизы.

## Публичные контракты

| Контракт | Поведение |
|----------|-----------|
| `AppReleaseClient.latest()` | `GET api.github.com/repos/Liis17/Hako/releases/tags/nightly`; 404 → `nil` (CI удаляет релиз перед публикацией) |
| `AppRelease.commit` | `target_commitish`: CI создаёт релиз с `--target <SHA>`, поэтому это полный SHA |
| `AppRelease.isUpdate(for:)` | `true`, если коммит релиза — 40 hex-символов, есть ассет `Hako.dmg` и коммит отличается от текущего. Релиз только движется вперёд, поэтому отличие = новее |
| `AppRelease.Asset.sha256` | Из поля `digest` вида `sha256:<hex>`; без него загрузка отклоняется |
| `AppUpdateCoordinator.check(manual:)` | Автоматическая проверка не показывает ошибки; ручная показывает ошибку и сообщение, если релиз сейчас пересоздаётся |
| `AppUpdateCoordinator.automaticCheckKey` | `"checkUpdatesAutomatically"` в `UserDefaults.standard`, по умолчанию `true` |

## Установка

1. Отказ, если `InstanceStore.contentBusy` не пуст (повторяется после загрузки): перезапуск прервал бы файловые операции сборок.
   Очередь установки Minecraft продолжается после перезапуска, игры от лаунчера не зависят ([[Minecraft/Launching]]).
2. `preflight`: путь не содержит `/AppTranslocation/`, папка `.app` доступна на запись — иначе просьба переместить Hako в «Программы».
3. Загрузка во временную папку, проверка размера и SHA-256.
4. `hdiutil attach -nobrowse -readonly -noautoopen` во временную папку `itemReplacementDirectory` на томе приложения,
   копирование `Hako.app`, `hdiutil detach -force`.
5. Копия должна иметь тот же `CFBundleIdentifier`, `HakoCommit == release.commit` и проходить `codesign --verify --deep --strict`.
6. `FileManager.replaceItemAt` атомарно заменяет запущенный бандл; `/bin/sh` ждёт выхода текущего PID и вызывает `open`;
   затем `NSApp.terminate`. Отложенное переименование сборки может отменить выход — тогда новая версия откроется после выхода.

## Ограничения

- Сборки CI подписаны ad-hoc, без Developer ID и нотаризации. Загрузка через `URLSession` не ставит карантин,
  поэтому заменённое приложение открывается без подтверждения Gatekeeper.
- Анонимный GitHub API ограничен 60 запросами в час с IP; проверка раз в 6 часов в него укладывается.
- В macOS 27 `hdiutil attach/detach` выводит предупреждение об устаревании, но работает.

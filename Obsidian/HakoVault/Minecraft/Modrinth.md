# Modrinth

Parent: [[Index]]

## Назначение

Каталог Modrinth для сборки: поиск модов Fabric и ресурспаков, выбор совместимой версии, проверка загрузки
и установка вместе с обязательными зависимостями. Страница каталога описана в [[UI/Launcher]];
официальный Fabric API при создании сборки — в [[Minecraft/Installation]].

## Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `macos/Hako/Hako/Minecraft/ModrinthClient.swift` | `ModrinthClient`, `ModrinthProject`, `ModrinthVersion`, `ModrinthSort` | API v2: поиск, версии, проекты, распознавание по хешу, загрузка |
| `macos/Hako/Hako/Instances/InstanceContentController.swift` | `install(_:in:mods:)`, `installedProjects(_:mods:)`, `ContentConfirmation` | Установка, зависимости, проверка Loader/Java, модалки |
| `macos/Hako/Hako/Instances/InstanceContent.swift` | `importItem(from:into:mods:replace:origin:)` | Публикация файла и запись происхождения мода |
| `macos/Hako/Hako/Views/Launcher/ModrinthCatalogView.swift` | `ModrinthCatalogView`, `ModrinthCatalogRow` | Страница каталога |
| `macos/Hako/HakoTests/ModrinthTests.swift` | `ModrinthTests` | Фасеты, выбор версии, зависимости, повреждённая загрузка, несовместимый Loader |

## Клиент

`ModrinthClient` — actor поверх `https://api.modrinth.com/v2` с User-Agent Hako; запросы только по HTTPS,
идентификаторы проектов — ASCII-буквы и цифры. `InstanceContentController` получает клиент параметром `init`
(по умолчанию — новый `ModrinthClient`), тесты подменяют `URLSession`.

| Метод | Запрос | Контракт |
|-------|--------|----------|
| `search(_:mods:minecraft:sort:offset:)` | `GET /search`, `limit=20` | Пустой запрос не передаётся; `+` кодируется как `%2B`. `ModrinthSort`: relevance, downloads, newest, updated |
| `latestVersion(project:mods:minecraft:)` | `GET /project/{id}/version` | Точная версия Minecraft и загрузчик `fabric` (моды) или `minecraft` (ресурспаки); release → beta → alpha, внутри канала — новейшая по `date_published` |
| `projects(_:)` / `versions(_:)` | `GET /projects`, `GET /versions` | Названия, иконки и типы зависимостей; проект по `version_id` |
| `projectIDs(of:)` | `POST /version_files` (`sha512`) | Хеширует файлы на акторе и возвращает `URL → project_id` |
| `download(_:into:)` | URL файла | Временный файл переносится под именем из Modrinth; размер и SHA-512 обязаны совпасть |

Фасеты модов: `project_type:mod`, `categories:fabric`, `versions:<mc>`, `environment!=server_only`,
`environment!=dedicated_server_only` — серверные моды скрыты. Ресурспаков: `project_type:resourcepack`, `versions:<mc>`.
`ModrinthVersion.file(mods:)` выбирает primary либо первый `.jar`/`.zip` без `sources/dev/javadoc-jar`,
с HTTPS, ненулевым размером, SHA-1 и SHA-512; версия без такого файла не устанавливается.
`ModrinthProject.pageURL` — `https://modrinth.com/{project_type}/{slug ?? id}`.

## Установка

`install` выполняется через общий `perform`: действуют `disabledReason`, блокировка `contentBusy`
и очередь подтверждений. `catalogInstalling[instanceID]` хранит устанавливаемый проект для индикатора строки.

1. Последняя совместимая версия выбранного проекта; её отсутствие — ошибка.
2. Обязательные (`required`) зависимости обходятся в ширину, включая зависимости зависимостей, до 30 проектов.
   Установленные и включённые пропускаются; отключённые помечаются «Отключён — будет включён»;
   отсутствующие получают последнюю совместимую версию или пометку «Нет совместимой версии».
   Моды-зависимости ставятся только в Fabric-сборку вне очереди/установки («Нужна сборка с Fabric»);
   типы, кроме модов и ресурспаков, — «Не поддерживается Hako».
3. Если список не пуст, модалка «Нужны зависимости» предлагает «Добавить с зависимостями»
   (нет кнопки, если ставить нечего), «Только мод»/«Только ресурспак» или «Отмена».
4. Все файлы загружаются во временную папку и проверяются до изменения сборки. Для JAR читается
   `fabric.mod.json`: `fabricloader` сравнивается с закреплённым Loader, `java` — с Java сборки, если она известна.
   Несовпадение отменяет установку; без `fabric.mod.json` или с нечитаемым предикатом проверка пропускается.
5. Публикуются сначала зависимости, затем выбранный проект; коллизия имени проходит через подтверждение замены.
   Отключённые зависимости включаются. Если ресурспак потянул моды, перезагружаются оба списка.

Моды записываются в `minecraft/.hako-mods.json` с происхождением Modrinth (проект, версия, страница, SHA-512):
в списке появляются бейдж «Modrinth» и ссылка на страницу. Fabric API сохраняет `FabricAPIDescriptor`,
поэтому продолжает получать обновления. Локальный импорт стирает происхождение файла.

## Распознавание установленного

`installedProjects` возвращает `projectID → файлы`: моды с происхождением Modrinth из реестра и
остальные файлы (кроме папок-паков), распознанные через `version_files` по SHA-512. Поэтому вручную
скачанные с Modrinth файлы тоже считаются установленными. Отключённые файлы входят в результат.

## Ограничения

- Обновления ищутся только для Fabric API; моды из каталога обновляются переустановкой.
- Зависимости `optional`, `embedded` и `incompatible` не проверяются.
- Хеши файлов для распознавания пересчитываются при каждом открытии каталога и установке, без кэша.

## Решения и основания

Решения пользователя: предлагать установку недостающих зависимостей, ставить последнюю совместимую версию,
поиск, сортировка, скрытие серверных модов и проверка Loader/Java. На кнопке — SF Symbol `shippingbox`,
как у бейджа источника: использование логотипа Modrinth требует письменного разрешения Rinth, Inc.
(`COPYING.md` в репозитории `modrinth/code`).

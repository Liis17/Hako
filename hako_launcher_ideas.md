# Hako — идеи для Minecraft-лаунчера

Hako — сторонний лаунчер для Minecraft: Java Edition. Название связано с японским словом **箱 (hako)** — «коробка», поэтому концепцию лаунчера можно строить вокруг отдельных изолированных «коробок» со сборками.

---

## 1. Boxes вместо обычных instances

Каждая сборка Minecraft представляется как отдельная **Box**.

Внутри Box находятся:

- версия Minecraft;
- выбранный mod loader;
- список модов;
- конфиги;
- resource packs;
- shader packs;
- saves;
- настройки Java;
- JVM-параметры;
- параметры запуска.

Основные действия:

- **Create Box**
- **Clone Box**
- **Rename Box**
- **Archive Box**
- **Delete Box**
- **Pack Box**
- **Share Box**
- **Export Box**

Идея может стать основной визуальной и продуктовой концепцией Hako.

---

## 2. Snapshots и Rollback

Перед важными изменениями Hako автоматически создаёт snapshot состояния Box.

Например перед:

- обновлением Minecraft;
- обновлением loader;
- обновлением модов;
- массовым обновлением сборки;
- изменением конфигурации;
- запуском мира на новой версии игры.

Snapshot может хранить:

- версии всех модов;
- loader;
- Minecraft version;
- manifest;
- важные config-файлы;
- список файлов сборки.

Если обновление что-то сломало:

**Rollback → вернуть Box в рабочее состояние.**

---

## 3. Diff изменений сборки

Перед обновлением показывать пользователю понятный список изменений.

Пример:

```text
+ Sodium 0.7.2
↑ Fabric API 0.130 → 0.131
- OldMod
~ options.txt изменён
```

Можно показывать:

- добавленные моды;
- удалённые моды;
- обновлённые моды;
- изменённые конфиги;
- изменение версии Minecraft;
- изменение loader;
- изменение Java runtime.

---

## 4. Проверка конфликтов модов до запуска

Hako может анализировать сборку ещё до старта игры.

Проверять:

- зависимости модов;
- обязательные библиотеки;
- несовместимые моды;
- поддерживаемые версии Minecraft;
- поддерживаемый loader;
- несовместимые версии loader;
- known incompatibilities;
- client-only / server-only ограничения.

Вместо краша пользователь получает понятное предупреждение:

```text
Iris требует Sodium версии >= X.
Установленная версия Sodium слишком старая.
```

---

## 5. Crash Analyzer

После падения Minecraft Hako автоматически анализирует:

- crash-report;
- latest.log;
- loader logs;
- Java exception;
- список загруженных модов.

И показывает человеческое объяснение.

Пример:

```text
Вероятная причина:
Sodium 0.7 несовместим с Iris 1.9.

[Отключить Sodium]
[Обновить Sodium]
[Открыть лог]
[Запустить Safe Mode]
```

---

## 6. Safe Mode

Специальный режим запуска проблемной Box.

Варианты:

- запустить без всех пользовательских модов;
- оставить только loader;
- отключить последние добавленные моды;
- отключить моды, которые Crash Analyzer считает подозрительными;
- временно отключить resource packs;
- временно отключить shaders.

После успешного запуска можно включать компоненты обратно.

---

## 7. Performance Presets

Готовые профили оптимизации.

Например:

### Vanilla
Без модификаций производительности.

### Balanced
Небольшой набор безопасных оптимизаций.

### Performance
Максимум FPS и оптимизация памяти.

### Shaders
Оптимизации + совместимая конфигурация для шейдеров.

Hako может предлагать подходящие моды, например Sodium/Lithium/FerriteCore и аналоги, но обязательно показывать пользователю, что именно будет установлено.

---

## 8. Java Manager

Hako самостоятельно управляет Java runtime.

Задачи:

- определять необходимую версию Java;
- скачивать подходящий runtime;
- хранить несколько версий Java одновременно;
- выбирать runtime автоматически;
- позволять выбрать собственную Java вручную.

Пример:

```text
Minecraft 1.12.2 → Java 8
Minecraft 1.20.1 → Java 17
Minecraft 1.21.x → Java 21
Minecraft 26.x   → Java 25
```

Пользователь не должен вручную настраивать `JAVA_HOME`.

---

## 9. Minecraft Profile

Полноценный профиль Minecraft внутри Hako.

Можно показывать:

- username;
- UUID;
- 3D-модель персонажа;
- текущий skin;
- тип модели Classic / Slim;
- доступные cape.

Функции:

- загрузка нового skin;
- смена Classic / Slim;
- сброс skin на стандартный;
- выбор доступного cape;
- отключение cape.

---

## 10. 3D-превью скина

Перед применением skin пользователь видит 3D-модель.

Возможности:

- вращение;
- zoom;
- Classic / Slim preview;
- просмотр front/back;
- предпросмотр cape;
- сравнение нового и текущего skin.

---

## 11. Единый каталог Modrinth + CurseForge

Один интерфейс поиска независимо от источника.

Например пользователь ищет:

```text
Iris
```

Hako может показать варианты из:

- Modrinth;
- CurseForge.

Можно добавить фильтры:

- все источники;
- только Modrinth;
- только CurseForge.

При этом внутренняя архитектура должна использовать общий интерфейс `ContentProvider`.

---

## 12. Поиск всех типов контента

Единый каталог не только для модов.

Категории:

- Mods
- Modpacks
- Resource Packs
- Shaders
- Datapacks

Hako автоматически фильтрует результаты по текущей Box:

```text
Minecraft 1.21
Fabric
```

→ показываются только совместимые проекты.

---

## 13. Автоматическая установка зависимостей

При установке мода Hako сам определяет необходимые зависимости.

Например:

```text
Mod A
 ├── требует Fabric API
 ├── требует Cloth Config
 └── рекомендует Mod Menu
```

Можно разделять зависимости на:

- required;
- optional;
- recommended;
- incompatible.

---

## 14. Синхронизация Box между компьютерами

Синхронизировать не обязательно сами `.jar`.

Можно хранить:

- manifest;
- project IDs;
- version IDs;
- configs;
- saves;
- launcher settings.

На другом ПК Hako самостоятельно скачивает моды заново из Modrinth/CurseForge/Mojang.

Это сильно уменьшает объём синхронизации.

---

## 15. Share Code

Пользователь нажимает:

**Share Box**

и получает короткий код:

```text
HAKO-A7FK2P
```

Другой пользователь вводит его в Hako, после чего лаунчер:

- получает manifest;
- создаёт Box;
- скачивает Minecraft;
- устанавливает loader;
- скачивает моды;
- применяет configs.

Для этого можно сделать небольшой backend Hako.

---

## 16. Server Pack Sync

Сервер может публиковать Hako manifest своей сборки.

При попытке входа:

```text
Для сервера требуется Hako Pack v14.

3 мода нужно обновить
1 мод нужно добавить
2 файла config изменились

[Синхронизировать и запустить]
```

Полезно для:

- приватных серверов;
- серверов друзей;
- небольших сообществ;
- модовых серверов.

---

## 17. Quick Join серверов

Отдельный раздел серверов.

Карточка сервера может показывать:

- название;
- адрес;
- ping;
- количество игроков;
- версию Minecraft;
- требуемую Box;
- статус сборки.

Кнопка:

**Play / Join**

Если нужной Box нет — Hako может создать её автоматически.

---

## 18. Save Manager

Отдельный интерфейс управления мирами.

Показывать:

- название мира;
- скриншот;
- версию Minecraft;
- последнее открытие;
- размер;
- game mode;
- seed, если доступен;
- используемую Box.

Действия:

- backup;
- restore;
- clone;
- rename;
- открыть папку;
- экспортировать мир.

---

## 19. Автоматические backup миров

Hako может автоматически сохранять миры.

Варианты политики:

- backup перед каждым запуском;
- backup раз в день;
- backup перед обновлением Minecraft;
- backup перед изменением modpack;
- хранить последние N копий.

Можно использовать ротацию:

```text
Последние 5 запусков
Последние 7 дней
Последние 4 недели
```

---

## 20. Галерея screenshots

Общий раздел скриншотов всех Box.

Функции:

- просмотр;
- сортировка;
- удаление;
- копирование;
- открыть в Finder/Explorer;
- показать, из какой Box сделан screenshot;
- привязка к миру/серверу;
- быстрый экспорт.

---

## 21. Локальная статистика

Статистика использования без обязательной телеметрии.

Например:

```text
Minecraft 26.3 — 42 часа
Create Pack — 18 часов
Vanilla — 7 часов

Последний запуск — вчера
```

Можно считать:

- playtime;
- количество запусков;
- последний запуск;
- любимую Box;
- самые используемые версии Minecraft.

Данные могут храниться полностью локально.

---

## 22. Launch Profiles

У одной Box может быть несколько профилей запуска.

Пример:

### Normal
```text
RAM: 6 GB
```

### Shaders
```text
RAM: 10 GB
Дополнительные JVM args
```

### Debug
```text
Подробный logging
Console
Debug JVM flags
```

### Low Power
```text
RAM: 4 GB
Экономичные настройки
```

---

## 23. Portable Mode

Hako можно запускать как полностью переносимый лаунчер.

Например с внешнего SSD.

Структура:

```text
Hako/
├── launcher/
├── java/
├── libraries/
├── assets/
└── boxes/
```

Преимущества:

- перенос между ПК;
- использование на Steam Deck;
- отсутствие зависимости от системных путей;
- удобный backup.

---

## 24. Экспорт в стандартные форматы

Hako не должен запирать пользователя в своей экосистеме.

Полезно поддержать:

- `.mrpack`;
- CurseForge modpack format;
- собственный Hako manifest;
- обычный ZIP с overrides.

Пользователь всегда может забрать свою сборку.

---

## 25. Быстрый доступ к папкам

Контекстное меню каждой Box:

- Open mods folder
- Open config folder
- Open saves folder
- Open resourcepacks folder
- Open shaderpacks folder
- Open screenshots folder
- Open logs folder
- Open instance directory

Это мелкая, но очень часто используемая функция.

---

## 26. Developer Mode

Отдельный расширенный режим для разработчиков и продвинутых пользователей.

Показывать:

- итоговый launch command;
- JVM arguments;
- game arguments;
- classpath;
- Java version;
- environment variables;
- download URLs;
- SHA-1/SHA-512 hashes;
- Minecraft auth status;
- Xbox/XSTS status;
- asset loading;
- library resolution;
- network logs;
- loader metadata.

В обычном режиме всё это скрыто.

---

## 27. Download Manager

Отдельная очередь загрузок.

Показывать:

- Minecraft client;
- libraries;
- assets;
- mods;
- Java runtime;
- resource packs;
- shaders.

Функции:

- pause/resume;
- retry;
- параллельные загрузки;
- проверка hash;
- скорость;
- ETA;
- общий прогресс.

---

## 28. Общий deduplicated cache

Не хранить одинаковые файлы отдельно в каждой Box.

Можно иметь общий cache:

```text
Hako/
├── cache/
│   ├── minecraft/
│   ├── libraries/
│   ├── assets/
│   ├── mods/
│   └── java/
└── boxes/
```

Box содержит ссылки/manifest на необходимые файлы.

Плюсы:

- меньше занимаемого места;
- быстрее создание новых Box;
- не нужно повторно скачивать одинаковые моды.

---

## 29. Проверка целостности Box

Кнопка:

**Verify Box**

Hako проверяет:

- наличие файлов;
- hashes;
- повреждённые libraries;
- Minecraft client;
- assets;
- mods.

Повреждённые файлы автоматически скачиваются заново.

---

## 30. Repair Mode

Если Box не запускается, можно предложить:

```text
Repair Box
```

Этапы:

- проверить manifest;
- проверить Minecraft files;
- проверить libraries;
- проверить Java;
- проверить loader;
- проверить моды;
- восстановить отсутствующие файлы.

---

## 31. Mod Update Policies

Для каждой Box можно настроить поведение обновлений.

Например:

- только stable releases;
- разрешать beta;
- разрешать alpha;
- обновлять автоматически;
- только уведомлять;
- никогда не обновлять конкретный мод;
- закрепить версию.

---

## 32. Pin версии модов

Можно закреплять отдельные версии:

```text
Sodium 0.6.13 🔒
```

Тогда `Update All` не будет его трогать.

---

## 33. Умное Update All

Вместо слепого обновления всего:

1. Hako строит план обновления.
2. Проверяет зависимости.
3. Проверяет конфликты.
4. Создаёт snapshot.
5. Показывает diff.
6. Только после подтверждения обновляет Box.

---

## 34. Import существующих сборок

Импортировать:

- `.minecraft`;
- Prism Launcher instance;
- MultiMC instance;
- Modrinth App profile;
- CurseForge profile;
- `.mrpack`;
- ZIP-сборку.

После импорта Hako пытается определить:

- Minecraft version;
- loader;
- mods;
- configs.

---

## 35. Migration Assistant

Помощник перехода на Hako.

Например:

```text
Найдены:
• Official Minecraft Launcher
• Prism Launcher
• CurseForge
• Modrinth App

[Импортировать]
```

---

## 36. Multi-account

Поддержка нескольких Microsoft/Minecraft аккаунтов.

Функции:

- добавить аккаунт;
- быстро переключиться;
- выбрать аккаунт для конкретной Box;
- отображать skin/head;
- показать username;
- показать UUID.

Refresh token хранить в системном безопасном хранилище.

---

## 37. Offline-friendly режим

Если необходимые файлы уже скачаны, Hako должен позволять запускать игру без повторных сетевых запросов там, где это технически возможно.

При этом явно показывать:

```text
Offline mode
```

и ограничения такого режима.

---

## 38. Управление памятью и JVM

Удобные настройки вместо ручных аргументов.

Например:

```text
RAM:
[ 6 GB ]

Java:
[ Auto — Java 21 ]

Garbage Collector:
[ Auto ]

Advanced JVM Args:
[ ... ]
```

Для большинства пользователей — Auto.

---

## 39. Автоопределение ресурсов ПК

Hako может видеть:

- RAM;
- CPU;
- GPU;
- OS;
- architecture.

И рекомендовать настройки:

```text
В системе 32 GB RAM.
Для этой сборки рекомендуется 8 GB.
```

---

## 40. Предупреждения о слишком большой RAM

Minecraft часто не становится лучше от чрезмерного heap.

Hako может предупреждать:

```text
Вы выделили 24 GB RAM.
Для этой сборки это избыточно и может ухудшить работу GC.
Рекомендуется 8–10 GB.
```

---

## 41. GPU / Graphics Info

Показывать:

- используемый GPU;
- driver;
- OpenGL/Vulkan-related информацию, если нужно;
- detected integrated/discrete GPU.

Полезно для диагностики проблем с shaders и производительностью.

---

## 42. Автоматический выбор GPU

На ноутбуках можно помогать пользователю выбрать дискретную видеокарту для Minecraft.

Особенно полезно на Windows с hybrid graphics.

---

## 43. Log Viewer

Встроенный просмотрщик:

- `latest.log`;
- crash reports;
- debug logs;
- loader logs.

Функции:

- поиск;
- фильтр по level;
- подсветка ошибок;
- копирование;
- экспорт;
- переход к Crash Analyzer.

---

## 44. Privacy-first

Hako может отличаться тем, что не требует обязательного аккаунта Hako и не собирает лишнюю телеметрию.

Принципы:

- Minecraft account используется только для Minecraft auth;
- локальная статистика остаётся локальной;
- telemetry только opt-in;
- понятно показывать, какие данные отправляются.

---

## 45. Cloud Sync как опциональная функция

Если позже появится Hako account, можно синхронизировать:

- manifests;
- launcher settings;
- configs;
- saves;
- screenshots;
- server list.

Не обязательно синхронизировать Minecraft/mod files — их можно восстановить по manifest.

---

## 46. Box Templates

Готовые шаблоны:

```text
Vanilla
Fabric
NeoForge
Performance
Shaders
Create
Technical
Server Client
```

Создание новой Box становится очень быстрым.

---

## 47. Favorite Boxes

Закрепление часто используемых сборок на главном экране.

---

## 48. Recent Boxes

Последние запущенные Box с большой кнопкой Play.

---

## 49. Box Covers

У каждой Box может быть:

- собственная обложка;
- фон;
- icon;
- accent;
- screenshot мира.

Это сделает библиотеку сборок визуально приятной.

---

## 50. Hako-стиль интерфейса

Использовать тему коробок не только в названии.

Например:

- «Pack Box»
- «Open Box»
- «Clone Box»
- «Archive Box»
- «Share Box»
- «Unpack Modpack»
- анимация открытия коробки при первом создании сборки.

Главное — не переборщить и оставить термины понятными.

---

# Что особенно может выделить Hako

Если выбирать функции, которые реально способны отличить Hako от обычного лаунчера:

1. **Boxes как полноценная концепция сборок**
2. **Snapshots + Rollback**
3. **Crash Analyzer**
4. **Safe Mode**
5. **Умный Update All с diff**
6. **Share Code**
7. **Server Pack Sync**
8. **Единый Modrinth + CurseForge каталог**
9. **Автоматический Java Manager**
10. **Полноценный Minecraft Profile со skin/cape**
11. **Save Manager + автоматические backup**
12. **Developer Mode**

---

# Возможный порядок реализации

## MVP

- Microsoft/Minecraft login;
- Minecraft Profile;
- Vanilla Minecraft download;
- запуск игры;
- Java Manager;
- Boxes;
- базовые launcher settings;
- проверка файлов.

## Версия 0.2

- Fabric;
- NeoForge;
- Modrinth;
- установка модов;
- resource packs;
- shaders;
- управление Box.

## Версия 0.3

- CurseForge;
- Update All;
- dependency resolver;
- conflict detection;
- snapshots;
- rollback.

## Версия 0.4

- Crash Analyzer;
- Safe Mode;
- Save Manager;
- автоматические backups;
- Log Viewer.

## Версия 0.5

- Share Code;
- Server Pack Sync;
- cloud manifests;
- синхронизация между устройствами.

## Позже

- Cloud Sync;
- расширенные server-функции;
- Box Templates;
- performance presets;
- import/migration assistant;
- расширенный Developer Mode.

---

# Основная идея продукта

Hako можно позиционировать не просто как launcher, а как **менеджер изолированных Minecraft-сред**.

Каждая сборка — отдельная **箱 / Box**:

```text
Hako
├── Vanilla Box
├── Fabric Performance Box
├── Create Box
├── Friends Server Box
└── Experimental Box
```

Hako отвечает за:

- установку;
- совместимость;
- обновление;
- восстановление;
- запуск;
- перенос;
- совместное использование;
- диагностику.

Это может дать проекту собственную идентичность вместо копирования интерфейса официального Minecraft Launcher, Prism или CurseForge.

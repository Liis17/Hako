# Установка Minecraft и Java

Parent: [[Index]]

## Назначение и контракты

`Minecraft/MojangClient.swift` относительно `macos/Hako/Hako/` содержит actor `MojangClient`:
`catalog()` возвращает весь официальный каталог; `prepare(_:platform:)` проверяет SHA-1 описания,
клиент, компонент/версию Java и все группы нативных библиотек. `MojangError.unsupported` — отсутствие
подходящих файлов; сетевые и HTTP-ошибки означают сбой проверки, а не несовместимость.
`Minecraft/MojangModels.swift` описывает современные и legacy metadata, rules, libraries, assets и runtime.

`Minecraft/MinecraftInstaller.swift` — actor `MinecraftInstaller.install(_:at:platform:progress:)`.
Каждая установка пишет только в папку переданной сборки. Файлы скачиваются через URLSession во временный
файл, проверяются по SHA-1 и размеру, затем публикуются. До четырёх бинарных загрузок одновременно.
Проверенный существующий файл используется повторно; испорченный скачивается заново.
Java собирается из raw-файлов, исполняемых прав и относительных симлинков внутри Java.
Клиент и полный JSON — `minecraft/versions/{id}/`, библиотеки — `minecraft/libraries/`,
старые natives — `minecraft/natives/`, logging — `minecraft/assets/log_configs/`.
Assets сохраняются в objects/indexes; virtual и map_to_resources создают локальные копии.
Современные native JAR остаются в libraries. Запуска Minecraft установщик не выполняет.

`Instances/InstallationCoordinator.swift` — общий `@MainActor @Observable` координатор приложения.
Очередь FIFO по дате создания, одна активная сборка. `start()` восстанавливает прерванные установки,
`enqueue` ставит сборку в очередь, `pause` отменяет активные задачи и сохраняет паузу после их завершения.
Сборка остаётся installing, пока отменённые загрузки ещё могут писать файлы; в это время переименование
заблокировано. Переход между страницами и выход не отменяют загрузку. После закрытия приложения
установка продолжается при следующем запуске, пользовательская пауза остаётся паузой.

## Официальные источники

- [Каталог версий](https://piston-meta.mojang.com/mc/game/version_manifest_v2.json): URL и SHA-1 полного JSON.
- [Каталог Java](https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json): платформы `mac-os` и `mac-os-arm64`, компоненты и ссылки на runtime manifest.
  Длинный сегмент URL не является хешем текущего содержимого; URL задан централизованно в `MojangClient`.
- [1.6.4](https://piston-meta.mojang.com/v1/packages/b71bae449192fbbe1582ff32fb3765edf0b9b0a8/1.6.4.json): отсутствие javaVersion означает Java 8 / jre-legacy.
- [22w18a](https://piston-meta.mojang.com/v1/packages/1de25e62031021df204de79c264822898c937447/22w18a.json): ARM Java доступна, но ARM-библиотек игры нет.
- [1.19](https://piston-meta.mojang.com/v1/packages/14bbfb25fb1c1c798e3c9b9482b081a78d1f3a9d/1.19.json): отдельные native artifacts для Intel и ARM.

## Ограничения

Используется только нативная архитектура компьютера и Java компонента, указанного Mojang;
Intel fallback на Apple Silicon, Rosetta, другие поставщики Java и подмена библиотек исключены
по решению пользователя. Каталог остаётся полным, но несовместимую сборку создать нельзя.
Совместимость определяется метаданными, а не сравнением текстовых номеров версий.
URL загрузок должен быть HTTPS; пути, архивы и симлинки не могут выходить за папку сборки.

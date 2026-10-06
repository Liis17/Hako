# MicrosoftAuth

Parent: [[Index]]

## Назначение

Вход через Microsoft device code flow: приложение показывает код, пользователь вводит его
на microsoft.com/link, приложение получает токены, профиль Xbox (gamertag, XUID, аватар),
email и, если доступен, профиль Minecraft. Без Minecraft вход всё равно завершается —
аккаунт остаётся только Microsoft. Хранение результата — [[Data/Persistence]],
экран — [[UI/Screens#LoginView|LoginView]].

## Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `macos/Hako/Hako/Auth/MicrosoftAuth.swift` | `MicrosoftAuth`, `MicrosoftAuthError`, `DeviceCode`, `MicrosoftToken`, `XboxProfile`, `MinecraftProfile`, `MinecraftSession` | Сетевые запросы цепочки входа и ошибки |
| `macos/Hako/Hako/Views/LoginView.swift` | `LoginView.signIn()` | Оркестрация входа и сохранение результата |
| `macos/Hako/Hako/Auth/MinecraftSessionCoordinator.swift` | `MinecraftSessionCoordinator.connect` | Общая сессия, refresh token, срок действия и повтор подключения |

## Публичные контракты

| Контракт | Поведение и условия |
|----------|---------------------|
| `MicrosoftAuth.clientID` | Client ID приложения Azure (Entra ID) проекта, задан в коде. Это публичный идентификатор, не секрет: он и так попадает в бинарник приложения. Пустое значение → `MicrosoftAuthError.missingClientID` |
| `requestDeviceCode() async throws -> DeviceCode` | POST `login.microsoftonline.com/consumers/oauth2/v2.0/devicecode`, `scope=XboxLive.signin openid profile email offline_access`. Возвращает `userCode`, `deviceCode`, `verificationUri`, `expiresIn`, `interval` |
| `waitForToken(_:) async throws -> MicrosoftToken` | Опрос `/consumers/oauth2/v2.0/token` (`grant_type=urn:ietf:params:oauth:grant-type:device_code`) каждые `interval` секунд до `expiresIn`. `authorization_pending` — ждать, `slow_down` — +5 с, `authorization_declined` → `.declined`, `expired_token` или дедлайн → `.codeExpired`. Отмена задачи прерывает опрос (`CancellationError` из `Task.sleep`) |
| `MicrosoftToken.email` | `email` или `preferred_username` из payload `id_token` (base64url JWT, подпись не проверяется — только для отображения). `nil`, если `id_token` нет |
| `MinecraftProfile` | `uuid`, `name`, `skinURL?`, `skinVariant: MinecraftSkinVariant?`; вариант используется для ширины рук в 3D-превью |
| `refresh(_:) async throws -> MicrosoftToken` | `grant_type=refresh_token`, `scope=XboxLive.signin offline_access`. Ответ содержит новый refresh token |
| `signIn(with:) async throws -> (XboxProfile, MinecraftSession?)` | Xbox Live → профиль Xbox → Minecraft. Ошибки Xbox прерывают вход; **любая** ошибка шага Minecraft даёт `nil`. После шага Minecraft проверяет отмену задачи |
| `signInToMinecraft(with:) async throws -> MinecraftSession` | Xbox Live → Minecraft для уже сохранённого аккаунта; вызывается после `refresh` из `MinecraftSessionCoordinator` |

## Поток

1. Xbox Live: `user.auth.xboxlive.com/user/authenticate` — `AuthMethod: RPS`, `RpsTicket: d=<токен Microsoft>` → пользовательский токен.
2. Профиль Xbox: XSTS `xsts.auth.xboxlive.com/xsts/authorize` с `RelyingParty: http://xboxlive.com` →
   `xid` (XUID) и `gtg` (gamertag) из `DisplayClaims.xui`. Аватар — GET
   `profile.xboxlive.com/users/me/profile/settings?settings=GameDisplayPicRaw`
   (`Authorization: XBL3.0 x=<uhs>;<xsts>`, `x-xbl-contract-version: 3`); ошибка аватара → `avatarURL = nil`.
3. Minecraft: XSTS с `RelyingParty: rp://api.minecraftservices.com/` →
   `api.minecraftservices.com/authentication/login_with_xbox` (`identityToken: XBL3.0 x=<uhs>;<xsts>`) →
   GET `/minecraft/profile` с `Bearer` → `id` (UUID без дефисов), `name`, `skins`.
   Скин — `ACTIVE` из `skins` (иначе первый); ссылку textures.minecraft.net переводят на https,
   потому что App Transport Security не пропускает http. `MinecraftProfileResponse.minecraftProfile`
   также разбирает `variant` активного скина (`CLASSIC` / `SLIM`); отсутствующее или неизвестное
   значение даёт `nil`, не прерывая вход. Вариант сохраняется через `Account.connect(_:)`
   ([[Data/Persistence]]).

`LoginView.signIn()` сохраняет токены в Keychain и читает журналы игрового времени до вставки `Account`.
Затем `PlaytimeCoordinator.transferGuest(to:)` сохраняет аккаунт вместе с переносом гостевых счётчиков
и владельца гостевых сессий одной операцией ([[Data/Persistence]]). Ошибка записи прерывает вход;
повторный вход не дублирует уже перенесённое время.

## Ошибки

`MicrosoftAuthError: LocalizedError` — русские сообщения для экрана входа и профиля:

| Случай | Источник |
|--------|----------|
| `.missingClientID` | Пустой `clientID` |
| `.oauth(String)` | `error_description` из ответа Microsoft (например, `AADSTS…` при неверном Client ID) |
| `.codeExpired`, `.declined` | Ответы опроса токена |
| `.xbox(code:)` | HTTP 401 от Xbox/XSTS с `XErr`: 2148916233 — нет профиля Xbox, 2148916235 — регион, 2148916236/37 — подтверждение возраста, 2148916238 — детский аккаунт |
| `.appNotApproved` | HTTP 403 от `login_with_xbox` |
| `.noMinecraft` | HTTP 404 от `/minecraft/profile` |
| `.unexpectedResponse(status:)` | Прочие HTTP-статусы |

Сетевые ошибки `URLSession` показываются как есть (системное локализованное описание).
При входе ошибки шага Minecraft не показываются — `signIn` их поглощает; ошибку автоподключения
показывает вкладка профиля.

## Ограничения и важные детали

- Приложение Azure должно поддерживать личные аккаунты Microsoft и иметь включённые
  public client flows (device code flow не использует секрет клиента).
- Endpoint `devicecode` принимает `XboxLive.signin` вместе с OIDC-scope `openid profile email`
  (проверено запросом). Наличие `email` в `id_token` проверяется только настоящим входом.
- Mojang пропускает в Minecraft API только одобренные Client ID (заявка: aka.ms/mce-reviewappid).
  Заявка на одобрение Client ID проекта подана и пока не одобрена: вход в Microsoft и Xbox Live
  проходит, а шаг Minecraft возвращает 403 (`.appNotApproved`). После одобрения код менять не нужно.
- Device code flow не использует секрет клиента; секреты приложения Azure в репозиторий не добавляются.
- Исходящие соединения в песочнице разрешены настройкой `ENABLE_OUTGOING_NETWORK_CONNECTIONS = YES`
  (entitlement `com.apple.security.network.client`).
- Код модуля изолирован на главном акторе (`SWIFT_DEFAULT_ACTOR_ISOLATION = MainActor`);
  сетевые вызовы — асинхронные `URLSession.shared.data(for:)`.

## Общая Minecraft-сессия

`MinecraftSessionCoordinator` — @MainActor @Observable сервис на приложение; `ContentView` вызывает
`monitor` для текущего аккаунта. `identity(for:)` возвращает сессию только до срока истечения токена
и при наличии Minecraft UUID/ника. Microsoft/Xbox-профиль без Minecraft-токена запуск не разрешает.
`connect` повторно использует действительный токен; за 60 секунд до истечения обновляет его через
существующую цепочку auth. Параллельные запросы одного XUID объединены в одну задачу.
Обновлённый Microsoft refresh token сохраняется до запроса Minecraft, даже если тот неуспешен.

Проверка срока выполняется каждые 30 секунд. После ошибки auth фоновый цикл не повторяет сетевые
запросы бесконечно; явный `connect(force: true)` из профиля повторяет подключение.
Выход отменяет задачи и удаляет кэш сессий; проверка отмены не позволяет завершившемуся запросу
восстановить удалённый аккаунт. Старые поколения задач не освобождают слот нового подключения.
Токены остаются только в Keychain и памяти; сервис не пишет их в журнал.

# MicrosoftAuth

Parent: [[Index]]

## Назначение

Вход в Minecraft через Microsoft device code flow: приложение показывает код,
пользователь вводит его на microsoft.com/link, приложение получает токены
и профиль Minecraft. Хранение результата — [[Data/Persistence]], экран — [[UI/Screens#LoginView|LoginView]].

## Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `macos/Hako/Hako/Auth/MicrosoftAuth.swift` | `MicrosoftAuth`, `MicrosoftAuthError`, `DeviceCode`, `MicrosoftToken`, `MinecraftSession` | Сетевые запросы цепочки входа и ошибки |
| `macos/Hako/Hako/Views/LoginView.swift` | `LoginView.signIn()` | Оркестрация шагов и сохранение результата |

## Публичные контракты

| Контракт | Поведение и условия |
|----------|---------------------|
| `MicrosoftAuth.clientID` | Client ID приложения Azure (Entra ID) проекта, задан в коде. Это публичный идентификатор, не секрет: он и так попадает в бинарник приложения. Пустое значение → `MicrosoftAuthError.missingClientID` |
| `requestDeviceCode() async throws -> DeviceCode` | POST `login.microsoftonline.com/consumers/oauth2/v2.0/devicecode`, `scope=XboxLive.signin offline_access`. Возвращает `userCode`, `deviceCode`, `verificationUri`, `expiresIn`, `interval` |
| `waitForToken(_:) async throws -> MicrosoftToken` | Опрос `/consumers/oauth2/v2.0/token` (`grant_type=urn:ietf:params:oauth:grant-type:device_code`) каждые `interval` секунд до `expiresIn`. `authorization_pending` — ждать, `slow_down` — +5 с, `authorization_declined` → `.declined`, `expired_token` или дедлайн → `.codeExpired`. Отмена задачи прерывает опрос (`CancellationError` из `Task.sleep`) |
| `signInToMinecraft(with:) async throws -> MinecraftSession` | Xbox Live → XSTS → Minecraft → профиль (см. поток ниже). Возвращает UUID, ник, токен Minecraft и момент его истечения |

## Поток

1. `user.auth.xboxlive.com/user/authenticate` — `AuthMethod: RPS`, `RpsTicket: d=<токен Microsoft>` → токен Xbox Live.
2. `xsts.auth.xboxlive.com/xsts/authorize` — `RelyingParty: rp://api.minecraftservices.com/` → токен XSTS и `uhs`.
3. `api.minecraftservices.com/authentication/login_with_xbox` — `identityToken: XBL3.0 x=<uhs>;<xsts>` → токен Minecraft, `expires_in`.
4. GET `api.minecraftservices.com/minecraft/profile` с `Bearer` → `id` (UUID без дефисов), `name`.

## Ошибки

`MicrosoftAuthError: LocalizedError` — русские сообщения для экрана входа:

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

## Ограничения и важные детали

- Приложение Azure должно поддерживать личные аккаунты Microsoft и иметь включённые
  public client flows (device code flow не использует секрет клиента).
- Mojang пропускает в Minecraft API только одобренные Client ID (заявка: aka.ms/mce-reviewappid).
  Заявка на одобрение Client ID проекта подана и пока не одобрена: вход в Microsoft и Xbox Live
  проходит, а шаг 3 возвращает 403 (`.appNotApproved`). После одобрения код менять не нужно.
- Device code flow не использует секрет клиента; секреты приложения Azure в репозиторий не добавляются.
- Исходящие соединения в песочнице разрешены настройкой `ENABLE_OUTGOING_NETWORK_CONNECTIONS = YES`
  (entitlement `com.apple.security.network.client`).
- Обновление токена Minecraft по refresh token не реализовано: токены только сохраняются
  в `TokenKeychain` (см. [[Data/Persistence]]).
- Код модуля изолирован на главном акторе (`SWIFT_DEFAULT_ACTOR_ISOLATION = MainActor`);
  сетевые вызовы — асинхронные `URLSession.shared.data(for:)`.

# Auth API

The public listener uses port `8081`; the internal listener uses port `8091` and must only be reachable by trusted services. All request and response bodies are JSON unless an endpoint has no body.

## Public API

### Create an account

`POST /v1/auth/register`

```json
{"device_name":"Gaming PC"}
```

Returns `201` with `request_id`, `bot_link`, `exchange_secret`, and `expires_at`. Open the link in Telegram and send `/start` to the bot. The link format is `https://t.me/<bot_username>?start=<code>`; the code and exchange secret are independent values.

### Request a login

`POST /v1/auth/login`

```json
{"username":"Player_123","device_name":"Gaming PC"}
```

Returns `201` with `request_id`, `exchange_secret`, and `expires_at`. The bot sends the account owner the **Подтвердить** and **Отклонить** buttons. Unknown usernames return `404`.

### Poll and exchange a request

`GET /v1/auth/requests/{request_id}` requires `Authorization: Bearer <exchange_secret>` and returns `status` and `expires_at`. Poll every **2 seconds** while the status is `pending`.

`POST /v1/auth/exchange` uses the same authorization header and body:

```json
{"request_id":"<uuid>"}
```

An approved request returns a one-time session token and account details. A repeated exchange returns `409`; the request status then reports `consumed`.

### Current account and logout

- `GET /v1/auth/me` with `Authorization: Bearer <token>` returns account and session details.
- `POST /v1/auth/logout` with the same header revokes the current session and returns `204`.

Every successful token check slides the session expiry to 14 days after that request. Expired and revoked sessions cannot be renewed. Each login creates a separate session.

## Internal API

`POST /internal/v1/tokens/check` is served only on port `8091`. It requires `X-Service-Secret: <HAKO_AUTH_SERVICE_SECRET>` and a JSON body `{"token":"<session-token>"}`. A valid token returns `account_id`, `username`, `session_id`, and `expires_at`, and slides the session expiry by 14 days.

## Errors and limits

Errors use `{ "error": { "code": "...", "message": "..." } }`. Request bodies are limited to 16 KiB. Device names must contain 1–128 nonblank characters. Telegram usernames must be 5–32 ASCII letters, digits, or underscores.

Register, login, and exchange have per-IP in-memory sliding-window limits (5, 10, and 30 requests per minute respectively). Counters reset when the Auth process restarts. `HAKO_AUTH_TRUSTED_PROXIES` is a comma-separated CIDR list; `X-Forwarded-For` is considered only when the socket peer belongs to that list.

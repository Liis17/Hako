# Auth API

Auth has a public listener on port `8081` and an internal listener on port `8091`. The internal listener must be reachable only by trusted services. All request and response bodies are JSON except where an endpoint has no body.

## Public API

### Create an account

`POST /v1/auth/register`

```json
{"device_name":"Gaming PC"}
```

Returns `201` with `request_id`, `bot_link`, `exchange_secret`, and `expires_at`:

```json
{
  "request_id": "<uuid>",
  "bot_link": "https://t.me/<bot_username>?start=<code>",
  "exchange_secret": "<secret>",
  "expires_at": "<RFC3339 timestamp>"
}
```

Open `bot_link` in Telegram and send `/start`. The request expires after five minutes. Auth obtains `<bot_username>` from Telegram `getMe`; while the bot identity is unavailable, this endpoint returns `503 bot_unavailable`. If the Telegram account already has a Hako account, the request status becomes `account_exists` and exchange returns `409`.

### Request a login

`POST /v1/auth/login`

```json
{"username":"Player_123","device_name":"Gaming PC"}
```

Returns `201` with `request_id`, `exchange_secret`, and `expires_at`. Auth sends the account owner **Подтвердить** and **Отклонить** buttons in Telegram. Unknown usernames return `404 account_not_found`. The request expires after five minutes.

### Poll and exchange a request

`GET /v1/auth/requests/{request_id}` requires `Authorization: Bearer <exchange_secret>` and returns `status` and `expires_at`. Possible statuses are `pending`, `approved`, `denied`, `expired`, `account_exists`, and `consumed`. Poll every **2 seconds** while the status is `pending`.

```json
{"status":"pending","expires_at":"<RFC3339 timestamp>"}
```

`POST /v1/auth/exchange` uses the same authorization header and body:

```json
{"request_id":"<uuid>"}
```

An approved request returns `200` with a one-time session `token`, `token_expires_at`, and account details. The session token is returned only by this exchange. A repeated exchange returns `409 request_consumed`; a pending request returns `409 request_pending`, a denied request returns `403 request_denied`, an expired request returns `410 request_expired`, and an already-registered Telegram account returns `409 account_exists`.

```json
{
  "token": "<session-token>",
  "token_expires_at": "<RFC3339 timestamp>",
  "account": {
    "id": "<uuid>",
    "username": "Player_123",
    "created_at": "<RFC3339 timestamp>"
  }
}
```

### Current account and logout

- `GET /v1/auth/me` with `Authorization: Bearer <token>` returns `account` (`id`, `username`, `created_at`) and `session` (`device_name`, `expires_at`).
- `POST /v1/auth/logout` with the same header revokes the current session and returns `204` with no body.

`GET /v1/auth/me` response shape:

```json
{
  "account": {"id":"<uuid>","username":"Player_123","created_at":"<RFC3339 timestamp>"},
  "session": {"device_name":"Gaming PC","expires_at":"<RFC3339 timestamp>"}
}
```

Successful `/v1/auth/me` and internal token checks renew an active session to 14 days after the request. `/v1/auth/logout` requires an active session and revokes it immediately. An expired or revoked token cannot be renewed. Each login creates a separate session. Token values are stored as hashes in PostgreSQL.

Account identity is the Telegram numeric `telegram_id`, which is unique in the Auth database. Telegram usernames can change and are refreshed from Telegram updates; the username is used for login lookup, not as the account's unique identity.

## Internal API

`POST /internal/v1/tokens/check` is served only on port `8091`. It requires `X-Service-Secret: <HAKO_AUTH_SERVICE_SECRET>` and a JSON body:

```json
{"token":"<session-token>"}
```

A valid token returns `account_id`, `username`, `session_id`, and `expires_at`, and renews the session for 14 days. A wrong or missing service secret returns `401 unauthorized`; token checks also return `401` when a session is expired, revoked, or unknown. Keep this listener private; Pack sends the shared secret from the Compose network.

The `token` value must contain 1–256 bytes.

Both listeners serve `GET /healthz`, which returns `200` and `ok`. A healthy HTTP service does not guarantee Telegram is available; bot polling retries in the background.

## Errors and limits

Errors use `{ "error": { "code": "...", "message": "..." } }`.

| Status | Typical codes | Meaning |
| ---: | --- | --- |
| `400` | `invalid_request` | Invalid JSON, UUID, device name, or username |
| `401` | `unauthorized` | Missing/invalid bearer credential or service secret |
| `403` | `request_denied` | Telegram account owner denied the login |
| `404` | `not_found`, `account_not_found` | Unknown route or username |
| `405` | `method_not_allowed` | Method is not supported on this route |
| `409` | `request_pending`, `request_consumed`, `account_exists` | Request cannot be exchanged in its current state |
| `410` | `request_expired` | Authorization request expired |
| `413` | `payload_too_large` | Request body exceeds 16 KiB |
| `415` | `unsupported_media_type` | Request content type is not JSON |
| `429` | `rate_limited` | Per-IP request limit was exceeded |
| `500` | `internal_error` | Unexpected database or internal error |
| `503` | `bot_unavailable` | Auth has not obtained a Telegram bot username from `getMe` |

Device names must contain 1–128 nonblank characters. Telegram usernames must be 5–32 ASCII letters, digits, or underscores. Register, login, and exchange have per-IP in-memory sliding-window limits of 5, 10, and 30 requests per minute respectively. Counters reset when the Auth process restarts. `HAKO_AUTH_TRUSTED_PROXIES` is a comma-separated CIDR list; `X-Forwarded-For` is considered only when the socket peer belongs to that list.

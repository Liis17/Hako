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

## Playtime

Both endpoints use `Authorization: Bearer <Hako session token>` and renew active sessions using the same rules as `/v1/auth/me`.

### Submit a completed session

`POST /v1/playtime/sessions`

```json
{"session_id":"<uuid>","duration_ms":123456}
```

`duration_ms` is a nonnegative signed 64-bit integer in milliseconds; fractional and out-of-range values and the nil UUID are rejected with `400 invalid_request`. The server derives ownership from the bearer token, never a client-supplied account ID.

Returns `200`:

```json
{"session_id":"<same uuid>"}
```

The first submission stores the UUID and increments the account total atomically. An identical replay returns the same acknowledgement without incrementing the total. Reusing a UUID with a different duration or account returns `409 playtime_session_conflict`. Deduplication records persist independently of authentication sessions; retrying after a lost response or logging in again does not add time twice. Independent sessions, including overlapping games on two computers, are summed. No wall-clock timestamps are required. Account totals use signed 64-bit milliseconds; an overflowing increment rolls back the entire submission.

### Read the profile total

`GET /v1/playtime` returns `200`:

```json
{"total_duration_ms":123456}
```

A profile with no submitted sessions returns zero. Invalid, expired and revoked tokens return `401`; neither endpoint accepts an account ID to read or modify another profile.

Deploy Auth with its playtime migration before distributing clients using these endpoints. Existing authentication response shapes remain unchanged.

## Internal API

`POST /internal/v1/tokens/check` is served only on port `8091`. It requires `X-Service-Secret: <HAKO_AUTH_SERVICE_SECRET>` and a JSON body:

```json
{"token":"<session-token>"}
```

A valid token returns `account_id`, `username`, `session_id`, and `expires_at`, and renews the session for 14 days. A wrong or missing service secret returns `401 unauthorized`; token checks also return `401` when a session is expired, revoked, or unknown. Keep this listener private; Pack sends the shared secret from the Compose network.

The `token` value must contain 1–256 bytes.

The Telegram `/sessions` command lists at most 20 active sessions and shortens long device names in the message and buttons. It shows **Завершить все сессии** when the account has an active session or an unexpired `pending`/`approved` authorization request. That action revokes every active session and expires those requests, including approvals not yet exchanged.

Both listeners serve `GET /healthz`, which returns `200` and `ok`. A healthy HTTP service does not guarantee Telegram is available. Temporary Telegram delivery and polling errors retry with bounded backoff. A permanent Telegram client error (`4xx`) on an outbox item marks that item failed, logs it, and lets the worker continue with later items and `getUpdates`.

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

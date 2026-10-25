# Hako server

The Rust workspace contains two independent services. `hako-auth` implements Telegram account registration, login approvals, rolling sessions, and public/internal Auth APIs. `hako-pack` validates and stores personal `.hakopack` archives, exposes conditional CRUD and streaming downloads, and resumes delayed S3 cleanup from PostgreSQL.

## Structure

| Path | Purpose |
| --- | --- |
| `crates/auth/` | `hako-auth` binary, public and internal listeners, Auth migrations |
| `crates/pack/` | `hako-pack` binary, Pack listener, Pack migrations |
| `Dockerfile` | Shared multi-stage build for either service |
| `init-db.sh` | Creates isolated PostgreSQL roles and databases on first startup |
| `docs/auth-api.md` | Public and internal Auth API reference |
| `docs/pack-api.md` | Pack API, archive format, limits, and environment reference |
| `docs/hakopack-manifest.schema.json` | Draft 2020-12 schema for `.hakopack` v1 manifests |
| `docs/hakopack-example.json` | Example `.hakopack` v1 manifest |

## Run with Docker Compose

Copy `.env.example` to `.env`, then run from the repository root:

```sh
docker compose up --build -d
docker compose ps
curl http://127.0.0.1:8081/healthz
curl http://127.0.0.1:8082/healthz
```

Only Auth public port `8081` and Pack port `8082` are published on loopback. Auth internal port `8091` is available to other Compose services and is not published to the host. PostgreSQL data lives in the `pgdata` volume. `docker compose down -v` removes the stack and its database volume.

For public deployment, terminate HTTPS at an external reverse proxy and forward requests to the Auth public listener. Reverse-proxy configuration and deployment details are part of stage 4.

## Environment variables

Values marked **required** must be set before the service starts. Secrets are validated but are not written to logs or error responses. Compose reads the root `.env` file.

| Variable | Service | Requirement / default | Purpose |
| --- | --- | --- | --- |
| `POSTGRES_PASSWORD` | Compose | Required | Local PostgreSQL superuser password |
| `HAKO_AUTH_DB_PASSWORD` | Compose | Required | Password for the Auth database role |
| `HAKO_PACK_DB_PASSWORD` | Compose | Required | Password for the Pack database role |
| `HAKO_AUTH_PUBLIC_ADDR` | Auth | `0.0.0.0:8081` | Public listener address |
| `HAKO_AUTH_INTERNAL_ADDR` | Auth | `0.0.0.0:8091` | Internal listener address |
| `HAKO_AUTH_DATABASE_URL` | Auth | Required | Auth PostgreSQL connection URL |
| `HAKO_TELEGRAM_BOT_TOKEN` | Auth | Required | Telegram bot credential |
| `HAKO_AUTH_SERVICE_SECRET` | Auth | Required | Shared secret for internal service calls |
| `HAKO_AUTH_TRUSTED_PROXIES` | Auth | Optional | Trusted proxy addresses |
| `HAKO_PACK_ADDR` | Pack | `0.0.0.0:8082` | Pack listener address |
| `HAKO_PACK_DATABASE_URL` | Pack | Required | Pack PostgreSQL connection URL |
| `HAKO_PACK_AUTH_INTERNAL_URL` | Pack | `http://auth:8091` | Auth internal API base URL |
| `HAKO_PACK_SERVICE_SECRET` | Pack | Required; same value as `HAKO_AUTH_SERVICE_SECRET` | Shared secret for internal Auth token checks |
| `HAKO_S3_ENDPOINT` | Pack | Optional as a group | S3-compatible endpoint; all required S3 fields must be set to enable storage |
| `HAKO_S3_BUCKET` | Pack | Optional as a group | Bucket for personal packs |
| `HAKO_S3_REGION` | Pack | Optional as a group | S3 region |
| `HAKO_S3_ACCESS_KEY_ID` | Pack | Optional as a group | S3 access key ID |
| `HAKO_S3_SECRET_ACCESS_KEY` | Pack | Optional as a group | S3 secret access key |
| `HAKO_S3_SESSION_TOKEN` | Pack | Optional | Temporary S3 session token |
| `HAKO_S3_PATH_STYLE` | Pack | `true` | Use path-style S3 requests, recommended for custom endpoints |
| `HAKO_PACK_MAX_ARCHIVE_BYTES` | Pack | `1073741824` | Maximum streamed ZIP size |
| `HAKO_PACK_MAX_UNCOMPRESSED_BYTES` | Pack | `4294967296` | Maximum total uncompressed ZIP size from entry metadata |
| `HAKO_PACK_MAX_ENTRIES` | Pack | `10000` | Maximum ZIP entry count |
| `HAKO_PACK_MAX_MANIFEST_BYTES` | Pack | `1048576` | Maximum `manifest.json` size |
| `HAKO_PACK_CLEANUP_DELAY` | Pack | `86400` seconds | Delayed S3 deletion; must exceed the one-hour download limit and be no more than one day |

Telegram polling starts in the background. An unavailable or invalid bot token is logged without exposing the token; polling retries with backoff while the HTTP health endpoints remain available. Without the S3 configuration group, Pack still starts and reports healthy; Pack operations that need object storage return `503 storage_unavailable`. The APIs are documented in [`docs/auth-api.md`](docs/auth-api.md) and [`docs/pack-api.md`](docs/pack-api.md).

For server tests, set `HAKO_TEST_DATABASE_URL` to an isolated, empty PostgreSQL database. CI creates `hako_auth_test` and `hako_pack_test` for this purpose.

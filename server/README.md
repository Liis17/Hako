# Hako server

The Rust workspace contains two independent services. `hako-auth` owns the public and internal Auth listeners, and `hako-pack` owns the Pack API. This stage provides startup configuration, database migrations, health endpoints, graceful shutdown, and container infrastructure; it does not implement Auth or Pack business logic.

## Structure

| Path | Purpose |
| --- | --- |
| `crates/auth/` | `hako-auth` binary, public and internal listeners, Auth migrations |
| `crates/pack/` | `hako-pack` binary, Pack listener, Pack migrations |
| `Dockerfile` | Shared multi-stage build for either service |
| `init-db.sh` | Creates isolated PostgreSQL roles and databases on first startup |

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
| `HAKO_PACK_SERVICE_SECRET` | Pack | Required | Shared secret for internal service calls |
| `HAKO_S3_ENDPOINT` | Pack | Required | S3-compatible endpoint |
| `HAKO_S3_BUCKET` | Pack | Required | Bucket for personal packs |
| `HAKO_S3_REGION` | Pack | Required | S3 region |
| `HAKO_S3_ACCESS_KEY_ID` | Pack | Required | S3 access key ID |
| `HAKO_S3_SECRET_ACCESS_KEY` | Pack | Required | S3 secret access key |
| `HAKO_S3_SESSION_TOKEN` | Pack | Optional | Temporary S3 session token |
| `HAKO_S3_PATH_STYLE` | Pack | Required (`true` or `false`) | Use path-style S3 requests |

The S3 and Telegram values are validated at startup in this skeleton; their integrations are implemented in later stages.

# Hako server

The Rust workspace contains two independent services backed by PostgreSQL and, for pack archives, an external S3-compatible object store.

| Component | Purpose | Listener |
| --- | --- | --- |
| `hako-auth` (`crates/auth/`) | Telegram registration and login approval, session API, and internal token checks | Public `8081`, internal `8091` |
| `hako-pack` (`crates/pack/`) | Private `.hakopack` metadata and archive API | `8082` |
| PostgreSQL | Auth and Pack use separate databases and roles; migrations run when each service starts | Compose network only |
| S3-compatible storage | Stores archive bytes; Pack metadata and delayed deletion queue stay in PostgreSQL | External, optional at startup |

Auth discovers the bot username using Telegram `getMe`; it does not need a separate username setting. Pack calls Auth's internal token endpoint with the shared service secret. The services are not yet integrated with the macOS or Windows clients.

## Docker Compose

From the repository root, copy `.env.example` to `.env`, replace every `replace_me_...` value, then start the stack:

```sh
cp .env.example .env
# Edit .env and set a real Telegram bot token and matching service secrets.
docker compose up -d
docker compose ps
docker compose exec auth curl -fsS http://127.0.0.1:8081/healthz
docker compose exec pack curl -fsS http://127.0.0.1:8082/healthz
```

On PowerShell, use `Copy-Item .env.example .env`.

The service images default to the prebuilt `docker.barkfluff.com/hako-auth:latest` and `docker.barkfluff.com/hako-pack:latest` published by CI (see [Server image publishing](#server-image-publishing)). A deployment host needs only `compose.yaml` and `.env` copied from this repository — the first-run database bootstrap is embedded in `compose.yaml`, so no repository checkout is required. On such a host, run `docker compose pull` before `docker compose up -d` (the `build` sections reference a repository checkout and matter only for local builds):

```sh
docker login docker.barkfluff.com
docker compose pull
docker compose up -d
```

To build the images locally instead, for example for development without registry access, run `docker compose up -d --build`; Compose tags the built images with the configured image names. Override the image reference through `HAKO_AUTH_IMAGE` or `HAKO_PACK_IMAGE`, for example to pin a published `sha-<commit>` tag.

No service port is published to the host: Auth `8081`, Auth's internal `8091`, Pack `8082`, and PostgreSQL are reachable only inside the Compose network, which is explicitly named `hako` so that an external reverse-proxy container can join it. PostgreSQL data persists in the `pgdata` volume; `docker compose down` preserves it, while `docker compose down -v` deletes it. The database roles and databases are created by the bootstrap script embedded in `compose.yaml` (top-level `configs`) only when PostgreSQL initializes an empty data directory.

Pack can start without S3 configuration and still reports healthy; archive create, replacement, and download requests then return `503 storage_unavailable`. Metadata and delete operations use PostgreSQL; deletions queue object cleanup, which retries while S3 is unavailable. If any required S3 setting is provided, all five required settings must be present or Pack exits on startup. A missing or invalid Telegram token does not prevent Auth health checks, but registration returns `503 bot_unavailable` until `getMe` supplies the bot username.

## Environment variables

Compose reads `.env` from the repository root. Required values have no default; sample values beginning with `replace_me_` are placeholders, not usable credentials. Empty S3 values are treated as unset.

| Variable | Used by | Default / requirement | Purpose |
| --- | --- | --- | --- |
| `POSTGRES_USER` | Compose/PostgreSQL | Fixed: `postgres` | Bootstrap administrator role inside the database container |
| `POSTGRES_DB` | Compose/PostgreSQL | Fixed: `postgres` | Bootstrap database used by the initialization script |
| `POSTGRES_PASSWORD` | Compose/PostgreSQL | Required | Bootstrap administrator password |
| `HAKO_AUTH_DB_PASSWORD` | Compose/PostgreSQL | Required | Password for the `hako_auth` role created by the embedded bootstrap script |
| `HAKO_PACK_DB_PASSWORD` | Compose/PostgreSQL | Required | Password for the `hako_pack` role created by the embedded bootstrap script |
| `HAKO_AUTH_IMAGE` | Compose | Default: `docker.barkfluff.com/hako-auth:latest` | Auth image reference; override to pin a `sha-<commit>` tag |
| `HAKO_PACK_IMAGE` | Compose | Default: `docker.barkfluff.com/hako-pack:latest` | Pack image reference; override to pin a `sha-<commit>` tag |
| `HAKO_AUTH_PUBLIC_ADDR` | Auth | `0.0.0.0:8081` | Public HTTP listener; use the default with the fixed Compose port mapping |
| `HAKO_AUTH_INTERNAL_ADDR` | Auth | `0.0.0.0:8091` | Internal listener for health and service-to-service token checks; changing its port also requires updating Pack's URL |
| `HAKO_AUTH_DATABASE_URL` | Auth | Required | Auth PostgreSQL connection URL |
| `HAKO_TELEGRAM_BOT_TOKEN` | Auth | Required | BotFather token used by `getMe`, polling, and Telegram messages |
| `HAKO_AUTH_SERVICE_SECRET` | Auth | Required | Secret required by `/internal/v1/tokens/check` |
| `HAKO_AUTH_TRUSTED_PROXIES` | Auth | Empty | Comma-separated CIDR values for trusted proxies; only these peers may supply the client address through `X-Forwarded-For` |
| `HAKO_PACK_ADDR` | Pack | `0.0.0.0:8082` | Pack HTTP listener; use the default with the fixed Compose port mapping |
| `HAKO_PACK_DATABASE_URL` | Pack | Required | Pack PostgreSQL connection URL |
| `HAKO_PACK_AUTH_INTERNAL_URL` | Pack | `http://auth:8091` | Base URL for Auth's internal API |
| `HAKO_PACK_SERVICE_SECRET` | Pack | Required; must equal `HAKO_AUTH_SERVICE_SECRET` | Value sent in `X-Service-Secret` to Auth |
| `HAKO_S3_ENDPOINT` | Pack | Optional group | S3-compatible endpoint URL |
| `HAKO_S3_BUCKET` | Pack | Optional group | Bucket for private archives |
| `HAKO_S3_REGION` | Pack | Optional group | Signing region |
| `HAKO_S3_ACCESS_KEY_ID` | Pack | Optional group | S3 access key |
| `HAKO_S3_SECRET_ACCESS_KEY` | Pack | Optional group | S3 secret key |
| `HAKO_S3_SESSION_TOKEN` | Pack | Empty | Optional temporary credential token |
| `HAKO_S3_PATH_STYLE` | Pack | `true` | Enables path-style bucket addressing, useful with custom S3 endpoints |
| `HAKO_PACK_MAX_ARCHIVE_BYTES` | Pack | `1073741824` | Maximum streamed ZIP upload size (1 GiB) |
| `HAKO_PACK_MAX_UNCOMPRESSED_BYTES` | Pack | `4294967296` | Maximum aggregate uncompressed ZIP entry size (4 GiB) |
| `HAKO_PACK_MAX_ENTRIES` | Pack | `10000` | Maximum ZIP entries, including directories |
| `HAKO_PACK_MAX_MANIFEST_BYTES` | Pack | `1048576` | Maximum uncompressed `manifest.json` size (1 MiB) |
| `HAKO_PACK_VALIDATION_CONCURRENCY` | Pack | `2` | Maximum simultaneous archive validations; requests wait up to one second, then receive `503` with `Retry-After: 1` |
| `HAKO_PACK_VALIDATION_TIMEOUT_SECS` | Pack | `900` seconds | Maximum time spent decompressing and validating one archive |
| `HAKO_PACK_CLEANUP_DELAY` | Pack | `86400` seconds | Delay before deleting replaced/deleted objects; must be greater than 3600 and at most 86400 seconds |
| `RUST_LOG` | Auth and Pack | `info` | `tracing` filter passed to each service |
| `HAKO_TEST_DATABASE_URL` | Auth and Pack integration tests | Unset; PostgreSQL tests skip | Connection URL for one isolated test database; set it separately for each service's integration suite |

All size limits, validation concurrency, and validation timeout must be positive integers. Archive validation runs in a bounded blocking pool; requests that cannot enter within one second receive `503 validation_busy` with `Retry-After: 1`, and validation that exceeds its time budget is rejected as `invalid_archive`. `HAKO_PACK_CLEANUP_DELAY` must exceed the one-hour maximum download duration and cannot exceed one day. To disable S3-backed operations, leave the five required S3 settings empty; a partial group is a configuration error.

## Server image publishing

Pushes to `main` that change `server/` trigger `.github/workflows/server-images.yml`, which builds both service images for `linux/amd64` and publishes them to the private registry `docker.barkfluff.com`. Each image is tagged `latest` and `sha-<short commit>`. The workflow requires the `REGISTRY_USERNAME` and `REGISTRY_PASSWORD` repository secrets; manual runs are available through `workflow_dispatch`.

## Create a Telegram bot

1. In Telegram, open [@BotFather](https://t.me/BotFather), send `/newbot`, and follow its prompts.
2. Copy the token BotFather returns into `HAKO_TELEGRAM_BOT_TOKEN` in `.env`.
3. Restart Auth with `docker compose up -d auth` and check `docker compose logs auth` for `Telegram bot identity is ready`.

Auth calls `getMe` to discover the bot username automatically. No `HAKO_TELEGRAM_BOT_USERNAME` setting exists. Registration produces `https://t.me/<bot_username>?start=<code>`; with an invalid placeholder token, health stays available but registration returns `503 bot_unavailable` until the bot identity is ready.

## Public HTTPS

No service port is published to the host. Run the reverse proxy as a container, attach it to the Compose network, and forward to the services by name; Auth's internal `8091` listener and PostgreSQL stay private to the network:

```sh
docker network connect hako <proxy-container>
```

Both public services can share one domain: route `/v1/packs` to Pack and everything else — the Auth APIs `/v1/auth/...`, `/v1/playtime/...`, `/v1/playtime` and `/healthz` — to Auth. The proxy must resolve `auth` and `pack` through Docker DNS at request time (nginx: `resolver 127.0.0.11` with a variable `proxy_pass`), because Compose assigns new container addresses whenever it recreates services.

Example nginx server block for `hako.example.com` behind Cloudflare (TLS terminates at Cloudflare, the origin certificate is a Cloudflare Origin CA certificate, and the Cloudflare SSL mode must be Full (strict); see the [nginx proxy module reference](https://nginx.org/en/docs/http/ngx_http_proxy_module.html)):

```nginx
server {
    listen 80;
    server_name hako.example.com;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl http2;
    server_name hako.example.com;

    ssl_certificate     /etc/nginx/ssl/hako.example.com.pem;
    ssl_certificate_key /etc/nginx/ssl/hako.example.com.key;
    ssl_protocols       TLSv1.2 TLSv1.3;

    resolver 127.0.0.11 valid=10s ipv6=off;

    location /v1/packs {
        set $pack_upstream http://pack:8082;
        proxy_pass $pack_upstream;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $http_cf_connecting_ip;
        proxy_set_header X-Forwarded-Proto https;
        client_max_body_size 1g;
        proxy_request_buffering off;
        proxy_buffering off;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }

    location = /healthz/pack {
        set $pack_health_upstream http://pack:8082/healthz;
        proxy_pass $pack_health_upstream;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $http_cf_connecting_ip;
        proxy_set_header X-Forwarded-Proto https;
    }

    location / {
        set $auth_upstream http://auth:8081;
        proxy_pass $auth_upstream;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $http_cf_connecting_ip;
        proxy_set_header X-Forwarded-Proto https;
    }
}
```

Set `HAKO_AUTH_TRUSTED_PROXIES` to the proxy's source range as Auth sees it, written as a CIDR. Find the network subnet with `docker network inspect hako` and use it, for example `HAKO_AUTH_TRUSTED_PROXIES=172.18.0.0/16`; an untrusted or absent `X-Forwarded-For` is ignored and Auth falls back to the proxy address. Behind Cloudflare, forward `CF-Connecting-IP` in `X-Forwarded-For` as in the example; to also reject direct connections that bypass Cloudflare, allow only the [published Cloudflare ranges](https://www.cloudflare.com/ips/) and deny everything else.

Cloudflare limits proxied request bodies (100 MB on the Free and Pro plans), so larger archive uploads fail at the Cloudflare edge before reaching Pack; downloads are not limited.

## Local development and tests

Install Rust/Cargo and run commands from `server/`:

```sh
cargo fmt --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace --locked
```

Without `HAKO_TEST_DATABASE_URL`, PostgreSQL integration tests print a skip message; unit tests still run. For integration coverage, use an isolated PostgreSQL instance with empty databases named `hako_auth_test` and `hako_pack_test`, then run each suite against its own database.

PowerShell:

```powershell
$env:HAKO_TEST_DATABASE_URL = 'postgres://postgres:postgres@localhost:5432/hako_auth_test'
cargo test --locked --package hako-auth --test postgres_auth
$env:HAKO_TEST_DATABASE_URL = 'postgres://postgres:postgres@localhost:5432/hako_pack_test'
cargo test --locked --package hako-pack --test postgres_pack
Remove-Item Env:HAKO_TEST_DATABASE_URL -ErrorAction SilentlyContinue
```

CI runs the server checks in `.github/workflows/tests.yml` with a PostgreSQL service and separate test databases. See [`docs/auth-api.md`](docs/auth-api.md), [`docs/pack-api.md`](docs/pack-api.md), [`docs/hakopack-manifest.schema.json`](docs/hakopack-manifest.schema.json), and [`docs/hakopack-example.json`](docs/hakopack-example.json) for API and archive details.

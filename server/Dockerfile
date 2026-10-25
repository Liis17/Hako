FROM rust:1.97-bookworm AS builder

WORKDIR /workspace
COPY Cargo.toml Cargo.lock ./
COPY crates ./crates

ARG SERVICE_PACKAGE
RUN cargo build --release --locked --package "${SERVICE_PACKAGE}" \
    && install -D "target/release/${SERVICE_PACKAGE}" /out/hako-service

FROM debian:bookworm-slim

RUN apt-get update \
    && apt-get install --no-install-recommends --yes ca-certificates curl \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --system --gid 10001 hako \
    && useradd --system --uid 10001 --gid hako --home-dir /nonexistent --shell /usr/sbin/nologin hako

ARG HEALTHCHECK_PORT
ENV HEALTHCHECK_PORT=${HEALTHCHECK_PORT}
COPY --from=builder --chown=10001:10001 /out/hako-service /usr/local/bin/hako-service

USER 10001:10001
EXPOSE ${HEALTHCHECK_PORT}
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl --fail --silent "http://127.0.0.1:${HEALTHCHECK_PORT}/healthz" || exit 1

ENTRYPOINT ["/usr/local/bin/hako-service"]

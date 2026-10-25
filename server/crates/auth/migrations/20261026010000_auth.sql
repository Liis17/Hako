CREATE TABLE accounts (
    id UUID PRIMARY KEY,
    telegram_id BIGINT NOT NULL UNIQUE,
    username TEXT NOT NULL,
    chat_id BIGINT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX accounts_username_ci_idx ON accounts (lower(username));

CREATE TABLE sessions (
    id UUID PRIMARY KEY,
    account_id UUID NOT NULL REFERENCES accounts(id),
    token_hash BYTEA NOT NULL UNIQUE,
    device_name TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    last_used_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ
);

CREATE INDEX sessions_active_account_idx ON sessions (account_id, expires_at)
    WHERE revoked_at IS NULL;

CREATE TABLE auth_requests (
    id UUID PRIMARY KEY,
    kind TEXT NOT NULL CHECK (kind IN ('register', 'login')),
    status TEXT NOT NULL CHECK (status IN (
        'pending', 'approved', 'denied', 'expired', 'account_exists', 'consumed'
    )),
    device_name TEXT NOT NULL,
    code_hash BYTEA UNIQUE,
    exchange_secret_hash BYTEA NOT NULL,
    username TEXT,
    account_id UUID REFERENCES accounts(id),
    chat_id BIGINT,
    message_id BIGINT,
    created_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    completed_at TIMESTAMPTZ,
    CHECK ((kind = 'register' AND code_hash IS NOT NULL) OR
           (kind = 'login' AND code_hash IS NULL))
);

CREATE INDEX auth_requests_account_status_idx ON auth_requests (account_id, status);

CREATE TABLE telegram_state (
    singleton BOOLEAN PRIMARY KEY DEFAULT TRUE CHECK (singleton),
    update_offset BIGINT NOT NULL DEFAULT 0
);

INSERT INTO telegram_state (singleton, update_offset) VALUES (TRUE, 0);

CREATE TABLE telegram_updates (
    update_id BIGINT PRIMARY KEY,
    processed_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE telegram_outbox (
    id BIGSERIAL PRIMARY KEY,
    payload JSONB NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    delivered_at TIMESTAMPTZ
);

CREATE INDEX telegram_outbox_pending_idx ON telegram_outbox (id)
    WHERE delivered_at IS NULL;

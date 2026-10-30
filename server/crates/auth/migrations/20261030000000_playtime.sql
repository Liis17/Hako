CREATE TABLE playtime_sessions (
    id UUID PRIMARY KEY,
    account_id UUID NOT NULL REFERENCES accounts(id),
    duration_ms BIGINT NOT NULL CHECK (duration_ms >= 0)
);
CREATE INDEX playtime_sessions_account ON playtime_sessions(account_id);
CREATE TABLE playtime_totals (
    account_id UUID PRIMARY KEY REFERENCES accounts(id),
    duration_ms BIGINT NOT NULL CHECK (duration_ms >= 0)
);

CREATE TABLE packs (
    owner_id UUID NOT NULL,
    pack_id UUID NOT NULL,
    revision BIGINT NOT NULL CHECK (revision > 0),
    etag TEXT NOT NULL,
    s3_key TEXT NOT NULL,
    size_bytes BIGINT NOT NULL CHECK (size_bytes >= 0),
    sha256 TEXT NOT NULL,
    name TEXT NOT NULL,
    minecraft_version TEXT NOT NULL,
    loader TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (owner_id, pack_id)
);

CREATE INDEX packs_list_order ON packs (owner_id, updated_at DESC, pack_id ASC);

CREATE TABLE pack_cleanup (
    id BIGSERIAL PRIMARY KEY,
    s3_key TEXT NOT NULL UNIQUE,
    eligible_at TIMESTAMPTZ NOT NULL,
    attempts INTEGER NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    last_error TEXT,
    created_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX pack_cleanup_ready ON pack_cleanup (eligible_at, id);

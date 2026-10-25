ALTER TABLE telegram_outbox
    ADD COLUMN failed_at TIMESTAMPTZ,
    ADD COLUMN failure_reason TEXT;

DROP INDEX telegram_outbox_pending_idx;
CREATE INDEX telegram_outbox_pending_idx ON telegram_outbox (id)
    WHERE delivered_at IS NULL AND failed_at IS NULL;

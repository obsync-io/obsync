-- V003: enterprise audit trail — an append-only record of who performed which action.
-- No secrets are ever stored here (see the V001 header). Timestamps are ISO-8601 TEXT;
-- action and entity_type are stored as readable names for clean export.
CREATE TABLE audit_log (
    id          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    occurred_at TEXT    NOT NULL,
    actor       TEXT    NOT NULL,
    action      TEXT    NOT NULL,
    entity_type TEXT    NOT NULL,
    entity_id   TEXT        NULL,
    entity_name TEXT        NULL,
    detail      TEXT        NULL
);

-- Attribute each run to the identity that started it: the desktop app's interactive user,
-- or the Windows Service's service account.
ALTER TABLE runs ADD COLUMN triggered_by TEXT NULL;

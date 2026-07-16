-- V013: structured server edition/version captured at test time, and the persisted outcome of
-- each repository profile's last validation (mirrors V002 for connection profiles).
ALTER TABLE connection_profiles ADD COLUMN server_edition TEXT NULL;
ALTER TABLE connection_profiles ADD COLUMN server_version TEXT NULL;
ALTER TABLE repository_profiles ADD COLUMN last_validation_status INTEGER NOT NULL DEFAULT 0;
ALTER TABLE repository_profiles ADD COLUMN last_validated_at      TEXT        NULL;
ALTER TABLE repository_profiles ADD COLUMN last_validation_detail TEXT        NULL;

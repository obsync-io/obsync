-- V002: persist the outcome of each server profile's last connectivity test.
ALTER TABLE connection_profiles ADD COLUMN last_test_status INTEGER NOT NULL DEFAULT 0;
ALTER TABLE connection_profiles ADD COLUMN last_tested_at    TEXT        NULL;
ALTER TABLE connection_profiles ADD COLUMN last_test_detail  TEXT        NULL;

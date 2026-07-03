-- V006: global application settings (key/value). First use: HTTP/HTTPS proxy configuration.
-- No secrets are stored here — the proxy password lives in Windows Credential Manager.
CREATE TABLE app_settings (
    key   TEXT NOT NULL PRIMARY KEY,
    value TEXT
);

-- Obsync local state schema (V001).
-- Guids are stored as TEXT, timestamps as ISO-8601 TEXT, booleans as INTEGER (0/1),
-- and complex sub-objects as JSON TEXT. No secrets are ever stored here.

CREATE TABLE connection_profiles (
    id                       TEXT    NOT NULL PRIMARY KEY,
    name                     TEXT    NOT NULL,
    server_name              TEXT    NOT NULL,
    auth_mode                INTEGER NOT NULL,
    username                 TEXT        NULL,
    encrypt                  INTEGER NOT NULL,
    trust_server_certificate INTEGER NOT NULL,
    connect_timeout_seconds  INTEGER NOT NULL,
    created_at               TEXT    NOT NULL,
    updated_at               TEXT    NOT NULL
);

CREATE TABLE repository_profiles (
    id              TEXT    NOT NULL PRIMARY KEY,
    name            TEXT    NOT NULL,
    owner           TEXT    NOT NULL,
    repository_name TEXT    NOT NULL,
    remote_url      TEXT        NULL,
    default_branch  TEXT    NOT NULL,
    auth_mode       INTEGER NOT NULL,
    created_at      TEXT    NOT NULL,
    updated_at      TEXT    NOT NULL
);

CREATE TABLE jobs (
    id                    TEXT    NOT NULL PRIMARY KEY,
    name                  TEXT    NOT NULL,
    description           TEXT        NULL,
    enabled               INTEGER NOT NULL,
    connection_profile_id TEXT    NOT NULL,
    repository_profile_id TEXT    NOT NULL,
    databases_json        TEXT    NOT NULL,
    branch                TEXT        NULL,
    destination_folder    TEXT    NOT NULL,
    commit_mode           INTEGER NOT NULL,
    local_export_path     TEXT        NULL,
    selection_json        TEXT    NOT NULL,
    schedule_json         TEXT    NOT NULL,
    advanced_json         TEXT    NOT NULL,
    run_summary_json      TEXT    NOT NULL,
    created_at            TEXT    NOT NULL,
    updated_at            TEXT    NOT NULL,
    FOREIGN KEY (connection_profile_id) REFERENCES connection_profiles (id) ON DELETE RESTRICT,
    FOREIGN KEY (repository_profile_id) REFERENCES repository_profiles (id) ON DELETE RESTRICT
);

CREATE TABLE runs (
    id               TEXT    NOT NULL PRIMARY KEY,
    run_key          TEXT    NOT NULL,
    job_id           TEXT    NOT NULL,
    job_name         TEXT    NOT NULL,
    trigger          INTEGER NOT NULL,
    status           INTEGER NOT NULL,
    server_name      TEXT    NOT NULL,
    databases        TEXT    NOT NULL,
    started_at       TEXT    NOT NULL,
    completed_at     TEXT        NULL,
    duration_ms      INTEGER NOT NULL,
    objects_scanned  INTEGER NOT NULL,
    objects_added    INTEGER NOT NULL,
    objects_modified INTEGER NOT NULL,
    objects_deleted  INTEGER NOT NULL,
    objects_failed   INTEGER NOT NULL,
    commit_sha       TEXT        NULL,
    commit_url       TEXT        NULL,
    error_message    TEXT        NULL,
    FOREIGN KEY (job_id) REFERENCES jobs (id) ON DELETE CASCADE
);

CREATE INDEX ix_runs_job_id ON runs (job_id, started_at DESC);

CREATE TABLE run_logs (
    id        INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    run_id    TEXT    NOT NULL,
    timestamp TEXT    NOT NULL,
    level     INTEGER NOT NULL,
    message   TEXT    NOT NULL,
    detail    TEXT        NULL,
    FOREIGN KEY (run_id) REFERENCES runs (id) ON DELETE CASCADE
);

CREATE INDEX ix_run_logs_run_id ON run_logs (run_id, id);

CREATE TABLE run_changes (
    id            INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    run_id        TEXT    NOT NULL,
    change_type   INTEGER NOT NULL,
    object_type   INTEGER NOT NULL,
    schema_name   TEXT    NOT NULL,
    object_name   TEXT    NOT NULL,
    relative_path TEXT    NOT NULL,
    previous_hash TEXT        NULL,
    new_hash      TEXT        NULL,
    FOREIGN KEY (run_id) REFERENCES runs (id) ON DELETE CASCADE
);

CREATE INDEX ix_run_changes_run_id ON run_changes (run_id);

CREATE TABLE object_states (
    id                INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    job_id            TEXT    NOT NULL,
    database_name     TEXT    NOT NULL,
    object_type       INTEGER NOT NULL,
    schema_name       TEXT    NOT NULL,
    object_name       TEXT    NOT NULL,
    object_id         INTEGER     NULL,
    file_path         TEXT    NOT NULL,
    last_hash         TEXT    NOT NULL,
    last_scripted_at  TEXT    NOT NULL,
    last_committed_at TEXT        NULL,
    last_commit_sha   TEXT        NULL,
    last_run_id       TEXT        NULL,
    last_status       INTEGER NOT NULL,
    error_message     TEXT        NULL,
    FOREIGN KEY (job_id) REFERENCES jobs (id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX ux_object_states_identity
    ON object_states (job_id, database_name, object_type, schema_name, object_name);

-- V005: export/backup output modes.
-- Make repository_profile_id nullable (Export Only jobs have no GitHub repo) and add export_path.
-- SQLite can't drop NOT NULL in place, so rebuild the jobs table. The migration runner disables
-- foreign_keys for the migration, so dropping the referenced jobs table is safe; ids are preserved,
-- so runs/object_states keep resolving.
CREATE TABLE jobs_new (
    id                    TEXT    NOT NULL PRIMARY KEY,
    name                  TEXT    NOT NULL,
    description           TEXT        NULL,
    enabled               INTEGER NOT NULL,
    connection_profile_id TEXT    NOT NULL,
    repository_profile_id TEXT        NULL,   -- was NOT NULL; null for Export Only
    databases_json        TEXT    NOT NULL,
    branch                TEXT        NULL,
    destination_folder    TEXT    NOT NULL,
    commit_mode           INTEGER NOT NULL,
    local_export_path     TEXT        NULL,
    export_path           TEXT        NULL,   -- new: Export Only destination (folder or .zip)
    reviewers_json        TEXT        NULL,
    selection_json        TEXT    NOT NULL,
    schedule_json         TEXT    NOT NULL,
    advanced_json         TEXT    NOT NULL,
    run_summary_json      TEXT    NOT NULL,
    created_at            TEXT    NOT NULL,
    updated_at            TEXT    NOT NULL,
    FOREIGN KEY (connection_profile_id) REFERENCES connection_profiles (id) ON DELETE RESTRICT,
    FOREIGN KEY (repository_profile_id) REFERENCES repository_profiles (id) ON DELETE RESTRICT
);

INSERT INTO jobs_new
    (id, name, description, enabled, connection_profile_id, repository_profile_id, databases_json,
     branch, destination_folder, commit_mode, local_export_path, export_path, reviewers_json,
     selection_json, schedule_json, advanced_json, run_summary_json, created_at, updated_at)
SELECT
    id, name, description, enabled, connection_profile_id, repository_profile_id, databases_json,
    branch, destination_folder, commit_mode, local_export_path, NULL, reviewers_json,
    selection_json, schedule_json, advanced_json, run_summary_json, created_at, updated_at
FROM jobs;

DROP TABLE jobs;
ALTER TABLE jobs_new RENAME TO jobs;

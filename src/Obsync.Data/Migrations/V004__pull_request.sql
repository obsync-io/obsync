-- V004: pull request commit mode.
-- Reviewers (GitHub usernames) requested on a job's PR, and the PR opened by each run.
ALTER TABLE jobs ADD COLUMN reviewers_json TEXT NULL;   -- JSON array of usernames (like databases_json)

ALTER TABLE runs ADD COLUMN pr_url    TEXT    NULL;
ALTER TABLE runs ADD COLUMN pr_number INTEGER NULL;

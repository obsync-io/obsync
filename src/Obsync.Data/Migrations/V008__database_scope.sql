-- Dynamic database scope: a job can target all user databases on the server (resolved at run
-- start) instead of a fixed list, with an optional exclusion list. Additive columns.
ALTER TABLE jobs ADD COLUMN database_scope INTEGER NOT NULL DEFAULT 0;
ALTER TABLE jobs ADD COLUMN excluded_databases_json TEXT NULL;

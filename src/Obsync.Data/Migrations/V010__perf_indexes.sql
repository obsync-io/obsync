-- Performance indexes for VLDB-scale runs. Reading a run's changes orders by
-- (change_type, schema_name, object_name); without a covering index SQLite sorts 100k+ rows in a
-- temp B-tree on every load. The global recent-runs query orders by started_at across all jobs,
-- which the existing (job_id, started_at) index cannot serve.
CREATE INDEX ix_run_changes_run_order ON run_changes (run_id, change_type, schema_name, object_name);
CREATE INDEX ix_runs_started_at ON runs (started_at DESC);

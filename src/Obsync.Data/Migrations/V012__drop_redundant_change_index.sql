-- V001 created ix_run_changes_run_id ON run_changes (run_id); V010 added
-- ix_run_changes_run_order ON run_changes (run_id, change_type, schema_name, object_name), whose
-- leading run_id column serves every lookup the old single-column index could. The old index is a
-- strict-prefix duplicate: each run_changes insert (hundreds of thousands on a VLDB first run)
-- maintains both indexes for zero query benefit. Drop the redundant one.
DROP INDEX ix_run_changes_run_id;

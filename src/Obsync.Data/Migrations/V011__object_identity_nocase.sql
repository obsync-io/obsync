-- SQL Server object identities are case-insensitive (in the default collations), but the tracked
-- state's unique index compared names with BINARY collation. A case-only rename (dbo.Foo -> dbo.FOO)
-- therefore INSERTED a second row instead of updating the first, and every later run failed loading
-- the prior map with a duplicate-identity error. Rebuild the index with NOCASE identity columns.

-- De-duplicate any case-twin rows first (keep the most recent — upserts update in place, so a twin
-- is always a later INSERT with a higher id).
DELETE FROM object_states
WHERE id NOT IN (
    SELECT MAX(id) FROM object_states
    GROUP BY job_id,
             database_name COLLATE NOCASE,
             object_type,
             schema_name COLLATE NOCASE,
             object_name COLLATE NOCASE
);

DROP INDEX ux_object_states_identity;

CREATE UNIQUE INDEX ux_object_states_identity
    ON object_states (job_id,
                      database_name COLLATE NOCASE,
                      object_type,
                      schema_name COLLATE NOCASE,
                      object_name COLLATE NOCASE);

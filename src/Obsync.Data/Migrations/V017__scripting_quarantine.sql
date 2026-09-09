-- Objects Obsync has observed it cannot script, under evidence that has not changed since.
--
-- A skip of an object that HAS prior state blocks its type's watermark from advancing. That is
-- right for a transient failure -- a change predating the new watermark would otherwise never be
-- re-examined -- and a trap for a permanent one. A procedure later altered WITH ENCRYPTION, or a
-- table SMO cannot script, has a modify_date above the frozen watermark, so it is re-streamed every
-- run, yielded as unscriptable every run, and re-freezes the watermark every run. The loop has no
-- exit: every object of that type modified since the freeze is re-scripted forever, and that set
-- only grows. The run also escalates to Warning on every run, which trains people to ignore the
-- colour.
--
-- Nothing in the product could tell the two cases apart, because at the moment of the skip they are
-- identical. The distinction is only visible over time: an object skipped AGAIN, for the SAME
-- reason, at the SAME modify_date, is not going to succeed next time either. That is what this
-- table records -- an observation, not a human acknowledgement.
--
-- A quarantined object stops freezing its type's watermark. The watermark then advances past it, so
-- the provider stops streaming it, and the incremental planner treats it as an ordinary unchanged
-- object: marked seen (so the deletion pass does not remove it) with its last good file retained.
-- It leaves quarantine automatically when its modify_date changes, because that is the only
-- evidence that anything about it is different.
--
-- Identity columns use NOCASE to match object_states (see V011): SQL Server object names are
-- case-insensitive in the default collations, and a BINARY comparison here would let a case-only
-- rename create a second row that never matches the first.
CREATE TABLE scripting_quarantine (
    job_id        TEXT    NOT NULL,
    database_name TEXT    NOT NULL COLLATE NOCASE,
    object_type   INTEGER NOT NULL,
    schema_name   TEXT    NOT NULL COLLATE NOCASE,
    object_name   TEXT    NOT NULL COLLATE NOCASE,
    modify_date   TEXT    NOT NULL,
    reason        TEXT    NOT NULL,
    first_seen_at TEXT    NOT NULL,
    last_seen_at  TEXT    NOT NULL,
    PRIMARY KEY (job_id, database_name, object_type, schema_name, object_name),
    FOREIGN KEY (job_id) REFERENCES jobs (id) ON DELETE CASCADE
);

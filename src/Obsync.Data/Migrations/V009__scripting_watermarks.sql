-- Incremental scripting watermarks: the max sys.objects.modify_date seen per job/database/type
-- on the last successful run. Stored verbatim as ISO-8601 TEXT (a server-local, opaque monotonic
-- value — never converted between time zones). Cascades away with the owning job, like object_states.
CREATE TABLE scripting_watermarks (
    job_id        TEXT    NOT NULL,
    database_name TEXT    NOT NULL,
    object_type   INTEGER NOT NULL,
    watermark     TEXT    NOT NULL,
    PRIMARY KEY (job_id, database_name, object_type),
    FOREIGN KEY (job_id) REFERENCES jobs (id) ON DELETE CASCADE
);

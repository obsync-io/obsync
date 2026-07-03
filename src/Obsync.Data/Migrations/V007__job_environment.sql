-- Environment tags: free-form tags on a job, denormalized onto each run so History shows the
-- environment a run executed under. Additive columns (nullable TEXT holding a JSON array).
ALTER TABLE jobs ADD COLUMN tags_json TEXT NULL;
ALTER TABLE runs ADD COLUMN tags_json TEXT NULL;

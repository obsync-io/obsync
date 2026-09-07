-- V014: runs distinguish an object whose DEFINITION changed from a file that merely had to be
-- restored to the repository.
--
-- Pull-request mode recuts its head branch from the base every run, so while a pull request sits
-- unmerged every file it proposed is absent from the working tree. The engine rewrote those files
-- and counted every one of them as "modified", which made a run where nothing in SQL had changed
-- report the entire estate as modified. The two facts are now counted separately.
ALTER TABLE runs ADD COLUMN objects_restored INTEGER NOT NULL DEFAULT 0;

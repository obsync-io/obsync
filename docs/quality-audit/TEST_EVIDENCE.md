# Obsync production-readiness audit — test evidence

Audit date: 2026-07-15 · Machine: MOWNICA (Windows 11 Home 26200, 12 logical cores) ·
SQL Server 2025 RTM-GDR 17.0.1125.2 (localhost, Windows auth) · git 2.50.1.windows.1 ·
.NET SDK 10.0.302. Baseline commit `002afdf` (v0.8.2); fixes landed in `eea2781`, `50b0911`,
`bfbb947`, `79ff112`, `4e5bbd8`* and later commits on `main` (\*benchmark ran at `4e5bbd8`).

## 1. Build & unit/integration test baseline (before fixes)

```
dotnet build Obsync.slnx --no-incremental      → Build succeeded. 0 Warning(s) 0 Error(s) (43.0s)
dotnet test  Obsync.slnx --no-build            → 477/477 passed
  Shared 108 · Engine 92 · Metadata 45 · Integration 43 · Git 23 · App 166
```

## 2. Final build & test state (after all fixes)

```
dotnet build Obsync.slnx --no-incremental      → Build succeeded. 0 Warning(s) 0 Error(s)
dotnet test  Obsync.slnx --no-build            → 544/544 passed
  Shared 111 · Engine 94 · Metadata 47 · Integration 53 · Git 31 · App 208
```

+67 tests over baseline, all regression tests for defects fixed in this audit. One integration-test
run mid-audit reported a single failure that did not reproduce in four consecutive re-runs
(50/50 ×4); the failing test name was not captured — noted as an unconfirmed flake to watch in CI.

## 3. End-to-end runtime battery (`tools/Obsync.E2E`)

Harness added by this audit: drives the REAL pipeline (metadata + SMO providers → hashing →
file writes → git) against disposable databases (`ObsyncAuditE2E`, `ObsyncAuditE2E2` — created and
dropped by the harness) and local bare git repositories standing in for GitHub. 15 scenarios,
73 assertions.

- Run 1 (baseline code): **S01 failed the entire run** — `'UserDefinedDataType' does not contain a
  definition for 'IsSystemObject'` (OBS-01, Critical) — plus 17 harness-expectation errors.
- Run 2 (after OBS-01 fix): 55/73; confirmed the schema-filter mass-delete live (S13: **−12
  deletions with status Succeeded** on narrowing the filter) and isolated the remaining
  harness-path bugs (single-DB jobs don't nest a per-database folder; git `core.quotepath`).
- Final run (all fixes): **73/73 PASS** (`obsync-e2e/20260715-184002/e2e-results.md`). Coverage:

| Scenario | What it proves |
|---|---|
| S01 initial full run | 28 objects scripted incl. hostile names (`dbo.usp with space`, `dbo.uspÜnïcødé`, `Weird]Name`, 120-char name hashed suffix), all five generated artifacts, reference data with `O''Brien` escaping + `Ünïcødé ✓` fidelity, over-cap table skipped not truncated, **no credential material in any committed file**, commit pushed to the remote |
| S02 no-change | No new commit, +0/~0/−0 |
| S03 modify | Exactly the changed proc (+ inventory) committed; not re-detected on the next run |
| S04 delete | One deletion, file removed at remote HEAD, others intact, not repeated |
| S05 rename | Old file deleted + new added in one run |
| S06 encrypted module | Warning + reported skip in run logs + counted, **no file, no collateral deletion** |
| S07 push failure | Warning (never false success), remote untouched; **stranded commit re-pushed by the next run** and the blocked change verified at the remote |
| S08 non-fast-forward divergence | Loud Warning with actionable text; **foreign commit never overwritten (no force push)** |
| S09 cancellation | Status Cancelled, zero stuck Running rows, rerun recovers to a full tree |
| S10 Export Only (folder + zip) | Files exported, **no git repo, no workspace clone, no commit sha** — the mode does only what it says |
| S11 Local Commit Only | Local commit exists; **remote commit count unchanged** |
| S12 determinism | Two fresh environments over the same DB → **identical git tree hashes** |
| S13 schema filter | Filter honored; widening deletes nothing; **narrowing deletes nothing** (post-fix; −12 pre-fix) |
| S14 two jobs, same repository, concurrent | Both complete, both destination trees present (per-repo lock serialization) |
| S15 all-user-databases scope | Both test DBs resolved and nested per-DB; excluded databases (incl. the user's real DBs) never scripted |

## 4. Scheduled execution observed live (service, desktop app closed)

The real `Obsync.Service.exe` (repo build) ran console-hosted against an isolated data root
(`OBSYNC_DATA_ROOT` sandbox; the app was not involved), with one ExportOnly job on cron
`0 0/2 * * * ?` against a disposable database.

- **Baseline build: every scheduled fire crashed** — `KeyNotFoundException: Key trigger not found`
  at `SyncQuartzJob.cs:38`, at 19:04:00 and 19:06:00, before reaching the engine; no run rows, no
  export, while the scheduler heartbeat stayed fresh (OBS-00, Critical, ships in 0.8.0–0.8.2).
- **Fixed build:** service start 19:10:57 →
  - 19:11:06 `CatchUp run … finished with status Succeeded` (the occurrence missed during the
    rebuild was caught up exactly once — MissedRunPolicy observed working),
  - 19:12:00.009 `Scheduled run … Succeeded`, 19:14:00.014 `Scheduled run … Succeeded`
    (fires within 15 ms of the advertised times),
  - export tree written (`docs/ metadata/ procedures/ security/`),
  - sandbox `obsync.db` after the observation:
    ```
    run_key         | trigger      | status    | started_at (UTC)
    20260715-191106 | 3 (CatchUp)  | Succeeded | 02:11:06.977
    20260715-191200 | 1 (Scheduled)| Succeeded | 02:12:00.009
    20260715-191400 | 1 (Scheduled)| Succeeded | 02:14:00.014
    schedulerHeartbeat = {"TimestampUtc":"02:14:06.969","Account":"MOWNICA\\nnall","Version":"0.8.2"}
    jobs.NextRunAt     = 02:16:00 (the true next cron fire)
    ```
  - exactly one run per occurrence (duplicate prevention held).

Graceful `Stop-Service` semantics (the new stop-interrupts-runs hosted service) could not be
exercised against the console host (headless console processes only accept a forceful kill) —
remains on the human verification list against a real service install.

## 5. Identity evidence

- Desktop/manual runs record `TriggeredBy` = interactive identity; the observed service runs
  recorded the executing account `MOWNICA\nnall` in the heartbeat and run rows (see §4 table).
- On the user's real machine, a stale catch-up (due 2026-07-11) pending despite a Running installed
  service corroborated OBS-00 (details + one benign accidental NoChanges catch-up run disclosed in
  BUG_AUDIT_REPORT §"Incidental live finding").

## 6. SQL Server catalog probes

`sys.objects.modify_date` behavior on SQL Server 2025 (probe script in session log; disposable
`ObsyncModDateProbe` DB):

| Operation | Bumps table modify_date |
|---|---|
| CREATE NONCLUSTERED INDEX | **YES** |
| DROP NONCLUSTERED INDEX | **YES** |
| CREATE TRIGGER on the table | **YES** |
| GRANT on the table | NO |
| sp_addextendedproperty | NO |

→ incremental scripting misses inline grant/extended-property changes only (documented caveat,
OPEN-03); index and trigger DDL are caught.

SQLite `ON CONFLICT` vs `COLLATE NOCASE` unique index: probed empirically (in-memory DB) — a plain
column-list conflict target matches a NOCASE index and updates in place, including re-casing the
key columns. Basis for the V011 migration design.

## 7. Performance benchmark (real pipeline, `tools/Obsync.Benchmark`)

Post-fix build (`4e5bbd8`), local SQL Server 2025, LocalCommitOnly against a local bare repo
(full git cost, zero network). Workload: **50,202 objects** (existing ObsyncBench DB: modules +
tables + hostile/encrypted objects). Full table in PERFORMANCE_REPORT.md; headline:

```
full initial   50,202 obj  587.7s (scripting 460.5s, first commit 126.0s)  peak 363 MB
no-change      50,202 obj   39.5s                                          peak 439 MB
no-change warm 50,202 obj    6.7s  (7,473 obj/s)                           peak 393 MB
incremental (500 changed)   11.3s                                          peak 394 MB
cancellation latency 0.17s from token to return (status Cancelled)
```

## 8. Commands of record

```
git log --oneline    002afdf → eea2781 → 50b0911 → bfbb947 → 79ff112 → … (audit fixes on main)
dotnet build Obsync.slnx --no-incremental
dotnet test  Obsync.slnx --no-build
dotnet run --project tools/Obsync.E2E                # E2E battery (drops its DBs unless --keep)
dotnet run --project tools/Obsync.E2E -- --seed-service <root>   # service-observation sandbox
OBSYNC_DATA_ROOT=<root>\Obsync  Obsync.Service.exe   # isolated console-hosted service
dotnet run -c Release --project tools/Obsync.Benchmark -- --skip-generate --cancel-after 8
```

All test resources were disposable and local: databases `ObsyncAuditE2E`, `ObsyncAuditE2E2`,
`ObsyncSvcE2E`, `ObsyncModDateProbe` (created and dropped), temp git remotes/workspaces under
`%TEMP%`, an isolated service data root. The user's real data root, Credential Manager, installed
service, and GitHub were not modified (one read-only inspection of the real `obsync.db`, plus the
disclosed accidental NoChanges catch-up run — see BUG_AUDIT_REPORT).

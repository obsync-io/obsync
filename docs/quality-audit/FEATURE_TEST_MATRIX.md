# Obsync production-readiness audit — feature/test matrix

Audit date: 2026-07-15. Every user-visible feature and option, with its verification result.

**Result legend** — **RT**: verified at runtime this session (E2E battery, live service
observation, benchmark, or unit/integration test executing the real component) · **CODE**: verified
by full code-path tracing this session (no runtime exercise) · **FIXED**: defect found, fixed, and
regression-verified this session (bug IDs reference BUG_AUDIT_REPORT.md) · **LIMIT**: works as
designed with a documented limitation · **HUMAN**: cannot be verified in this environment — needs
the owner (live GitHub, real MSI install, visual/DPI pass).

## 1. Dashboard

| Feature | Result | Evidence / notes |
|---|---|---|
| Total Sync Jobs / Successful / Failed / Objects Tracked cards | CODE | Derived from live repository queries on every load; refresh on activation + run-state events traced. |
| Latest Commit card | FIXED (OBS-28) | Blanked when the newest run had no commit; now scans the last 20 runs. `DashboardViewModelTests`. |
| Sync Jobs table: status, Last Run, Next Run, change counts | CODE + RT | Next Run stamped on save and kept Quartz-accurate; runtime-verified in the service observation (NextRunAt = true next fire). Running badge survives navigation (coordinator-shared state). |
| Manual run / Open row actions | CODE | Routed through the run coordinator (concurrency + production-tag guard chokepoint). |
| Scheduler warning banners | RT (negative) + CODE | Banner logic traced; the live observation proved the underlying heartbeat mechanism — and OBS-00 showed heartbeat-fresh ≠ runs-executing until fixed. |
| Empty/loading/failed states | CODE | Empty-state collapses grid; status banners now cleared on reload (OBS-40 batch). |
| Stale status messages after navigation | FIXED (OBS-40d) | Cleared at LoadAsync. |
| Getting-started buttons navigate but don't open dialogs | LIMIT | Cosmetic under-delivery; documented. |

## 2. Servers

| Feature | Result | Evidence |
|---|---|---|
| Add/Edit/Delete server; delete ordering (row before credential) | CODE | FK RESTRICT verified in schema; delete order traced. |
| Test connection = engine parity | CODE | Probe and engine share `SqlConnectionStringFactory` (Encrypt/TrustCert/timeout honored); SMO mirrors flags. |
| Host, HOST\INSTANCE, HOST,PORT, localhost formats | RT (localhost) + CODE | Server name passes through `DataSource` verbatim; localhost exercised throughout E2E. Named-instance/port formats: pass-through by construction, not exercised live (single local instance). |
| Windows Integrated auth | RT | All E2E and service runs. Identity shown = `WindowsIdentity.GetCurrent()` and matches the executing account (service observation recorded `MOWNICA\nnall`). |
| SQL authentication | CODE | Password from Credential Manager; blank-password-on-edit keeps the saved secret (incl. Test fallback). No SQL-auth login existed in the environment to exercise live. |
| Encrypt / Trust server certificate / timeout | FIXED (OBS-22/SEC-1) | TrustCert dialog default was ON — now off + caution text. `ServerDialogViewModelTests`. |
| Offline server, bad host, login denied, timeout | CODE | Failure surfaces as actionable probe/run errors; SQL transient classification tested (`SqlTransientErrorsTests`). Not fault-injected live. |
| Connection status persistence/badge | CODE | V002 columns + status converters traced. |

## 3. Repositories & GitHub

| Feature | Result | Evidence |
|---|---|---|
| Add/Edit/Delete; validate; check token; saved-token fallback on blank | CODE | Traced incl. fallback paths. |
| Token trim | FIXED (OBS-17) | Stored + Octokit-side trim. Tests. |
| Token storage & hygiene | RT + CODE | Credential Manager only; env-var git-config injection (never argv/.git/config); E2E S01 grep: no token in any committed file. |
| Repository rename (owner/name edit) | FIXED (OBS-07) | Existing clone silently pushed to the OLD remote forever; now `remote set-url` on prepare. Real-git test. |
| Read-only/revoked token behavior | FIXED (OBS-18) + CODE | git 401/403/404 now permanent (no useless retries); token permission checker (write probe) traced. Live revoked-token run needs GitHub (HUMAN). |
| Rate limiting | FIXED (OBS-39) + CODE | Secondary-limit Retry-After honored (capped 120 s); primary limit deliberately not retried. |
| Network loss / Octokit failures | FIXED (OBS-16) | HttpRequestException/timeout now return Result failures instead of raw run-Failed. |
| Proxy (None/System/Manual) for API + git | FIXED (OBS-21) + CODE | Manual mode now requires a valid URL (was silently direct); credentials env-injected for git; bypass-list asymmetry documented (OPEN). Live proxy pass = HUMAN. |
| Git missing → bundled MinGit → PATH resolution | CODE | Resolution order traced and matches what the MSI ships; SHA-pinned MinGit at build. |
| Dirty tree / stale lock / corrupt clone recovery | FIXED (OBS-06, OBS-08) + RT | index.lock heal + force-sync tested against real git repos; corrupt-clone re-clone pre-existing with tests. |
| Concurrent git operations | RT | Per-repo lock: E2E S14 two jobs, same repo, concurrent → both complete, both trees present. |
| Push rejection / non-fast-forward / divergence | RT | E2E S07 (stranded commit re-pushed) + S08 (loud, no force push, foreign commit intact). Wedge recovery is manual — OPEN-01. |
| Empty repository initial sync | RT | Every E2E environment starts from a seeded near-empty bare repo. |

## 4. Sync job wizard

| Feature | Result | Evidence |
|---|---|---|
| Job name validation; duplicate names | FIXED (OBS-33) | Case-insensitive uniqueness at save. Tests. |
| Tags + production-tag Run-Now guard | CODE | Guard at the coordinator chokepoint; scheduled runs never prompt; CLI does not prompt (README scoped, OPEN-08). |
| Server selection, load databases, selected DBs, empty selection | CODE + RT | Dynamic scope runtime-verified (S15); fixed-list used across all E2E jobs. |
| All-user-databases + exclusions | RT | S15: both test DBs resolved, per-DB nesting, exclusions honored (user's real DBs never scripted). |
| Presets (Recommended 10 types / Programmability 4 / Full Schema 24 / Custom picker) | RT + CODE | Preset expansion table verified against the catalog — **no preset/description mismatch found**. FullSchema + ProgrammabilityOnly + Custom exercised live in E2E. |
| Schema filters (allow-list, case-insensitive) | RT + FIXED (OBS-02) | S13 live; narrowing no longer deletes out-of-scope files (incl. schema DDL files). T-SQL side uses DB collation (LIMIT on case-sensitive-collation servers; documented F14). |
| Server-level objects (logins, Agent jobs, linked servers…) | CODE | Type-level skip suspension + `$server` scope traced; unit-tested (`ServerObjectScriptingTests`); not exercised against live msdb/Agent (local Agent not running) — HUMAN for Agent-object fidelity. |
| Reference data: table list, deterministic INSERTs, row cap | RT + FIXED (OBS-24) | S01: escaping/unicode/ordering verified at the remote; over-cap skipped not truncated; missing table = reported skip; CR/LF data now corruption-proof. |
| Generated files toggles (inventory/options/permissions/docs/security review) | RT | All five produced and committed in S01; toggle wiring traced; review row added (OBS-34). |
| Commit modes: Direct / PR / Local-only / Export-only | RT (3 of 4 fully) | Direct: whole battery. Local-only: S11 (commit local, remote untouched). Export-only: S10 (files + zip, zero git). PR mode: full state-machine tested via `DeliveryGateTests` + PR-mode fixes (OBS-15); **live PR against real GitHub = HUMAN**. |
| Destination folder & path validation | FIXED (OBS-35) | Traversal/rooted/invalid chars rejected at step 3. 10 test cases. |
| Local export path mirror | LIMIT (OPEN-07) | Mirrors changed files only, no deletions — documented. |
| Schedule cadences (manual/hourly/daily/weekly/cron), run-on-startup, only-if-changes | RT + FIXED | Cron runtime-verified live (service observation). Cadence↔preview agreement traced (incl. hourly midnight-anchoring LIMIT F13). Invalid/never-firing cron now rejected (OBS-10); heartbeat-commit semantics honestly labeled (OBS-34). |
| Maintenance windows | FIXED (OBS-11) + CODE | Window math incl. overnight wrap unit-tested; Daily/Weekly starvation now blocked at save; GetNextRun window-advances all cadences. Hourly display flap = OPEN-05. |
| Advanced knobs (workers, timeouts, retries, ref-data cap, incremental) | CODE | End-to-end wiring verified per-knob (audit option matrix); SqlRetryCount/GitRetryCount remain import-only (LIMIT F8). |
| Review step completeness & accuracy | FIXED (OBS-34) | Missing rows added; rebuilt on every entry (no stale-review path exists). Tests. |
| Edit round-trip fidelity | CODE | Every wizard-surfaced field loads back; unsurfaced fields preserved by mutating the loaded instance. |

## 5. Object scripting fidelity (runtime, disposable DB)

| Class | Result | Evidence |
|---|---|---|
| Schemas, tables (identity/computed/defaults/checks/FKs/unique), indexes (incl. filtered + included), views (incl. indexed), procs, scalar/inline/multi TVFs, triggers, sequences, synonyms, alias + table types, XML schema collections, roles, users, grants, extended properties | RT | E2E S01: 28 objects scripted and committed; spot-checked content at the bare remote. |
| Hostile names: spaces, unicode, `]`, reserved words, 120+ chars | RT | Files `dbo.usp with space.sql`, `dbo.uspÜnïcødé.sql`, `dbo.Weird]Name.sql`, hash-suffixed long name — all present and stable across runs. |
| UDT/table-type/XML/aggregate databases | FIXED (OBS-01, Critical) | Previously failed the ENTIRE run; now scripted (map corrected, reflection-locked by `SmoTypeMapTests`). |
| Encrypted modules | RT | S06: Warning + reported skip + counted + no file + no collateral deletion. LIMIT: forces a full type-scan every run (documented; `.obsyncignore` mitigation). |
| CLR modules | FIXED (OBS-05) | Were silently invisible (and wrongly deletable); now reported skips. Catalog-semantics fix; live CLR assembly not exercised (HUMAN if CLR estates matter). |
| Determinism / byte stability | RT | S12: two independent environments over the same DB → identical git tree hashes; S02: no false changes on re-run. |
| Recreate-in-clean-database applicability | HUMAN | Generated scripts were content-verified, not replayed into a clean database this session. |
| Columnstore/partitioned/temporal/memory-optimized/full-text | CODE / HUMAN | Types routed via SMO (partition objects unit-tested at the provider level); not present in the E2E database. |

## 6. Change detection (production-critical)

| Transition | Result | Evidence |
|---|---|---|
| Added / Modified / Deleted / Renamed | RT | S01/S03/S04/S05 — each detected exactly once, never repeated, files correct at the remote. |
| No-change runs stay clean | RT | S02 (+ benchmark warm run: 50,202 objects, +0/~0/−0). |
| Failed scripting never appears as deletion | RT | S06 (encrypted) + suspended-deletion paths; CLR case fixed (OBS-05). |
| Partial scans never delete out-of-scope objects | FIXED (OBS-02/03) + RT | Schema-filter narrowing (was −12 live!) and type deselection now retain; databases removed from a job always retained (by construction). |
| Mass-disappearance safety | FIXED (OBS-04) | Circuit breaker: unattended runs suspend >50-object/>50% wipes with Warning; manual run confirms. Integration test with 120 objects. |
| Hashing/normalization: no hidden changes, no false positives | RT + FIXED (OBS-24) | S02/S12; reference-data CR/LF corruption closed; normalization is opt-out per job. |
| Baselines vs later changes | RT | S01 (all Added) vs S03 (one Modified) distinct in counts and commits. |
| Incremental watermarks | RT + FIXED (OBS-25/planner ordering) | Benchmark run 4 (500 changed → ~501, 11.3 s); watermark discipline (snapshot-first, skip-gating, healthy-status-only persistence) traced + planner regression tests. LIMIT: inline table grants/EPs lag (OPEN-03, measured on SQL 2025). |

## 7. History & timeline

| Feature | Result | Evidence |
|---|---|---|
| Runs/Timeline views, filters, search | CODE + FIXED (OBS-40b/c) | Job filter now case-insensitive; cap caption honest. |
| Counts, duration, commit sha, change lists, diff viewer | CODE | Diff viewer reads the local clone only (no token/network); entry points gated by commit presence. |
| Open on GitHub / copy sha | FIXED (OBS-41) | Now hidden for Export/Local-only; PR links at the run's commit sha. |
| Export report (HTML/CSV/JSON) | CODE | Secret-free by construction (see SECURITY_REVIEW); streamed for large runs. |
| Retention & cleanup, keep-forever | CODE | Cannot delete Running rows; startup + daily service cleanup; 0 = forever. |
| Failed/warning/no-change run details | RT | Every such state produced and inspected during E2E (logs carry per-object skip reasons — S06 checked log content). |

## 8. Settings

| Area | Result | Evidence |
|---|---|---|
| General: retention, committer identity, production tags, notifications | CODE | Each traced UI → app_settings → consumer (engine resolves committer/retention per run; both hosts read the same keys). |
| Unsaved-input survival | FIXED (OBS-12) | Activation refresh no longer wipes Settings / clears typed passwords. Tests. |
| Alerts: email/webhook, triggers, scheduled-only, test send | CODE + FIXED (OBS-20, OBS-40e) | Disable no longer deletes the stored SMTP secret; port validated regardless of toggle; send isolation (15 s cap, best-effort, post-persistence) traced; crash-recovered runs now alert (OBS-32). Live SMTP/webhook delivery = HUMAN. |
| Network & storage: proxy, workspaces root, data folder view | FIXED (OBS-21) + CODE | Manual proxy URL validated; workspaces override consumed per-run (no restart), diagnostics/support bundle honor it. |
| Security & audit: least-priv script generator, audit log + export | CODE | Generator escapes identifiers/literals, no sysadmin; audit events cover run outcomes incl. service runs; CSV/JSON export traced. |
| Diagnostics + support bundle | CODE | Probe rows reuse real probes; bundle secret-free (see SECURITY_REVIEW). |
| About: version, update check | CODE | Notify-only, throttled, proxy-aware, prerelease-safe. Live "new version available" path = HUMAN. |
| "Read-only by design" / "secrets never in the state DB" statements | RT + CODE | Both audited true (SECURITY_REVIEW); state-DB claim now unqualified after OBS-19 redaction. |

## 9. Windows Service & scheduler (highest-risk area)

| Feature | Result | Evidence |
|---|---|---|
| **Scheduled execution with the app closed** | **FIXED (OBS-00, Critical) + RT** | v0.8.0–0.8.2 crashed on EVERY plain cron fire (`Key trigger not found`) — zero scheduled runs ever executed in shipped builds, masked by a healthy heartbeat. Fixed and observed live: fires at :12:00.009 and :14:00.014 exactly as advertised, app closed, exports written, run rows recorded. |
| Missed-run catch-up | RT | Observed live: exactly one CatchUp → Succeeded for the occurrence missed while the service was down; policy unit tests pre-existing. |
| Duplicate prevention (app/service/CLI, per-job + per-repo) | RT + CODE | One run per occurrence observed; cross-process file lock crash-safe; E2E S14 for the repo lock. |
| Reconciliation ≤30 s for create/edit/delete | RT + FIXED (OBS-09, OBS-13) | Observed (job scheduled at startup); one bad cron no longer kills the loop; reconcile no longer fires unattended startup runs. |
| Enabled honored | FIXED (OBS-14) | Engine-level gate; integration test. |
| Heartbeat honesty | LIMIT + RT | Heartbeat proves scheduling liveness, not run success — OBS-00 demonstrated the gap; run-failure counts + alerts cover the rest. Banner text can misattribute causes (OPEN, Low). |
| Service under LocalSystem / dedicated accounts | HUMAN | Per-user data/credential constraint documented; requires machine-level installs to exercise. Real machine's installed service issue diagnosed (OBS-00, disclosed). |
| Graceful stop cancels in-flight runs | FIXED (OBS-31) — CODE | Stop-ordered hosted service interrupts Quartz jobs; console host can't receive a soft stop, so the Stop-Service drill = HUMAN. |
| Crash recovery | RT (mechanism) | Orphaned-run cleaner lock-probe design + tests; hard-killed service left no stuck rows in the observation (runs completed). Kill-mid-run drill = HUMAN. |
| Schedule persistence across restart/upgrade | RT + CODE | DB is the source of truth, rebuilt each start — observed across the service restart during testing. |

## 10–11. Failure handling, concurrency, lifecycle

| Scenario | Result | Evidence |
|---|---|---|
| Commit / push / PR failure state discipline (delivery gate) | RT | `DeliveryGateTests` (real engine + SQLite) all five modes + deletion retention; E2E S07/S08 live. |
| Cancellation (engine, UI, CLI Ctrl+C) | RT + FIXED (OBS-30) | S09 + benchmark probe (0.17 s latency, clean Cancelled); pre-run-wait cancel no longer crashes. |
| Final-persistence failure (disk full, DB busy) | FIXED (OBS-26) | Guarded with FailRun fallback; busy timeout 30 s (OBS-37). |
| Export failure atomicity | FIXED (OBS-27) | Zip temp+swap; staging cleaned in finally. |
| Job deleted while running (any process) | FIXED (OBS-29) | Cross-process lock check; test holds a real lock. |
| Two jobs same DB / same repo / same folder; manual+scheduled overlap | RT + CODE | S14; same-job overlap → skip-with-log (safe); same-folder collisions serialized by the repo lock. |
| App close / Windows shutdown / process kill mid-run | CODE + HUMAN | Confirm-on-close + orphan recovery traced; kill-mid-commit drill covered by the index.lock fix's test, full drill = HUMAN. |
| Service fatal exit code / SCM recovery | FIXED (OBS-31a) | Exit 1 on fatal. |
| CLI honesty (exit codes, lock conflicts) | FIXED (OBS-38) | Warning → exit 3, documented. |

## 12–15. Security, performance, installer, accessibility

| Area | Result | Evidence |
|---|---|---|
| Security review (full) | See SECURITY_REVIEW.md | No Critical/High; 2 Medium fixed. |
| Performance & scale | See PERFORMANCE_REPORT.md | 50k-object benchmark on the fixed build; cancel latency 0.17 s. Slow-network/low-memory/low-disk/500k = not measured (HUMAN/backlog). |
| Installer / upgrade / uninstall | CODE + HUMAN | WiX structure re-audited (MajorUpgrade + downgrade block, service account remembered, stop-before-replace, data preserved outside install dir, MinGit pinned); **clean-machine install/upgrade pass remains the owner's gate**, as does the first post-fix release. Uninstall data policy doc gap = OPEN (Low). |
| CI workflows | CODE | Build+test on push/PR; release gated on tag==version; previously green on GitHub runners (repo history). This audit's commits have not yet run on CI. |
| Accessibility & UI resilience | HUMAN (mostly) | Escape-close fixed on 2 dialogs (OBS-40a); AutomationProperties and keyboard paths previously UIA-verified per repo history; DPI/scaling/high-contrast/visual pass needs a human. |

## Coverage gaps summary (everything not runtime-verified this session)

Live GitHub (push/PR per commit mode against github.com, revoked-token and protected-branch
behavior), SQL authentication logins, named-instance/port connection formats, SQL Agent/linked-server
scripting against a live configured instance, CLR assemblies, columnstore/partitioned/temporal/
memory-optimized/full-text objects, alert delivery (SMTP/webhook), proxy environments, service under
LocalSystem/domain accounts + Stop-Service drill, MSI clean-machine install/upgrade, DPI/visual/
accessibility pass, VLDB at 500k objects, low-disk/low-memory/slow-network conditions. Each is
listed with its blocking status in RELEASE_READINESS.md.

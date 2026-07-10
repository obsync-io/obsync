# Launch readiness — evidence-based assessment

*Assessed 2026-07-10 against commit `3384b5a` (four parallel code audits over engine/consistency,
Git/GitHub, data/security/ops, and UI truthfulness, plus a measured benchmark of the real pipeline).
This document tracks what was found, what was fixed, and what remains. Update it as items close.*

**Verdict: conditionally ready.** The launch-blocking defects found by this pass are fixed and
regression-tested. What remains before flipping the switch is *human verification that cannot be
automated from the repository* (clean-machine MSI pass, one live end-to-end run against a real
GitHub repository per commit mode) plus a short list of P2 hardening items that are acceptable to
ship with, documented below.

---

## 1. Fixed in this pass (commit `3384b5a`; scheduling batch in `ef96254`)

| # | Sev | Finding | Fix | Verified by |
| --- | --- | --- | --- | --- |
| 1 | P0 | **Silent repo/DB divergence**: object-state hashes, deletion records, and watermarks advanced even when the commit, push, or PR failed — every later run reported "no changes" while the repository missed the work (worst in PR mode, whose head branch is recut every run) | Delivered gate: state advances only after durable delivery (commit for direct/local, opened PR for PR mode); deletions deferred to the same gate | `DeliveryGateTests` — 6 scenarios through the **real engine + real state DB** (only SQL/git faked), incl. deletion retention across a failed delivery |
| 2 | P0 | **Parallel directory-creation race**: a worker could write into a directory another worker had claimed-but-not-created; one race failed the whole run (reproduced twice in the first 2k benchmark) | `GetOrAdd` with creation inside the factory | Benchmark rerun — failure gone across all subsequent suites |
| 3 | P0 | **Truncated files could be committed**: writes were non-atomic and the "unchanged" self-heal checks existence only, so a torn file from a kill/cancel would be swept into the next commit as-is | Atomic temp+rename writes; `*.obsync-tmp` excluded from `git add`/`status` as a hard-kill backstop | `GitWorkspaceTests.CommitAll_ExcludesAtomicWriteTempFiles` |
| 4 | P1 | **GitHub PAT and proxy credentials on the git command line** — recorded verbatim by Windows process-creation auditing (Event 4688), Sysmon, and EDR into machine-wide security logs | Secrets moved to `GIT_CONFIG_*` environment variables (not captured by process auditing); `LC_ALL=C` pins stderr for error classification | Existing GitWorkspace E2E tests (auth path unchanged functionally) |
| 5 | P1 | **No per-repository lock** — two jobs sharing a repository profile could interleave `checkout`/`add -A` in one clone, producing cross-branch contaminated commits | Per-repository file lock; second job waits (bounded 30 min) and runs back-to-back | `JobRunLockTests` (named locks + `WaitAsync`) |
| 6 | P1 | **Watermark advanced past skipped objects** — a transiently skipped object whose change predated the new watermark was never re-examined until it changed again | Watermarks withheld for any type with skips that run | Delivered-gate + planner tests |
| 7 | P1 | **Incremental mode deleted committed files of `.obsyncignore`-matched objects** (full scans retain them) | Planner reports ignored prior-state objects; engine marks them seen | `IncrementalPlannerTests.IgnoredObject_WithPriorState…` regression |
| 8 | P1 | **No way to cancel a run** anywhere (UI or CLI), despite full engine support; SQL cancellation surfaced as a scary "severe error" *Failed* run | Per-job CTS + Cancel button in the Job Workspace; CLI Ctrl+C; provider errors under a requested cancellation recorded as **Cancelled** | Coordinator cancel tests; benchmark cancellation probe: 1.2 s latency, status `Cancelled` |
| 9 | P1 | **MSI major upgrade silently reset the service account to LocalSystem**, stopping all schedules fleet-wide | Account remembered in registry; interactive dialog prefills it; silent-upgrade password requirement documented | WXS validated; needs the human upgrade pass (§4) |
| 10 | P1 | Dashboard error/status line was never bound — run-refused and refresh errors were invisible | Bound (same pattern as Jobs) | App render/test suite |
| 11 | P1 | **Which objects failed and why was invisible in-app** (only in exported reports) | Log rows expand to show the per-object skip list; skip-Warnings now carry an explanatory banner message; failed counts shown in History grids | App tests; manual XAML review |
| 12 | P2 | Interrupted/corrupt clone bricked the job until manual folder deletion | Self-heal: `.git`-less remnants and corrupt `.git` are deleted and recloned | `GitWorkspaceTests.Prepare_RecoversFrom…` (both paths) |
| 13 | P2 | Concurrent app+service first start after an upgrade could fail one host's startup (migration race) | Write-lock transaction + re-check under the lock; 60 s init busy timeout | Code-reviewed; race window closed by construction |
| 14 | P2 | Case-only name collisions (case-sensitive DBs) crashed every run with an opaque duplicate-key error | Fail-fast with a message naming both objects and the workaround | `BuildPriorMap` guard |
| 15 | P2 | >100 MB generated file would wedge the branch behind a misleading push error | Skip-and-report at ~95 MB; specific push-failure explanations for GH001 (file size) and GH006 (protected branch) | Guard in `ApplyItemAsync` |
| 16 | P2 | A failed options/permissions/security-review catalog read failed the whole run after all objects had scripted | Fail-soft reported skips (same policy as docs/reference data) | Artifact generator refactor |
| 17 | P2 | Stale views while the service runs jobs; closing the app mid-run without warning; history cap silent; decline-vs-running message wrong; no background-thread crash logging; raw git stderr in banners | All addressed in the App batch (activation refresh, closing confirm, cap caption, outcome enum, AppDomain/TaskScheduler handlers, explained push reasons) | 166 App tests |
| 18 | P2 | Scheduling non-functional in real deployments (service manual-start LocalSystem, no health signal, no missed-run policy, no cross-process duplicate guard, unsafe stale-run cleanup) | Fixed earlier this cycle (`ef96254`): auto-start + heartbeat + health banners + catch-up + job run lock + lock-probing recovery | 477-test suite; scheduling test batch |

Also: CLI reported a hardcoded wrong version (fixed); unreferenced DacFx package removed; the
internal AI build-prompt file removed from the public repo.

## 2. Measured performance (real pipeline)

Methodology: `tools/Obsync.Benchmark` drives the production engine (metadata providers → hashing →
atomic writes → git commit) against generated workloads on a local SQL Server 2025 instance, with
fully isolated state (temp `obsync.db`, temp workspaces, a local bare repository as the remote — no
network noise). Workload mix: 60% procedures / 20% views / 20% functions (~1.5 KB realistic
scripts), plus SMO-path tables and six hostile objects (an encrypted procedure and path-illegal
names). Reports land in `artifacts/benchmarks/`.

Results (after-fix suite, commit `3384b5a`, 12 logical cores, local SQL Server 2025 Developer;
reports: `bench-2000/10000/50000-20260710-*.md`):

| Workload | Run | Status | Duration | Scripting | Git commit | Peak memory |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| 2,058 objects + 50 tables | full initial | Warning (1 skip) | 54.8 s | 43.5 s | 9.3 s | 111 MB |
| | warm no-change | Warning (1 skip) | 17.0 s | 16.4 s | — | 125 MB |
| | incremental, 100 changed | Warning (1 skip) | 21.4 s | 20.5 s | 0.6 s | 127 MB |
| | cancel 5 s into a full run | **Cancelled** | 7.2 s | — | — | 125 MB |
| 10,102 objects + 100 tables | full initial | Warning (1 skip) | 247 s | 210 s | 37 s | 166 MB |
| | warm no-change | Warning (1 skip) | 74 s | 73 s | — | 168 MB |
| | incremental, 500 changed | Warning (2 skips) | 209 s | 206 s | 2.3 s | 165 MB |
| 50,202 objects + 200 tables | full initial | Warning (**36 skips**) | 1,899 s (~32 min) | 1,413 s | 482 s | 347 MB |
| | next run (self-heal) | Warning (1 skip) | 151 s | 147 s | 2.6 s | 382 MB |
| | warm no-change | Warning (1 skip) | **35 s** | 27 s | — | 366 MB |
| | incremental, 1,000 changed | Warning (1 skip) | 61 s | 36 s | 18 s | 385 MB |

Reading the table:

- **Scripting scales roughly linearly** (≈45 obj/s per full run at 2k–10k, ≈35 obj/s at 50k under
  first-run SQL contention). The **first-run git commit grows superlinearly** (9 s → 37 s → 482 s
  for 2k → 10k → 50k *new* files) — a one-time cost; steady-state commits are 0.6–18 s.
- **Warm/incremental runs are the steady state** and scale sublinearly: a no-change pass over
  50k objects takes 35 s (~1,400 obj/s); syncing 1,000 changed objects takes ~1 minute.
- **Memory is bounded**: peak working set 111 → 166 → 385 MB across 2k → 10k → 50k, dominated by
  the run's change list (first runs are worst-case).
- **The 36 first-run skips at 50k are the failure policy working**: transient scripting failures
  under heavy first-run load were reported (run = Warning with reasons in the logs), nothing stale
  was committed, and the very next run re-detected and delivered all 35 recoverable objects
  automatically (`+35` on the self-heal row) — the 1 remaining permanent skip is the deliberately
  encrypted procedure. The one-per-run "1 skip" everywhere is that same encrypted procedure,
  correctly re-reported.
- These runs predate one committed refinement (watermarks are now withheld only for skips of
  previously-scripted objects), which makes post-skip runs slightly faster than measured here.

Findings from the harness beyond the numbers:

- The 6 hostile objects behave correctly at every size: the encrypted procedure is a reported skip
  (run = Warning, reason in logs), and path-illegal names (`p colon:name`, `p*star`, quotes,
  Unicode, dots) map to collision-safe file names with stable hash suffixes.
- Progress events are throttled (a handful per phase + one per 500 objects) — no UI flooding.
- Cancellation latency from token to run return: **~1.2 s** mid-scripting, recorded as `Cancelled`.
- Memory stays flat during scripting (bounded channel backpressure); peak working set grows with
  the run's *change list* (first runs are the worst case since everything is a change).

**Tested limits, stated precisely:** workloads up to the sizes in the table above on a 12-core
developer machine against a local SQL Server. Production capacity beyond the largest tested size is
extrapolation and is not claimed. The known architectural ceiling is git itself (~1M files per
repository).

## 3. Remaining backlog (open, prioritized)

| Sev | Item | Notes / mitigation in place |
| --- | --- | --- |
| P2 | Non-fast-forward + stranded local commit has no automated recovery (rebase or reset-and-rescript) | Never force-pushes (safe); the explained error tells the user; PR mode avoids it entirely. Manual recovery = delete the workspace folder (now self-healing on next run — the stranded commit's changes are re-detected thanks to the delivered gate) |
| P2 | Settings changes (proxy, alerts, retention, committer, production tags) are not audited | Trust & Audit follow-up; job/run/profile actions are audited |
| P2 | Old app binary opening a newer database schema has no guard | All migrations to date are additive; add a min-version key before the first schema-breaking migration |
| P2 | Run-history retention defaults to forever and SQLite never shrinks (`VACUUM`) | Retention is configurable in Settings; document growth for VLDB users or ship a default |
| P2 | Corrupt JSON in a single settings/job row throws on every page load with no self-heal | Never observed in practice; needs a tolerant read + quarantine |
| P2 | Support bundle omits per-run log entries (the most useful debug artifact) | Run reports (HTML/CSV/JSON) contain them; add to the bundle |
| P3 | Determinate progress bar (objects done / total) — live counts now exist but the bar is indeterminate | Progress messages update every 500 objects |
| P3 | Path mapper: reserved device names (`CON`, `PRN`…) and dot-bearing schema names can collide | Hostile-name suite covers the common cases; these two are exotic |
| P3 | GitHub secondary-rate-limit backoff ignores `Retry-After`; token checker cannot verify PR scope | Bounded retries; PR-scope failure is well-messaged at run time |
| P3 | GHES unsupported (validation/PR/links hardcode github.com) | Documented in README known limits |
| P3 | PR mode accumulates one local branch per run (never pruned) | Cosmetic ref growth in the private workspace |
| P3 | Incremental planner skip doesn't self-heal a file deleted directly on GitHub until the object next changes | Full runs self-heal; documented |
| P3 | `DpapiSecretProtector` registered but unused; accessibility gaps on Add/Edit dialogs; timeline cards mouse-only | Flagged for a product call |

## 4. Human-only verification before launch (go/no-go gate)

These cannot be verified from the repository and block the public release until executed once:

1. **Clean-machine MSI pass** — install 0.7.0+, confirm service auto-start (delayed), scheduler
   warning under LocalSystem, reinstall with the real account, schedule fires with the app closed
   and after a reboot, **upgrade from the previous MSI preserves the service account**, uninstall
   leaves no service/process.
2. **One live run per commit mode against a real GitHub repository** — direct commit, PR mode
   (including a PR-create failure retry, e.g. by revoking PR scope once), Local-commit-only, and
   Export. Verify a push failure (bad token) recovers on the next run.
3. **CI/release workflows** — first green run of `ci.yml` on a clean `windows-latest` runner and a
   draft release from `release.yml` (never yet executed on GitHub's runners).
4. **SignPath** — signing application (requires the public repo + releases); MSI ships unsigned
   until then, which is a launch-messaging decision.

## 5. Verification evidence (this pass)

- Full solution build: 0 warnings / 0 errors. Test suite: **477 passed, 0 failed** (43 integration,
  45 metadata, 92 engine, 108 shared, 166 app, 23 git) — including the new delivery-gate,
  clone-recovery, temp-file-exclusion, planner-regression, and named-lock tests.
- Benchmarks: suites in `artifacts/benchmarks/` (before/after the race fix; after-fix scaling
  suite). The before/after pair for the 2k workload demonstrates the race fix (2 of 4 runs failed
  before; 0 after) and the cancellation-status fix (`Failed` → `Cancelled`).
- Scenarios exercised end-to-end this pass: full run, no-change run, warm no-change run,
  incremental run after N ALTERs, one-failed-object-among-many (encrypted proc), hostile names,
  user cancellation mid-run, delivery failure at commit/push/PR with retry on the next run,
  deletion retention across failed delivery, partial/corrupt clone recovery.
- **Not executed here** (explicitly untested): real GitHub network paths (push/PR/rate limits/
  proxy), MSI install/upgrade/uninstall on a clean machine, service-executed scheduled run on an
  installed service, machine reboot, GHES, SQL auth (benchmarks used Windows auth), multi-database
  dynamic scope at scale.

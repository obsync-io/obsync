# Obsync production-readiness audit — release readiness

Audit date: 2026-07-15 · Audited: v0.8.2 (`002afdf`) · Result state: `main` with all audit fixes.

## Recommendation

**NO-GO for v0.8.2 as shipped — GO, conditionally, for a new release cut from the audited `main`.**

v0.8.2 contains a Critical defect that defeats the product's core promise: **no scheduled run ever
executes** (OBS-00 — every plain cron fire crashes before reaching the engine, silently, behind a
healthy heartbeat). It shipped in 0.8.0–0.8.2 and is live on at least one real deployment (this
machine's installed service). A second Critical (OBS-01) fails entire runs for any database
containing a user-defined type, table type, XML schema collection, or aggregate — i.e. a large
share of real enterprise databases.

Both are fixed on `main`, along with 11 further High-severity defects (wrongful mass deletions on
scope changes, permanent git workspace wedges, scheduler-killing cron, settings-wiping UI, silent
maintenance-window starvation) and ~30 Medium/Low issues — all regression-tested, with the full
suite green (544/544) and the end-to-end battery 73/73.

## Blocking before the next release (owner actions)

1. **Cut and ship a release with the audit fixes** — until then, every deployed 0.8.x installation
   has non-functioning schedules. Consider yanking/annotating the 0.8.x release notes.
2. **Clean-machine MSI pass** (install → configure → upgrade from 0.8.2 → verify data/credentials/
   service account survive): unchanged human-only gate, now doubly important because the upgrade
   carries the scheduler fix.
3. **One live GitHub run per commit mode** (Direct, PR, Local-only push-later, Export) with a real
   token: the PR-mode API path and live push semantics are the two things this environment
   fundamentally cannot exercise (all git behavior was verified against local remotes; the
   GitHub API only against its documented contract).
4. **Let CI run on the audit commits** (the render tests and the new suites on windows-latest).
5. **This machine**: upgrade the installed service and confirm the Dashboard shows scheduled runs
   actually executing (its event log should currently contain `Key trigger not found` errors).

## Non-blocking defects & known limitations (documented, safe)

- **OPEN-01** Non-fast-forward wedge: loud, actionable, no data destroyed (verified live), but
  recovery is manual; the delivered-at-commit design means deleting a wedged workspace can silently
  lose the stranded changeset. Highest-priority backlog item (self-healing reconciliation).
- **OPEN-02** PR mode re-reports unmerged content each run and accumulates head branches.
- **OPEN-03** Incremental mode lags inline table grant/extended-property sections (measured on SQL
  2025; the consolidated permissions file captures grants every run) — documented caveat.
- **OPEN-04** No enable/disable control in the UI (flag honored end-to-end; import-only today).
- **OPEN-05…09 + minor batch**: hourly+window Next-Run display flap, lock-probe races, local-export
  mirror accumulation, CLI production-guard scope, per-command git timeout, argv-length ceiling,
  GHES split-brain on hand-edited URLs, no single-instance guard, byte-vs-char size guard,
  uninstall data-policy doc gap. All Low-impact, none corrupts state; tracked in the bug report.

## Production-safety assessment (post-fix build)

- **Won't corrupt source-control history:** delivery-gated state advancement verified by real-engine
  tests and live push-failure/divergence scenarios; deletions are scope-aware with a mass-delete
  circuit breaker; determinism verified by identical tree hashes across independent runs.
- **Won't lose changes silently:** stranded commits re-push; failures and skips are loud
  (Warning/Failed with actionable reasons); the one silent-loss compound path (OPEN-01 + workspace
  deletion) is documented.
- **Safe for production SQL Servers:** read-only catalog access, parameterized/escaped SQL, capped
  scripting connections (≤8), lock-timeout bound, retry limited to transient classes; 50k-object
  benchmark shows modest flat memory and second-scale steady-state runs.
- **Safe with secrets:** no Critical/High security findings; Credential-Manager-only storage,
  argv-clean git auth, redacted persisted errors, secret-free exports (see SECURITY_REVIEW.md).
- **Honest UI:** the audit's truthfulness pass aligned labels, review screens, README claims, and
  error text with actual behavior.

## Required follow-up work (recommended order)

1. Ship the fix release + upgrade guidance (blocking list above).
2. OPEN-01 self-healing divergence recovery (design exists: regenerate-on-reset with delivery
   invalidation).
3. Job enable/disable UI + surfacing `Description` (or drop the field).
4. Live-environment matrix the owner can run quarterly: alerts (SMTP/webhook), proxy, SQL auth,
   Agent/linked-server estates, DPI/accessibility sweep.
5. VLDB (500k) benchmark on a representative estate; chunked state transactions if cross-host
   contention appears.
6. Backlog batch: per-command git timeout, commit-message via stdin, PR head-branch housekeeping,
   import-value clamping, `RemoteUrl` validation.

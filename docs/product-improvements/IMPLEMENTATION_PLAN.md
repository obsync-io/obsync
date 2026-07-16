# Obsync v0.9.0 — Implementation Plan

Companion to [IMPROVEMENT_ASSESSMENT.md](IMPROVEMENT_ASSESSMENT.md). Work is grouped into independent, separately committed tracks; each track builds cleanly and passes the full suite on its own so any track can be reverted without touching the others (rollback = `git revert` of that track's commit).

## Release grouping (v0.9.0)

| # | Track | Commit theme | Depends on |
|---|---|---|---|
| 1 | Scheduling trust | Overdue detection + next-run preview + service-identity clarity | — |
| 2 | Dashboard health | Needs-attention section + corrective actions | 1 (overdue state) |
| 3 | Job actions | Pause/resume, duplicate, export; overflow menu; clipping fix | 1 (badge states) |
| 4 | Server & repository health | Structured server info, persisted repo validation (migration V013), branch-existence check | — |
| 5 | Wizard clarity | Preset transparency, mode descriptions, folder preview/collision, branch validation, DB search, review preflight | 4 (branch check reuses validation) |
| 6 | History & diff | Trigger/PR/± split columns, truncation fixes, diff copy/wrap/find/filter | — |
| 7 | Diagnostics, logs, storage, About | New probes, logs panel + retention cap, sizes/open-folder, support info | — |
| 8 | Audit & alerts hardening | New audit events, alert retry + failure logging, permission-generator server scope + revoke script | — |
| 9 | Docs | Assessment, plan, implemented/deferred, drift & migration proposals, enterprise roadmap, design system | all |

## Acceptance criteria (per track)

1. **Scheduling trust** — A job whose `NextRunAt` is >5 minutes in the past while the scheduler reports healthy shows "Overdue" (amber, text + dot) on Dashboard/Jobs/Job Workspace; the wizard Schedule step shows the computed next run + time zone live and states the executing identity for Windows-integrated connections; the service banner appears only when the cadence needs the service.
2. **Dashboard** — With a failed job, a warning job, an overdue job, or a failed server test present, a "Needs attention" list appears with one row each and a working corrective action (Open job / Open Servers); with nothing wrong the section is absent (not an empty shell). Values refresh on run completion and navigation, as today.
3. **Job actions** — Pause immediately clears Next Run and shows a "Paused" badge; Resume restores scheduling within one service reconcile tick (existing behavior, now reachable); Duplicate creates "<name> (copy)" (disabled, unscheduled until saved through the wizard); Export writes the existing secret-free `.obsync-job.json`. All actions have tooltips + automation names; nothing clips at 960 px width.
4. **Server/repo health** — Server rows show edition/version + last-checked without any new page-load query; server name copyable. Repository validation persists status/timestamp (V013), shows a badge, and fails clearly when the configured default branch does not exist; verdict text is commit-mode-aware. No token material is ever persisted or displayed.
5. **Wizard** — Every preset can be expanded to list its exact object types (sourced from `ObjectSelectionPresets.Expand`, not a hand-written list); all four commit modes have one-line descriptions; destination step live-previews the effective folder and warns when another job targets the same repo+folder; invalid git branch names are rejected with the reason; databases list has search + select-all/clear; Review offers an optional preflight (SQL connect, repo access, branch exists, export path writable, credential presence) with per-check results that never block Save on warnings.
6. **History/diff** — Runs grid shows +/~/− split, trigger, and a PR link when present; badges never truncate at default width; one-click filter reset. Diff window: copy name/path/script, find-in-script (F3/Enter cycling), word-wrap toggle, Added/Modified/Deleted filter chips.
7. **Diagnostics/logs/storage/About** — New probes report Pass/Warning/Fail with timestamps; Copy results copies the full report; logs panel shows recent app+service entries filtered by severity/search, with Open logs folder; app log files are capped (31 days) like the service's; storage section shows sizes + free space with Open folder buttons; About shows app/engine/runtime/git/schema/service versions + Copy support info.
8. **Audit/alerts** — Each new audit action writes exactly one event with actor + entity; alert delivery retries once on transient failure and logs (never throws into) the run pipeline; permission generator can include server-level grants and produce a matching revoke script (both deterministic, tested byte-stable).

## Test strategy

- Every behavioral change lands with unit tests in the owning test project; view-model logic (overdue computation, attention aggregation, preflight, duplicate naming) is tested without WPF.
- Migration V013 gets a round-trip persistence test (Integration.Tests) like V002.
- Render smoke tests (existing `DesignSystemTests` harness) must keep passing for every touched view; new controls join the render pass.
- Redaction: a regression test feeds credential-shaped strings through the logs-panel parser and support-bundle writer and asserts absence.
- Full gate before release: `dotnet build` 0 warn/0 err, all unit/integration tests green, E2E battery (15 scenarios) green, screenshot pass of every changed screen via the UI harness at 100% and 150% scale.

## Rollback considerations

- Tracks are independent commits; revert per track.
- V013 is additive (nullable columns) — reverting the code leaves harmless columns; no destructive migration.
- No serialized-format changes (job JSON, inventory, hashes untouched — hash stability is an invariant guarded by byte-identity tests).
- No behavior change for the Windows Service other than honoring `Enabled` (already shipped) and the retention cap for app logs (service already capped).

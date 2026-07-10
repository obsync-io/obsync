# Changelog

All notable changes to Obsync. Versions are the MSI/installer baselines; dates are build dates.

## Unreleased

- **Reliable scheduling** — the launch-blocking scheduling gaps are closed end-to-end:
  - The MSI now registers the Obsync service **automatic (delayed) start** and starts it at install,
    so schedules survive reboots with no manual step (previously manual-start, i.e. never running).
  - **Scheduler health in the app** — the service heartbeats into the job database every 30s;
    Dashboard, Jobs, Job Workspace, and the job wizard warn whenever an enabled schedule exists that
    the service cannot execute (not installed, stopped, or running under an account that cannot see
    your jobs), and the Settings → Diagnostics service row states the exact reason.
  - **Missed-run catch-up** — a schedule that came due while the machine or service was off runs
    once at service startup (a *Catch Up* run in History), never per missed occurrence, and never
    when a later run already covered it.
  - **Cross-process duplicate prevention** — a per-job machine-wide run lock spans the app, service,
    and CLI; an occurrence that overlaps a still-active run is skipped with a logged reason.
  - **Honest crash recovery** — interrupted runs are failed with an explicit reason at the next app
    or service start; the old five-minute cleanup that could falsely fail a live long service run is
    gone (recovery now probes the run lock instead of guessing by age).
  - Startup runs are attributed as *Startup* (they previously logged as *Scheduled* and were
    wrongly subject to the maintenance window); disabling a job now clears its Next Run; "Next Run"
    is stamped immediately on save; next-run previews use the UTC offset in effect at the fire date
    (daylight-saving correctness).

## 0.6.0 — 2026-07-07

- **Database Timeline** — the History page gained a Timeline view: runs grouped by day with change
  totals, expandable per-run object lists, and click-through to the diff viewer preselected at the
  clicked object. Search now also matches database names and the triggering user.
- **Dependency Explorer** — new Dependencies tab in the Job Workspace: pick any synced object and
  see what depends on it (referencing modules, foreign-key tables, triggers) and what it uses,
  read live from the server. Click a dependency to drill into it.
- **Generated schema documentation** — each database now gets `docs/README.md` committed next to
  its scripts: an object index plus a data dictionary (column types, nullability, defaults, keys,
  and `MS_Description` descriptions). Regenerates only when the schema changes.
- **Security reviews** — each run writes `security/security-review.md` per database (guest access,
  grants to `public`, high-risk grants, `db_owner` members, orphaned users, `TRUSTWORTHY`) and
  `server/security-review.md` with the server pass (`sysadmin` members, `sa` enabled, password
  policy, high-risk server grants). Versioned, so posture drift shows up as commits.

> Upgrading note: each existing job's first run after 0.6.0 commits the two new generated files —
> that is the feature rollout, not drift.

## 0.5.0 — 2026-07-07

- **Update checks** — notify-only: a startup toast (at most daily, once per version) and a manual
  check in Settings → About. Only the GitHub releases endpoint is ever contacted.
- **Audit hardening** — every run (including scheduled service runs) now writes a run-outcome audit
  event with commit SHA and push result; the complete audit trail exports as CSV or JSON from
  Settings.
- **Repositories page redesign** — single full-width list with an Add/Edit dialog, plus a
  "Check token" action that re-validates the stored token's permissions against GitHub.

## 0.4.0 — 2026-07-06

- **VLDB performance** — incremental scripting (per-type `modify_date` watermarks; unchanged
  objects skip scripting entirely from the third run), parallel SMO scripting, and batched
  state/change/log persistence. UI hardening for huge runs (capped grids, streamed reports).
- **Enterprise installer** — branded dialogs, bundled MinGit (SHA-pinned), Windows Event Log
  source, service recovery settings, silent-install service account properties, production
  license & third-party notices.

## 0.3.0 — 2026-07-06

- **Server-level objects** — logins, server roles, credentials, linked servers, and SQL Agent
  jobs/operators/alerts scripted under `server/`, plus an always-on `server-configuration.sql`.
- **Drift alerting** — SMTP and webhook alerts on failure/warning/changes, fired after runs from
  both the app and the service.

## 0.2.0 — 2026-07-06

- **Dynamic database scope** — "All user databases", resolved live each run.
- **Reference data versioning** — selected lookup tables exported as deterministic INSERT scripts.
- **Script & diff viewer** — inspect any run's changes side-by-side or unified, offline, from the
  local clone.
- Daily-driver options: run-history retention, git committer identity, workspaces root override,
  job export/import, run-failure notifications.

## 0.1.0 — 2026-07-02

Initial installer baseline: SQL Server → GitHub schema sync (metadata fast-path + SMO), change
detection with per-object state, direct-commit and pull-request modes, export/local-only modes,
scheduling via the Windows service, audit log, token permission checker, least-privilege SQL
permission generator, proxy support, maintenance windows, `.obsyncignore`, run reports,
environment tags, and the WPF app with dashboard, jobs, history, and diagnostics.

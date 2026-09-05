# Changelog

All notable changes to Obsync. Versions are the MSI/installer baselines; dates are build dates.

## 0.10.1.1 - 2026-09-05

**Display and layout fixes**, found by running the app and reviewing every screen against seeded
data rather than empty states.

### Fixed

- **Blurry text.** The app shipped with no application manifest, so it never declared DPI
  awareness and Windows bitmap-stretched the whole window whenever the effective scaling differed
  from the one the process started at - every glyph resampled. It bit hardest on scaled laptop
  panels, mixed-DPI desks, and Windows 365 / RDP sessions, where the session DPI can change on
  reconnect. The app now declares Per-Monitor v2, so it re-renders at the new scale instead.
- **Warning banners lost words.** The scheduler-health message sat in a horizontal StackPanel,
  which measures its children with infinite width - so the text laid out to its MaxWidth and
  everything past the panel edge was clipped mid-sentence rather than wrapped. "Set the service's
  Log On account to your Windows account" rendered as "Set the service's Log On accour".
- **Job tables were unreadable.** The sum of the columns' minimum widths came to within 20px of the
  space available, so every column sat pinned at its minimum and the proportional widths never
  applied: job names showed as "Ad-hoc...", tags as "P..", while Last Run, Changes and Next Run sat
  empty. Rebalanced, and the default window is now 1400x860 (was 1180x760) - nine columns never fit
  1180. The 960px minimum window is unchanged and still verified by TableLayoutTests.
- History's Status column clipped the "No changes" badge.

### Changed

- The repository token field is labelled **Access token**, not "Fine-grained access token". Classic
  tokens have always worked - the permission check reads the repository's pull/push flags, which
  both token types carry - but the label read as a restriction and sent people to request an
  organization approval they did not need. The hint now spells out both, including the Resource
  owner setting that otherwise silently denies access to an organization's repositories.
- When a token authenticates but cannot see the repository, the error names the likely causes
  (approval still pending, Resource owner set to a personal account, classic token not authorized
  for SSO, or a typo) instead of only saying access failed. GitHub returns 404 rather than 403
  there, so "not found" and "not granted" cannot be told apart - hence naming them all.

## 0.10.1.0 — 2026-09-04

**Service-account and scheduling-identity fixes** found by a review of the installer's Service
Account screen and everything downstream of it. Every finding was verified before it was changed —
the installer ones by building probe MSIs and dumping the emitted tables — and several were refuted
on inspection and deliberately left alone. Suite 1,112 → 1,149.

**Upgrade notes.** This release changes behaviour you may notice:

- `OBSYNC_DATA_ROOT` must now be a **fully qualified** path. A relative value resolved against each
  host's working directory — the app's install folder versus `C:\Windows\System32` for the service —
  which silently put the two on different databases. A relative value is now ignored in favour of
  the default root.
- The installer refuses a blank password for an account that needs one. Blank is still correct for a
  gMSA (`DOMAIN\name$`), a virtual account (`NT SERVICE\...`) or a built-in one (`NT AUTHORITY\...`).
- The service's shutdown budget is 90s (was 30s), so a stop during a long run has time to cancel it
  cleanly instead of being killed mid-run.

### Fixed

- **The installer discarded the service account you chose on an upgrade.** The remembered account was
  searched straight into `SERVICE_ACCOUNT`, and `AppSearch` overwrites a property that is already
  set — so a silent upgrade passing `SERVICE_ACCOUNT` and `SERVICE_PASSWORD` reset the account to the
  old one and applied the **new password to it**, and the wizard's own answer was clobbered too.
- The upgrade default now reads the service's live logon account, so a change made with `sc.exe
  config` or the services.msc **Log On** tab is no longer reverted by a repair or upgrade.
- A blank service password reached `CreateService` as NULL, installing a service that could not log
  on. A click-through upgrade reproduced this every time, because the account is prefilled and the
  password box never is.
- **A stale heartbeat was reported as a wrong-account problem**, telling you to set the service's Log
  On account to the account it already used. It is now reported as a liveness problem.
- A heartbeat dated in the future read as fresh indefinitely, so a dead scheduler could report itself
  healthy until the clock caught up.
- The app no longer says "the service is not installed — reinstall Obsync" while a scheduler is
  demonstrably running and writing to the database.
- A service running under a different account with a shared data root reported plain "Scheduling
  active", suppressing every warning, while each run failed on credentials it could not read.
- "Start the service" was the only advice offered for a stopped service, which does not help when the
  cause is a bad password or a missing "Log on as a service" right — the two causes an install can
  leave behind. The Log On tab is now named.
- A transient reconcile failure suppressed the scheduler heartbeat, so a service doing its heaviest
  legitimate work reported itself as misconfigured.
- The startup heartbeat was written after crash recovery, per-run alerts and job scheduling, leaving
  a window where a healthy service looked broken.
- **A fatal service crash was recorded to the SCM as a clean stop**, so the installer's
  restart-on-failure recovery never fired.
- The in-flight run drain could consume the entire shutdown budget, leaving Quartz none — and an
  exhausted budget turned a deliberate `Stop-Service` into a recorded failure and an auto-restart.
- A read-only leftover lock file or a denied locks folder was reported as "another Obsync process is
  running this job", skipping every occurrence forever and never alerting. It is now recorded as a
  failure naming the folder.
- The run-lock liveness probe acquired the lock to test it, so it could make a concurrent run lose
  the race it was checking for.
- A bad data root killed the service before any logger existed — no log file, no event-log entry.
- A corrupt scheduler-heartbeat row threw through the dashboard, job list, job detail and the Create
  Job wizard, which awaits it before the window is shown.

### Added

- Audit events for run-history pruning, crash recovery of an interrupted run, and service start/stop.
- `tests/Obsync.Packaging.Tests` — structural regression tests over the installer source.

## 0.10.0 — 2026-09-03

**Correctness, security and coverage fixes** from a full review of the shipped 0.9.0 tree. Every
finding was reproduced before it was changed, and several turned out to be wrong as reported — those
are corrected in `docs/quality-audit/` rather than repeated. Suite 726 → 1,112.

**Upgrade notes.** This release changes behaviour you may notice:

- Branch names beginning with `-` are now rejected. They reach git as bare positional arguments, so
  a name like `--upload-pack=…` was read as an option. If an existing job uses one, editing it will
  now ask you to rename the branch.
- A credential embedded in a repository profile's `RemoteUrl` is stripped before the URL is given to
  git. Obsync authenticates with an injected header, so nothing is lost — unless you had put a token
  there *instead of* configuring one, in which case configure the token under Repositories.
- The `ssh` and `ext` git transports are refused. Obsync's remotes are HTTPS; `ext::` runs an
  arbitrary command, and either could be reached from a rewritten URL in machine configuration.
- Duplicating a job now refuses if the source job could not run as it stands, naming the reason.
- A database containing object types Obsync does not script now finishes with a **Warning** listing
  them, where it previously reported complete success. Nothing about what is captured has changed —
  only whether the gap is reported.

### Security

- **The GitHub token is no longer sent to a host substituted by machine configuration.** The auth
  header was set unscoped, so a `url.<host>.insteadOf` entry in the system or global gitconfig
  silently rewrote the remote and the token was delivered to the rewritten host on the first
  request — with the user seeing only "repository not found". No attacker is required: an internal
  mirror or proxy distributed by ordinary configuration management is enough. The header is now
  scoped to the remote's own URL prefix.
- **Machine git configuration can no longer run programs during a sync.** Shipping MinGit reads as
  isolation but is not — the bundled system config explicitly includes Git for Windows' own — so
  `core.hooksPath`, `core.fsmonitor`, `filter.*`, `core.sshCommand` and `ext::` URLs each executed
  on Obsync's own commands. Hooks, the filesystem monitor and the credential helper are now disabled
  for every invocation, and the transport allow-list refuses `ext` and `ssh` by name. Settings that
  matter to a site — `core.autocrlf`, a corporate CA bundle, a proxy — are deliberately still read.
- **A hung git command can no longer wedge a job indefinitely.** Nothing bounded a git invocation:
  `GIT_TERMINAL_PROMPT=0` gates only the terminal fallback, and a credential helper inherited from
  the environment blocked forever holding both the job and repository locks, with the run still
  showing as in progress. An expired token was enough to trigger it. Askpass helpers are now
  disabled, commits are never signed, and every command is bounded (10 minutes network, 2 local).
- **Credentials are scrubbed from every persisted failure message**, not only the one URL shape the
  previous rule matched. It required a `user:password@` pair, so it missed `https://<token>@host` —
  the form GitHub's own documentation produces — and `http://user:@host`, which Obsync itself built
  when a proxy had a username and no stored password. Raw exception text reaching the run history,
  run reports and alert payloads is scrubbed too, and the support bundle no longer carries a
  credential embedded in a repository URL.
- The unused `DpapiSecretProtector` and `ISecretProtector` are removed. Nothing referenced them.

### Scheduling

- **A schedule its maintenance window can never admit is refused instead of silently never running.**
  The existing guard covered only Daily and Weekly; Hourly and Cron were accepted and then skipped
  on every occurrence, with a healthy trigger and an advancing "Next run". Job import applied no
  window check at all, at any cadence. A window that opens and closes at the same time is refused too.
- **The hourly "next run" no longer reports a time the job cannot run at.** The trigger's step
  restarts at midnight, so for any interval that does not divide 24 the preview drifted off the real
  schedule — landing on a time that was not an occurrence at all, or a full day late. It also fed the
  overdue badge and the missed-run catch-up, so it could raise a false alarm or fire an unattended
  run nothing had missed.
- **"Every N hours" is described honestly.** For an interval that does not divide 24 it is not a
  period: "every 23 hours" runs twice a day, 23 hours apart and then one hour apart. The schedule now
  names the times it actually runs, and the wizard shows them as you type.
- **A schedule that will never run says so** — as "Never runs" in the Jobs and Dashboard tables, the
  job header and the attention list, instead of a confident date or a blank cell.
- **A paused Cron job shows its next run again on resume**, and a completed run no longer overwrites
  the scheduler's next-run time with the one that just elapsed.
- Daylight-saving: an occurrence falling in the hour that spring-forward removes now resolves to the
  first time that exists, rather than to a time inside a gap the clock skips.

### Scale and reliability

- **The SMO prefetch memory ceiling now measures what prefetch loads.** It compared the filtered
  object count against a limit describing the cost of loading every table in the database, so a
  schema filter selecting a few hundred tables out of hundreds of thousands passed the ceiling and
  then prefetched them all, on every worker connection. The sequential path had no ceiling at all.
- **SMO connections are returned to the pool.** The primary connection for each database, and the
  one for the server-level pass, were never disconnected — only the slice workers were — leaving a
  session open on the SQL Server for each.
- **A transient failure part-way through reading a database no longer discards the whole run.** It
  escaped to the top, so nothing was committed and no state advanced, including for databases that
  had already finished. It is now contained to its own database, whose deletions are suspended and
  whose watermarks are held back so the next run re-scans it in full.
- **Objects of types Obsync cannot script are reported.** They were unreachable at every stage, so a
  database using Service Broker, certificates or external tables produced a run reporting zero skips
  and complete success. Their absence is now counted and listed. The set of types Obsync scripts is
  unchanged.


## 0.9.0 — 2026-07-16

**Production-trust and clarity release.** A full product-improvement review (assessment, plan, and
evidence in `docs/product-improvements/`) drove this release: the app now tells you when something
needs attention instead of leaving you to infer it, without changing the calm six-section design.

- **Overdue schedules are visible** — a scheduled job whose next run silently passed now shows an
  amber "Overdue" (with the scheduled time and a corrective hint) on the Dashboard, Jobs, and Job
  Workspace instead of a stale timestamp.
- **Dashboard "Needs attention" panel** — failed jobs (with the error), warning runs, overdue
  schedules, and servers failing their connection test, each with a one-click Open action. Absent
  when everything is healthy.
- **Pause, resume, duplicate, export** — pause/resume a job's schedule from the list (paused jobs
  show a neutral "Paused" badge and clear their next run); duplicate a job as a paused copy; export
  its secret-free configuration — all from a new per-row menu.
- **Server & repository health that persists** — servers show their SQL Server edition/version and
  last-checked time (captured during tests, no extra load) plus a copy-name button; repository
  validation results (including whether the configured branch actually exists) are now stored and
  shown as a status badge; a read-only token now says exactly which commit modes it breaks.
- **Wizard clarity** — every preset can show its exact included object types (sourced from the real
  preset expansion); all four commit modes have inline descriptions; the destination step
  live-previews the resolved folder and warns when another job writes to the same place; branch
  names are validated against git's rules; the databases list gained search + select-all; the
  schedule step shows the computed next run with your time zone and documents the overlap policy
  (a run still in progress means the next fire is skipped); the Review step can run optional
  preflight checks (SQL, repository + branch, export path, credentials, folder collisions) that
  never block saving.
- **History & diff** — the runs grid now shows added/modified/deleted counts separately, the run's
  trigger (manual/scheduled/startup/catch-up), and a link to the pull request it opened; filters get
  a one-click reset. The diff viewer gains change-type filters, find-in-script (F3/Shift+F3), a
  word-wrap toggle, and copy actions for object name, path, and script.
- **Diagnostics & logs** — new checks for Windows Credential Manager, data/workspace folder
  writability, and state-database integrity, each timestamped, with copy-all; a Recent-logs panel
  (severity filter, search, open-folder) inside Settings; the app's log files are now capped at 31
  days (previously unbounded).
- **Storage & About** — workspace/state-database/log sizes and free disk with open-folder buttons;
  About now shows app, engine, service, .NET, Git, and Windows versions plus the database schema
  version, with one-click "Copy support info" and project/issue/documentation links.
- **Security & audit** — the least-privilege permission generator can include server-level grants
  and now also produces a matching revoke script; the audit trail additionally records settings
  changes, credential updates, permission-script generation, audit/support-bundle exports, update
  checks, run cancellations, and job pause/resume/duplicate/export.
- **Alert reliability** — email and webhook alerts retry once on transient delivery failure.
- **Polish** — column truncation and clipped row actions fixed on the Dashboard, Jobs, and History
  tables at small window sizes (now guarded by automated layout probes).

Also new in the repository: proposals for a future drift-detection/schema-compare module and a
migration-assessment module, an enterprise roadmap, and a design-system reference
(`docs/product-improvements/`, `docs/design-system/`).

## 0.8.3 — 2026-07-15

**Critical fix — upgrade required.** In 0.8.0–0.8.2 the Windows Service crashed on every plain
scheduled (cron) fire before the run started, so **scheduled syncs never executed** — while the
scheduler heartbeat stayed healthy and "Next Run" kept advancing. Startup and catch-up runs were
unaffected, which masked the breakage. This release fixes it; after upgrading, your schedules run
at their advertised times. (If you want confirmation your install was affected, the Windows
Application event log will contain `Key trigger not found` errors from source "Obsync".)

This release also carries the fixes from a full production-readiness audit
(`docs/quality-audit/` in the repository has the complete reports):

- **Databases with user-defined types no longer fail** — any database containing an alias type,
  table type, XML schema collection, CLR type, or aggregate previously failed its entire run.
- **Scope changes never delete your history** — narrowing a schema filter or deselecting object
  types now retains the committed files of out-of-scope objects instead of committing their
  deletion; and if most tracked objects vanish at once on an unattended run (the signature of lost
  SQL metadata visibility, not a real mass drop), deletions are suspended with a Warning — a manual
  Run Now confirms and applies them.
- **CLR modules are reported, not silently omitted** — they now surface as explicit skips like
  encrypted modules (previously they were invisible and could be misreported as deletions).
- **Git workspaces self-heal more** — a stale `index.lock` left by a crash or cancel no longer
  wedges every later run; editing a repository's owner/name now re-points the existing clone
  (pushes previously kept going to the old repository); leftover files from an interrupted run are
  cleaned instead of leaking into the next commit or blocking checkout.
- **Scheduler resilience** — one invalid or never-firing cron expression no longer takes down the
  whole scheduler; the wizard now validates cron expressions (including "will it ever fire") and
  rejects Daily/Weekly times that can never intersect an enabled maintenance window (previously
  such jobs silently never ran); saving a job no longer triggers an immediate unattended run when
  "run on service startup" is enabled; disabled jobs can no longer slip through a scheduling race.
- **Settings honesty** — switching away from the app no longer wipes unsaved Settings input (or
  clears typed passwords); disabling email alerts or the proxy keeps the stored password as the
  label promises; a manual proxy now requires a valid URL instead of silently connecting direct;
  "Trust server certificate" defaults off for new servers, with a caution.
- **Correctness hardening** — case-only object renames no longer brick a job; reference-data
  values containing line breaks are exported corruption-proof; zip exports are built atomically
  (a failure can no longer destroy the previous good export); a disk-full at the end of a run can
  no longer leave a phantom "Running" row; pull-request mode no longer pushes zero-diff branches
  in a loop when the base branch already has the content.
- **Smaller fixes** — GitHub tokens are trimmed (pasted trailing newlines broke PR mode only);
  HTTP 401/403/404 from git are no longer retried as transient; persisted git errors redact any
  embedded proxy credentials; the Dashboard "Latest Commit" no longer blanks when the newest run
  had no commit; duplicate job names are rejected; destination paths are validated; the CLI
  returns exit code 3 for Warning runs; crash-recovered failed runs now send the configured
  alerts; the review step shows every option it previously omitted.
- **New:** the `OBSYNC_DATA_ROOT` environment variable relocates the data root (state database,
  workspaces, logs, locks) for test harnesses and managed deployments.

## 0.8.2 — 2026-07-10

> [!WARNING]
> **Scheduled runs never executed in this release.** Every cron fire crashed before reaching the
> engine, silently — the service heartbeat stayed healthy and "Next Run" kept advancing. Fixed in
> 0.8.3; upgrade if you are on this version. Manual runs were unaffected.

- **Modernized installer wizard** — Segoe UI dialog typography, a cleaner brand side panel and
  banner, product-specific page copy with sentence-cased headers, and a more readable Service
  Account page. Purely presentational; install behavior is unchanged.

## 0.8.1 — 2026-07-10

> [!WARNING]
> **Scheduled runs never executed in this release.** Every cron fire crashed before reaching the
> engine, silently — the service heartbeat stayed healthy and "Next Run" kept advancing. Fixed in
> 0.8.3; upgrade if you are on this version. Manual runs were unaffected.

- **Tabbed Settings** — the Settings page is reorganized into categorized tabs (General, Alerts,
  Network & storage, Security & audit, Diagnostics, About) instead of one long scroll, with a
  readable card width.
- **Modern scrollbars** — a slim, rounded scrollbar style replaces the stock Windows chrome
  throughout the app.
- **Collapsible sidebar** — the navigation rail collapses to a compact icon-only strip via a toggle
  at its foot; tooltips carry the labels and the preference persists across sessions.

## 0.8.0 — 2026-07-10

> [!WARNING]
> **Scheduled runs never executed in this release.** Every cron fire crashed before reaching the
> engine, silently — the service heartbeat stayed healthy and "Next Run" kept advancing. Fixed in
> 0.8.3; upgrade if you are on this version. Manual runs were unaffected.

- **Nothing is silently lost** — tracked object state (hashes, deletions, incremental watermarks)
  now advances only after the run's changes are durably delivered: a commit in direct/local modes,
  an opened pull request in PR mode. A failed commit, push, or PR no longer made later runs report
  "no changes" while the repository silently missed the work.
- **Cancel running syncs** — a Cancel button in the Job Workspace and Ctrl+C in the CLI stop a run
  cleanly within seconds; the run is recorded as Cancelled (not a scary failure) and everything it
  had in flight is re-detected next run.
- **Reliability under parallel scripting** — fixed an intermittent whole-run failure caused by a
  directory-creation race between workers; file writes are now atomic (an interrupted run can never
  leave a truncated script for a later commit to pick up); interrupted or corrupt git workspaces
  self-heal by recloning.
- **Credential hardening** — the GitHub token and proxy credentials no longer appear on the git
  child-process command line (which Windows process auditing and EDR record); they travel in
  environment variables instead.
- **Safer multi-job setups** — jobs sharing one destination repository now run back-to-back on a
  per-repository lock instead of interleaving git operations in one clone.
- **Failure-policy fixes** — incremental runs no longer delete committed files of
  `.obsyncignore`-matched objects; watermarks no longer advance past transiently skipped objects; a
  failed options/permissions/security-review read is a reported skip instead of a run failure;
  generated files over ~95 MB are skipped (GitHub rejects them at 100 MB) instead of wedging the
  branch; case-only name collisions fail with an actionable message.
- **Clearer errors and truthful UI** — push failures show the explained, actionable reason (raw git
  output stays in technical details, incl. protected-branch and file-too-large guidance);
  skip-warnings explain themselves; per-object skip reasons are visible in the run's Logs tab;
  Dashboard shows action errors; History shows skipped counts and discloses its 100-run window;
  views refresh when the window activates so service-run results appear; closing the app mid-run
  asks first; live "N objects processed" progress during scripting.
- **Upgrades keep the service account** — the MSI remembers the configured service logon account,
  so a major upgrade no longer silently resets a working service to Local System.
- Benchmarked: a repeatable real-pipeline benchmark harness ships in `tools/Obsync.Benchmark`;
  measured results and tested limits are documented in `docs/LAUNCH-READINESS.md`.

## 0.7.0 — 2026-07-10

> [!NOTE]
> The heading below overstates what landed. The same commit introduced the defect fixed in 0.8.3:
> every plain cron fire threw before reaching the engine, so no scheduled run executed in 0.8.0
> through 0.8.2. The deployment and health work listed here did ship and is accurate.

- **Reliable scheduling** — the deployment and visibility gaps around scheduling are closed
  (but see the note above — execution itself was broken until 0.8.3):
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

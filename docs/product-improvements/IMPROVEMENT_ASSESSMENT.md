# Obsync — Product Improvement Assessment

**Date:** 2026-07-16 · **Assessed against:** `main` @ `a917680` (v0.8.3 + performance pass) · **Method:** full repository inspection (6 parallel code audits over every UI surface, the engine, scheduler, service, data layer, tests, docs, and packaging) plus baseline screenshots of the running shell.

This document assesses every improvement requested in the product-improvement brief. Priorities:

- **P0** — required for production trust or correctness
- **P1** — high-value usability or enterprise improvement
- **P2** — valuable but not required for the next release
- **P3** — future capability

Verdicts: **Implement now** (this release), **Planned** (documented, next releases), **Rejected** (with reason), **Already present** (no work needed beyond noted deltas).

---

## Snapshot of what already exists (repo evidence)

The brief assumes several gaps that the codebase has already closed. Highlights found during inspection:

| Area | Current state |
|---|---|
| Run statuses | `RunStatus` already has `Warning` (partial success), `NoChanges`, `Cancelled`; the engine escalates skips/partial failures to Warning (`SyncEngine.EscalateForSkips`) — a partial run is **never** classified as full success |
| Scheduler health | `SchedulerHealthService` combines SCM status + service logon account + a 30-second DB heartbeat into 4 states (Healthy / NotInstalled / NotRunning / NotExecutingYourJobs), surfaced as banners on Dashboard, Jobs, Job Workspace, and the wizard's Schedule step |
| Missed runs | Startup catch-up policy (`MissedRunPolicy`), one catch-up per job, DST-aware next-run math, maintenance windows with midnight wrap |
| Overlap | Quartz `[DisallowConcurrentExecution]` + cross-process per-job file lock + per-repository lock (scheduled runs wait ≤30 min, then skip) — safe default: **skip if already running** |
| Diagnostics | Git CLI, disk space, service health, proxy, per-server SQL probe, per-repository token/access checks; support bundle (verified secret-free) |
| Alerts | SMTP + generic webhook, proxied, 15 s timeout, Failed/Warning/Changes triggers, scheduled-only gate, test button |
| Audit | Append-only audit log (server/repo/job CRUD, run start/complete/fail), CSV + JSON export of the full trail |
| Reports | Run reports in HTML/CSV/JSON; job config export/import (secret-free) |
| Dependency analysis | One-level Uses / Used-by explorer per job (read-only, live catalog) — already shipped |
| History/diff | Runs + timeline views, split/unified diff (DiffPlex, virtualized, off-thread), per-file git version history (`git log --follow`), open-on-GitHub |
| Tests | 561 tests + 15-scenario runtime E2E battery; render smoke tests for every view |

---

## 1. Dashboard improvements — **P1 · Implement now (scoped)**

- **Current:** 5 metric cards (Total Jobs, Successful Last Runs, Failed Jobs, Objects Tracked, Latest Commit), scheduler warning banner, getting-started empty state, jobs table. Refresh on navigation/activation/run events (`DashboardViewModel.cs`).
- **Gaps (evidence):** no attention-required section; Failed Jobs count ignores `Warning` runs; no overdue detection anywhere (`NextRunAt` renders as a plain timestamp); no server/repository reachability surfacing; "Changes" column header truncates.
- **User problem:** the dashboard cannot answer "is any action required?" without reading the whole table.
- **Decision:** add a compact **Needs attention** section (failed / warning / overdue jobs, failed server tests, scheduler issue) with direct corrective actions; count Warning runs as needing attention; add overdue detection (shared with Jobs page); keep the existing 5 cards (authoritative, cheap) — **do not** add the full 14-metric list, which would create clutter for a single-user desktop tool.
- Effort M · Security none · Perf negligible (reuses loaded data).

## 2. Server inventory — **P1 · Implement now (scoped)**

- **Current:** Name, instance, auth mode, 3-state test badge; `LastTestStatus/At/Detail` persisted; probe captures version/edition/product level but buries them in one string; database counts not shown; no copy affordance.
- **Decision:** show **Version/Edition** (structured, from the existing probe) and **Last checked**; add copyable server name; keep the page a list, not a monitoring console. **Desktop vs service identity:** a true "test as service account" is not possible from the desktop process without the service's credentials; the honest, cheap version is (a) the existing heartbeat-account health check, and (b) an explicit note in the wizard when a Windows-integrated job is scheduled (see §5). Live per-server "service connection works" probing is **Planned** (requires a service-side probe RPC — see DEFERRED).
- Effort S–M · Perf: no new queries on page load (data captured at test time).

## 3. Repository health — **P1 · Implement now (scoped)**

- **Current:** Name, owner/repo, default branch, actions. Token check (`CheckRepositoryAccessAsync`: token validity, repo found, pull/push perms) is **transient — nothing persisted** (asymmetric with Servers). `GetBranchesAsync` exists but has zero callers; branch existence never verified; validation ignores commit mode.
- **Decision:** persist validation outcome (`LastValidationStatus/At/Detail`, migration) and show a status badge + last-validated; **verify the configured default branch exists** during validation (wire the existing `GetBranchesAsync`); make the validation verdict **mode-aware in messaging** (read-only token is fine for Local Commit Only, fatal for Direct Commit; Export Only jobs never validate GitHub — already true). Full per-mode operation testing (PR-permission pre-flight) is **Planned**: the GitHub API cannot report PR-write permission without attempting a PR; we document the 403-at-create behavior instead of faking a check.
- Effort M · Security: no token metadata exposed (status + timestamp only).

## 4. Job list — **P1 · Implement now**

- **Current:** columns Name/Source/Destination/Tags/Status/Last Run/Changes/Next Run + Open/Run/Edit/Delete. `SyncJob.Enabled` exists and the scheduler honors it within one reconcile tick, but there is **no pause/resume UI, no Paused badge, no duplicate, no per-row export**; the Actions column clips at default width.
- **Decision:** add **Pause/Resume** (drives `Enabled`; Paused badge in Status), **Duplicate**, and **Export configuration** (porter already exists) — Duplicate/Export/Delete move into an overflow "⋯" menu with accessible names so the row stays uncluttered; show **Overdue** state on Next Run; fix column clipping.
- Effort M · Risk low (model + scheduler reaction already exist and are tested).

## 5. Job wizard — **P1 · Implement now (clarity only, no new complexity)**

Per-step assessment (wizard is strong; changes are additive clarity):

- **Source:** database search + Select all/Clear (lists can be large); note explaining the scheduled-run identity for Windows-integrated connections. "Sync all user databases" semantics (live resolution, exclusions, offline-DB skip → Warning) already implemented — add the one-line explanation the brief asks for. — *Implement now.*
- **Objects:** presets exist but their contents are invisible. Add **"View included object types"** (expander fed by the real `ObjectSelectionPresets.Expand`, so it can never drift from behavior). Artifact toggles already exist with path-naming labels. Grouped Custom selector: **Planned** (cosmetic; flat list is alphabetical and functional). Schema-filter tokenization: **Planned** (free text works; validation against live schemas requires a connection round-trip mid-wizard). — *Preset transparency: Implement now.*
- **Destination:** add inline one-line descriptions for **all four** commit modes (only PR/Export have them today); **live effective-folder preview** (`EffectiveDestinationFolder` already computed for Review — surface it on step 3); **warn when another job writes to the same repository+folder**; **branch-name validation** (git ref-format rules). Path-traversal/invalid-char/rooted-path validation already exists. — *Implement now.*
- **Schedule:** the readiness banner exists; add **live next-run preview with time zone** (`ProvisionalNextRun` already computes it — surface it), and only show the service banner when the chosen cadence actually needs the service (`NeedsScheduler` exists, unused for gating). Overlap policy: skip-if-running is implemented engine-wide; **document it in the UI caption** rather than adding a policy picker (queue/cancel options are speculative — no user demand, real complexity). — *Implement now.*
- **Review:** solid summary exists. **Preflight checks before save** (SQL connect, repo access, branch exists, destination path, credential presence): *Implement now* as an optional "Run preflight" action on the Review step — non-blocking (warnings don't prevent Save; missing basics already block via validation).
- Effort M–L across steps · all changes reuse existing services.

## 6. History — **P1 · Implement now (display-level only)**

- **Current:** stores trigger, identity, duration, PR URL/number — but shows none of them in the Runs grid; single `ChangeCount` number; badges/columns truncate at default width.
- **Decision:** show the **+added / ~modified / −deleted** split in the grid (same tokens the Timeline already uses); add **Trigger** (Manual/Scheduled/Startup/Catch-up); surface the **PR link** on PR runs; fix truncation; add a **filter-reset** action. Status terminology already correct (`Warning` = "completed with warnings"; distinct `NoChanges`, `Cancelled`). Persisted failure-stage and separate warning-vs-skip counts: **Planned** (schema change, low display value vs. the logs tab that already explains each skip).
- Effort S–M.

## 7. Changed-object & diff viewer — **P1 · Implement now (scoped)**

- **Current:** split/unified diff, version history, virtualization, GitHub links; missing find-in-script, word wrap, change-type/object-type filters, copy actions.
- **Decision:** add **change-type filter chips** (Added/Modified/Deleted), **copy object name / path / script**, **word-wrap toggle**, and **find within script**. Full-screen: window is resizable/maximizable — *Rejected* (OS maximize suffices). "Restore to database": **Rejected permanently** (read-only product); copy/export of historical versions already possible via the version rail + copy-script.
- Effort M.

## 8. Global search — **P2 · Planned**

Indexed local metadata exists (SQLite object states, runs, audit), so this is feasible without touching SQL Servers — but it is differentiation, not trust. Deferring keeps this release focused on production correctness. (See DEFERRED_IMPROVEMENTS.)

## 9. Command palette — **P2 · Planned**

Brief itself classifies it below production correctness. Deferred.

## 10. Background activity center — **P2 · Planned (partial already present)**

Per-job Running badges, indeterminate progress + Cancel in the Job Workspace, and busy states on every long action already exist. A global operations panel is additive polish; deferred with design notes.

## 11. Notification center — **P2 · Planned (partial already present)**

Toasts (failure/warning, missed-failure summary at launch, update available) + email/webhook alerts + the new dashboard attention section cover "surface items requiring attention." A persistent unread-state center is deferred to avoid duplicating History.

## 12. Alert integrations — **P1 (hardening) / P2 (vendors)**

- **Current:** generic webhook (proxied, 15 s timeout, secret-free payload) + SMTP. No retry, no duplicate suppression, no webhook auth header.
- **Decision:** *Implement now:* one bounded retry for transient alert-delivery failures and failure logging (cheap, honest). Vendor presets (Teams/Slack/PagerDuty/ServiceNow): **Planned** — generic webhook remains the foundation, per the brief.

## 13. Diagnostics — **P0 · Implement now**

- **Current:** 6 check families, statuses Pass/Warning/Fail, run-all, support bundle (secret-free, verified). Missing: Credential Manager probe, data-folder/state-DB/workspace writability, per-check timestamps, copy results.
- **Decision:** add **Credential Manager round-trip probe**, **data folder / workspaces writability**, **state database** check (size + integrity pragma), **per-check timestamps**, and **Copy results**. Update-endpoint reachability is exercised by the existing "Check for updates" — not duplicated as a diagnostic.
- Effort M · Security: probes never log secret values (write/delete a sentinel credential).

## 14. Integrated logs viewer — **P0 (scoped) · Implement now**

- **Current:** per-run logs already have an in-app viewer (Job Workspace → Logs, friendly + expandable technical detail). **File logs have no viewer, no "open folder" action — and the app log sink has no retention cap (unbounded growth).**
- **Decision:** *Implement now:* fix the unbounded app-log retention (cap like the service's 31 days); add a **Logs panel on the Diagnostics tab** — recent app + service log entries, severity filter, search, copy, Open logs folder. Redaction: Serilog messages never receive secrets by construction (verified call sites); a regression test asserts the viewer/export path contains no credential material. Clear-logs with policy: **Planned** (retention cap makes it non-urgent).
- Effort M.

## 15. Settings — **P1 · Implement now (scoped)**

- **Current:** per-section Save buttons (consistent), inline status lines, validation on every section; retention options exactly as the brief requests (30/90/180/365/forever); production-tag guard implemented and explained in the UI.
- **Decision:** per-section Save is **kept** — it is the pattern that already minimizes accidental loss (small blast radius, explicit statuses); converting to page-level save or autosave would be churn without evidence of a problem. *Implement now:* show **approximate history size** next to retention (brief request, cheap), and the storage health items in §16. "Protected environment tags" rename: **Rejected** — "Production tags" is clearer and already consistently used in UI + docs. Git identity options already cover app-default/custom; "use system git config" **Rejected** (service and app would silently diverge).

## 16. Network & storage — **P1 · Implement now (scoped)**

- **Current:** paths shown read-only; workspaces relocatable; proxy None/System/Manual applied to GitHub API, git CLI, update checks, webhooks — **not SMTP** (System.Net.Mail limitation).
- **Decision:** *Implement now:* show **sizes** (workspaces, state DB, logs) + free disk, **Open folder** buttons, and a UI note that SMTP does not use the proxy (honesty requirement). Clean-unused-workspaces: **Planned** (needs job-to-workspace liveness accounting to be safe).

## 17. Security & audit — **P1 · Implement now (scoped)**

- **Current:** least-privilege generator (idempotent CREATE USER + 3 grants, deterministic, copy/save); `includeServerObjects` overload **exists but is not reachable from the UI**; no revoke script; audit covers CRUD + run lifecycle but misses settings/credential/export/update events.
- **Decision:** *Implement now:* wire the existing server-level-permissions option into the UI; add a **revoke script** generator (mirror of the grants — same builder, inverse statements); add missing audit events (**SettingsChanged, CredentialsUpdated, PermissionScriptGenerated, AuditLogExported, SupportBundleExported, UpdateChecked, RunCancelled, JobPaused/Resumed/Duplicated**). Validation-of-existing-access: **Planned** (requires querying the target login's effective permissions — read-only but a new query surface).

## 18. About & support — **P1 · Implement now**

- **Current:** app + engine versions only; no runtime/git/service/schema versions; no copyable support block; static reassurance texts (both verified accurate in the 2026-07-15 security review).
- **Decision:** add runtime, git, Windows, state-schema, and service versions (all already gathered for the support bundle — reuse `SupportBundleWriter`'s source), project/docs/issue links, and a **Copy support info** button.
- Effort S.

## 19. Accessibility & desktop polish — **P0 (defects) · Implement now**

- **Current:** tooltips + automation names on icon actions, Esc/Enter on dialogs, min window size, status = color + text + dot (never color alone). Baseline screenshots show real defects: truncated "Changes" header, clipped Actions column on Jobs, truncated status badges on History at default width.
- **Decision:** fix the truncation/clipping defects; audit tab order on the two dialogs; keep the existing text+icon status convention. Full DPI matrix (100/125/150/200 %) re-verified via render probes at scale factors; physical-monitor verification remains a human gate (documented).

## 20. Dark mode — **P2 · Planned**

Brief's own precondition list (production safety, diagnostics, accessibility first) is exactly this release's scope. The palette is centralized in `Themes/Colors.xaml`, which makes a future theme swap tractable; a partial dark mode is worse than none. Deferred with design notes.

## 21. Design system — **P1 (documentation) · Implement now**

The system exists in XAML (`Themes/{Colors,Typography,Icons,Controls}.xaml` + App.xaml badge templates, enforced by `DesignSystemTests`); it has never been written down. **Create `docs/design-system/DESIGN_SYSTEM.md`** documenting tokens, styles, and usage rules — documentation of what is, not a redesign.

## 22. Object explorer — **P2/P3 · Planned**

Local tracked metadata could power it without live server load, but it is differentiation. Deferred; the Dependencies tab already covers the highest-value slice (impact analysis).

## 23. Dependency analysis — **Already present (v1) / P2 for depth**

One-level Uses/Used-by explorer shipped. Cross-database references, dependency graphs, dynamic-SQL caveats: deferred as a module extension.

## 24. Drift detection — **P2 · Proposal now, build later**

Today drift surfaces implicitly (deterministic files → git commits). A real drift module (DB vs Git, DB vs DB, baselines) is a distinct product surface. **`DRIFT_MODULE_PROPOSAL.md` is authored in this release**; no drift code ships.

## 25. Schema compare — **P2 · Planned**

Same foundation as drift (normalized deterministic scripts). Covered inside the drift proposal as the interactive counterpart; analysis-only first version, no deployment scripts.

## 26. Reporting — **P1 · Already largely present**

Run reports (HTML/CSV/JSON), audit export (CSV/JSON), job config export all exist and include version/date headers. *Implement now:* nothing beyond §6/§13 surfaces. PDF: **Rejected** (no reliable report engine in the stack; HTML prints fine).

## 27. Migration assessment — **P3 · Proposal only**

**`MIGRATION_ASSESSMENT_PROPOSAL.md` is authored in this release** (SQL Server → PostgreSQL / AlloyDB / BigQuery: inventory, type matrix, syntax analysis, scoring model with explainability requirements). No analyzer code ships — building a shallow one would violate the "no unsupported claims" rule.

## 28. AI-assisted features — **P3 · Roadmap only**

No secure provider abstraction exists; every prerequisite in the brief (opt-in, disclosure, redaction, audit, BYOK) is unmet. Documented in the enterprise roadmap; nothing ships.

## 29. Enterprise capabilities — **P3 · Roadmap now**

**`ENTERPRISE_ROADMAP.md` is authored in this release** (GitHub Enterprise Server first — lowest-friction: Octokit + git already support custom base URLs; then GitLab/Azure DevOps, SSO/RBAC, policy, air-gap). No enterprise complexity ships in the OSS desktop now.

---

## Priority rollup for this release (v0.9.0)

**P0 (all implemented this release):**
1. Overdue schedule detection (Dashboard, Jobs, Job Workspace)
2. Truncation/clipping defects (Jobs actions, History badges, Dashboard header)
3. App-log unbounded growth fix + logs panel + Open logs folder
4. Diagnostics: credential-store / storage / state-DB probes, timestamps, copy
5. Secret-redaction regression tests for logs panel & support-bundle path

**P1 (implemented this release):** dashboard attention section · server version/edition + last-checked + copy name · repository validation persistence + branch-existence check + mode-aware verdicts · pause/resume, duplicate, export-config job actions · wizard: preset transparency, mode descriptions, folder preview + collision warning, branch validation, next-run preview, DB search/select-all, review preflight · history trigger/PR/± split + filter reset · diff copy actions, wrap, find, type filter · alert retry + failure logging · storage sizes + open-folder · audit event completeness · permission generator server-scope + revoke script · About support info · DESIGN_SYSTEM.md

**P1 deferred (documented in DEFERRED_IMPROVEMENTS.md):** notification center · background activity center · settings dirty-state rework · workspace cleanup action · service-side connection probe

**P2/P3 (proposals/roadmap only):** global search · command palette · object explorer · drift module · schema compare · vendor alert presets · dark mode · migration assessment · AI features · enterprise control plane

# Obsync v0.9.0 — Implemented Improvements

Companion to [IMPROVEMENT_ASSESSMENT.md](IMPROVEMENT_ASSESSMENT.md) and [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md). Every item below shipped in v0.9.0 with tests; verification evidence at the end.

## Track 1–3 — Scheduling trust, dashboard health, job actions

**What changed / why**
- **Overdue detection (P0).** A scheduled, enabled, non-running job whose cached next run is >5 minutes past now shows an amber dot + "Overdue" (tooltip carries the scheduled time and the corrective hint) on the Dashboard and Jobs tables and the Job Workspace meta line. Users could previously believe a schedule was operating because a Next Run timestamp was displayed — even after it silently passed. Rule: `SyncJob.IsScheduleOverdue(now)` (pure, clock-injected).
- **Pause / Resume (P1).** `SyncJob.Enabled` existed and the scheduler honored it, but there was no UI. The Jobs page now has a pause/resume toggle per row: pausing persists `Enabled=false`, clears the cached next run (in the DB, not just on screen), and shows a neutral "Paused" badge; the service unschedules within one reconcile tick. Audited (JobPaused/JobResumed).
- **Duplicate + Export configuration (P1).** New per-row overflow "⋯" menu (Duplicate / Export configuration… / Delete) so the row stays uncluttered. Duplicate deep-copies the job as "<name> (copy)" — paused, fresh run summary — and audits JobDuplicated. Export reuses the secret-free job-config porter and audits JobExported (also added to the Job Workspace export path).
- **Needs-attention panel (P1).** A dashboard card (absent when everything is healthy) listing failed jobs (with the error's first line), warning jobs, overdue jobs, and servers whose last connection test failed — each with a direct Open action; capped at 6 rows with "+N more". Aggregation is a pure, tested `AttentionModel.Build`.
- **Cancel audit (P1).** Cancelling a run now writes AuditAction.RunCancelled.
- **Truncation/clipping fixes (P0).** The Jobs/Dashboard "Changes" header truncated and the Actions column clipped its own buttons at default width. Columns were rebalanced from *measured* header/badge widths; cells ellipsize instead of wrapping; tag chips trim. A DPI-aware layout probe (`TableLayoutTests`) renders both tables at a 960-px content area with worst-case rows and asserts nothing clips.

**Files:** `SyncJob.cs`, `DashboardViewModel/View`, `JobsViewModel/View`, `JobDetailViewModel/View`, `AttentionModel.cs` (new), `JobConfigExport.cs` (new), `Converters.cs`, `App.xaml`.
**Tests added:** `SyncJobOverdueTests` (9-case theory incl. grace boundary), `AttentionModelTests`, pause/resume/duplicate coverage in `JobsViewModelTests`, cancel-audit test, `TableLayoutTests` render probe.
**Known limitation:** with the navigation rail expanded at the 960-px minimum window width, nine columns physically cannot fit — same as before this release; collapsing the rail fits. Resume of a cron-scheduled job shows its next run after the service's next reconcile (the app does not host a Quartz engine — by design).

## Track 4 — Server & repository health

**What changed / why**
- **Structured server info (P1).** The probe already captured edition/version but buried them in a status string. They are now persisted (`ServerEdition`/`ServerVersion`, migration **V013**) at test time and shown as "SQL Server" and "Last checked" columns. A failed test preserves the last-known edition/version. Copy-server-name button added. No new page-load queries.
- **Persisted repository validation (P1).** Repositories previously persisted nothing about token checks (asymmetric with Servers). New `RepositoryValidationStatus` (Unvalidated/Valid/Attention/Failed) + timestamp + detail, persisted from both dialog Validate and row Check token (V013), shown as a status badge with the detail and last-validated time in the tooltip.
- **Branch existence check (P1).** Validation now verifies the configured default branch exists (first caller of the previously dead `GetBranchesAsync`); a missing branch is a hard validation failure with an exact message. A branch-listing API failure degrades to a warning, never a false failure.
- **Mode-aware verdicts (P1).** A read-only token now reads: "Read-only access — Direct Commit and Pull Request jobs will fail to push; Local Commit Only and Export Only are unaffected." No fake PR-permission pre-check (GitHub cannot report it without creating a PR — documented instead).
- **Alert delivery retry (P1).** Email and webhook alerts each retry once after 5 s on delivery failure, then log a warning; never throws into the run pipeline.

**Files:** `ConnectionProfiles.cs`, `ConnectionProfileRepository`, `RepositoryProfileRepository`, `V013__server_info_and_repo_validation.sql` (new), `ServersView/ViewModel`, `RepositoriesView/ViewModel`, both dialogs, `GitHubService.cs`, `RunAlertService.cs`.
**Tests added:** `ProfileHealthPersistenceTests` (V013 round-trips), `RunAlertServiceRetryTests`, branch-missing/case-sensitivity paths in `RepositoryTokenCheckTests`, save-outcome semantics, `ServersPageRenderTests`.

## Track 5 — Wizard clarity

**What changed / why** (all additive; the five-step structure is untouched)
- **Source:** live database filter + Select all/Clear (acting on the filtered view); identity caption for Windows-integrated connections ("Manual runs connect as {you}; scheduled runs connect as the Obsync service account"); one-line explanation of "Sync all user databases" semantics (auto-inclusion, exclusions, offline-DB skip behavior).
- **Objects:** "View included object types" expander whose content comes from `ObjectSelectionPresets.Expand` at runtime — it can never drift from actual behavior.
- **Destination:** one-line descriptions for all four commit modes; live "Files will be written to:" effective-folder preview; warning when another job writes to the same repository + folder; git branch-name validation via new pure `GitRefName.IsValidBranchName`.
- **Schedule:** live next-run preview with the local time zone (driven by the same computation that stamps the save); the service-readiness banner now appears only when the chosen cadence actually needs the service; a caption documenting the real overlap policy (skip if still running).
- **Review:** optional "Run preflight checks" — SQL connection, repository access + branch existence (mode-aware: missing branch is Fail for PR, Warning for push modes since it's created on first push), export-destination writability, credential presence, folder collision. Advisory only; never blocks Save. Orchestrated by the new DI-registered `JobPreflightService`, results reuse the timestamped diagnostics row template.

**Files:** `CreateJobViewModel.cs`, `CreateJobWindow.xaml`, `GitRefName.cs` (new), `JobPreflightService.cs` (new), `AppServices.cs` (registration).
**Tests added:** `GitRefNameTests` (30 theory cases), `JobPreflightServiceTests`, `WizardClarityTests` (16), `CreateJobWindowRenderTests` (all five steps — the wizard window was previously in no render pass). All pre-existing wizard tests pass unchanged.

## Track 6 — History & diff clarity

**What changed / why**
- **Runs grid:** the single change number became the same +added/~modified/−deleted tokens the Timeline uses (shared template, so the two can't diverge); Trigger column (Manual/Scheduled/Startup/Catch-up); "PR #n" link on pull-request runs; "Clear filters" appears when any filter is active. Status badges, Skipped, and Run time no longer truncate at default width (columns consolidated: tags into the Job cell, scanned count into the Changes tooltip — measured, probe-asserted).
- **Diff viewer:** Added/Modified/Deleted filter chips with counts (combining with the text filter); copy object name / path (context menu) and Copy script (correct side per change type); word-wrap toggle (horizontal scroll disables while wrapping); find-in-script with Enter/F3/Shift+F3 cycling, match counter, and scroll-to-row on the virtualized list.

**Files:** `HistoryView/ViewModel`, `TimelineModels.cs`, `ScriptDiffWindow/ViewModel`, `DiffTextSearch.cs` (new), `RunDisplayConverters.cs` (new).
**Tests added:** `DiffTextSearchTests`, `RunDisplayConvertersTests`, `HistoryRunsRenderTests` (960-px no-clip probe), copy-side theory + find cycling in `ScriptDiffViewModelTests`.

## Tracks 7–8 — Diagnostics, logs, storage, About, audit, permissions

**What changed / why**
- **Diagnostics depth (P0):** new probes — Credential Manager round-trip (sentinel credential, value never logged), data-folder and workspaces writability, state database (size + `PRAGMA quick_check`); every check now shows when it ran; "Copy results" copies the full report.
- **Logs (P0):** the app log sink had **no retention cap** (unbounded growth) — now capped at 31 days like the service. New "Recent logs" panel on the Diagnostics tab: newest app + service log files, severity filter, search, copy, Open logs folder; a faithful parser with continuation-line folding (redaction is guaranteed at the Serilog call sites, and a test documents the parser is verbatim).
- **Storage health (P1):** workspaces/state-DB/logs sizes + free disk (computed asynchronously), Open-folder buttons, and an honest note that SMTP alerts do not use the proxy.
- **About (P1):** Support-information card — app/engine/service (heartbeat-gated)/.NET/Git/Windows versions + state-DB schema version, "Copy support info", and project/issues/documentation links.
- **Permission generator (P1):** the existing server-level-permissions capability is now reachable (checkbox), and a **revoke script** generator produces the deterministic inverse of the grants (DROP USER only as commented guidance — never executable by default).
- **Audit completeness (P1):** SettingsChanged (per section), CredentialsUpdated (names the credential, never the value), PermissionScriptGenerated, AuditLogExported, SupportBundleExported, UpdateChecked.

**Files:** `DiagnosticsService.cs`, `LogFileReader.cs` (new), `StorageUsage.cs` (new), `SupportInfoService.cs` (new), `SettingsView/ViewModel`, `App.xaml.cs`, `SqlPermissionScriptBuilder.cs`, `AuditLogExport.cs`.
**Tests added:** `LogFileReaderTests`, `StorageUsageTests`, `SupportInfoServiceTests` (incl. key-whitelist redaction guarantee), diagnostics probe tests, revoke-script byte-stability + inverse coverage, audit-write positive/negative cases.

## Verification (definition-of-done evidence)

| Gate | Result |
|---|---|
| Build (`dotnet build Obsync.slnx`, warnings-as-errors) | 0 warnings / 0 errors |
| Unit + integration tests | **726 / 726 green** (was 561 before this release: Shared 158, App 318, Engine 98, Integration 64, Metadata 54, Git 34) |
| Runtime E2E battery (`tools/Obsync.E2E`, 15 scenarios vs live SQL Server + local bare remotes) | All assertions passed (exit 0) |
| UI verification | Before/after screenshots of all six shell pages via the isolated UI harness (scratch data root — never the real one); render smoke tests cover every view incl. the wizard window and both job tables at worst-case widths |
| Secrets | No token/password persisted or displayed by any new surface; support bundle, logs panel, and support-info card covered by redaction tests |
| Desktop/service consistency | No service behavior changes except honoring the pause toggle (existing reconcile path) — engine, hashing, and file formats untouched (hash-stability invariant preserved) |

**Screens modified:** Dashboard, Jobs, Job Workspace, Servers, Repositories (+ dialogs), Create/Edit Job wizard (all five steps), History (Runs grid), Script diff window, Settings (Diagnostics, Network & storage, Security & audit, About).
**Backend modified:** Shared models (SyncJob, connection/repository profiles), Data (V013 migration, two repositories), GitHub service (branch check wiring), Engine alerting (retry), Metadata (revoke script builder).
**Service components:** none functionally changed (V013 is additive; the service reads the same tables).

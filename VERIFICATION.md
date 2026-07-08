# Pre-launch verification pass

A one-evening, structured click-through of everything that shipped without a live pass. Work top to
bottom; each item says what to do and what "pass" looks like. Delete this file once the pass is done
(findings become issues; nothing here is user documentation).

Environment: your real SQL Server, your real GitHub repository, and a machine (or VM) that has
never had Obsync installed — the installer section needs a clean target.

## 1. Install & upgrade (0.6.0 MSI)

- [ ] **Clean install** `artifacts\Obsync-0.6.0-win-x64.msi` on a machine without Obsync.
      Pass: installer shows the branded dialogs, app launches from the Start Menu, `obsync` works
      in a new terminal (machine PATH), no .NET/git install needed.
- [ ] **Upgrade** an existing 0.5.0 machine. Pass: settings, jobs, and credentials survive;
      version shows 0.6.0 in Settings → About.
- [ ] **Service**: set the service account (services.msc → your user), re-enter "Log on as a
      service" if prompted, start the service. Pass: service runs; a scheduled job fires.
- [ ] **Uninstall** on the clean machine. Pass: no leftover Start Menu entry or PATH entry;
      `%LOCALAPPDATA%\Obsync` (user data) intentionally remains.

## 2. Core flow regression (15 minutes)

- [ ] Create a job end-to-end in the wizard (server → objects → destination → schedule → review).
- [ ] Run Now → commit lands on GitHub; Job Workspace shows counts, commit link works.
- [ ] Edit the job; confirm nothing resets (schedule, advanced options, tags).
- [ ] Make a schema change on the server; run again. Pass: exactly that change is committed.

## 3. Features awaiting their first live pass

### Repositories page (redesigned)
- [ ] Page is a single list; Add/Edit opens the dialog; columns stretch properly (probe artifact:
      they compressed in the headless render — verify they fill the width live).
- [ ] **Check token** row action reports the stored token's permissions in the status line.
- [ ] Edit with a blank token keeps the saved one; **Validate** works without re-pasting the token.

### Audit
- [ ] Settings → Recent activity shows run outcomes for **scheduled** runs (service), not just
      manual ones, each with commit SHA in the detail.
- [ ] **Export audit log** writes CSV and JSON; both open cleanly (Excel / text editor).

### Update check
- [ ] Settings → About → Check for updates reports something sensible (placeholder repo until
      launch — expect "could not check" or "latest"; no crash, no hang).

### History: Timeline (0.6.0)
- [ ] Timeline tab groups runs by day with correct totals; dots match statuses.
- [ ] Expanding an entry lists changed objects; clicking one opens the diff viewer **preselected
      at that object**.
- [ ] Filters (job/status/search) affect both tabs; selecting a timeline entry enables the header
      Export report / View changes buttons.
- [ ] Search matches a database name and a `DOMAIN\user`.

### Job Workspace: Dependencies (0.6.0)
- [ ] Picker lists indexed objects instantly; search narrows (try `schema.name` form).
- [ ] Selecting a table shows Used by / Uses with plausible content — verify one against
      SSMS "View Dependencies".
- [ ] Clicking a dependency drills into it; a cross-database reference is shown but not clickable.
- [ ] A job that has never run shows the "run this job once" hint instead of an empty picker.

### Generated docs & security review (0.6.0)
- [ ] First run after upgrade commits `docs/README.md` and `security/security-review.md`
      (plus `server/security-review.md` if the job scripts server objects).
- [ ] `docs/README.md` renders well on GitHub; spot-check one table's columns/types against SSMS;
      a table/column `MS_Description` you set appears.
- [ ] Security review findings are plausible (e.g. grant something to `public` in a test DB →
      next run's diff shows the new finding; revoke → it disappears).
- [ ] A second run with **no schema changes** commits neither file (docs gate) and stays fast.

## 4. VLDB benchmark (your biggest database)

Record these numbers — they are the performance claim for launch.

1. Point a new job at your largest database (full type selection, direct commit or export-only).
2. **Run 1 (cold, full scan):** note wall-clock duration and objects scanned (History → run row).
3. Make one small change (e.g. `ALTER PROCEDURE`), **Run 2**, note duration.
4. Change nothing, **Run 3 (incremental no-op):** note duration — this is the number that matters;
   it should be a small fraction of Run 1.
5. Note: `SELECT COUNT(*) FROM sys.objects` for scale context, machine specs, and whether the
   docs/security artifacts noticeably affected Run 3 (they should not — docs skip unchanged runs;
   the security queries are trivial).

| Run | Duration | Scanned | Changed | Notes |
| --- | --- | --- | --- | --- |
| 1 (full) | | | | |
| 2 (one change) | | | | |
| 3 (no-op) | | | | |

## 5. Alerting & PR mode (if not already live-tested)

- [ ] PR mode job: run creates a branch + PR with reviewers; no-change run creates **no** PR.
- [ ] Alerts: Send test alert works (SMTP and/or webhook); a real failed run alerts once.

## Sign-off

Date: ____  Obsync version: 0.6.0  SQL Server: ______  Findings filed: ______

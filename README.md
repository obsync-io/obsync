# Obsync

**Automatically script, track, commit, and push SQL Server object changes to GitHub.**

Obsync is an enterprise-grade Windows application that connects to SQL Server, scripts
database objects at high speed, detects changes against the previous upload, and
commits/pushes only the changed object scripts to GitHub. It supports manual runs and
scheduled background jobs, and can also save scripts to a local path.

The entire product is built around one clean concept — the **Sync Job**:

> A SQL Server source database is scripted, change-detected, committed, and pushed to a
> GitHub repository on a defined schedule.

## Status

🚧 Under active development. See the architecture below and the issue tracker for progress.

## Tech stack

| Area | Technology |
| --- | --- |
| Framework / language | .NET 10 (LTS), C# 14 |
| Desktop UI | WPF (custom Fluent-inspired enterprise theme) |
| Background worker | .NET Worker Service (installable as a Windows Service) |
| CLI | .NET console app for automation |
| SQL connectivity | Microsoft.Data.SqlClient |
| High-fidelity scripting | SQL Server Management Objects (SMO) |
| Optional schema tooling | DacFx (later) |
| Scheduling | Quartz.NET |
| Local state | SQLite (Microsoft.Data.Sqlite + Dapper) |
| Logging | Serilog |
| Git | Git CLI (LibGit2Sharp later) |
| GitHub API | Octokit.NET |
| Secrets | Windows Credential Manager + DPAPI |

## Solution layout

```
Obsync.sln
src/
  Obsync.Shared      Domain models, result types, validation, path mapping, hashing, normalization
  Obsync.Data        SQLite local state: schema, migrations, repositories
  Obsync.Metadata    Raw SQL Server metadata readers (Microsoft.Data.SqlClient)
  Obsync.Smo         SMO scripter for full object-type coverage and high fidelity
  Obsync.Git         Local Git workspace: clone, pull, diff, commit, push
  Obsync.GitHub      GitHub API integration (Octokit.NET)
  Obsync.Scheduler   Scheduling abstractions over Quartz.NET
  Obsync.Security    Credential storage (Windows Credential Manager) + DPAPI encryption
  Obsync.Engine      Core orchestration: inventory -> script -> hash -> diff -> commit -> push
  Obsync.Service     Windows Service / Worker host for scheduled jobs
  Obsync.Cli         CLI tool for automation and testing
  Obsync.App         WPF desktop application
tests/
  Obsync.Engine.Tests
  Obsync.Metadata.Tests
  Obsync.Git.Tests
  Obsync.Integration.Tests
```

Dependencies point inward: `Obsync.Shared` has no project references; infrastructure
projects depend on `Obsync.Shared`; `Obsync.Engine` orchestrates over interfaces; the
hosts (`App`, `Service`, `Cli`) compose everything via dependency injection.

## Security

- SQL passwords and GitHub tokens are stored in Windows Credential Manager / encrypted
  with DPAPI — never in plaintext, `appsettings.json`, or the local SQLite database.
- Secrets are masked in the UI and excluded from logs.
- Supported authentication: Windows Integrated, SQL Login, and GitHub fine-grained PAT.

> [!IMPORTANT]
> **Windows Credential Manager vaults are per-user.** Secrets saved by the desktop app live in the
> vault of the Windows account that ran the app. For **scheduled** runs to work, install the
> `Obsync.Service` Windows Service under that **same account** (not the default `LocalSystem`).
> Otherwise the service cannot read the credentials and scheduled runs fail authentication even though
> "Run Now" from the app succeeds. The service logs the identity it runs as on startup, and a run that
> can't find its credentials fails with a clear, actionable message.

## Read-only by design

Obsync **only reads** from SQL Server. It queries catalog metadata and object definitions
(`sys.sql_modules`, `sys.objects`, `sys.schemas`, `sys.triggers`, `sys.synonyms`, `sys.sequences`,
`sys.databases`, `sys.database_permissions`) and scripts objects via SMO with scripting only
(no data). It never executes DDL or DML against the source, never runs deployment scripts, and
never requires `sysadmin`.

### Least-privilege account

A dedicated read-only account needs only the following, per database:

| Permission | Why |
| --- | --- |
| `CONNECT` | open a connection to the database |
| `VIEW DEFINITION` | read object definitions (`sys.sql_modules`, SMO scripting) |
| `VIEW DATABASE STATE` | read database metadata used during scripting |

Generate a ready-to-run grant script from **Settings → Required SQL permissions** (enter the account
and databases, then Copy or Save as `.sql`). For a job spanning many databases you can instead grant
server-wide `VIEW ANY DEFINITION` + `VIEW SERVER STATE`.

## Auditing

Obsync keeps an append-only audit trail of who did what — job create/edit/delete, manual run starts,
and SQL server / GitHub repository profile changes — attributed to the Windows identity that
performed the action. Each run is also stamped with the account that started it (the interactive user
for an app run, or the service account for a scheduled run). View the recent trail in
**Settings → Recent activity**. The trail lives in the local SQLite database and never contains
secrets.

## Environment tags

Tag a job with free-form labels (e.g. `prod`, `finance`, `pci`) in the job wizard. Tags show as chips
on the Dashboard, Jobs list, Job Workspace, and — recorded per run — in History, so you can see at a
glance which jobs touch which environments.

Any tag in the **production** list (configured in **Settings → Production tags**, default
`prod, production`) renders red and arms a safeguard: a manual **Run Now** on a production-tagged job
asks for confirmation first. Scheduled runs are never prompted.

## Syncing every database on a server

A job's **Source** step offers two database scopes:

- **Selected databases** — the fixed list you check in the wizard.
- **All user databases** — every online user database on the server, resolved fresh at the start of
  each run, so databases created later are picked up automatically with no job edit. The checklist
  becomes an optional **exclusion** list, compared case-insensitively. Offline databases are skipped
  with a warning; system databases are never included.

With the dynamic scope each database gets its own subfolder under the destination folder, so the
repository layout stays stable as databases come and go.

## Server-level objects

A job can also version the SQL Server **instance** itself. On the **Objects** step, tick
**Version server-level objects** and pick from: logins, user-defined server roles, server
credentials, linked servers, and SQL Agent jobs, operators, and alerts. A
`server/server-configuration.sql` file (the instance's `sp_configure` values) is always captured
with the pass. Files land under a `server/` tree next to the per-database folders and ride the same
hash → diff → commit pipeline — an unchanged instance produces no commit, dropped objects are
deleted (when enabled), and un-scriptable objects are reported as skips:

```
server/
  logins/            roles/              credentials/        linked-servers/
  agent/jobs/        agent/operators/    agent/alerts/
  server-configuration.sql
```

Two notes:

- **Secrets never land in the repo.** SMO scripts logins with a placeholder hashed password, and
  credential scripts carry the identity only — the files document existence, role membership, and
  configuration, not passwords.
- **Permissions.** Server-level scripting needs server-wide `VIEW ANY DEFINITION` +
  `VIEW SERVER STATE`, and Agent jobs additionally need membership in msdb's `SQLAgentReaderRole`.
  Without the msdb role, Agent objects are reported as skipped (and nothing is deleted).

## Versioning reference data

Schema without its lookup data is half the picture. On the **Objects** step, tick
**Version reference data** and pick the static/lookup tables to track (the picker shows live row
counts). Each table's rows are exported as a deterministic T-SQL INSERT script under `data/`
(e.g. `data/dbo.Currency.sql`) and change-tracked exactly like any object — a data change commits,
an unchanged table doesn't.

Scripts are stable across runs and machines: rows are ordered by primary key (or by all sortable
columns when there is no key), literals use invariant formatting, identity columns are wrapped in
`SET IDENTITY_INSERT`, and computed/rowversion columns are excluded. Tables over the per-table row
cap (default 5,000 — reference data means lookup tables, not fact tables) are **reported as
skipped**, never silently truncated; the cap is adjustable on the same step. A table missing from
one of a multi-database job's databases is reported and skipped, and never causes the run to fail.

## Viewing scripts and diffs in the app

Every change a run commits can be inspected without leaving Obsync. Click **View diff** on a row of
the Job Workspace **Changes** tab, or select a past run on the **History** page and click
**View changes**. The viewer lists the run's changed objects on the left (filterable) and shows the
selected object on the right — side-by-side or unified, with line numbers and word-level change
highlights. Added objects show the full new script; deleted objects show what was removed.

Content is read from the repository's local git workspace (the same clone the engine commits from) —
no network calls and no token needed. If the commit isn't available on this machine (for example on
a different PC than the one that ran the sync), the viewer says so and offers **Open on GitHub**
instead.

## Everyday options

Settings covers the knobs a real deployment needs:

- **Run history** — keep runs for 30/90/180/365 days or forever (the default). Pruning happens at
  startup and daily in the service; what was committed to GitHub is never touched.
- **Git commits** — set the committer name/email so `git blame` and the GitHub history show your
  team instead of a generic tool identity.
- **Git workspaces location** — move the local clones to another drive; applies from each job's
  next run, and the old location is never deleted behind your back.
- **Notifications** — an in-app toast when a run fails or ends with warnings, plus a summary of
  scheduled runs that failed while the app was closed. Can be turned off.
- **Update checks** — on startup (at most once per day) and via Settings → About → Check for
  updates, Obsync asks github.com's releases endpoint whether a newer version exists and only
  notifies; it never auto-installs, and nothing else is transmitted — no telemetry.

Jobs are also portable: **Export configuration** (Job Workspace → Configuration) writes a
secret-free JSON file that references the server and repository by name; **Import** on the Jobs
page re-attaches it to the matching profiles on another machine — passwords and tokens never
leave Windows Credential Manager.

## Performance on very large databases

Obsync targets VLDBs — hundreds of thousands of objects. Two things in the job wizard's
Schedule → advanced settings matter at that scale:

- **Max parallel workers** — how many objects are normalized/hashed/written concurrently per
  database (0 = one per CPU core). Scripting itself also fans out: large SMO table collections
  are partitioned across worker connections, capped at 8 so the source server is never hammered.
- **Query / lock timeouts** — bound how long a metadata read may run or wait on locks.

### Incremental scripting

On by default (and recommended for very large databases). Steady-state runs read one cheap
snapshot of `sys.objects.modify_date`, then re-read only the objects that changed since the last
successful run — everything else keeps its recorded hash and committed file untouched, so a
nightly run on a 500k-object database only pays for the day's changes.

Correctness guardrails, applied automatically:

- Watermarks only advance when a run ends healthy (success, no-changes, or warning); after a
  failed or cancelled run everything past the last good watermark is re-examined.
- If an object turns up that is older than the watermark but was never scripted (say the schema
  filter or the object selection changed), its whole type gets a full scan that run — nothing is
  ever silently left unscripted.
- Export runs are always full snapshots.

**Caveat:** permission- or extended-property-only changes don't update SQL Server's
`modify_date`, so incremental runs won't notice them on otherwise-unchanged objects. Turn the
option off and run once to capture those, then turn it back on. Only tables, views, procedures,
functions, triggers, synonyms, and sequences are filtered — every other type is always fully
scanned (they are few).

## Alerting

Obsync can alert people when a sync run finishes — the service-side counterpart to the in-app
toasts, so unattended drift and failures are noticed without opening the app. Two channels, both
configured globally in Settings → Alerts:

- **Email (SMTP)** — host/port/TLS, optional authentication (the password lives in Windows
  Credential Manager, like every other secret), a from address, and comma-separated recipients.
- **Webhook** — a JSON POST to any http(s) endpoint (Teams, Slack, or your own service). Honors
  the configured proxy.

Triggers are independently toggleable: run failed (on by default), run finished with warnings (on
by default), and changes detected/committed (off by default — noisy). A filter restricts alerts to
scheduled/startup runs (on by default), since a manual run already has the user watching. Delivery
is best-effort: an alert failure never fails or delays the run.

Alerts are sent by whichever host ran the job — the app for Run Now, the Windows Service for
scheduled runs — so the service account must be able to reach the SMTP relay or webhook endpoint.

## Run reports

Any run can be exported as a shareable file for a reviewer, an auditor, or a ticket — on demand,
nothing is written automatically. Use **Export report** on the Job Workspace (exports the latest run)
or on the **History** page (select any past run), then choose a format:

- **HTML** — a formatted, self-contained document (run summary + changed objects + log timeline) that
  opens in any browser offline; no external styles, scripts, or fonts.
- **CSV** — one row per changed object, for Excel or a pipeline.
- **JSON** — the full run structured for tooling.

Reports are built from the same data shown in the app and contain no secrets (SQL passwords and GitHub
tokens live only in Windows Credential Manager).

## Ignoring objects (`.obsyncignore`)

To keep noisy objects out of version control, commit a `.obsyncignore` file in a job's destination
folder (the folder its scripts are written to). It is read on every run and never modified or deleted
by Obsync. Sections are comma-separated; `#` starts a comment; a bare line is an object name glob
(`*` = any run, `?` = one character), matched against `schema.name` and the bare name.

```
# .obsyncignore — exclude noisy objects from scripting
schemas: staging, temp          # ignore whole schemas
objects: dbo.tmp_*, *_bak       # ignore by name pattern
types:   view, synonym          # ignore whole object types (table, "stored procedure"/proc, ...)
dbo.zz_*                        # a bare line is an object glob
```

Ignored objects are simply not scripted; anything already committed for them is left untouched.
Rules from `.obsyncignore` merge with a job's configured ignore patterns. (Zip exports have no
persisted folder, so they honor only the job's configured patterns.)

## Installing

Obsync ships as a per-machine **MSI** that installs the desktop app, the `obsync` CLI (added to
`PATH`), the background **Obsync** Windows Service, and a bundled, SHA256-pinned **MinGit**. It is
**zero-prerequisite** — the target machine needs **no .NET runtime and no git install**. Build it
with:

```powershell
pwsh packaging\build-installer.ps1          # add -SigningThumbprint <tp> to code-sign
# → artifacts\Obsync-<version>-win-x64.msi
```

Install interactively by double-clicking the MSI (the wizard includes a **Service Account** step),
or silently:

```powershell
msiexec /i Obsync-<version>-win-x64.msi /qn /l*v install.log
```

See **[packaging/INSTALL.md](packaging/INSTALL.md)** for the full enterprise deployment guide:
silent-install properties, gMSA setup, repair/uninstall/upgrade, service recovery defaults, and
Event Viewer / log locations.

### Service account (required for scheduled runs)

The Windows Service is installed **manual-start under `LocalSystem`** by default. Because Obsync keeps
secrets in the per-user Windows Credential Manager and its data under `%LOCALAPPDATA%`, the service
**must run under the same Windows account that runs the desktop app** (see the Credential Manager note
above). Supply that account at install time:

```powershell
msiexec /i Obsync-<version>-win-x64.msi SERVICE_ACCOUNT="DOMAIN\user" SERVICE_PASSWORD="secret" /qn
```

For a group Managed Service Account pass `SERVICE_ACCOUNT="DOMAIN\name$"` and no password. The
account needs the "Log on as a service" right (the Service Control Manager grants it when the
account is assigned).

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Git on `PATH`.

```powershell
dotnet build Obsync.slnx
dotnet test  Obsync.slnx
```

## License

[MIT](LICENSE) © 2026 Obsync Contributors.

Obsync redistributes third-party components under their own licenses — see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The installer additionally bundles an
unmodified [MinGit (Git for Windows)](https://github.com/git-for-windows/git), which is a
separate program licensed under GPLv2 and aggregated alongside Obsync.

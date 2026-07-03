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

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Git on `PATH`.

```powershell
dotnet build Obsync.slnx
dotnet test  Obsync.slnx
```

## License

[MIT](LICENSE) © 2026 Obsync Contributors.

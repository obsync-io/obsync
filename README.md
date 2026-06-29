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

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Git on `PATH`.

```powershell
dotnet build Obsync.slnx
dotnet test  Obsync.slnx
```

## License

[MIT](LICENSE) © 2026 Obsync Contributors.

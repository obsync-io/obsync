# Security policy

Obsync is built for security-conscious environments: it is **read-only against SQL Server** (it
never executes DDL/DML against your databases), stores secrets exclusively in **Windows Credential
Manager**, and sends **no telemetry** — the only outbound calls are to your configured GitHub
repository (and, if enabled, the GitHub releases endpoint for the notify-only update check and
your own alert SMTP/webhook targets). See the README's *Security* and *Read-only by design*
sections for the full model.

## Reporting a vulnerability

Please **do not open a public issue for security vulnerabilities.**

Report privately instead, either via:

- **GitHub private vulnerability reporting** — the *Security* tab of this repository →
  *Report a vulnerability* (preferred), or
- **Email**: security@obsync.io

Include what you can: affected version (Settings → About), reproduction steps, and impact. You can
expect an acknowledgement within **72 hours** and a status update within **14 days**. Please give
us a reasonable window to ship a fix before public disclosure; we will credit reporters in the
release notes unless you prefer otherwise.

## Supported versions

Only the **latest release** receives security fixes. Obsync notifies you in-app when a newer
version is available (notify-only — it never auto-installs).

## Scope notes

- The MSI bundles an unmodified, SHA-256-pinned MinGit; vulnerabilities in git itself should be
  reported to the git-for-windows project, though we will ship bundled-git updates promptly.
- Obsync runs with the privileges of the account you give it; reports that require an already-
  compromised local account or SQL `sysadmin` rights are generally out of scope.

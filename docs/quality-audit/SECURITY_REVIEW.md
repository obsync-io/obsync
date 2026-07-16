# Obsync production-readiness audit — defensive security review

Audit date: 2026-07-15 · Scope: secret handling, injection, path safety, TLS, supply chain, and
the truth of every security claim the product makes. Method: full data-flow tracing by a dedicated
security audit pass, corroborated by the other audit passes and by runtime checks from the E2E
battery. This is a defensive review for the protection of enterprise users; no exploit tooling was
built.

## Verdict

Secret handling is genuinely strong — the architecture (Windows Credential Manager only, no secret
columns in SQLite, token via environment-injected git config, argv kept clean for process-creation
auditing) held up under end-to-end tracing. The audit found **no Critical or High security
defects**. Two Medium and three Low findings were identified; both Mediums and two Lows are fixed.

## Findings and dispositions

| # | Sev | Finding | Disposition |
|---|---|---|---|
| SEC-1 | Medium | The Add Server dialog defaulted **Trust server certificate = ON** (the model default is off) — every UI-added server accepted any certificate, disabling MITM protection for the TDS session. | **Fixed** (OBS-22): defaults off + caution text; edit path preserves the stored value. Regression-tested. |
| SEC-2 | Medium | Raw git stderr is persisted into `runs.error_message`, `run_logs.detail`, audit detail, run reports, and the support bundle. A **manual proxy URL embeds `user:password@host`**, and git/curl proxy failures can echo it — the one credible path for a secret to reach the state DB and exports. | **Fixed** (OBS-19): URL userinfo is redacted (`://***@`) in every stderr-derived failure string before it leaves Obsync.Git. Unit-tested. |
| SEC-3 | Low | Branch names beginning with `-` reach git as positionals (option-injection; self-inflicted config only, no shell involved — args use `ArgumentList`). | Open (Low). Practical exposure requires the operator to name their own branch like an option; wizard-side rejection is a backlog item. |
| SEC-4 | Low | `DestinationFolder` allowed `..` traversal outside the clone (operator-authored value; not reachable from SQL-derived names, which are sanitized). | **Fixed** (OBS-35): wizard validation rejects traversal, rooted destination folders, and invalid path characters. |
| SEC-5 | Low | `DpapiSecretProtector` is registered but has no callers; README wording implied an app-level DPAPI path. | Documented (this file); the class is a candidate for removal at the owner's call (standing "no dead code" rule). Credential Manager itself uses DPAPI internally, so the user-facing claim is not false. |

## Verified secure (traced end-to-end, with runtime corroboration where noted)

**Secret storage.** All four secrets (SQL password, GitHub token, proxy password, SMTP password)
live exclusively in Windows Credential Manager (`CredWriteW`, per-user persistence), keyed by
namespaced identifiers. Config models carry only the key reference. After the OBS-20 fix, disabling
a feature no longer deletes its stored secret (matching the UI's "leave blank to keep the saved
one" promise).

**State database.** No table carries a secret column (V001–V011 reviewed); `app_settings` JSON
payloads for proxy/alerts exclude passwords by model shape. Runtime corroboration: the E2E battery
greps every committed file for the credential-store token — zero hits (S01).

**Git token path.** The token travels only as a base64 `AUTHORIZATION` header injected through
`GIT_CONFIG_KEY_n/GIT_CONFIG_VALUE_n` **environment variables** — never argv (Windows Event 4688 /
Sysmon record child command lines), never `.git/config`, with `credential.helper` explicitly
cleared and `GIT_TERMINAL_PROMPT=0`. Every network git command (clone/fetch/push) routes through
this one wrapper; the local-only history/diff reader makes no network calls and takes no token.
The failed-command debug log redacts and sits below the default log level.

**Logging.** No `Log*` call carries a password, token, or connection string (swept); connection
strings (which embed the SQL password for SQL-auth) are handed only to `SqlConnection`. Serilog
file sinks receive structured messages only.

**Exports.** Run reports (HTML/CSV/JSON) and the audit-log export draw exclusively from persisted
user-facing run/audit rows; HTML output is fully HTML-encoded with no external references. The
support bundle contains config (secret-free models), diagnostics, recent runs, and app/service
logs — never the state database and never `app_settings`. Job export/import references profiles by
name and never embeds credentials; import cannot smuggle a secret in. (All of these inherit the
SEC-2 fix for stderr-derived error text.)

**SQL injection.** Dynamic identifiers are bracket-escaped (`]` doubled) everywhere they are
composed (module scripting, reference data, permission scripts, server objects); values that reach
live queries are parameterized (schema filters, watermarks, object ids, documentation caps, type
codes); the dependency query applies `QUOTENAME()` server-side; database selection travels as
`InitialCatalog`, never concatenated. The least-privilege permission-script generator escapes both
identifiers and literals.

**Scripted secrets.** Server logins script with SMO's placeholder hash; `CREATE CREDENTIAL` is
composed identity-only — no secret material can enter the repository from server-object scripting.

**Path safety.** SQL-derived names are sanitized (invalid chars replaced, length-capped, collision
suffixed with a stable hash) — verified live with hostile names (space, unicode, `]`, 120+ chars)
in E2E S01. Atomic writes (`.obsync-tmp` + rename) with the temp pattern excluded from `git add`.
Operator-authored paths are now validated (SEC-4).

**TLS & network.** No `ServerCertificateCustomValidationCallback` or accept-any-cert anywhere;
Octokit, the update check, the proxy test, and webhooks use default certificate validation. SMTP
honors `UseTls` (default on). The webhook accepts only absolute http(s) URLs. After SEC-1's fix,
the UI default no longer weakens SQL TLS.

**Update channel.** Notify-only: contacts `https://api.github.com/repos/obsync-io/obsync/releases/latest`,
parses the version defensively, never downloads or executes anything; the release URL opens in the
browser only on explicit user click. A malicious/compromised release JSON can at worst display a
wrong version string.

**Supply chain.** MinGit is version-pinned and SHA-256-verified at installer build; WiX is pinned
via the tool manifest; the SDK via `global.json`; packages via central package management. No
`BinaryFormatter`/unsafe deserialization; `System.Text.Json` without polymorphic type resolution.

**Claims audited true.** "Read-only by design" (every SQL path is a catalog read or SMO scripting;
generated DDL is never executed against a source; runtime-verified across the E2E battery — test
databases were never mutated by runs); "secrets are never stored in the state database" (above,
with SEC-2 now closing the stderr edge); "no telemetry" (outbound endpoints are exactly: the user's
repo, the GitHub releases endpoint, the optional proxy test, and user-configured SMTP/webhook);
the support-bundle "secrets are never included" label; the least-privilege permission script
(CONNECT + VIEW DEFINITION + VIEW DATABASE STATE — no sysadmin).

## Residual risks

- The **non-fast-forward wedge** (OPEN-01 in the bug report) is a reliability risk, not a security
  one, but its manual recovery has operators poking inside `%LOCALAPPDATA%` workspaces — recovery
  instructions should stay prominent.
- **CLI bypasses the production-tag confirmation** (OPEN-08): an automation account can run a
  production-tagged job without a prompt. The guard was always documented as an interactive-app
  safeguard; README now states the scope explicitly.
- **Custom `RemoteUrl` values** (only settable by hand-editing the DB or import files) are not
  validated against the owner/name used for API calls and web links — a deliberate GHES-ish edit
  produces split-brain behavior rather than an error.
- Git has **no per-command timeout**; a blackholed connection ties up the per-repo lock until run
  cancellation or the 30-minute lock bound.
- The **support bundle includes recent app/service logs**; log content is secret-free by
  construction (above), but operators shipping bundles to third parties should still review them —
  server names, database names, and object names are present by design.

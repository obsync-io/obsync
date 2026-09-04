# Obsync production-readiness audit — defensive security review

Audit date: 2026-07-15 · Scope: secret handling, injection, path safety, TLS, supply chain, and
the truth of every security claim the product makes. Method: full data-flow tracing by a dedicated
security audit pass, corroborated by the other audit passes and by runtime checks from the E2E
battery. This is a defensive review for the protection of enterprise users; no exploit tooling was
built.

## Verdict

Secret handling is genuinely strong — the architecture (Windows Credential Manager only, no secret
columns in SQLite, token via environment-injected git config, argv kept clean for process-creation
auditing) held up under end-to-end tracing. Two Medium and three Low findings were identified in
the original pass; all five are now fixed.

> **Corrected 2026-09-03.** This section previously read "no Critical or High security defects".
> That is no longer accurate on two counts: SEC-4 was re-rated **High** on re-examination (below),
> and a later pass found **SEC-6**, a Medium the original audit did not reach — the machine's git
> configuration is fully in effect for every git command, which was measured to exfiltrate the PAT
> and to execute arbitrary code. The "we bundle our own git" assurance in *Git token path* below was
> also narrower than it read; see SEC-6.

## Findings and dispositions

| # | Sev | Finding | Disposition |
|---|---|---|---|
| SEC-1 | Medium | The Add Server dialog defaulted **Trust server certificate = ON** (the model default is off) — every UI-added server accepted any certificate, disabling MITM protection for the TDS session. | **Fixed** (OBS-22): defaults off + caution text; edit path preserves the stored value. Regression-tested. |
| SEC-2 | Medium | Raw git stderr is persisted into `runs.error_message`, `run_logs.detail`, audit detail, run reports, and the support bundle. A **manual proxy URL embeds `user:password@host`**, and git/curl proxy failures can echo it — the one credible path for a secret to reach the state DB and exports. | **Fixed** (OBS-19), **corrected 2026-09-03**: the original rule required a `user:password` pair, so it did **not** redact `https://<token>@host` (the form GitHub's docs produce) or `http://user:@host` (what `ProxyProvider` itself builds when a proxy has a username and no stored password) — so "every stderr-derived failure string" overstated the coverage. Now a shared `SecretRedactor` scrubs all URL userinfo plus GitHub token shapes, applied in `GitWorkspace.Summarize`, `GitCommandRunner.RedactArguments` (which let a token-bearing remote URL through as a positional argument) and the support bundle. Note git 2.50.1 was measured to strip URL userinfo before echoing it, so the stderr route was not a live leak on current git — the verified exposures are argv, `.git/config` and the bundle. |
| SEC-3 | Low | Branch names beginning with `-` reach git as positionals (option-injection; self-inflicted config only, no shell involved — args use `ArgumentList`). | **Fixed** (2026-09-03). `GitRefName` now rejects a leading `-`, and the two entry points that had **no branch validation at all** — the Repository dialog's default branch and the job-config importer — apply the same rule as the wizard. Note a `--` separator cannot help here: it divides revisions from paths, so adding one would reinterpret the branch as a pathspec rather than protect it. |
| SEC-4 | ~~Low~~ **High** | `DestinationFolder` allowed `..` traversal outside the clone. **The original assessment was wrong**: it stated this was "not reachable from SQL-derived names, which are sanitized", but the *database name* is SQL-derived, was never sanitized, and was composed straight into the repository path — so a database named `..\..\Windows\Temp` redirected every write outside the workspace, as the service account. Object and schema names were sanitized; the database name was not. | **Fixed** (2026-09-03): database names are reduced to a single path component, and every write, copy and delete is now checked for containment against its root. The job-config importer — which bypassed the wizard's checks entirely — validates paths with the same shared rule. The earlier wizard-only fix (OBS-35) closed one route of several. |
| SEC-5 | Low | `DpapiSecretProtector` is registered but has no callers; README wording implied an app-level DPAPI path. | **Fixed** (2026-09-03): `DpapiSecretProtector`, `ISecretProtector` and the DI registration are deleted under the standing "no dead code" rule — nothing constructed, resolved or tested them. The README half of this finding was already stale: it now says plainly that Obsync implements no encryption of its own. |
| SEC-6 | Medium | The machine's git configuration is fully in effect for every git command, and `http.extraheader` was set **unscoped**. Measured, not inferred: a `url.<host>.insteadOf` entry silently rewrites the remote and the unscoped `AUTHORIZATION` header is then sent to the rewritten host on the first request, with the user seeing only "repository not found"; and five further keys execute programs — `core.hooksPath`, `core.fsmonitor`, `filter.*.smudge/clean`, `core.sshCommand` (via an ssh rewrite) and `protocol.ext.allow` (via an `ext::` rewrite). The strongest case needs no attacker at all: an internal mirror or proxy pushed by ordinary config management silently ships the customer's PAT to it. Bundling MinGit does **not** isolate us — `tools/git/etc/gitconfig` ends with an `include.path` of Git for Windows' system config. | **Fixed** (2026-09-03): the auth header is scoped to the remote's own URL prefix (measured: withheld from a rewritten host, still sent to the real one); `credential.helper` is cleared unconditionally rather than only when a token exists; and `core.hooksPath`, `core.fsmonitor`, `core.askPass` and a transport allow-list are forced on **every** invocation. `ext` and `ssh` are denied **by name**, because `protocol.<name>.allow` outranks the catch-all `protocol.allow` — with only the catch-all, an `ext::` remote still executed. Deliberately **not** full isolation: `GIT_CONFIG_NOSYSTEM` plus a private HOME would also close `filter.*`, but would discard `core.autocrlf` (changing every committed file's line endings on upgrade) and any corporate CA bundle or proxy. `filter.*` from machine config is the accepted residual. |
| SEC-7 | Medium | No git invocation had a timeout, and `GIT_TERMINAL_PROMPT=0` gates only the *terminal* fallback — askpass is consulted first. Measured: with `GIT_ASKPASS` set (VS Code sets it in every terminal it opens) a 401 blocks indefinitely; `commit.gpgsign=true` with a passphrase-protected key blocks on pinentry, which has no desktop under the service. The trigger is routine — an **expired or revoked PAT** is enough. A hung run held the job and repository locks with no bound at any layer, stayed `Running` because the orphan cleaner skips a held lock, and via Quartz's ten-thread pool starved jobs on unrelated repositories. | **Fixed** (2026-09-03): `GIT_ASKPASS`/`SSH_ASKPASS` removed from the child environment and `core.askPass` forced empty (measured: the same request now fails in about a second instead of blocking); `--no-gpg-sign` on commit; and a per-command timeout that terminates the process tree, reported as a failed run rather than a cancelled one. |
| SEC-8 | Low | A credential in a hand-set `RemoteUrl` reached git as a **command-line argument** — recorded verbatim by Windows process-creation auditing, the exact exposure the `GIT_CONFIG_*` design exists to prevent for the token — and was persisted by git into `.git/config`. Neither copy can be scrubbed after the fact. | **Fixed** (2026-09-03): `EffectiveRemoteUrl` strips userinfo before the URL is used. Obsync authenticates with an injected header, so nothing is lost. Note `RemoteUrl` still has no *validation* (see residual risks). |

## Verified secure (traced end-to-end, with runtime corroboration where noted)

**Secret storage.** All four secrets (SQL password, GitHub token, proxy password, SMTP password)
live exclusively in Windows Credential Manager (`CredWriteW`, per-user persistence), keyed by
namespaced identifiers. Config models carry only the key reference. After the OBS-20 fix, disabling
a feature no longer deletes its stored secret (matching the UI's "leave blank to keep the saved
one" promise).

**State database.** No table carries a secret column (V001–V013 reviewed); `app_settings` JSON
payloads for proxy/alerts exclude passwords by model shape. Runtime corroboration: the E2E battery
greps every committed file for the credential-store token — zero hits (S01).

> **Corrected 2026-09-03.** This section read as though shipping MinGit isolated Obsync from the
> machine's git configuration. It does not: `tools/git/etc/gitconfig` ends with an `include.path` of
> Git for Windows' system config, so machine-wide settings apply in full — see SEC-6. The header was
> also **unscoped**, so a `url.*.insteadOf` rewrite delivered it to another host. Both are fixed.

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

**Path safety.** Object and schema names are sanitized (invalid chars replaced, length-capped,
collision suffixed with a stable hash) — verified live with hostile names (space, unicode, `]`,
120+ chars) in E2E S01. Atomic writes (`.obsync-tmp` + rename) with the temp pattern excluded from
`git add`.

> **Corrected 2026-09-03.** This section previously said "SQL-derived names are sanitized" without
> qualification. That was true of object and schema names but **not** of the database name, which
> was composed into the repository path raw — the omission behind SEC-4's incorrect Low rating.
> Database names are now sanitized to a single path component, every write/copy/delete is
> containment-checked against its root, and the job-config importer validates paths with the same
> rule the wizard uses. Names matching Windows device names (`CON`, `NUL`, `COM1`-`COM9`,
> `LPT1`-`LPT9`) are also escaped — a file named `CON.sql` made `git add` fail outright.

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
- ~~Git has **no per-command timeout**~~ — **fixed 2026-09-03** (SEC-7). Every git command is now
  bounded (10 minutes network, 2 minutes local) and the process tree killed on expiry. The earlier
  wording also understated it: nothing fired the cancellation token on a timer, so a hung command was
  bounded by *nothing*, and the run row stayed `Running` because the orphan cleaner skips a run whose
  lock is still held.
- The **support bundle includes recent app/service logs**; log content is secret-free by
  construction (above), but operators shipping bundles to third parties should still review them —
  server names, database names, and object names are present by design.

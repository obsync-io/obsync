# Obsync — Enterprise Capability Roadmap

**Date:** 2026-07-16 · **Assessed against:** `main` @ `a917680` (v0.8.3) · **Status:** Roadmap only — nothing in this document ships code. It exists so that when an enterprise capability is requested, the sequencing, prerequisites, and honest constraints are already worked out.

This is the roadmap referenced by `IMPROVEMENT_ASSESSMENT.md` §28 (AI-assisted features) and §29 (Enterprise capabilities).

## Guiding principle: the OSS desktop experience is the product

Obsync's identity is a **single-user, read-only, no-telemetry Windows desktop tool** whose entire complexity budget is spent on one concept (the Sync Job). Every capability below is evaluated against one hard rule:

> **Enterprise features must not leak complexity into the OSS desktop experience.** No new required configuration, no new nav sections, no auth prompts, no degraded first-run for the user who installs the MSI and syncs one database to github.com.

Where a capability genuinely requires central infrastructure (SSO, RBAC, policy distribution), this document says so honestly rather than shipping a desktop-shaped imitation of it.

Effort scale: **S** ≤ 1 week · **M** ≤ 1 month · **L** ≤ 1 quarter · **XL** > 1 quarter. Confidence reflects how well the touchpoints are already understood from the codebase.

---

## Phase 1 — GitHub Enterprise Server (GHES)

**Value:** The single most-requested "enterprise" gap, and the cheapest. Many regulated SQL Server shops run GHES on-premises; today the README lists "GitHub.com only" as a known limit, and this is the difference between "cannot evaluate Obsync at all" and "works day one."

**Why it is lowest-friction:** the architecture is already almost host-neutral.

- The git layer (`src/Obsync.Git`) shells out to the git CLI and is **fully host-agnostic** — clone/fetch/commit/push work against any HTTPS remote today. Authentication is a generic basic-auth header injected per command via `GIT_CONFIG_*` environment variables, not a github.com-specific mechanism.
- Octokit.NET (the only GitHub API client, in `src/Obsync.GitHub/GitHubService.cs`) supports a custom base address: `new GitHubClient(new Connection(product, baseAddress, adapter))` pointed at `https://ghes.example.com/api/v3/`.

**Actual touchpoints (enumerated from the code):**

| Touchpoint | Location | Change |
|---|---|---|
| API client creation | `GitHubService.CreateClientAsync` — builds `new GitHubClient(new Connection(Product, adapter))`, which defaults to `https://api.github.com` | Accept a per-repository-profile API base URL and pass it to the `Connection` |
| Commit web URL | `GitHubService.BuildCommitUrl` — hardcodes `https://github.com/{owner}/{name}/commit/{sha}` | Derive the web host from the profile (GHES web URL = `https://<host>/…`, API = `https://<host>/api/v3`) |
| Blob web URL | `GitHubService.BuildBlobUrl` — hardcodes `https://github.com/{owner}/{name}/blob/…` (used by "Open on GitHub" in the Job Workspace and diff viewer) | Same derivation |
| Default remote URL | `RepositoryProfile` (`src/Obsync.Shared/Models/ConnectionProfiles.cs`) — falls back to `https://github.com/{Owner}/{RepositoryName}.git` when `RemoteUrl` is empty | Fall back from the profile's host instead; existing profiles keep the github.com default |
| Proxy/diagnostics probes | `src/Obsync.Data/ProxyProvider.cs` — pins `https://api.github.com` / `https://github.com` for proxy resolution and reachability checks | Resolve against the profile's host so diagnostics test the endpoint that runs actually use |
| Update checker | `src/Obsync.App/Services/UpdateChecker.cs` — contacts `api.github.com/repos/obsync-io/obsync/releases/latest` | **Unchanged by design** — this is the product's own release feed, not the sync target. It must remain independently toggleable (see Offline/air-gapped) so GHES-only networks don't see a failed check |

**Prerequisites:** a `HostBaseUrl` (or equivalent) field on the repository profile + migration; validation that the fine-grained-PAT permission probe (`CheckRepositoryAccessAsync`) behaves identically on GHES (classic PATs are more common there); one GHES test instance for verification.

**What stays simple in OSS:** the field defaults to github.com and never appears in the wizard unless the user opens an "Advanced" reveal on the repository profile. No behavior change for existing users.

**Effort:** S–M (high confidence — the touchpoints above are exhaustive; the risk is GHES API version drift, mitigated by Octokit's own GHES support).

---

## Phase 2 — Additional git hosts: GitLab, Azure DevOps, Bitbucket

**Value:** Opens the large population of enterprises standardized on a non-GitHub host. The git plumbing already works against all of them; only the API surface differs.

**Prerequisite — the `IGitHost` seam.** Today `IGitHubService` is consumed directly by the engine and view models. Before a second provider is added, the *actually used* operations must be extracted into a provider-neutral interface. The current API usage is small and well-bounded:

```
interface IGitHost
{
    Task<Result<TokenPermissionReport>> CheckRepositoryAccessAsync(...)   // token valid, repo visible, read, write
    Task<Result<IReadOnlyList<HostRepository>>> GetRepositoriesAsync(...) // wizard picker
    Task<Result<IReadOnlyList<string>>> GetBranchesAsync(...)             // branch validation
    Task<Result<PullRequestInfo>> CreatePullRequestAsync(...)             // PR mode (MR in GitLab, PR in ADO/Bitbucket)
    string BuildCommitUrl(...); string BuildBlobUrl(...)                  // "Open on <host>" links
    string BuildAuthorizationHeader(string token)                        // git-over-HTTPS auth (host-specific username conventions)
}
```

The engine already treats "open a PR" and "push a branch" as the only host interactions during a run, so the seam is narrow. Per-host quirks that must live behind it: GitLab merge requests + `oauth2`/`PRIVATE-TOKEN` auth; Azure DevOps organization/project/repo triple (not owner/name) and PAT-as-basic-auth; Bitbucket workspace/repo and app passwords; each host's retry/rate-limit semantics (the current `WithRetryAsync`/`IsTransient` logic is Octokit-typed and must be generalized).

**What stays simple in OSS:** one "Host" selector on the repository profile; every downstream screen keeps saying "repository," "branch," "pull request" with host-appropriate labels. No plugin system — providers compile in.

**Effort:** M for the seam + first additional provider (GitLab recommended: closest PR model), S–M per subsequent provider. Confidence medium — API mapping is known territory, but token-permission probing differs meaningfully per host.

---

## Phase 3 — Central control plane capabilities

Everything in this phase shares one honest precondition: **a single-user desktop app has no auth boundary.** Obsync's identity model is the Windows account — the audit trail (`AuditWriter` → append-only `audit_log`, actor = Windows identity) and Credential Manager secrets are all per-user by construction. Capabilities that promise "roles," "policies," or "central visibility" are only truthful with a central component (a small server, or a shared store the organization controls). None of these ship as desktop-local simulations.

### 3.1 SSO & RBAC

- **Honest assessment:** there is nothing to enforce against on the desktop. The app runs as one Windows user, reads that user's credential vault, and writes that user's SQLite database. "Log in with Okta" on a local tool would gate nothing that the user doesn't already own. This was evaluated and explicitly rejected as a desktop feature once before (5-tier RBAC proposal, 2026-07).
- **When it becomes real:** with a central control plane (3.2), SSO authenticates *to the control plane* and RBAC governs *central* actions — who may edit shared job definitions, approve policy changes, or view org-wide run history. The desktop remains Windows-identity-based.
- **Prerequisites:** control plane (3.2) first. **Effort:** XL (low confidence until the control plane exists). **OSS guard:** the desktop app never grows a login screen.

### 3.2 Central configuration & multi-user / multi-machine

- **Value:** today jobs are portable only via secret-free export/import JSON (`JobConfigPorter`). Teams running Obsync on several machines want one source of truth for job definitions, production-tag lists, alert settings, and proxy settings.
- **Options, in ascending ambition:**
  1. **File-share config store (S–M):** an org-managed UNC path holding exported job/settings JSON that the app can *pull* from (one-way, read-only, secrets always local). Cheap, air-gap friendly, no server. Fits the existing porter format.
  2. **Git-backed config (M):** the same JSON versioned in a repository — configuration changes get review and history for free, matching the product's own philosophy.
  3. **Control-plane service (XL):** a server holding job definitions, receiving run results, hosting RBAC/SSO/policy. This is a different product tier and should be a separate, non-OSS-burdening deliverable.
- **Hard constraint:** secrets never centralize through Obsync — SQL passwords and PATs stay in each machine's Credential Manager; a shared definition references credentials by name, exactly as import does today.
- **OSS guard:** all three options are additive; a standalone install never contacts any store.

### 3.3 Organization policies, protected environments, approval workflows

- **Honest assessment:** **PR mode is the approval workflow today**, and it is the right one — the approval happens where the reviewers already work (GitHub branch protection, required reviewers, CODEOWNERS). Obsync opens the PR (`CreatePullRequestAsync`, optional requested reviewers) and never merges. The production-tag safeguard (red chip + Run Now confirmation) is the local guardrail.
- **Roadmap:** document "protected environment" recipes (production-tagged jobs must use PR mode + branch protection) before building anything. A locally *enforced* "jobs tagged `prod` may only use PR mode" rule is a small, honest addition (S) because it constrains the user's own configuration rather than pretending to be an org policy. True org-mandated policy requires 3.2/3.4.
- **Effort:** S (local rule) / inherits XL (org-enforced). **OSS guard:** recipes and an optional local rule only.

### 3.4 Policy-as-code

- **Value:** security teams want reviewable, versioned rules: "PR mode required for prod," "signed commits required," "server-level scripting requires approval," "retention ≥ 1 year."
- **Approach:** a declarative policy file (JSON/YAML) validated at job save and run start — distributed the same way as 3.2 config (file share or git). Precedent exists in-product: `.obsyncignore` is already a versioned, file-based control read every run.
- **Prerequisites:** a stable policy vocabulary (commit modes, tags, retention, signing) and 3.2 distribution. **Effort:** M after 3.2 (medium confidence). **OSS guard:** no policy file present = no behavior change.

### 3.5 Audit retention & SIEM integration

- **Current state:** an append-only audit trail already exists (`src/Obsync.Data/Repositories/AuditWriter.cs` → `audit_log`: UTC timestamp, Windows actor, action, entity, detail; runs stamped with `triggered_by`) with full CSV/JSON export from Settings. Tamper-evident hash-chaining in local SQLite was evaluated and rejected as theater — the user owns the file; forwarding is the honest integrity boundary.
- **Roadmap (ascending):**
  1. **JSON Lines audit export/stream (S):** append each audit event to a rotating `.jsonl` file alongside the logs — every SIEM collector (Splunk UF, Elastic agent, Sentinel AMA) can tail a file. Highest value per unit effort.
  2. **Windows Event Log channel (S):** the installer already registers an "Obsync" event source for service logging; writing audit events there feeds WEF/Sentinel with zero new infrastructure.
  3. **Syslog (RFC 5424) / webhook forwarding (M):** reuse the alert plumbing (proxy-aware webhook already exists) for push delivery.
  4. **Retention controls (S):** the run-history retention pattern (30/90/180/365/forever + startup/daily pruning) applied to `audit_log`, default forever.
- **OSS guard:** all off by default; the in-app "Recent activity" view is unchanged.

### 3.6 Centralized alerting

- **Current state:** per-machine SMTP + generic webhook alerting already exists (failed/warning/changes triggers, scheduled-only filter, proxy-aware, best-effort).
- **Roadmap:** first, ship **payload presets** for common receivers (Teams Adaptive Card, Slack blocks, PagerDuty Events) on the existing webhook — S, no new transport, already assessed as a P2 item. True *centralized* alerting (org-wide dedupe, routing, escalation) is a control-plane feature (3.2/XL) and should not be imitated locally.
- **OSS guard:** presets are a dropdown on the existing Alerts card.

### 3.7 Service account management

- **Current state:** the MSI installs the Windows Service with a `SERVICE_ACCOUNT`/`SERVICE_PASSWORD` (or gMSA) at install time (`packaging/Obsync.wxs`, wizard Service Account step, `packaging/INSTALL.md` covers gMSA and "Log on as a service"); scheduler-health banners detect account mismatch end-to-end via the DB heartbeat.
- **Roadmap:** (a) an in-app "Fix service account" assist on the scheduler warning — re-configure the service logon (elevated) instead of sending users to `services.msc` (S–M; UAC flow is the main cost); (b) first-class gMSA verification in Diagnostics (`Test-ADServiceAccount` equivalent) (S); (c) documented rotation runbook for the service account and PATs (S, docs only). Central *inventory* of service accounts is a control-plane concern.
- **OSS guard:** the current per-machine flow remains the default and is already complete for single-machine use.

---

## Standalone tracks (no control plane required)

### Signed commits

- **Value:** provenance — proof that repository history came from the Obsync service identity, required by some regulated environments and enforceable via GitHub branch protection ("require signed commits").
- **Approach:** git-native signing configured per job or globally: SSH signing (`gpg.format=ssh`, `user.signingKey`, `commit.gpgSign=true`) is the practical choice — no GPG keyring dependency, and the bundled MinGit supports it. Injected per command via the existing `GIT_CONFIG_*` environment mechanism, so nothing lands in `.git/config`.
- **Service identity constraints (honest):** scheduled runs sign as the *service account*, so the signing key must live where that account can read it (its own profile/credential store — the same per-user constraint as all other secrets, and the same warning surface applies). Key passphrases are unusable non-interactively; keys must be passphrase-less and ACL-protected, and the docs must say so. GitHub shows "Verified" only if the public key is registered on the account that owns the committer email.
- **Effort:** M (medium-high confidence — config injection plumbing exists; the work is key management UX + docs). **OSS guard:** off by default; one Settings card.

### Offline / air-gapped operation

- **Value:** classified and OT networks. Obsync is unusually close already: the MSI is zero-prerequisite (self-contained .NET, bundled SHA256-pinned MinGit — no runtime or git install to mirror inside the gap), there is no telemetry, and Local Commit Only / Export Only modes never touch a remote.
- **Remaining gaps:** (a) a **single "offline mode" toggle** that disables the update check (today it is best-effort and throttled, but it still attempts `api.github.com`) and all GitHub reachability probes in Diagnostics so an air-gapped install shows a clean bill of health instead of expected failures; (b) documentation for the internal-remote pattern (direct commit to an internal GHES/GitLab — lands with Phases 1–2); (c) a documented offline update path (download MSI outside, verify hash, install).
- **Effort:** S (high confidence). **OSS guard:** one toggle; default behavior unchanged.

### Proxy & custom certificate support

- **Current state:** proxy support is **done** — None/System/Manual for all GitHub traffic (Octokit via `HttpClientAdapter`, git via env-injected `http.proxy`, update checker, webhooks), credentials in Credential Manager, proxy diagnostics row.
- **The gap is custom CAs:** TLS-inspecting proxies and internal GHES/GitLab instances present certificates from a private CA. .NET's `HttpClient` trusts the Windows machine store (usually fine once the org pushes its root via GPO), but **bundled MinGit uses its own `ca-bundle.crt`, not the Windows store** — so git operations fail with `SSL certificate problem` even when the API calls succeed. There is no `http.sslCAInfo`/custom-trust handling anywhere in the codebase today.
- **Approach:** configure git with `http.sslBackend=schannel` (uses the Windows certificate store, matching `HttpClient` behavior) via the existing per-command config injection — likely the entire fix; offer an explicit CA-bundle path setting as the escape hatch. Never offer "disable TLS verification."
- **Effort:** S (high confidence; the risk is edge cases in schannel + client-cert proxies). **OSS guard:** invisible unless it fixes someone.

### Package deployment (MSI, winget, Intune)

- **Current state:** a per-machine MSI with full silent-install support (`SERVICE_ACCOUNT`, `INSTALLFOLDER`, `/qn`, verbose logging, gMSA) already exists — that is the hard part, and it is done. `packaging/INSTALL.md` is already an enterprise deployment guide.
- **Roadmap:** (a) **winget** manifest (S — requires a *signed* MSI in practice, so it queues behind SignPath); (b) **Intune** packaging notes (S, docs only — the MSI is LOB-app ready; document the service-account property and detection rules); (c) SCCM/GPO notes fall out of the same properties. MSIX is **rejected** — the app installs a Windows Service, which MSIX cannot do without workarounds that break the zero-surprise install.
- **Effort:** S total (high confidence). **OSS guard:** none needed — distribution only.

### Auto-update governance

- **Current state:** deliberately notify-only — the update checker contacts one GitHub releases endpoint at most daily, never auto-installs, sends nothing but the request (README documents this). That default is itself an enterprise feature.
- **Roadmap:** (a) a **"disable update checks" setting** (part of the offline toggle) for environments where even the outbound check is unwanted; (b) an admin-controllable machine-wide override (registry/GPO ADMX or an `appsettings` key the MSI can stamp) so fleet operators can enforce it — S; (c) if auto-update is ever built, it must be opt-in, signed-artifact-only (SignPath), and deferrable — but managed fleets already have Intune/winget for that, so building a private updater is **deprioritized** (organizations distributing software centrally do not want apps updating themselves).
- **Effort:** S for (a)+(b). **OSS guard:** current behavior is already the calm default.

---

## AI-assisted features (restating the preconditions — nothing ships)

Per `IMPROVEMENT_ASSESSMENT.md` §28: no secure LLM provider abstraction exists in the codebase, and every precondition below is currently unmet. Candidate features (schema-change summaries in commit messages, security-review explanations, migration-assessment narratives) are plausible *later*, but only under **all** of the following, without exception:

1. **Opt-in** — AI features are off by default; enabling them is an explicit, per-feature user action. No dark defaults.
2. **Disclosure** — every AI-generated artifact is labeled as such (in the UI and in any committed file), and the user is told exactly what data leaves the machine before the first call.
3. **Redaction** — schema metadata may be sensitive; reference-data rows, connection details, and anything matching the product's existing secret-handling rules never leave the machine. A reviewable redaction layer sits in front of any provider call.
4. **Audit** — every AI invocation is an audit event (what feature, what data category, which provider/model), riding the existing `audit_log`.
5. **BYOK** — the user supplies their own provider endpoint and key (stored in Credential Manager like every other secret); Obsync ships no key, proxies no traffic, and adds no vendor account. Air-gapped users can point at an internal endpoint or use nothing.
6. **The non-AI product is fully usable** — no feature may degrade, gate, or nag when AI is disabled. AI summarizes and explains; it never becomes the only way to do something, and it never writes to a database (read-only positioning is non-negotiable).

These conditions protect the product's core identity — read-only, no-telemetry, secrets-never-leave-Windows — which is worth more to Obsync's enterprise audience than any individual AI feature.

---

## Sequencing summary

| Order | Capability | Effort | Confidence | Central plane needed? |
|---|---|---|---|---|
| 1 | GitHub Enterprise Server | S–M | High | No |
| 2 | Custom CA support (git schannel) | S | High | No |
| 3 | Offline/air-gap toggle + update-check governance | S | High | No |
| 4 | winget/Intune packaging notes | S | High | No |
| 5 | Audit JSONL/Event Log export + retention | S–M | High | No |
| 6 | `IGitHost` seam + GitLab | M | Medium | No |
| 7 | Signed commits | M | Medium-High | No |
| 8 | Azure DevOps, Bitbucket | S–M each | Medium | No |
| 9 | Alert payload presets | S | High | No |
| 10 | Shared config store (file/git) + local policy rules | M | Medium | No |
| 11 | Policy-as-code | M | Medium | Partially |
| 12 | Control plane (central config/visibility, SSO, RBAC, org policy, centralized alerting) | XL | Low | Yes — separate deliverable |

Items 1–9 are all achievable without violating the OSS-simplicity rule and without any central infrastructure. Item 12 is a different product tier and must be planned as one, not accreted into the desktop app.

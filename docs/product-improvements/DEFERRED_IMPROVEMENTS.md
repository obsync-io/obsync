# Obsync — Deferred Improvements

Companion to [IMPROVEMENT_ASSESSMENT.md](IMPROVEMENT_ASSESSMENT.md). Every requested improvement that did **not** ship in v0.9.0, with the reason, prerequisites, and the recommended release. ("Rejected" items are listed last — they are not planned at all.)

## Deferred (planned)

| Improvement | Reason deferred | Prerequisites | Recommended release |
|---|---|---|---|
| Notification center (persistent, unread state) | Toasts + email/webhook alerts + the new dashboard Needs-attention panel already surface everything actionable; a persistent center risks duplicating History | Evidence that toasts are being missed; a read/unread store | 0.10+ |
| Background activity center | Per-job Running badges, indeterminate progress + Cancel already exist; a global panel is polish, not trust | An operation registry service shared by all long-running commands | 0.10+ |
| Global search (Ctrl+K) | Differentiation, not production trust; kept the release focused on P0/P1 | Local search index over jobs/servers/repos/runs/object states (all already in SQLite) | 0.10 |
| Command palette (Ctrl+Shift+P) | Brief itself ranks it below production correctness | Global search foundation (shared index + navigation actions) | 0.10+ |
| Service-side connection probe ("does the service account reach this server?") | A true test requires code running under the service identity; faking it would violate the no-unsupported-claims rule. v0.9.0 ships the honest version: heartbeat-account health + wizard identity caption | A probe RPC hosted by the Windows Service (named pipe) + result surfacing | 0.10 |
| Settings dirty-state / unsaved-changes warning | Per-section Save already minimizes blast radius; a monolithic dirty-tracking rework is churn without evidence of loss | Split SettingsViewModel per section first | 0.10+ |
| Clean-unused-workspaces action | Deleting workspace clones safely requires job-to-workspace liveness accounting; sizes + Open-folder shipped instead | Workspace ownership ledger; confirmation flow | 0.10 |
| Schema-filter tokenized editor + live schema validation | Free text works and is validated for parse; live validation needs a mid-wizard connection round-trip | Cached schema list per connection | 0.10+ |
| Grouped Custom object-type selector | Cosmetic; the flat checklist is complete and alphabetical | None (pure UI) | 0.10+ |
| Persisted failure stage + separate warning/skip counts on runs | Schema change with low display value — the Logs tab already itemizes each skip with reasons | Run-table migration; engine stage plumbing | 0.11+ |
| Vendor alert presets (Teams, Slack, PagerDuty, ServiceNow) | Generic webhook is the stable foundation (brief's own guidance); vendor payloads need per-vendor testing | Webhook auth/secret header support via Credential Manager; template abstraction | 0.11+ |
| Webhook auth header / HMAC signing | Needs a secret-storage + redaction design of its own | Credential-Manager-backed webhook secrets | 0.11 |
| Dark mode (System/Light/Dark) | Brief's own precondition list (production safety, diagnostics, accessibility) was exactly this release's scope; a partial dark mode is worse than none | Theme-swap mechanism over `Themes/Colors.xaml`; diff/editor palette audit; contrast verification | 0.11+ |
| Object explorer | Differentiation; Dependencies tab already covers impact analysis | Drift/compare metadata surface (shared browsing UI) | 0.12+ / with drift module |
| Dependency analysis depth (cross-DB, graphs) | v1 (one-level Uses/Used-by) shipped earlier; deeper analysis must label inferred/incomplete edges honestly | Dependency capture at scripting time; dynamic-SQL caveat model | 0.12+ |
| Drift detection & environment comparison module | Distinct product surface; architecture proposal shipped instead ([DRIFT_MODULE_PROPOSAL.md](DRIFT_MODULE_PROPOSAL.md)) | Proposal sign-off; baselines schema | dedicated 0.12 track |
| Schema compare module | Same engine as drift (covered in the same proposal); analysis-only v1 | Drift module v1 | after drift v1 |
| Validation of existing access (permission generator) | Requires querying a target login's effective permissions — new read surface, needs its own tests | `sys.fn_my_permissions`/impersonation-free effective-permission query design | 0.11 |
| Migration assessment module | Proposal shipped ([MIGRATION_ASSESSMENT_PROPOSAL.md](MIGRATION_ASSESSMENT_PROPOSAL.md)); building a shallow analyzer would produce unreliable claims | Per-construct rule sets + fixture databases; PoC per the proposal | P3 (post-1.0 track) |
| AI-assisted features | No secure provider abstraction; every precondition (opt-in, disclosure, redaction, audit, BYOK) unmet | See ENTERPRISE_ROADMAP.md §AI | P3 |
| Enterprise platform (GHES, GitLab/ADO, SSO/RBAC, policy, air-gap) | Roadmap shipped ([ENTERPRISE_ROADMAP.md](ENTERPRISE_ROADMAP.md)); GHES is the first increment | Per roadmap phasing | P3, GHES first |
| PDF reports | No reliable PDF engine in the stack; HTML reports print cleanly | A vetted PDF library decision | not scheduled |

## Rejected (with reason)

| Improvement | Reason |
|---|---|
| "Restore" / execute historical scripts from the diff viewer | Violates read-only-by-design; copy/export of historical versions already exists |
| Full 14-metric dashboard | Clutter for a single-user desktop tool; Needs-attention section answers "is action required?" without a monitoring wall |
| Overlap-policy picker (queue / cancel-previous) | Engine-wide skip-if-running is the safe default and is now documented in the UI; the alternatives are speculative complexity |
| "Protected environment tags" rename | "Production tags" is clearer and consistently used across UI + docs |
| "Use system Git configuration" identity option | The desktop app and the Windows Service would silently resolve different configs |
| Fake PR-permission pre-check | The GitHub API cannot report PR-write permission without creating a PR; documented honestly instead |
| Dedicated full-screen diff mode | The window is resizable/maximizable; OS maximize suffices |

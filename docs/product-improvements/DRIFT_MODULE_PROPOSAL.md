# Obsync — Drift Detection & Environment Comparison Module (Proposal)

**Date:** 2026-07-16 · **Status:** Proposal (no code ships with this document) · **Assessed against:** `main` @ `a917680` (v0.8.3 + performance pass) · **Companion to:** `IMPROVEMENT_ASSESSMENT.md` §24 (drift detection) and §25 (schema compare).

This document is the architectural proposal called for by §24 of the improvement assessment. It covers **both** the Drift Detection module (baseline-anchored, eventually schedulable) and the interactive **Schema Compare** module (§25) — they are the same comparison engine with two entry points: one anchored to a stored baseline, one run ad hoc between any two sides. Every architectural claim below is grounded in the current codebase, with file and class names cited.

---

## 1. Positioning & scope

**A distinct module, not a Sync feature.** The existing Sync pipeline (`SyncEngine`, `src/Obsync.Engine/SyncEngine.cs`) answers one question: *"what changed since my last run, and commit it."* Drift answers a different question: *"how do these two things differ, and should I be worried?"* — and it must be able to compare pairs the sync engine never sees (two live databases; a database against a six-month-old commit). Folding it into the Sync page would conflate "pending commit" with "environment drift" and force the engine's write/commit machinery into read paths where it has no business. Drift gets its own nav section, its own persistence, and its own run lifecycle.

**Read-only, always.** The module executes only the catalog `SELECT`s the existing scripting providers already run (`MetadataScriptProvider` reads `sys.sql_modules`/catalog views; `SmoScriptProvider` scripts via SMO), reads git objects via the existing `GitCommandRunner` (`git show`, `git ls-tree` — the same local-workspace-only pattern `ScriptHistoryService` uses today), and writes nothing to any database, any repository, or any remote. No `INSERT`/`UPDATE`/`DDL` against SQL Server, no commits, no pushes. Its only writes are to Obsync's own SQLite state database and a local evidence folder.

**Analysis only in v1 — no deployment scripts.** The module reports differences; it does not generate `ALTER`/sync scripts to reconcile them. Deterministic *detection* is a solved problem in this codebase; deterministic *migration script generation* (dependency ordering, data-preserving table rebuilds, permission replay) is an order of magnitude harder and is exactly where competing tools destroy data. Per the working rule of no unsupported claims: nothing ships that could be mistaken for a deployment tool. "Open the two scripts side by side and act deliberately" is the v1 remediation story.

**What the module reuses (and why the proposal is cheap relative to its value):** the entire scripting stack (`IObjectScriptProvider` hybrid, `IScriptNormalizer`, `IObjectHasher`, `IObjectFilePathMapper`), the tracked-state model (`TrackedObjectState` in `src/Obsync.Shared/Models/SyncRun.cs`), the deterministic repository layout (`SqlObjectTypeCatalog` folder names, `ObjectInventoryWriter` manifests), the diff viewer (`DiffRowMapper` + `ScriptDiffWindow`), the report writers (`RunReportWriter` HTML/CSV/JSON pattern), the alert channels (`RunAlertService`), and the E2E harness (`tools/Obsync.E2E`). The module adds *comparison orchestration and classification* on top of proven primitives — it does not add a second scripting engine.

---

## 2. User workflows

### W1 — Database vs Git baseline (v1, the core workflow)

*"Has anyone changed production outside of source control?"* The user picks a job (which fixes the server, databases, selection profile, repository, and layout), picks a commit (default: the branch head; alternatively any commit — a git commit **is** a baseline, §3), and runs the comparison. The module scripts the live database through the same providers the job uses, normalizes and hashes each object exactly as `SyncEngine.ApplyItemAsync` does, and compares against the object set at that commit. Output: a findings list (Added / Modified / Deleted / Unknown, §4) with per-object evidence and drill-down diffs.

### W2 — Database vs database (v2)

*"Is QA actually what we're about to test against prod?"* The user picks two connection profiles + database pairs (dev/QA/UAT/prod in any combination) and one selection profile to apply to **both** sides (§5 explains why one shared profile is mandatory). Both sides are scripted live through the identical pipeline; hashes are compared; mismatches get line diffs. No job, no repository, no tracked state involved — this is the "Schema Compare" entry point of §25.

### W3 — Baseline vs current baseline (v1, nearly free)

*"What drifted between the release-tag commit and today's head?"* Both sides are git commits in the same repository. No SQL connection at all — the comparison runs entirely against the local workspace clone (`ObsyncEngineOptions.WorkspacesRoot`), using per-commit object manifests or file hashing (§3). This is the cheapest comparison the module offers and doubles as the "what did the last N sync runs change in aggregate" view.

### W4 — Scheduled drift scan with alerting (v3)

A drift *scan definition* (job-shaped: comparison pair + cadence) runs unattended through the existing scheduler stack (`Obsync.Scheduler` Quartz jobs, `SyncQuartzJob` pattern, the Windows service host). A scan that finds new drift — findings not present in the previous scan of the same definition — fires the existing alert channels (`RunAlertService`: SMTP + generic webhook, 15 s send timeout, proxy-aware) with a drift-specific payload. Deliberately last in the phasing: unattended verdicts demand the false-positive discipline of §5 to be proven in interactive use first, and the audit's history with the scheduler (OBS-00) argues for shipping scheduled anything only with E2E service coverage.

### W5 — On-demand schema compare with exportable report (v1 for DB-vs-Git, v2 for DB-vs-DB)

Every comparison's findings can be exported as HTML/CSV/JSON, following the exact conventions of `RunReportWriter` (`src/Obsync.App/Services/RunReportWriter.cs`): version/date headers, secret-free, self-contained HTML that prints fine (the assessment already rejected PDF engines). The report includes the comparison's identity block — both sides, effective selection, timestamps, tool version — so it can stand alone as an audit artifact.

Explicitly **not** workflows: reconciling drift automatically, "sync this object back," or three-way merge. Out of scope for this module entirely, not just v1 (see §7).

---

## 3. Data model — baselines and findings

### A git commit IS a baseline

The sync engine already produces, on every run, a deterministic normalized script per object (`ScriptNormalizer` guarantees byte-identical output; the file write and the SHA-256 hash consume the same UTF-8 bytes — see the encode-once comment at `SyncEngine.ApplyItemAsync`) and records the lowercase-hex SHA-256 in `TrackedObjectState.LastHash`. When `IncludeObjectInventory` is on (default in `ObjectSelectionProfile`), each commit also contains `metadata/object-inventory.json` — a sorted, timestamp-free manifest of every tracked object with its type, schema, name, repo path, and hash (`ObjectInventoryEntry` in `src/Obsync.Shared/Scripting/ObjectInventory.cs`).

That means a commit is **self-describing**: to know "what did the database look like at commit X," the module reads one JSON file from git — no re-hashing, no checkout. Fallback when the manifest is absent or the job disabled it: enumerate the commit's tree (`git ls-tree -r`), read each `.sql` blob (`git show <sha>:<path>` — the retrieval `ScriptHistoryService.GetVersionsAsync` already performs), normalize with `NormalizationOptions.Default`, and hash with `Sha256ObjectHasher`. Object identity is recovered from the path via the inverse of `ObjectFilePathMapper` conventions (`<type-folder>/<schema>.<name>.sql` under the job's database folder); path-mangled names (the mapper's sanitization suffix) resolve through the manifest when present and are reported as `Unknown — identity unrecoverable from path` when not.

A "baseline" is therefore mostly a **named pointer**, not a copy:

```sql
-- V013__drift_module.sql (sketch — follows the existing Obsync.Data/Migrations conventions)

CREATE TABLE drift_baselines (
    id                     TEXT PRIMARY KEY,           -- GUID
    name                   TEXT NOT NULL,              -- "prod release 2026-07"
    kind                   INTEGER NOT NULL,           -- 0 = GitCommit (v1's only kind)
    job_id                 TEXT NULL,                  -- interprets layout/selection; NULL for ad hoc
    repository_profile_id  TEXT NOT NULL,
    branch                 TEXT NOT NULL,
    commit_sha             TEXT NOT NULL,
    selection_json         TEXT NOT NULL,              -- effective ObjectSelectionProfile at capture (§5)
    created_at             TEXT NOT NULL,
    created_by             TEXT NOT NULL,              -- DOMAIN\user, like SyncRun.TriggeredBy
    notes                  TEXT NULL
);

CREATE TABLE drift_comparisons (
    id                 TEXT PRIMARY KEY,               -- GUID
    kind               INTEGER NOT NULL,               -- 0 DbVsGit · 1 DbVsDb · 2 GitVsGit
    left_descriptor    TEXT NOT NULL,                  -- JSON: side identity (server+db, or repo+sha)
    right_descriptor   TEXT NOT NULL,
    selection_json     TEXT NOT NULL,                  -- the ONE effective selection applied to both sides
    status             INTEGER NOT NULL,               -- mirrors RunStatus semantics incl. Warning
    started_at         TEXT NOT NULL,
    completed_at       TEXT NULL,
    objects_compared   INTEGER NOT NULL DEFAULT 0,
    findings_total     INTEGER NOT NULL DEFAULT 0,
    findings_unknown   INTEGER NOT NULL DEFAULT 0,     -- surfaced separately, never buried (§5)
    error_message      TEXT NULL,
    triggered_by       TEXT NULL
);

CREATE TABLE drift_findings (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    comparison_id  TEXT NOT NULL REFERENCES drift_comparisons(id) ON DELETE CASCADE,
    database_name  TEXT NOT NULL COLLATE NOCASE,       -- identity columns NOCASE, matching V011
    object_type    INTEGER NOT NULL,                   -- SqlObjectType
    schema_name    TEXT NOT NULL COLLATE NOCASE,
    object_name    TEXT NOT NULL COLLATE NOCASE,
    category       INTEGER NOT NULL,                   -- §4 classification
    aspect         INTEGER NOT NULL,                   -- 0 Definition · 1 Permission · 2 DatabaseOption · 3 Artifact · 4 ReferenceData
    left_hash      TEXT NULL,                          -- evidence: BOTH hashes always carried (§5)
    right_hash     TEXT NULL,
    left_absent_reason  TEXT NULL,                     -- why a side has no script, when it doesn't
    right_absent_reason TEXT NULL,
    detail         TEXT NULL                           -- e.g. "case-only name difference: dbo.Foo vs dbo.FOO"
);
CREATE INDEX ix_drift_findings_comparison ON drift_findings(comparison_id, category);
```

**Evidence scripts are files, not rows.** Mismatched objects' normalized scripts are written under a per-comparison evidence folder inside the Obsync data root (sibling of `workspaces/`, resolved via `ObsyncPaths`) and referenced by convention (`<comparison-id>/<side>/<repo-relative-path>`). Storing full scripts in SQLite would repeat the mistake the engine already caps with `MaxPersistedChanges = 50_000` — the largest SQLite write in the system for detail no view can show. Findings rows are capped at the same order of magnitude; beyond the cap the comparison stores counts plus the exported report only, and says so in its log.

**Optional pinned snapshots (later, if ever):** a `kind = PinnedSnapshot` baseline that copies the manifest into the baselines table would allow baselining a database with no repository at all. Deliberately **not** in v1 — it duplicates what a commit already is, and W2 covers the repo-less case live.

---

## 4. Comparison algorithm

The algorithm is the sync engine's change-detection core, generalized from "prior state vs live" to "any left side vs any right side."

### Step 1 — resolve both sides to `(identity → hash)` maps

- **Live database side:** stream objects through the existing provider hybrid (`IObjectScriptProvider` routed by `SqlObjectTypeCatalog` descriptor strategy, exactly as `SyncEngine.StreamProvidersAsync` does), normalize (`ScriptNormalizer`), hash (`Sha256ObjectHasher`). Identity key = the engine's `StateKey` format `{(int)Type}|{Schema}|{Name}` compared `OrdinalIgnoreCase` — the same case-insensitive identity V011 (`V011__object_identity_nocase.sql`) established for tracked state after OBS-23 (case-only rename bricked the prior map under a BINARY index).
- **Git commit side:** read `metadata/object-inventory.json` at the commit (hashes precomputed at scripting time); fall back to blob hashing per §3. Database options / permissions / docs artifacts (`SqlObjectType.DatabaseArtifact` synthetic identities, `metadata/database-options.sql`, `security/permissions/permissions.sql`) hash as files.
- **Tracked-state shortcut (DB-vs-Git against a job's current head):** when the right side is the job's branch head *and* the job's last run delivered, `TrackedObjectState.LastHash` **is** the commit-side map — one SQLite read replaces any git work. The module verifies the precondition (`LastCommitSha` consistency) before taking the shortcut, and falls back to the manifest when it doesn't hold.

### Step 2 — hash equality ⇒ no drift

Set-join the two maps on identity. Equal hashes are proof of byte-identical normalized definitions (the entire sync engine already stakes correctness on this: `ApplyItemAsync` classifies `Unchanged` on `priorState.LastHash == hash`). No diff, no script retention, no finding. This is the fast path that must cover >99% of objects in a healthy environment.

### Step 3 — classification on mismatch or absence

| Category | Condition |
|---|---|
| **Modified** | Present on both sides, hashes differ |
| **Missing from Git** (DB-vs-Git) / **Only in left** | Present in database, absent at the commit |
| **Missing from database** (DB-vs-Git) / **Only in right** | Present at the commit, absent in the database |
| **Added / Deleted** (baseline-anchored runs) | The directional reading of the two rows above when the older side is declared the baseline |
| **Unknown — scripting failed** | Either side could not produce a definition (§5 — never mapped to Deleted/Missing) |
| **Unknown — identity unrecoverable** | A git-side file whose object identity cannot be reconstructed (no manifest + mangled path) |

Each finding also carries an **aspect** so a permission-only or options-only drift is not reported as a definition change: `Definition` (the object's script), `Permission` (the consolidated `security/permissions/permissions.sql` artifact, or per-object GRANT sections when `IncludePermissions` embeds them — built by `SqlPermissionScriptBuilder`), `DatabaseOption` (`metadata/database-options.sql` from `DatabaseArtifactReader`), `Artifact` (docs/inventory/security-review generated files), `ReferenceData` (versioned table data under `data/`). Aspect attribution falls out of the identity: synthetic artifacts already have distinct `SqlObjectType.DatabaseArtifact` / `ReferenceData` identities in tracked state today.

### Step 4 — line-level diff, on demand only

For `Modified` findings the UI renders a split/unified diff through the existing DiffPlex infrastructure — `DiffRowMapper.BuildSplit`/`BuildUnified` (`src/Obsync.App/ViewModels/DiffRowMapper.cs`, pure, off-UI-thread, virtualized in `ScriptDiffWindow`). Diffs are computed lazily when a finding is opened, never eagerly for the whole findings list — at 500k objects an eager-diff design is a memory and latency disaster, and hash inequality already proves the mismatch.

---

## 5. False-positive prevention (the make-or-break section)

A drift tool that cries wolf gets turned off. Every rule here mirrors a defense the sync engine already learned the hard way.

1. **Scripting failure is never drift.** An object either side fails to script classifies as **`Unknown — scripting failed`** with the provider's reason, never as Deleted/Missing. This mirrors the engine exactly: `RawScriptedObject.SkipReason` (`IObjectScriptProvider.cs`) flows to `SyncEngine.RecordSkipAsync`, which marks the object *seen* so `ApplyDeletions` cannot delete it, and `EscalateForSkips` turns any skip into a `Warning` run — a partial run is never a clean run. The drift analogue: any `Unknown` finding forces the comparison's status to `Warning`, and `findings_unknown` is a first-class counter shown beside the drift counts, never folded into them.

2. **Mass-absence circuit breaker.** If more than 50 objects *and* more than half of one side's object set come up absent, the comparison aborts with a diagnostic verdict ("the comparison login has likely lost metadata visibility — VIEW DEFINITION revoked, remapped login") instead of reporting a thousand Deleted findings. This is `ApplyDeletions`' mass-deletion breaker (`candidates.Count > 50 && candidates.Count * 2 > prior.Count`, `SyncEngine.cs` ~line 1673) transplanted: the far likelier cause of a vanishing schema is lost visibility, not a real mass drop. Unlike the engine, drift has no "Run Now confirms it" escape hatch — a read-only report has nothing to apply, so it simply refuses to publish a verdict it cannot trust.

3. **Compare the effective selection first — refuse to compare unlike scopes.** Almost every naive DB-vs-DB comparison "finds" drift that is actually filter asymmetry. Before any scripting, the module resolves ONE effective `ObjectSelectionProfile` (preset expanded via `ResolveTypes()`/`ObjectSelectionPresets.Expand`, schema filter, job `IgnorePatterns`, plus the repo-side `.obsyncignore` rules the engine merges in `LoadIgnoreRulesAsync` via `IgnoreRules.Parse`) and applies it identically to both sides. For DB-vs-Git the baseline's `selection_json` (captured at baseline creation, §3) is diffed against the job's current selection: objects only in scope on one side are reported under a separate **`Out of comparison scope`** bucket — excluded from drift counts, visible on demand. An ignored object is *never* a finding; this is the same retain-don't-delete semantics `IncrementalPlanner.Plan` applies to ignored items today.

4. **Normalize both sides, always, with the same options.** All hashing runs on `ScriptNormalizer` output with `NormalizationOptions.Default` — including the git side when blob-hashing (a repo produced by a job with `NormalizeScripts = false`, or files hand-edited with CRLF endings, must not diff as drift over line endings; the normalizer's CRLF→LF, trailing-whitespace, and SSMS `Script Date` header rules exist precisely to kill these). The normalizer's byte-identical guarantee (documented invariant at the top of `ScriptNormalizer.Normalize`) is what makes cross-source hash comparison sound at all.

5. **Encrypted and CLR modules.** `MetadataScriptProvider.ReadModulesAsync` LEFT-JOINs `sys.sql_modules` specifically so `WITH ENCRYPTION` and CLR modules surface as skips instead of disappearing. Drift rules: unscriptable on both sides ⇒ single `Unknown — definition unavailable on both sides` finding (informational, not drift — the module cannot honestly say more); unscriptable on one side only ⇒ `Unknown`, with the asymmetry called out (this often *is* interesting — encrypted in prod, plain in dev — but it is not provable definition drift).

6. **Case-sensitivity and collation.** Identity matching is case-insensitive (`OrdinalIgnoreCase` keys, NOCASE persistence — consistent with V011 and the engine's `BuildPriorMap`). A pair matched case-insensitively whose names differ in case produces a low-severity **`Case-only name difference`** detail on the finding rather than an Added+Deleted pair. When one compared server has a case-sensitive catalog collation and genuinely hosts `dbo.Foo` *and* `dbo.FOO`, the collision is detected during map construction and reported explicitly as a collation-semantics finding instead of silently merging — the module never guesses which one matches.

7. **Every finding carries evidence.** `drift_findings` stores both hashes (or NULL + `*_absent_reason`), and the evidence folder stores both normalized scripts (or the reason one is missing: "scripting failed: <provider reason>", "absent at commit abc1234", "out of selection scope"). The finding view shows the evidence block verbatim. A user must always be able to answer "why does Obsync claim this?" from the finding itself — the same philosophy as the diagnostics page and the deletion breaker's self-explanatory warning text.

8. **Staleness honesty.** A comparison against a branch head records the exact `commit_sha` compared, not "latest" — and the live side records its scripting start/end timestamps. Drift found in a 40-minute VLDB scan may already be committed by the time the user reads it; the report says what was compared and when, and the UI offers one-click re-check of a single finding (re-script one object, re-hash — milliseconds).

---

## 6. Performance at VLDB scale (500k+ objects)

The product's stated scale target is the audit's VLDB profile; the drift module inherits the engine's proven mechanics rather than inventing new ones.

- **Hash-only until proven different.** Step 2's hash join is the whole comparison for unchanged objects: no diffing, no script retention, no per-object allocations beyond the map entry. This is the same economics that lets sync runs handle ~1M objects (see the encode-once and slim-prior-map notes from the `9483454`/`a917680` performance passes).
- **Manifest-first git side.** Reading one `object-inventory.json` per database (sorted, deterministic, hash-complete) replaces up to 500k `git show` invocations. Blob-hash fallback is chunked through `GitCommandRunner` batching, but the honest guidance is: at VLDB scale, keep the inventory artifact enabled (it is on by default).
- **Bounded parallelism via `ChannelPipeline`.** The live-side scripting stream runs through the existing bounded producer/consumer (`src/Obsync.Engine/ChannelPipeline.cs`: single producer pumping the providers, `workers * 2` channel capacity, backpressure throttling the SQL reader, first-fault teardown). Worker count follows the job's `Advanced.MaxParallelWorkers` convention. Memory for the streaming half stays flat regardless of database size — that is the pipeline's documented contract.
- **Incremental watermarks — reused only where they are valid.** The `modify_date` watermark machinery (`IncrementalPlanner`, `IScriptingWatermarkRepository`, V009) is sound only against *the same job's own tracked state* — its skip decision substitutes `TrackedObjectState.LastHash` for re-scripting. Therefore: **valid** for W1 when comparing a job's source database against its own branch head (skip-eligible objects take their prior hash; the planner's no-prior violation rule and skip-blocked watermark semantics apply unchanged); **invalid** — and not used — for DB-vs-DB or comparisons against historical commits, where the other side has no correlated watermark. Those run full scans; the metadata provider's bulk-query fast path (a handful of `sys.sql_modules` reads instead of per-object round trips) is what makes a full VLDB scan tolerable today, and drift rides it as-is.
- **Memory ceilings.** The two identity→hash maps are the only full-population structures: ~500k entries of (short string key, 64-char hash) per side — hundreds of MB avoided by *not* retaining scripts. Scripts are retained only for mismatched objects, written straight to the evidence folder (atomic-write pattern as in `SyncEngine.WriteAtomicAsync`) rather than accumulated in memory; findings rows are capped (§3). Diffs are lazy (§4). The 95 MB `MaxScriptChars`-class guard applies to evidence files too — an oversized script stores a truncation notice, not 100 MB of INSERTs.
- **SQLite writes chunked**, following the existing batch-insert chunking the state repository already uses (`BatchInsertChunkingTests` covers the pattern after commit `a917680`).

---

## 7. UI structure

**New nav section: "Drift."** One more `NavButton` RadioButton in the `MainWindow.xaml` nav rail (the established pattern: `IsChecked` bound through `SectionToBool`, `NavigateCommand` parameter — Dashboard / Servers / Repositories / Jobs / History / Settings today). Not a Jobs tab, not a Sync sub-view.

- **Comparisons list (landing view):** table of comparisons and baselines — kind badge (DB↔Git / DB↔DB / Git↔Git), both sides, when run, status (reusing the `RunStatus`-style badge language incl. `Warning`), finding counts split as *drift / unknown / out-of-scope*. Actions: open, re-run, export report, delete. "New comparison" launches a short wizard (pick kind → pick sides → confirm effective selection with the §5-3 scope diff shown *before* running).
- **Finding drill-down:** master/detail — findings grid (virtualized, filter by category/aspect/type/schema, the History view's filtering conventions) with an evidence pane: both hashes, both absence reasons, and an **Open diff** action that reuses `ScriptDiffWindow` + `DiffRowMapper` verbatim (split/unified toggle, find, copy, wrap — all shipped diff-viewer capabilities apply for free).
- **Evidence display:** the finding detail always shows the §5-7 evidence block; `Unknown` findings show the provider's skip reason in the same phrasing the run log uses ("The module definition is unavailable (encrypted with WITH ENCRYPTION, or a CLR object)").
- **Empty states:** section-level ("No comparisons yet — compare a database against its repository to check for drift", mirroring the dashboard's getting-started card) and result-level — a zero-finding comparison renders an explicit **"No drift detected — N objects compared, hashes identical"** success state, never a blank grid; a comparison that aborted on the §5-2 breaker renders the diagnostic verdict prominently.
- **Progress:** long scans report through the existing `IProgress<SyncProgress>`-style phase reporting (the engine's throttled every-500-objects cadence) with cancellation.

**Out of v1 (explicitly):** deployment/sync script generation (out of the module entirely, §1); DB-vs-DB (v2); scheduled scans, alerting, and drift dashboards/trends (v3); pinned non-git baselines; cross-server *data* comparison beyond the already-versioned `ReferenceDataTables`; object-level "ignore this finding" suppression lists (revisit with v3 alerting, where noise suppression earns its complexity); any Dashboard "drift" tile (nothing on the dashboard until scheduled scans give it truthful, current data).

---

## 8. Testing strategy

The module's credibility is its false-positive rate, so the test plan is weighted accordingly.

- **Differential tests on the E2E harness.** `tools/Obsync.E2E` already provisions disposable databases on a local SQL Server (`SqlSeeder`) and local bare repositories, driving the real production pipeline. Drift scenarios extend the battery: (a) **clean-after-sync invariant** — run a sync, then a DB-vs-Git comparison against the produced commit: **zero findings, always** (this single test transitively verifies normalizer/hasher/path-mapper agreement between the two modules); (b) mutate the database (ALTER a proc, DROP a table, GRANT a permission, flip a database option) → exactly the expected findings with correct category *and aspect*; (c) commit-side mutation (hand-edit a file, delete a file) → Missing-from-database / Modified with git-side evidence; (d) W3 Git-vs-Git between two harness-produced commits.
- **False-positive suite (the §5 rules, one test per rule):** offline/unreachable database ⇒ comparison fails with a connection verdict, zero findings emitted; revoked VIEW DEFINITION mid-set ⇒ `Unknown` findings + `Warning` status, never Deleted (mirrors `DeletionSafetyTests`); mass-absence ⇒ breaker verdict, no findings published; encrypted module (harness seeds `WITH ENCRYPTION` objects) on one/both sides ⇒ the two `Unknown` shapes; selection/filter mismatch between baseline and current job selection ⇒ out-of-scope bucket, not drift; CRLF/trailing-whitespace/`Script Date`-header-only differences ⇒ zero findings (normalizer parity); case-only rename ⇒ single case-detail finding, not Added+Deleted (extends `ObjectStateCaseTests`' V011 coverage).
- **Determinism tests:** the same comparison run twice produces identical findings in identical order (the `ObjectInventoryWriter` byte-identity test is the precedent for locking determinism in this codebase); exported HTML/CSV/JSON reports are byte-stable modulo the timestamp header.
- **Unit tests** for the pure core — classification given two maps, scope resolution/diffing, manifest parsing, path→identity recovery — following the codebase's pattern of extracting pure logic for exhaustive testing (`IncrementalPlanner`, `MissedRunPolicy`, `RunAlertEvaluator` are all deliberately pure for this reason).
- **Render smoke tests** for the new views, matching the existing every-view smoke coverage noted in the assessment snapshot.

---

## 9. Phasing & effort

Confidence legend: **High** = mechanism exists and is reused nearly as-is; **Medium** = new composition of proven parts; **Low** = new surface area with real unknowns.

### v1 — DB-vs-Git on demand (+ Git-vs-Git)

Comparison core (maps, classification, evidence store), baseline/comparison/finding persistence (V013), the Drift nav section with list + drill-down reusing `ScriptDiffWindow`, report export, E2E differential + false-positive suites. Watermark reuse for the own-head case only.

**Effort: 4–7 weeks.** Core comparison + persistence 1.5–2.5 wk (High — pipeline, normalizer, hasher, manifest all exist); UI 1.5–2.5 wk (Medium — new views, but grid/diff/report patterns are established); test suites 1–2 wk (High confidence in approach, the harness exists; the range covers seeding encrypted/CLR/collation fixtures).

### v2 — DB-vs-DB (interactive Schema Compare)

Second live side through the same pipeline, shared-selection wizard step, dual-connection orchestration (two `ChannelPipeline` scans, sequenced or interleaved after profiling), collation-collision handling, report identity block for two servers.

**Effort: 2–4 weeks** (Medium — no new primitives, but dual-live orchestration and the DB-vs-DB false-positive matrix need real-world shakeout; the wide range reflects unknowns in cross-version SQL Server scripting symmetry, e.g. the same object scripting slightly differently from SQL 2016 vs 2022 catalogs — a risk that must be measured on the harness before the classification thresholds are trusted).

### v3 — Scheduled drift scans + alert integration

Scan definitions on the Quartz scheduler (reusing `SyncQuartzJob` conventions: `[DisallowConcurrentExecution]`, per-definition locking, missed-run policy), new-drift-since-last-scan delta logic, `RunAlertService` payloads for drift, scheduler E2E coverage in the service sandbox (`--seed-service` already exists in the harness for exactly this kind of verification — a lesson OBS-00 made non-negotiable).

**Effort: 2–4 weeks** (Medium for the scheduling mechanics, which are well-trodden; **Low** confidence on alert/suppression tuning — "when is re-alerting on persistent drift noise vs. signal" is a product question that will iterate. Budget assumes the alert model stays within the existing SMTP/webhook channels; vendor-specific integrations remain governed by assessment §12).

No false precision: totals are **8–15 weeks** across all three phases, sequential, single senior engineer already fluent in this codebase. The dominant risk to the low end is §5 — every false-positive class found in the field after shipping costs more than preventing it before.

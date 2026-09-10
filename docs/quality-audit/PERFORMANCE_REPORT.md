# Obsync production-readiness audit — performance report

Audit date: 2026-07-15 · Harness: `tools/Obsync.Benchmark` (drives the REAL pipeline — metadata +
SMO providers → hashing → file writes → git commit — with fully isolated state and a local bare
repository as the remote, so numbers include full git cost and zero network noise).

> **Corrections, 2026-09-09.** Every measurement below is real and was left untouched. Three things
> about them were stated wrongly or not at all, and the corrections apply to the addendum as well as
> to the main table. They are listed here rather than only in place because each one changes how a
> number should be read:
>
> 1. **Object size: ~750 bytes.** The workload generator emitted one fixed comment-padded body per
>    type (a ~1 KB procedure, much smaller views and functions), which came out at **≈750 bytes per
>    file** across the working tree — 35.9 MB over 50,207 files, measured below. Real stored
>    procedures run 2-10 KB. Every size-derived figure here (working tree, `.git`, and any
>    extrapolation to a VLDB repository) is therefore optimistic by roughly **3-10×**, and the
>    hashing/normalization/write share of per-object cost is understated by the same factor.
>    Throughput figures in objects/second are affected far less — the first scan is dominated by
>    per-object catalog round trips, not by body size — but they are not independent of it either.
>    The harness now takes `--object-bytes` (default 4,096) and records the measured average module
>    size in each artifact; nothing below has been re-measured at a realistic size.
> 2. **The divergence sweep and every push path were never exercised.** The harness hardcoded
>    `CommitMode.LocalCommitOnly`, with no way to override it. `SyncEngine.DivergedTypes` — the sweep
>    that reads and hashes *every tracked file* on *every* pull-request run — is gated on
>    `CommitMode == PullRequest`, so it never ran here; LocalCommitOnly also never pushes. No number
>    in this document covers pull-request mode, the divergence sweep, `git push`, or the head-branch
>    recut/reconcile. The harness now takes `--mode local|direct|pr`.
> 3. **The recorded artifacts state a workload they did not measure** — see the workload section.

## Environment

- MOWNICA: Windows 11 Home (26200), 12 logical cores
- SQL Server 2025 RTM-GDR (17.0.1125.2), local instance, Windows auth
- git 2.50.1.windows.1; commit under test `4e5bbd8` (all audit fixes applied)
- Mode: LocalCommitOnly (full commit cost). Push was **not** excluded by design — the harness had
  the mode hardcoded with no override, so no other mode could be run. Corrected: `--mode`.

## Workload

`ObsyncBench` database: **50,202 objects scanned**, at roughly **750 bytes per object** (see the
corrections above). Composed of T-SQL modules plus 100 tables (exercising the SMO path) and
hostile-name/encrypted objects. This is the audit's "thousands of objects" scale test; the product's
stated VLDB target (hundreds of thousands) extrapolates from the same pipeline but was not run at
that size here.

**What the recorded artifact says, and why it is wrong.** The generator was an idempotent *top-up*:
it created only what was missing and never removed anything, so a request for 10,000 objects against
a database that already held 50,000 scanned 50,000 while the artifact header still printed the
request. `artifacts/benchmarks/bench-10000-20260715-185551.md` therefore carries
"10,000 module objects (60% procs / 20% views / 20% functions), 100 tables" in its header and
"50,202" in its Scanned column; the header is the request, and only the Scanned column describes the
run. The same defect mislabels the whole `bench-10000-20260716-*` set, in both directions:
`-014234.md` and `-015636.md` scanned **2,308** objects (the 2,000-table workload on the separate
`ObsyncTblBench` database) under a header claiming 10,000 modules and 100 tables, while `-015810.md`
and `-021544.md` scanned 50,202 under the same header. The addendum's own row labels ("2,000 tables
+ 300 modules", "50,202") describe what was actually scanned and are correct; the *file headers*
behind them are not.

Two consequences for this section as originally written: the estate's per-type composition is **not
known** from the artifacts — "60% procedures / 20% views / 20% functions, 100 tables" describes the
last *request*, not the 50,202 objects that were walked, which accumulated across successive top-up
runs of differing sizes — and the artifact file names encode the request too. The harness now
reports the counts read back from SQL Server before each suite, names the file after the measured
count, and can rebuild the database to exactly the requested workload (`--reset-workload`).

## Results (report: `artifacts/benchmarks/bench-10000-20260715-185551.md`)

| Run | Status | Scanned | +/~/− | Skips | Duration | Obj/s | Peak WS | Allocated |
|---|---|---:|---|---:|---:|---:|---:|---:|
| Full initial | Warning | 50,202 | +50,200/~0/−0 | 7 | 587.7 s | 85 | 363 MB | 2,816 MB |
| No-change (cold) | Warning | 50,202 | +6/~2/−0 | 1 | 39.5 s | 1,269 | 439 MB | 2,207 MB |
| No-change (warm) | Warning | 50,202 | +0/~0/−0 | 1 | **6.7 s** | 7,473 | 393 MB | 654 MB |
| Incremental, 500 changed | Warning | 50,202 | ~501 | 1 | **11.3 s** | 4,446 | 394 MB | 669 MB |
| Cancellation probe (8 s in) | Cancelled | 2,087 | — | 0 | 8.2 s | — | 326 MB | — |

Phase timing (full initial): scripting 460.5 s, first commit 126.0 s, repository preparation 0.6 s.
Workspace after the suite: 50,207 files, 35.9 MB working tree, 20.1 MB `.git` — **≈750 bytes per
file**. At a realistic 2-10 KB per object the same 50k estate is roughly 100-500 MB of working tree,
so read both figures as a floor, not as a projection.

Notes:
- The Warning statuses are **by design**: the workload contains deliberately unscriptable objects
  (encrypted module et al.), which the engine reports as skips instead of dropping — 7 on the full
  scan, 1 recurring (the encrypted module forces a full-scan of its type each run, a documented
  limitation with the `.obsyncignore` mitigation).
- The cold no-change run's `+6/~2` is first-run-after-upgrade artifact/doc regeneration settling;
  the warm run is the honest steady state (+0/~0/−0).
- **Cancellation latency 0.17 s** from token to a persisted `Cancelled` run.

## Interpretation

- **Steady state is where it matters and it is fast**: a no-change scan of 50k objects in 6.7 s
  (~7,500 obj/s) and a 500-object incremental delta in 11.3 s. The incremental watermark path
  (`modify_date` skipping) delivers its designed payoff and — after this audit's fixes — does so
  without the correctness holes (oversized-skip watermark gate, out-of-filter retention,
  planner ordering).
- **First run is scripting-bound** (~78% scripting, ~21% first git commit). 85 obj/s on the initial
  scan is dominated by per-module catalog reads and the 100-table SMO path; it is a one-time cost.
- **Memory is flat and modest** (≤ 440 MB peak WS at 50k objects) — the bounded-channel pipeline's
  backpressure holds; allocations on warm runs drop ~4× vs cold.
- **UI responsiveness** was not re-measured in this pass (prior UIA verification covers it); the
  engine-side contributors — batched persistence, capped changes grid, streaming reports — are in
  place and unchanged.

## SQL Server impact review

- Metadata reads are bulk set-based catalog queries (one per type family), parameterized, with the
  optional `SET LOCK_TIMEOUT` bound so a blocked metadata read fails fast instead of hanging on a
  busy server.
- SMO scripting parallelism is **capped at 8 connections** regardless of the worker pool; the
  worker pool itself parallelizes hashing/writing, not SQL load.
- The incremental snapshot is a single bulk `modify_date` scan per database per run.
- SQL retry (default 3, exponential-ish) applies to the scripting readers; the artifact/doc/security
  readers deliberately fail-soft into reported skips rather than retrying (README updated to say
  exactly this).
- No query in the scripting path acquires long-lived locks; everything reads committed catalog
  state. The engine remains read-only against sources.

## Bottlenecks & recommendations

1. First-scan throughput (85 obj/s) is the slowest dimension — acceptable as a one-time cost, but
   VLDB-first-run users will wait hours at 500k objects. Candidate: batch `sys.sql_modules`
   definitions more aggressively / widen the metadata fast-path.
2. First commit of a huge tree (126 s for 50k files) is git-bound; `core.untrackedCache` is already
   enabled — little headroom left short of sharding repositories.
3. The known git-scaling ceiling (~1M files per repository) is unchanged and documented; repo-per-
   schema sharding remains future work.
4. SQLite busy timeout raised 5 s → 30 s in this audit after the write-transaction analysis;
   at 500k-state batches, chunked transactions would further shrink the cross-host lock window.
5. Not measured here (bounded by environment): slow-network Git/GitHub behavior, low-memory and
   low-disk operation, and a true 500k-object VLDB run — see RELEASE_READINESS for the remaining
   human-gated items.
6. Not measured here (bounded by the harness, until 2026-09-09): **pull-request mode in full** — the
   `DivergedTypes` sweep that reads and hashes every tracked file on every PR run, the head-branch
   recut from base, the head-branch reconcile, and `git push` in any mode. The sweep is the one
   per-run cost that scales with the *tracked* estate rather than with the changed set, and its
   in-code estimate (≈51 µs/file, ~1 minute per run at a million objects) has never been checked
   against a measurement. The harness can now run it: `--mode pr` against the local bare remote
   exercises the sweep, the recut, the reconcile and the push for real. Opening the pull request
   itself cannot be done locally and is recorded by a local stand-in, so GitHub's REST latency and
   its failure modes remain unmeasured.
7. Object size: everything above was measured at ~750 bytes per object. Re-running the suite with
   `--object-bytes 4096` (or higher) is the cheapest way to put real numbers behind the working-tree
   and repository projections.

---

## Addendum — 2026-07-16 optimization pass (post-audit)

A dedicated performance review (two fresh-context auditors over the hot paths, findings verified
against this report's numbers) was implemented and re-measured. Changes: per-slice SMO prefetch
(the parallel table path never prefetched — the N+1 its own doc claimed to prevent; bounded by a
25k-table ceiling), streamed object-inventory serialize/hash (the string form previously crossed
the 95 MB guard at ~380k objects and was skipped forever with a perpetual Warning), single-pass
byte-identical script normalizer (locked by a frozen reference implementation + ~3,100
differential cases committed in `ScriptNormalizerEquivalenceTests`: a 70-input corpus across 16
option combinations, 2,000 seeded fuzz iterations, a 1 MB script, and an idempotency pass — an
earlier exploratory run covered far more but is not reproducible from the repository), encode-once hash+write, size-guard before hashing, batched self-heal
existence probe, slim prior-state projection (~half the resident bytes), chunked multi-row SQLite
inserts (parameter limit 32,766 confirmed empirically; chunks 200×14 / 350×8 / 560×5), V012 drops
a strict-prefix-duplicate index, per-run persisted change rows capped at 50k (counters stay exact;
surfaced in the run log), server-side schema filter on the incremental snapshot,
`clone -c core.longpaths` (the old post-clone set left the clone itself unprotected),
`feature.manyFiles` + `core.fsyncMethod=batch` + `.git/info/exclude`-based tmp exclusion
(re-enabling untracked-cache eligibility), and `git diff --cached --quiet` replacing the
porcelain-status capture (~100 MB of stdout on a 1M-file first run).

**Re-measured (same machine/method; interference-checked with re-runs):**

| Workload | Metric | Before | After |
|---|---|---|---|
| 2,000 tables + 300 modules | Full scan | 236.0 s | **43.6 s (5.4×)** |
| 2,000 tables + 300 modules | Full-scan allocations | 31.3 GB | **1.45 GB** |
| 50,202 modules+tables | Full scan | 587.7 s | **369.5 s (1.6×)** — scripting phase 460.5 → 190.8 s (2.4×) |
| 50,202 | No-change (cold) | 39.5 s | **11.6 s (3.4×)** |
| 50,202 | No-change (warm) | 6.7 s | 6.7 s (unchanged — dominated by the encrypted-object full-scan-of-type, by design) |
| 50,202 | Incremental, 500 changed | 11.3 s | 11.6 s (unchanged) |
| 50,202 | Peak working set | 363 MB | **203 MB** |

**Honest notes:** (1) the first-COMMIT phase measured 126 → 178 s; an isolated A/B (plain vs tuned
git config, synthetic 50k files) attributes ~5% of that to `feature.manyFiles`+`fsyncMethod=batch`
on a one-time mass add — kept because index v4 and the untracked cache target the 1M-file steady
state, where a ~5% one-time cost is the right trade; the rest of the delta was environmental
(back-to-back benchmark disk churn — the re-run confirmed). (2) Correctness was re-proven after
the changes: full suite 561/561, E2E battery 73/73 (including the determinism tree-hash check —
the byte-identity work means existing deployments' stored hashes remain valid; no mass re-commit
on upgrade). (3) Deliberately deferred, with reasons: `core.fsmonitor` daemon (lifecycle surprise
in service/multi-user contexts — revisit as opt-in), pathspec-scoped commits (would lose the
full-sweep self-heal semantics), per-type aggregate no-change short-circuit (checksum-collision
risk needs a conservative-fallback design), per-database parallelism (multiplies production SQL
load; needs opt-in design), streaming pending-state to a staging table (touches the delivery-gate
invariant), provider-stream overlap (subtle fault-teardown concurrency for a modest win).
(4) **Added 2026-09-09:** the corrections at the top of this document apply to this table too. Every
row was measured in LocalCommitOnly mode on ~750-byte objects, so none of it covers the divergence
sweep, push, or pull-request mode, and none of it says anything about production-sized bodies. The
row labels here are accurate; the artifact files behind them are the mislabelled
`bench-10000-2026071*` set described in the Workload section — the "2,000 tables + 300 modules" rows
come from files whose headers claim 10,000 modules and 100 tables.

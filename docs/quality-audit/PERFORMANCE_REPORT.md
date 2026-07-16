# Obsync production-readiness audit — performance report

Audit date: 2026-07-15 · Harness: `tools/Obsync.Benchmark` (drives the REAL pipeline — metadata +
SMO providers → hashing → file writes → git commit — with fully isolated state and a local bare
repository as the remote, so numbers include full git cost and zero network noise).

## Environment

- MOWNICA: Windows 11 Home (26200), 12 logical cores
- SQL Server 2025 RTM-GDR (17.0.1125.2), local instance, Windows auth
- git 2.50.1.windows.1; commit under test `4e5bbd8` (all audit fixes applied)
- Mode: LocalCommitOnly (full commit cost; push excluded by design of the harness)

## Workload

`ObsyncBench` database: **50,202 objects** — T-SQL modules (60% procedures / 20% views /
20% functions), 100 tables (exercising the SMO path), plus hostile-name and encrypted objects.
This is the audit's "thousands of objects" scale test; the product's stated VLDB target
(hundreds of thousands) extrapolates from the same pipeline but was not run at that size here.

## Results (report: `artifacts/benchmarks/bench-10000-20260715-185551.md`)

| Run | Status | Scanned | +/~/− | Skips | Duration | Obj/s | Peak WS | Allocated |
|---|---|---:|---|---:|---:|---:|---:|---:|
| Full initial | Warning | 50,202 | +50,200/~0/−0 | 7 | 587.7 s | 85 | 363 MB | 2,816 MB |
| No-change (cold) | Warning | 50,202 | +6/~2/−0 | 1 | 39.5 s | 1,269 | 439 MB | 2,207 MB |
| No-change (warm) | Warning | 50,202 | +0/~0/−0 | 1 | **6.7 s** | 7,473 | 393 MB | 654 MB |
| Incremental, 500 changed | Warning | 50,202 | ~501 | 1 | **11.3 s** | 4,446 | 394 MB | 669 MB |
| Cancellation probe (8 s in) | Cancelled | 2,087 | — | 0 | 8.2 s | — | 326 MB | — |

Phase timing (full initial): scripting 460.5 s, first commit 126.0 s, repository preparation 0.6 s.
Workspace after the suite: 50,207 files, 35.9 MB working tree, 20.1 MB `.git`.

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

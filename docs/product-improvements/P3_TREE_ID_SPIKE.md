# P3 spike — can a git tree id replace the divergence sweep?

**Status:** measured, 2026-09-09. Recommendation below; no code written against it.
**Question:** Architecture note 01 (P3) proposes recording the git tree id of each database folder at
delivery, then comparing it against the base branch at plan time — one command instead of reading and
hashing every tracked file. It called this "both cheaper and more precise than what exists", and
asked for a spike before committing to it.

## Why a spike rather than an argument

The same note's companion (note 03) recommended shallow cloning on equally confident reasoning. That
recommendation was measured during the audit and turned out to **break the product** — `--depth 1`
breaks the script history viewer and makes the 0.13.0 head-branch reconcile fail with `refusing to
merge unrelated histories`. Same author, same confidence, no measurement behind either. So this one
got measured.

## What was measured

A synthetic repository matching Obsync's real layout (`db/procedures/`, `db/tables/`, `db/views/`),
with objects of ~3 KB — roughly the size of a real stored procedure, and about 4× the 750-byte
synthetic objects the performance benchmark used. `core.autocrlf=true` and `feature.manyFiles=true`,
matching what Obsync's bundled git config sets.

git 2.50.1.windows.1. **Caveat:** Obsync ships its own git under `tools/git`, which is not present in
a source checkout, so this used the system git of the same Git-for-Windows lineage.

| | Operation | 24,000 files | 70,000 files |
|---|---|---|---|
| **A** | `rev-parse HEAD:db/procedures` — the proposal | **24.3 ms** | **18.9 ms** |
| **A2** | Same, all three folders | 46.8 ms | 51.2 ms |
| **B** | Read + normalize + SHA every file — today | **1,425 ms** | **3,772 ms** |
| **C** | `diff --name-only` between two trees | 30.2 ms | 57.3 ms |

(20,000 and 60,000 of those files respectively sit in a single folder, which is what Obsync's layout
produces — see the per-folder limit in `README.md`.)

## What the numbers say

**The tree-id lookup does not scale with the estate.** 24.3 ms at 24k files, 18.9 ms at 70k — the
difference is noise. That is the expected shape: `rev-parse <ref>:<path>` walks to the *parent* tree
and reads one entry; it never reads the folder tree it names. Tripling the estate did not move it.

**The sweep it would replace is linear**, at a marginal 51 µs per file.

**But the performance case is weaker than note 01 implies.** Extrapolated, the current sweep costs
about **0.9 minutes per run at 1,000,000 files**. That is real, and it is pure waste on a healthy
run — but it is roughly one minute, not the hours the note's framing suggests. Two things make that
figure a **floor rather than an estimate**:

- The measurement harness hashes in Python against a page cache that was warm from having just
  written the files. A cold read of ~3 GB of working tree, on a slower or networked disk, is a
  different number.
- The real implementation additionally allocates a second full-size buffer for every file containing
  a CR — which, on an `autocrlf` checkout, is every file. That is roughly 2× the estate in transient
  allocation per run, and the harness does not model the resulting GC pressure at all.

So: the saving is worth having, but it does not justify rushing the change.

## The stronger argument is precision, not speed

`diff --name-only` between the two trees named **exactly one file** — `dbo.obj_0000001.sql` — after a
single-object edit, at both sizes.

Today `DivergedTypes` reports at **type** granularity. One stale view withholds the incremental
filter from *every view in the database*, and if enough types diverge the filter is dropped for the
database entirely. Object granularity means re-scanning only what actually diverged. On a large
estate that is a far bigger saving than the sweep's own cost, and it is a correctness-shaped
improvement rather than a performance one.

## A second advantage, unlooked-for

**Tree ids are immune to the line-ending problem entirely.** Measured: the tree id was unchanged
across an `autocrlf` checkout, and the working tree reported clean despite holding CRLF.

That matters because line endings have already cost this product one release. The sweep must
normalize CRLF before hashing precisely because git rewrites line endings on checkout — and getting
that wrong is what produced 0.13.0's "13,305 objects modified when nothing changed". A tree id is
computed over the blob as git stores it, so that entire class of bug does not exist on this path.

## The finding that blocks a naive swap

**A tree id describes the commit, not the working tree.** Measured: after deleting a file from the
working tree, the folder's tree id was unchanged — `tree id notices a file deleted from the WORKING
TREE: False`, at both sizes.

This is not a detail. `DivergedTypes`' missing-file branch is exactly what 0.13.0 added to stop a
partial proposal shipping, and that branch asks a question about the **working tree**. A tree-id
comparison cannot answer it.

The two questions do coincide in pull-request mode, because `PrepareAsync` recuts the head branch
from base immediately before planning, so the working tree *is* the base commit's content at that
moment. But that is an **invariant the design would newly depend on**, not something that is true by
construction. It has to be written down and pinned by a test, or a future change to when the recut
happens silently reintroduces the 0.13.0 defect.

## Recommendation

**Adopt it — as a fast path, not a replacement — and not in 0.14.0.**

1. **Record the delivered tree id** per job and database at delivery time (needs a migration and a
   capture point in the delivery path).
2. **At plan time**, compare the base branch's folder tree id against the recorded one.
   - **Equal** → nothing diverged. Skip the sweep entirely. This is the healthy steady state, and
     the case where the current cost is pure waste.
   - **Different** → `diff --name-only` between the two trees yields the exact diverged paths.
     Re-scan those objects, not their whole types.
3. **Keep a working-tree check for the missing-file half**, and make it cheap — a directory
   enumeration, not a read-and-hash of every file. This is what preserves the 0.13.0 fix.
4. **Pin the invariant** that planning runs after the recut and before any writes, with a test that
   fails if that ordering changes.

**Sequencing.** This is a performance and precision improvement on a path that is currently
*correct*. Tier 1 closed four preconditions that were producing *wrong* results. Those should ship
first. This belongs in the release after, alongside the other deferred scale work.

## What this spike did not measure

- **A million files.** Both tiers were built in-process; the 70k tier took five minutes to construct.
  The scaling shape is clear and the mechanism is understood, but the extrapolation is arithmetic.
- **The real C# sweep.** The comparison used a Python proxy, which is why its result is quoted as a
  floor.
- **Cold cache.** Every measurement ran against files written moments earlier.
- **The bundled git.** The system git of the same lineage was used instead.
- **A repository with real history.** The spike repository has two commits. `rev-parse <ref>:<path>`
  should not care, but it was not tested against deep history.

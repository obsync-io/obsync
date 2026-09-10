"""
P3 spike: can a git tree id replace the full-estate read-and-hash divergence sweep?

Note 01 proposes recording the tree id of each database folder at delivery, then comparing it
against the base branch at plan time -- one command instead of reading and hashing every tracked
file. This measures whether that is actually cheap at scale, and tests the semantics it rests on.

Nothing here is argued. Every number is measured, and the caveats at the end are things the
experiment demonstrated rather than things I reasoned about.
"""
import hashlib
import os
import shutil
import subprocess
import sys
import time

ROOT = os.path.join(os.environ['TEMP'], 'obsync-p3-spike')
GIT = shutil.which('git')


def _force_remove(func, path, _exc):
    # git marks objects read-only; Windows refuses to delete those without clearing the bit.
    os.chmod(path, 0o700)
    func(path)


def nuke(path):
    for _ in range(3):
        shutil.rmtree(path, onexc=_force_remove)
        if not os.path.exists(path):
            return
        time.sleep(0.5)


def run(args, cwd, check=True):
    r = subprocess.run([GIT] + args, cwd=cwd, capture_output=True, text=True)
    if check and r.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {r.stderr.strip()}")
    return r.stdout.strip()


def timed(label, fn, repeats=5):
    """Best-of-N, so an antivirus hiccup does not become the headline number."""
    best = None
    for _ in range(repeats):
        start = time.perf_counter()
        result = fn()
        elapsed = time.perf_counter() - start
        best = elapsed if best is None else min(best, elapsed)
    print(f"  {label:<52} {best * 1000:9.1f} ms")
    return best, result


def build_repo(path, procedures, tables, views):
    if os.path.exists(path):
        nuke(path)
    os.makedirs(path)
    run(['init', '-q', '-b', 'main'], path)
    run(['config', 'user.email', 'spike@obsync.local'], path)
    run(['config', 'user.name', 'spike'], path)
    run(['config', 'core.autocrlf', 'true'], path)   # what Obsync's bundled config sets
    run(['config', 'feature.manyFiles', 'true'], path)

    # A realistic body: the benchmark's synthetic objects average ~750 bytes, which the audit found
    # to be 3-10x smaller than real stored procedures. Use 3 KB as a mid estimate of the real thing.
    body = ('-- filler line to approximate a real procedure body\n' * 60)
    for folder, count in (('procedures', procedures), ('tables', tables), ('views', views)):
        target = os.path.join(path, 'db', folder)
        os.makedirs(target, exist_ok=True)
        for i in range(count):
            with open(os.path.join(target, f'dbo.obj_{i:07d}.sql'), 'w', newline='\n') as handle:
                handle.write(f'CREATE PROCEDURE dbo.obj_{i:07d} AS BEGIN\n{body}SELECT 1;\nEND\n')

    run(['add', '-A', '--', '.'], path)
    run(['commit', '-q', '-m', 'seed'], path)
    return run(['rev-parse', 'HEAD'], path)


def full_read_and_hash(path):
    """What DivergedTypes does today: read every tracked file, normalize CRLF, SHA-256 it."""
    count = 0
    for folder in ('procedures', 'tables', 'views'):
        target = os.path.join(path, 'db', folder)
        for name in os.listdir(target):
            with open(os.path.join(target, name), 'rb') as handle:
                data = handle.read()
            hashlib.sha256(data.replace(b'\r\n', b'\n')).hexdigest()
            count += 1
    return count


def measure(procedures, tables, views):
    total = procedures + tables + views
    print(f"\n=== {total:,} files ({procedures:,} in one folder) ===")
    build_start = time.perf_counter()
    head = build_repo(ROOT, procedures, tables, views)
    print(f"  (build took {time.perf_counter() - build_start:.1f}s)")

    _, tree = timed('A. rev-parse HEAD:db/procedures (proposed)',
                    lambda: run(['rev-parse', 'HEAD:db/procedures'], ROOT))
    timed('A2. rev-parse for all three folders',
          lambda: [run(['rev-parse', f'HEAD:db/{f}'], ROOT) for f in ('procedures', 'tables', 'views')])

    hash_time, hashed = timed('B. read + normalize + SHA every file (today)',
                              lambda: full_read_and_hash(ROOT), repeats=2)
    print(f"     ...covered {hashed:,} files")

    # One object changes, the way a single ALTER PROCEDURE would show up.
    victim = os.path.join(ROOT, 'db', 'procedures', 'dbo.obj_0000001.sql')
    with open(victim, 'a', newline='\n') as handle:
        handle.write('-- edited\n')
    run(['add', '-A', '--', '.'], ROOT)
    run(['commit', '-q', '-m', 'one change'], ROOT)
    changed_tree = run(['rev-parse', 'HEAD:db/procedures'], ROOT)

    print(f"  tree id changed by one edit: {tree != changed_tree}")
    _, names = timed('C. diff --name-only between the two trees',
                     lambda: run(['diff', '--name-only', tree, changed_tree], ROOT))
    print(f"     ...named exactly: {names!r}")

    # The semantic the whole proposal rests on: does the tree id survive a CRLF checkout?
    shutil.rmtree(os.path.join(ROOT, 'db', 'views'))
    run(['reset', '--hard', '-q', 'HEAD'], ROOT)
    after_checkout = run(['rev-parse', 'HEAD:db/procedures'], ROOT)
    dirty = run(['status', '--porcelain'], ROOT)
    print(f"  tree id stable across an autocrlf checkout: {after_checkout == changed_tree}")
    print(f"  working tree reports clean despite CRLF:     {dirty == ''}")

    # And the caveat: a tree id describes the COMMIT, not the working tree.
    os.remove(victim)
    after_delete = run(['rev-parse', 'HEAD:db/procedures'], ROOT)
    print(f"  tree id notices a file deleted from the WORKING TREE: {after_delete != changed_tree}")

    return total, hash_time


print(f"git: {run(['--version'], os.environ['TEMP'])}")
results = []
for procs, tabs, views in ((20000, 2000, 2000), (60000, 5000, 5000)):
    results.append(measure(procs, tabs, views))

print("\n=== scaling of the sweep this would replace ===")
for total, hash_time in results:
    print(f"  {total:>8,} files  ->  {hash_time:7.2f}s   ({hash_time / total * 1e6:.1f} us/file)")
if len(results) == 2:
    (n1, t1), (n2, t2) = results
    per_file = (t2 - t1) / (n2 - n1)
    print(f"  marginal cost: {per_file * 1e6:.1f} us/file")
    print(f"  extrapolated to 1,000,000 files: {per_file * 1_000_000 / 60:.1f} minutes per run")

nuke(ROOT)

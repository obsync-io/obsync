# Changelog

All notable changes to Obsync. Versions are the MSI/installer baselines; dates are build dates.

## 0.13.1 - 2026-09-07

**A run says what changed in the database, not what it wrote to disk.** Reported from the 0.13.0
deployment: a run where nothing in SQL had changed displayed **13,305 Modified**.

Test suite: 1,408 → 1,411.

### Fixed — an unchanged object was reported as modified

The count was not an approximation or a rounding artefact. Obsync compares each object's stored hash
against the freshly scripted one, and for all 13,305 it returned **`Unchanged`** — every definition
was identical. The engine then relabelled each one `Modified`, purely so its file would be written
back after the recut had removed it.

Two unrelated facts were sharing one word: *the object's definition changed*, and *the repository
needed catching up*. Pull-request mode recuts its head branch from the base every run, so while a
pull request waits for approval **every file it proposed is absent from the working tree** — which
made the second fact true for the entire estate, on every run, and reported it as the first.

There is now a `Restored` change type and a count of its own, so the tiles mean what they say:

| | |
|---|---|
| **Added** | no prior state — a genuinely new object |
| **Modified** | stored hash ≠ new hash — the definition actually changed |
| **Deleted** | a tracked object no longer in the database |
| **Restored** | the definition was unchanged; only the file had to be rewritten |
| **Not scripted** | encrypted, CLR, or failed to script |

A run on an unmerged proposal now reads `Added 0 · Modified 0 · Deleted 0 · Restored 13,305`, which
is the truth: nothing changed in SQL, and the branch needed the files.

Restorations still count toward the run's total change count, and still produce the commit — they
are real repository content, and treating them as "no changes" would have been the opposite mistake.
They carry through the history timeline (a `↻` token beside `+ ~ −`), the diff viewer's filter
chips, the exported report, the commit message body, and the alert webhook payload, which gains a
`counts.restored` field. The field is additive; existing consumers are unaffected.

### Fixed — the scanned total was one lower than the change total

`13,304 scanned / 13,305 modified` had a second cause behind it. The **object-inventory** artifact is
written by its own streaming code path rather than through the shared one, and that path counted the
artifact as a change but never as scanned — so 0.13.0's counter fix reached four of the five
per-database artifacts and missed the one with bespoke handling. Both sides now agree.

### Fixed — server-level objects were self-healed on existence alone

The database pass compares file **content** before deciding an unchanged object needs no rewrite
(0.12.1). The server-level pass still only checked that the file existed, so a server object left
stale by a pull request closed without merging, or edited by hand in the repository, was never
overwritten. It now compares content, exactly as the database pass does.

The object-inventory artifact deliberately keeps the existence-only check: it is streamed precisely
so it is never held whole, and re-reading it to compare content would undo that at VLDB scale, where
it runs to hundreds of megabytes. The gap that leaves — a manifest that exists but is stale — cannot
be produced by a recut, which removes the file outright; it needs a file edited by hand. The reason
is recorded beside the code rather than left to be rediscovered.

### Internal

- Schema migration **V014** adds `runs.objects_restored`.
- Two existing tests failed on the new semantics and were corrected rather than adjusted around: a
  file edited by hand is a restoration, not a modification, and the webhook contract test now pins
  the added field. Both were the tests doing their job.

## 0.13.0 - 2026-09-07

**One branch, one pull request, and an honest count of what changed.** Found while testing 0.12.2
against a new 13,300-object database whose pull request was waiting for approvers — the condition
that turns three separate latent defects into visible ones at the same time.

Test suite: 1,399 → 1,408.

### Changed — pull-request mode reuses one head branch per job

The head branch carried the **run key**, so every run cut a branch nothing would ever delete and
opened a pull request nothing would ever close. On a repository that requires approval to merge —
where an unmerged pull request is the normal state rather than an edge case — a daily job
accumulated a branch and an open pull request **per day, indefinitely**, each one restating the same
proposal. Nothing in the product had ever deleted a branch or closed a pull request.

The branch is now `obsync/<job-slug>/<job-id>`, stable for the life of the job. The job id is there
because two jobs whose names slugify identically would otherwise share a branch and overwrite each
other's proposals — the run key had been hiding that collision by accident.

The branch is still **recut from the base every run**. That invariant is what makes a closed-unmerged
pull request recoverable and what makes a deletion tombstone retire correctly, and none of it
changes. What changes is only how the recut branch is reconciled with its own previous push:

- **The remote already carries this exact tree** — push nothing. The open pull request proposes
  precisely this content already, and restating it would dismiss reviewer approvals on a repository
  configured to dismiss them, for no change at all. An unchanged run now touches the remote not at
  all.
- **Otherwise** — merge the remote head in with `-s ours`, which keeps the recut tree wholesale while
  making the remote's tip an ancestor, so the push is an ordinary **fast-forward**.

`--force-with-lease` was the obvious choice and it is the wrong one, for two independent reasons.
A repository whose rulesets block non-fast-forward updates refuses a force push outright, which
would wedge pull-request mode completely — and rulesets are exactly what this deployment uses. And
the lease would protect nobody even where force is permitted: `PrepareAsync` fetches at the start of
every run, which refreshes the very remote-tracking ref the lease is taken against. Both were
verified against the bundled git before any code was written.

Merging also keeps a reviewer's own commits on the branch in the history rather than deleting them,
while still letting Obsync's scripted content win.

This makes the adopt-rather-than-duplicate path built in 0.12.0 reachable for the first time: GitHub
answers the second create for the same head with 422, which is already classified as "could have
taken effect", so the existing pull request is adopted and **its number stays put across runs**.

**Upgrading:** head branches created by earlier versions keep their old names, so the pull requests
already open against them are orphaned — Obsync opens one pull request on the new branch and will
never touch the old ones. Close them and delete their branches once you have decided which proposal
you want.

### Fixed — every object reported as modified on a run where nothing had changed

A regression introduced in 0.12.1. The unchanged-object self-heal compares the file's **bytes** —
correctly, because a file edited by hand in the repository must be overwritten. But the bundled
MinGit ships `core.autocrlf=true`, which the git hardening deliberately does not override, while
Obsync writes LF. So after any clone or re-clone every file on disk is byte-different from what was
scripted, even though the blob git stores is identical.

The whole estate was therefore reported as modified and rewritten — and produced **no commit at
all**, because git's clean filter maps the rewritten files straight back to the same blobs. A length
pre-check made it worse by short-circuiting on a difference that was never real.

Line endings are not content: the comparison now falls back to a normalized one, and only a CR that
immediately precedes an LF is collapsed, so a lone CR inside a string literal still counts. The
divergence probe that decides whether a pull request was closed unmerged had the same blind spot and
would have blamed a fresh clone on a reviewer.

### Fixed — a proposal could carry only part of the changeset

The divergence probe treated a **missing** file as harmless, reasoning that the existing self-heal
rewrites those. That is false for an object the incremental planner skipped: a planned skip writes
nothing at all and never reaches the self-heal.

Pull-request mode recuts its head from the base every run, so while a pull request sits unmerged
every file it proposed is absent from the working tree — and incremental scripting is **on by
default**. The filter therefore dropped exactly the objects the recut had deleted, and the run
committed a fragment. Merging that fragment would have landed part of the estate on the base branch
with every state row still recording "delivered", and nothing would ever have re-proposed the rest.

Missing-file detection is no longer gated behind script normalization — it needs no hashing, and it
guards against losing content rather than merely against a degraded experience.

### Fixed — the run tiles counted two different populations

`Scanned` excluded the per-database artifact files (object inventory, database options, permissions,
security review, documentation) while `Added` / `Modified` / `Deleted` included them, because an
options-only change must still read as a change. A 13,300-object database therefore displayed
"13,300 scanned / 13,305 modified" and looked, reasonably, like a bug. Artifacts now count in both.

`Skipped` was bound to the **failure** counter, so a run where twelve objects could not be scripted
displayed "12 skipped", which reads as benign and is not. That counter covers genuine failures and
objects that cannot be scripted at all (encrypted modules, CLR), so it is now labelled **Not
scripted**, on the job page, in history, and in the exported run report.

### Internal

- The head-branch reconciliation is covered by five tests against a real git binary and a throwaway
  bare repository — identical content, changed content, a commit pushed by somebody else, a base
  branch that moved, and an object dropped before the proposal merged. Each was confirmed to fail
  with the reconciliation removed.
- The deletion-tombstone hazard left open in 0.12.2 — a dropped object whose file was never on base,
  orphaned if an older pull request later merged — closes with the branch change rather than with new
  state: there is only ever one open proposal, and every run replaces its content with the current
  estate. It has its own test rather than an assurance.

## 0.12.2 - 2026-09-07

**A deletion is remembered until the branch confirms it.** Completes the family of bugs that
appeared when a pull request was closed without being merged. Additions were always recovered;
modifications were fixed in 0.12.1; deletions were the one left.

Test suite: 1,395 → 1,399.

### Fixed — a deletion proposed in a pull request that was closed unmerged was lost

Additions and modifications recover because the working tree is compared against a state row that
still exists. A deletion has no such row **by construction** — dropping the row *is* how a delivered
deletion was recorded.

So in pull-request mode, where delivery means **proposed** rather than landed, a reviewer closing the
pull request left the object gone from SQL, its file still on the base branch, and nothing connecting
them. The file was orphaned in the repository permanently, the deletion was never proposed again,
and every run reported success throughout. On an enterprise GitHub that requires approval to merge,
an unmerged pull request is the normal state, which is what made this reachable rather than
theoretical.

The state row is now retained as a tombstone until the tree confirms the file has gone, and retired
quietly when it has. Retiring matters as much as retaining: without it the tombstone would propose an
empty deletion on every run, forever.

A deletion whose file is already absent is also no longer reported as a deletion in pull-request
mode. It inflated the run's counts and produced an empty commit for something that was bookkeeping
rather than a change.

**Direct-commit mode is deliberately untouched.** Only pull-request mode may treat an absent file as
proof the deletion landed, and the reason is specific: pull-request mode recuts its head branch from
the base every run, so its working tree is a faithful mirror of base. Direct-commit mode has no such
guarantee — a run that deleted the file and then failed to commit leaves it gone locally while the
remote still has it, and preparing the workspace does not always restore it. Reading that absence as
"already deleted" would retire the tombstone and orphan the file on the remote: the same bug arrived
at from the other side. An existing test, written for exactly that scenario, caught the first version
of this fix making precisely that mistake.

### Known remaining gap

Pull-request mode still opens a new pull request on a new timestamped branch every run, so unmerged
proposals accumulate rather than updating one pull request, and merged head branches are never
deleted. That is a design change with real trade-offs — in particular, force-updating a branch under
an open pull request would let a reviewer's approval transfer to content they never saw — and it is
deliberately left to its own release.

## 0.12.1 - 2026-09-07

**Telling the truth about credentials, and about what the repository actually contains.** Two
findings from a live enterprise deployment, both of which could mislead silently for a month or
lose work outright.

Test suite: 1,381 → 1,395.

### Fixed — a revoked token displayed as "Valid"

Octokit's `ForbiddenException` is a **sibling** of `AuthorizationException`, not a subclass, so every
HTTP 403 fell past the 401 arm into the generic one and was reported as a check that could not *run*
— which by design leaves the stored verdict untouched, because a network blink says nothing about a
credential.

But 403 is exactly what GitHub returns for **SAML/SSO deauthorization** and for **an organisation
withdrawing a fine-grained token's repository grant** — the two most common enterprise revocations.
The badge therefore stayed green for up to the full 30-day decay window while every push failed. A
403 that is not a rate limit is now a verdict rather than a failed check.

The catch ordering matters and is not obvious from reading it: all three rate-limit types *derive*
from `ForbiddenException`, so they must be caught first or a rate limit would be recorded as a
rejected credential. The compiler enforces the order; a test now records why it is what it is.

Also fixed on that screen:

- **A repository with no token saved at all** displayed "Valid". That is the most certain evidence
  the product ever has, and it was being discarded. It is now recorded as Failed.
- **A check that genuinely could not run** left the page asserting two contradictory things side by
  side — a failure message next to a green badge, with nothing to reconcile them. The message now
  states that the status shown is from the last completed check and has not been changed.
- **The dashboard and the Repositories page disagreed.** The dashboard judged repositories on the
  raw stored status while the page rendered the decayed one, so a stale failure appeared as a red
  "failed its last check" row beside a neutral "Not validated" pill for the same repository — and
  collected a second row from the staleness loop for the same underlying fact. Both now read the
  effective status.
- **A read-only token raised nothing on the dashboard**, though it is the failure this checker was
  built to catch and every push-based job on it fails. It now raises a warning.

### Fixed — a change proposed in a pull request that was closed unmerged was lost

In pull-request mode "delivered" means **proposed**, not landed. If a reviewer closes the pull
request without merging, the base branch keeps the old file while Obsync's state records the new
hash as delivered. Two checks then both passed — the hash matched, and the file existed — so the
object was never written again. The modification was silently dropped from every later run while the
job reported success.

On an enterprise GitHub that requires approval to merge, an unmerged pull request is the **normal**
state rather than an edge case, which is what makes this reachable rather than theoretical.

Two changes were needed, and only the pair works:

- The self-heal for an unchanged object now compares the file's **bytes**, not merely its existence.
  That also covers a file edited by hand in the repository, which Obsync should overwrite because it
  is the source of truth.
- Incremental scripting filters at the *provider*, and a planned skip writes nothing at all — it
  marks the object seen, counts it scanned, and returns. So a skipped object never reaches that
  self-heal, and the content check alone would have fixed nothing in the default configuration.
  Pull-request runs now withhold the incremental filter from any type whose tracked files no longer
  carry what was recorded as delivered, at the cost of one full scan of that type on the run that
  notices. Pull-request mode only: it is the only mode where the tree can legitimately disagree with
  our state.

**Known remaining gap:** a *deletion* proposed in a pull request that is then closed unmerged is
still not re-proposed — its state row is removed on delivery, so nothing tracks it afterwards. That
needs a tombstone and is deliberately left to its own change.

### Internal

- The delivery-gate fixture substituted `IModifiedObjectReader` with a no-op, so the modification
  snapshot came back empty, the incremental planner had nothing to plan, and **every incremental
  assertion in that file was silently vacuous**. Three separate attempts to demonstrate the skip
  behaviour passed against code with the fix removed, for that reason rather than because the fix
  was unnecessary. The snapshot is now modelled properly.
- The fake script provider now honours the incremental watermark filter; it ignored it entirely,
  which made every incremental test unable to observe the difference between filtered and not.
- Each fix was reverted individually to confirm its tests fail.

## 0.12.0 - 2026-09-07

**A lost confirmation is not a failed operation.** Four defects of one shape, found by auditing the
product for the class of bug a customer hit in 0.11.3: GitHub created a pull request, the connection
dropped before its response arrived, and the run reported that it could not be opened.

A minor rather than a patch release because one fix changes behaviour someone could have been
relying on — transient push failures now genuinely retry, where before they failed immediately.

Test suite: 1,365 → 1,381.

### Fixed — pushes

- **A push that could not communicate was read as a push that did not happen.** git can send the
  pack, the server can accept it and update the ref, and the connection can drop before the reply
  arrives — or Obsync's own command timeout can kill git mid-conversation. Reported as a failure that
  costs a false alert, and in pull-request mode the next run cuts another head branch and pushes the
  same content again. After an *ambiguous* failure the push now asks the server: it compares origin's
  copy of the branch against local HEAD with `ls-remote` and treats a match as the success it was.
  The comparison is deliberately against the server rather than the local remote-tracking ref, which
  a push git never saw succeed has not updated — using it would answer "not pushed" for precisely the
  case this detects. A rejection is not ambiguous, so a protected branch, a ruleset or a
  non-fast-forward goes straight to failure without a wasted round trip.
- **The retry count in the job wizard did nothing for pushes.** `failed to push some refs` was
  classified as a permanent failure, and permanent markers are tested first and short-circuit — but
  it is git's generic *trailer*, printed under a dropped connection exactly as readily as under a
  rejection. Its presence made every push failure permanent. It is gone, and each rejection GitHub
  can actually give (`GH001`, `GH006`, `GH013`, push declined, protected branch) now has its own
  entry, so the cause decides rather than the trailer.

### Fixed — work marked delivered that was not

Direct-commit mode recorded objects as delivered **before** the push, on the reasoning that a local
commit is durable because the next run re-pushes a stranded one. The re-push is real; the durability
is conditional, and the condition is that the clone survives. The corrupt-workspace self-heal
re-clones from scratch, changing the workspaces root in Settings abandons the old location, and a
backup restore or antivirus can remove the directory outright.

After any of those, with state already advanced, every affected object's stored hash matched content
that had never been delivered — so the job reported **NoChanges forever** against a repository that
never received the work, while the run row asserted a commit SHA that existed on one machine only.

Delivery now means the push landed, exactly as pull-request mode has always meant the pull request
opened. Nothing is lost by waiting: a stranded commit is still pushed by the next run, and state
advances then.

### Fixed — duplicate incidents from a duplicate alert

Alert delivery is retried once on any failure, including a timeout — and a timeout cannot distinguish
"the endpoint never received it" from "the endpoint received it and the acknowledgement was lost". A
duplicate email is untidy; a duplicate POST to PagerDuty, ServiceNow or Jira opens a second incident
for one event, and nothing in the payload let a receiver tell a re-send from a new event. Deliveries
now carry a stable `Idempotency-Key` header, and the run's identity (`runId`, `runKey`,
`idempotencyKey`) travels in the body for receivers that cannot read headers. The existing payload
fields are unchanged.

### Internal

- The delivery-gate test that asserted the old direct-mode rule was rewritten rather than deleted,
  with the reasoning for the reversal recorded in it — and a new test proves the stranded commit is
  still delivered by the next run, since that was the entire justification for the old design.
  Reverting the change fails 18 tests.
- Each fix was reverted individually to confirm its tests fail.

## 0.11.4 - 2026-09-06

**Telling the truth about GitHub.** Every fix here came out of one real deployment, in order, as each
one uncovered the next. Two are correctness bugs; the rest are the product describing what happened
instead of guessing.

Test suite: 1,321 → 1,365.

### Fixed — a pull request that existed was reported as failed

The serious one. `client.PullRequest.Create` — a POST — was wrapped in a retry helper that treats a
transport error as transient. A request reached GitHub and **created the pull request**, the TLS
connection dropped before its response returned, the helper retried, and the run finished by
reporting that the pull request could not be opened. It was open on GitHub the whole time.

The cost was not only the wrong message. The engine marks a run's objects as delivered only when
that call succeeds, so a false failure left the state un-advanced — and the next run would cut a
fresh timestamped head branch and open a **second** pull request for the same content, once per run,
until somebody noticed.

A client cannot distinguish "the request never arrived" from "the request was applied and the reply
was lost", so it no longer tries: on any failure it asks GitHub whether an open pull request now
exists for this head and base, before retrying and before giving up. Finding one is proof the work
landed. That also covers GitHub's 422 for a duplicate, which is the same situation from the other
side. Reconciliation is skipped for outcomes that cannot be ambiguous — a rejected token creates
nothing — and an empty result from the server-side `head` filter is not taken as proof, because that
filter is documented only as `user:ref-name` while these head branches contain slashes; it re-asks
without the filter and matches locally.

### Fixed — a push GitHub refused for policy reasons

- **`GH013` was not handled at all.** The engine understood `GH006` (classic branch protection) and
  `GH001` (file size), but not the code GitHub emits for **rulesets** — its current branch-policy
  mechanism, where classic protection is the legacy one. The modern and more common case fell
  through with no guidance, though the product already knew how to say "switch to Pull request mode"
  for the legacy one. It is now recognised, and quotes the violated rules: GitHub prints them as
  bullets under the code, and those bullets are the only part that says what to do.
- **The arm below it matched the bare word "rejected".** git prints `! [remote rejected]` for every
  server-side refusal, so any rejection without its own explicit arm was diagnosed as *"the remote
  branch has commits Obsync does not have — pull/merge the branch"*, sending the user to fix a branch
  that was perfectly up to date. The match is now limited to phrases that genuinely mean the branch
  is behind, and an unrecognised refusal says so plainly instead of guessing.
- **Preflight looked for the wrong mechanism.** The branch check read the branch object's `protected`
  flag — chosen because the classic protection endpoint needs admin — but that flag was built for the
  classic system, and a boolean could never name *which* rule applies. A check that exists to prevent
  exactly this surprise could not see it. It now asks `GET /repos/{owner}/{repo}/rules/branches/{branch}`
  first, which needs no admin and covers organisation-level rulesets as well as repository ones, and
  it names the rule. The classic flag remains as a fallback.
- git's stderr was truncated at 500 characters — enough to lose the bullets that say what to do. Now
  2,000, still bounded because the text is persisted into run history, reports and support bundles.

### Fixed — "see inner exception", and the code that never did

Every `HttpRequestException` catch in the GitHub client reported `ex.Message` and nothing else. For a
TLS failure .NET puts the stage in the outer message and the **cause** one level down, so the product
was reliably printing the half that says nothing — including the sentence whose own last three words
are an instruction to look further.

The chain is now flattened, deduplicated and capped, and a new explainer covers the .NET-shaped
causes: an untrusted chain, an expired certificate (which points at the system clock, not at a
corporate root), a hostname mismatch, a blocked revocation check, a proxy 407, DNS, a refused
connection. It is deliberately separate from git's explainer, whose advice — *"set the TLS backend,
or supply your CA bundle, in Settings → Network"* — cannot work here: git reaches `github.com` over
MinGit with a configurable backend, while the REST client reaches `api.github.com` over .NET, always
schannel, always the Windows certificate store. The new messages say so.

The engine now also states what only it can state as fact: that the branch reached GitHub. The push
returned success moments earlier, so "could not reach GitHub" would otherwise read as a total outage.
It names the pushed branch too, because each attempt cuts a new one and nothing else reported which
were left without a pull request.

### Internal

- The retry helper carries an explicit warning that it is only safe for idempotent calls — and that
  invariant is now asserted by a test that parses the source, because the remark asserting it was
  already false when written: a second POST was still flowing through it.
- Two review passes over the pull-request fix found three bugs in it, all corrected here: its own
  lookup had reproduced the cancellation-versus-timeout mistake the fix was written to correct; the
  policy documented a precondition instead of enforcing it, leaving a throwing lookup able to destroy
  the original failure; and the test claiming to cover that passed a lookup returning null rather
  than one that throws, making it a duplicate of its neighbour.

## 0.11.3 - 2026-09-06

**Uninstall hardening**, from a four-agent review of the removal path — and a correction to 0.11.2.

The MSI half of uninstalling was already right: every artifact the installer creates was traced to a
removal path and found to have one. What was wrong sat either side of it — a shutdown budget aimed
at the wrong deadline, and a decommissioning story that existed only in the source.

Test suite: 1,305 → 1,321.

### Fixed

- **The service's stop budget was three times longer than anything waits.** 0.11.2 gave the host 90
  seconds to drain and terminated the process at 92 if it had not, so that an installer would never
  copy files over a live process. Windows Installer waits **a maximum of 30 seconds** for a service
  to stop — documented on the `ServiceControl` table's `Wait` column, and not configurable from the
  package — and then proceeds to delete the service and remove files regardless. The terminate was
  therefore firing about a minute after the handles it was meant to release had already been
  deferred to a reboot: it protected nothing, on the uninstall path and equally on the upgrade path
  it was written for. The budget is now 20 seconds with a 2-second grace, sized against the
  installer's cap, and the arithmetic is asserted rather than assumed. Shrinking it costs little —
  the cooperative path finishes in seconds, and anything that does not is recovered at next start.
- **Terminating the service orphaned its `git.exe` children.** Killing the host ends only the host,
  and git is not an unrelated program: it runs from `tools\git\` inside the very folder an uninstall
  is deleting and an upgrade is replacing, so an orphan held open exactly what the terminate existed
  to release. Killing our own process tree is not possible — `Process.Kill` refuses when the tree
  contains the caller — so running git processes are now tracked and swept first.
- **An older build silently adopted a newer database.** The migration runner only ever asked "have I
  applied this?", so a `__migrations` row for a version it had never heard of was invisible:
  everything it knew was already applied, nothing was pending, and it started against a schema from
  the future without so much as a log line. Uninstalling is the way in — the MSI's downgrade block
  matches *installed* products sharing the UpgradeCode, and an uninstall deregisters the product
  first, while the data root survives both because it lives outside the install folder. It now
  refuses to start, naming the migrations it does not recognise.
- **Log files were bounded on one axis only.** The file count was capped at 31 while Serilog's
  default per-file cap is 1 GB. That matters most where nobody is looking: a service left on the
  installer's Local System default writes forever while doing no useful work, into a directory that
  resolves inside `C:\Windows\System32\config\systemprofile` and that an administrator cannot browse
  without taking ownership.
- `INSTALL.md` claimed the 0.11.2 unattended-uninstall fix landed in "0.12.0". It shipped in
  **0.11.2**, and the note now also covers retirement rather than only upgrade.

### Changed — documentation

The uninstall was structurally clean and almost entirely undocumented. `INSTALL.md` now answers the
questions it raises:

- **The pre-uninstall credential step has moved to where someone uninstalling will find it** — it
  was an H3 filed under *Silent install*, three sections above the uninstall commands, and it never
  mentioned that `obsync.exe`, the tool it tells you to run, is removed by the very uninstall it
  must precede.
- **What uninstalling leaves behind**, named: the database, the git clones (usually the largest
  item), logs and locks, the per-account credential vaults, the retained "Log on as a service"
  right, and the Local System data root under `System32` — with the commands to remove them.
- **A recovery path for anyone who has already uninstalled.** The credential key prefix
  (`Obsync:GitHub:<id>` and friends) and the `cmdkey` commands appeared in no user-facing document,
  so a live GitHub token could sit in a vault with nothing able to name it.
- **Fleet retirement**, documented for the first time — including that Add/Remove Programs
  advertises `MsiExec.exe /I{ProductCode}`, which is *maintenance mode*: a retirement script that
  reads `UninstallString` and appends `/qn` silently removes nothing and exits 0. Resolve the
  ProductCode at run time instead. Exit codes 1605 and 1641 are added, and a `/qn` uninstall cannot
  close the desktop app across sessions, so it returns 3010 with the Add/Remove Programs entry
  already gone.
- **If an uninstall fails part-way** — a rollback restores the product but not the service's
  recovery actions, its delayed start, or its logon password, and none of that is announced.

### Internal

- The stop budget, its drain share, its grace and the installer's cap now live together and are
  asserted against each other, so the relationship survives someone changing one number.
- Seven packaging tests pin the uninstall invariants: no permanent components, the PATH entry
  removable, every component reachable from the feature, exactly one custom action and it cannot run
  on uninstall, the service stopped-and-removed with a synchronous stop, and nothing in the package
  addressing a path outside the install folder — which is what makes retained user data safe by
  construction rather than by policy.
- Both new guards were reverted individually to confirm their tests fail.

## 0.11.2 - 2026-09-06

**Upgrade hardening.** A five-agent review of the upgrade path found that the product upgraded
cleanly by hand and failed the way an enterprise actually deploys it: silently, by a fleet tool,
while the app and the service were running. Ten defects, three of them release-blocking.

Test suite: 1,259 → 1,305, including a new `Obsync.Service.Tests` project.

### Fixed — unattended install, upgrade and uninstall

- **A password-account install could not be uninstalled, repaired, or self-repaired unattended.**
  The silent-install guard was written to mean "an account was passed without a password", but
  `LaunchConditions` runs at sequence 100 while `AppSearch` is at 50 and `SetSERVICE_ACCOUNT` at 52
  — so by evaluation time `SERVICE_ACCOUNT` had already been filled in from the SCM by the
  installer's own registry search. It could not tell an operator's answer from its own memory. Both
  actions also carry an empty sequence condition, so they run on uninstall and repair too. The
  result was `1603` from `msiexec /x /qn`, `msiexec /fa /qn`, and the MSI self-repair that the Start
  Menu shortcut triggers — which breaks SCCM supersedence and Intune retirement, and needs no
  password in the first place. A leading `Installed` term now draws the line where the service is
  actually re-created. Verified by evaluating the built package's condition through MSI's own
  evaluator across eleven scenarios.
- **`msiexec /qr` installed a service that could never log on.** The guard exempted UI level 4, but
  `INSTALLUILEVEL_REDUCED` suppresses the wizard dialogs, so the Service Account page never ran and
  nothing collected the password. Only full UI is exempt now.
- **An upgrade relocated a non-default installation.** An upgrade is a fresh install under a new
  ProductCode, so `INSTALLFOLDER` fell back to `Program Files`: anyone who had installed elsewhere
  was moved with no prompt and exit 0. It also defeated files-in-use detection outright, because the
  new target paths were then paths nobody held open — Restart Manager found nothing to close while
  the old folder was demolished around a running app. The directory is remembered now, and published
  as `ARPINSTALLLOCATION`, which was empty, so an admin could not even read the current path back
  out of Add/Remove Programs.
- **Rebuilding the same version installed a second copy** rather than upgrading, leaving two
  identical Add/Remove Programs entries over one refcounted set of components, where the first
  uninstall removes nothing at all. Re-running the *same* `.msi` file was always safe; this needed a
  rebuild, which a re-run release workflow produces.
- **Uninstalling revoked "Log on as a service"** — WiX defaults `RemoveOnUninstall` to yes and the
  authoring's comment claimed otherwise. Worse, that revoke is a *commit* action while the new
  product's re-grant is undone by its rollback action, so a **failed upgrade** left the service
  account without the right, and the resulting error 1069 reads as a bad password.

### Fixed — stopping and restarting the service

- **The service reported a stop it had not achieved.** The framework's Windows service lifetime
  waits out its shutdown budget and then returns regardless — nothing kills the process — so the SCM
  was told SERVICE_STOPPED while the process was still running a sync and still holding every DLL in
  the install folder. MSI's `Wait="yes"` was satisfied, so it copied files over a live process and
  started a second service beside the first. It also published `waitHint = 0` with a static
  checkpoint for the whole stop, the documented signature of a hung service, so nothing waited the
  configured 90 seconds anyway. The service now heartbeats its progress to the SCM and terminates if
  the budget is exceeded.
- **The shutdown budget was spent before anything was cancelled.** Hosted services stop in reverse
  registration order, and the in-flight-run canceller was registered *before* the scheduling
  bootstrapper — so the bootstrapper's two shutdown database writes, contending with the very run
  nothing had yet asked to stop, could consume 60 of the 90 seconds first.
- **Answering "do not close applications" produced a half-upgraded install.** All three hosts share
  one flat folder of libraries, so the service binary was replaced while the shared DLLs the running
  app held were deferred to a reboot — and the new service started immediately against the old
  engine, including the catch-up run it issues at startup. It now checks the versions beside it and
  refuses to start, naming the remedy.
- **The "a sync is still running, close anyway?" prompt blocked Restart Manager.** WPF raises
  `Closing` even when Windows is ending the session, so the installer's request to close got a modal
  dialog behind its own progress window — which is how users ended up on the deferred-file path
  above. It no longer prompts on a session end.

### Fixed — update notification

- **It said none of what the product already knew.** The notification offered a version and a link
  while the installer source and `INSTALL.md` between them documented four preconditions that decide
  whether the upgrade works. They now travel with the offer.
- **A failed check cost a machine its whole day.** The 24-hour throttle was stamped *before* the
  request, so a laptop offline at login, a proxy hiccup, or a shared egress IP that had spent
  GitHub's unauthenticated 60-per-hour budget burned the entire window — and behind one NAT the same
  machines starved every morning and never learned about an update. Stamped on success only. An
  exhausted rate limit is now reported as the shared-network limit it is, rather than as an HTTP
  error that sends people to investigate their proxy.

### Added

- **`SHA256SUMS.txt` on every release.** Not a substitute for the code signature the MSI still lacks
  — integrity, not origin — but enterprises that must allow-list by hash had nothing authoritative
  to quote.
- **The release workflow refuses a version that is not exactly three numeric fields.** Windows
  Installer compares only the first three fields of `ProductVersion` and ignores the fourth, so two
  releases differing only there install side by side instead of upgrading — and `wix build` accepts
  a four-field version without a warning.
- `INSTALL.md` gains what a fleet needs: what to close before upgrading and why an open service list
  surfaces as the misleading error 1923, detection by UpgradeCode rather than the per-build
  ProductCode, the exit-code table that stops a good `3010` being reported as a failure, and the
  recovery drill for a part-way failure — including the trap where a bare retry finds no remembered
  account, falls back to Local System, and *succeeds*, leaving a service that runs but never
  executes a schedule.

### Internal

- Database migrations had never been run against data. Every existing test initialized a brand-new
  temp file, so `V001`..`V013` was only ever exercised as "create everything in order on an empty
  database" — the one shape an upgrade never has. The two migrations that rebuild rather than append
  had therefore never touched a populated table anywhere, including the de-duplication in `V011`
  whose entire purpose is to repair databases that had hit the case-collision bug. Both are correct;
  they now have coverage, as does the concurrent-initializer design that protects the install-time
  race between the service starting and the app launching.
- Each authoring and ordering change was reverted individually to confirm its test fails.

## 0.11.1 - 2026-09-06

**Installer fixes.** The setup wizard's text has been silently clipped since 0.9.x, and the artwork
used the brand accent as a background. Both are fixed. Nothing outside `packaging/` changed, so the
engine, service and app are identical to 0.11.0.

### Fixed

- **Clipped text throughout the wizard.** The installer switched from Tahoma 8 to Segoe UI 9 without
  re-laying out its controls. That raises the GDI line box from 13px to 15px, and installer units are
  literal pixels that do *not* rescale with the font, so every box laid out for 13 was one size too
  small. Affected, and now measured rather than estimated:
  - **Account** and **Password** labels on the Service account page were 12 units for a 15-unit line,
    cut through their descenders — and their true extents ran *into* the edit boxes two units below.
  - Both explanatory notes on that page were 24 units against text wrapping to three lines, so a
    third of each paragraph never rendered.
  - The "enter a password" dialog was 60 units against 75 needed; its last line was invisible.
  - The "enter an account" dialog was 40 against 45.
- Copy on those pages was rewritten to fit its boxes, rather than boxes being grown past the point
  the dialog can hold them.

### Changed

- **The welcome and finish artwork.** The side panel was a flat fill of `#1B17FF` across 29% of the
  window. That token is named *AccentColor* in the app's own palette, whose actual surfaces are
  `#F8FAFC` and `#111827` — an accent was being used as a ground, which is what made the wizard read
  as dated beside the application. It is now a deep gradient holding the brand hue at the top where
  the mark sits and settling into indigo ink, with a soft glow and two faint arcs echoing the mark.
  The mark is 56px rather than 92px, and the panel content is left-aligned on a margin.
- **The page banner.** Its 2px full-saturation rule is now a hairline in the app's border colour. It
  previously sat directly above the system's own etched separator, so every inner page drew two
  competing lines under its header.
- **Text is coloured for the first time.** There was previously not a single colour anywhere in the
  installer — every string was system black on system grey, and the only brand presence in the whole
  wizard was two bitmap files. Page titles now use the app's AccentHover, body text its TextPrimary,
  and the secondary notes a new muted style at 8pt. (`TextStyle` is the only colour lever MSI offers:
  there is no colour column in its Dialog or Control tables at all.)

### Internal

- `DialogTextFitTests` measures every Text control in every locally authored dialog with the same GDI
  API MSI paints with, and checks that no two controls overlap and nothing extends past its dialog.
  Nothing previously asserted on a single coordinate, height, font or bitmap dimension, which is why
  the clipping shipped. Character budgets are not sufficient — the same control width fits 56 or 61
  characters depending on where the words break — so the strings are measured, not counted.
- Verified by building the MSI and reading its `Control` and `TextStyle` tables back out, then
  rendering the Service account page from those tables.

## 0.11.0 - 2026-09-06

**Production hardening.** A five-agent adversarial review of 0.10.1.1 found 41 defects; this
release fixes all of them. It was triggered by a real deployment where the Add Repository dialog
showed five green ticks and the first run died on `git clone` — the checks used a different HTTP
client, over a different transport, than the runs they were vouching for.

Test suite: 1,149 → 1,247.

### BREAKING

- **`obsync run` is now treated as unattended.** It passed `RunTrigger.Manual`, which disabled the
  mass-deletion safety stop, the disabled-job gate and the maintenance window — on the product's own
  automation entry point, where nobody is present and the object counts are printed only *after* the
  push. It now refuses a disabled job, or one outside its maintenance window, with **new exit code
  4**, and `Skipped` maps to 4 rather than 0. Scripts relying on the old behaviour will start
  failing: enable the job, widen the window, or use Run Now in the app.

### Fixed — data loss

- **The mass-deletion safety stop exempted small scopes.** `candidates.Count > 50` was an
  unconditional pass, so a 40-object database whose login lost `VIEW DEFINITION` had every file
  deleted, committed, pushed and its state rows dropped on a *scheduled* run — reported as
  Succeeded. A 4,000-of-10,000 loss also slipped the majority test. Replaced with four rules: total
  wipe at any size, ≥100 absolute, majority above a floor, and whole-schema wipe. The ratio now
  divides by in-scope rows, since counting deselected types diluted it.
- **An ignored destination was read as "identical tree".** `git add -A` honours `.gitignore`, so a
  database named `Bin`, `Temp`, `Logs` or `Build` — or a `*.sql` rule in an application repository —
  staged nothing, which the engine took as proof the repository already held the content. It marked
  the changes delivered and persisted every hash, so later runs matched and wrote nothing: NoChanges
  forever against a repository that never received a single object. The engine now asks git via
  `check-ignore` and fails with the matching rule quoted.
- **Missing `VIEW DEFINITION` committed hollow scripts as success.** SQL Server returns `NULL` from
  `sys.sql_modules.definition` rather than erroring, so SMO scripted objects as empty over correct
  ones. Preflight now opens each selected database *by name* and counts unreadable definitions.
- **A recovering database discarded every other database's completed work.** 922/927 were not in the
  transient list while 4060 (offline) was, so two operationally identical states behaved completely
  differently: one committed everything else, the other committed nothing at all.

### Fixed — security

- **Git hardening was bypassable from the inherited environment.** Only `GIT_ASKPASS` and
  `SSH_ASKPASS` were removed. `GIT_CONFIG_PARAMETERS` is applied *after* the numbered `GIT_CONFIG_*`
  block, so it beat every hardening key — re-enabling `protocol.ext.allow` made an `ext::` remote
  execute a shell command. `GIT_DIR` redirected every add/commit/checkout away from the working
  directory, and `GIT_ALLOW_PROTOCOL` replaced the transport allow-list wholesale. Setting a user
  environment variable needs no privilege, which made this an easier path than the machine gitconfig
  the hardening was written against. 26 variables are now stripped, plus any inherited numbered
  block.
- The support bundle scrubbed every JSON entry but copied Serilog log files verbatim.

### Fixed — checks that did not check

- **No validation surface exercised git at all.** Preflight, Add Repository and Diagnostics all used
  Octokit over `HttpClient` against `api.github.com`, while runs use bundled MinGit over schannel
  against `github.com`. .NET does not check certificate revocation and git-for-Windows does, so a
  firewall blocking the CA's responder left every tick green and killed every clone. Added a
  `git ls-remote` probe wired into both, configured by the same code path a real clone uses.
- **"Write / push — Contents" did not mean a push would be accepted.** `permissions.push` is the
  collaborator role and stays true under branch protection. Preflight now warns first.
- **Preflight verified under the wrong identity.** Every probe ran as the signed-in user while
  scheduled runs execute as the service account, and both the credential vault and `%LOCALAPPDATA%`
  are per-account. A new **Run identity** check names both accounts and states plainly that nothing
  above transfers when they differ.
- Alerting was green in test and silently dead in production: the test button sends from the app
  under the signed-in user, scheduled runs send from the service, and the failure was logged and
  swallowed. Delivery outcomes are now recorded and surfaced on the dashboard.
- Repository validation badges never decayed — a token validated in January and expired in March
  still read "Valid" in September. They now expire after 30 days, and repositories finally raise
  "Needs attention" rows (servers always had them; repositories had no equivalent loop).

### Fixed — occurrences that vanished

- The repository lock and the credential reads both threw *above* the run insert, so an occurrence
  left no history row, no alert and no audit event while the next-run time advanced — the job simply
  appeared never to have fired. Both now record a Failed run.
- A stranded `config.lock` or `refs/heads/<branch>.lock` wedged a workspace permanently: only
  `index.lock` was cleaned, and the corrupt-workspace self-heal is gated on fetch failing, which
  neither of those causes. All git lock files are now swept.
- The command timeout was chosen by "does this carry secrets" rather than by cost, so `add`,
  `commit` and `checkout` got the two-minute budget meant for index operations. At the scale this
  product designs for that killed the run — and since state correctly did not advance, every later
  run repeated the work and failed identically, forever.
- The data root could resolve to a **relative** path when a service account had no loaded profile,
  so the service silently used `C:\Windows\System32\Obsync` and reported itself perfectly healthy.
- The server-level pass had none of the containment the database pass has, and it runs first — so a
  login that could not open its default database failed the whole run before any work was done.

### Added

- **`obsync credential set | list | delete | prune`** and **`obsync whoami`**. Credentials are
  per-Windows-account, and the installer recommended a gMSA — whose password is machine-managed, so
  it cannot be signed in to and the app cannot be run as it. No supported path could write into its
  vault. A console *can* run as those accounts (`psexec -s`, a scheduled task), so this closes it.
  Values are read from stdin or a hidden prompt, never from the command line. `prune` removes
  secrets whose profile was deleted — the key embeds the profile id, so nothing could name them
  before.
- **Settings → Git TLS.** Windows (schannel), Windows without revocation checking, or OpenSSL with
  an optional CA bundle. Obsync ships its own git so nobody has to install one, and then offered no
  way to configure it — the only remedy for a blocked revocation responder was to find the hidden
  binary and run `git config` by hand.
- **The installer grants "Log on as a service"** for any account that is not a built-in service
  principal. `INSTALL.md` claimed it already did; nothing did, which is the Error 1069 people hit.
  Where the right is defined by Group Policy no installer can fix it, and the in-app message now
  says so instead of looping through a password re-entry that was never the problem.
- Orphaned workspaces are reported in Diagnostics with their size and reclaimable from Settings.
  Deleting a repository now reclaims its clone, which nothing ever did — the path is keyed on the
  profile id, so the directory became unnameable the moment the row went.
- The permission script grants `SELECT` when reference-data versioning is enabled. It claimed to be
  "exactly what Obsync needs" and was not for that job shape: the wizard's table picker works under
  `VIEW DEFINITION`, so the row counts looked right and the run then failed on the first `SELECT`.
- Git network attempts are configurable per job; the setting existed but was import-only.

### Changed

- Silent installs passing `SERVICE_ACCOUNT` without `SERVICE_PASSWORD` are refused rather than
  installing a service that can never log on and reporting success.
- Certificate, revocation and proxy-407 failures are classified permanent instead of retried, and
  are explained by cause — the connectivity catch-all matched first and sent people to their network
  team for a certificate-trust problem.
- Credential errors keep both the Windows description and the error code; the two-argument
  `Win32Exception` constructor had been discarding the description.
- Offline and restoring databases are shown as such in the job wizard instead of being offered
  identically to healthy ones.
- The bundled gitconfig no longer makes git-lfs mandatory — it was required and not bundled, so any
  repository where someone had enabled LFS failed its checkout fatally.
- `RunningAsAnotherAccount` now reaches the dashboard and the wizard. It is the one state Obsync
  knows in advance will fail authentication, and every banner had gated it out.

## 0.10.1.1 - 2026-09-05

**Display and layout fixes**, found by running the app and reviewing every screen against seeded
data rather than empty states.

### Fixed

- **Blurry text.** The app shipped with no application manifest, so it never declared DPI
  awareness and Windows bitmap-stretched the whole window whenever the effective scaling differed
  from the one the process started at - every glyph resampled. It bit hardest on scaled laptop
  panels, mixed-DPI desks, and Windows 365 / RDP sessions, where the session DPI can change on
  reconnect. The app now declares Per-Monitor v2, so it re-renders at the new scale instead.
- **Warning banners lost words.** The scheduler-health message sat in a horizontal StackPanel,
  which measures its children with infinite width - so the text laid out to its MaxWidth and
  everything past the panel edge was clipped mid-sentence rather than wrapped. "Set the service's
  Log On account to your Windows account" rendered as "Set the service's Log On accour".
- **Job tables were unreadable.** The sum of the columns' minimum widths came to within 20px of the
  space available, so every column sat pinned at its minimum and the proportional widths never
  applied: job names showed as "Ad-hoc...", tags as "P..", while Last Run, Changes and Next Run sat
  empty. Rebalanced, and the default window is now 1400x860 (was 1180x760) - nine columns never fit
  1180. The 960px minimum window is unchanged and still verified by TableLayoutTests.
- History's Status column clipped the "No changes" badge.

### Changed

- The repository token field is labelled **Access token**, not "Fine-grained access token". Classic
  tokens have always worked - the permission check reads the repository's pull/push flags, which
  both token types carry - but the label read as a restriction and sent people to request an
  organization approval they did not need. The hint now spells out both, including the Resource
  owner setting that otherwise silently denies access to an organization's repositories.
- When a token authenticates but cannot see the repository, the error names the likely causes
  (approval still pending, Resource owner set to a personal account, classic token not authorized
  for SSO, or a typo) instead of only saying access failed. GitHub returns 404 rather than 403
  there, so "not found" and "not granted" cannot be told apart - hence naming them all.

## 0.10.1.0 — 2026-09-04

**Service-account and scheduling-identity fixes** found by a review of the installer's Service
Account screen and everything downstream of it. Every finding was verified before it was changed —
the installer ones by building probe MSIs and dumping the emitted tables — and several were refuted
on inspection and deliberately left alone. Suite 1,112 → 1,149.

**Upgrade notes.** This release changes behaviour you may notice:

- `OBSYNC_DATA_ROOT` must now be a **fully qualified** path. A relative value resolved against each
  host's working directory — the app's install folder versus `C:\Windows\System32` for the service —
  which silently put the two on different databases. A relative value is now ignored in favour of
  the default root.
- The installer refuses a blank password for an account that needs one. Blank is still correct for a
  gMSA (`DOMAIN\name$`), a virtual account (`NT SERVICE\...`) or a built-in one (`NT AUTHORITY\...`).
- The service's shutdown budget is 90s (was 30s), so a stop during a long run has time to cancel it
  cleanly instead of being killed mid-run.

### Fixed

- **The installer discarded the service account you chose on an upgrade.** The remembered account was
  searched straight into `SERVICE_ACCOUNT`, and `AppSearch` overwrites a property that is already
  set — so a silent upgrade passing `SERVICE_ACCOUNT` and `SERVICE_PASSWORD` reset the account to the
  old one and applied the **new password to it**, and the wizard's own answer was clobbered too.
- The upgrade default now reads the service's live logon account, so a change made with `sc.exe
  config` or the services.msc **Log On** tab is no longer reverted by a repair or upgrade.
- A blank service password reached `CreateService` as NULL, installing a service that could not log
  on. A click-through upgrade reproduced this every time, because the account is prefilled and the
  password box never is.
- **A stale heartbeat was reported as a wrong-account problem**, telling you to set the service's Log
  On account to the account it already used. It is now reported as a liveness problem.
- A heartbeat dated in the future read as fresh indefinitely, so a dead scheduler could report itself
  healthy until the clock caught up.
- The app no longer says "the service is not installed — reinstall Obsync" while a scheduler is
  demonstrably running and writing to the database.
- A service running under a different account with a shared data root reported plain "Scheduling
  active", suppressing every warning, while each run failed on credentials it could not read.
- "Start the service" was the only advice offered for a stopped service, which does not help when the
  cause is a bad password or a missing "Log on as a service" right — the two causes an install can
  leave behind. The Log On tab is now named.
- A transient reconcile failure suppressed the scheduler heartbeat, so a service doing its heaviest
  legitimate work reported itself as misconfigured.
- The startup heartbeat was written after crash recovery, per-run alerts and job scheduling, leaving
  a window where a healthy service looked broken.
- **A fatal service crash was recorded to the SCM as a clean stop**, so the installer's
  restart-on-failure recovery never fired.
- The in-flight run drain could consume the entire shutdown budget, leaving Quartz none — and an
  exhausted budget turned a deliberate `Stop-Service` into a recorded failure and an auto-restart.
- A read-only leftover lock file or a denied locks folder was reported as "another Obsync process is
  running this job", skipping every occurrence forever and never alerting. It is now recorded as a
  failure naming the folder.
- The run-lock liveness probe acquired the lock to test it, so it could make a concurrent run lose
  the race it was checking for.
- A bad data root killed the service before any logger existed — no log file, no event-log entry.
- A corrupt scheduler-heartbeat row threw through the dashboard, job list, job detail and the Create
  Job wizard, which awaits it before the window is shown.

### Added

- Audit events for run-history pruning, crash recovery of an interrupted run, and service start/stop.
- `tests/Obsync.Packaging.Tests` — structural regression tests over the installer source.

## 0.10.0 — 2026-09-03

**Correctness, security and coverage fixes** from a full review of the shipped 0.9.0 tree. Every
finding was reproduced before it was changed, and several turned out to be wrong as reported — those
are corrected in `docs/quality-audit/` rather than repeated. Suite 726 → 1,112.

**Upgrade notes.** This release changes behaviour you may notice:

- Branch names beginning with `-` are now rejected. They reach git as bare positional arguments, so
  a name like `--upload-pack=…` was read as an option. If an existing job uses one, editing it will
  now ask you to rename the branch.
- A credential embedded in a repository profile's `RemoteUrl` is stripped before the URL is given to
  git. Obsync authenticates with an injected header, so nothing is lost — unless you had put a token
  there *instead of* configuring one, in which case configure the token under Repositories.
- The `ssh` and `ext` git transports are refused. Obsync's remotes are HTTPS; `ext::` runs an
  arbitrary command, and either could be reached from a rewritten URL in machine configuration.
- Duplicating a job now refuses if the source job could not run as it stands, naming the reason.
- A database containing object types Obsync does not script now finishes with a **Warning** listing
  them, where it previously reported complete success. Nothing about what is captured has changed —
  only whether the gap is reported.

### Security

- **The GitHub token is no longer sent to a host substituted by machine configuration.** The auth
  header was set unscoped, so a `url.<host>.insteadOf` entry in the system or global gitconfig
  silently rewrote the remote and the token was delivered to the rewritten host on the first
  request — with the user seeing only "repository not found". No attacker is required: an internal
  mirror or proxy distributed by ordinary configuration management is enough. The header is now
  scoped to the remote's own URL prefix.
- **Machine git configuration can no longer run programs during a sync.** Shipping MinGit reads as
  isolation but is not — the bundled system config explicitly includes Git for Windows' own — so
  `core.hooksPath`, `core.fsmonitor`, `filter.*`, `core.sshCommand` and `ext::` URLs each executed
  on Obsync's own commands. Hooks, the filesystem monitor and the credential helper are now disabled
  for every invocation, and the transport allow-list refuses `ext` and `ssh` by name. Settings that
  matter to a site — `core.autocrlf`, a corporate CA bundle, a proxy — are deliberately still read.
- **A hung git command can no longer wedge a job indefinitely.** Nothing bounded a git invocation:
  `GIT_TERMINAL_PROMPT=0` gates only the terminal fallback, and a credential helper inherited from
  the environment blocked forever holding both the job and repository locks, with the run still
  showing as in progress. An expired token was enough to trigger it. Askpass helpers are now
  disabled, commits are never signed, and every command is bounded (10 minutes network, 2 local).
- **Credentials are scrubbed from every persisted failure message**, not only the one URL shape the
  previous rule matched. It required a `user:password@` pair, so it missed `https://<token>@host` —
  the form GitHub's own documentation produces — and `http://user:@host`, which Obsync itself built
  when a proxy had a username and no stored password. Raw exception text reaching the run history,
  run reports and alert payloads is scrubbed too, and the support bundle no longer carries a
  credential embedded in a repository URL.
- The unused `DpapiSecretProtector` and `ISecretProtector` are removed. Nothing referenced them.

### Scheduling

- **A schedule its maintenance window can never admit is refused instead of silently never running.**
  The existing guard covered only Daily and Weekly; Hourly and Cron were accepted and then skipped
  on every occurrence, with a healthy trigger and an advancing "Next run". Job import applied no
  window check at all, at any cadence. A window that opens and closes at the same time is refused too.
- **The hourly "next run" no longer reports a time the job cannot run at.** The trigger's step
  restarts at midnight, so for any interval that does not divide 24 the preview drifted off the real
  schedule — landing on a time that was not an occurrence at all, or a full day late. It also fed the
  overdue badge and the missed-run catch-up, so it could raise a false alarm or fire an unattended
  run nothing had missed.
- **"Every N hours" is described honestly.** For an interval that does not divide 24 it is not a
  period: "every 23 hours" runs twice a day, 23 hours apart and then one hour apart. The schedule now
  names the times it actually runs, and the wizard shows them as you type.
- **A schedule that will never run says so** — as "Never runs" in the Jobs and Dashboard tables, the
  job header and the attention list, instead of a confident date or a blank cell.
- **A paused Cron job shows its next run again on resume**, and a completed run no longer overwrites
  the scheduler's next-run time with the one that just elapsed.
- Daylight-saving: an occurrence falling in the hour that spring-forward removes now resolves to the
  first time that exists, rather than to a time inside a gap the clock skips.

### Scale and reliability

- **The SMO prefetch memory ceiling now measures what prefetch loads.** It compared the filtered
  object count against a limit describing the cost of loading every table in the database, so a
  schema filter selecting a few hundred tables out of hundreds of thousands passed the ceiling and
  then prefetched them all, on every worker connection. The sequential path had no ceiling at all.
- **SMO connections are returned to the pool.** The primary connection for each database, and the
  one for the server-level pass, were never disconnected — only the slice workers were — leaving a
  session open on the SQL Server for each.
- **A transient failure part-way through reading a database no longer discards the whole run.** It
  escaped to the top, so nothing was committed and no state advanced, including for databases that
  had already finished. It is now contained to its own database, whose deletions are suspended and
  whose watermarks are held back so the next run re-scans it in full.
- **Objects of types Obsync cannot script are reported.** They were unreachable at every stage, so a
  database using Service Broker, certificates or external tables produced a run reporting zero skips
  and complete success. Their absence is now counted and listed. The set of types Obsync scripts is
  unchanged.


## 0.9.0 — 2026-07-16

**Production-trust and clarity release.** A full product-improvement review (assessment, plan, and
evidence in `docs/product-improvements/`) drove this release: the app now tells you when something
needs attention instead of leaving you to infer it, without changing the calm six-section design.

- **Overdue schedules are visible** — a scheduled job whose next run silently passed now shows an
  amber "Overdue" (with the scheduled time and a corrective hint) on the Dashboard, Jobs, and Job
  Workspace instead of a stale timestamp.
- **Dashboard "Needs attention" panel** — failed jobs (with the error), warning runs, overdue
  schedules, and servers failing their connection test, each with a one-click Open action. Absent
  when everything is healthy.
- **Pause, resume, duplicate, export** — pause/resume a job's schedule from the list (paused jobs
  show a neutral "Paused" badge and clear their next run); duplicate a job as a paused copy; export
  its secret-free configuration — all from a new per-row menu.
- **Server & repository health that persists** — servers show their SQL Server edition/version and
  last-checked time (captured during tests, no extra load) plus a copy-name button; repository
  validation results (including whether the configured branch actually exists) are now stored and
  shown as a status badge; a read-only token now says exactly which commit modes it breaks.
- **Wizard clarity** — every preset can show its exact included object types (sourced from the real
  preset expansion); all four commit modes have inline descriptions; the destination step
  live-previews the resolved folder and warns when another job writes to the same place; branch
  names are validated against git's rules; the databases list gained search + select-all; the
  schedule step shows the computed next run with your time zone and documents the overlap policy
  (a run still in progress means the next fire is skipped); the Review step can run optional
  preflight checks (SQL, repository + branch, export path, credentials, folder collisions) that
  never block saving.
- **History & diff** — the runs grid now shows added/modified/deleted counts separately, the run's
  trigger (manual/scheduled/startup/catch-up), and a link to the pull request it opened; filters get
  a one-click reset. The diff viewer gains change-type filters, find-in-script (F3/Shift+F3), a
  word-wrap toggle, and copy actions for object name, path, and script.
- **Diagnostics & logs** — new checks for Windows Credential Manager, data/workspace folder
  writability, and state-database integrity, each timestamped, with copy-all; a Recent-logs panel
  (severity filter, search, open-folder) inside Settings; the app's log files are now capped at 31
  days (previously unbounded).
- **Storage & About** — workspace/state-database/log sizes and free disk with open-folder buttons;
  About now shows app, engine, service, .NET, Git, and Windows versions plus the database schema
  version, with one-click "Copy support info" and project/issue/documentation links.
- **Security & audit** — the least-privilege permission generator can include server-level grants
  and now also produces a matching revoke script; the audit trail additionally records settings
  changes, credential updates, permission-script generation, audit/support-bundle exports, update
  checks, run cancellations, and job pause/resume/duplicate/export.
- **Alert reliability** — email and webhook alerts retry once on transient delivery failure.
- **Polish** — column truncation and clipped row actions fixed on the Dashboard, Jobs, and History
  tables at small window sizes (now guarded by automated layout probes).

Also new in the repository: proposals for a future drift-detection/schema-compare module and a
migration-assessment module, an enterprise roadmap, and a design-system reference
(`docs/product-improvements/`, `docs/design-system/`).

## 0.8.3 — 2026-07-15

**Critical fix — upgrade required.** In 0.8.0–0.8.2 the Windows Service crashed on every plain
scheduled (cron) fire before the run started, so **scheduled syncs never executed** — while the
scheduler heartbeat stayed healthy and "Next Run" kept advancing. Startup and catch-up runs were
unaffected, which masked the breakage. This release fixes it; after upgrading, your schedules run
at their advertised times. (If you want confirmation your install was affected, the Windows
Application event log will contain `Key trigger not found` errors from source "Obsync".)

This release also carries the fixes from a full production-readiness audit
(`docs/quality-audit/` in the repository has the complete reports):

- **Databases with user-defined types no longer fail** — any database containing an alias type,
  table type, XML schema collection, CLR type, or aggregate previously failed its entire run.
- **Scope changes never delete your history** — narrowing a schema filter or deselecting object
  types now retains the committed files of out-of-scope objects instead of committing their
  deletion; and if most tracked objects vanish at once on an unattended run (the signature of lost
  SQL metadata visibility, not a real mass drop), deletions are suspended with a Warning — a manual
  Run Now confirms and applies them.
- **CLR modules are reported, not silently omitted** — they now surface as explicit skips like
  encrypted modules (previously they were invisible and could be misreported as deletions).
- **Git workspaces self-heal more** — a stale `index.lock` left by a crash or cancel no longer
  wedges every later run; editing a repository's owner/name now re-points the existing clone
  (pushes previously kept going to the old repository); leftover files from an interrupted run are
  cleaned instead of leaking into the next commit or blocking checkout.
- **Scheduler resilience** — one invalid or never-firing cron expression no longer takes down the
  whole scheduler; the wizard now validates cron expressions (including "will it ever fire") and
  rejects Daily/Weekly times that can never intersect an enabled maintenance window (previously
  such jobs silently never ran); saving a job no longer triggers an immediate unattended run when
  "run on service startup" is enabled; disabled jobs can no longer slip through a scheduling race.
- **Settings honesty** — switching away from the app no longer wipes unsaved Settings input (or
  clears typed passwords); disabling email alerts or the proxy keeps the stored password as the
  label promises; a manual proxy now requires a valid URL instead of silently connecting direct;
  "Trust server certificate" defaults off for new servers, with a caution.
- **Correctness hardening** — case-only object renames no longer brick a job; reference-data
  values containing line breaks are exported corruption-proof; zip exports are built atomically
  (a failure can no longer destroy the previous good export); a disk-full at the end of a run can
  no longer leave a phantom "Running" row; pull-request mode no longer pushes zero-diff branches
  in a loop when the base branch already has the content.
- **Smaller fixes** — GitHub tokens are trimmed (pasted trailing newlines broke PR mode only);
  HTTP 401/403/404 from git are no longer retried as transient; persisted git errors redact any
  embedded proxy credentials; the Dashboard "Latest Commit" no longer blanks when the newest run
  had no commit; duplicate job names are rejected; destination paths are validated; the CLI
  returns exit code 3 for Warning runs; crash-recovered failed runs now send the configured
  alerts; the review step shows every option it previously omitted.
- **New:** the `OBSYNC_DATA_ROOT` environment variable relocates the data root (state database,
  workspaces, logs, locks) for test harnesses and managed deployments.

## 0.8.2 — 2026-07-10

> [!WARNING]
> **Scheduled runs never executed in this release.** Every cron fire crashed before reaching the
> engine, silently — the service heartbeat stayed healthy and "Next Run" kept advancing. Fixed in
> 0.8.3; upgrade if you are on this version. Manual runs were unaffected.

- **Modernized installer wizard** — Segoe UI dialog typography, a cleaner brand side panel and
  banner, product-specific page copy with sentence-cased headers, and a more readable Service
  Account page. Purely presentational; install behavior is unchanged.

## 0.8.1 — 2026-07-10

> [!WARNING]
> **Scheduled runs never executed in this release.** Every cron fire crashed before reaching the
> engine, silently — the service heartbeat stayed healthy and "Next Run" kept advancing. Fixed in
> 0.8.3; upgrade if you are on this version. Manual runs were unaffected.

- **Tabbed Settings** — the Settings page is reorganized into categorized tabs (General, Alerts,
  Network & storage, Security & audit, Diagnostics, About) instead of one long scroll, with a
  readable card width.
- **Modern scrollbars** — a slim, rounded scrollbar style replaces the stock Windows chrome
  throughout the app.
- **Collapsible sidebar** — the navigation rail collapses to a compact icon-only strip via a toggle
  at its foot; tooltips carry the labels and the preference persists across sessions.

## 0.8.0 — 2026-07-10

> [!WARNING]
> **Scheduled runs never executed in this release.** Every cron fire crashed before reaching the
> engine, silently — the service heartbeat stayed healthy and "Next Run" kept advancing. Fixed in
> 0.8.3; upgrade if you are on this version. Manual runs were unaffected.

- **Nothing is silently lost** — tracked object state (hashes, deletions, incremental watermarks)
  now advances only after the run's changes are durably delivered: a commit in direct/local modes,
  an opened pull request in PR mode. A failed commit, push, or PR no longer made later runs report
  "no changes" while the repository silently missed the work.
- **Cancel running syncs** — a Cancel button in the Job Workspace and Ctrl+C in the CLI stop a run
  cleanly within seconds; the run is recorded as Cancelled (not a scary failure) and everything it
  had in flight is re-detected next run.
- **Reliability under parallel scripting** — fixed an intermittent whole-run failure caused by a
  directory-creation race between workers; file writes are now atomic (an interrupted run can never
  leave a truncated script for a later commit to pick up); interrupted or corrupt git workspaces
  self-heal by recloning.
- **Credential hardening** — the GitHub token and proxy credentials no longer appear on the git
  child-process command line (which Windows process auditing and EDR record); they travel in
  environment variables instead.
- **Safer multi-job setups** — jobs sharing one destination repository now run back-to-back on a
  per-repository lock instead of interleaving git operations in one clone.
- **Failure-policy fixes** — incremental runs no longer delete committed files of
  `.obsyncignore`-matched objects; watermarks no longer advance past transiently skipped objects; a
  failed options/permissions/security-review read is a reported skip instead of a run failure;
  generated files over ~95 MB are skipped (GitHub rejects them at 100 MB) instead of wedging the
  branch; case-only name collisions fail with an actionable message.
- **Clearer errors and truthful UI** — push failures show the explained, actionable reason (raw git
  output stays in technical details, incl. protected-branch and file-too-large guidance);
  skip-warnings explain themselves; per-object skip reasons are visible in the run's Logs tab;
  Dashboard shows action errors; History shows skipped counts and discloses its 100-run window;
  views refresh when the window activates so service-run results appear; closing the app mid-run
  asks first; live "N objects processed" progress during scripting.
- **Upgrades keep the service account** — the MSI remembers the configured service logon account,
  so a major upgrade no longer silently resets a working service to Local System.
- Benchmarked: a repeatable real-pipeline benchmark harness ships in `tools/Obsync.Benchmark`;
  measured results and tested limits are documented in `docs/LAUNCH-READINESS.md`.

## 0.7.0 — 2026-07-10

> [!NOTE]
> The heading below overstates what landed. The same commit introduced the defect fixed in 0.8.3:
> every plain cron fire threw before reaching the engine, so no scheduled run executed in 0.8.0
> through 0.8.2. The deployment and health work listed here did ship and is accurate.

- **Reliable scheduling** — the deployment and visibility gaps around scheduling are closed
  (but see the note above — execution itself was broken until 0.8.3):
  - The MSI now registers the Obsync service **automatic (delayed) start** and starts it at install,
    so schedules survive reboots with no manual step (previously manual-start, i.e. never running).
  - **Scheduler health in the app** — the service heartbeats into the job database every 30s;
    Dashboard, Jobs, Job Workspace, and the job wizard warn whenever an enabled schedule exists that
    the service cannot execute (not installed, stopped, or running under an account that cannot see
    your jobs), and the Settings → Diagnostics service row states the exact reason.
  - **Missed-run catch-up** — a schedule that came due while the machine or service was off runs
    once at service startup (a *Catch Up* run in History), never per missed occurrence, and never
    when a later run already covered it.
  - **Cross-process duplicate prevention** — a per-job machine-wide run lock spans the app, service,
    and CLI; an occurrence that overlaps a still-active run is skipped with a logged reason.
  - **Honest crash recovery** — interrupted runs are failed with an explicit reason at the next app
    or service start; the old five-minute cleanup that could falsely fail a live long service run is
    gone (recovery now probes the run lock instead of guessing by age).
  - Startup runs are attributed as *Startup* (they previously logged as *Scheduled* and were
    wrongly subject to the maintenance window); disabling a job now clears its Next Run; "Next Run"
    is stamped immediately on save; next-run previews use the UTC offset in effect at the fire date
    (daylight-saving correctness).

## 0.6.0 — 2026-07-07

- **Database Timeline** — the History page gained a Timeline view: runs grouped by day with change
  totals, expandable per-run object lists, and click-through to the diff viewer preselected at the
  clicked object. Search now also matches database names and the triggering user.
- **Dependency Explorer** — new Dependencies tab in the Job Workspace: pick any synced object and
  see what depends on it (referencing modules, foreign-key tables, triggers) and what it uses,
  read live from the server. Click a dependency to drill into it.
- **Generated schema documentation** — each database now gets `docs/README.md` committed next to
  its scripts: an object index plus a data dictionary (column types, nullability, defaults, keys,
  and `MS_Description` descriptions). Regenerates only when the schema changes.
- **Security reviews** — each run writes `security/security-review.md` per database (guest access,
  grants to `public`, high-risk grants, `db_owner` members, orphaned users, `TRUSTWORTHY`) and
  `server/security-review.md` with the server pass (`sysadmin` members, `sa` enabled, password
  policy, high-risk server grants). Versioned, so posture drift shows up as commits.

> Upgrading note: each existing job's first run after 0.6.0 commits the two new generated files —
> that is the feature rollout, not drift.

## 0.5.0 — 2026-07-07

- **Update checks** — notify-only: a startup toast (at most daily, once per version) and a manual
  check in Settings → About. Only the GitHub releases endpoint is ever contacted.
- **Audit hardening** — every run (including scheduled service runs) now writes a run-outcome audit
  event with commit SHA and push result; the complete audit trail exports as CSV or JSON from
  Settings.
- **Repositories page redesign** — single full-width list with an Add/Edit dialog, plus a
  "Check token" action that re-validates the stored token's permissions against GitHub.

## 0.4.0 — 2026-07-06

- **VLDB performance** — incremental scripting (per-type `modify_date` watermarks; unchanged
  objects skip scripting entirely from the third run), parallel SMO scripting, and batched
  state/change/log persistence. UI hardening for huge runs (capped grids, streamed reports).
- **Enterprise installer** — branded dialogs, bundled MinGit (SHA-pinned), Windows Event Log
  source, service recovery settings, silent-install service account properties, production
  license & third-party notices.

## 0.3.0 — 2026-07-06

- **Server-level objects** — logins, server roles, credentials, linked servers, and SQL Agent
  jobs/operators/alerts scripted under `server/`, plus an always-on `server-configuration.sql`.
- **Drift alerting** — SMTP and webhook alerts on failure/warning/changes, fired after runs from
  both the app and the service.

## 0.2.0 — 2026-07-06

- **Dynamic database scope** — "All user databases", resolved live each run.
- **Reference data versioning** — selected lookup tables exported as deterministic INSERT scripts.
- **Script & diff viewer** — inspect any run's changes side-by-side or unified, offline, from the
  local clone.
- Daily-driver options: run-history retention, git committer identity, workspaces root override,
  job export/import, run-failure notifications.

## 0.1.0 — 2026-07-02

Initial installer baseline: SQL Server → GitHub schema sync (metadata fast-path + SMO), change
detection with per-object state, direct-commit and pull-request modes, export/local-only modes,
scheduling via the Windows service, audit log, token permission checker, least-privilege SQL
permission generator, proxy support, maintenance windows, `.obsyncignore`, run reports,
environment tags, and the WPF app with dashboard, jobs, history, and diagnostics.

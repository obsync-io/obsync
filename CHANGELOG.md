# Changelog

All notable changes to Obsync. Versions are the MSI/installer baselines; dates are build dates.

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

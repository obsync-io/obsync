# Obsync enterprise deployment guide

Obsync ships as a single per-machine MSI (`Obsync-<version>-win-x64.msi`, built by
`packaging\build-installer.ps1`). The install has **zero prerequisites**:

- **No .NET runtime needed** — the app, service, and CLI are published self-contained.
- **No git install needed** — a pinned, SHA256-verified [MinGit](https://github.com/git-for-windows/git)
  is bundled under `<install dir>\tools\git\` and used automatically (override with the `OBSYNC_GIT`
  environment variable, or remove nothing and it falls back to `git` on `PATH` when the bundled copy
  is absent). git is GPLv2 and is distributed unmodified as a separate aggregated program alongside
  the MIT-licensed app; its license files ship in `tools\git\`.

The MSI installs:

| Piece | Details |
| --- | --- |
| Desktop app | `Obsync.App.exe`, Start Menu shortcut |
| CLI | `obsync.exe`; the install directory is added to the machine `PATH` |
| Windows service | `Obsync` (display name "Obsync Sync Service"), **automatic (delayed) start**, started by the installer |
| Bundled git | `tools\git\cmd\git.exe` (MinGit) |

## Interactive install

Double-click the MSI. The wizard steps are:

1. **Welcome**
2. **License Agreement** — accepting is required to continue.
3. **Destination Folder** — defaults to `C:\Program Files\Obsync\`.
4. **Service Account** — choose who the `Obsync` service logs on as:
   - **Local System — configure later** (default): the service installs and runs, but it cannot see
     your jobs or run schedules, because Obsync's data and credentials live in the *per-user*
     profile and Windows Credential Manager vault. The app shows a scheduler warning until the
     account is fixed.
   - **This account**: enter `DOMAIN\user` (or `.\user` on a standalone PC) and its password —
     **the same account that runs the Obsync app**, so the service sees the credentials and data the
     app saved. The account also needs the **"Log on as a service"** right; if it does not have it,
     the install still succeeds but the service will not start (see below). For a group Managed
     Service Account enter `DOMAIN\name$` and leave the password blank — the wizard only accepts a
     blank password for account kinds Windows logs on without one (`DOMAIN\name$`, `NT SERVICE\...`,
     `NT AUTHORITY\...`).
5. **Ready to install** → **Install** (elevation prompt) → progress.
6. **Finish** — with an optional "Launch Obsync" checkbox.

## Silent install

```powershell
msiexec /i Obsync-<version>-win-x64.msi /qn `
    SERVICE_ACCOUNT="DOMAIN\ObsyncSvc" SERVICE_PASSWORD="..." `
    INSTALLFOLDER="D:\Apps\Obsync" `
    /l*v install.log
```

- `SERVICE_ACCOUNT` / `SERVICE_PASSWORD` — the service logon account. Omit both to get Local System
  (configure later — schedules won't run until the account is set). The account needs the
  **"Log on as a service"** right. If the service fails to start after an unattended install, grant
  the right (Group Policy, or re-enter the credentials once on the service's Log On tab in
  `services.msc`, which grants it) and start the service.
- `INSTALLFOLDER` — the install directory (the same property the wizard's Destination Folder page
  sets). Omit for the default under Program Files.
- `/l*v install.log` — verbose install log; always capture it for unattended rollouts.

### gMSA (group Managed Service Account)

```powershell
msiexec /i Obsync-<version>-win-x64.msi /qn SERVICE_ACCOUNT="DOMAIN\ObsyncSvc$" /l*v install.log
```

Pass **no** `SERVICE_PASSWORD` — Windows Installer passes a null password to the Service Control
Manager, which is what gMSA logons require. Prerequisites, before running the MSI:

1. The gMSA exists (`New-ADServiceAccount`) and the target machine may retrieve its password
   (`-PrincipalsAllowedToRetrieveManagedPassword`).
2. The account is installed on the host: `Install-ADServiceAccount ObsyncSvc` (verify with
   `Test-ADServiceAccount ObsyncSvc`).
3. The gMSA has the "Log on as a service" right. The installer grants it (`SeServiceLogonRight`)
   for any account that is not a built-in service principal. Where that right is defined by **Group
   Policy**, the local grant is overwritten at the next policy refresh and a "Deny log on as a
   service" entry beats it outright — that case needs an AD change, and no installer can fix it.
4. Job credentials (SQL passwords, GitHub tokens) must exist in **that account's** Credential
   Manager vault. A gMSA has a machine-managed password, so it cannot be signed in to and the
   desktop app cannot be run as it — use the CLI, run **as the gMSA**, to write into its vault:

   ```powershell
   # A scheduled task is the supported way to run a command as a gMSA.
   schtasks /create /tn ObsyncCred /ru "DOMAIN\ObsyncSvc$" /sc once /st 00:00 ^
            /tr "cmd /c echo <token>| obsync credential set github \"My Repo\""
   schtasks /run /tn ObsyncCred
   schtasks /delete /tn ObsyncCred /f
   ```

   Verify what landed, again as the service account:

   ```powershell
   schtasks /create /tn ObsyncCredList /ru "DOMAIN\ObsyncSvc$" /sc once /st 00:00 ^
            /tr "cmd /c obsync credential list > C:\Temp\obsync-cred.txt"
   ```

   `obsync credential set` reads the value from stdin or a hidden prompt — never from the command
   line — so it does not reach Windows process-creation auditing. Piping it in a task command line
   as above DOES put it there; prefer a file redirect (`< secret.txt`) that you delete afterwards.
   `obsync whoami` prints the account and data root a run will use, which is worth capturing the
   same way when a scheduled run behaves differently from a manual one.

   For **LocalSystem** or an `NT SERVICE\...` account, `psexec -s obsync credential set ...` is
   simpler and interactive.

## Repair, uninstall, upgrade

```powershell
msiexec /fa Obsync-<version>-win-x64.msi    # repair (also available via Add/Remove Programs)
msiexec /x  Obsync-<version>-win-x64.msi /qn    # uninstall (stops and removes the service)
```

### Clear the secrets first — the tool that does it is removed by the uninstall

> **Order matters.** `obsync credential prune` *is* `obsync.exe`, which lives in the install folder
> and goes away with it. Run this **before** `msiexec /x`, or the documented cleanup is no longer
> available and you are left picking entries out of Credential Manager by hand — see
> **If you have already uninstalled** below.

Uninstalling does **not** clear Windows Credential Manager, and it cannot: the vault is
per-account, and the uninstaller runs as SYSTEM, so it has no way to reach the vaults holding
the secrets. Deleting a repository in the app only ever removes the copy in the signed-in
user's vault — the copy stored for the service account survives, and because the key embeds the
profile's id, nothing in the product can name it afterwards.

Before uninstalling, clear each vault that holds Obsync secrets — as that account:

```powershell
obsync credential list             # what this account holds, including orphans
obsync credential prune --yes      # remove secrets whose profile is gone
psexec -s obsync credential prune --yes    # the same, for LocalSystem
```

`prune` compares against the database at the data root `obsync whoami` reports. Run it against
the wrong root and every live secret looks orphaned, which is why `--yes` is required.


### What uninstalling leaves behind

Deliberately, and it is not a short list. Uninstall removes the program; it does not remove your
data, because an installer is the wrong thing to be deleting a year of audit history with.

| What | Where | Why it stays |
|---|---|---|
| Jobs, settings, run history, audit log | `%LOCALAPPDATA%\Obsync\obsync.db` | Deleting it would make uninstall-then-reinstall — the documented recovery from a failed upgrade — a data-loss event |
| Git clones | `%LOCALAPPDATA%\Obsync\workspaces\` | Roughly 55 MB per synced repository at 50,000 objects, and unbounded in commit history. **Usually the largest item.** |
| Logs, run locks | `%LOCALAPPDATA%\Obsync\logs\`, `…\locks\` | — |
| Secrets | Windows Credential Manager, per account | The uninstaller runs as SYSTEM and cannot reach a user's vault |
| "Log on as a service" | The service account | Revoking a right the site may have granted for its own reasons is not the uninstaller's business — and revoking it during a *failed* upgrade used to strand the service. Remove it via `secpol.msc` if you are retiring the account. |

Three of these are easy to miss:

- **There is one data root per Windows account** — every user who ever opened the app has their
  own, and so does the service account.
- **The service's own data root, if you left it on Local System**, resolves to
  `C:\Windows\System32\config\systemprofile\AppData\Local\Obsync`: a real database and log directory
  that an administrator cannot browse without taking ownership. It is the same mechanism behind a
  Local System service reporting that it cannot see your jobs.
- **A relocated workspaces folder** (Settings → Network & storage) is wherever you put it, and the
  only record of where that was is inside the database you are about to leave behind.

To remove everything, as an administrator, after uninstalling:

```powershell
Remove-Item -Recurse -Force C:\Users\*\AppData\Local\Obsync
Remove-Item -Recurse -Force 'C:\Windows\System32\config\systemprofile\AppData\Local\Obsync'
```

`-Force` is required: git marks its pack files read-only.

If a DBA ran the generated permission script on a monitored SQL Server, that server still has the
login and its role memberships. Generate and run the **revoke** script from the app *before*
uninstalling — like the credential tool, it is gone afterwards.

### If you have already uninstalled

Secrets are recoverable; nothing is lost, it is just manual. They are ordinary Generic credentials
stored under the username `Obsync`:

```powershell
cmdkey /list | findstr Obsync
cmdkey /delete:Obsync:GitHub:<32-hex-id>
```

The keys are `Obsync:Sql:<id>`, `Obsync:GitHub:<id>`, `Obsync:Proxy` and `Obsync:Smtp`. Each vault
is per-account, so run this **as each account that used Obsync**, including
`psexec -s cmdkey /list` for Local System. A GitHub token found here is live until you revoke it on
GitHub.


Upgrades are **major upgrades under a stable UpgradeCode**: installing a newer MSI replaces the
older version in place (same or different folder — settings, jobs, and credentials live outside the
install folder and are untouched). Installing an *older* version over a newer one is blocked with
"A newer version of Obsync is already installed."

**Before upgrading, close two things:**

- **The Obsync window.** The app, the service and the CLI share one folder of libraries, so a
  running app keeps files open that the upgrade needs to replace. The installer will offer to close
  it for you; let it. If you answer *"Do not close applications"* instead, Windows defers those
  files to the next restart while the new service binary is installed immediately — the service
  would then be running against the previous version's libraries. It detects this and refuses to
  start, logging *"This installation is only half upgraded"* to the Application event log; restart
  the computer to finish. Scheduled runs do not happen until you do.
- **`services.msc`, Server Manager, and anything else showing the service list.** Each holds an open
  handle to the `Obsync` service. The upgrade deletes and re-creates the service, and an open handle
  keeps the delete pending, so re-creating it fails with `ERROR_SERVICE_MARKED_FOR_DELETE` (1072).
  Windows Installer reports that as **error 1923, "Verify that you have sufficient privileges to
  install system services"**, which is misleading — it is not a permissions problem. The upgrade
  rolls back cleanly; close the console and run it again.

A sync that is running when the upgrade starts is cancelled and recorded as such. It is **not**
re-run automatically afterwards — the schedule moves on to the next occurrence — so upgrade outside
your maintenance window, or trigger the job manually once the upgrade finishes.

**Service account on upgrade:** the account the service is *currently* configured with is used as
the default, so an upgrade never silently resets a working service to Local System. It is read from
the service itself (`HKLM\SYSTEM\CurrentControlSet\Services\Obsync\ObjectName`), falling back to the
value Obsync recorded at install time (`HKLM\SOFTWARE\Obsync\ServiceAccount`) — so a later change
made with `sc.exe config` or the services.msc **Log On** tab is picked up too, instead of being
reverted. It is only a *default*: an account you type in the wizard, or pass on the command line,
always wins.

Windows cannot remember the *password*, so:

- **Interactive upgrade** — the Service Account page opens preselected with the current account;
  re-enter the password. The wizard will not let you continue with a blank one unless the account is
  a kind that logs on without a password (gMSA, `NT SERVICE\...`, `NT AUTHORITY\...`).
- **Silent upgrade** of a password-logon service — you **must** pass `SERVICE_PASSWORD="..."` again:

  ```powershell
  msiexec /i Obsync-<version>-win-x64.msi /qn `
      SERVICE_ACCOUNT="DOMAIN\svc_obsync" SERVICE_PASSWORD="..." /l*v upgrade.log
  ```

  The account is remembered, but the password cannot be, and an upgrade *re-creates* the service
  rather than reconfiguring it — so without a password it would be installed unable to log on
  (error 1069). Rather than do that and report success, the upgrade is **refused**: msiexec exits
  **1603** and the reason is written to the Application event log as MsiInstaller event **10005**,
  so it is readable without `/l*v`. Nothing is changed; the existing installation keeps running.

  gMSA, `NT SERVICE\...`, `NT AUTHORITY\...` and Local System log on without a password and need
  nothing extra. Neither does **uninstall** (`/x`) or **repair** (`/fa`), silent or not — a repair
  reconfigures the existing service, which leaves its stored password alone.

  > **Deploying with SCCM or Intune:** these run as SYSTEM at `/qn`, so the password has to be in
  > the deployment command line for a password-account fleet. If that is unacceptable, run those
  > machines on a **gMSA** instead (`SERVICE_ACCOUNT="DOMAIN\name$"`, no password) — it is the
  > configuration this refusal is designed to push you towards. Note that secrets are stored per
  > Windows account, so a gMSA's vault must be populated separately with `obsync credential set`.

  > **Upgrading *from* 0.11.0 or 0.11.1 with a password account:** those two builds shipped a guard
  > that also fires during their own removal, which may block an unattended upgrade regardless of
  > what you pass. Upgrade those machines interactively, or uninstall interactively first. Every
  > version from 0.11.2 onwards is unaffected — including retirement: a `/qn` **uninstall** of
  > 0.11.0 or 0.11.1 with a password account also returns 1603, so those machines must be removed
  > interactively before a fleet tool can retire them.

### Deploying to a fleet (SCCM, Intune, GPO)

**Detection rule — use the UpgradeCode, not the ProductCode.** The ProductCode is regenerated for
every build, so a detection rule keyed on it breaks at every release. The stable identifier is the
UpgradeCode `{7B2E9E9C-3C1E-4C7A-9E2B-0A1F6D5C4B30}`; a file-version rule against
`%ProgramFiles%\Obsync\Obsync.App.exe` also works and is easier to express in Intune.

**Exit codes.** Map these, or a successful upgrade gets reported as a failure:

| Code | Meaning | Action |
|---|---|---|
| `0` | Success | — |
| `3010` | Success, **reboot required** | Treat as success with a soft reboot. Files the running app held could not be replaced; the service is running the previous version's libraries and refuses to start until the restart completes. |
| `1603` | Failed, nothing changed | See the reasons above — a missing `SERVICE_PASSWORD`, or an open service list (reported as 1923). Safe to retry. |
| `1618` | Another installation in progress | Retry later. |
| `1605` | **Product not installed** | Only meaningful when *removing*. Map it to success, or an idempotent retirement fails on machines that are already clean. Not a success code in Intune by default. |
| `1641` | Success, **reboot initiated** | A success code. |

A wrapper script of the form `if ($LASTEXITCODE -ne 0) { fail }` will report a perfectly good 3010
upgrade as a failure. SCCM and Intune both map 3010 correctly out of the box.

**Logs.** Add `/l*v C:\Windows\Temp\obsync-install.log` to the command line and collect that path;
the `<Launch>` refusals above also write MsiInstaller event **10005** to the Application log, which
survives without `/l*v`.

**Deployments run as SYSTEM**, which matters here: job secrets live in the Windows Credential
Manager of the account that runs them, and SYSTEM is not that account. Deploying the MSI installs
the product but cannot populate the service account's vault — use `obsync credential set` under that
account afterwards (see **Credentials** above).

**Retiring it again — do not use `UninstallString`.** Add/Remove Programs advertises
`MsiExec.exe /I{ProductCode}`, which is *maintenance mode*, not uninstall. Windows Installer emits
`/I` because this package deliberately keeps Repair available, and it never generates a
`QuietUninstallString` at all. A retirement script that reads `UninstallString` and appends `/qn`
therefore runs a silent no-op that **exits 0 having removed nothing** — a green report over a
machine that still has Obsync on it.

Resolve the ProductCode at run time instead, since it changes with every build:

```powershell
$p = Get-ChildItem HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall |
     Where-Object { (Get-ItemProperty $_.PSPath).DisplayName -eq 'Obsync' }
msiexec /x $p.PSChildName /qn /norestart /l*v C:\Windows\Temp\obsync-uninstall.log
```

`/norestart` matters: the case below is exactly a files-in-use-at-the-end case, and a fleet
retirement should never reboot a user's machine on its own.

**A `/qn` uninstall cannot close the desktop app.** The installer runs as SYSTEM in session 0 and
Restart Manager cannot shut down an application in an interactive user's session. If someone has
Obsync open, its files are deferred to the next restart and the uninstall returns **3010** — with
the Add/Remove Programs entry *already gone*, so it cannot simply be re-run. The folder finishes
emptying at the reboot. Retire when users are logged off, or accept the reboot.

### If an upgrade fails part-way

Nothing is lost: settings, jobs, run history and credentials live outside the install folder and are
never touched. But two states need checking by hand, because Windows Installer cannot restore either
one:

1. **The service logon account.** A rollback re-creates the service from the package's own values,
   and Windows never hands a password back to an installer, so the service may exist but be unable
   to log on (error 1069). Repair it directly:

   ```powershell
   sc.exe config Obsync obj= "DOMAIN\svc_obsync" password= "..."   # or the services.msc Log On tab
   Start-Service Obsync
   ```

2. **Whether the product is still registered at all.** `RemoveExistingProducts` runs before the
   installer's own transaction begins, so a failure *after* the old version has been removed may
   leave neither version installed. Check with
   `Get-Package -Name Obsync` (or Add/Remove Programs). If nothing is listed, reinstall — and **pass
   the account and password explicitly**:

   ```powershell
   msiexec /i Obsync-<version>-win-x64.msi /qn `
       SERVICE_ACCOUNT="DOMAIN\svc_obsync" SERVICE_PASSWORD="..." INSTALLFOLDER="D:\Apps\Obsync"
   ```

   This matters more than it looks. A failed upgrade may have removed both places the installer
   remembers the account from, so a bare retry finds nothing, falls back to **Local System**, and
   **succeeds** — leaving a service that runs but never executes a schedule, with a green
   deployment report. The app's dashboard warns about exactly this state ("the service is running
   as a different account"), but nothing else will.


### If an uninstall fails part-way

Windows Installer rolls back a failed uninstall, so the product comes back — files, shortcut, PATH
entry, registry values and the service. Two things do **not** come back with it, and neither is
announced:

- **The service's recovery actions and its delayed start.** Both are applied by actions that are
  deliberately skipped when removing, so nothing re-applies them on the way back. The service
  returns as a plain automatic-start service with no restart-on-failure policy — which you will
  only notice at the next boot storm, or the next crash that no longer restarts itself.
- **The logon password**, if the service runs as a named account. Rollback re-creates the service
  from the package, and Windows never hands a password back to an installer, so it comes back
  unable to log on with error 1069.

Repair all three directly:

```powershell
sc.exe config Obsync obj= "DOMAIN\svc_obsync" password= "..."
sc.exe config Obsync start= delayed-auto
sc.exe failure Obsync reset= 86400 actions= restart/60000/restart/60000/restart/60000
Start-Service Obsync
```

The product is then fully usable and can be uninstalled again normally.

If the uninstall failed *without* rolling back — msiexec killed, power loss — check which half
survived:

```powershell
Get-Package -Name Obsync ; Get-Service Obsync -EA SilentlyContinue ; Test-Path 'C:\Program Files\Obsync'
```

If the Add/Remove Programs entry is still there, re-run the uninstall; Windows Installer tolerates
files that are already gone. If the entry has gone but the service or the folder remain, there is no
installer state left to work with and the remainder has to be removed by hand:

```powershell
sc.exe stop Obsync ; sc.exe delete Obsync
Remove-Item -Recurse -Force 'C:\Program Files\Obsync'
Remove-Item -Recurse 'HKLM:\SOFTWARE\Obsync'
Remove-Item -Recurse 'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\Obsync'
Remove-Item "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Obsync.lnk" -EA SilentlyContinue
# and strip C:\Program Files\Obsync\ from the machine PATH
```

"Log on as a service" is left granted on purpose — that is not leftover state, see
**What uninstalling leaves behind**.

## What the service does

The `Obsync` Windows service runs scheduled sync jobs **with the desktop app closed** (Quartz
scheduler host) and picks up job/schedule changes made in the app within 30 seconds — no restart
needed. It is installed **automatic (delayed) start** and started by the installer, so schedules
keep firing after every reboot with no manual step. To change the logon account later:

```powershell
Stop-Service Obsync
sc.exe config Obsync obj= "DOMAIN\user" password= "..."   # or the Log On tab in services.msc
Start-Service Obsync
```

- **Missed schedules**: if a run time passes while the machine or service is off, the service runs
  the job **once** at startup to catch up (never once per missed occurrence, and never if a later
  run already covered it).
- **Recovery**: preconfigured by the installer — on each of the first three failures the service
  restarts after 60 seconds; the failure counter resets daily.
- **Event Viewer**: the installer registers the **"Obsync"** source in the **Application** log; the
  service writes Warning-and-above events there (console/dev runs do not).
- **Rolling file logs**: `%LOCALAPPDATA%\Obsync\logs\service-<date>.log` for the service account
  (daily rolling, 31 files retained). The desktop app logs to the same folder under the interactive
  user's profile.

## Code signing (release builds)

The build script signs `Obsync.App.exe`, `Obsync.Service.exe`, `obsync.exe`, and the MSI when given
a certificate thumbprint; otherwise it prints an UNSIGNED warning:

```powershell
pwsh packaging\build-installer.ps1 -SigningThumbprint <sha1-thumbprint>
# or: $env:OBSYNC_SIGN_THUMBPRINT = '<sha1-thumbprint>'
```

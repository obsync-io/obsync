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
   - **This account**: enter `DOMAIN\user` and password — **the same account that runs the Obsync
     app**, so the service sees the credentials and data the app saved. For a group Managed Service
     Account enter `DOMAIN\name$` and leave the password blank.
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
3. The gMSA has the "Log on as a service" right (granted by the installer's service assignment, or
   via Group Policy).
4. Job credentials (SQL passwords, GitHub tokens) must exist in **that account's** Credential
   Manager vault — configure jobs by running the Obsync app under the same account.

## Repair, uninstall, upgrade

```powershell
msiexec /fa Obsync-<version>-win-x64.msi    # repair (also available via Add/Remove Programs)
msiexec /x  Obsync-<version>-win-x64.msi /qn    # uninstall (stops and removes the service)
```

Upgrades are **major upgrades under a stable UpgradeCode**: installing a newer MSI replaces the
older version in place (same or different folder — settings, jobs, and credentials live outside the
install folder and are untouched). Installing an *older* version over a newer one is blocked with
"A newer version of Obsync is already installed."

**Service account on upgrade:** the last configured logon account is remembered (registry,
`HKLM\SOFTWARE\Obsync\ServiceAccount`) and used as the default, so an upgrade never silently resets
a working service to Local System. Windows cannot remember the *password*, so:

- **Interactive upgrade** — the Service Account page opens preselected with the remembered account;
  re-enter the password (gMSA accounts need none).
- **Silent upgrade** of a password-logon service — pass `SERVICE_PASSWORD="..."` again (the account
  is remembered; without the password the service is reconfigured but cannot start, and the app's
  scheduler warning says so). gMSA and Local System silent upgrades need nothing extra.

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

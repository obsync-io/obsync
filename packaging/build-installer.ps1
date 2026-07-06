<#
.SYNOPSIS
    Builds the Obsync MSI installer.

.DESCRIPTION
    Publishes the three hosts (App, Service, CLI) self-contained for win-x64 into one shared staging
    folder (the .NET runtime files are identical across the three, so they de-duplicate), stages a
    pinned MinGit under tools\git\ so the MSI harvests it, then runs WiX to harvest that folder into
    a per-machine MSI.

    The result requires NO .NET runtime and NO git install on the target machine. Output lands in
    artifacts\ at the repository root.

.PARAMETER SigningThumbprint
    SHA-1 thumbprint of a code-signing certificate in the current user's store. When provided (or via
    the OBSYNC_SIGN_THUMBPRINT environment variable), the three host exes are Authenticode-signed
    before wix build and the finished MSI is signed after. When absent the build is unsigned.

.EXAMPLE
    pwsh packaging\build-installer.ps1

.EXAMPLE
    pwsh packaging\build-installer.ps1 -SigningThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $SigningThumbprint = $env:OBSYNC_SIGN_THUMBPRINT
)

$ErrorActionPreference = 'Stop'

$packagingDir = $PSScriptRoot
$repoRoot = Split-Path $packagingDir -Parent
$stageDir = Join-Path $repoRoot 'artifacts\publish'
$outDir = Join-Path $repoRoot 'artifacts'

<#
  Pinned MinGit (bundled so installs have zero prerequisites -- no git on PATH needed).

  git is GPLv2; Obsync (MIT) distributes it UNMODIFIED as a separate aggregated program alongside the
  app ("mere aggregation" under GPLv2), and the MinGit zip's own license files (LICENSE.txt and the
  per-component notices it contains) are extracted with it into tools\git\.

  pinned: MinGit 2.55.0.2 x64 (git-for-windows v2.55.0.windows.2), SHA256 from the official release
  notes at https://github.com/git-for-windows/git/releases/tag/v2.55.0.windows.2 -- a mismatch below
  fails the build; never weaken it to a warning.
#>
$minGitVersion = '2.55.0.2'
$minGitZipName = "MinGit-$minGitVersion-64-bit.zip"
$minGitUrl = "https://github.com/git-for-windows/git/releases/download/v2.55.0.windows.2/$minGitZipName"
$minGitSha256 = 'E3EA2944CEA4B3FABCD69C7C1669EF69B1B66C05AC7806D81224D0ABAD2DEC31'
$minGitCacheDir = Join-Path $packagingDir 'cache'
$minGitZipPath = Join-Path $minGitCacheDir $minGitZipName

function Get-VerifiedMinGit {
    New-Item -ItemType Directory -Force -Path $minGitCacheDir | Out-Null

    if (Test-Path $minGitZipPath) {
        $hash = (Get-FileHash $minGitZipPath -Algorithm SHA256).Hash
        if ($hash -eq $minGitSha256) {
            Write-Host "MinGit $minGitVersion found in cache (SHA256 verified)" -ForegroundColor DarkCyan
            return
        }
        Write-Warning "Cached $minGitZipName has SHA256 $hash (expected $minGitSha256); re-downloading."
        Remove-Item $minGitZipPath -Force
    }

    Write-Host "Downloading $minGitUrl" -ForegroundColor DarkCyan
    Invoke-WebRequest -Uri $minGitUrl -OutFile $minGitZipPath -UseBasicParsing

    $hash = (Get-FileHash $minGitZipPath -Algorithm SHA256).Hash
    if ($hash -ne $minGitSha256) {
        Remove-Item $minGitZipPath -Force
        throw ("MinGit download failed SHA256 verification: got $hash, expected $minGitSha256. " +
            'Refusing to bundle an unverified git. If git-for-windows re-released the asset, re-pin ' +
            'the version and hash in packaging/build-installer.ps1 from the official release notes.')
    }
    Write-Host "MinGit $minGitVersion downloaded (SHA256 verified)" -ForegroundColor DarkCyan
}

function Find-SignTool {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    # Not on PATH: take the newest x64 signtool from the Windows SDK, the usual dev-box layout.
    $kits = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (Test-Path $kits) {
        $candidate = Get-ChildItem -Path $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -like '*\x64\*' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }
    throw 'signtool.exe not found (PATH or Windows SDK). Install the Windows SDK signing tools or drop -SigningThumbprint.'
}

function Invoke-Sign {
    param([string[]] $Files)
    $signTool = Find-SignTool
    foreach ($file in $Files) {
        Write-Host "Signing $file" -ForegroundColor DarkCyan
        & $signTool sign /sha1 $SigningThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $file
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $file" }
    }
}

# --- Version: single source of truth is Directory.Build.props <VersionPrefix> ---
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$propsText = Get-Content -Raw -Path $propsPath
if ($propsText -notmatch '<VersionPrefix>\s*([^<\s]+)\s*</VersionPrefix>') {
    throw "Could not read <VersionPrefix> from $propsPath"
}
$version = $Matches[1]
Write-Host "Building Obsync $version installer ($Runtime, $Configuration)" -ForegroundColor Cyan

# --- Clean staging so a removed file never lingers in the MSI ---
if (Test-Path $stageDir) { Remove-Item -Recurse -Force $stageDir }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

# --- Publish the three hosts into the SAME folder (shared runtime overwrites identically) ---
$hosts = @(
    'src\Obsync.App\Obsync.App.csproj',
    'src\Obsync.Service\Obsync.Service.csproj',
    'src\Obsync.Cli\Obsync.Cli.csproj'
)
foreach ($proj in $hosts) {
    Write-Host "Publishing $proj" -ForegroundColor DarkCyan
    dotnet publish (Join-Path $repoRoot $proj) `
        -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=false -p:DebugType=none `
        -o $stageDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $proj" }
}

# --- Stage the bundled MinGit under tools\git so the MSI harvest picks it up ---
Get-VerifiedMinGit
$gitStageDir = Join-Path $stageDir 'tools\git'
Write-Host "Extracting MinGit to $gitStageDir" -ForegroundColor DarkCyan
Expand-Archive -Path $minGitZipPath -DestinationPath $gitStageDir
if (-not (Test-Path (Join-Path $gitStageDir 'cmd\git.exe'))) {
    throw "MinGit extraction did not produce tools\git\cmd\git.exe -- the zip layout changed; fix the staging step."
}

# --- Code signing (host exes before wix build; the MSI afterwards) ---
if ($SigningThumbprint) {
    Invoke-Sign -Files @(
        (Join-Path $stageDir 'Obsync.App.exe'),
        (Join-Path $stageDir 'Obsync.Service.exe'),
        (Join-Path $stageDir 'obsync.exe')
    )
}
else {
    Write-Host 'UNSIGNED build -- provide -SigningThumbprint (or set OBSYNC_SIGN_THUMBPRINT) for release.' -ForegroundColor Yellow
}

# --- Ensure the WiX tool + extensions are available ---
Push-Location $repoRoot
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed' }
    dotnet wix extension add -g WixToolset.UI.wixext/5.0.2
    if ($LASTEXITCODE -ne 0) { throw 'adding WixToolset.UI.wixext failed' }
    dotnet wix extension add -g WixToolset.Util.wixext/5.0.2
    if ($LASTEXITCODE -ne 0) { throw 'adding WixToolset.Util.wixext failed' }
}
finally {
    Pop-Location
}

# --- Build the MSI ---
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$msiPath = Join-Path $outDir "Obsync-$version-$Runtime.msi"

dotnet wix build (Join-Path $packagingDir 'Obsync.wxs') `
    -arch x64 `
    -d Version=$version `
    -d PublishDir=$stageDir `
    -d PackagingDir=$packagingDir `
    -d RepoRoot=$repoRoot `
    -ext WixToolset.UI.wixext `
    -ext WixToolset.Util.wixext `
    -o $msiPath
if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }

if ($SigningThumbprint) {
    Invoke-Sign -Files @($msiPath)
}

Write-Host ""
Write-Host "Built: $msiPath ($([math]::Round((Get-Item $msiPath).Length / 1MB, 1)) MB)" -ForegroundColor Green

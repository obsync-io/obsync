<#
.SYNOPSIS
    Builds the Obsync MSI installer.

.DESCRIPTION
    Publishes the three hosts (App, Service, CLI) self-contained for win-x64 into one shared staging
    folder (the .NET runtime files are identical across the three, so they de-duplicate), then runs
    WiX to harvest that folder into a per-machine MSI.

    The result requires NO .NET runtime on the target machine (self-contained). Output lands in
    artifacts\ at the repository root.

.EXAMPLE
    pwsh packaging\build-installer.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$packagingDir = $PSScriptRoot
$repoRoot = Split-Path $packagingDir -Parent
$stageDir = Join-Path $repoRoot 'artifacts\publish'
$outDir = Join-Path $repoRoot 'artifacts'

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

# --- Ensure the WiX tool + UI extension are available ---
Push-Location $repoRoot
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed' }
    dotnet wix extension add -g WixToolset.UI.wixext/5.0.2
    if ($LASTEXITCODE -ne 0) { throw 'adding WixToolset.UI.wixext failed' }
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
    -o $msiPath
if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }

Write-Host ""
Write-Host "Built: $msiPath" -ForegroundColor Green

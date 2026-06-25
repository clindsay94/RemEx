#Requires -RunAsAdministrator
<#
.SYNOPSIS
    One-time fix for the Remex.Agent "split-brain" install: makes the LocalSystem service AND the
    login-launched GUI host run the SAME build from ONE canonical location (Program Files\RemEx).

.DESCRIPTION
    The dev install registered the RemexHost service pointing at the repo's artifacts\publish folder,
    while the HKCU Run key launches the GUI host from "$env:ProgramFiles\RemEx" — two different,
    out-of-sync builds that collide on the canonical port + IPC pipes at boot/login.

    This script (elevated):
      1. Stops + removes the RemexHost service (whatever path it currently points at).
      2. Kills any lingering Remex.Agent processes (so Program Files files aren't locked).
      3. Deploys the staged self-contained build into $InstallDir.
      4. Re-installs the service pointing at $InstallDir\Remex.Agent.exe --agent (via install-service.ps1),
         which also refreshes the firewall rules + event-log source for the new path, then starts it.

    After this, the future "I changed code" loop is just scripts\update-local-install.ps1 (it copies to
    Program Files and restarts the now-correctly-pointed service).

.PARAMETER StageDir
    Source of the build to deploy. Defaults to the staging publish produced alongside this fix.
    Pass -Publish to (re)publish fresh instead.

.PARAMETER InstallDir
    Canonical install location. Defaults to "$env:ProgramFiles\RemEx".

.PARAMETER Publish
    Re-run `dotnet publish` (self-contained win-x64) into a fresh staging dir instead of reusing StageDir.

.EXAMPLE
    # From an elevated PowerShell, in the repo root:
    .\scripts\fix-host-install.ps1
#>
param(
    [string]$StageDir = (Join-Path $PSScriptRoot "..\artifacts\stage_8bn"),
    [string]$InstallDir = (Join-Path $env:ProgramFiles "RemEx"),
    [switch]$Publish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot    = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$HostProj    = Join-Path $RepoRoot "remex.agent"
$ServiceName = "RemexHost"
$InstallScript = Join-Path $PSScriptRoot "install-service.ps1"

if ($Publish) {
    $StageDir = Join-Path $RepoRoot "artifacts\stage_fix"
    if (Test-Path $StageDir) { Remove-Item $StageDir -Recurse -Force }
    Write-Host "Publishing Remex.Agent (self-contained, win-x64) -> $StageDir ..." -ForegroundColor Cyan
    dotnet publish $HostProj -c Release -r win-x64 --self-contained -o $StageDir
    if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed (exit $LASTEXITCODE)."; exit 1 }
}

$stageExe = Join-Path $StageDir "Remex.Agent.exe"
if (-not (Test-Path $stageExe)) {
    Write-Error "Staged build not found at $stageExe. Run with -Publish, or point -StageDir at a publish folder."
    exit 1
}

# 1. Stop + remove the old service (it may point at artifacts\publish).
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "Stopping + removing existing '$ServiceName' service..." -ForegroundColor Yellow
    if ($svc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        try { $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20)) } catch { }
    }
    & $InstallScript -Action Uninstall
}

# 2. Kill any lingering host instances (login GUI host, dev runs, a just-stopped service still
#    releasing handles) and WAIT until none remain — otherwise the copy below races a locked DLL.
Write-Host "Stopping any lingering Remex.Agent processes..." -ForegroundColor Yellow
for ($i = 0; $i -lt 12; $i++) {
    $procs = Get-Process -Name 'Remex.Agent' -ErrorAction SilentlyContinue
    if (-not $procs) { break }
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# 3. Deploy the staged build to the canonical install dir, retrying briefly: file handles can linger
#    for a moment after a process exits, which manifests as "being used by another process".
if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir | Out-Null }
Write-Host "Deploying $StageDir -> $InstallDir ..." -ForegroundColor Cyan
$copied = $false
for ($i = 0; $i -lt 10; $i++) {
    try {
        Copy-Item (Join-Path $StageDir "*") $InstallDir -Recurse -Force -ErrorAction Stop
        $copied = $true
        break
    } catch {
        Write-Host "  copy attempt $($i + 1) blocked ($($_.Exception.Message)); retrying..." -ForegroundColor DarkGray
        Start-Sleep -Milliseconds 750
    }
}
if (-not $copied) { Write-Error "Could not copy into $InstallDir (files still locked)."; exit 1 }
Write-Host "Files deployed." -ForegroundColor Green

# 4. Re-install the service pointing at the canonical location.
Write-Host "Re-installing '$ServiceName' pointing at $InstallDir ..." -ForegroundColor Cyan
& $InstallScript -Action Install -HostPath (Join-Path $InstallDir "Remex.Agent.exe")

Write-Host ""
Write-Host "=====================================================" -ForegroundColor Green
Write-Host "  Host install unified at: $InstallDir" -ForegroundColor Green
Write-Host "  Service + login GUI host now run the same build." -ForegroundColor Green
Write-Host "=====================================================" -ForegroundColor Green
Write-Host "Verify:  sc.exe qc $ServiceName   (BINARY_PATH_NAME should be under Program Files\RemEx)" -ForegroundColor Cyan

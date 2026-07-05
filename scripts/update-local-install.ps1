#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Rebuilds Remex.Agent and copies the fresh self-contained publish over an existing
    local installation — the fast "I changed code, update my installed copy" loop.

.DESCRIPTION
    The installer (installer/RemEx.iss) ships a SELF-CONTAINED win-x64 publish: a whole
    folder of DLLs + the .NET runtime, not just Remex.Agent.exe. Code changes land in the
    managed DLLs (Remex.Agent.dll, Remex.Core.dll, ...), NOT in the .exe (which is just a
    native bootstrap shim). So updating an install means copying the WHOLE publish folder,
    never swapping the .exe alone.

    This script:
      1. Stops the RemexHost service if it is installed + running (files are locked otherwise).
      2. Publishes Remex.Agent exactly like the installer (-r win-x64 --self-contained).
      3. Copies the publish output over the install directory.
      4. Restarts the service if it was running before.

    Use installer/build-installer.ps1 instead when the version changed, the service-install
    logic changed, or you need a distributable Setup.exe for other machines.

.PARAMETER InstallDir
    The installed RemEx directory to update. Defaults to "$env:ProgramFiles\RemEx".

.PARAMETER SkipPublish
    Reuse the existing publish output instead of running dotnet publish again.

.EXAMPLE
    .\scripts\update-local-install.ps1

.EXAMPLE
    .\scripts\update-local-install.ps1 -InstallDir "D:\Apps\RemEx" -SkipPublish
#>
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles "RemEx"),
    [switch]$SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$HostProj   = Join-Path $RepoRoot "remex.agent"
$PublishDir = Join-Path $RepoRoot "artifacts\publish\remex.agent\release_win-x64"
$ServiceName = "RemexHost"

if (-not (Test-Path $InstallDir)) {
    Write-Error "Install directory not found: $InstallDir`nRun the full installer first, or pass -InstallDir."
    exit 1
}

# 1. Stop the service if it exists and is running; remember so we can restart it.
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$wasRunning = $false
if ($service) {
    if ($service.Status -eq "Running") {
        Write-Host "Stopping service '$ServiceName' (files are locked while it runs)..." -ForegroundColor Yellow
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(20))
        $wasRunning = $true
    } else {
        Write-Host "Service '$ServiceName' is installed but not running (Status: $($service.Status))." -ForegroundColor DarkGray
    }
} else {
    Write-Host "Service '$ServiceName' is not installed — client-only update." -ForegroundColor DarkGray
}

# 2. Publish self-contained win-x64, matching installer/build-installer.ps1.
if ($SkipPublish) {
    if (-not (Test-Path $PublishDir)) {
        Write-Error "No existing publish output at $PublishDir; cannot -SkipPublish."
        exit 1
    }
    Write-Host "Reusing existing publish output: $PublishDir" -ForegroundColor DarkGray
} else {
    Write-Host "Publishing Remex.Agent (self-contained, win-x64)..." -ForegroundColor Cyan
    dotnet publish $HostProj -c Release -r win-x64 --self-contained
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed (exit $LASTEXITCODE)."
        exit 1
    }
}

# 3. Copy the WHOLE publish folder over the install directory.
Write-Host "Copying publish output -> $InstallDir ..." -ForegroundColor Cyan
Copy-Item (Join-Path $PublishDir "*") $InstallDir -Recurse -Force
Write-Host "Files updated." -ForegroundColor Green

# 4. Restart the service if it was running before.
if ($wasRunning) {
    Write-Host "Restarting service '$ServiceName'..." -ForegroundColor Cyan
    Start-Service -Name $ServiceName
    Write-Host "Service restarted." -ForegroundColor Green
}

Write-Host ""
Write-Host "=====================================================" -ForegroundColor Green
Write-Host "  Local install updated: $InstallDir" -ForegroundColor Green
Write-Host "=====================================================" -ForegroundColor Green

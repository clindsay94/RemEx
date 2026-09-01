#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Rebuilds Remex.Agent and copies the fresh self-contained publish over your existing
    local installation — the fast "I changed code, update my installed copy" loop, with
    no need to re-run the full Setup.exe installer.

.DESCRIPTION
    RemEx is NOT a Windows Service. It runs as a single elevated interactive-session app
    that is auto-started at sign-in by a Task Scheduler logon task named "RemEx"
    (see scripts/autostart-remex.ps1). So updating an install is just:

      1. Stop the running RemEx (the logon task's instance, plus any stray Remex.Agent
         process) so its files aren't locked.
      2. Publish Remex.Agent exactly like the installer (-r win-x64 --self-contained).
      3. Copy the WHOLE publish folder over the install directory. Code changes land in the
         managed DLLs (Remex.Agent.dll, Remex.Core.dll, ...), NOT in Remex.Agent.exe (a native
         bootstrap shim), so the whole folder must be copied — never just the .exe.
      4. Start RemEx again via the "RemEx" logon task (elevated, minimized), or launch the
         installed Remex.Agent.exe directly if the task isn't registered.

    Your machine-wide state (cert.pfx, paired_clients.json in ProgramData/HKLM) lives OUTSIDE
    the install directory and is never touched — pairings and certificates survive the update.

    Use installer/build-installer.ps1 instead when the version changed, the autostart/task
    logic changed, or you need a distributable Setup.exe for another machine.

.PARAMETER InstallDir
    The installed RemEx directory to update. Defaults to "$env:ProgramFiles\RemEx".

.PARAMETER SkipPublish
    Reuse the existing publish output instead of running dotnet publish again.

.PARAMETER NoRestart
    Update the files but don't start RemEx again afterwards (you'll start it yourself).

.EXAMPLE
    .\scripts\update-local-install.ps1

.EXAMPLE
    .\scripts\update-local-install.ps1 -InstallDir "D:\Apps\RemEx" -SkipPublish
#>
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles "RemEx"),
    [switch]$SkipPublish,
    [switch]$NoRestart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# This is Windows install tooling (ProgramFiles + a Task Scheduler logon task + a win-x64
# self-contained publish). On Linux, update your install with installer/build-linux.sh and the
# XDG autostart entry instead. ($IsWindows is undefined in Windows PowerShell 5.1 — which only ever
# runs on Windows — so treat "no such variable" as Windows to stay StrictMode-safe.)
$onWindows = if (Test-Path Variable:\IsWindows) { $IsWindows } else { $true }
if (-not $onWindows) {
    Write-Host "This fast-update script is Windows-only." -ForegroundColor Yellow
    Write-Host "On Linux, rebuild and reinstall with:  ./installer/build-linux.sh" -ForegroundColor Yellow
    exit 1
}

$RepoRoot    = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$HostProj    = Join-Path $RepoRoot "remex.agent"
$PublishDir  = Join-Path $RepoRoot "artifacts\publish\remex.agent\release_win-x64"
$TaskName    = "RemEx"            # the logon auto-start task (see scripts/autostart-remex.ps1)
$ProcessName = "Remex.Agent"     # Remex.Agent.exe -> process name

if (-not (Test-Path $InstallDir)) {
    Write-Error "Install directory not found: $InstallDir`nRun the full installer first (installer\RemEx.iss / build-installer.ps1), or pass -InstallDir to point at your install."
    exit 1
}

# ─────────────────────────────────────────────────────────────────────────────
# 1. Stop the running app so its files aren't locked. Remember whether it was
#    running so we can start it again afterwards.
# ─────────────────────────────────────────────────────────────────────────────
$wasRunning = $false

$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($task) {
    if ($task.State -eq "Running") { $wasRunning = $true }
    Write-Host "Stopping the RemEx logon task instance..." -ForegroundColor Yellow
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
} else {
    Write-Host "RemEx logon task '$TaskName' is not registered — will start Remex.Agent.exe directly if needed." -ForegroundColor DarkGray
}

# Stop any remaining Remex.Agent process (a manually-launched instance, or one the task
# hasn't fully released yet). Its DLLs stay locked until the process actually exits.
# @() forces an array so .Count is valid even for a single process under StrictMode.
$procs = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
if ($procs.Count -gt 0) {
    $wasRunning = $true
    Write-Host "Stopping running RemEx ($($procs.Count) process(es))..." -ForegroundColor Yellow
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    # Wait (up to ~10s) for the file locks to actually release before copying.
    for ($i = 0; $i -lt 20 -and (Get-Process -Name $ProcessName -ErrorAction SilentlyContinue); $i++) {
        Start-Sleep -Milliseconds 500
    }
    if (Get-Process -Name $ProcessName -ErrorAction SilentlyContinue) {
        Write-Warning "RemEx is still running after 10s. Close it manually, then re-run this script."
        exit 1
    }
    Write-Host "RemEx stopped." -ForegroundColor Green
} else {
    Write-Host "No running RemEx process found." -ForegroundColor DarkGray
}

# ─────────────────────────────────────────────────────────────────────────────
# 2. Publish self-contained win-x64, matching installer/build-installer.ps1.
# ─────────────────────────────────────────────────────────────────────────────
if ($SkipPublish) {
    if (-not (Test-Path $PublishDir)) {
        Write-Error "No existing publish output at $PublishDir; cannot -SkipPublish. Run once without -SkipPublish first."
        exit 1
    }
    Write-Host "Reusing existing publish output: $PublishDir" -ForegroundColor DarkGray
} else {
    Write-Host "Publishing Remex.Agent (self-contained, win-x64)..." -ForegroundColor Cyan
    dotnet publish $HostProj -c Release -r win-x64 --self-contained
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed (exit $LASTEXITCODE). Fix the build error above and re-run."
        exit 1
    }
}

# ─────────────────────────────────────────────────────────────────────────────
# 3. Copy the WHOLE publish folder over the install directory.
# ─────────────────────────────────────────────────────────────────────────────
Write-Host "Copying fresh build -> $InstallDir ..." -ForegroundColor Cyan
Copy-Item (Join-Path $PublishDir "*") $InstallDir -Recurse -Force
Write-Host "Files updated." -ForegroundColor Green

# ─────────────────────────────────────────────────────────────────────────────
# 3b. Re-apply the signed uiAccess=true manifest to the installed apphost.
#     The publish output ships an unsigned, uiAccess="false" Remex.Agent.exe, so the
#     copy above reverts the privilege that lets the agent drive a Windows UAC prompt
#     remotely. Re-sign it here while the process is still stopped (step 1) and the
#     file is unlocked. Requires PromptOnSecureDesktop=0 to be useful (machine policy,
#     left to the operator). (RemEx-ywl7o)
# ─────────────────────────────────────────────────────────────────────────────
$agentExe = Join-Path $InstallDir "Remex.Agent.exe"
try {
    & (Join-Path $PSScriptRoot "sign-uiaccess.ps1") -ExePath $agentExe
} catch {
    Write-Warning "Could not apply signed uiAccess manifest to $agentExe : $_"
    Write-Warning "Remote control of UAC prompts will not work until this succeeds (see scripts\sign-uiaccess.ps1)."
}

# ─────────────────────────────────────────────────────────────────────────────
# 4. Start RemEx again (unless -NoRestart).
# ─────────────────────────────────────────────────────────────────────────────
if ($NoRestart) {
    Write-Host "Skipping restart (-NoRestart). Start RemEx yourself when ready." -ForegroundColor DarkGray
} elseif ($wasRunning -or $task) {
    if ($task) {
        Write-Host "Starting RemEx via the '$TaskName' logon task (elevated)..." -ForegroundColor Cyan
        Start-ScheduledTask -TaskName $TaskName
        Write-Host "RemEx started." -ForegroundColor Green
    } else {
        $exe = Join-Path $InstallDir "Remex.Agent.exe"
        if (Test-Path $exe) {
            Write-Host "Launching $exe ..." -ForegroundColor Cyan
            Start-Process -FilePath $exe
            Write-Host "RemEx started." -ForegroundColor Green
        } else {
            Write-Warning "Remex.Agent.exe not found in $InstallDir — start RemEx manually."
        }
    }
} else {
    Write-Host "RemEx wasn't running before the update, so it wasn't restarted." -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "=====================================================" -ForegroundColor Green
Write-Host "  Local install updated: $InstallDir" -ForegroundColor Green
Write-Host "=====================================================" -ForegroundColor Green

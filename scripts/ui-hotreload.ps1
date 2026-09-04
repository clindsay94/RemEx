<#
.SYNOPSIS
    Starts / stops the Debug RemEx host that has XAML hot reload enabled (RemEx-1us2w).

.DESCRIPTION
    remex.desktop is a LIBRARY. The app is Remex.Agent.exe, and the normal loop ships it through
    scripts/update-local-install.ps1 into C:\Program Files\RemEx as a Release publish. HotAvalonia is
    Debug-only by design, so that installed build can never hot reload - this script runs the Debug
    build straight out of artifacts/bin instead.

    -Stop puts things back the way the rest of the repo expects: the Debug host is killed and the
    INSTALLED Release build is relaunched. Always finish a session with it, or the next person to
    look at RemEx is looking at a Debug build without realising.

    On the installed-exe rule: docs and memory say host testing needs C:\Program Files\RemEx\Remex.Agent.exe
    because running the DLL trips a .NET Host firewall prompt and refuses INBOUND connections. That
    constraint is about the phone reaching the PC. Pure UI work does not need inbound anything, so a
    Debug run is fine for it - and is the only way to get hot reload. Anything involving a real device
    still goes through the installed build.

.EXAMPLE
    pwsh scripts/ui-hotreload.ps1 -Start
    pwsh scripts/ui-hotreload.ps1 -Start -AppArgs '--view Settings'
    pwsh scripts/ui-hotreload.ps1 -Stop
#>
[CmdletBinding(DefaultParameterSetName = 'Status')]
param(
    [Parameter(ParameterSetName = 'Start')][switch]$Start,
    [Parameter(ParameterSetName = 'Stop')][switch]$Stop,
    [Parameter(ParameterSetName = 'Status')][switch]$Status,

    # Skip the build and just launch what is already in artifacts/bin.
    [Parameter(ParameterSetName = 'Start')][switch]$NoBuild,

    # Extra args to pass straight through to Remex.Agent.exe (e.g. '--view Settings'), used by
    # scripts/ui-palette-sweep.ps1 (RemEx-8q7de) to open a specific view without sending keystrokes
    # to a running host.
    [Parameter(ParameterSetName = 'Start')][string]$AppArgs
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$debugExe = Join-Path $repoRoot 'artifacts\bin\remex.agent\debug\Remex.Agent.exe'
$installedExe = 'C:\Program Files\RemEx\Remex.Agent.exe'

function Get-RemexProcess {
    Get-Process -Name Remex.Agent -ErrorAction SilentlyContinue
}

function Stop-Remex {
    $p = Get-RemexProcess
    if ($p) {
        $p | Stop-Process -Force
        Start-Sleep -Milliseconds 900
    }
}

if ($Start) {
    # STOP FIRST, THEN BUILD. A running Remex.Agent holds an exclusive lock on the very DLLs the
    # build copies into artifacts/bin/remex.agent/debug - Remex.Desktop.dll and
    # Remex.Agent.Windows.dll - so building while it runs fails with MSB3026 after six retries.
    # It fails in the WORST possible way for this script's purpose: -Start reports the build error,
    # but the stale process is still running and still answering UI Automation, so the screenshot
    # that follows looks fine and is of the OLD build. A visual gate that silently photographs the
    # previous binary is worse than no gate. Measured while gating RemEx-lrxyo.
    Stop-Remex

    if (-not $NoBuild) {
        Write-Host 'Building Debug (hot reload is Debug-only)...' -ForegroundColor Cyan
        & dotnet build (Join-Path $repoRoot 'remex.agent\remex.agent.csproj') -c Debug -v q --nologo
        if ($LASTEXITCODE -ne 0) { throw "Debug build failed (exit $LASTEXITCODE)." }
    }
    if (-not (Test-Path $debugExe)) { throw "Debug host not found at $debugExe - run without -NoBuild." }

    if ($AppArgs) {
        Start-Process $debugExe -WorkingDirectory (Split-Path -Parent $debugExe) -ArgumentList $AppArgs
    }
    else {
        Start-Process $debugExe -WorkingDirectory (Split-Path -Parent $debugExe)
    }
    Start-Sleep -Seconds 12

    if (-not (Get-RemexProcess)) { throw 'Debug host exited during startup.' }
    Write-Host "Hot reload host running: $debugExe" -ForegroundColor Green
    Write-Host 'Edit any .axaml under remex.desktop and it re-renders in place. Alt+F5 forces a full reload.'
    Write-Host 'Snapshot it with: pwsh scripts/ui-snapshot.ps1 -Screenshot -Tree'
    Write-Host 'Finish with:     pwsh scripts/ui-hotreload.ps1 -Stop' -ForegroundColor Yellow
    return
}

if ($Stop) {
    Stop-Remex
    if (Test-Path $installedExe) {
        Start-Process $installedExe
        Write-Host "Restored the installed Release build: $installedExe" -ForegroundColor Green
    }
    else {
        Write-Warning "Installed build not found at $installedExe - nothing relaunched."
    }
    return
}

$proc = Get-RemexProcess
if (-not $proc) {
    Write-Host 'Remex.Agent is not running.'
    return
}
foreach ($p in $proc) {
    $path = try { $p.Path } catch { '<access denied>' }
    $mode = if ($path -like '*\artifacts\bin\*') { 'DEBUG (hot reload ON)' } else { 'Release / installed (no hot reload)' }
    Write-Host ("pid {0}  {1}`n    {2}" -f $p.Id, $mode, $path)
}

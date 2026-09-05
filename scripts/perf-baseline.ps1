#Requires -Version 7
<#
.SYNOPSIS
    Records cold-start time and memory footprint for the installed RemEx host, before and after
    a given range of commits, on the SAME installed exe path (RemEx-gtwk8).

.DESCRIPTION
    The Material work (RemEx-3sju5) adds a large styles/ControlTheme resource dictionary, ripples,
    shadows and animation. This script answers "did cold start or memory get worse" with numbers
    instead of a guess, by building each ref in an isolated git worktree, deploying it through that
    worktree's OWN scripts/update-local-install.ps1 (so the install directory and exe path never
    change — no new Windows Firewall prompt, see bd memory env-host-testing-needs-installed-exe),
    and measuring the installed Remex.Agent.exe with UI Automation, the same read-only pattern
    scripts/ui-snapshot.ps1 uses to find the main window.

    For each ref this script:
      1. Adds a detached git worktree at that commit under $env:TEMP.
      2. Runs THAT worktree's scripts/update-local-install.ps1 -NoRestart (it resolves its own
         $PSScriptRoot, so it publishes and copies from the worktree's own remex.agent — no separate
         "source root" plumbing is needed as long as the worktree carries a script with the same
         -InstallDir/-SkipPublish/-NoRestart contract as this checkout's copy; every ref in this
         repo's history back to before the Material work does).
      3. Launches the installed Remex.Agent.exe $Launches times. Launch 1 (right after the deploy)
         is the true cold start; launches 2..N are warm restarts of an already-installed, already
         one-time-JITted binary. Each launch is timed from Start-Process to the main window
         appearing in the UIA tree (poll every 50ms), then left running for -SettleSeconds before
         WorkingSet64 / PrivateMemorySize64 / handle count are sampled via Get-Process and the
         process is stopped again.
      4. Writes <Out>\perf-<ref>.json (every raw sample, for later drill-down) and folds all refs
         into <Out>\perf-summary.md.

    Whatever happens — including a launch that never shows a window — the `finally` block redeploys
    HEAD from THIS checkout (the branch tip, not a worktree that is about to be deleted), removes
    every worktree this script created, and restarts the installed host if and only if it was
    already running when the script started. That is the machine's "leave it as I found it"
    contract; read it before changing the try/finally shape.

    UI Automation here is READ-ONLY (RootElement children filtered by ProcessId, matched by Name):
    no keystrokes are injected, the running profile is never touched, and nothing here builds
    inside ui-hotreload's dev loop.

.PARAMETER Refs
    Git refs to measure, in order. Each becomes one row in the summary. Default: main (pre-Material)
    then HEAD (this branch tip).

.PARAMETER Launches
    Total launches per ref, including the cold one. Must be >= 2 (launch 1 is cold, 2..N are the
    warm sample the median/p90 come from). Default: 7.

.PARAMETER SettleSeconds
    Seconds to let the host sit idle after its window appears, before sampling memory. Default: 8.

.PARAMETER SteadySeconds
    Seconds after the window appears to take a SECOND, later memory sample (in addition to the
    SettleSeconds one), so a slow post-startup climb shows up instead of being hidden by an early
    sample. Must be >= SettleSeconds. Default: 20.

.PARAMETER Out
    Output directory for the per-ref JSON and the combined summary. Default:
    $env:TEMP\remex-ui\perf-<timestamp>\ (same $env:TEMP\remex-ui root as ui-snapshot.ps1).

.EXAMPLE
    pwsh scripts/perf-baseline.ps1

.EXAMPLE
    pwsh scripts/perf-baseline.ps1 -Refs @('main','HEAD') -Launches 10 -SettleSeconds 5
#>
[CmdletBinding()]
param(
    [string[]]$Refs = @('main', 'HEAD'),
    [int]$Launches = 7,
    [int]$SettleSeconds = 8,
    [int]$SteadySeconds = 20,
    [string]$Out = (Join-Path $env:TEMP ("remex-ui\perf-{0:yyyyMMdd-HHmmss}" -f (Get-Date)))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# This is Windows install/UIA tooling (Program Files + win-x64 self-contained publish + UI
# Automation). It has no meaning on Linux, so fail fast and clearly rather than half-running.
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    Write-Error 'scripts/perf-baseline.ps1 is Windows-only (Program Files install + UI Automation).'
    exit 1
}

if ($Launches -lt 2) {
    Write-Error "-Launches must be >= 2 (launch 1 is the cold start; launches 2..N are the warm sample). Got: $Launches"
    exit 1
}

if ($SteadySeconds -lt $SettleSeconds) {
    Write-Error "-SteadySeconds ($SteadySeconds) must be >= -SettleSeconds ($SettleSeconds) - the steady sample is taken later in the same launch, not instead of the settle one."
    exit 1
}

$RepoRoot    = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$InstallDir  = Join-Path $env:ProgramFiles 'RemEx'
$ProcessName = 'Remex.Agent'
$ExePath     = Join-Path $InstallDir 'Remex.Agent.exe'

New-Item -ItemType Directory -Force -Path $Out | Out-Null
Write-Host "Output directory: $Out" -ForegroundColor Cyan

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# ─────────────────────────────────────────────────────────────────────────────
# Helpers
# ─────────────────────────────────────────────────────────────────────────────

# Stops every running Remex.Agent process. Throws if any of them are still alive after the
# timeout — a hung process here would otherwise silently corrupt the NEXT launch's cold-start
# timing, which is worse than stopping the run.
function Stop-RemexHost {
    param([int]$TimeoutSeconds = 10)
    $procs = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return }
    foreach ($p in $procs) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
    foreach ($p in $procs) {
        if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
            throw "Remex.Agent (PID $($p.Id)) did not exit within ${TimeoutSeconds}s of Stop-Process."
        }
    }
}

# Polls the UIA tree for the main RemEx window belonging to $ProcessId, the same
# RootElement-children-filtered-by-ProcessId pattern as scripts/ui-snapshot.ps1. Returns the
# elapsed milliseconds on success; throws (rather than returning a sentinel) if the window never
# shows up, because a launch that silently "succeeds" with no window is exactly the failure mode
# (a blocking firewall/elevation prompt) the run instructions call out.
function Wait-RemexWindow {
    param([int]$ProcessId, [int]$TimeoutMs = 60000, [int]$PollMs = 50)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
        foreach ($w in $windows) {
            if ($w.Current.Name -like 'RemEx*') { return $sw.ElapsedMilliseconds }
        }
        Start-Sleep -Milliseconds $PollMs
    }
    throw "RemEx main window did not appear within ${TimeoutMs}ms of launch (PID $ProcessId). " +
        'If a firewall or elevation prompt is on screen, dismiss it and re-run — this script will ' +
        'not retry blindly past a blocked launch.'
}

# Established TCP connections owned by $ProcessId, as a proxy for "a phone session is up" (the app
# auto-connects to the paired phone on every launch, and a live session in one ref's sample and not
# the other's can move tens of megabytes - see docs/PERF-BASELINE.md Caveats). Deliberately does
# NOT call Get-NetTCPConnection: that cmdlet goes through the NetTCPIP module's CIM (WMI) proxy,
# the exact same native loader that Get-ScheduledTask uses, and this script already loads
# UIAutomationClient/UIAutomationTypes in-process for the window poll above - the same combination
# that permanently breaks CIM for the rest of the process's life (bd memory
# uiautomation-breaks-scheduledtasks-in-process; see also Invoke-UpdateLocalInstall's comment).
# netstat.exe is an external process with no CIM involved at all, so it sidesteps the conflict
# entirely instead of paying for a fresh pwsh.exe child on every sample. Returns $null - not a
# sentinel count like -1 - when the query itself could not be answered, so a genuine "zero
# established connections" is never confused with "the count is unknown".
function Get-EstablishedConnectionCount {
    param([int]$ProcessId)
    try {
        $lines = & netstat.exe -ano -p TCP 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $lines) { return $null }
        $count = 0
        foreach ($line in $lines) {
            $fields = ($line.Trim() -split '\s+')
            if ($fields.Count -ge 5 -and $fields[0] -eq 'TCP' -and $fields[3] -eq 'ESTABLISHED' -and $fields[4] -eq "$ProcessId") {
                $count++
            }
        }
        return $count
    } catch {
        return $null
    }
}

# Renders a connection count for display: an actual count if we have one, otherwise "unknown"
# rather than a blank or a misleading 0/-1.
function Format-ConnectionCount {
    param($Value)
    if ($null -eq $Value) { return 'unknown' }
    return "$Value"
}

# One full launch/measure/stop cycle. The caller decides whether this is the cold sample (index 0)
# or a warm one. Takes TWO memory samples per launch: one at $SettleSeconds (the original sample)
# and a later one at $SteadySeconds, so a slow post-startup climb (uncollected gen0/gen1, or a
# background animation still allocating) shows up instead of being hidden by an early sample.
function Measure-RemexLaunch {
    param([int]$SettleSeconds, [int]$SteadySeconds)
    Stop-RemexHost
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process -FilePath $ExePath -PassThru
    $timeToWindowMs = Wait-RemexWindow -ProcessId $proc.Id

    Start-Sleep -Seconds $SettleSeconds
    $proc.Refresh()
    $settleWorkingSetMB = [Math]::Round($proc.WorkingSet64 / 1MB, 1)
    $settlePrivateMB = [Math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
    $settleHandles = $proc.HandleCount
    $settleConnections = Get-EstablishedConnectionCount -ProcessId $proc.Id

    $remainingSeconds = [Math]::Max(0, $SteadySeconds - $SettleSeconds)
    if ($remainingSeconds -gt 0) { Start-Sleep -Seconds $remainingSeconds }
    $proc.Refresh()
    $steady = [PSCustomObject]@{
        WorkingSetMB = [Math]::Round($proc.WorkingSet64 / 1MB, 1)
        PrivateMB    = [Math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
        Handles      = $proc.HandleCount
        Connections  = Get-EstablishedConnectionCount -ProcessId $proc.Id
    }

    $sample = [PSCustomObject]@{
        TimeToWindowMs = $timeToWindowMs
        WorkingSetMB   = $settleWorkingSetMB
        PrivateMB      = $settlePrivateMB
        Handles        = $settleHandles
        Connections    = $settleConnections
        Steady         = $steady
    }
    Stop-RemexHost
    return $sample
}

function Get-Percentile {
    param([double[]]$Values, [double]$Percentile)
    $sorted = @($Values | Sort-Object)
    $idx = [Math]::Ceiling($Percentile / 100.0 * $sorted.Count) - 1
    $idx = [Math]::Max(0, [Math]::Min($sorted.Count - 1, [int]$idx))
    return $sorted[$idx]
}

# Median of a set of connection-count samples, any of which may be $null (the query could not be
# answered for that sample - see Get-EstablishedConnectionCount). [double]$null silently becomes
# 0 in PowerShell, which is exactly the "unknown read as zero" bug this function exists to avoid:
# if ANY sample in the set is unknown, the whole median is reported as 'unknown' rather than
# quietly averaging in a false zero.
function Get-ConnectionMedian {
    param([object[]]$Values)
    foreach ($v in $Values) {
        if ($null -eq $v) { return 'unknown' }
    }
    $doubles = @($Values | ForEach-Object { [double]$_ })
    return [string](Get-Percentile $doubles 50)
}

function ConvertTo-SafeFileName {
    param([string]$Text)
    return ($Text -replace '[\\/:*?"<>|]', '_')
}

# Runs an update-local-install.ps1 copy in a FRESH pwsh.exe process rather than in-process
# (call-operator '&' into this same process). This is not a style choice: this script loads
# UIAutomationClient/UIAutomationTypes (for the window poll below), and doing that in-process
# permanently breaks the ScheduledTasks module's CIM native loader for the rest of THIS process's
# lifetime — every later Get-ScheduledTask/Stop-ScheduledTask throws "The type initializer for
# 'Microsoft.Management.Infrastructure.Native.ApplicationMethods' threw an exception.", which is
# exactly what update-local-install.ps1 calls first. Isolating each deploy in its own process
# (confirmed to avoid the conflict) is cheaper than trying to reorder Add-Type calls and hope.
function Invoke-UpdateLocalInstall {
    param([string]$ScriptPath, [string]$InstallDirParam)
    & pwsh -NoProfile -File $ScriptPath -InstallDir $InstallDirParam -NoRestart
    if ($LASTEXITCODE -ne 0) {
        throw "update-local-install.ps1 ($ScriptPath) exited with code $LASTEXITCODE."
    }
}

# ─────────────────────────────────────────────────────────────────────────────
# Main
# ─────────────────────────────────────────────────────────────────────────────

$wasRunningBeforeScript = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue).Count -gt 0
$worktreePaths = New-Object System.Collections.Generic.List[string]
$results = New-Object System.Collections.Generic.List[object]

try {
    foreach ($ref in $Refs) {
        Write-Host ''
        Write-Host "=== $ref ===" -ForegroundColor Green

        $commitHash = (& git -C $RepoRoot rev-parse $ref).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $commitHash) {
            throw "git rev-parse failed to resolve ref '$ref'."
        }

        $worktreePath = Join-Path $env:TEMP ('remex-perf-' + (ConvertTo-SafeFileName $ref) + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
        Write-Host "Adding worktree for $ref ($commitHash) -> $worktreePath" -ForegroundColor Cyan
        & git -C $RepoRoot worktree add --detach $worktreePath $commitHash
        if ($LASTEXITCODE -ne 0) { throw "git worktree add failed for ref '$ref' ($commitHash)." }
        $worktreePaths.Add($worktreePath)

        $wtScript = Join-Path $worktreePath 'scripts\update-local-install.ps1'
        if (-not (Test-Path $wtScript)) {
            throw "Ref '$ref' ($commitHash) has no scripts\update-local-install.ps1 in its worktree. " +
                'This script relies on every measured ref carrying that same -InstallDir/-SkipPublish/' +
                '-NoRestart contract; add a -SourceRoot parameter to update-local-install.ps1 in the ' +
                'current checkout and use it for this ref if that ever stops being true.'
        }

        Write-Host "Deploying $ref to $InstallDir via the worktree's own update-local-install.ps1..." -ForegroundColor Cyan
        $deploySw = [System.Diagnostics.Stopwatch]::StartNew()
        Invoke-UpdateLocalInstall -ScriptPath $wtScript -InstallDirParam $InstallDir
        $deploySw.Stop()

        Write-Host "Measuring $Launches launch(es) (1 cold + $($Launches - 1) warm), settle ${SettleSeconds}s + steady ${SteadySeconds}s..." -ForegroundColor Cyan
        $samples = @()
        for ($i = 1; $i -le $Launches; $i++) {
            $sample = Measure-RemexLaunch -SettleSeconds $SettleSeconds -SteadySeconds $SteadySeconds
            Write-Host ("  launch {0}/{1}: {2} ms to window, {3} MB working set (settle, {4} conn), {5} MB working set (steady, {6} conn)" -f `
                    $i, $Launches, $sample.TimeToWindowMs, $sample.WorkingSetMB, (Format-ConnectionCount $sample.Connections), `
                    $sample.Steady.WorkingSetMB, (Format-ConnectionCount $sample.Steady.Connections)) -ForegroundColor DarkGray
            $samples += $sample
        }

        $cold = $samples[0]
        $warm = @($samples[1..($samples.Count - 1)])
        $warmTimes = @($warm | ForEach-Object { [double]$_.TimeToWindowMs })
        $warmWorkingSet = @($warm | ForEach-Object { [double]$_.WorkingSetMB })
        $warmPrivate = @($warm | ForEach-Object { [double]$_.PrivateMB })
        $warmHandles = @($warm | ForEach-Object { [double]$_.Handles })
        $warmConnections = @($warm | ForEach-Object { $_.Connections })
        $warmSteadyWorkingSet = @($warm | ForEach-Object { [double]$_.Steady.WorkingSetMB })
        $warmSteadyPrivate = @($warm | ForEach-Object { [double]$_.Steady.PrivateMB })
        $warmSteadyHandles = @($warm | ForEach-Object { [double]$_.Steady.Handles })
        $warmSteadyConnections = @($warm | ForEach-Object { $_.Steady.Connections })

        # With the default 7 launches there are only 6 warm samples, and a "90th percentile" of 6
        # values is really just the max - see docs/PERF-BASELINE.md Caveats. Label the column
        # honestly instead of implying a percentile the sample size can't support.
        $warmP90Label = if ($warm.Count -lt 10) { 'warm max' } else { 'Warm P90' }

        $refResult = [PSCustomObject]@{
            Ref                       = $ref
            CommitHash                = $commitHash
            PublishDurationMs         = [Math]::Round($deploySw.Elapsed.TotalMilliseconds, 0)
            ColdStartMs               = $cold.TimeToWindowMs
            WarmMedianMs              = Get-Percentile $warmTimes 50
            WarmP90Ms                 = Get-Percentile $warmTimes 90
            WarmP90Label              = $warmP90Label
            WorkingSetMedianMB        = Get-Percentile $warmWorkingSet 50
            PrivateMedianMB           = Get-Percentile $warmPrivate 50
            HandlesMedian             = Get-Percentile $warmHandles 50
            ConnectionsMedian         = Get-ConnectionMedian $warmConnections
            SteadyWorkingSetMedianMB  = Get-Percentile $warmSteadyWorkingSet 50
            SteadyPrivateMedianMB     = Get-Percentile $warmSteadyPrivate 50
            SteadyHandlesMedian       = Get-Percentile $warmSteadyHandles 50
            SteadyConnectionsMedian   = Get-ConnectionMedian $warmSteadyConnections
            Samples                   = $samples
        }
        $results.Add($refResult)

        $refFileName = 'perf-' + (ConvertTo-SafeFileName $ref) + '.json'
        $refJsonPath = Join-Path $Out $refFileName
        [System.IO.File]::WriteAllText($refJsonPath, ($refResult | ConvertTo-Json -Depth 6))
        Write-Host "Wrote $refJsonPath" -ForegroundColor DarkGray
    }

    # Deterministic column order. All rows in one run share the same -Launches, so the warm
    # P90/max label is the same for every row - take it from the first result.
    $warmColumnLabel = if ($results.Count -gt 0) { $results[0].WarmP90Label } else { 'Warm P90' }
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add(('| Ref | Commit | Cold Start (ms) | Warm Median (ms) | {0} (ms) | Working Set @Settle (MB) | Private @Settle (MB) | Handles @Settle | Conn @Settle | Working Set @Steady (MB) | Private @Steady (MB) | Handles @Steady | Conn @Steady |' -f $warmColumnLabel))
    $lines.Add('|---|---|---|---|---|---|---|---|---|---|---|---|---|')
    foreach ($r in $results) {
        $lines.Add(('| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12} |' -f `
                    $r.Ref, $r.CommitHash.Substring(0, 7), $r.ColdStartMs, $r.WarmMedianMs, $r.WarmP90Ms, `
                    $r.WorkingSetMedianMB, $r.PrivateMedianMB, $r.HandlesMedian, $r.ConnectionsMedian, `
                    $r.SteadyWorkingSetMedianMB, $r.SteadyPrivateMedianMB, $r.SteadyHandlesMedian, $r.SteadyConnectionsMedian))
    }
    $summaryPath = Join-Path $Out 'perf-summary.md'
    [System.IO.File]::WriteAllText($summaryPath, ($lines -join "`n") + "`n")
    Write-Host ''
    Write-Host "Summary: $summaryPath" -ForegroundColor Green
    Get-Content $summaryPath | Write-Host
}
finally {
    Write-Host ''
    Write-Host 'Restoring the machine: redeploying HEAD from this checkout...' -ForegroundColor Yellow
    try {
        Invoke-UpdateLocalInstall -ScriptPath (Join-Path $RepoRoot 'scripts\update-local-install.ps1') -InstallDirParam $InstallDir
    } catch {
        Write-Warning "Failed to redeploy HEAD after the run: $_"
    }

    foreach ($wt in $worktreePaths) {
        Write-Host "Removing worktree $wt..." -ForegroundColor DarkGray
        try {
            & git -C $RepoRoot worktree remove --force $wt
        } catch {
            Write-Warning "Failed to remove worktree $wt : $_"
        }
    }
    try { & git -C $RepoRoot worktree prune } catch { Write-Warning "git worktree prune failed: $_" }

    if ($wasRunningBeforeScript) {
        Write-Host 'Restarting the installed host (it was running before this script started)...' -ForegroundColor Yellow
        try {
            Start-Process -FilePath $ExePath
        } catch {
            Write-Warning "Failed to restart Remex.Agent.exe: $_"
        }
    }
}

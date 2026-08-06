#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs one board-drain lane as a headless Claude Code session, and exits when it is done.

.DESCRIPTION
    This is the answer to the question docs/SPEC-parallel-board-drain-dispatcher.md section 10 left
    open: what a lane actually IS. It is a `claude -p` session running in the lane's worktree, on
    Opus, working the one bead the dispatcher assigned it.

    It implements the -Launcher contract of scripts/ralph-dispatch.ps1 exactly, which is a fixed
    POSITIONAL argument list and never a command string:

        <laneRoot> <laneNumber> <beadId> <branch> <promptPath>

    Nothing about the rest of the design depends on this file. Delete it and lanes go back to being
    started by hand; swap it for a different program and the dispatcher does not notice. That is
    deliberate - the loop is the asset, and how an agent gets started is the part most likely to
    change.

    PERMISSIONS. The session runs with --dangerously-skip-permissions, and the reason is specific
    rather than general: a lane is a throwaway worktree OUTSIDE the repository root, on its own
    branch, with no push rights exercised and no path into the integration branch except through
    the merge queue - which rebases, re-verifies in the integration tree, and quarantines anything
    that fails. Nothing a lane does is trusted on the strength of the lane having done it. It is
    also the only setting that does not deadlock: a permission prompt in a headless session with
    nobody watching blocks that lane until it times out, silently.

    The session is deliberately NOT given --add-dir for the integration tree. Its bead lives on the
    shared board, which it reaches through BEADS_DIR because bd is a subprocess reading the
    environment - not through the file tools.

    SELF-HEALING. If the agent exits without having marked its bead ready-to-land, this script
    marks the bead 'returned'. That matters more than it sounds: a lane stuck in 'working' holds a
    path claim, and a claim nobody is honouring blocks every future wave from touching those files.
    A crashed lane must not quietly cost throughput for the rest of the day.

.PARAMETER LaneRoot
    The lane worktree. Positional 0.

.PARAMETER Lane
    The lane number. Positional 1.

.PARAMETER Bead
    The bead this lane was assigned. Positional 2.

.PARAMETER Branch
    The lane's branch, ralph/lane-<n>/<bead-id>. Positional 3.

.PARAMETER PromptPath
    The loop procedure the agent must follow - docs/ralph-board-drain.md, inside the lane.
    Positional 4.

.EXAMPLE
    ./scripts/ralph-dispatch.ps1 -Lanes 3 -Launcher ./scripts/ralph-lane-agent.ps1
    The normal way in. The dispatcher calls this once per provisioned lane.

.NOTES
    Environment overrides, all optional:
      RALPH_AGENT_MODEL   model for the lane session. Default 'opus' - the board holds genuinely
                          hard beads, so the executor needs the judgment, not just the reviewer.
      RALPH_AGENT_EFFORT  --effort level, passed through untouched when set.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$LaneRoot,

    [Parameter(Mandatory = $true, Position = 1)]
    [int]$Lane,

    [Parameter(Mandatory = $true, Position = 2)]
    [string]$Bead,

    [Parameter(Mandatory = $true, Position = 3)]
    [string]$Branch,

    [Parameter(Mandatory = $true, Position = 4)]
    [string]$PromptPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The agent's own exit code is the interesting one, and several commands here are expected to fail
# as a normal answer. Same reasoning as the other ralph-* scripts.
if (Test-Path 'variable:PSNativeCommandUseErrorActionPreference') {
    $PSNativeCommandUseErrorActionPreference = $false
}

function Write-Line {
    param([string]$Text, [string]$Colour = 'Gray')
    Write-Host "[lane $Lane] $Text" -ForegroundColor $Colour
}

function Stop-Hard {
    param([string]$Text)
    Write-Host "[lane $Lane] $Text" -ForegroundColor Red
    exit 2
}

# ---------------------------------------------------------------------------
# The lane's environment.
# ---------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $LaneRoot -PathType Container)) {
    Stop-Hard "Lane root does not exist: $LaneRoot"
}

$envFile = Join-Path $LaneRoot '.ralph' 'lane.env'
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) {
    Stop-Hard "No lane environment at $envFile. Provision the lane with scripts/ralph-lane-bootstrap.ps1 first."
}

# Plain KEY=VALUE, written by the bootstrap. Split on the FIRST '=' only: a Windows path in the
# value is fine, but a value containing '=' would be truncated by a naive split.
$laneEnv = @{}
foreach ($line in (Get-Content -LiteralPath $envFile)) {
    $trimmed = $line.Trim()
    if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }
    $split = $trimmed.IndexOf('=')
    if ($split -lt 1) { continue }
    $key = $trimmed.Substring(0, $split).Trim()
    $value = $trimmed.Substring($split + 1)
    $laneEnv[$key] = $value
    Set-Item -Path "Env:$key" -Value $value
}

if (-not $laneEnv.ContainsKey('RALPH_INTEGRATION_ROOT')) {
    Stop-Hard "$envFile does not name RALPH_INTEGRATION_ROOT, so this script cannot find the shared board."
}
$IntegrationRoot = $laneEnv['RALPH_INTEGRATION_ROOT']

# The prompt has to be the LANE's copy of the procedure, not the integration copy. The agent's
# workspace is the lane, and a path pointing outside it is either unreadable or - worse, if a
# future permission mode allows it - hands the lane a file it has no business reading. The file is
# tracked, so the lane's copy is identical by construction.
if (-not (Test-Path -LiteralPath $PromptPath -PathType Leaf)) {
    Stop-Hard "The loop procedure is not at $PromptPath."
}
$resolvedPrompt = (Resolve-Path -LiteralPath $PromptPath).Path
$resolvedLane = (Resolve-Path -LiteralPath $LaneRoot).Path
if (-not $resolvedPrompt.StartsWith($resolvedLane, [System.StringComparison]::OrdinalIgnoreCase)) {
    Stop-Hard "The prompt at $resolvedPrompt is outside the lane at $resolvedLane. The lane must be given its own copy of the procedure - the agent cannot read outside its workspace."
}

$claude = Get-Command claude -ErrorAction SilentlyContinue
if (-not $claude) {
    Stop-Hard 'The claude CLI is not on PATH, so this lane cannot be started. Start it by hand, or pass a different -Launcher.'
}

# Logs live in the INTEGRATION tree, not the lane: the reaper deletes a lane worktree once its work
# lands, and the log of how the work was done is exactly the thing worth keeping afterwards.
# /.ralph is gitignored, so nothing here reaches the verify fingerprint.
$logDir = Join-Path $IntegrationRoot '.ralph' 'lanes'
if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$logPath = Join-Path $logDir "lane-$Lane-$Bead.log"

function Invoke-Bd {
    $raw = & bd --directory $IntegrationRoot @args 2>&1
    $code = $LASTEXITCODE
    $text = (@($raw) | ForEach-Object { $_.ToString() }) -join "`n"
    return [pscustomobject]@{ ExitCode = $code; Output = $text.Trim() }
}

# ---------------------------------------------------------------------------
# Run the lane.
# ---------------------------------------------------------------------------

$model = if ($env:RALPH_AGENT_MODEL) { $env:RALPH_AGENT_MODEL } else { 'opus' }

# A pointer, not a procedure. Everything about HOW to do the work is in the file this names, which
# is the whole reason the spec forbids generating prompt text: the file can be edited mid-run and
# every lane started afterwards picks the change up, with no script to keep in step.
$prompt = @(
    "You are lane $Lane of a parallel RemEx board drain, working in this git worktree on branch $Branch."
    "Read $resolvedPrompt in full and follow it exactly, including the section titled LANE MODE, which overrides several steps for you."
    "Your assigned bead is $Bead. Do not pick a different one and do not work more than that one bead."
    "When it is finished and verified, mark it ready to land and stop. Do not close the bead and do not push."
) -join ' '

$claudeArgs = @('-p', $prompt, '--model', $model, '--dangerously-skip-permissions')
if ($env:RALPH_AGENT_EFFORT) { $claudeArgs += @('--effort', $env:RALPH_AGENT_EFFORT) }

Write-Line "starting $Bead on $model in $LaneRoot" 'Cyan'
Write-Line "log: $logPath"

$startedAt = Get-Date
Push-Location -LiteralPath $LaneRoot
try {
    # Tee rather than plain redirection so the log is readable WHILE the lane runs - the operator
    # watching a wave wants to see where a lane got to, not a file that appears at the end.
    & $claude.Source @claudeArgs 2>&1 | Tee-Object -FilePath $logPath
    $agentExit = $LASTEXITCODE
}
finally {
    Pop-Location
}
$elapsed = [int]((Get-Date) - $startedAt).TotalSeconds

# ---------------------------------------------------------------------------
# Post-flight. A lane that did not mark itself must not keep its claim.
# ---------------------------------------------------------------------------

$state = ''
$show = Invoke-Bd show $Bead --json
if ($show.ExitCode -eq 0) {
    try {
        $issue = $show.Output | ConvertFrom-Json
        if ($issue -is [array]) { $issue = $issue[0] }
        if ($issue.PSObject.Properties.Name -contains 'metadata' -and $issue.metadata -and
            $issue.metadata.PSObject.Properties.Name -contains 'ralphLaneState') {
            $state = [string]$issue.metadata.ralphLaneState
        }
    }
    catch { $state = '' }
}

if ($state -ceq 'ready-to-land') {
    Write-Line "finished in ${elapsed}s, ready to land." 'Green'
    exit $agentExit
}

if ($state -ceq 'returned' -or $state -ceq 'quarantined') {
    Write-Line "finished in ${elapsed}s, state '$state'. The branch is kept as evidence." 'Yellow'
    exit $agentExit
}

# Anything else - still 'working', or no state at all - means the agent stopped without completing
# the contract: it crashed, ran out of turns, or decided not to finish. Whatever the cause, the
# claim it holds is now blocking other lanes from files nobody is editing, so release it and say so.
$note = "Lane $Lane ended after ${elapsed}s without marking this bead ready-to-land (agent exit $agentExit, lane state was '$(if ($state) { $state } else { 'unset' })'). The dispatcher returned it so its path claim stops blocking other lanes. The branch $Branch is kept as evidence and the session log is at $logPath."
Invoke-Bd update $Bead --status open --set-metadata ralphLaneState=returned --append-notes $note | Out-Null
Write-Line "did not finish (agent exit $agentExit after ${elapsed}s). Bead returned, branch kept, log at $logPath." 'Yellow'
exit $(if ($agentExit -ne 0) { $agentExit } else { 1 })

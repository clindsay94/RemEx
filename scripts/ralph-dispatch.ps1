#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Drives the parallel board drain: plan, provision, run, land, reap.

.DESCRIPTION
    This is the front end for the three scripts that do the actual work, and it deliberately
    contains almost no logic of its own:

      scripts/ralph-cluster.ps1         which beads can run together, and the path claims
      scripts/ralph-lane-bootstrap.ps1  one lane, provisioned and proven to build
      scripts/ralph-merge-queue.ps1     serialised landing with an integration-tree verify

    Designed in docs/SPEC-parallel-board-drain-dispatcher.md section 8 (RemEx-56fu.5.5).

    THE DISPATCHER HOLDS NO STATE. Every phase reconstructs what it needs from two places that
    survive this process dying: bd, and the branches under refs/heads/ralph/. Nothing in .ralph/
    is trusted across invocations and there is no journal of its own. That is not tidiness - a
    dispatcher that dies mid-run is a normal event, and the alternative is a private view of the
    world that disagrees with the repository and cannot be told apart from the truth by looking.
    -Status is the proof: it reads bd and git and nothing else, so it is correct even on a
    machine where this script has never run.

    WHAT LAUNCHES A LANE IS DELIBERATELY NOT DECIDED HERE. The spec leaves it open (section 10)
    because it is the most likely thing to change and nothing else depends on it: a lane is
    anything that can run the loop in a directory and exit. So the default is to provision the
    lanes and print the exact command for each, and -Launcher takes a program that this script
    starts once per lane with a fixed argument list:

        <launcher> <laneRoot> <laneNumber> <beadId> <branch> <promptPath>

    where promptPath is the LANE's own copy of docs/ralph-board-drain.md, because an agent's
    workspace is its worktree and a path pointing back at the integration tree is at best
    unreadable to it. scripts/ralph-lane-agent.ps1 is the launcher that ships with this, running
    the lane as a headless Claude Code session.

    A program and an argument array, never a command string - the dispatcher must not compose
    shell text (constraint 4), and passing argv directly removes the quoting and encoding
    corruption that has repeatedly bitten this repository.

.PARAMETER Lanes
    How many lanes to plan and provision. Defaults to 3, which Phase 0 measured at 2.89x serial
    bead throughput. Higher is not obviously better: the lanes share a NuGet cache and a Gradle
    daemon, and four concurrent .NET verifies already slow each other by 17%.

.PARAMETER PlanOnly
    Print the clustering and exit, provisioning nothing. The clustering is the part of this
    design most likely to be wrong, so it has to be inspectable on its own.

.PARAMETER Limit
    How many ready beads the planner considers. Passed straight through.

.PARAMETER UseLabelHistory
    Turn on the planner's label co-change source. Off by default because it was measured on this
    repo and yields almost nothing. Passed straight through.

.PARAMETER Launcher
    A program or script that runs one lane, started once per provisioned lane with the fixed
    argument list described above. Without it, this script stops after provisioning and prints
    what to run - which is the honest default while the launch mechanism is undecided.

.PARAMETER NoWait
    With -Launcher, start the lanes and return immediately instead of waiting for them and then
    landing. Use it when the lanes are long-running sessions you want to watch.

.PARAMETER SkipLaneVerify
    Passed to the bootstrap: do not prove each lane builds before giving it work. Faster, and it
    gives up the one guarantee provisioning exists to provide. For debugging provisioning only.

.PARAMETER Land
    Drain the merge queue and do nothing else.

.PARAMETER Scope
    What the merge queue's per-landing verify runs. 'all' (default) includes Android.

.PARAMETER Reap
    Tear down lanes whose work has landed - remove the worktree, delete the branch, release the
    path claim. Anything that did not land is kept, with the reason, because a failed lane's
    branch is the evidence for its reopened bead.

.PARAMETER DryRun
    With -Reap, say what would be removed and remove nothing.

.PARAMETER Status
    Print what is running, claimed and queued, reconstructed from bd and git alone.

.PARAMETER Watch
    Poll the same reconstruction and print ONE LINE PER CHANGE, then exit once no lane is working
    any more. Built for an orchestrator watching a wave: -Status answers "what now", -Watch answers
    "tell me when something happens" without anyone re-running anything.

    It reports every terminal state, not just the good one. A watcher that printed only
    'ready-to-land' would stay silent through a quarantine, and silence looks exactly like a lane
    that is still thinking.

.PARAMETER IntervalSeconds
    How often -Watch re-reads the board. Default 60. A bead takes ~18 minutes; polling faster buys
    nothing and costs a bd query per lane per poll.

.PARAMETER MaxMinutes
    How long -Watch keeps going before giving up and saying so. Default 240.

.PARAMETER LanesRoot
    Where lanes live. Defaults to a sibling of the repository - Z:\RemEx gives Z:\RemEx.lanes.
    Lanes must live outside the repository root; verify.ps1 fingerprints untracked-but-not-
    ignored files, so a nested worktree would pollute every other lane's receipt.

.PARAMETER Json
    Print one line of JSON and nothing else.

.EXAMPLE
    ./scripts/ralph-dispatch.ps1 -Lanes 3 -PlanOnly
    Show which three beads would go out in parallel and why. Changes nothing.

.EXAMPLE
    ./scripts/ralph-dispatch.ps1 -Lanes 3
    Plan, provision three lanes, and print the command to start each one.

.EXAMPLE
    ./scripts/ralph-dispatch.ps1 -Status
    What is running, what is waiting to land, what is quarantined.

.EXAMPLE
    ./scripts/ralph-dispatch.ps1 -Land
    Land every lane that marked itself ready, one at a time, verifying each landing.
#>
[CmdletBinding(DefaultParameterSetName = 'Drain')]
param(
    [Parameter(ParameterSetName = 'Drain')]
    [ValidateRange(1, 16)]
    [int]$Lanes = 3,

    [Parameter(ParameterSetName = 'Drain')]
    [switch]$PlanOnly,

    [Parameter(ParameterSetName = 'Drain')]
    [ValidateRange(1, 500)]
    [int]$Limit = 30,

    [Parameter(ParameterSetName = 'Drain')]
    [switch]$UseLabelHistory,

    [Parameter(ParameterSetName = 'Drain')]
    [string]$Launcher,

    [Parameter(ParameterSetName = 'Drain')]
    [switch]$NoWait,

    [Parameter(ParameterSetName = 'Drain')]
    [switch]$SkipLaneVerify,

    [Parameter(ParameterSetName = 'Land', Mandatory = $true)]
    [switch]$Land,

    [Parameter(ParameterSetName = 'Drain')]
    [Parameter(ParameterSetName = 'Land')]
    [ValidateSet('dotnet', 'all')]
    [string]$Scope = 'all',

    [Parameter(ParameterSetName = 'Reap', Mandatory = $true)]
    [switch]$Reap,

    [Parameter(ParameterSetName = 'Reap')]
    [switch]$DryRun,

    [Parameter(ParameterSetName = 'Status', Mandatory = $true)]
    [switch]$Status,

    [Parameter(ParameterSetName = 'Watch', Mandatory = $true)]
    [switch]$Watch,

    [Parameter(ParameterSetName = 'Watch')]
    [ValidateRange(10, 3600)]
    [int]$IntervalSeconds = 60,

    [Parameter(ParameterSetName = 'Watch')]
    [ValidateRange(1, 1440)]
    [int]$MaxMinutes = 240,

    [string]$LanesRoot,

    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Same reasoning as the merge queue: this script runs git commands whose failure is a normal
# answer - rev-parse --verify on a branch that does not exist is how you ask whether it exists -
# and a machine that has flipped this preference would turn every one of those into a thrown
# error instead of an answer.
if (Test-Path 'variable:PSNativeCommandUseErrorActionPreference') {
    $PSNativeCommandUseErrorActionPreference = $false
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ClusterScript = Join-Path $PSScriptRoot 'ralph-cluster.ps1'
$BootstrapScript = Join-Path $PSScriptRoot 'ralph-lane-bootstrap.ps1'
$QueueScript = Join-Path $PSScriptRoot 'ralph-merge-queue.ps1'
$PromptPath = Join-Path $RepoRoot 'docs' 'ralph-board-drain.md'
$ScratchDir = Join-Path $RepoRoot '.ralph'
$started = Get-Date

if (-not $LanesRoot) {
    $LanesRoot = Join-Path (Split-Path -Parent $RepoRoot) ((Split-Path -Leaf $RepoRoot) + '.lanes')
}

# ---------------------------------------------------------------------------
# Output helpers. Everything human-facing goes through these so -Json can
# silence the lot and leave a single parseable line on stdout. Same shape as
# scripts/verify.ps1 and the other ralph-* scripts.
# ---------------------------------------------------------------------------

function Write-Say {
    param([string]$Text, [string]$Colour = 'Gray')
    if (-not $Json) { Write-Host $Text -ForegroundColor $Colour }
}

function Write-Stage {
    param([string]$Text)
    if (-not $Json) { Write-Host "`n$Text" -ForegroundColor Cyan }
}

function Write-Warn {
    param([string]$Text)
    if (-not $Json) { Write-Host "  Note: $Text" -ForegroundColor Yellow }
}

function Stop-WithProblem {
    param([string]$Stage, [string]$Text, [string]$WhatToDo)
    if ($Json) {
        Write-Output (@{
            schema = 1; result = 'FAIL'; stage = $Stage
            problem = $Text; whatToDo = $WhatToDo
        } | ConvertTo-Json -Compress)
    }
    else {
        Write-Host "`n  Problem: $Text" -ForegroundColor Red
        if ($WhatToDo) { Write-Host "  What to do: $WhatToDo" -ForegroundColor Yellow }
    }
    exit 1
}

# ---------------------------------------------------------------------------
# Small helpers.
# ---------------------------------------------------------------------------

# StrictMode turns a missing property into a terminating error, and bd omits 'metadata' entirely
# on a bead that has none - which is the common case.
function Get-Prop {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    if ($Object.PSObject.Properties.Name -contains $Name) { return $Object.$Name }
    return $null
}

# Collects arguments through $args and must stay a SIMPLE function to do so, for the reason
# documented at length in ralph-merge-queue.ps1: [Parameter(ValueFromRemainingArguments)] would
# make this advanced, which silently adds PowerShell's common parameters, and '-d' is an
# unambiguous prefix of '-Debug'. Do not add [Parameter()] here.
function Invoke-Git {
    param([string]$In)
    $raw = & git -C $In @args 2>&1
    $code = $LASTEXITCODE
    $text = (@($raw) | ForEach-Object { $_.ToString() }) -join "`n"
    return [pscustomobject]@{ ExitCode = $code; Output = $text.Trim() }
}

# --directory is pinned rather than relying on the working directory, because bd resolves its
# workspace by walking up from cwd. Simple, not advanced, for the same reason as Invoke-Git.
function Invoke-Bd {
    $raw = & bd --directory $RepoRoot @args 2>&1
    $code = $LASTEXITCODE
    $text = (@($raw) | ForEach-Object { $_.ToString() }) -join "`n"
    return [pscustomobject]@{ ExitCode = $code; Output = $text.Trim() }
}

function Get-BeadJson {
    param([string]$Id)
    $result = Invoke-Bd show $Id --json
    if ($result.ExitCode -ne 0) { return $null }
    try {
        $parsed = $result.Output | ConvertFrom-Json
        if ($parsed -is [array]) { return $parsed[0] }
        return $parsed
    }
    catch { return $null }
}

function Get-PwshPath {
    # Reuse the interpreter already running rather than hoping the right pwsh is first on PATH.
    $self = (Get-Process -Id $PID).Path
    if ($self) { return $self }
    return 'pwsh'
}

# Runs one of the sibling scripts in a child process and hands back its single JSON line.
#
# A child process is not a preference. Every one of those scripts ends in `exit`, and a .ps1
# invoked in-process runs its `exit` in THIS session - the dispatcher would terminate partway
# through provisioning, having already claimed beads, with no summary and no reap.
function Invoke-ChildScript {
    param(
        [string]$Script,
        [string[]]$ScriptArgs,
        # Print the child's output as well as capturing it. Off by default because most callers
        # here want a JSON answer and nothing else; on for the merge queue, whose report IS the
        # thing the operator needs and which used to be swallowed whole.
        [switch]$Echo
    )

    $pwshExe = Get-PwshPath

    # Redirect to a file and wait on the PROCESS rather than capturing with `& ... 2>&1`. The call
    # operator returns when every handle on the child's stdout closes, not when the child exits,
    # and verify.ps1 -Scope all - reachable from here through both the merge queue and the lane
    # bootstrap - starts a Gradle daemon that inherits that handle and outlives the build by
    # design. That cost one landing three and a half hours after 139 seconds of real work. An
    # inherited file handle blocks nobody. See RemEx-xx1u.
    $outFile = [System.IO.Path]::GetTempFileName()
    $errFile = [System.IO.Path]::GetTempFileName()
    $lines = @()
    $code = 1
    try {
        $proc = Start-Process -FilePath $pwshExe `
            -ArgumentList (@('-NoProfile', '-File', $Script) + @($ScriptArgs)) `
            -NoNewWindow -PassThru `
            -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        $proc.WaitForExit()
        $code = $proc.ExitCode

        foreach ($f in @($outFile, $errFile)) {
            if (Test-Path -LiteralPath $f) {
                $lines += @(Get-Content -LiteralPath $f -ErrorAction SilentlyContinue)
            }
        }
    }
    finally {
        Remove-Item -LiteralPath $outFile -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $errFile -Force -ErrorAction SilentlyContinue
    }

    $lines = @($lines | ForEach-Object { [string]$_ })
    if ($Echo) { foreach ($l in $lines) { Write-Host $l } }

    # Scan back for the last line that parses. The scripts print exactly one JSON line under
    # -Json, but a child of a child (verify.ps1 inside the bootstrap) can still reach this
    # stream, and taking the last parseable object is right in both cases.
    $parsed = $null
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        $text = $lines[$i].Trim()
        if (-not $text.StartsWith('{')) { continue }
        try { $parsed = $text | ConvertFrom-Json; break } catch { continue }
    }

    return [pscustomobject]@{
        ExitCode = $code
        Json     = $parsed
        Output   = ($lines -join "`n").Trim()
    }
}

# Maps every branch checked out somewhere to the worktree holding it. Parsed from git rather
# than remembered anywhere, per the rule that this script holds no state of its own.
function Get-WorktreeByBranch {
    $map = @{}
    $result = Invoke-Git -In $RepoRoot worktree list --porcelain
    if ($result.ExitCode -ne 0) { return $map }
    $current = $null
    foreach ($line in ($result.Output -split "`n")) {
        $line = $line.Trim()
        if ($line.StartsWith('worktree ')) {
            $current = $line.Substring('worktree '.Length)
        }
        elseif ($line.StartsWith('branch refs/heads/') -and $current) {
            $map[$line.Substring('branch refs/heads/'.Length)] = $current
        }
    }
    return $map
}

# The whole board, reconstructed. This is the ONLY place lane state comes from, and it reads
# exactly two sources: the branches under refs/heads/ralph/, and bd. Every phase below uses it,
# which is why -Status cannot drift away from what -Reap and -Land actually do.
function Get-LaneBoard {
    $worktrees = Get-WorktreeByBranch
    $rows = [System.Collections.Generic.List[object]]::new()

    # The prefix form, not a 'ralph/lane-*' glob: git's ref globs are matched with FNM_PATHNAME,
    # so a '*' does not cross a '/' and the two-level branch names would silently match nothing.
    # (Measured while building the merge queue - it reported an empty board.)
    $refs = @((Invoke-Git -In $RepoRoot for-each-ref --format='%(refname:short)%09%(committerdate:unix)' 'refs/heads/ralph/').Output -split "`n" | Where-Object { $_ })

    foreach ($ref in $refs) {
        $parts = $ref -split "`t"
        $branch = $parts[0]
        $finishedAt = if ($parts.Count -gt 1) { [int64]$parts[1] } else { 0 }

        if ($branch -notmatch '^ralph/lane-(\d+)/(.+)$') {
            $rows.Add([pscustomobject]@{
                Branch = $branch; Bead = ''; Lane = 0; State = 'unrecognised'
                BeadStatus = ''; Title = ''; Paths = @(); Worktree = ''
                FinishedAt = $finishedAt
                Why = 'branch name does not look like ralph/lane-<n>/<bead-id>'
            })
            continue
        }

        $laneNumber = [int]$Matches[1]
        $beadId = $Matches[2]
        $issue = Get-BeadJson -Id $beadId
        $meta = if ($issue) { Get-Prop $issue 'metadata' } else { $null }

        $rawPaths = Get-Prop $meta 'ralphLanePaths'
        $paths = @()
        if ($rawPaths) {
            # bd stores a scalar as the parsed JSON scalar but keeps an array as its literal
            # string, so a claim comes back either way depending on how it was written.
            if ($rawPaths -is [array]) { $paths = @($rawPaths) }
            else { try { $paths = @(($rawPaths | ConvertFrom-Json)) } catch { $paths = @($rawPaths) } }
        }

        $rows.Add([pscustomobject]@{
            Branch     = $branch
            Bead       = $beadId
            Lane       = $laneNumber
            State      = if ($issue) { [string](Get-Prop $meta 'ralphLaneState') } else { '' }
            # The lane's review verdict, published the moment review finishes rather than at the
            # end of the session. A lane's log is a transcript that arrives all at once when
            # 'claude -p' exits, so without this the operator has no way to know whether the
            # work was reviewed until it is already over. See RemEx-2eoo.
            Review     = if ($issue) { [string](Get-Prop $meta 'ralphReviewVerdict') } else { '' }
            BeadStatus = if ($issue) { [string](Get-Prop $issue 'status') } else { '' }
            Title      = if ($issue) { [string](Get-Prop $issue 'title') } else { '' }
            Paths      = $paths
            Worktree   = if ($worktrees.ContainsKey($branch)) { $worktrees[$branch] } else { '' }
            FinishedAt = $finishedAt
            Why        = if ($issue) { '' } else { 'bd does not know this bead' }
        })
    }

    return , @($rows | Sort-Object Lane, Bead)
}

# One word for what a row is doing, and it is derived, never stored. A closed bead whose branch
# still exists is the definition of reapable: the merge queue closes a bead only after the
# integration tree verified green with its work in it.
function Get-RowDisposition {
    param($Row)
    if ($Row.Why) { return 'unknown' }
    if ($Row.BeadStatus -ceq 'closed') { return 'landed' }
    switch ($Row.State) {
        'working' { return 'working' }
        'ready-to-land' { return 'ready-to-land' }
        'returned' { return 'returned' }
        'quarantined' { return 'quarantined' }
        default { return 'unmarked' }
    }
}

# ---------------------------------------------------------------------------
# Preflight. Applies to every mode, because every mode is meaningless in the
# wrong tree.
# ---------------------------------------------------------------------------

foreach ($needed in @($ClusterScript, $BootstrapScript, $QueueScript, $PromptPath)) {
    if (-not (Test-Path -LiteralPath $needed -PathType Leaf)) {
        Stop-WithProblem 'preflight' "Missing $needed." `
            'All four are tracked files. Their absence means this is not a complete checkout, or the script was copied somewhere on its own.'
    }
}

if ((Invoke-Git -In $RepoRoot rev-parse --is-inside-work-tree).Output -cne 'true') {
    Stop-WithProblem 'preflight' "$RepoRoot is not a git working tree." 'Run this from the integration checkout.'
}

# Refuse to run from inside a lane. A worktree's git dir lives under the main repository's
# .git/worktrees/, so the common dir is the tell. Without this check, running the lane's own
# copy of this script would provision lanes-of-lanes and land onto a lane branch, and the first
# symptom would be a very confusing merge queue.
#
# The exit code is checked rather than the output alone. --path-format arrived in git 2.31, and on
# anything older this command FAILS with its usage text on stderr - which would then be treated as a
# path, mismatch the repo root, and refuse to run in the integration copy. A guard that fires when
# it cannot answer is worse than no guard.
$commonProbe = Invoke-Git -In $RepoRoot rev-parse --path-format=absolute --git-common-dir
$commonDir = if ($commonProbe.ExitCode -eq 0) { $commonProbe.Output } else { '' }
if ($commonDir) {
    $commonParent = Split-Path -Parent ($commonDir.TrimEnd('/', '\'))
    if ($commonParent -and -not [string]::Equals(
            [System.IO.Path]::GetFullPath($commonParent).TrimEnd('/', '\'),
            [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('/', '\'),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        Stop-WithProblem 'preflight' "$RepoRoot is a linked worktree, not the integration copy." `
            "The dispatcher runs in the integration tree and nowhere else - that is where the one board and the one merge queue live. Run $commonParent's copy of this script instead."
    }
}

$IntegrationBranch = (Invoke-Git -In $RepoRoot rev-parse --abbrev-ref HEAD).Output
if (-not $IntegrationBranch -or $IntegrationBranch -ceq 'HEAD') {
    Stop-WithProblem 'preflight' 'The integration copy is not on a branch (detached HEAD).' `
        'Check out the integration branch first. Lanes branch from it and the queue lands onto it.'
}

# ===========================================================================
# -Status. Reconstructed from bd and git branch, and from nothing else.
# ===========================================================================

if ($PSCmdlet.ParameterSetName -ceq 'Status') {
    $board = Get-LaneBoard
    $rows = @($board | ForEach-Object {
        [pscustomobject]@{
            lane        = $_.Lane
            bead        = $_.Bead
            branch      = $_.Branch
            disposition = Get-RowDisposition $_
            beadStatus  = $_.BeadStatus
            laneState   = $_.State
            worktree    = $_.Worktree
            title       = $_.Title
            paths       = @($_.Paths)
            why         = $_.Why
            review      = $_.Review
        }
    })

    if ($Json) {
        Write-Output (@{
            schema            = 1
            result            = if ($rows.Count -gt 0) { 'PASS' } else { 'NOOP' }
            integrationBranch = $IntegrationBranch
            lanesRoot         = $LanesRoot
            lanes             = @($rows)
            durationSec       = [int]((Get-Date) - $started).TotalSeconds
        } | ConvertTo-Json -Compress -Depth 6)
        exit 0
    }

    Write-Stage "Board drain status on $IntegrationBranch"
    if ($rows.Count -eq 0) {
        Write-Say '  No lane branches exist. Nothing is running and nothing is waiting to land.'
        Write-Say "  Start a wave with: ./scripts/ralph-dispatch.ps1 -Lanes 3"
        exit 0
    }

    $colours = @{
        'working' = 'Cyan'; 'ready-to-land' = 'Green'; 'landed' = 'DarkGray'
        'returned' = 'Yellow'; 'quarantined' = 'Red'; 'unmarked' = 'Yellow'; 'unknown' = 'Red'
    }
    foreach ($r in $rows) {
        $colour = if ($colours.ContainsKey($r.disposition)) { $colours[$r.disposition] } else { 'Gray' }
        Write-Host ("  lane {0,-2} {1,-14} {2}" -f $r.lane, $r.disposition, $r.branch) -ForegroundColor $colour
        if ($r.title) { Write-Host "              $($r.title)" -ForegroundColor DarkGray }
        if ($r.why) { Write-Host "              $($r.why)" -ForegroundColor Red }
        # Shown even for a lane still working, which is the point: an operator arriving mid-wave
        # can see that review has happened and what it said, without waiting for the transcript.
        if ($r.review) {
            $reviewColour = if ($r.review -match '^\s*FAIL') { 'Yellow' } else { 'Green' }
            Write-Host "              review $($r.review)" -ForegroundColor $reviewColour
        }
        elseif ($r.disposition -ceq 'ready-to-land') {
            Write-Host '              no review verdict published - the merge queue will check for a Reviewed-by trailer' -ForegroundColor DarkGray
        }
        if ($r.worktree) { Write-Host "              worktree $($r.worktree)" -ForegroundColor DarkGray }
        elseif ($r.disposition -ceq 'working') {
            Write-Host '              marked working but has no worktree - nothing is running in it' -ForegroundColor Yellow
        }
        if ($r.paths.Count -gt 0) {
            $shown = @($r.paths | Select-Object -First 4)
            Write-Host "              claims $($r.paths.Count): $($shown -join ', ')$(if ($r.paths.Count -gt 4) { ', ...' })" -ForegroundColor DarkGray
        }
    }

    $counts = $rows | Group-Object disposition | Sort-Object Name
    Write-Host ''
    foreach ($c in $counts) { Write-Say "  $($c.Name): $($c.Count)" }
    if (@($rows | Where-Object { $_.disposition -ceq 'ready-to-land' }).Count -gt 0) {
        Write-Say "`n  Land them with: ./scripts/ralph-dispatch.ps1 -Land" 'Green'
    }
    if (@($rows | Where-Object { $_.disposition -ceq 'landed' }).Count -gt 0) {
        Write-Say "  Tear down what landed with: ./scripts/ralph-dispatch.ps1 -Reap" 'Green'
    }
    if (@($rows | Where-Object { $_.disposition -ceq 'quarantined' }).Count -gt 0) {
        Write-Say '  Quarantined lanes are kept on purpose: the branch and its worktree are the evidence.' 'Yellow'
    }
    Write-Say "`n  All of the above came from bd and git branch. This script stores nothing." 'DarkGray'
    exit 0
}

# ===========================================================================
# -Watch. The same reconstruction as -Status, on a timer, emitting only what
# CHANGED. Every line on stdout is an event for whatever is watching this.
# ===========================================================================

if ($PSCmdlet.ParameterSetName -ceq 'Watch') {
    $deadline = (Get-Date).AddMinutes($MaxMinutes)
    $seen = @{}
    $seenReview = @{}
    $firstPass = $true

    while ($true) {
        $board = Get-LaneBoard
        $current = @{}

        foreach ($row in $board) {
            $disposition = Get-RowDisposition $row
            $current[$row.Branch] = $disposition

            if (-not $seen.ContainsKey($row.Branch)) {
                $suffix = if ($firstPass) { '' } else { ' (new lane)' }
                Write-Output "lane $($row.Lane)  $($row.Bead)  $disposition$suffix"
            }
            elseif ($seen[$row.Branch] -cne $disposition) {
                Write-Output "lane $($row.Lane)  $($row.Bead)  $($seen[$row.Branch]) -> $disposition"
            }

            # A second event stream on the same lane. Kept separate from the disposition rather
            # than folded into it because review is not a lane state: a lane goes on working
            # after a FAIL verdict, and collapsing the two would either lose the verdict or
            # invent a state the merge queue does not understand.
            $verdict = [string]$row.Review
            $priorVerdict = if ($seenReview.ContainsKey($row.Branch)) { [string]$seenReview[$row.Branch] } else { '' }
            if ($verdict -and $verdict -cne $priorVerdict) {
                Write-Output "lane $($row.Lane)  $($row.Bead)  review $verdict"
            }
            $seenReview[$row.Branch] = $verdict
        }

        foreach ($gone in @($seen.Keys | Where-Object { -not $current.ContainsKey($_) })) {
            Write-Output "$gone is gone (landed and reaped, or deleted by hand)"
        }

        $seen = $current
        # Flushed explicitly: whatever is watching this treats a line as an event, and a line
        # sitting in a buffer for the next poll interval is an event that arrives late enough to
        # be useless.
        [Console]::Out.Flush()

        $working = @($board | Where-Object { (Get-RowDisposition $_) -ceq 'working' })
        if ($working.Count -eq 0) {
            Write-Output "idle - no lane is working. $($board.Count) lane branch(es) remain."
            break
        }
        if ((Get-Date) -gt $deadline) {
            Write-Output "watch gave up after $MaxMinutes minute(s) with $($working.Count) lane(s) still working."
            break
        }

        $firstPass = $false
        Start-Sleep -Seconds $IntervalSeconds
    }
    exit 0
}

# ===========================================================================
# -Land. The merge queue, unchanged - this is a pass-through so that one
# entry point covers the whole workflow, not a second implementation.
# ===========================================================================

function Invoke-LandPhase {
    Write-Stage 'Landing'
    $queueArgs = @('-Scope', $Scope, '-LanesRoot', $LanesRoot)
    if ($Json) { $queueArgs += '-Json' }

    # -Echo because the queue's own report is the only account of what happened to each bead, and
    # capturing it without printing it meant a quarantine surfaced as "Merge queue exited 1" and
    # nothing else. Reconstructing one landing then took the journal, the bd note, the reflog and
    # process CPU counters to recover what the queue had already worked out and printed. The
    # instruction not to paraphrase the queue only works if the queue can be heard. See RemEx-gprg.
    $run = Invoke-ChildScript -Script $QueueScript -ScriptArgs $queueArgs -Echo:(-not $Json)
    if (-not $Json) {
        Write-Say "  Merge queue exited $($run.ExitCode)." $(if ($run.ExitCode -eq 0) { 'Green' } else { 'Yellow' })
    }
    return $run
}

if ($PSCmdlet.ParameterSetName -ceq 'Land') {
    $run = Invoke-LandPhase
    if ($Json) {
        Write-Output (@{
            schema = 1
            result = if ($run.ExitCode -eq 0) { 'PASS' } else { 'FAIL' }
            phase  = 'land'
            queue  = $run.Json
            durationSec = [int]((Get-Date) - $started).TotalSeconds
        } | ConvertTo-Json -Compress -Depth 7)
    }
    exit $run.ExitCode
}

# ===========================================================================
# -Reap. Removes only what landed. Everything else is evidence.
# ===========================================================================

function Invoke-ReapPhase {
    param([switch]$Pretend)

    Write-Stage 'Reaping'
    $board = Get-LaneBoard
    $removed = [System.Collections.Generic.List[object]]::new()
    $kept = [System.Collections.Generic.List[object]]::new()

    foreach ($row in $board) {
        $disposition = Get-RowDisposition $row
        if ($disposition -cne 'landed') {
            $why = switch ($disposition) {
                'working' { 'an agent is in it' }
                'ready-to-land' { 'it is waiting for the merge queue' }
                'returned' { 'the bead came back - the branch is the evidence' }
                'quarantined' { 'it failed on integration - the branch and worktree are the evidence' }
                'unmarked' { 'no lane state, so it may hold uncommitted or unmarked work' }
                default { $row.Why }
            }
            $kept.Add(@{ branch = $row.Branch; bead = $row.Bead; disposition = $disposition; why = $why })
            Write-Say "  keep   $($row.Branch) - $why"
            continue
        }

        if ($Pretend) {
            $removed.Add(@{ branch = $row.Branch; bead = $row.Bead; worktree = $row.Worktree; dryRun = $true })
            Write-Say "  would remove $($row.Branch)$(if ($row.Worktree) { " and $($row.Worktree)" })" 'Green'
            continue
        }

        $problems = [System.Collections.Generic.List[string]]::new()

        if ($row.Worktree -and (Test-Path -LiteralPath $row.Worktree)) {
            # dotnet's build server can hold handles in a worktree it built. Shut it down before
            # removing, or the remove fails on Windows with a file-in-use error that reads like a
            # permissions problem. Guarded by Get-Command: with $ErrorActionPreference = 'Stop', a
            # missing dotnet is a TERMINATING CommandNotFoundException, so an unguarded call would
            # abort the whole reap on a machine that has only the Android toolchain.
            if (Get-Command dotnet -ErrorAction SilentlyContinue) {
                & dotnet build-server shutdown *>&1 | Out-Null
            }
            $wtRemove = Invoke-Git -In $RepoRoot worktree remove $row.Worktree
            if ($wtRemove.ExitCode -ne 0) {
                $problems.Add("worktree remove failed: $($wtRemove.Output)")
            }
        }

        if ($problems.Count -eq 0) {
            # -d, not -D. It refuses a branch whose commits are not reachable from the current
            # head, which for a landed bead cannot happen - and if it somehow does, refusing is
            # the direction to fail in. -D would delete the only copy of unmerged work.
            $brDelete = Invoke-Git -In $RepoRoot branch -d $row.Branch
            if ($brDelete.ExitCode -ne 0) {
                $problems.Add("branch delete failed: $($brDelete.Output)")
            }
        }

        if ($problems.Count -gt 0) {
            $kept.Add(@{ branch = $row.Branch; bead = $row.Bead; disposition = 'landed'; why = ($problems -join '; ') })
            Write-Say "  keep   $($row.Branch) - $($problems -join '; ')" 'Yellow'
            continue
        }

        # The merge queue unsets ralphLaneState and ralphLane when it closes a bead, but the path
        # claim outlives both. It is already dead - a claim is live only on an open bead in a live
        # state - so this is tidying rather than correctness, and leaving it would make -Status
        # report claims for work that finished.
        Invoke-Bd update $row.Bead --unset-metadata ralphLanePaths | Out-Null

        $removed.Add(@{ branch = $row.Branch; bead = $row.Bead; worktree = $row.Worktree })
        Write-Say "  reaped $($row.Branch)" 'Green'
    }

    if (-not $Pretend) {
        # Clears registrations for lane directories somebody deleted by hand, which otherwise
        # make `git worktree add` refuse the same path next wave.
        Invoke-Git -In $RepoRoot worktree prune | Out-Null
    }

    if ($removed.Count -eq 0 -and $kept.Count -eq 0) { Write-Say '  Nothing to reap.' }
    return [pscustomobject]@{ Removed = $removed; Kept = $kept }
}

if ($PSCmdlet.ParameterSetName -ceq 'Reap') {
    $reaped = Invoke-ReapPhase -Pretend:$DryRun
    if ($Json) {
        Write-Output (@{
            schema      = 1
            result      = 'PASS'
            phase       = 'reap'
            dryRun      = [bool]$DryRun
            removed     = @($reaped.Removed)
            kept        = @($reaped.Kept)
            durationSec = [int]((Get-Date) - $started).TotalSeconds
        } | ConvertTo-Json -Compress -Depth 5)
    }
    exit 0
}

# ===========================================================================
# The drain: plan -> provision -> run -> land -> reap.
# ===========================================================================

if ($IntegrationBranch -ceq 'main' -or $IntegrationBranch -ceq 'master') {
    Stop-WithProblem 'preflight' "The integration copy is on '$IntegrationBranch'." `
        'The loop never works on main. Check out the integration branch (v2.5-board-drain) first.'
}

# ---------------------------------------------------------------------------
# Plan.
# ---------------------------------------------------------------------------

Write-Stage "Planning $Lanes lane(s)"

$clusterArgs = @('-Lanes', "$Lanes", '-Limit', "$Limit", '-Json')
if ($UseLabelHistory) { $clusterArgs += '-UseLabelHistory' }

$plan = Invoke-ChildScript -Script $ClusterScript -ScriptArgs $clusterArgs
if ($plan.ExitCode -ne 0 -or $null -eq $plan.Json) {
    Stop-WithProblem 'plan' "The planner failed (exit $($plan.ExitCode))." `
        "Run it on its own to see why: ./scripts/ralph-cluster.ps1 -Lanes $Lanes"
}

$planned = [System.Collections.Generic.List[object]]::new()
$deferredToNextWave = [System.Collections.Generic.List[object]]::new()

foreach ($bucket in @(Get-Prop $plan.Json 'lanes')) {
    $beads = @(Get-Prop $bucket 'beads')
    if ($beads.Count -eq 0) { continue }

    # One bead per lane per wave. A lane branch is named ralph/lane-<n>/<bead-id> and the merge
    # queue derives the bead FROM that name, so a lane cannot hold two beads without the queue
    # landing the second one under the first one's id. The rest of the bucket is the next wave's
    # work and is reported rather than dropped.
    $planned.Add([pscustomobject]@{
        Lane  = [int](Get-Prop $bucket 'lane')
        Bead  = [string](Get-Prop $beads[0] 'bead')
        Title = [string](Get-Prop $beads[0] 'title')
        Paths = @(Get-Prop $beads[0] 'paths')
    })
    foreach ($extra in @($beads | Select-Object -Skip 1)) {
        $deferredToNextWave.Add(@{
            lane = [int](Get-Prop $bucket 'lane')
            bead = [string](Get-Prop $extra 'bead')
            title = [string](Get-Prop $extra 'title')
        })
    }
}

foreach ($p in $planned) {
    Write-Say "  lane $($p.Lane)  $($p.Bead)  $($p.Title)" 'Green'
    if ($p.Paths.Count -eq 0) {
        Write-Say '           footprint unknown - nothing in the bead text resolved to a file' 'Yellow'
    }
    else {
        $shown = @($p.Paths | Select-Object -First 4)
        Write-Say "           claims $($p.Paths.Count): $($shown -join ', ')$(if ($p.Paths.Count -gt 4) { ', ...' })"
    }
}
foreach ($u in @(Get-Prop $plan.Json 'unscheduled')) {
    Write-Say "  not scheduled: $(Get-Prop $u 'bead') - $(Get-Prop $u 'why')" 'Yellow'
}
foreach ($d in $deferredToNextWave) {
    Write-Say "  next wave: $($d.bead) (lane $($d.lane) already has work this wave)" 'DarkGray'
}

if ($planned.Count -eq 0) {
    Write-Say "`n  Nothing to dispatch." 'Yellow'
    if ($Json) {
        Write-Output (@{
            schema = 1; result = 'NOOP'; phase = 'plan'; integrationBranch = $IntegrationBranch
            provisioned = @(); nextWave = @($deferredToNextWave)
            durationSec = [int]((Get-Date) - $started).TotalSeconds
        } | ConvertTo-Json -Compress -Depth 5)
    }
    exit 0
}

if ($PlanOnly) {
    Write-Say "`n  This is an estimate and it is advisory only - nothing here authorises anything." 'Cyan'
    Write-Say '  The merge queue catches real overlap at rebase time whatever this says.' 'Cyan'
    Write-Say '  Nothing was provisioned.'
    if ($Json) {
        Write-Output (@{
            schema = 1; result = 'PASS'; phase = 'plan'; integrationBranch = $IntegrationBranch
            planned = @($planned | ForEach-Object { @{ lane = $_.Lane; bead = $_.Bead; title = $_.Title; paths = @($_.Paths) } })
            unscheduled = @(Get-Prop $plan.Json 'unscheduled')
            nextWave = @($deferredToNextWave)
            durationSec = [int]((Get-Date) - $started).TotalSeconds
        } | ConvertTo-Json -Compress -Depth 6)
    }
    exit 0
}

# ---------------------------------------------------------------------------
# Provision.
# ---------------------------------------------------------------------------

# Lanes branch from the integration HEAD, so anything uncommitted here is invisible to every one
# of them - and then collides with all of them at landing, having never been reviewed or
# verified. Refusing costs one commit; not refusing costs a whole wave.
$dirty = (Invoke-Git -In $RepoRoot status --porcelain).Output
if ($dirty) {
    Stop-WithProblem 'provision' 'The integration tree has uncommitted changes.' `
        "Lanes branch from HEAD, so those changes would be missing from every lane and would then conflict with every landing. Commit or stash them first. `git -C `"$RepoRoot`" status` shows what."
}

if (-not (Test-Path -LiteralPath $ScratchDir)) {
    New-Item -ItemType Directory -Path $ScratchDir -Force | Out-Null
}

Write-Stage 'Provisioning'

$provisioned = [System.Collections.Generic.List[object]]::new()
$refused = [System.Collections.Generic.List[object]]::new()

# The planner numbers its buckets 1..N every time, because it is planning a wave in isolation and
# has no idea what is already running. Reusing a number whose lane directory is still occupied
# fails in the bootstrap - measured, on the second wave dispatched while the first was still live -
# and the bead gets rolled back with a message about a directory, which reads like a bug rather
# than like "that lane is busy". So the numbers are reassigned here, to whatever is actually free.
#
# Occupied means EITHER a branch under refs/heads/ralph/lane-<n>/ or a directory at <root>/lane-<n>.
# Both, because they go missing independently: a reaped lane leaves neither, a quarantined one
# leaves both, and a lane whose worktree someone deleted by hand leaves only the branch.
$occupied = [System.Collections.Generic.HashSet[int]]::new()
foreach ($row in (Get-LaneBoard)) {
    if ($row.Lane -gt 0) { $null = $occupied.Add($row.Lane) }
}
if (Test-Path -LiteralPath $LanesRoot) {
    foreach ($dir in @(Get-ChildItem -LiteralPath $LanesRoot -Directory -ErrorAction SilentlyContinue)) {
        if ($dir.Name -match '^lane-(\d+)$') { $null = $occupied.Add([int]$Matches[1]) }
    }
}

$nextLane = 1
foreach ($p in $planned) {
    while ($occupied.Contains($nextLane) -and $nextLane -lt 99) { $nextLane++ }
    if ($occupied.Contains($nextLane)) {
        # 99 is the bootstrap's own ceiling on a lane number, so there is nowhere left to put this.
        # Lane 0 is the marker for "not placed"; the provisioning loop below skips it.
        $refused.Add(@{ lane = 0; bead = $p.Bead; why = 'no free lane number below 100' })
        $p.Lane = 0
        continue
    }
    if ($p.Lane -ne $nextLane) {
        Write-Say "  $($p.Bead): lane $($p.Lane) is taken, using lane $nextLane instead." 'DarkGray'
    }
    $p.Lane = $nextLane
    $null = $occupied.Add($nextLane)
}

# Undoes everything provisioning did to a bead, so a failure leaves the board exactly as it was
# rather than holding a claim no lane is honouring.
function Reset-BeadAfterFailure {
    param([string]$BeadId, [string]$Note)
    # ralphReviewVerdict goes too: a verdict left over from a failed attempt would show against
    # the next lane to pick this bead up, and a stale PASS is worse than no verdict at all.
    Invoke-Bd update $BeadId --status open `
        --unset-metadata ralphLaneState --unset-metadata ralphLane --unset-metadata ralphLanePaths `
        --unset-metadata ralphReviewVerdict `
        --append-notes $Note | Out-Null
}

foreach ($p in $planned) {
    if ($p.Lane -le 0) { continue }   # no free lane number; already recorded as refused
    $branch = "ralph/lane-$($p.Lane)/$($p.Bead)"

    $exists = Invoke-Git -In $RepoRoot rev-parse --verify --quiet "refs/heads/$branch"
    if ($exists.ExitCode -eq 0) {
        $refused.Add(@{ lane = $p.Lane; bead = $p.Bead; why = "branch $branch already exists" })
        Write-Say "  lane $($p.Lane) skipped - $branch already exists. Reap or land it first." 'Yellow'
        continue
    }

    # bd's claim, not the path claim. It has to happen first: ralph-cluster.ps1 checks a new
    # claim against `bd list --status in_progress`, so a bead left 'open' holds a claim that the
    # next lane's collision check cannot see.
    $bdClaim = Invoke-Bd update $p.Bead --claim
    if ($bdClaim.ExitCode -ne 0) {
        $refused.Add(@{ lane = $p.Lane; bead = $p.Bead; why = "bd update --claim failed: $($bdClaim.Output)" })
        Write-Say "  lane $($p.Lane) skipped - could not claim $($p.Bead)." 'Yellow'
        continue
    }

    # Marked live BEFORE the path claim is recorded, so that a second lane provisioned moments
    # later sees the first one's claim as live. The planner already produced disjoint buckets, so
    # this is belt and braces - but the window is free to close and the cost of missing it is two
    # agents in one file.
    Invoke-Bd update $p.Bead --set-metadata "ralphLane=$($p.Lane)" --set-metadata 'ralphLaneState=working' | Out-Null

    if ($p.Paths.Count -gt 0) {
        # A real file, passed by path - the same rule the spec's constraint 4 states, and the
        # only way to hand an array to a child pwsh started with -File.
        $claimFile = Join-Path $ScratchDir "lane-$($p.Lane)-claim.txt"
        Set-Content -LiteralPath $claimFile -Value @($p.Paths) -Encoding utf8NoBOM

        $claim = Invoke-ChildScript -Script $ClusterScript -ScriptArgs @('-Claim', $p.Bead, '-PathsFile', $claimFile, '-Json')
        if ($claim.ExitCode -ne 0) {
            $why = if ($claim.Json) { [string](Get-Prop $claim.Json 'problem') } else { $claim.Output }
            Reset-BeadAfterFailure -BeadId $p.Bead -Note "Dispatcher could not claim this bead's paths: $why"
            $refused.Add(@{ lane = $p.Lane; bead = $p.Bead; why = "path claim refused: $why" })
            Write-Say "  lane $($p.Lane) skipped - $why" 'Yellow'
            continue
        }
    }
    else {
        Write-Warn "$($p.Bead) claims no paths - its footprint is unknown, so only layer 3 protects it."
    }

    $bootstrapArgs = @('-Lane', "$($p.Lane)", '-Bead', $p.Bead, '-LanesRoot', $LanesRoot, '-BaseBranch', $IntegrationBranch, '-Json')
    if ($SkipLaneVerify) { $bootstrapArgs += '-SkipVerify' }

    Write-Say "  lane $($p.Lane): provisioning $($p.Bead)$(if (-not $SkipLaneVerify) { ' (this pays one cold build)' })..."
    $boot = Invoke-ChildScript -Script $BootstrapScript -ScriptArgs $bootstrapArgs
    if ($boot.ExitCode -ne 0) {
        $why = if ($boot.Json) { "$(Get-Prop $boot.Json 'problem') $(Get-Prop $boot.Json 'whatToDo')".Trim() } else { $boot.Output }
        Reset-BeadAfterFailure -BeadId $p.Bead -Note "Dispatcher could not provision a lane for this bead. This is a provisioning failure, not a bead failure: $why"
        $refused.Add(@{ lane = $p.Lane; bead = $p.Bead; why = "provisioning failed: $why" })
        Write-Say "  lane $($p.Lane) FAILED to provision - $why" 'Red'
        continue
    }

    $provisioned.Add([pscustomobject]@{
        Lane     = $p.Lane
        Bead     = $p.Bead
        Title    = $p.Title
        Branch   = [string](Get-Prop $boot.Json 'branch')
        LaneRoot = [string](Get-Prop $boot.Json 'laneRoot')
        Verify   = [string](Get-Prop $boot.Json 'verify')
    })
    Write-Say "  lane $($p.Lane) ready at $(Get-Prop $boot.Json 'laneRoot')" 'Green'
}

if ($provisioned.Count -eq 0) {
    Write-Say "`n  No lanes were provisioned." 'Yellow'
    if ($Json) {
        Write-Output (@{
            schema = 1; result = 'FAIL'; phase = 'provision'; integrationBranch = $IntegrationBranch
            provisioned = @(); refused = @($refused); nextWave = @($deferredToNextWave)
            durationSec = [int]((Get-Date) - $started).TotalSeconds
        } | ConvertTo-Json -Compress -Depth 5)
    }
    exit 1
}

# ---------------------------------------------------------------------------
# Run.
#
# What a lane IS is deliberately undecided (spec section 10). Without -Launcher
# this phase prints the command and stops, which is the honest thing to do
# while the mechanism is open - the lanes exist, they build, they are claimed,
# and the operator starts them however they like.
# ---------------------------------------------------------------------------

Write-Stage 'Running'

$laneProcesses = [System.Collections.Generic.List[object]]::new()

if ($Launcher) {
    foreach ($lane in $provisioned) {
        # A program and an argument ARRAY. Never a composed command string: constraint 4 forbids
        # this script generating script text, and argv passing removes the quoting corruption
        # that shell strings have repeatedly caused in this repository.
        # The LANE's copy of the procedure, not the integration copy. An agent's workspace is its
        # own worktree, so a path pointing back here is at best unreadable to it. The file is
        # tracked and the lane branched from HEAD, so the two copies are identical by construction.
        $lanePrompt = Join-Path $lane.LaneRoot 'docs' 'ralph-board-drain.md'
        $launchArgs = @($lane.LaneRoot, "$($lane.Lane)", $lane.Bead, $lane.Branch, $lanePrompt)

        # Start-Process cannot execute a .ps1 - it is not an executable image on any platform - so
        # a script launcher is run through the interpreter already running. Still argv, still no
        # composed command string.
        $launchExe = $Launcher
        if ($Launcher -match '\.ps1$') {
            $launchExe = Get-PwshPath
            $launchArgs = @('-NoProfile', '-File', $Launcher) + $launchArgs
        }

        try {
            $proc = Start-Process -FilePath $launchExe -ArgumentList $launchArgs `
                -WorkingDirectory $lane.LaneRoot -PassThru
            $laneProcesses.Add([pscustomobject]@{ Lane = $lane.Lane; Bead = $lane.Bead; Process = $proc })
            Write-Say "  lane $($lane.Lane) started (pid $($proc.Id))" 'Green'
        }
        catch {
            $refused.Add(@{ lane = $lane.Lane; bead = $lane.Bead; why = "launcher failed: $($_.Exception.Message)" })
            Write-Say "  lane $($lane.Lane) launcher FAILED - $($_.Exception.Message)" 'Red'
        }
    }
}
else {
    Write-Say '  No -Launcher given, so the lanes are provisioned and idle. Start each one with:'
    foreach ($lane in $provisioned) {
        Write-Say ''
        Write-Say "    cd `"$($lane.LaneRoot)`"" 'Green'
        Write-Say "    # then run the loop there, on $($lane.Bead):" 'DarkGray'
        Write-Say "    #   Read docs/ralph-board-drain.md and follow it exactly, in LANE MODE." 'DarkGray'
    }
    Write-Say ''
    Write-Say '  Each lane reads .ralph/lane.env for its lane number, bead and BEADS_DIR.' 'DarkGray'
    Write-Say '  When a lane finishes it sets ralphLaneState=ready-to-land, and then:' 'DarkGray'
    Write-Say '    ./scripts/ralph-dispatch.ps1 -Land' 'Green'
    Write-Say '    ./scripts/ralph-dispatch.ps1 -Reap' 'Green'
}

$landResult = $null
$reapResult = $null
$landFailed = $false

if ($Launcher -and -not $NoWait -and $laneProcesses.Count -gt 0) {
    Write-Say "`n  Waiting for $($laneProcesses.Count) lane(s)..."
    foreach ($lp in $laneProcesses) {
        $lp.Process.WaitForExit()
        $code = $lp.Process.ExitCode
        Write-Say "  lane $($lp.Lane) exited $code" $(if ($code -eq 0) { 'Green' } else { 'Yellow' })
    }

    # Landing is not conditional on the lanes exiting 0. A lane that failed leaves its bead in a
    # state the queue will not land, and the queue re-derives every candidate from bd anyway - so
    # running it is always safe and skipping it would strand the lanes that did succeed.
    $land = Invoke-LandPhase
    $landResult = $land.Json
    if ($land.ExitCode -ne 0) { $landFailed = $true }
    $reapResult = (Invoke-ReapPhase)
}

# ---------------------------------------------------------------------------
# Summary.
# ---------------------------------------------------------------------------

# A quarantined landing is a real failure and has to reach the caller's exit code, or a scripted
# wave reads 0 and starts the next one on top of an integration branch that just rejected work.
$anythingFailed = ($refused.Count -gt 0) -or $landFailed

if ($Json) {
    Write-Output (@{
        schema            = 1
        result            = if ($anythingFailed) { 'PARTIAL' } else { 'PASS' }
        phase             = if ($landResult) { 'reap' } else { 'run' }
        integrationBranch = $IntegrationBranch
        lanesRoot         = $LanesRoot
        provisioned       = @($provisioned | ForEach-Object {
            @{ lane = $_.Lane; bead = $_.Bead; branch = $_.Branch; laneRoot = $_.LaneRoot; verify = $_.Verify }
        })
        launched          = @($laneProcesses | ForEach-Object { @{ lane = $_.Lane; bead = $_.Bead; pid = $_.Process.Id } })
        refused           = @($refused)
        nextWave          = @($deferredToNextWave)
        land              = $landResult
        reaped            = if ($reapResult) { @($reapResult.Removed) } else { @() }
        durationSec       = [int]((Get-Date) - $started).TotalSeconds
    } | ConvertTo-Json -Compress -Depth 8)
}
else {
    Write-Host "`nDispatch finished in $([int]((Get-Date) - $started).TotalSeconds)s." -ForegroundColor Cyan
    Write-Host "  provisioned : $($provisioned.Count)" -ForegroundColor Green
    if ($refused.Count -gt 0) {
        Write-Host "  refused     : $($refused.Count)" -ForegroundColor Yellow
        foreach ($r in $refused) { Write-Host "      lane $($r.lane) $($r.bead) - $($r.why)" -ForegroundColor Yellow }
    }
    if ($landFailed) {
        Write-Host '  land        : something did not land. The merge queue printed why, above.' -ForegroundColor Yellow
    }
    Write-Host '  Nothing about this run was written down anywhere: check it with -Status.' -ForegroundColor DarkGray
}

exit $(if ($anythingFailed) { 1 } else { 0 })

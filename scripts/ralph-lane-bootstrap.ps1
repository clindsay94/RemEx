#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Provisions one parallel board-drain lane - a git worktree that can actually build.

.DESCRIPTION
    A lane is a sibling git worktree where one agent works on one bead, so several beads can
    be drained at once without the agents editing each other's files. This script creates one
    and then PROVES it works before anybody is given work in it.

    That last part is the whole point. `git worktree add` populates a working copy from the git
    index, so every gitignored path is missing from a fresh lane, and the resulting failures
    point at the wrong thing - a missing local.properties reads as a broken Android SDK, not as
    a half-provisioned worktree. Discovering that after an agent has spent twenty minutes on a
    bead means the failure gets blamed on the bead. Discovering it here means it gets blamed on
    provisioning, which is what it is.

    So the script finishes by running the lane's own ./scripts/verify.ps1 once. A lane that
    cannot build clean is a provisioning bug, and this is where it should surface.

    What it does, in order:

      1. Create the worktree at a path OUTSIDE the repository root, on branch
         ralph/lane-<n>/<bead-id>.
      2. Copy every entry in scripts/ralph-lane-manifest.txt, failing loudly if a required one
         is missing.
      3. Write the lane environment file, including BEADS_DIR (so the lane reaches the one
         shared board rather than forking it) and MSBUILDDISABLENODEREUSE.
      4. Assert that the lane's build output resolves under the lane root.
      5. Run ./scripts/verify.ps1 -Scope dotnet in the lane.

    Nothing here deletes anything. If the lane path is already in use the script stops and tells
    you the command to run; tearing lanes down is the dispatcher's reap phase, not this script's
    job, because a failed lane's branch is the evidence for its reopened bead.

    Designed in docs/SPEC-parallel-board-drain-dispatcher.md sections 2, 3 and 4 (RemEx-56fu.5.2).

.PARAMETER Lane
    The lane number. Becomes the directory name (lane-3) and part of the branch name. Lane
    numbers make concurrent branches unambiguous in `git branch`.

.PARAMETER Bead
    The bead id this lane will work on, e.g. RemEx-56fu.5.2. Becomes the rest of the branch
    name, so a branch is still self-describing weeks later.

.PARAMETER LanesRoot
    Where lanes live. Defaults to a sibling of the repository - Z:\RemEx gives Z:\RemEx.lanes.
    This must be outside the repository root and the script refuses if it is not: verify.ps1
    fingerprints untracked-but-not-ignored files, so a worktree nested inside the repo would
    show up in every other lane's fingerprint and the receipts would stop meaning anything.

.PARAMETER BaseBranch
    What to branch the lane from. Defaults to the branch the integration copy is currently on,
    which is the integration branch by definition.

.PARAMETER SkipVerify
    Skip the build-and-test proof at the end. Faster, but you are giving up the guarantee this
    script exists to provide, and an agent in this lane may hit a provisioning failure that
    looks like a code failure. For debugging provisioning itself, never for a lane about to be
    given real work.

.PARAMETER Json
    Print one line of JSON and nothing else. For the dispatcher.

.EXAMPLE
    ./scripts/ralph-lane-bootstrap.ps1 -Lane 1 -Bead RemEx-56fu.5.3
    Provision lane 1 for that bead, with friendly progress output.

.EXAMPLE
    ./scripts/ralph-lane-bootstrap.ps1 -Lane 2 -Bead RemEx-abcd -Json
    Same, printing a single parseable line. This is what the dispatcher calls.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 99)]
    [int]$Lane,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Bead,

    [Parameter(Mandatory = $false)]
    [string]$LanesRoot,

    [Parameter(Mandatory = $false)]
    [string]$BaseBranch,

    [Parameter(Mandatory = $false)]
    [switch]$SkipVerify,

    [Parameter(Mandatory = $false)]
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ManifestPath = Join-Path $PSScriptRoot 'ralph-lane-manifest.txt'
$started = Get-Date

# ---------------------------------------------------------------------------
# Output helpers. Everything human-facing goes through these so that -Json can
# silence the lot and leave a single parseable line on stdout. Same shape as
# scripts/verify.ps1 so the two read alike in a terminal.
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
            schema = 1; result = 'FAIL'; lane = $Lane; bead = $Bead
            stage = $Stage; problem = $Text; whatToDo = $WhatToDo
        } | ConvertTo-Json -Compress)
    }
    else {
        Write-Host "`n  Problem: $Text" -ForegroundColor Red
        if ($WhatToDo) { Write-Host "  What to do: $WhatToDo" -ForegroundColor Yellow }
    }
    exit 1
}

# ---------------------------------------------------------------------------
# Path containment.
#
# Deliberately CASE-INSENSITIVE, which is the opposite of the rule for git
# pathspecs in CLAUDE.md, and for a reason. This is a safety check asking "is
# the lane inside the repo?", where the two possible mistakes are not equal.
# Refusing a lane that only looks nested costs one confused operator. Accepting
# one that really is nested silently poisons every lane's verify fingerprint,
# and nothing downstream would ever report it. So the check errs toward refusing.
# ---------------------------------------------------------------------------

function Test-PathIsUnder {
    param([string]$Child, [string]$Parent)

    $c = ([System.IO.Path]::GetFullPath($Child) -replace '\\', '/').TrimEnd('/')
    $p = ([System.IO.Path]::GetFullPath($Parent) -replace '\\', '/').TrimEnd('/')

    if ([string]::Equals($c, $p, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    return $c.StartsWith("$p/", [System.StringComparison]::OrdinalIgnoreCase)
}

# ---------------------------------------------------------------------------
# The manifest. One path per line, '?' prefix means optional, '#' starts a
# comment. Kept as a file rather than an array in this script so the copy set
# is reviewable in a diff - see the header of ralph-lane-manifest.txt.
# ---------------------------------------------------------------------------

function Read-LaneManifest {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Stop-WithProblem 'manifest' "The lane manifest is missing: $Path" `
            "It should be committed alongside this script. Restore it with: git checkout -- scripts/ralph-lane-manifest.txt"
    }

    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($raw in Get-Content -LiteralPath $Path) {
        # Comments are stripped at the first '#'. No path in this repo contains one,
        # and a manifest that needed such a path would deserve a real parser.
        $line = $raw
        $hash = $line.IndexOf('#')
        if ($hash -ge 0) { $line = $line.Substring(0, $hash) }
        $line = $line.Trim()
        if (-not $line) { continue }

        $optional = $false
        if ($line.StartsWith('?')) {
            $optional = $true
            $line = $line.Substring(1).Trim()
        }
        if (-not $line) { continue }

        $entries.Add([pscustomobject]@{ Path = $line; Optional = $optional })
    }
    return , $entries.ToArray()
}

# ---------------------------------------------------------------------------
# Work out where this lane goes, and refuse the layouts that would break things.
# ---------------------------------------------------------------------------

if (-not $LanesRoot) {
    # Z:\RemEx -> Z:\RemEx.lanes. Derived from the repo root rather than hardcoded,
    # so the same script gives ~/RemEx.lanes on Linux.
    $LanesRoot = Join-Path (Split-Path -Parent $RepoRoot) ((Split-Path -Leaf $RepoRoot) + '.lanes')
}
elseif (-not [System.IO.Path]::IsPathRooted($LanesRoot)) {
    # Resolve a relative -LanesRoot against the repo, not against wherever the caller
    # happened to be standing.
    $LanesRoot = Join-Path $RepoRoot $LanesRoot
}

$LanesRoot = [System.IO.Path]::GetFullPath($LanesRoot)
$LanePath = Join-Path $LanesRoot "lane-$Lane"
$Branch = "ralph/lane-$Lane/$Bead"

Write-Stage "Provisioning lane $Lane for $Bead"
Write-Say "  integration copy : $RepoRoot"
Write-Say "  lane             : $LanePath"
Write-Say "  branch           : $Branch"

if (Test-PathIsUnder -Child $LanePath -Parent $RepoRoot) {
    Stop-WithProblem 'layout' "The lane path is inside the repository: $LanePath" `
        "Lanes must sit outside the repo. verify.ps1 fingerprints untracked-but-not-ignored files, so a nested worktree would appear in every other lane's fingerprint and the receipts would stop meaning what they say. Pass -LanesRoot with a path outside $RepoRoot."
}

# ---------------------------------------------------------------------------
# Preflight. Everything that can be checked without changing anything.
# ---------------------------------------------------------------------------

Write-Stage "Checking prerequisites"

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Stop-WithProblem 'preflight' "git is not on PATH." "Install git, or open a terminal where 'git --version' works."
}

git -C $RepoRoot rev-parse --git-dir *> $null
if ($LASTEXITCODE -ne 0) {
    Stop-WithProblem 'preflight' "$RepoRoot is not a git working copy." "Run this script from a checkout of the RemEx repository."
}

if (-not $BaseBranch) {
    $BaseBranch = (git -C $RepoRoot rev-parse --abbrev-ref HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $BaseBranch -or $BaseBranch -ceq 'HEAD') {
        Stop-WithProblem 'preflight' "Could not work out which branch the integration copy is on." `
            "Check out a branch in $RepoRoot, or pass -BaseBranch explicitly."
    }
}
Write-Say "  base branch      : $BaseBranch"

git -C $RepoRoot check-ref-format --branch $Branch *> $null
if ($LASTEXITCODE -ne 0) {
    Stop-WithProblem 'preflight' "'$Branch' is not a valid git branch name." `
        "The bead id is used verbatim in the branch name. Pass a -Bead value without spaces, '~', '^', ':' or '..'."
}

git -C $RepoRoot show-ref --verify --quiet "refs/heads/$Branch"
if ($LASTEXITCODE -eq 0) {
    Stop-WithProblem 'preflight' "Branch '$Branch' already exists." `
        "A lane branch is kept after a failure on purpose - it is the evidence for the reopened bead. Inspect it, then either finish it or delete it with: git branch -D $Branch"
}

if (Test-Path -LiteralPath $LanePath) {
    Stop-WithProblem 'preflight' "Something is already at $LanePath." `
        "This script never deletes a lane, because a lane can hold uncommitted work. Inspect it, then remove it with: git -C `"$RepoRoot`" worktree remove `"$LanePath`""
}

# A worktree is created from the branch TIP, so uncommitted work in the integration copy
# is not in the lane. Worth saying out loud - it is a genuinely surprising way to end up
# debugging a fix that "isn't there".
$dirty = @(git -C $RepoRoot status --porcelain)
if ($dirty.Count -gt 0) {
    Write-Warn "the integration copy has $($dirty.Count) uncommitted change(s). The lane starts from the last commit on $BaseBranch, so those changes will NOT be in it."
}

$manifest = Read-LaneManifest -Path $ManifestPath
Write-Say "  manifest         : $($manifest.Count) entries"

# ---------------------------------------------------------------------------
# Step 0 - safe.directory.
#
# MEASURED, and not predicted by the original design. Z: records no ownership,
# so git refuses any new worktree path there with "detected dubious ownership"
# and verify.ps1 dies at the fingerprint stage. git stores these with forward
# slashes, so compare in that form or every run appends a duplicate.
# ---------------------------------------------------------------------------

Write-Stage "Marking the lane as a safe git directory"

$laneGitPath = $LanePath -replace '\\', '/'
$safeDirs = @(git config --global --get-all safe.directory 2>$null)
$alreadySafe = $safeDirs | Where-Object { [string]::Equals($_, $laneGitPath, [System.StringComparison]::OrdinalIgnoreCase) }

if ($alreadySafe) {
    Write-Say "  Already listed."
}
else {
    git config --global --add safe.directory $laneGitPath
    if ($LASTEXITCODE -ne 0) {
        Stop-WithProblem 'safe-directory' "Could not add $laneGitPath to git's safe.directory list." `
            "Run it by hand: git config --global --add safe.directory `"$laneGitPath`""
    }
    Write-Say "  Added $laneGitPath"
}

# ---------------------------------------------------------------------------
# Step 1 - the worktree.
# ---------------------------------------------------------------------------

Write-Stage "Creating the worktree"

if (-not (Test-Path -LiteralPath $LanesRoot)) {
    New-Item -ItemType Directory -Path $LanesRoot -Force | Out-Null
    Write-Say "  Created $LanesRoot"
}

$addOutput = @(git -C $RepoRoot worktree add -b $Branch $LanePath $BaseBranch 2>&1)
if ($LASTEXITCODE -ne 0) {
    Stop-WithProblem 'worktree' "git worktree add failed: $($addOutput -join ' ')" `
        "Check that '$BaseBranch' exists and that $LanesRoot is writable."
}
Write-Say "  Worktree at $LanePath on $Branch" 'Green'

# ---------------------------------------------------------------------------
# Step 2 - the copy manifest.
#
# A missing REQUIRED entry is fatal and the lane is left in place rather than
# cleaned up: a half-provisioned lane is inspectable, and this script does not
# delete working copies.
# ---------------------------------------------------------------------------

Write-Stage "Copying the files a worktree does not get"

$copied = 0
$skipped = 0
foreach ($entry in $manifest) {
    $source = Join-Path $RepoRoot $entry.Path
    $dest = Join-Path $LanePath $entry.Path

    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        if ($entry.Optional) {
            Write-Say "  skip  $($entry.Path) (optional, not present here)"
            $skipped++
            continue
        }
        Stop-WithProblem 'manifest-copy' "Required file '$($entry.Path)' does not exist in $RepoRoot." `
            "The lane cannot build without it. Create it in the integration copy first, then remove the lane with: git -C `"$RepoRoot`" worktree remove `"$LanePath`" and run this script again."
    }

    $destDir = Split-Path -Parent $dest
    if ($destDir -and -not (Test-Path -LiteralPath $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }
    Copy-Item -LiteralPath $source -Destination $dest -Force
    Write-Say "  copy  $($entry.Path)"
    $copied++
}
Write-Say "  $copied copied, $skipped skipped." 'Green'

# ---------------------------------------------------------------------------
# Step 3 - the lane environment file.
#
# Lives in the lane's .ralph/, which is gitignored. That placement is
# load-bearing rather than tidy: verify.ps1's fingerprint covers tracked files
# PLUS untracked-not-ignored ones, so an env file written anywhere else would
# change the fingerprint of the build being used to prove the lane works.
# ---------------------------------------------------------------------------

Write-Stage "Writing the lane environment file"

$laneRalphDir = Join-Path $LanePath '.ralph'
if (-not (Test-Path -LiteralPath $laneRalphDir)) {
    New-Item -ItemType Directory -Path $laneRalphDir -Force | Out-Null
}
$envFile = Join-Path $laneRalphDir 'lane.env'
$beadsDir = Join-Path $RepoRoot '.beads'

# Plain KEY=VALUE, no quoting and no interpolation, so both shells can read it:
#   pwsh   Get-Content lane.env | ... (or the dispatcher's own parser)
#   bash   set -a; . .ralph/lane.env; set +a
$laneEnv = [ordered]@{
    # The single shared board. Copying .beads/ into a lane would fork it, and two lanes
    # would claim the same bead into two databases that never see each other. bd resolves
    # its workspace by walking UP from the current directory, and a lane sits outside the
    # repo, so without this a lane finds no board at all.
    BEADS_DIR                = $beadsDir

    # MSBuild's persistent worker nodes are keyed by toolset, not by working copy, so a
    # node started for one lane can attach to another's tree. Cheap insurance against a
    # class of bug that is close to undiagnosable after the fact.
    MSBUILDDISABLENODEREUSE  = '1'

    RALPH_LANE               = $Lane
    RALPH_LANE_BRANCH        = $Branch
    RALPH_LANE_ROOT          = $LanePath
    RALPH_LANE_BEAD          = $Bead
    RALPH_INTEGRATION_ROOT   = $RepoRoot
}

$envLines = [System.Collections.Generic.List[string]]::new()
$envLines.Add('# Generated by scripts/ralph-lane-bootstrap.ps1. Do not edit by hand -')
$envLines.Add('# re-provision the lane instead. Format is KEY=VALUE, one per line.')
$envLines.Add("# Lane $Lane, bead $Bead, created $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')).")
$envLines.Add('')
foreach ($k in $laneEnv.Keys) { $envLines.Add("$k=$($laneEnv[$k])") }

Set-Content -LiteralPath $envFile -Value $envLines -Encoding utf8NoBOM
Write-Say "  $envFile"
Write-Say "  BEADS_DIR=$beadsDir"

# ---------------------------------------------------------------------------
# Step 4 - assert the build output really is per-lane.
#
# Directory.Build.props sets UseArtifactsOutput with no explicit ArtifactsPath.
# MSBuild resolves the default relative to the directory holding the props file,
# and every worktree has its own tracked copy of that file at its own root, so
# each lane gets <lane>/artifacts for free. This assertion is not checking a
# thing that might be broken today - it is a tripwire, so that adding an explicit
# ArtifactsPath later cannot silently make every lane write to one shared folder.
# ---------------------------------------------------------------------------

Write-Stage "Checking the lane builds into its own artifacts folder"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Stop-WithProblem 'artifacts-path' "dotnet is not on PATH." "Install the .NET SDK, or open a terminal where 'dotnet --version' works."
}

$probeProject = Join-Path $LanePath 'remex.core' 'remex.core.csproj'
$probeOutput = @(dotnet msbuild $probeProject -getProperty:ArtifactsPath -nologo 2>&1)
if ($LASTEXITCODE -ne 0) {
    Stop-WithProblem 'artifacts-path' "Could not read ArtifactsPath from $probeProject : $($probeOutput -join ' ')" `
        "Check that the .NET SDK is new enough to support 'dotnet msbuild -getProperty' (8.0 or later)."
}

$artifactsPath = ($probeOutput | Where-Object { $_ } | Select-Object -Last 1).Trim()
if (-not $artifactsPath) {
    Stop-WithProblem 'artifacts-path' "ArtifactsPath came back empty for $probeProject." `
        "Check that UseArtifactsOutput is still set in Directory.Build.props."
}

if (-not (Test-PathIsUnder -Child $artifactsPath -Parent $LanePath)) {
    Stop-WithProblem 'artifacts-path' "This lane would build into $artifactsPath, which is outside the lane at $LanePath." `
        "Lanes must not share build output - that is how stale-build defects (RemEx-t0f3, RemEx-n3z6, RemEx-u5q0) get reintroduced. Something now sets an explicit ArtifactsPath; make it lane-relative before running lanes in parallel."
}
Write-Say "  $artifactsPath" 'Green'

# ---------------------------------------------------------------------------
# Step 5 - prove it builds, before an agent is given work here.
# ---------------------------------------------------------------------------

$verifyResult = 'SKIPPED'
$verifySeconds = 0

if ($SkipVerify) {
    Write-Stage "Skipping the build check (-SkipVerify)"
    Write-Warn "this lane is NOT proven. If an agent hits a build failure here, suspect provisioning before suspecting the bead."
}
else {
    Write-Stage "Proving the lane builds (this pays one cold build, on purpose)"
    Write-Say "  Running verify.ps1 -Scope dotnet in the lane. Expect several minutes."

    $verifyScript = Join-Path $LanePath 'scripts' 'verify.ps1'
    if (-not (Test-Path -LiteralPath $verifyScript -PathType Leaf)) {
        Stop-WithProblem 'verify' "The lane has no scripts/verify.ps1." `
            "That file is tracked, so its absence means the worktree did not populate correctly. Remove the lane and try again: git -C `"$RepoRoot`" worktree remove `"$LanePath`""
    }

    # Run it in a child pwsh so the exit code is unambiguous and the lane's environment
    # cannot leak back into this process on a normal (non -File) invocation.
    $pwshExe = (Get-Process -Id $PID).Path
    if (-not $pwshExe) { $pwshExe = 'pwsh' }

    $savedEnv = @{}
    foreach ($k in $laneEnv.Keys) { $savedEnv[$k] = [Environment]::GetEnvironmentVariable($k) }
    $verifyStarted = Get-Date
    try {
        foreach ($k in $laneEnv.Keys) { Set-Item -Path "Env:$k" -Value ([string]$laneEnv[$k]) }

        if ($Json) {
            & $pwshExe -NoProfile -File $verifyScript -Scope dotnet -Json | Out-Null
        }
        else {
            & $pwshExe -NoProfile -File $verifyScript -Scope dotnet
        }
        $verifyExit = $LASTEXITCODE
    }
    finally {
        foreach ($k in $savedEnv.Keys) {
            if ($null -eq $savedEnv[$k]) { Remove-Item -Path "Env:$k" -ErrorAction SilentlyContinue }
            else { Set-Item -Path "Env:$k" -Value $savedEnv[$k] }
        }
    }
    $verifySeconds = [int]((Get-Date) - $verifyStarted).TotalSeconds

    if ($verifyExit -ne 0) {
        $verifyResult = 'FAIL'
        Stop-WithProblem 'verify' "The lane does not build clean (verify.ps1 exited $verifyExit after ${verifySeconds}s)." `
            "This is a provisioning bug, not a bead. Read the failure above, then check whether something gitignored is missing from scripts/ralph-lane-manifest.txt. Do NOT give an agent work in this lane until it passes. The lane is left at $LanePath for inspection."
    }
    $verifyResult = 'PASS'
    Write-Say "  Lane verified in ${verifySeconds}s." 'Green'
}

# ---------------------------------------------------------------------------
# Done.
# ---------------------------------------------------------------------------

$totalSeconds = [int]((Get-Date) - $started).TotalSeconds

if ($Json) {
    Write-Output (@{
        schema        = 1
        result        = 'OK'
        lane          = $Lane
        bead          = $Bead
        branch        = $Branch
        laneRoot      = $LanePath
        baseBranch    = $BaseBranch
        envFile       = $envFile
        beadsDir      = $beadsDir
        artifactsPath = $artifactsPath
        filesCopied   = $copied
        filesSkipped  = $skipped
        verify        = $verifyResult
        verifySeconds = $verifySeconds
        durationSec   = $totalSeconds
        timestampUtc  = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    } | ConvertTo-Json -Compress)
}
else {
    Write-Host "`nLane $Lane is ready." -ForegroundColor Green
    Write-Host "  Work in : $LanePath"
    Write-Host "  Branch  : $Branch"
    Write-Host "  Board   : shared via BEADS_DIR - claims and closes land on the one real board."
    if ($verifyResult -ceq 'SKIPPED') {
        Write-Host "  Build   : NOT CHECKED (-SkipVerify)." -ForegroundColor Yellow
    }
    else {
        Write-Host "  Build   : verified clean in ${verifySeconds}s."
    }
    Write-Host "`n  When the lane is finished with, remove it from the integration copy:"
    Write-Host "    git -C `"$RepoRoot`" worktree remove `"$LanePath`""
    Write-Host "  Keep the branch if the bead failed - it is the evidence." -ForegroundColor Gray
}

exit 0

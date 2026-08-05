#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Works out which beads can safely be drained in parallel, and holds the lane claims.

.DESCRIPTION
    Layers 1 and 2 of docs/SPEC-parallel-board-drain-dispatcher.md section 5. Layer 3 - git at
    merge time - is scripts/ralph-merge-queue.ps1 and is the only layer that is trusted.

    LAYER 1, ESTIMATE. Guesses which files each ready bead will touch, so that lanes are less
    likely to collide. The guess WILL be wrong sometimes: a bead records intent, not a file set,
    and an agent discovering it must edit something nobody anticipated is normal and correct.
    So the estimate is used only to order and cluster work. It never authorises anything, and a
    bad estimate costs throughput, never correctness.

    Four sources, each reported separately in the output so a thin estimate is visible rather
    than silently thin:

      text     paths written literally in the bead's title, description and acceptance
               criteria, kept only if they exist in the repository or their parent directory
               does. The validation matters - real bead text contains things like
               "Windows/PowerShell" and "codex/openai" that look exactly like paths.
      grep     identifiers in the bead text, resolved to files with git grep. This is what
               carries beads that name a symbol and no path at all, which is most of the
               interesting ones. An identifier matching more files than -MaxGrepFiles is too
               generic to say anything and is dropped.
      gitnexus impact analysis on the same identifiers - only when the index is FRESH. A stale
               graph describes code that is no longer there, and a confidently wrong estimate
               is worse than an admitted gap, so this source reports itself as skipped instead.
      labels   files historically co-changed with closed beads carrying the same labels.
               OFF by default, because it was measured on this repo and yields almost nothing:
               nine closed 'tooling' beads produced ONE distinct file between them, since most
               closed beads have no bead-id-tagged commit in reach. Turn it on with
               -UseLabelHistory if the history ever gets richer; the plan reports its yield.

    Some files are excluded from every estimate, and this is load-bearing rather than tidiness.
    docs/CHANGELOG.md is touched by EVERY bead - the merge queue refuses to land one that is not
    - so counting it as overlap would make every bead collide with every other and nothing would
    ever be parallelised. Same for the loop journal. Both are merge=union in .gitattributes,
    which is what makes ignoring them safe.

    LAYER 2, CLAIMS. A lane records the paths it intends to touch on its own bead, under the
    ralphLanePaths metadata key, and the planner refuses to schedule a bead whose estimate
    intersects a claim some other lane is currently holding. bd is already the one shared mutable
    store with a server mediating access, so this introduces no new coordination primitive - and
    deliberately is NOT a tracked file, which would itself become the most contended file in the
    repository.

.PARAMETER Lanes
    How many lanes to plan for. Defaults to 3, the count Phase 0 measured at 2.89x serial
    throughput.

.PARAMETER Bead
    Consider only these beads. Repeatable. Default: the whole ready queue.

.PARAMETER Limit
    How many ready beads to consider. The ready queue is long and the tail is not going to be
    scheduled into a handful of lanes anyway.

.PARAMETER MaxGrepFiles
    An identifier that appears in more files than this is treated as too generic to be evidence
    and contributes nothing. Raising it makes estimates broader, which produces false collisions;
    a false collision costs a whole wave of throughput, while a missed one costs one rebase
    conflict that layer 3 already handles safely. So err low.

.PARAMETER UseLabelHistory
    Include the label co-change source. See the note above on why it is off by default.

.PARAMETER Claim
    Record or amend the path claim for this bead, from -Paths. Refuses, without writing, if the
    paths intersect a claim another lane is holding.

.PARAMETER Release
    Clear the path claim on this bead.

.PARAMETER Paths
    The paths to claim. Used with -Claim.

.PARAMETER Json
    Print one line of JSON and nothing else. For the dispatcher.

.EXAMPLE
    ./scripts/ralph-cluster.ps1
    Plan three lanes from the ready queue and print the clustering. Changes nothing.

.EXAMPLE
    ./scripts/ralph-cluster.ps1 -Lanes 4 -Json
    The same, as one line of JSON, for the dispatcher to consume.

.EXAMPLE
    ./scripts/ralph-cluster.ps1 -Claim RemEx-abcd -Paths remex.core/Foo.cs,remex.core/Bar.cs
    Record what a lane intends to touch, refusing if another live lane already claims any of it.
#>
[CmdletBinding(DefaultParameterSetName = 'Plan')]
param(
    [Parameter(ParameterSetName = 'Plan')]
    [ValidateRange(1, 16)]
    [int]$Lanes = 3,

    [Parameter(ParameterSetName = 'Plan')]
    [string[]]$Bead,

    [Parameter(ParameterSetName = 'Plan')]
    [ValidateRange(1, 500)]
    [int]$Limit = 30,

    [Parameter(ParameterSetName = 'Plan')]
    [ValidateRange(1, 200)]
    [int]$MaxGrepFiles = 12,

    [Parameter(ParameterSetName = 'Plan')]
    [switch]$UseLabelHistory,

    [Parameter(ParameterSetName = 'Claim', Mandatory = $true)]
    [string]$Claim,

    [Parameter(ParameterSetName = 'Claim', Mandatory = $true)]
    [string[]]$Paths,

    [Parameter(ParameterSetName = 'Release', Mandatory = $true)]
    [string]$Release,

    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Same reasoning as scripts/ralph-merge-queue.ps1: this script runs commands whose failure is a
# normal answer (git grep finding nothing exits 1), and a machine that has flipped this
# preference would turn every one of those into a thrown error.
if (Test-Path 'variable:PSNativeCommandUseErrorActionPreference') {
    $PSNativeCommandUseErrorActionPreference = $false
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
$started = Get-Date

# Touched by every bead by construction, so counting them as overlap would mean no two beads are
# ever schedulable together. Both are merge=union in .gitattributes, which is what lets the
# merge queue land concurrent edits to them without a conflict - so ignoring them here is safe,
# not optimistic.
$AlwaysExcluded = @(
    'docs/CHANGELOG.md'
    'docs/ralph-state.jsonl'
    '.token-savior-cache.json'
)

# States in which a lane is actually holding its claim. A returned or quarantined bead still has
# a branch, but no agent is working it, so its paths are free for someone else to take on.
$LiveStates = @('working', 'ready-to-land')

# ---------------------------------------------------------------------------
# Output helpers, matching scripts/verify.ps1 and the other ralph-* scripts.
# ---------------------------------------------------------------------------

function Write-Say {
    param([string]$Text, [string]$Colour = 'Gray')
    if (-not $Json) { Write-Host $Text -ForegroundColor $Colour }
}

function Write-Stage {
    param([string]$Text)
    if (-not $Json) { Write-Host "`n$Text" -ForegroundColor Cyan }
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
# Shell helpers. Both are SIMPLE functions on purpose - adding [Parameter()]
# would make them advanced, which silently grants PowerShell's common
# parameters, and short flags like -d then bind to -Debug instead of reaching
# the command. That cost a real bug in ralph-merge-queue.ps1; see the note
# there.
# ---------------------------------------------------------------------------

function Invoke-Git {
    $raw = & git -C $RepoRoot @args 2>&1
    $code = $LASTEXITCODE
    $text = (@($raw) | ForEach-Object { $_.ToString() }) -join "`n"
    return [pscustomobject]@{ ExitCode = $code; Output = $text.Trim() }
}

function Invoke-Bd {
    $raw = & bd --directory $RepoRoot @args 2>&1
    $code = $LASTEXITCODE
    $text = (@($raw) | ForEach-Object { $_.ToString() }) -join "`n"
    return [pscustomobject]@{ ExitCode = $code; Output = $text.Trim() }
}

function Get-Prop {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    if ($Object.PSObject.Properties.Name -contains $Name) { return $Object.$Name }
    return $null
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

# bd stores a scalar metadata value as the parsed JSON scalar but keeps a JSON array as its
# literal string, so a claim comes back either way depending on how it was written. Read through
# this rather than assuming one shape.
function Get-ClaimedPaths {
    param($Issue)
    $raw = Get-Prop (Get-Prop $Issue 'metadata') 'ralphLanePaths'
    if (-not $raw) { return @() }
    if ($raw -is [array]) { return @($raw) }
    try { return @(($raw | ConvertFrom-Json)) } catch { return @($raw) }
}

function Test-ClaimIsLive {
    param($Issue)
    if ((Get-Prop $Issue 'status') -ceq 'closed') { return $false }
    $state = Get-Prop (Get-Prop $Issue 'metadata') 'ralphLaneState'
    return ($LiveStates -ccontains $state)
}

# Two path sets intersect if they share a file OR if one claims a directory containing the
# other's file. Compared case-INSENSITIVELY on purpose, which is the opposite of the rule for git
# pathspecs in CLAUDE.md: here the two mistakes are not equal. Declaring a false overlap costs
# one bead's throughput on one wave; missing a real one puts two agents in the same file.
#
# The parameters are NOT called $A and $B, and the loop variables are not $a and $b. PowerShell
# variable names are case-INSENSITIVE, so $a and $A are one variable: 'foreach ($b in $B)' would
# overwrite $B with its own first element, and every outer iteration after the first would then
# compare against a single string instead of the whole set. It reported one overlap where there
# were twelve, silently, and the clustering it produced looked entirely plausible.
function Get-PathOverlap {
    param([string[]]$Left, [string[]]$Right)
    $hits = [System.Collections.Generic.List[string]]::new()
    foreach ($lp in $Left) {
        foreach ($rp in $Right) {
            if ([string]::Equals($lp, $rp, [System.StringComparison]::OrdinalIgnoreCase) -or
                $lp.StartsWith("$rp/", [System.StringComparison]::OrdinalIgnoreCase) -or
                $rp.StartsWith("$lp/", [System.StringComparison]::OrdinalIgnoreCase)) {
                if (-not $hits.Contains($lp)) { $hits.Add($lp) }
            }
        }
    }
    return , $hits.ToArray()
}

# ---------------------------------------------------------------------------
# Preflight.
# ---------------------------------------------------------------------------

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Stop-WithProblem 'preflight' 'git is not on PATH.' `
        "Install git, or open a terminal where 'git --version' works."
}
if (-not (Get-Command bd -ErrorAction SilentlyContinue)) {
    Stop-WithProblem 'preflight' 'bd (beads) is not on PATH.' `
        "The plan comes from the bead board, so it cannot be built without bd. Open a terminal where 'bd version' works."
}
if ((Invoke-Git rev-parse --git-dir).ExitCode -ne 0) {
    Stop-WithProblem 'preflight' "$RepoRoot is not a git working copy." `
        'Run this script from a checkout of the RemEx repository.'
}

# ---------------------------------------------------------------------------
# -Claim / -Release: layer 2 writes.
# ---------------------------------------------------------------------------

if ($PSCmdlet.ParameterSetName -ceq 'Release') {
    $issue = Get-BeadJson -Id $Release
    if ($null -eq $issue) {
        Stop-WithProblem 'release' "bd does not know a bead called '$Release'." 'Check the id with: bd list'
    }
    Invoke-Bd update $Release --unset-metadata ralphLanePaths | Out-Null
    Write-Say "Released the path claim on $Release." 'Green'
    if ($Json) { Write-Output (@{ schema = 1; result = 'PASS'; action = 'release'; bead = $Release } | ConvertTo-Json -Compress) }
    exit 0
}

if ($PSCmdlet.ParameterSetName -ceq 'Claim') {
    $issue = Get-BeadJson -Id $Claim
    if ($null -eq $issue) {
        Stop-WithProblem 'claim' "bd does not know a bead called '$Claim'." 'Check the id with: bd list'
    }

    $wanted = @($Paths | ForEach-Object { ($_ -replace '\\', '/').Trim().TrimEnd('/') } | Where-Object { $_ } | Sort-Object -Unique)
    if ($wanted.Count -eq 0) {
        Stop-WithProblem 'claim' 'No paths were given to claim.' `
            'Pass the files this lane intends to touch, for example: -Paths remex.core/Foo.cs,remex.core/Bar.cs'
    }

    # Check against every OTHER live claim. Not atomic - bd has no compare-and-set - but the
    # window is small and layer 3 catches anything that slips through it, which is exactly the
    # division of labour the spec sets out: this layer makes collisions rare, git makes them safe.
    $listed = Invoke-Bd list --status in_progress --json
    $others = @()
    if ($listed.ExitCode -eq 0) {
        try { $others = @($listed.Output | ConvertFrom-Json) } catch { $others = @() }
    }

    foreach ($other in $others) {
        if ((Get-Prop $other 'id') -ceq $Claim) { continue }
        if (-not (Test-ClaimIsLive $other)) { continue }
        $overlap = Get-PathOverlap -Left $wanted -Right (Get-ClaimedPaths $other)
        if ($overlap.Count -gt 0) {
            Stop-WithProblem 'claim' "$($other.id) is already working on: $($overlap -join ', ')" `
                "Finish what you can without those paths, then stop and return this bead with a note saying which paths were taken. Do not edit them anyway - the other lane is in them right now."
        }
    }

    $encoded = ($wanted | ConvertTo-Json -Compress -AsArray)
    Invoke-Bd update $Claim --set-metadata "ralphLanePaths=$encoded" | Out-Null
    Write-Say "$Claim now claims $($wanted.Count) path(s)." 'Green'
    foreach ($p in $wanted) { Write-Say "    $p" }
    if ($Json) {
        Write-Output (@{ schema = 1; result = 'PASS'; action = 'claim'; bead = $Claim; paths = @($wanted) } | ConvertTo-Json -Compress)
    }
    exit 0
}

# ---------------------------------------------------------------------------
# Plan. Everything below is read-only.
# ---------------------------------------------------------------------------

Write-Stage 'Reading the board'

$readyResult = Invoke-Bd ready --json
if ($readyResult.ExitCode -ne 0) {
    Stop-WithProblem 'plan' "bd could not list the ready queue. $($readyResult.Output)" `
        'Check the board is reachable with: bd ready'
}
$ready = @()
try { $ready = @($readyResult.Output | ConvertFrom-Json) } catch { $ready = @() }

if ($Bead) { $ready = @($ready | Where-Object { $Bead -ccontains (Get-Prop $_ 'id') }) }
if ($ready.Count -eq 0) {
    Write-Say '  Nothing is ready.'
    if ($Json) { Write-Output (@{ schema = 1; result = 'NOOP'; lanes = @(); unscheduled = @() } | ConvertTo-Json -Compress) }
    exit 0
}

# An epic is a container for its children, not a unit of work a lane can be handed.
$ready = @($ready | Where-Object { (Get-Prop $_ 'issue_type') -cne 'epic' })
$considered = @($ready | Select-Object -First $Limit)
Write-Say "  $($ready.Count) ready, considering the first $($considered.Count)."

# The tracked file list, used to throw out path-shaped text that is not a path.
$trackedFiles = @((Invoke-Git ls-files).Output -split "`n" | Where-Object { $_ })
$trackedSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($f in $trackedFiles) { [void]$trackedSet.Add($f) }
$dirSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($f in $trackedFiles) {
    $parts = $f -split '/'
    for ($i = 1; $i -lt $parts.Count; $i++) { [void]$dirSet.Add(($parts[0..($i - 1)] -join '/')) }
}
Write-Say "  $($trackedFiles.Count) tracked files, $($dirSet.Count) directories."

# --- Is the gitnexus index fresh enough to be worth asking? -----------------
$gitnexusUsable = $false
$gitnexusWhy = 'not installed'
if (Get-Command npx -ErrorAction SilentlyContinue) {
    $status = (& npx --no-install gitnexus status 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { $gitnexusWhy = 'not installed' }
    elseif ($status -match 'stale') {
        # A graph describing code that has since changed produces a confidently wrong estimate,
        # which is worse than an admitted gap: the operator cannot tell it is wrong by looking.
        $gitnexusWhy = 'index is stale - run: npx gitnexus analyze'
    }
    else {
        $gitnexusUsable = $true
        $gitnexusWhy = 'fresh'
    }
}

# --- Label co-change, built once for the whole run --------------------------
$labelFiles = @{}
$labelYield = 0
if ($UseLabelHistory) {
    $closed = @()
    $closedResult = Invoke-Bd list --status closed --json
    if ($closedResult.ExitCode -eq 0) {
        try { $closed = @($closedResult.Output | ConvertFrom-Json) } catch { $closed = @() }
    }
    $beadToFiles = @{}
    $log = (Invoke-Git log --all --format='%x01%s' --name-only -n 800).Output
    foreach ($chunk in ($log -split "`u{0001}")) {
        $lines = @($chunk -split "`n" | Where-Object { $_.Trim() })
        if ($lines.Count -lt 2) { continue }
        $subject = $lines[0]
        $files = @($lines[1..($lines.Count - 1)])
        foreach ($m in [regex]::Matches($subject, 'RemEx-[A-Za-z0-9.]+')) {
            $id = $m.Value
            if (-not $beadToFiles.ContainsKey($id)) { $beadToFiles[$id] = [System.Collections.Generic.HashSet[string]]::new() }
            foreach ($f in $files) { [void]$beadToFiles[$id].Add($f) }
        }
    }
    foreach ($c in $closed) {
        $cid = Get-Prop $c 'id'
        if (-not $beadToFiles.ContainsKey($cid)) { continue }
        foreach ($lab in @(Get-Prop $c 'labels')) {
            if (-not $lab) { continue }
            if (-not $labelFiles.ContainsKey($lab)) { $labelFiles[$lab] = [System.Collections.Generic.HashSet[string]]::new() }
            foreach ($f in $beadToFiles[$cid]) { [void]$labelFiles[$lab].Add($f) }
        }
    }
    foreach ($k in $labelFiles.Keys) { $labelYield += $labelFiles[$k].Count }
}

# ---------------------------------------------------------------------------
# Estimate one bead.
# ---------------------------------------------------------------------------

function Get-Estimate {
    param($Issue)

    $text = @(
        (Get-Prop $Issue 'title')
        (Get-Prop $Issue 'description')
        (Get-Prop $Issue 'acceptance_criteria')
        (Get-Prop $Issue 'design')
    ) -join "`n"
    if (-not $text) { $text = '' }

    $files = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $sources = [System.Collections.Generic.List[string]]::new()

    # --- text ---------------------------------------------------------------
    $textHits = 0
    foreach ($m in [regex]::Matches($text, '[A-Za-z0-9_.\-]+(?:/[A-Za-z0-9_.\-]+)+')) {
        # Bead prose ends sentences on paths, so a trailing dot is punctuation far more often
        # than it is part of a filename.
        $candidate = ($m.Value -replace '\\', '/').TrimEnd('.', ',', ')', ';', ':')
        if (-not $candidate) { continue }
        if ($trackedSet.Contains($candidate) -or $dirSet.Contains($candidate)) {
            if ($files.Add($candidate)) { $textHits++ }
        }
    }
    if ($textHits -gt 0) { $sources.Add('text') }

    # --- identifiers, via grep ----------------------------------------------
    # Longest first: a longer identifier is more distinctive, and the cap means only the most
    # informative few get spent.
    $identifiers = [System.Collections.Generic.List[string]]::new()
    foreach ($m in [regex]::Matches($text, '\b(?:[a-z0-9]+_[a-z0-9_]+|[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]*)+)\b')) {
        if (-not $identifiers.Contains($m.Value)) { $identifiers.Add($m.Value) }
    }
    $picked = @($identifiers | Sort-Object -Property Length -Descending | Select-Object -First 6)

    $grepHits = 0
    foreach ($ident in $picked) {
        $found = Invoke-Git grep -l --fixed-strings $ident
        if ($found.ExitCode -ne 0) { continue }
        $matched = @($found.Output -split "`n" | Where-Object { $_ })
        # Too generic to be evidence. Naming a type that half the repo mentions says nothing
        # about which files THIS bead will edit.
        if ($matched.Count -gt $MaxGrepFiles) { continue }
        foreach ($f in $matched) {
            if ($AlwaysExcluded -ccontains $f) { continue }
            if ($files.Add($f)) { $grepHits++ }
        }
    }
    if ($grepHits -gt 0) { $sources.Add('grep') }

    # --- gitnexus -----------------------------------------------------------
    $nexusHits = 0
    if ($gitnexusUsable) {
        foreach ($ident in @($picked | Select-Object -First 3)) {
            $out = (& npx --no-install gitnexus impact $ident --depth 1 --limit 20 2>&1 | Out-String)
            if ($LASTEXITCODE -ne 0) { continue }
            try { $parsed = $out | ConvertFrom-Json } catch { continue }
            $targetFile = Get-Prop (Get-Prop $parsed 'target') 'filePath'
            if ($targetFile -and $files.Add($targetFile)) { $nexusHits++ }
            foreach ($depth in @(Get-Prop $parsed 'byDepth')) {
                if ($null -eq $depth) { continue }
                foreach ($p in $depth.PSObject.Properties) {
                    foreach ($sym in @($p.Value)) {
                        $fp = Get-Prop $sym 'filePath'
                        if ($fp -and $files.Add($fp)) { $nexusHits++ }
                    }
                }
            }
        }
    }
    if ($nexusHits -gt 0) { $sources.Add('gitnexus') }

    # --- labels -------------------------------------------------------------
    $labelHits = 0
    if ($UseLabelHistory) {
        foreach ($lab in @(Get-Prop $Issue 'labels')) {
            if (-not $lab -or -not $labelFiles.ContainsKey($lab)) { continue }
            foreach ($f in $labelFiles[$lab]) {
                if ($AlwaysExcluded -ccontains $f) { continue }
                if ($files.Add($f)) { $labelHits++ }
            }
        }
    }
    if ($labelHits -gt 0) { $sources.Add('labels') }

    foreach ($x in $AlwaysExcluded) { [void]$files.Remove($x) }

    return [pscustomobject]@{
        Paths   = @($files | Sort-Object)
        Sources = @($sources)
    }
}

# ---------------------------------------------------------------------------
# Live claims held by lanes that are working right now.
# ---------------------------------------------------------------------------

Write-Stage 'Checking what other lanes are holding'

$inProgress = @()
$ipResult = Invoke-Bd list --status in_progress --json
if ($ipResult.ExitCode -eq 0) {
    try { $inProgress = @($ipResult.Output | ConvertFrom-Json) } catch { $inProgress = @() }
}

$liveClaims = [System.Collections.Generic.List[object]]::new()
foreach ($issue in $inProgress) {
    if (-not (Test-ClaimIsLive $issue)) { continue }
    $claimed = Get-ClaimedPaths $issue
    if ($claimed.Count -eq 0) { continue }
    $liveClaims.Add([pscustomobject]@{
        Bead  = Get-Prop $issue 'id'
        Lane  = Get-Prop (Get-Prop $issue 'metadata') 'ralphLane'
        Paths = @($claimed)
    })
}

if ($liveClaims.Count -eq 0) { Write-Say '  No live claims - every lane is free.' }
foreach ($c in $liveClaims) { Write-Say "  $($c.Bead) (lane $($c.Lane)) holds $($c.Paths.Count) path(s)" }

# ---------------------------------------------------------------------------
# Estimate and cluster.
# ---------------------------------------------------------------------------

Write-Stage "Estimating what $($considered.Count) bead(s) would touch"

$estimates = [System.Collections.Generic.List[object]]::new()
foreach ($issue in $considered) {
    $est = Get-Estimate -Issue $issue
    $estimates.Add([pscustomobject]@{
        Bead     = Get-Prop $issue 'id'
        Title    = Get-Prop $issue 'title'
        Priority = Get-Prop $issue 'priority'
        Paths    = $est.Paths
        Sources  = $est.Sources
    })
}

$withEstimate = @($estimates | Where-Object { $_.Paths.Count -gt 0 }).Count
Write-Say "  $withEstimate of $($estimates.Count) produced a usable estimate."
Write-Say "  sources: text and grep always; gitnexus $gitnexusWhy; labels $(if ($UseLabelHistory) { "on, $labelYield file(s) known" } else { 'off (measured at ~1 file per label on this repo)' })"

# Highest priority first; within a priority, the biggest footprint first, because a bead that
# touches a lot is the hardest to fit later and the cheapest to place now.
$ordered = @($estimates | Sort-Object -Property @{ Expression = 'Priority' }, @{ Expression = { $_.Paths.Count }; Descending = $true })

$laneBuckets = @()
for ($i = 1; $i -le $Lanes; $i++) {
    $laneBuckets += [pscustomobject]@{
        Lane  = $i
        Beads = [System.Collections.Generic.List[object]]::new()
        Paths = [System.Collections.Generic.List[string]]::new()
        HasUnknown = $false
    }
}

$unscheduled = [System.Collections.Generic.List[object]]::new()

# Beads whose footprint is unknown are held back for a second pass rather than competing in the
# first. They sort last (a zero-size estimate is the smallest there is), so by the time the main
# pass reached one, every lane already held something and the "needs an empty lane" rule meant it
# could never be placed at all - a bead naming no files was permanently unschedulable. Deferring
# them lets the known work pack the lanes by priority first, and then an unknown takes idle
# capacity if there is any.
$unknownFootprint = [System.Collections.Generic.List[object]]::new()

foreach ($e in $ordered) {
    # A path some other lane is in right now is not available at any price.
    $clash = $null
    foreach ($c in $liveClaims) {
        $overlap = Get-PathOverlap -Left $e.Paths -Right $c.Paths
        if ($overlap.Count -gt 0) { $clash = "$($c.Bead) is in $($overlap -join ', ')"; break }
    }
    if ($clash) {
        $unscheduled.Add(@{ bead = $e.Bead; why = "a live lane holds it - $clash" })
        continue
    }

    if ($e.Paths.Count -eq 0) { $unknownFootprint.Add($e); continue }

    $placed = $false
    foreach ($bucket in $laneBuckets) {
        $overlap = Get-PathOverlap -Left $e.Paths -Right @($bucket.Paths)
        if ($overlap.Count -gt 0) { continue }
        $bucket.Beads.Add($e)
        foreach ($p in $e.Paths) { $bucket.Paths.Add($p) }
        $placed = $true
        break
    }

    if (-not $placed) {
        $unscheduled.Add(@{ bead = $e.Bead; why = 'its files are already spoken for in every lane this wave' })
    }
}

# Second pass. Exactly ONE unknown goes out per wave, into a lane that is otherwise empty.
# An empty lane is the only placement that does not risk colliding with work already scheduled
# beside it, and one per wave is the cap because two beads nobody can predict are the likeliest
# pair to hand the merge queue an avoidable conflict. Layer 3 would make that safe, only slow -
# but slow is the thing this whole exercise is trying to buy back.
$unknownPlaced = $false
foreach ($e in $unknownFootprint) {
    $placed = $false
    if (-not $unknownPlaced) {
        foreach ($bucket in $laneBuckets) {
            if ($bucket.Beads.Count -gt 0) { continue }
            $bucket.Beads.Add($e)
            $bucket.HasUnknown = $true
            $unknownPlaced = $true
            $placed = $true
            break
        }
    }

    if (-not $placed) {
        # Worth being blunt about the cause: a bead that names neither a file nor an identifier
        # is under-specified, and the fix is to say what it touches rather than to loosen this.
        # Until then it is work for the sequential loop, which has no lane to collide with.
        $why = if ($unknownPlaced) {
            'footprint unknown, and one unknown per wave is the limit - name a file or a symbol in the bead, or drain it sequentially'
        }
        else {
            'footprint unknown and every lane already has work - name a file or a symbol in the bead, or drain it sequentially'
        }
        $unscheduled.Add(@{ bead = $e.Bead; why = $why })
    }
}

# ---------------------------------------------------------------------------
# Report.
# ---------------------------------------------------------------------------

$plannedCount = @($laneBuckets | ForEach-Object { $_.Beads.Count } | Measure-Object -Sum).Sum

if ($Json) {
    Write-Output (@{
        schema      = 1
        result      = if ($plannedCount -gt 0) { 'PASS' } else { 'NOOP' }
        lanes       = @($laneBuckets | ForEach-Object {
            @{
                lane  = $_.Lane
                beads = @($_.Beads | ForEach-Object {
                    @{ bead = $_.Bead; title = $_.Title; priority = $_.Priority; paths = @($_.Paths); sources = @($_.Sources) }
                })
            }
        })
        liveClaims  = @($liveClaims | ForEach-Object { @{ bead = $_.Bead; lane = $_.Lane; paths = @($_.Paths) } })
        unscheduled = @($unscheduled)
        gitnexus    = $gitnexusWhy
        durationSec = [int]((Get-Date) - $started).TotalSeconds
    } | ConvertTo-Json -Compress -Depth 8)
    exit 0
}

Write-Stage "Plan for $Lanes lane(s)"
foreach ($bucket in $laneBuckets) {
    if ($bucket.Beads.Count -eq 0) {
        Write-Host "  lane $($bucket.Lane)  (nothing to give it)" -ForegroundColor DarkGray
        continue
    }
    Write-Host "  lane $($bucket.Lane)" -ForegroundColor Green
    foreach ($b in $bucket.Beads) {
        Write-Host "    $($b.Bead)  P$($b.Priority)  $($b.Title)" -ForegroundColor Green
        if ($b.Paths.Count -eq 0) {
            Write-Host '      footprint unknown - nothing in the bead text resolved to a file' -ForegroundColor Yellow
        }
        else {
            $shown = @($b.Paths | Select-Object -First 6)
            Write-Host "      $($b.Paths.Count) file(s) via $($b.Sources -join '+'): $($shown -join ', ')$(if ($b.Paths.Count -gt 6) { ', ...' })"
        }
    }
}

if ($unscheduled.Count -gt 0) {
    Write-Host "`n  Not scheduled this wave" -ForegroundColor Yellow
    foreach ($u in $unscheduled) { Write-Host "    $($u.bead) - $($u.why)" -ForegroundColor Yellow }
}

Write-Host "`n  This is an estimate and it is advisory only." -ForegroundColor Cyan
Write-Host '  Nothing here authorises anything: a wrong guess costs throughput, and the merge queue' -ForegroundColor Cyan
Write-Host '  catches real overlap at rebase time whatever this says.' -ForegroundColor Cyan
Write-Host "  Planned $plannedCount bead(s) in $([int]((Get-Date) - $started).TotalSeconds)s. Nothing was changed."
exit 0

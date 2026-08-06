#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Lands finished parallel board-drain lanes onto the integration branch, one at a time.

.DESCRIPTION
    Several lanes work at once; exactly one thing lands at a time. This script is that one
    thing. The concurrency in this design lives in the lanes and nowhere else - the landing is
    serialised by construction, because the whole reason to re-verify per landing is that
    isolated green plus isolated green is not green.

    A lane produces a verified BRANCH, not a closed bead. This script is what turns the former
    into the latter, and it is the only place a bead in a parallel drain is allowed to close.

    For each candidate, in the order the lanes finished:

      1. Check the branch carries a docs/CHANGELOG.md entry. Refuse before building if not.
      2. Record the pre-merge SHA, then rebase the lane branch onto the integration head.
         A conflict returns the bead with the conflicting paths named. Never auto-resolved:
         a merge resolution is a code change nobody reviewed and nothing verified.
      3. Fast-forward the integration branch onto the rebased lane branch.
      4. Run ./scripts/verify.ps1 IN THE INTEGRATION TREE. This is the receipt that counts.
      5. Require -Check to say VALID at the moment of closing (CLAUDE.md's verification rule).
      6. PASS -> bd close, journal the landing, tear the lane down.
         FAIL -> quarantine: reset the integration branch to the recorded SHA, reopen the bead
         with the failure attached, KEEP the branch and its worktree as evidence, do not retry.

    Two things are worth knowing before reading the code.

    WHY THE JOURNAL IS .jsonl AND NOT .json. verify.ps1 fingerprints '*.json' among its source
    patterns, and a git pathspec of '*.json' matches at any depth - so docs/ralph-state.json
    would be part of the fingerprint. Journalling a landing would then invalidate the very
    receipt that proves the landing was green, which makes "close the bead" and "record that
    you closed it" mutually exclusive. A '.jsonl' suffix is outside that pattern, and an
    append-only line format is the better fit for the file anyway. The file had never been
    written when this script was authored, so nothing had to be migrated.

    WHY THE DEFAULT SCOPE IS 'all'. The spec provisionally ran -Scope dotnet per landing and
    -Scope all once per batch, to keep the queue from being dominated by NDK builds, and said
    to drop the distinction if Phase 0 showed Android verify was cheaper than feared. It did:
    RemEx-56fu.5.1 measured -Scope all at 94s against ~49s for dotnet, with the queue at 14%
    utilisation. Doubling a number that small does not make it a bottleneck, and it buys back
    the thing the trade gave away - an Android regression attributed to a commit rather than
    to a whole batch. -Scope dotnet is still available for a batch known to be .NET-only, and
    when it is used, this script runs one -Scope all at the end if any landing touched Android
    paths, which is the spec's original fallback.

    Designed in docs/SPEC-parallel-board-drain-dispatcher.md section 7 (RemEx-56fu.5.3).

.PARAMETER Bead
    Land only these beads. Repeatable. They must still be genuine candidates - this narrows
    the queue, it does not bypass any gate. Default: every candidate found.

.PARAMETER Scope
    What ./scripts/verify.ps1 runs per landing. 'all' (default) includes the Android suite;
    'dotnet' skips it and triggers one batch-level 'all' run if any landing touched Android.

.PARAMETER IntegrationBranch
    The branch to land onto. Defaults to whatever the integration copy is currently on. main
    and master are refused outright - the loop never lands on either.

.PARAMETER LanesRoot
    Where lane worktrees live. Defaults to a sibling of the repository, matching
    ralph-lane-bootstrap.ps1. Only used to place a temporary worktree when a lane branch has
    no worktree of its own.

.PARAMETER PlanOnly
    Print the candidates in landing order and exit, changing nothing. The clustering and the
    ordering are the parts most likely to be wrong, and they must be inspectable without
    anything being merged.

.PARAMETER KeepLanes
    Do not remove the worktree or delete the branch after a successful landing. For debugging
    the queue itself; a normal drain wants the lane freed.

.PARAMETER Json
    Print one line of JSON and nothing else. For the dispatcher.

.EXAMPLE
    ./scripts/ralph-merge-queue.ps1 -PlanOnly
    Show what would land, in what order, and why anything is being skipped.

.EXAMPLE
    ./scripts/ralph-merge-queue.ps1
    Drain the queue: land every ready lane, verifying the integration tree per landing.

.EXAMPLE
    ./scripts/ralph-merge-queue.ps1 -Bead RemEx-abcd -Scope dotnet
    Land one specific bead, skipping the Android suite for this landing.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string[]]$Bead,

    [Parameter(Mandatory = $false)]
    [ValidateSet('dotnet', 'all')]
    [string]$Scope = 'all',

    [Parameter(Mandatory = $false)]
    [string]$IntegrationBranch,

    [Parameter(Mandatory = $false)]
    [string]$LanesRoot,

    [Parameter(Mandatory = $false)]
    [switch]$PlanOnly,

    [Parameter(Mandatory = $false)]
    [switch]$KeepLanes,

    [Parameter(Mandatory = $false)]
    [switch]$SelfTest,

    [Parameter(Mandatory = $false)]
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# This script deliberately runs git commands that are EXPECTED to fail - a rebase that hits a
# conflict is a normal outcome here, not an error. If native commands honoured the Stop
# preference, every one of those would throw past the handling written for it and the queue
# would abort instead of quarantining. The default is already false, but it is a preference
# variable, so a machine that has flipped it would silently break conflict handling rather
# than reporting anything. Pinning it costs two lines and removes the whole class of surprise.
if (Test-Path 'variable:PSNativeCommandUseErrorActionPreference') {
    $PSNativeCommandUseErrorActionPreference = $false
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
$VerifyScript = Join-Path $PSScriptRoot 'verify.ps1'
$JournalPath = Join-Path $RepoRoot 'docs' 'ralph-state.jsonl'
$JournalRelative = 'docs/ralph-state.jsonl'
$ChangelogRelative = 'docs/CHANGELOG.md'
$LockPath = Join-Path $RepoRoot '.ralph' 'merge-queue.lock'
$ReceiptPath = Join-Path $RepoRoot '.ralph' 'verify-receipt.json'
$ReceiptBackup = Join-Path $RepoRoot '.ralph' 'verify-receipt.premerge.json'
$started = Get-Date

if (-not $LanesRoot) {
    $LanesRoot = Join-Path (Split-Path -Parent $RepoRoot) ((Split-Path -Leaf $RepoRoot) + '.lanes')
}

# Outcomes accumulate here and become the summary. Nothing else holds queue state: everything
# authoritative lives in git and in bd, both of which survive this process dying mid-drain.
$landed = [System.Collections.Generic.List[object]]::new()
$quarantined = [System.Collections.Generic.List[object]]::new()
$returned = [System.Collections.Generic.List[object]]::new()
$skipped = [System.Collections.Generic.List[object]]::new()
$androidTouched = $false
$lockHeld = $false

# Holds the commit to fall back to while a landing is in flight, and $null the rest of the
# time. See the trap below - this is what makes an unexpected crash survivable.
$SafeSha = $null

# ---------------------------------------------------------------------------
# Output helpers. Everything human-facing goes through these so -Json can
# silence the lot and leave a single parseable line on stdout. Same shape as
# scripts/verify.ps1 and scripts/ralph-lane-bootstrap.ps1.
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

function Remove-Lock {
    if ($lockHeld -and (Test-Path -LiteralPath $LockPath)) {
        Remove-Item -LiteralPath $LockPath -Force -ErrorAction SilentlyContinue
    }
}

function Stop-WithProblem {
    param([string]$Stage, [string]$Text, [string]$WhatToDo)
    Remove-Lock
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

# StrictMode turns a missing property into a terminating error, and bd omits 'metadata'
# entirely on a bead that has none - which is the common case. Read through this instead.
function Get-Prop {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    if ($Object.PSObject.Properties.Name -contains $Name) { return $Object.$Name }
    return $null
}

# Runs git and hands back BOTH the exit code and the output, because most callers here need
# the output precisely when the command failed. Redirected native stderr arrives as
# ErrorRecord objects, so each line is stringified rather than formatted.
#
# It collects arguments through $args, and it must stay a SIMPLE function to do so. The
# obvious spelling - [Parameter(ValueFromRemainingArguments)] - makes this an ADVANCED
# function, which silently gains PowerShell's common parameters, and '-d' is an unambiguous
# prefix of '-Debug'. So 'branch -d <name>' arrived at git as 'branch <name>': not a delete
# but a create, which then failed with "a branch named <name> already exists" and left every
# landed lane branch undeleted. (Measured. '-f' survives because no common parameter starts
# with f, which is exactly why the bug was not obvious.) Do not add [Parameter()] here.
function Invoke-Git {
    param([string]$In)
    $raw = & git -C $In @args 2>&1
    $code = $LASTEXITCODE
    $text = (@($raw) | ForEach-Object { $_.ToString() }) -join "`n"
    return [pscustomobject]@{ ExitCode = $code; Output = $text.Trim() }
}

function Get-PwshPath {
    # Run verify.ps1 in a child pwsh with -NoProfile -File so its exit code is unambiguous and
    # nothing in this session's state can influence the result. Reuse the interpreter already
    # running rather than hoping the right pwsh is first on PATH.
    $self = (Get-Process -Id $PID).Path
    if ($self) { return $self }
    return 'pwsh'
}

# Maps every branch that is checked out somewhere to the worktree holding it. Parsed from git
# rather than remembered anywhere, per the spec's rule that the queue holds no state of its own.
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

# --directory is pinned rather than relying on the working directory, because bd resolves its
# workspace by walking up from cwd. Run from anywhere else, this script would quietly reach a
# different board, or none, and the queue's whole job is closing beads on the right one.
# Simple, not advanced, for the same reason as Invoke-Git above.
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

# One line per event, appended and committed as it happens rather than at the end of the drain.
# The journal exists because a session fork loses in-memory loop state; a journal that is only
# written on a clean exit would be missing exactly when it is needed.
function Add-JournalEntry {
    param(
        [string]$BeadId, [string]$Outcome, [int]$LaneNumber,
        [string]$SourceHash, [string]$Commit, [string]$Detail
    )

    $entry = [ordered]@{
        bead         = $BeadId
        outcome      = $Outcome
        lane         = $LaneNumber
        sourceHash   = $SourceHash
        commit       = $Commit
        timestampUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    }
    if ($Detail) { $entry['detail'] = $Detail }

    $docsDir = Split-Path -Parent $JournalPath
    if (-not (Test-Path -LiteralPath $docsDir)) {
        New-Item -ItemType Directory -Path $docsDir -Force | Out-Null
    }
    Add-Content -LiteralPath $JournalPath -Encoding utf8NoBOM `
        -Value ($entry | ConvertTo-Json -Compress -Depth 4)

    # The path is written out in full and in the exact case it has on disk. Never assembled
    # from a variable holding a remembered path - CLAUDE.md Hard Rule 4, because a case
    # mismatch in a pathspec stages nothing at all on Windows and says so nowhere.
    # '--' is QUOTED on purpose. Written bare, the PowerShell parser reads it as its own
    # end-of-parameters marker and drops it before git ever sees it, so the pathspec separator
    # silently disappears. (Measured.) It survives as a string.
    $add = Invoke-Git -In $RepoRoot add '--' 'docs/ralph-state.jsonl'
    if ($add.ExitCode -ne 0) {
        Write-Warn "could not stage $JournalRelative - $($add.Output)"
        return
    }
    $commitResult = Invoke-Git -In $RepoRoot commit -m "chore(ralph): journal $BeadId ($Outcome)"
    if ($commitResult.ExitCode -ne 0) {
        Write-Warn "could not commit $JournalRelative - $($commitResult.Output)"
    }
}

# Pull the receipt out of whatever verify.ps1 actually printed.
#
# `-Json` is documented as printing the receipt and nothing else. It does not, and cannot on its
# own: verify.ps1 silences its own Write-Say under -Json, but the scripts it calls report through
# Write-Host, which writes to the HOST rather than down the pipeline - so `| Out-Null` in the
# caller does not catch it and it arrives on stdout regardless. check-localization.ps1 alone has
# 47 such calls. ConvertFrom-Json over the whole capture therefore throws on the human text in
# front of the receipt, the result reads as null, and a PASS is recorded as a failure. That is
# what quarantined a green RemEx-56fu on the first real three-lane wave: 2413/2414 passing, zero
# failures, filed as an integration failure with the passing localization tail as its reason.
#
# So take the LAST line that parses as an object rather than the whole blob. That is robust to any
# amount of chatter in front of it, including chatter added later by someone who never reads this
# file - which is the failure mode worth designing against, since the leak is invisible until a
# landing blames it on the wrong thing hours later.
function ConvertFrom-ReceiptText {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $candidates = @($Text -split "`r?`n" |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_.StartsWith('{') -and $_.EndsWith('}') })

    for ($i = $candidates.Count - 1; $i -ge 0; $i--) {
        try { return ($candidates[$i] | ConvertFrom-Json) } catch { continue }
    }
    return $null
}

# Run verify.ps1 and wait for the PROCESS, not for its output pipe.
#
# `& pwsh ... 2>&1` looks equivalent and is not. The call operator returns when every handle on the
# child's stdout closes, and verify.ps1 -Scope all starts a Gradle daemon that inherits that handle
# and outlives the build on purpose. Measured on the first real wave: verify wrote its receipt at
# 11:57:26Z after 139 seconds, and the queue did not move until 15:30:51Z - three hours and
# thirty-three minutes, matching the Gradle daemon's default 3h idle timeout from its last use to
# the minute. Landings are serialised by design, so that is not one slow landing, it is a ceiling
# on the entire dispatcher.
#
# Redirecting to a file removes the pipe from the picture entirely: an inherited file handle blocks
# nobody. WaitForExit() on the process object is then the real signal, and unlike -Wait it makes no
# claim about the rest of the process tree - which is exactly the distinction that bit us.
function Invoke-VerifyProcess {
    param(
        [string[]]$Arguments,
        # Only the self-test passes this. It exists so the pipe behaviour above can be checked
        # against a stub that reproduces it in a second, instead of against a real Android build.
        [string]$Script = $null
    )

    $target = if ($Script) { $Script } else { $VerifyScript }
    $outFile = [System.IO.Path]::GetTempFileName()
    $errFile = [System.IO.Path]::GetTempFileName()
    try {
        $proc = Start-Process -FilePath $pwshPath `
            -ArgumentList (@('-NoProfile', '-File', $target) + $Arguments) `
            -NoNewWindow -PassThru `
            -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        $proc.WaitForExit()

        $out = ''
        $err = ''
        if (Test-Path -LiteralPath $outFile) { $out = [string](Get-Content -LiteralPath $outFile -Raw) }
        if (Test-Path -LiteralPath $errFile) { $err = [string](Get-Content -LiteralPath $errFile -Raw) }

        return [pscustomobject]@{
            ExitCode = $proc.ExitCode
            Text     = (@($out, $err) | Where-Object { $_ }) -join "`n"
            Receipt  = ConvertFrom-ReceiptText $out
        }
    }
    finally {
        Remove-Item -LiteralPath $outFile -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $errFile -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------
# Self-test. Before preflight, so it needs no clean tree, no lock and no lanes.
#
# Both things checked here failed in production and neither was visible from the queue's own
# output: the first reported a passing build as an integration failure, the second stalled a
# landing for three and a half hours. A landing is the serialised step, so anything that can
# silently break one is worth a second of checking on every pass.
# ---------------------------------------------------------------------------

if ($SelfTest) {
    $failures = [System.Collections.Generic.List[string]]::new()
    $checked = 0

    function Assert-Receipt {
        param([string]$What, [string]$Text, [string]$ExpectResult)
        $script:checked++
        $got = ConvertFrom-ReceiptText $Text
        $actual = if ($null -eq $got) { '<null>' } else { [string](Get-Prop $got 'result') }
        if ($actual -cne $ExpectResult) { $failures.Add("$What - expected '$ExpectResult', got '$actual'") }
    }

    $receiptLine = '{"schema":1,"result":"PASS","scope":"all","testsRun":2414,"problems":[]}'

    # The plain case, which never broke.
    Assert-Receipt 'a bare receipt parses' $receiptLine 'PASS'

    # THE REGRESSION. This is the exact shape that quarantined a green RemEx-56fu: pages of
    # Write-Host chatter from a child script, then the receipt.
    $leaky = @(
        '  Axis 1: key parity across all 9 files...'
        '  Reading 34 historical versions of strings.xml...'
        '  No new localization problems. Every key exists in all 9 files on both platforms,'
        $receiptLine
    ) -join "`n"
    Assert-Receipt 'a receipt behind child-script chatter still parses' $leaky 'PASS'

    # Trailing chatter too - nothing guarantees the receipt is last.
    Assert-Receipt 'a receipt with chatter on both sides parses' `
        ("noise`n$receiptLine`n  Receipt written to .ralph/verify-receipt.json") 'PASS'

    # A real FAIL must still read as FAIL rather than as a parse failure, or the two become
    # indistinguishable and quarantine stops meaning anything.
    Assert-Receipt 'a failing receipt is read as FAIL' `
        ('  build noise' + "`n" + '{"schema":1,"result":"FAIL","problems":["3 tests failed"]}') 'FAIL'

    # No receipt at all is still null. Being permissive about where the receipt is must not make
    # us permissive about whether there is one.
    $checked++
    if ($null -ne (ConvertFrom-ReceiptText "just noise`nand more noise")) {
        $failures.Add('output with no receipt should not parse as one')
    }
    $checked++
    if ($null -ne (ConvertFrom-ReceiptText '')) { $failures.Add('empty output should not parse as a receipt') }

    # A brace-shaped line that is not JSON must not be mistaken for the receipt, and must not
    # stop us finding the real one further up.
    Assert-Receipt 'a malformed brace line is skipped for the real receipt' `
        ("$receiptLine`n{not json at all}") 'PASS'

    # THE OTHER REGRESSION. A grandchild holding stdout is exactly the Gradle daemon, and the
    # call operator waits for it. Redirecting to a file must not: this stub exits at once while
    # leaving a child alive for well beyond the tolerance below.
    $stub = Join-Path ([System.IO.Path]::GetTempPath()) 'ralph-mq-selftest-stub.ps1'
    $pwshPath = Get-PwshPath
    @'
Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 30') -NoNewWindow | Out-Null
Write-Host '  pretend build output'
Write-Output '{"schema":1,"result":"PASS","scope":"stub"}'
exit 0
'@ | Set-Content -LiteralPath $stub -Encoding UTF8

    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $run = Invoke-VerifyProcess -Arguments @() -Script $stub
        $sw.Stop()

        $checked++
        if ($sw.Elapsed.TotalSeconds -ge 20) {
            $failures.Add(("waited $([int]$sw.Elapsed.TotalSeconds)s for a child that exited immediately - " +
                'the runner is waiting on the output pipe again, not on the process'))
        }
        $checked++
        if ($run.ExitCode -ne 0) { $failures.Add("stub exit code came back $($run.ExitCode), expected 0") }
        $checked++
        if ($null -eq $run.Receipt -or (Get-Prop $run.Receipt 'scope') -cne 'stub') {
            $failures.Add('the runner did not return the stub receipt')
        }
    }
    finally {
        Remove-Item -LiteralPath $stub -Force -ErrorAction SilentlyContinue
    }

    if ($failures.Count -gt 0) {
        Write-Host "Merge queue self-test FAILED - $($failures.Count) of $checked check(s)." -ForegroundColor Red
        foreach ($f in $failures) { Write-Host "  $f" -ForegroundColor Red }
        exit 1
    }

    Write-Host "Merge queue self-test passed - $checked check(s)." -ForegroundColor Green
    exit 0
}

# ---------------------------------------------------------------------------
# Preflight. Everything here is a precondition of doing this safely, and the
# reset in the quarantine path is the reason the bar is set where it is.
# ---------------------------------------------------------------------------

Write-Stage 'Checking the integration copy'

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Stop-WithProblem 'preflight' 'git is not on PATH.' `
        "Install git, or open a terminal where 'git --version' works."
}
if (-not (Get-Command bd -ErrorAction SilentlyContinue)) {
    Stop-WithProblem 'preflight' 'bd (beads) is not on PATH.' `
        "The queue closes and reopens beads, so it cannot run without bd. Open a terminal where 'bd version' works."
}
if (-not (Test-Path -LiteralPath $VerifyScript)) {
    Stop-WithProblem 'preflight' "There is no verify script at $VerifyScript." `
        'The merge queue has nothing to gate on without it. Restore scripts/verify.ps1.'
}

$gitDirCheck = Invoke-Git -In $RepoRoot rev-parse --git-dir
if ($gitDirCheck.ExitCode -ne 0) {
    Stop-WithProblem 'preflight' "$RepoRoot is not a git working copy." `
        'Run this script from a checkout of the RemEx repository.'
}

$currentBranch = (Invoke-Git -In $RepoRoot rev-parse --abbrev-ref HEAD).Output
if (-not $IntegrationBranch) { $IntegrationBranch = $currentBranch }

if ($IntegrationBranch -ceq 'main' -or $IntegrationBranch -ceq 'master') {
    Stop-WithProblem 'preflight' "The integration branch is '$IntegrationBranch'." `
        'The drain loop never lands on main or master. Switch to the integration branch first, for example: git switch v2.5-board-drain'
}
if ($currentBranch -cne $IntegrationBranch) {
    Stop-WithProblem 'preflight' "This copy is on '$currentBranch' but you asked to land onto '$IntegrationBranch'." `
        "The queue lands in the copy it is run from, so the two have to agree. Run: git switch $IntegrationBranch"
}

# A rebase or merge left half-finished means the tree is mid-surgery, and the queue is about to
# rebase and reset. Refuse rather than pile on.
$gitDir = (Invoke-Git -In $RepoRoot rev-parse --absolute-git-dir).Output
foreach ($marker in @('rebase-merge', 'rebase-apply', 'MERGE_HEAD', 'CHERRY_PICK_HEAD')) {
    if (Test-Path -LiteralPath (Join-Path $gitDir $marker)) {
        Stop-WithProblem 'preflight' "There is an unfinished git operation in this copy ($marker)." `
            'Finish or abort it first, then run the queue again. For a rebase that is: git rebase --abort'
    }
}

# THE load-bearing precondition. The quarantine path resets this tree hard to a recorded SHA,
# and that is only safe because the tree is clean when the queue starts and the queue commits
# everything it writes. A dirty tree here would be silently destroyed by the first failure.
$dirty = @((Invoke-Git -In $RepoRoot status --porcelain).Output -split "`n" | Where-Object { $_ })
if ($dirty.Count -gt 0) {
    if ($PlanOnly) {
        Write-Warn "$($dirty.Count) uncommitted change(s) here. Planning is read-only so this is fine, but a real drain will refuse until the tree is clean."
    }
    else {
        Stop-WithProblem 'preflight' "There are $($dirty.Count) uncommitted change(s) in $RepoRoot." `
            'A landing that fails resets this tree to the pre-merge commit, which would throw those away. Commit or stash them first, then run the queue again. To see them: git status'
    }
}

Write-Say "  integration copy : $RepoRoot"
Write-Say "  branch           : $IntegrationBranch"
Write-Say "  verify scope     : $Scope"

# ---------------------------------------------------------------------------
# One queue, one writer. Two of these running in one tree would be resetting
# each other's merges, so take a lock. It lives in .ralph/ because that is
# gitignored and per-machine - a lock is about this checkout, not the project.
# ---------------------------------------------------------------------------

if (-not $PlanOnly) {
    $lockDir = Split-Path -Parent $LockPath
    if (-not (Test-Path -LiteralPath $lockDir)) {
        New-Item -ItemType Directory -Path $lockDir -Force | Out-Null
    }

    if (Test-Path -LiteralPath $LockPath) {
        $holderPid = $null
        try {
            $holder = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
            $holderPid = Get-Prop $holder 'pid'
        }
        catch { $holderPid = $null }

        $alive = $false
        if ($holderPid) {
            $alive = $null -ne (Get-Process -Id $holderPid -ErrorAction SilentlyContinue)
        }

        if ($alive) {
            Stop-WithProblem 'lock' "Another merge queue is already running here (process $holderPid)." `
                'Wait for it to finish. Only one thing may land at a time, which is the point of the queue.'
        }
        # A lock whose process is gone was left by a crash. Saying so is better than either
        # silently reusing it or making a human delete a file they did not know about.
        Write-Warn "clearing a stale lock left by process $holderPid, which is no longer running"
        Remove-Item -LiteralPath $LockPath -Force
    }

    @{ pid = $PID; host = [System.Net.Dns]::GetHostName(); startedUtc = $started.ToUniversalTime().ToString('o') } |
        ConvertTo-Json -Compress | Set-Content -LiteralPath $LockPath -Encoding utf8NoBOM
    $lockHeld = $true
}

# ---------------------------------------------------------------------------
# Discovery. Candidates come from git branches and bd, and from nothing else -
# the spec's rule, so that a dispatcher crash loses no queue state.
# ---------------------------------------------------------------------------

Write-Stage 'Finding lanes that are ready to land'

# A trailing slash, not a glob. for-each-ref matches patterns with FNM_PATHNAME, so '*' does
# NOT cross a '/' - 'refs/heads/ralph/lane-*' matches nothing at all, silently, and the queue
# just reports an empty board. (Measured: it needs 'ralph/lane-*/*' to work as a glob.) The
# prefix form matches everything under refs/heads/ralph/ whatever its depth, and the regex
# below is what decides the shape, so there is one place to be wrong instead of two.
$refs = @((Invoke-Git -In $RepoRoot for-each-ref --format='%(refname:short)%09%(committerdate:unix)' 'refs/heads/ralph/').Output -split "`n" | Where-Object { $_ })

$candidates = [System.Collections.Generic.List[object]]::new()
foreach ($ref in $refs) {
    $parts = $ref -split "`t"
    $branch = $parts[0]
    $finishedAt = if ($parts.Count -gt 1) { [int64]$parts[1] } else { 0 }

    if ($branch -notmatch '^ralph/lane-(\d+)/(.+)$') {
        $skipped.Add(@{ branch = $branch; why = 'branch name does not look like ralph/lane-<n>/<bead-id>' })
        continue
    }
    $laneNumber = [int]$Matches[1]
    $beadId = $Matches[2]

    if ($Bead -and ($Bead -cnotcontains $beadId)) { continue }

    $issue = Get-BeadJson -Id $beadId
    if ($null -eq $issue) {
        $skipped.Add(@{ branch = $branch; bead = $beadId; why = 'bd does not know this bead' })
        continue
    }

    $status = Get-Prop $issue 'status'
    if ($status -ceq 'closed') {
        $skipped.Add(@{ branch = $branch; bead = $beadId; why = 'bead is already closed - it probably landed already' })
        continue
    }

    $laneState = Get-Prop (Get-Prop $issue 'metadata') 'ralphLaneState'
    if ($laneState -cne 'ready-to-land') {
        $shown = if ($laneState) { "'$laneState'" } else { 'not set' }
        $skipped.Add(@{ branch = $branch; bead = $beadId; why = "lane state is $shown, not 'ready-to-land'" })
        continue
    }

    $candidates.Add([pscustomobject]@{
        Branch = $branch; Bead = $beadId; Lane = $laneNumber
        FinishedAt = $finishedAt; Title = (Get-Prop $issue 'title')
    })
}

# "In the order lanes finish" - and the tip commit's date IS when the lane finished, so the
# ordering needs no bookkeeping of its own and cannot drift out of step with the branches.
$queue = @($candidates | Sort-Object FinishedAt)

if ($queue.Count -eq 0) {
    Write-Say '  Nothing is ready to land.'
    foreach ($s in $skipped) { Write-Say "    skipped $($s.branch): $($s.why)" }
    if (-not $Json) {
        Write-Say "`n  A lane marks itself ready with: bd update <bead> --set-metadata ralphLaneState=ready-to-land" 'Yellow'
    }
    Remove-Lock
    if ($Json) {
        Write-Output (@{
            schema = 1; result = 'NOOP'; integrationBranch = $IntegrationBranch; scope = $Scope
            landed = @(); quarantined = @(); returned = @(); skipped = @($skipped)
            durationSec = [int]((Get-Date) - $started).TotalSeconds
        } | ConvertTo-Json -Compress -Depth 5)
    }
    exit 0
}

Write-Say "  $($queue.Count) lane(s) ready, in landing order:"
foreach ($c in $queue) {
    Write-Say "    lane $($c.Lane)  $($c.Bead)  $($c.Branch)"
}
foreach ($s in $skipped) { Write-Say "    skipped $($s.branch): $($s.why)" 'DarkGray' }

if ($PlanOnly) {
    Write-Say "`nPlan only - nothing was merged, verified or closed." 'Yellow'
    Remove-Lock
    if ($Json) {
        Write-Output (@{
            schema = 1; result = 'PLAN'; integrationBranch = $IntegrationBranch; scope = $Scope
            queue = @($queue | ForEach-Object { @{ lane = $_.Lane; bead = $_.Bead; branch = $_.Branch } })
            skipped = @($skipped)
        } | ConvertTo-Json -Compress -Depth 5)
    }
    exit 0
}

# ---------------------------------------------------------------------------
# Landing, one candidate at a time.
# ---------------------------------------------------------------------------

$pwshPath = Get-PwshPath

# Safety net for anything this script did not anticipate. Every handled failure already resets
# the integration branch, but a bug in the queue itself - and there was one, found by the test
# fixture - crashes AFTER the fast-forward and leaves the integration branch holding a merge
# that was never verified. The next person to run anything gets a tree that fails for reasons
# unrelated to whatever they are doing. This turns that into a reset and a clear report.
trap {
    if ($SafeSha) {
        Write-Host "`n  The merge queue hit an unexpected error mid-landing." -ForegroundColor Red
        Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
        & git -C $RepoRoot reset --hard $SafeSha *> $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  Integration was reset to $SafeSha, so the tree is where it started." -ForegroundColor Yellow
        }
        else {
            Write-Host "  COULD NOT reset the integration branch. Run this before anything else:" -ForegroundColor Red
            Write-Host "    git -C `"$RepoRoot`" reset --hard $SafeSha" -ForegroundColor Red
        }
    }
    Remove-Lock
    exit 1
}

foreach ($candidate in $queue) {
    $beadId = $candidate.Bead
    $branch = $candidate.Branch
    $laneNumber = $candidate.Lane

    Write-Stage "Landing $beadId from lane $laneNumber"

    $preMergeSha = (Invoke-Git -In $RepoRoot rev-parse HEAD).Output
    $SafeSha = $preMergeSha
    Write-Say "  pre-merge commit : $($preMergeSha.Substring(0, 12))"

    # --- Gate: does this branch actually carry a changelog entry? -----------
    # Checked before anything is merged or built, because a missing entry costs a full verify
    # to discover otherwise. The lane authors this entry, not the queue: only the lane knows
    # what changed. The queue's job is to refuse work that skipped it.
    $diffResult = Invoke-Git -In $RepoRoot diff --name-only "$preMergeSha...$branch"
    if ($diffResult.ExitCode -ne 0) {
        $why = "could not diff the lane branch against the integration head - $($diffResult.Output)"
        Write-Say "  Returned: $why" 'Red'
        $returned.Add(@{ bead = $beadId; lane = $laneNumber; why = $why })
        Invoke-Bd update $beadId --set-metadata ralphLaneState=returned --append-notes "Merge queue could not diff $branch against $IntegrationBranch. $($diffResult.Output)" | Out-Null
        Add-JournalEntry -BeadId $beadId -Outcome 'returned' -LaneNumber $laneNumber -Commit $preMergeSha -Detail $why
        continue
    }

    $changedPaths = @($diffResult.Output -split "`n" | Where-Object { $_ })

    if ($changedPaths.Count -eq 0) {
        $why = 'the lane branch changes nothing relative to the integration head'
        Write-Say "  Returned: $why" 'Red'
        $returned.Add(@{ bead = $beadId; lane = $laneNumber; why = $why })
        Invoke-Bd update $beadId --status open --set-metadata ralphLaneState=returned --append-notes "Merge queue found nothing to land on $branch - it is identical to $IntegrationBranch. The work was never committed, or was committed somewhere else." | Out-Null
        Add-JournalEntry -BeadId $beadId -Outcome 'returned' -LaneNumber $laneNumber -Commit $preMergeSha -Detail $why
        continue
    }

    if ($changedPaths -cnotcontains $ChangelogRelative) {
        $why = "the branch has no $ChangelogRelative entry"
        Write-Say "  Returned: $why" 'Red'
        $returned.Add(@{ bead = $beadId; lane = $laneNumber; why = $why })
        Invoke-Bd update $beadId --status open --set-metadata ralphLaneState=returned --append-notes "Merge queue refused to land $branch : it does not touch $ChangelogRelative. Every change needs an entry under [Unreleased] (CLAUDE.md). Add one on the lane branch, then mark it ready again with: bd update $beadId --set-metadata ralphLaneState=ready-to-land" | Out-Null
        Add-JournalEntry -BeadId $beadId -Outcome 'returned' -LaneNumber $laneNumber -Commit $preMergeSha -Detail $why
        continue
    }

    $touchesAndroid = @($changedPaths | Where-Object { $_.StartsWith('remex.android/') -or $_.StartsWith('remex.core/') }).Count -gt 0
    Write-Say "  $($changedPaths.Count) file(s) changed$(if ($touchesAndroid) { ', including Android or shared core' })"

    # --- Rebase onto the integration head -----------------------------------
    # Skipped entirely when the integration head is already an ancestor, which is the case for
    # the first landing of any batch. That matters beyond speed: the fast path needs no
    # worktree, so a lane whose worktree was reaped early can still land.
    $ancestor = Invoke-Git -In $RepoRoot merge-base --is-ancestor $preMergeSha $branch
    if ($ancestor.ExitCode -ne 0) {
        Write-Say '  Rebasing the lane branch onto the integration head'

        $worktrees = Get-WorktreeByBranch
        $rebaseDir = $null
        $tempWorktree = $null

        if ($worktrees.ContainsKey($branch)) {
            $rebaseDir = $worktrees[$branch]
        }
        else {
            # The branch exists with no worktree - reaped early, or the lanes directory was
            # removed. A detached scratch worktree rebases it without disturbing this tree.
            $tempWorktree = Join-Path $LanesRoot ".landing-$laneNumber"
            if (Test-Path -LiteralPath $tempWorktree) {
                $why = "the scratch worktree path $tempWorktree already exists"
                Write-Say "  Returned: $why" 'Red'
                $returned.Add(@{ bead = $beadId; lane = $laneNumber; why = $why })
                Invoke-Bd update $beadId --set-metadata ralphLaneState=returned --append-notes "Merge queue needed a scratch worktree at $tempWorktree but the path is in use. Remove it with: git -C `"$RepoRoot`" worktree remove `"$tempWorktree`"" | Out-Null
                Add-JournalEntry -BeadId $beadId -Outcome 'returned' -LaneNumber $laneNumber -Commit $preMergeSha -Detail $why
                continue
            }
            $addResult = Invoke-Git -In $RepoRoot worktree add --detach $tempWorktree $branch
            if ($addResult.ExitCode -ne 0) {
                $why = "could not create a scratch worktree to rebase in - $($addResult.Output)"
                Write-Say "  Returned: $why" 'Red'
                $returned.Add(@{ bead = $beadId; lane = $laneNumber; why = $why })
                Invoke-Bd update $beadId --set-metadata ralphLaneState=returned --append-notes "Merge queue could not create a scratch worktree for $branch. $($addResult.Output)" | Out-Null
                Add-JournalEntry -BeadId $beadId -Outcome 'returned' -LaneNumber $laneNumber -Commit $preMergeSha -Detail $why
                continue
            }
            $rebaseDir = $tempWorktree
        }

        $rebase = Invoke-Git -In $rebaseDir rebase $IntegrationBranch
        if ($rebase.ExitCode -ne 0) {
            # Capture the conflicting paths BEFORE aborting - the abort is what erases them.
            $conflicts = @((Invoke-Git -In $rebaseDir diff --name-only --diff-filter=U).Output -split "`n" | Where-Object { $_ })
            Invoke-Git -In $rebaseDir rebase --abort | Out-Null
            if ($tempWorktree) { Invoke-Git -In $RepoRoot worktree remove $tempWorktree | Out-Null }

            $conflictList = if ($conflicts.Count -gt 0) { $conflicts -join ', ' } else { 'unknown' }
            $why = "rebase conflicts in: $conflictList"
            Write-Say "  Returned: $why" 'Red'
            Write-Say '  The branch is kept. A merge resolution is a code change nobody reviewed, so the queue never invents one.' 'Yellow'
            $returned.Add(@{ bead = $beadId; lane = $laneNumber; why = $why; conflicts = @($conflicts) })
            Invoke-Bd update $beadId --status open --set-metadata ralphLaneState=returned --append-notes "Merge queue could not rebase $branch onto $IntegrationBranch. Conflicting paths: $conflictList. The branch is kept as evidence. Resolve it on the lane branch, re-verify there, then mark it ready again with: bd update $beadId --set-metadata ralphLaneState=ready-to-land" | Out-Null
            Add-JournalEntry -BeadId $beadId -Outcome 'returned' -LaneNumber $laneNumber -Commit $preMergeSha -Detail $why
            continue
        }

        if ($tempWorktree) {
            # A detached rebase moves HEAD, not the branch, so the ref needs moving by hand.
            $rebasedSha = (Invoke-Git -In $tempWorktree rev-parse HEAD).Output
            Invoke-Git -In $RepoRoot branch -f $branch $rebasedSha | Out-Null
            Invoke-Git -In $RepoRoot worktree remove $tempWorktree | Out-Null
        }
        Write-Say '  Rebased cleanly.'
    }
    else {
        Write-Say '  Already based on the integration head, no rebase needed.'
    }

    # --- Fast-forward --------------------------------------------------------
    $merge = Invoke-Git -In $RepoRoot merge --ff-only $branch
    if ($merge.ExitCode -ne 0) {
        $why = "fast-forward refused after a clean rebase - $($merge.Output)"
        Write-Say "  Returned: $why" 'Red'
        $returned.Add(@{ bead = $beadId; lane = $laneNumber; why = $why })
        Invoke-Bd update $beadId --status open --set-metadata ralphLaneState=returned --append-notes "Merge queue could not fast-forward $IntegrationBranch to $branch even though the rebase succeeded. $($merge.Output)" | Out-Null
        Add-JournalEntry -BeadId $beadId -Outcome 'returned' -LaneNumber $laneNumber -Commit $preMergeSha -Detail $why
        continue
    }
    $mergedSha = (Invoke-Git -In $RepoRoot rev-parse HEAD).Output
    Write-Say "  Integration is now at $($mergedSha.Substring(0, 12))"

    # --- Verify in the integration tree --------------------------------------
    # Set the pre-merge receipt aside first. A failing verify overwrites it, and the quarantine
    # path then resets the tree to a state that IS proven green while leaving behind a receipt
    # saying FAIL - so -Check reports STALE for a tree that is perfectly fine, and the proof is
    # lost for no reason. Putting the old receipt back after the reset is not a fudge: it
    # describes exactly the commit the tree has been returned to.
    if (Test-Path -LiteralPath $ReceiptPath) {
        Copy-Item -LiteralPath $ReceiptPath -Destination $ReceiptBackup -Force
    }
    elseif (Test-Path -LiteralPath $ReceiptBackup) {
        Remove-Item -LiteralPath $ReceiptBackup -Force
    }

    Write-Say "  Running ./scripts/verify.ps1 -Scope $Scope here. This is the receipt that counts."

    $verifyRun = Invoke-VerifyProcess -Arguments @('-Scope', $Scope, '-Json')
    $verifyExit = $verifyRun.ExitCode
    $verifyText = $verifyRun.Text
    $receipt = $verifyRun.Receipt

    $verifyPassed = ($verifyExit -eq 0) -and ($null -ne $receipt) -and ((Get-Prop $receipt 'result') -ceq 'PASS')
    $failureDetail = ''

    if ($verifyPassed) {
        # CLAUDE.md's rule: a bead is not done until -Check says VALID. Run it as a separate
        # process against the receipt just written, because "the tests passed" is a claim and a
        # matching fingerprint is a fact. It also catches anything that edited the tree while
        # the build was running.
        $checkRun = Invoke-VerifyProcess -Arguments @('-Check', '-Json')
        $checkExit = $checkRun.ExitCode
        $checkObj = $checkRun.Receipt
        $checkVerdict = if ($checkObj) { Get-Prop $checkObj 'check' } else { $null }

        if (($checkExit -ne 0) -or ($checkVerdict -cne 'VALID')) {
            $verifyPassed = $false
            $failureDetail = "verify passed but -Check reported '$checkVerdict'. Something changed the working copy between the build and the check."
        }
    }
    else {
        # The @() goes OUTSIDE the if, not inside it. A block that emits a one-element array
        # has it enumerated back into a scalar on the way out, so the inner-@() spelling of
        # this line hands back a bare string the moment there is exactly one problem - which
        # is the commonest failure there is - and .Count then throws under StrictMode.
        $problems = @(if ($receipt) { Get-Prop $receipt 'problems' })
        if ($problems.Count -gt 0) {
            $failureDetail = ($problems -join '; ')
        }
        elseif ($verifyText) {
            $tail = @($verifyText -split "`n" | Where-Object { $_ } | Select-Object -Last 15)
            $failureDetail = ($tail -join "`n")
        }
        else {
            $failureDetail = "verify.ps1 exited $verifyExit and produced no output."
        }
    }

    if (-not $verifyPassed) {
        # --- Quarantine ------------------------------------------------------
        Write-Say '  FAIL - quarantining.' 'Red'
        Write-Say "  $failureDetail" 'Red'

        # Say what is about to be discarded rather than discarding it quietly. The tree was
        # clean at preflight and everything written since has been committed, so this should
        # be empty; if it is not, that is worth seeing before it goes.
        $strays = @((Invoke-Git -In $RepoRoot status --porcelain).Output -split "`n" | Where-Object { $_ })
        if ($strays.Count -gt 0) {
            Write-Warn "the reset will discard $($strays.Count) uncommitted change(s) left behind by the build:"
            foreach ($s in $strays) { Write-Warn "    $s" }
        }

        $reset = Invoke-Git -In $RepoRoot reset --hard $preMergeSha
        if ($reset.ExitCode -ne 0) {
            Stop-WithProblem 'quarantine' "Could not reset $IntegrationBranch back to $preMergeSha after a failed landing. $($reset.Output)" `
                "The integration branch is holding a merge that does not build. Reset it by hand before anything else lands: git -C `"$RepoRoot`" reset --hard $preMergeSha"
        }
        if (Test-Path -LiteralPath $ReceiptBackup) {
            Move-Item -LiteralPath $ReceiptBackup -Destination $ReceiptPath -Force
        }
        elseif (Test-Path -LiteralPath $ReceiptPath) {
            # There was no receipt before this landing, so leaving the failing one behind would
            # invent a history the tree does not have.
            Remove-Item -LiteralPath $ReceiptPath -Force
        }

        Write-Say "  Integration reset to $($preMergeSha.Substring(0, 12)). The lane branch and its worktree are kept as evidence."

        $note = "Merge queue quarantined this bead. It verified green in its own lane and FAILED when merged into $IntegrationBranch at $mergedSha, so this is an interaction with something that landed since, not a lane problem. Integration was reset to $preMergeSha. Failure: $failureDetail"
        Invoke-Bd update $beadId --status open --set-metadata ralphLaneState=quarantined --append-notes $note | Out-Null

        $quarantined.Add(@{ bead = $beadId; lane = $laneNumber; branch = $branch; why = $failureDetail })
        Add-JournalEntry -BeadId $beadId -Outcome 'quarantined' -LaneNumber $laneNumber -Commit $preMergeSha -Detail $failureDetail

        # No automatic retry. A lane that was green alone and fails on integration has found a
        # real interaction, and that deserves a fresh attempt with the failure in hand.
        continue
    }

    # --- Landed --------------------------------------------------------------
    $sourceHash = Get-Prop $receipt 'sourceHash'
    $testsPassed = Get-Prop $receipt 'testsPassed'
    $testsRun = Get-Prop $receipt 'testsRun'
    Write-Say "  PASS - $testsPassed of $testsRun tests, receipt VALID." 'Green'

    if ($touchesAndroid) { $androidTouched = $true }

    $closeReason = "Landed on $IntegrationBranch at $mergedSha from lane $laneNumber ($branch). Integration-tree verify.ps1 -Scope $Scope PASSED ($testsPassed of $testsRun tests) and -Check reported VALID against source fingerprint $sourceHash."
    Invoke-Bd update $beadId --unset-metadata ralphLaneState --unset-metadata ralphLane | Out-Null
    $close = Invoke-Bd close $beadId --reason $closeReason
    if ($close.ExitCode -ne 0) {
        Write-Warn "the bead did not close cleanly - $($close.Output)"
    }

    Add-JournalEntry -BeadId $beadId -Outcome 'landed' -LaneNumber $laneNumber `
        -SourceHash $sourceHash -Commit $mergedSha

    # Settled. From here on, resetting to the pre-merge commit would undo a bead that is
    # already closed, so the safety net must stop pointing at it.
    $SafeSha = $null

    # --- Release the lane ----------------------------------------------------
    if ($KeepLanes) {
        Write-Warn "keeping the lane worktree and branch (-KeepLanes)"
    }
    else {
        $worktrees = Get-WorktreeByBranch
        if ($worktrees.ContainsKey($branch)) {
            # Never --force. If git refuses because the lane holds uncommitted work, that work
            # is evidence of something the lane did not commit, and destroying it to tidy up
            # would be the single most expensive kind of tidying.
            $remove = Invoke-Git -In $RepoRoot worktree remove $worktrees[$branch]
            if ($remove.ExitCode -ne 0) {
                Write-Warn "could not remove the lane worktree at $($worktrees[$branch]) - $($remove.Output)"
                Write-Warn 'the branch is kept too, since git will not delete a branch that is checked out'
            }
        }

        $worktrees = Get-WorktreeByBranch
        if (-not $worktrees.ContainsKey($branch)) {
            # -d, never -D. It only deletes a branch that is fully merged, so if it refuses,
            # something is wrong with the belief that this landed.
            $del = Invoke-Git -In $RepoRoot branch -d $branch
            if ($del.ExitCode -ne 0) {
                Write-Warn "could not delete $branch - $($del.Output)"
            }
            else {
                Write-Say "  Lane $laneNumber released."
            }
        }
    }

    $landed.Add(@{ bead = $beadId; lane = $laneNumber; commit = $mergedSha; sourceHash = $sourceHash; testsPassed = $testsPassed; testsRun = $testsRun })
}

# ---------------------------------------------------------------------------
# Batch-level Android sweep. Only reachable when the operator opted out of the
# default scope: it is the spec's original trade, kept for the case it was
# written for, and it says plainly that a failure here belongs to the batch.
# ---------------------------------------------------------------------------

# Nothing is in flight now, so the safety net has nothing to fall back to and must not keep
# holding the last candidate's pre-merge commit - that would undo a landing.
$SafeSha = $null

# The spare copy only means anything mid-landing; left behind it is just a confusing second
# receipt sitting next to the real one.
if (Test-Path -LiteralPath $ReceiptBackup) { Remove-Item -LiteralPath $ReceiptBackup -Force }

$batchSweep = $null
if ($Scope -ceq 'dotnet' -and $androidTouched -and $landed.Count -gt 0) {
    Write-Stage 'Android sweep for this batch'
    Write-Say '  A landing touched remex.android/ or remex.core/ and the per-landing scope was dotnet,'
    Write-Say '  so the Android suite has not run yet. Running -Scope all once over everything that landed.'

    $sweepRun = Invoke-VerifyProcess -Arguments @('-Scope', 'all', '-Json')
    $sweepExit = $sweepRun.ExitCode
    $sweepReceipt = $sweepRun.Receipt

    if ($sweepExit -eq 0 -and $sweepReceipt -and ((Get-Prop $sweepReceipt 'result') -ceq 'PASS')) {
        $batchSweep = 'PASS'
        Write-Say "  PASS - $(Get-Prop $sweepReceipt 'testsPassed') of $(Get-Prop $sweepReceipt 'testsRun') tests." 'Green'
    }
    else {
        $batchSweep = 'FAIL'
        $sweepProblems = if ($sweepReceipt) { @(Get-Prop $sweepReceipt 'problems') -join '; ' } else { "verify.ps1 exited $sweepExit" }
        Write-Say "  FAIL - $sweepProblems" 'Red'
        Write-Say '  This failure belongs to the batch, not to one commit - that is the cost of -Scope dotnet per landing.' 'Yellow'
        Write-Say "  The landed beads that touched Android are: $((@($landed | ForEach-Object { $_.bead })) -join ', ')" 'Yellow'
        Add-JournalEntry -BeadId '(batch)' -Outcome 'android-sweep-failed' -LaneNumber 0 `
            -Commit (Invoke-Git -In $RepoRoot rev-parse HEAD).Output -Detail $sweepProblems
    }
}

# ---------------------------------------------------------------------------
# Summary.
# ---------------------------------------------------------------------------

Remove-Lock

$failedAnything = ($quarantined.Count -gt 0) -or ($returned.Count -gt 0) -or ($batchSweep -ceq 'FAIL')
$result = if ($failedAnything) { 'FAIL' } else { 'PASS' }

if ($Json) {
    Write-Output (@{
        schema            = 1
        result            = $result
        integrationBranch = $IntegrationBranch
        scope             = $Scope
        landed            = @($landed)
        quarantined       = @($quarantined)
        returned          = @($returned)
        skipped           = @($skipped)
        androidSweep      = $batchSweep
        durationSec       = [int]((Get-Date) - $started).TotalSeconds
    } | ConvertTo-Json -Compress -Depth 6)
}
else {
    Write-Host "`nMerge queue finished in $([int]((Get-Date) - $started).TotalSeconds)s." -ForegroundColor Cyan
    Write-Host "  landed      : $($landed.Count)" -ForegroundColor $(if ($landed.Count -gt 0) { 'Green' } else { 'Gray' })
    foreach ($l in $landed) { Write-Host "      $($l.bead) -> $($l.commit.Substring(0, 12))" -ForegroundColor Green }
    if ($quarantined.Count -gt 0) {
        Write-Host "  quarantined : $($quarantined.Count)" -ForegroundColor Red
        foreach ($q in $quarantined) { Write-Host "      $($q.bead) - $($q.why)" -ForegroundColor Red }
        Write-Host '  Those beads are open again with the failure attached, and their branches are kept.' -ForegroundColor Yellow
    }
    if ($returned.Count -gt 0) {
        Write-Host "  returned    : $($returned.Count)" -ForegroundColor Yellow
        foreach ($r in $returned) { Write-Host "      $($r.bead) - $($r.why)" -ForegroundColor Yellow }
    }
    if ($failedAnything) {
        Write-Host "`n  What to do: fix each one on its own lane branch, re-verify there, then mark it ready" -ForegroundColor Yellow
        Write-Host '  again with: bd update <bead> --set-metadata ralphLaneState=ready-to-land' -ForegroundColor Yellow
    }
}

exit $(if ($failedAnything) { 1 } else { 0 })

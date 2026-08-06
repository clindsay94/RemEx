#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds RemEx from clean, runs the tests, and writes a receipt proving it actually happened.

.DESCRIPTION
    This is the one accepted way to verify that a piece of work is finished. It exists because
    "the tests passed" turned out to be an unreliable claim: tests were passing against builds
    that predated the change, edits were reported as applied without landing, and nothing in the
    output distinguished a real green run from a stale one.

    The fix is a RECEIPT. Every run writes a small JSON file recording what was verified and,
    crucially, a fingerprint of the source code it was verified against. The fingerprint is a
    SHA-256 over the path and content of every source file in scope. Change any one of them by
    a single character and the fingerprint changes.

    That turns a judgement call into a check anybody can run:

        ./scripts/verify.ps1 -Check

    If the code has moved on since the receipt was written, that prints STALE and exits nonzero.
    A receipt is not "a run that passed once" - it is "a run that passed against exactly the code
    that is on disk right now". Those are different claims and only the second one is worth
    anything.

    The fingerprint covers tracked files AND new files that are not gitignored. Tracked-only
    would let you add an entire new source file without the fingerprint noticing, which is the
    same blind spot in a different costume.

    The fingerprint is also taken a second time after the tests finish. If it moved during the
    run, something edited the source mid-flight and the result means nothing, so the receipt is
    marked FAIL rather than PASS. This matters here because this working copy can be shared with
    another session.

.PARAMETER Scope
    What to verify. 'dotnet' (default) builds and tests the .NET solution. 'android' runs the
    Android release unit tests. 'all' does both.

.PARAMETER NoClean
    Skip the clean step and reuse existing build output. Faster, but you are giving up the
    guarantee this script exists to provide. Only use it while iterating, never to close work.

.PARAMETER Check
    Do not build anything. Read the existing receipt, recompute the fingerprint, and report
    whether the receipt still describes the code on disk. Exits 0 for VALID, 1 for STALE or if
    there is no receipt.

.PARAMETER Receipt
    Where to read/write the receipt. Defaults to .ralph/verify-receipt.json, which is
    deliberately gitignored - a receipt describes one machine's working copy at one moment and
    is meaningless anywhere else.

.PARAMETER SkipLocalization
    Skip the translation check. It is the slowest single step because it walks git history.

.PARAMETER Json
    Print only the receipt as a single line of JSON and nothing else. For scripts and loops.

.EXAMPLE
    ./scripts/verify.ps1
    Clean build plus full .NET test suite, with friendly progress output.

.EXAMPLE
    ./scripts/verify.ps1 -Scope all -Json
    Verify everything, print one line of JSON. This is what an automated loop should call.

.EXAMPLE
    ./scripts/verify.ps1 -Check
    Ask whether the last receipt still describes the current code. Cheap, no build.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidateSet('dotnet', 'android', 'all')]
    [string]$Scope = 'dotnet',

    [Parameter(Mandatory = $false)]
    [switch]$NoClean,

    [Parameter(Mandatory = $false)]
    [switch]$Check,

    [Parameter(Mandatory = $false)]
    [string]$Receipt,

    [Parameter(Mandatory = $false)]
    [switch]$SkipLocalization,

    [Parameter(Mandatory = $false)]
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$IsWin = $IsWindows -or ($env:OS -eq 'Windows_NT')

if (-not $Receipt) {
    $Receipt = Join-Path $RepoRoot '.ralph' 'verify-receipt.json'
}

# ---------------------------------------------------------------------------
# Output helpers. Everything human-facing goes through these so that -Json can
# silence the lot and leave a single parseable line on stdout.
# ---------------------------------------------------------------------------

function Write-Say {
    param([string]$Text, [string]$Colour = 'Gray')
    if (-not $Json) { Write-Host $Text -ForegroundColor $Colour }
}

function Write-Stage {
    param([string]$Text)
    if (-not $Json) { Write-Host "`n$Text" -ForegroundColor Cyan }
}

function Write-Problem {
    param([string]$Text, [string]$WhatToDo)
    if (-not $Json) {
        Write-Host "`n  Problem: $Text" -ForegroundColor Red
        if ($WhatToDo) { Write-Host "  What to do: $WhatToDo" -ForegroundColor Yellow }
    }
}

# ---------------------------------------------------------------------------
# The fingerprint.
#
# git ls-files -c -o --exclude-standard = tracked files plus untracked files that
# are not gitignored. Both halves matter: tracked-only misses a newly added source
# file, and including ignored files would fold build output into the fingerprint,
# which would make it change on every build and prove nothing.
# ---------------------------------------------------------------------------

function Get-Platform {
    # Recorded in the receipt because this repo shares artifacts/ between the Windows
    # build and the WSL/Linux one (see CLAUDE.md). A Linux run overwrites the build
    # output Windows was using. The fingerprint covers source, not build output, so
    # without this a Windows -Check would happily call a receipt valid while the
    # artifacts on disk were left behind by the other platform.
    if ($IsWin) { return 'windows' }
    elseif ($IsMacOS) { return 'macos' }
    else { return 'linux' }
}

function Get-ScopePatterns {
    param([string]$ForScope)

    $dotnet = @(
        '*.cs', '*.csproj', '*.props', '*.targets', '*.sln', '*.resx',
        '*.editorconfig', '*.manifest', '*.json'
    )
    $android = @(
        'remex.android/*', 'remex.core/*'
    )

    switch ($ForScope) {
        'dotnet'  { return $dotnet }
        'android' { return $android }
        'all'     { return ($dotnet + $android) }
    }
}

function Get-SourceFingerprint {
    param([string]$ForScope)

    Push-Location $RepoRoot
    try {
        $patterns = Get-ScopePatterns -ForScope $ForScope
        $files = @(git ls-files -c -o --exclude-standard -- $patterns)
        if ($LASTEXITCODE -ne 0) {
            throw "git ls-files failed. Is this a git working copy?"
        }

        # Sort explicitly. git's ordering is stable in practice but the fingerprint
        # must not depend on that, or the same tree could hash two different ways.
        $files = $files | Where-Object { $_ } | Sort-Object -CaseSensitive

        $hasher = [System.Security.Cryptography.IncrementalHash]::CreateHash(
            [System.Security.Cryptography.HashAlgorithmName]::SHA256)
        try {
            $counted = 0
            foreach ($rel in $files) {
                # The path goes into the hash as well as the content, so that renaming a
                # file changes the fingerprint even when its bytes are identical.
                $hasher.AppendData([System.Text.Encoding]::UTF8.GetBytes("$rel`n"))

                $full = Join-Path $RepoRoot $rel
                if (Test-Path -LiteralPath $full -PathType Leaf) {
                    $hasher.AppendData([System.IO.File]::ReadAllBytes($full))
                    $counted++
                }
            }
            $bytes = $hasher.GetHashAndReset()
            return @{
                Hash  = [System.Convert]::ToHexString($bytes).ToLowerInvariant()
                Files = $counted
            }
        }
        finally {
            $hasher.Dispose()
        }
    }
    finally {
        Pop-Location
    }
}

# ---------------------------------------------------------------------------
# -Check: is the existing receipt still true?
# ---------------------------------------------------------------------------

if ($Check) {
    if (-not (Test-Path -LiteralPath $Receipt)) {
        Write-Problem "There is no receipt at $Receipt." "Run ./scripts/verify.ps1 to create one."
        if ($Json) { Write-Output '{"schema":1,"check":"MISSING"}' }
        exit 1
    }

    $prior = Get-Content -LiteralPath $Receipt -Raw | ConvertFrom-Json
    $now = Get-SourceFingerprint -ForScope $prior.scope
    $herePlatform = Get-Platform
    # A receipt written on the other platform does not describe this machine's build output,
    # because artifacts/ is shared between the Windows and WSL/Linux builds.
    $priorPlatform = if ($prior.PSObject.Properties.Name -contains 'platform') { $prior.platform } else { 'unknown' }
    $samePlatform = ($priorPlatform -ceq $herePlatform)
    $valid = ($now.Hash -ceq $prior.sourceHash) -and ($prior.result -ceq 'PASS') -and $samePlatform

    if ($Json) {
        $verdict = if ($valid) { 'VALID' } else { 'STALE' }
        Write-Output (@{
            schema = 1; check = $verdict; scope = $prior.scope
            receiptHash = $prior.sourceHash; currentHash = $now.Hash
            receiptPlatform = $priorPlatform; currentPlatform = $herePlatform
            receiptResult = $prior.result; timestampUtc = $prior.timestampUtc
        } | ConvertTo-Json -Compress)
    }
    elseif ($valid) {
        Write-Say "VALID - the receipt from $($prior.timestampUtc) still describes the code on disk." 'Green'
        Write-Say "  $($prior.testsPassed) of $($prior.testsRun) tests passed, scope '$($prior.scope)'."
    }
    else {
        if (-not $samePlatform) {
            Write-Problem "This receipt was written on $priorPlatform, but you are on $herePlatform." `
                "Run ./scripts/verify.ps1 again here. The build output in artifacts/ is shared between the Windows and Linux builds, so the other platform's run has overwritten it."
        }
        elseif ($prior.result -cne 'PASS') {
            Write-Problem "The last verification did not pass (result: $($prior.result))." `
                "Fix the failure, then run ./scripts/verify.ps1 again."
        }
        else {
            Write-Problem "STALE - the code has changed since this receipt was written." `
                "Run ./scripts/verify.ps1 again. The previous result no longer applies."
            Write-Say "  receipt fingerprint: $($prior.sourceHash)"
            Write-Say "  current fingerprint: $($now.Hash)"
        }
    }

    exit $(if ($valid) { 0 } else { 1 })
}

# ---------------------------------------------------------------------------
# Verification proper.
# ---------------------------------------------------------------------------

$started = Get-Date
$problems = [System.Collections.Generic.List[string]]::new()
$testsRun = 0; $testsPassed = 0; $testsFailed = 0; $testsSkipped = 0
$warnings = 0

Write-Stage "Fingerprinting the source"
$before = Get-SourceFingerprint -ForScope $Scope
Write-Say "  $($before.Files) files, fingerprint $($before.Hash.Substring(0,16))..."

$gitHead = (git -C $RepoRoot rev-parse --short HEAD 2>$null)
if ($LASTEXITCODE -ne 0) { $gitHead = 'unknown' }
$gitDirty = [bool](git -C $RepoRoot status --porcelain 2>$null)

# --- .NET ------------------------------------------------------------------

if ($Scope -in @('dotnet', 'all')) {
    $sln = Join-Path $RepoRoot 'Remex.sln'

    # The WSL/Linux .NET install commonly ships Microsoft.NETCore.App without
    # Microsoft.AspNetCore.App, which makes the agent test host fail to start with a
    # message about installing .NET. Publishing self-contained bundles the runtime into
    # the test output and needs no change to the machine. Detect rather than assume, so
    # a properly provisioned Linux box is not penalised.
    $extra = @()
    $runtimes = @(dotnet --list-runtimes 2>$null)
    if (-not ($runtimes -match 'Microsoft\.AspNetCore\.App')) {
        Write-Say "  ASP.NET Core runtime not found - building self-contained for this machine."
        $extra = @('-p:RuntimeIdentifier=linux-x64', '-p:SelfContained=true')
    }

    if (-not $NoClean) {
        Write-Stage "Cleaning previous .NET build output"
        dotnet clean $sln -c Release --nologo -v quiet 2>&1 | Out-Null
        Write-Say "  Done."
    }
    else {
        Write-Say "`n  Skipping clean (-NoClean). This result is weaker than a clean one." 'Yellow'
    }

    Write-Stage "Building the .NET solution"
    $buildLog = dotnet build $sln -c Release --no-incremental --nologo @extra 2>&1
    $buildOk = ($LASTEXITCODE -eq 0)
    $warnings = @($buildLog | Select-String -Pattern ': warning ' -SimpleMatch).Count

    if (-not $buildOk) {
        $problems.Add('dotnet build failed')
        Write-Problem "The .NET build failed." "Scroll up for the compiler errors, or run: dotnet build Remex.sln -c Release"
        $buildLog | Select-String -Pattern ': error ' -SimpleMatch |
            Select-Object -First 15 | ForEach-Object { Write-Say "    $_" 'Red' }
    }
    else {
        Write-Say "  Built successfully. $warnings warning(s)."

        Write-Stage "Running the .NET test suite"
        $trxDir = Join-Path $RepoRoot '.ralph' 'trx'
        if (Test-Path -LiteralPath $trxDir) { Remove-Item -LiteralPath $trxDir -Recurse -Force }
        New-Item -ItemType Directory -Path $trxDir -Force | Out-Null

        dotnet test $sln -c Release --no-build --nologo @extra `
            --logger 'trx' --results-directory $trxDir 2>&1 | Out-Null
        $testExit = $LASTEXITCODE

        $trxFiles = @(Get-ChildItem -LiteralPath $trxDir -Filter '*.trx' -ErrorAction SilentlyContinue)
        if ($trxFiles.Count -eq 0) {
            $problems.Add('no .NET test results produced')
            Write-Problem "The test run produced no results file." "Run: dotnet test Remex.sln -c Release"
        }
        foreach ($trx in $trxFiles) {
            try {
                [xml]$doc = Get-Content -LiteralPath $trx.FullName -Raw
                $c = $doc.TestRun.ResultSummary.Counters
                $testsRun += [int]$c.total
                $testsPassed += [int]$c.passed
                $testsFailed += [int]$c.failed
                # 'total minus executed' is the honest skipped count; a test that never ran
                # is not a test that passed, and rolling it into passed would inflate the number.
                $testsSkipped += ([int]$c.total - [int]$c.executed)
            }
            catch {
                $problems.Add("could not read test results from $($trx.Name)")
            }
        }

        if ($testExit -ne 0 -or $testsFailed -gt 0) {
            $problems.Add("$testsFailed .NET test(s) failed")
            Write-Problem "$testsFailed of $testsRun tests failed." `
                "Run: dotnet test Remex.sln -c Release --filter <name of a failing test>"
        }
        else {
            Write-Say "  $testsPassed of $testsRun tests passed."
        }
    }
}

# --- Android ---------------------------------------------------------------

if ($Scope -in @('android', 'all')) {
    Write-Stage "Running the Android release unit tests"
    $androidDir = Join-Path $RepoRoot 'remex.android'
    $gradlew = Join-Path $androidDir $(if ($IsWin) { 'gradlew.bat' } else { 'gradlew' })

    if (-not (Test-Path -LiteralPath $gradlew)) {
        $problems.Add('gradle wrapper not found')
        Write-Problem "Could not find the Gradle wrapper at $gradlew." `
            "Check that remex.android is complete, or run the Android build once via ./build-remex.ps1 -t android"
    }
    else {
        Push-Location $androidDir
        try {
            # Release variant only. Debug is never installed on device here, and only the
            # release variant runs the lintVitalRelease gate.
            # This task only exists because app/build.gradle.kts sets testBuildType =
            # "release"; AGP 9 builds a unit-test component for that variant and no other.
            $gradleArgs = @('testReleaseUnitTest', '--console=plain')
            if (-not $NoClean) { $gradleArgs = @('clean') + $gradleArgs }

            # Capture rather than discard. This used to pipe to Out-Null, so when the task
            # name was wrong Gradle's "Task 'testReleaseUnitTest' not found" went nowhere and
            # the only thing reported was "Android unit tests failed" - which reads like a
            # failing test and sent the reader looking in entirely the wrong place. Whatever
            # goes wrong here, the person running it should see what Gradle actually said.
            $gradleOutput = & $gradlew @gradleArgs 2>&1
            $gradleOk = ($LASTEXITCODE -eq 0)
        }
        finally {
            Pop-Location
        }

        $resultsDir = Join-Path $androidDir 'app' 'build' 'test-results' 'testReleaseUnitTest'
        $suites = @(Get-ChildItem -LiteralPath $resultsDir -Filter '*.xml' -ErrorAction SilentlyContinue)
        foreach ($suite in $suites) {
            try {
                [xml]$doc = Get-Content -LiteralPath $suite.FullName -Raw
                $ts = $doc.testsuite
                $total = [int]$ts.tests
                $failed = [int]$ts.failures + [int]$ts.errors
                $skipped = [int]$ts.skipped
                $testsRun += $total
                $testsFailed += $failed
                $testsSkipped += $skipped
                $testsPassed += ($total - $failed - $skipped)
            }
            catch {
                $problems.Add("could not read Android results from $($suite.Name)")
            }
        }

        if (-not $gradleOk) {
            # Distinguish "a test failed" from "the build never got as far as running tests".
            # They need completely different responses and the old message conflated them.
            $text = ($gradleOutput | Out-String)
            $noSuchTask = $text -match "Task '.*' not found"

            if ($noSuchTask) {
                $problems.Add('Android test task does not exist')
                Write-Problem "The Android test task does not exist, so nothing was tested." `
                    "app/build.gradle.kts must set testBuildType = `"release`" for testReleaseUnitTest to exist (AGP 9 only builds a unit-test component for testBuildType). Check that line is still present."
            }
            else {
                $problems.Add('Android unit tests failed')
                Write-Problem "The Android unit tests failed." `
                    "Run: cd remex.android; ./gradlew testReleaseUnitTest"
            }

            # Show what Gradle actually said. The 'What went wrong' block is the useful part;
            # fall back to the tail if Gradle failed in some way that does not produce one.
            $reason = @($gradleOutput | Select-String -Pattern '^\* What went wrong:' -Context 0, 6)
            if ($reason.Count -gt 0) {
                Write-Host ""
                Write-Host "  Gradle said:"
                foreach ($line in ($reason[0].Context.PostContext)) {
                    # Stop at Gradle's boilerplate. "* Try: > Run with --stacktrace" and the
                    # docs links are the same six lines on every failure and bury the one
                    # line that actually differs.
                    if ($line -match '^\* Try:') { break }
                    if ($line.Trim()) { Write-Host "    $line" }
                }
            }
            elseif ($text.Trim()) {
                Write-Host ""
                Write-Host "  Last few lines of the Gradle output:"
                foreach ($line in (@($gradleOutput) | Select-Object -Last 8)) {
                    if ("$line".Trim()) { Write-Host "    $line" }
                }
            }
        }
        elseif ($suites.Count -eq 0) {
            $problems.Add('no Android test results produced')
            Write-Problem "The Android test run produced no results." `
                "Run: cd remex.android; ./gradlew testReleaseUnitTest"
        }
        else {
            Write-Say "  Android tests finished. Running totals now include them."
        }
    }
}

# --- The edit guard itself -------------------------------------------------
# The guard runs on every Edit and Write, so a silent regression in it would either
# block real work or, worse, stop catching the corruption it exists to catch. Its own
# tests are cheap, so there is no reason not to run them here.

Write-Stage "Checking the edit guard"
$guardTests = Join-Path $RepoRoot '.claude' 'scripts' 'test_guard_edit.py'
if (Test-Path -LiteralPath $guardTests) {
    $guardOut = python3 $guardTests 2>&1
    if ($LASTEXITCODE -ne 0) {
        $problems.Add('edit guard tests failed')
        Write-Problem "The edit guard is not behaving correctly." `
            "Run: python3 .claude/scripts/test_guard_edit.py"
        $guardOut | Select-String -Pattern '[FAIL]' -SimpleMatch |
            ForEach-Object { Write-Say "    $_" 'Red' }
    }
    else {
        Write-Say "  The edit guard passes its own tests."
    }
}
else {
    Write-Say "  Skipped - .claude/scripts/test_guard_edit.py is not present."
}

# --- Lane placement --------------------------------------------------------
# The dispatcher's whole safety argument is that concurrent lanes touch disjoint files, and the
# rule enforcing it is pure - no board, no git, no network - so it is checkable here for
# milliseconds. It is checked here because the failure is silent: a wrong rule still produces a
# plan that looks reasonable, and the only symptom is a lane returning on a conflict an hour later.

Write-Stage "Checking lane placement"
$cluster = Join-Path $PSScriptRoot 'ralph-cluster.ps1'
if (Test-Path -LiteralPath $cluster) {
    # No capture: the self-test reports through Write-Host, which goes to the console rather than
    # down the pipeline, so its failures are already on screen and a variable would hold nothing.
    & $cluster -SelfTest
    if ($LASTEXITCODE -ne 0) {
        $problems.Add('lane placement self-test failed')
        Write-Problem "Lanes could be scheduled over the same files." `
            "Run: ./scripts/ralph-cluster.ps1 -SelfTest"
    }
    else {
        Write-Say "  Concurrent lanes are scheduled over disjoint files."
    }
}
else {
    Write-Say "  Skipped - scripts/ralph-cluster.ps1 is not present."
}

# --- Translations ----------------------------------------------------------

if (-not $SkipLocalization) {
    Write-Stage "Checking translations"
    $checker = Join-Path $PSScriptRoot 'check-localization.ps1'
    if (Test-Path -LiteralPath $checker) {
        & $checker 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            $problems.Add('translation check failed')
            Write-Problem "The translation check found problems." `
                "Run ./scripts/check-localization.ps1 on its own to see them in full."
        }
        else {
            Write-Say "  Translations are complete and current."
        }
    }
    else {
        Write-Say "  Skipped - scripts/check-localization.ps1 is not present."
    }
}

# --- Did the source move while we were working? ----------------------------

Write-Stage "Re-checking the fingerprint"
$after = Get-SourceFingerprint -ForScope $Scope
if ($after.Hash -cne $before.Hash) {
    $problems.Add('source changed during the run')
    Write-Problem "The source code changed while this was running, so the result proves nothing." `
        "Make sure nothing else is editing this working copy, then run ./scripts/verify.ps1 again."
}
else {
    Write-Say "  Unchanged. The result describes the code on disk."
}

# --- Receipt ---------------------------------------------------------------

$result = if ($problems.Count -eq 0) { 'PASS' } else { 'FAIL' }
$receiptObj = [ordered]@{
    schema       = 1
    result       = $result
    scope        = $Scope
    platform     = (Get-Platform)
    gitHead      = $gitHead
    gitDirty     = $gitDirty
    sourceHash   = $after.Hash
    sourceFiles  = $after.Files
    testsRun     = $testsRun
    testsPassed  = $testsPassed
    testsFailed  = $testsFailed
    testsSkipped = $testsSkipped
    warnings     = $warnings
    clean        = (-not $NoClean)
    problems     = @($problems)
    durationSec  = [int]((Get-Date) - $started).TotalSeconds
    timestampUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}

$receiptDir = Split-Path -Parent $Receipt
if ($receiptDir -and -not (Test-Path -LiteralPath $receiptDir)) {
    New-Item -ItemType Directory -Path $receiptDir -Force | Out-Null
}
$receiptObj | ConvertTo-Json -Compress -Depth 5 |
    Set-Content -LiteralPath $Receipt -Encoding utf8NoBOM

if ($Json) {
    Write-Output ($receiptObj | ConvertTo-Json -Compress -Depth 5)
}
elseif ($result -ceq 'PASS') {
    Write-Host "`nPASS" -ForegroundColor Green
    Write-Host "  $testsPassed of $testsRun tests passed" -ForegroundColor Green -NoNewline
    if ($testsSkipped -gt 0) { Write-Host ", $testsSkipped skipped" -ForegroundColor Yellow -NoNewline }
    Write-Host " in $($receiptObj.durationSec)s."
    Write-Host "  Receipt written to $Receipt"
    Write-Host "  Anyone can confirm it still applies with: ./scripts/verify.ps1 -Check"
}
else {
    Write-Host "`nFAIL" -ForegroundColor Red
    foreach ($p in $problems) { Write-Host "  - $p" -ForegroundColor Red }
    Write-Host "  Receipt written to $Receipt (recorded as FAIL)."
    Write-Host "  Nothing should be marked done until this passes." -ForegroundColor Yellow
}

exit $(if ($result -ceq 'PASS') { 0 } else { 1 })

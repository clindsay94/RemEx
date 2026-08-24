#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Checks that RemEx's translations are complete, current, and actually exist.

.DESCRIPTION
    RemEx ships in 9 languages on two platforms, and a translation can go wrong in three
    different ways. This script checks all three, on both platforms, in one pass.

    AXIS 1 - PARITY ("is the key missing from one language?")
        Every key in the English file must exist in all 8 translated files, and no translated
        file may define a key English does not have. This is what catches a key accidentally
        dropped during a translation pass.

    AXIS 2 - STALENESS ("is the translation out of date?")
        Parity only proves a key EXISTS somewhere. It says nothing about whether the German
        still means what the English now says. Two detectors run here:

          (a) English-similarity - a "translation" that is still mostly English words, or that
              matches an OLD version of the English text from git history. Only values of at
              least -MinSimilarityLength characters are judged, because below that, cognates and
              loanwords ("Client Android", "Mode Palette", "Transfer File") look identical to
              untranslated text and produce constant false alarms.

          (b) Per-key freshness - if the English line for a key was edited more recently than the
              translated line for that same key, the translation is behind. This is the only
              detector that catches a value which is perfect prose in the target language but
              describes behaviour the app no longer has.

    AXIS 3 - REFERENCED BUT UNDEFINED ("is the key missing from ALL languages?")
        Code asks for a key that no file defines anywhere. Axes 1 and 2 are both blind to this,
        because parity is never broken - the key is equally absent from all nine files. On the PC
        side this is especially nasty: LocalizationService's lookup ends in "?? key", so the user
        is shown the raw key name (e.g. "TaskManager_KillFailed") on screen, in every language.

    AXIS 4 - PLACEHOLDER PARITY ("does the translation still take the same arguments?")
        A value can be present, translated, current, and defined everywhere, and still be broken
        from the inside. If a locale carries a placeholder English does not have, it either
        renders literally - a user in that language reads the characters "{0}" on screen - or it
        throws a FormatException, in that language only. If a locale is MISSING a placeholder
        English has, the argument is silently dropped and that language alone gets a sentence
        with the number missing.

        Axes 1-3 are all structurally blind to this: parity is intact because the key exists
        everywhere, the value is genuinely translated so staleness ignores it, and the key is
        defined so the undefined-key axis never fires. This axis exists because exactly that
        combination shipped a literal "{0}" to Spanish users (RemEx-xn7l).

        THE TWO PLATFORMS USE DIFFERENT SYNTAX and must not share an extractor:
          - PC .resx uses .NET composite formatting - {0}, {1}, with optional alignment and
            format specifier ({0,-10}, {0:F2}), and literal braces written doubled ({{ }}).
            Without honouring the doubling, every literal brace reads as a placeholder.
          - Android uses printf - %1$s, %2$d, %.2f, with %% for a literal percent. The unit of
            comparison is the (index, conversion) PAIR, because a locale that turns %1$d into
            %1$s changes how the number is formatted even though nothing crashes.

        A differing INDEX SET is an error - that is the crash-or-drop case. A matching index set
        with a differing CONVERSION is a warning, because it degrades formatting rather than
        breaking. Repeating a placeholder is legal in both syntaxes and is not a finding: sets
        are compared, not counts, so "Delete {0}? {0} is gone" is fine against a single "{0}".

    AXIS 5 - IDENTICAL TO ENGLISH ("was this translated at all?")
        A value that is byte-identical to its English source. Axis 2(a) is supposed to catch this
        and structurally cannot: it scores word overlap and skips anything under
        -MinSimilarityLength, which is 40 characters - most of the UI. Every one of Turkish's
        identical values is under that floor, so the entire class was invisible. Connor found it
        by eye, the Pair button still reading "Pair" in Turkish and Portuguese, while the output
        of this very script said "Translations are complete and current" (RemEx-0bygp).

        Identity does not suffer the cognate ambiguity that forced the length floor, because it is
        not a similarity judgement - but plenty of values are identical for good reasons: "RAM",
        "FPS:", "Windows", "macOS", preset names like "Neon". So this reports rather than fails,
        and the baseline below absorbs the existing set. What it buys is that a NEWLY untranslated
        string is loud the day it appears. Values with no translatable word at all - "{0} - {1}",
        an emoji and a number - are skipped rather than baselined.

    Parity, undefined-key and placeholder-index problems are ERRORS - they are objective and
    always wrong. Staleness and untranslated findings are WARNINGS by default, because they are
    heuristic; pass -StrictStaleness to make them fail the build too.

    KNOWN LIMITATION of the freshness detector: it can only see drift that git can see. When all
    nine files are edited in one commit - which the .resx sync workflow often does - every
    translation looks as fresh as the English, whether or not anyone retranslated it. So this
    detector under-reports rather than over-reports, and a clean freshness result is weaker
    evidence than a dirty one. The similarity detector does not depend on commit timing and
    covers the case where a bulk edit hid the drift.

    BASELINE: the repository already contained findings when this check was written. Rather than
    have the check fail forever, known findings are recorded in scripts/localization-baseline.json
    and reported as "known" instead of failing. Anything NOT in that file fails. This means the
    check can be switched on today and no NEW localization defect can be introduced. Every
    suppressed finding is counted in the output - nothing is hidden.

.PARAMETER Platform
    Which side to check: 'pc' (remex.desktop .resx), 'android' (res/values*/strings.xml), or
    'all'. Defaults to 'all'.

.PARAMETER Axis
    Which check to run: 'parity', 'staleness', 'undefined', 'placeholder', 'untranslated', or 'all'. Defaults to 'all'.
    Use -Axis parity for a fast check that needs no git history.

.PARAMETER SimilarityThreshold
    How much word overlap with English counts as "probably not translated", from 0.0 to 1.0.
    Defaults to 0.40.

.PARAMETER MinSimilarityLength
    Ignore values shorter than this many characters in the similarity detector. Defaults to 40.
    Lowering it will produce false alarms on short cognates.

.PARAMETER StrictStaleness
    Treat staleness warnings as errors, so they fail the build.

.PARAMETER NoHistory
    Skip reading historical English values out of git. Faster, but stops detecting translated
    values that match an older version of the English text.

.PARAMETER MaxFindings
    Maximum findings to print per category before summarising the rest. Defaults to 25.
    The total count is always reported in full, even when the list is truncated.

.PARAMETER UpdateBaseline
    Rewrite scripts/localization-baseline.json so that everything currently found becomes
    "known". Use this only when you have deliberately accepted the current state - it is how you
    silence a finding you are not fixing today. Never run it to make a red build go green.

.PARAMETER NoBaseline
    Ignore the baseline file and report every finding, including known ones. Use this to see the
    true total.

.EXAMPLE
    ./scripts/check-localization.ps1
    Runs every check on both platforms.

.EXAMPLE
    ./scripts/check-localization.ps1 -Platform android -Axis parity
    Fast pre-commit check after editing Android strings.

.EXAMPLE
    ./scripts/check-localization.ps1 -StrictStaleness
    What CI runs when you want stale translations to block a merge.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidateSet('all', 'pc', 'android')]
    [string]$Platform = 'all',

    [Parameter(Mandatory = $false)]
    [ValidateSet('all', 'parity', 'staleness', 'undefined', 'placeholder', 'untranslated')]
    [string]$Axis = 'all',

    [Parameter(Mandatory = $false)]
    [ValidateRange(0.0, 1.0)]
    [double]$SimilarityThreshold = 0.40,

    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 1000)]
    [int]$MinSimilarityLength = 40,

    [Parameter(Mandatory = $false)]
    [switch]$StrictStaleness,

    [Parameter(Mandatory = $false)]
    [switch]$NoHistory,

    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 100000)]
    [int]$MaxFindings = 25,

    [Parameter(Mandatory = $false)]
    [switch]$UpdateBaseline,

    [Parameter(Mandatory = $false)]
    [switch]$NoBaseline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# ---------------------------------------------------------------------------------------------
# What each platform's files look like.
#
# Note the locale codes deliberately differ between the two platforms and are NOT a mistake:
# Android uses the legacy 'in' for Indonesian and 'pt-rBR' for Brazilian Portuguese, while .NET
# resx uses 'id' and 'pt-BR'. Treating them as one list would report gaps that do not exist.
# ---------------------------------------------------------------------------------------------
$Platforms = @{
    pc      = @{
        Name        = 'PC (remex.desktop)'
        Kind        = 'resx'
        Dir         = 'remex.desktop/Localization'
        BaseFile    = 'remex.desktop/Localization/Strings.resx'
        LocaleFile  = 'remex.desktop/Localization/Strings.{0}.resx'
        Locales     = @('es', 'fr', 'hi', 'id', 'pl', 'pt-BR', 'tr', 'uk')
        # Where code asks for a key, and how. Group 1 of each pattern is the key name.
        SourceGlobs = @('remex.desktop', 'remex.agent')
        SourceExts  = @('*.cs', '*.axaml', '*.xaml')
    }
    android = @{
        Name        = 'Android (remex.android)'
        Kind        = 'android'
        Dir         = 'remex.android/app/src/main/res'
        BaseFile    = 'remex.android/app/src/main/res/values/strings.xml'
        LocaleFile  = 'remex.android/app/src/main/res/values-{0}/strings.xml'
        Locales     = @('es', 'fr', 'hi', 'in', 'pl', 'pt-rBR', 'tr', 'uk')
        SourceGlobs = @('remex.android/app/src/main')
        SourceExts  = @('*.kt', '*.java', '*.xml')
    }
}

# Words the repo renamed across the whole tree at some point. Without folding these together,
# a locale value that was mass-edited by a find-replace matches no version of the English file
# while still being entirely English - which is exactly how a stale-English value hides from an
# exact-match detector.
$TokenSynonyms = @{ 'host' = 'pc' }

$script:Findings = [System.Collections.Generic.List[object]]::new()

function Add-Finding {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('Error', 'Warning')][string]$Severity,
        [Parameter(Mandatory = $true)][string]$Category,
        [Parameter(Mandatory = $true)][string]$Platform,
        # A short, stable identity for this exact finding, used to match against the baseline.
        # It must NOT contain anything that moves on its own - no file paths, no line numbers, no
        # similarity scores - or a baseline entry stops matching the moment code is reformatted.
        [Parameter(Mandatory = $true)][string]$Id,
        [Parameter(Mandatory = $true)][string]$Message
    )
    $script:Findings.Add([pscustomobject]@{
            Severity = $Severity
            Category = $Category
            Platform = $Platform
            Id       = $Id
            Message  = $Message
        })
}

function Write-Step { param([string]$Text) Write-Host "  $Text" -ForegroundColor DarkGray }
function Write-Heading { param([string]$Text) Write-Host "`n$Text" -ForegroundColor Cyan }

# ---------------------------------------------------------------------------------------------
# Parsing
#
# Values come from a real XML parse so that entities and multi-line text are handled correctly.
# Line numbers come from a separate scan of the raw text, because XmlDocument does not expose
# them - and the freshness detector needs a line range per key to ask git blame about.
# ---------------------------------------------------------------------------------------------
function Get-LocalizationEntries {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][ValidateSet('resx', 'android')][string]$Kind
    )

    $entries = @{}
    $raw = Get-Content -LiteralPath $Path -Raw -Encoding utf8

    $xml = [xml]$raw

    if ($Kind -eq 'resx') {
        foreach ($node in $xml.SelectNodes('/root/data')) {
            $name = $node.GetAttribute('name')
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            # Skip non-string resources (icons, byte blobs) - they carry a type or mimetype.
            if ($node.GetAttribute('type') -or $node.GetAttribute('mimetype')) { continue }
            $valueNode = $node.SelectSingleNode('value')
            $entries[$name] = @{
                Value        = if ($null -ne $valueNode) { $valueNode.InnerText } else { '' }
                Kind         = 'string'
                Translatable = $true
                StartLine    = 0
                EndLine      = 0
            }
        }
    }
    else {
        foreach ($node in $xml.SelectNodes('/resources/string')) {
            $name = $node.GetAttribute('name')
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            $entries[$name] = @{
                Value        = $node.InnerText
                Kind         = 'string'
                # translatable="false" flips the parity rule for this key: it is SUPPOSED to be
                # absent from the locale files.
                Translatable = ($node.GetAttribute('translatable') -ne 'false')
                StartLine    = 0
                EndLine      = 0
            }
        }
        foreach ($node in $xml.SelectNodes('/resources/plurals')) {
            $name = $node.GetAttribute('name')
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            # Only the 'other' form is comparable across languages. Polish and Ukrainian carry
            # four quantity buckets where English carries two, so per-quantity parity would flag
            # correct Slavic plural rules as a defect.
            $other = $node.SelectSingleNode("item[@quantity='other']")
            $entries[$name] = @{
                Value        = if ($null -ne $other) { $other.InnerText } else { '' }
                Kind         = 'plurals'
                Translatable = ($node.GetAttribute('translatable') -ne 'false')
                StartLine    = 0
                EndLine      = 0
            }
        }
    }

    # Second pass over the raw text purely for line ranges.
    $lines = $raw -split "`r?`n"
    if ($Kind -eq 'resx') {
        $startPattern = '^\s*<data\s+name="([^"]+)"'
        $endPattern = '</data>'
    }
    else {
        $startPattern = '^\s*<(?:string|plurals)\s+name="([^"]+)"'
        $endPattern = '</string>|</plurals>'
    }

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $m = [regex]::Match($lines[$i], $startPattern)
        if (-not $m.Success) { continue }
        $name = $m.Groups[1].Value
        if (-not $entries.ContainsKey($name)) { continue }

        $entries[$name].StartLine = $i + 1
        $end = $i + 1
        if ($lines[$i] -match '/>\s*$' -or $lines[$i] -match $endPattern) {
            $end = $i + 1
        }
        else {
            for ($j = $i + 1; $j -lt $lines.Count; $j++) {
                if ($lines[$j] -match $endPattern) { $end = $j + 1; break }
            }
        }
        $entries[$name].EndLine = $end
    }

    return $entries
}

# ---------------------------------------------------------------------------------------------
# AXIS 1 - PARITY
# ---------------------------------------------------------------------------------------------
function Test-Parity {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Config,
        [Parameter(Mandatory = $true)][hashtable]$BaseEntries,
        [Parameter(Mandatory = $true)][hashtable]$LocaleEntries
    )

    $translatableKeys = @($BaseEntries.Keys | Where-Object { $BaseEntries[$_].Translatable })
    $untranslatableKeys = @($BaseEntries.Keys | Where-Object { -not $BaseEntries[$_].Translatable })

    foreach ($locale in $Config.Locales) {
        if (-not $LocaleEntries.ContainsKey($locale)) { continue }
        $entries = $LocaleEntries[$locale]

        $missing = @($translatableKeys | Where-Object { -not $entries.ContainsKey($_) } | Sort-Object)
        foreach ($key in $missing) {
            Add-Finding -Severity Error -Category 'Parity: missing translation' -Platform $Config.Name `
                -Id "$($Config.Kind)/parity-missing/$locale/$key" `
                -Message "$locale is missing '$key' (it exists in English)"
        }

        $extra = @($entries.Keys | Where-Object { -not $BaseEntries.ContainsKey($_) } | Sort-Object)
        foreach ($key in $extra) {
            Add-Finding -Severity Error -Category 'Parity: orphaned translation' -Platform $Config.Name `
                -Id "$($Config.Kind)/parity-orphan/$locale/$key" `
                -Message "$locale defines '$key', which English does not have"
        }

        # Kind drift: a key that is a plurals in English but a plain string in a locale (or the
        # reverse) compiles, then crashes or mis-formats at runtime.
        foreach ($key in $translatableKeys) {
            if (-not $entries.ContainsKey($key)) { continue }
            if ($entries[$key].Kind -ne $BaseEntries[$key].Kind) {
                Add-Finding -Severity Error -Category 'Parity: type mismatch' -Platform $Config.Name `
                    -Id "$($Config.Kind)/parity-kind/$locale/$key" `
                    -Message "'$key' is a $($BaseEntries[$key].Kind) in English but a $($entries[$key].Kind) in $locale"
            }
        }

        foreach ($key in $untranslatableKeys) {
            if ($entries.ContainsKey($key)) {
                Add-Finding -Severity Warning -Category 'Parity: translated an invariant' -Platform $Config.Name `
                    -Id "$($Config.Kind)/parity-invariant/$locale/$key" `
                    -Message "'$key' is marked translatable=`"false`" in English but $locale defines it anyway"
            }
        }

        # An empty translation passes a naive key-set comparison but shows the user nothing.
        foreach ($key in $translatableKeys) {
            if (-not $entries.ContainsKey($key)) { continue }
            if ([string]::IsNullOrWhiteSpace($entries[$key].Value) -and
                -not [string]::IsNullOrWhiteSpace($BaseEntries[$key].Value)) {
                Add-Finding -Severity Error -Category 'Parity: empty translation' -Platform $Config.Name `
                    -Id "$($Config.Kind)/parity-empty/$locale/$key" `
                    -Message "$locale has '$key' but its value is empty"
            }
        }
    }
}

# ---------------------------------------------------------------------------------------------
# AXIS 2a - ENGLISH SIMILARITY
# ---------------------------------------------------------------------------------------------
function Get-ComparableTokens {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { return @() }

    # Drop format specifiers first - "%1$s", "{0}" and "%%" are identical in every language and
    # would otherwise count as shared vocabulary.
    $cleaned = $Text -replace '%\d+\$[a-zA-Z]', ' ' -replace '%[sdf%]', ' ' -replace '\{\d+[^}]*\}', ' '

    $tokens = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($m in [regex]::Matches($cleaned.ToLowerInvariant(), "[\p{L}\p{N}][\p{L}\p{N}'’-]*")) {
        $t = $m.Value
        if ($TokenSynonyms.ContainsKey($t)) { $t = $TokenSynonyms[$t] }
        [void]$tokens.Add($t)
    }
    return $tokens
}

function Get-JaccardSimilarity {
    param(
        [Parameter(Mandatory = $true)]$SetA,
        [Parameter(Mandatory = $true)]$SetB,
        [Parameter(Mandatory = $true)]$Exclude
    )

    $a = [System.Collections.Generic.HashSet[string]]::new([string[]]@($SetA))
    $b = [System.Collections.Generic.HashSet[string]]::new([string[]]@($SetB))
    foreach ($t in @($Exclude)) { [void]$a.Remove($t); [void]$b.Remove($t) }

    if ($a.Count -eq 0 -or $b.Count -eq 0) { return 0.0 }

    $intersection = [System.Collections.Generic.HashSet[string]]::new($a)
    $intersection.IntersectWith($b)
    $union = [System.Collections.Generic.HashSet[string]]::new($a)
    $union.UnionWith($b)

    return [double]$intersection.Count / [double]$union.Count
}

function Get-HistoricalEnglishValues {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Config
    )

    $history = @{}
    $relPath = $Config.BaseFile

    $commits = @(& git -C $RepoRoot log --format=%H -- $relPath 2>$null)
    if ($LASTEXITCODE -ne 0 -or $commits.Count -eq 0) {
        Write-Step "No git history available for $relPath - skipping the historical comparison."
        return $history
    }

    Write-Step "Reading $($commits.Count) historical versions of $(Split-Path $relPath -Leaf)..."
    $tempFile = [System.IO.Path]::GetTempFileName()
    try {
        foreach ($sha in $commits) {
            $content = & git -C $RepoRoot show "${sha}:${relPath}" 2>$null
            if ($LASTEXITCODE -ne 0 -or -not $content) { continue }
            Set-Content -LiteralPath $tempFile -Value ($content -join "`n") -Encoding utf8
            try { $entries = Get-LocalizationEntries -Path $tempFile -Kind $Config.Kind }
            catch { continue }  # A historical revision that no longer parses is not our problem.
            foreach ($key in $entries.Keys) {
                if (-not $history.ContainsKey($key)) {
                    $history[$key] = [System.Collections.Generic.List[string]]::new()
                }
                $v = $entries[$key].Value
                if (-not [string]::IsNullOrWhiteSpace($v) -and -not $history[$key].Contains($v)) {
                    $history[$key].Add($v)
                }
            }
        }
    }
    finally {
        Remove-Item -LiteralPath $tempFile -ErrorAction SilentlyContinue
    }

    return $history
}

function Test-EnglishSimilarity {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Config,
        [Parameter(Mandatory = $true)][hashtable]$BaseEntries,
        [Parameter(Mandatory = $true)][hashtable]$LocaleEntries,
        [Parameter(Mandatory = $true)][hashtable]$History
    )

    foreach ($key in ($BaseEntries.Keys | Sort-Object)) {
        $english = $BaseEntries[$key].Value
        if (-not $BaseEntries[$key].Translatable) { continue }
        if ([string]::IsNullOrWhiteSpace($english)) { continue }
        # Short values are dominated by cognates and loanwords; judging them produces noise, not
        # findings. See the parameter help for worked examples.
        if ($english.Length -lt $MinSimilarityLength) { continue }

        $englishTokens = Get-ComparableTokens -Text $english

        # Derive this key's non-translatable vocabulary from the data rather than a hand-kept
        # list: any token that ALL eight translators left verbatim is a command, a product name
        # or a unit, so it is shared vocabulary and must not count as evidence of English.
        $shared = $null
        foreach ($locale in $Config.Locales) {
            if (-not $LocaleEntries.ContainsKey($locale)) { continue }
            if (-not $LocaleEntries[$locale].ContainsKey($key)) { continue }
            $lt = Get-ComparableTokens -Text $LocaleEntries[$locale][$key].Value
            if ($null -eq $shared) {
                $shared = [System.Collections.Generic.HashSet[string]]::new([string[]]@($lt))
            }
            else {
                $shared.IntersectWith([string[]]@($lt))
            }
        }
        if ($null -eq $shared) { continue }
        $shared.IntersectWith([string[]]@($englishTokens))

        $candidates = [System.Collections.Generic.List[string]]::new()
        $candidates.Add($english)
        if ($History.ContainsKey($key)) {
            foreach ($h in $History[$key]) { if (-not $candidates.Contains($h)) { $candidates.Add($h) } }
        }

        foreach ($locale in $Config.Locales) {
            if (-not $LocaleEntries.ContainsKey($locale)) { continue }
            if (-not $LocaleEntries[$locale].ContainsKey($key)) { continue }
            $localeValue = $LocaleEntries[$locale][$key].Value
            if ([string]::IsNullOrWhiteSpace($localeValue)) { continue }

            $localeTokens = Get-ComparableTokens -Text $localeValue
            $best = 0.0
            $bestWasCurrent = $true
            foreach ($candidate in $candidates) {
                $score = Get-JaccardSimilarity -SetA $localeTokens `
                    -SetB (Get-ComparableTokens -Text $candidate) -Exclude $shared
                if ($score -gt $best) {
                    $best = $score
                    $bestWasCurrent = ($candidate -eq $english)
                }
            }

            if ($best -ge $SimilarityThreshold) {
                $which = if ($bestWasCurrent) { 'the current English' } else { 'an OLD version of the English' }
                Add-Finding -Severity Warning -Category 'Staleness: still looks like English' -Platform $Config.Name `
                    -Id "$($Config.Kind)/similar-to-english/$locale/$key" `
                    -Message ("{0} '{1}' matches {2} text at {3:P0} word overlap: `"{4}`"" -f `
                        $locale, $key, $which, $best, (Get-Excerpt $localeValue))
            }
        }
    }
}

function Test-IdenticalToEnglish {
    <#
    .SYNOPSIS
        Reports translations whose value is byte-identical to the English source.

    .DESCRIPTION
        THIS IS THE AXIS THAT CATCHES WHAT Test-EnglishSimilarity CANNOT (RemEx-0bygp). That one
        scores word overlap, and deliberately skips anything shorter than -MinSimilarityLength,
        because below that cognates and loanwords drown the signal. Forty characters is most of the
        UI: every one of Turkish's identical values is under it, so the whole class was invisible.
        Connor found it by eye - the Pair button still reading 'Pair' in Turkish and Portuguese -
        and the gate said "Translations are complete and current" underneath.

        Byte-identity does not have the cognate problem that forced the length floor, because it is
        not a similarity judgement. It has a different one: plenty of values are identical for good
        reasons - 'RAM', 'FPS:', 'Windows', 'macOS', preset names like 'Neon' and 'Ember'. So this
        reports rather than fails, and the existing baseline absorbs the current set. What it buys
        is that a NEW untranslated string is loud on the day it appears.

        Values with no translatable word at all - '{0} - {1}', a hex mask, an emoji plus a number -
        are skipped outright rather than baselined, since 'untranslated' is not a thing they can be.
    #>
    param(
        [Parameter(Mandatory = $true)][hashtable]$Config,
        [Parameter(Mandatory = $true)][hashtable]$BaseEntries,
        [Parameter(Mandatory = $true)][hashtable]$LocaleEntries
    )

    foreach ($key in ($BaseEntries.Keys | Sort-Object)) {
        $english = $BaseEntries[$key].Value
        if (-not $BaseEntries[$key].Translatable) { continue }
        if ([string]::IsNullOrWhiteSpace($english)) { continue }
        if (-not (Test-HasTranslatableWord -Text $english)) { continue }

        foreach ($locale in $Config.Locales) {
            if (-not $LocaleEntries.ContainsKey($locale)) { continue }
            if (-not $LocaleEntries[$locale].ContainsKey($key)) { continue }

            if ($LocaleEntries[$locale][$key].Value -ceq $english) {
                Add-Finding -Severity Warning -Category 'Untranslated: identical to English' -Platform $Config.Name `
                    -Id "$($Config.Kind)/identical-to-english/$locale/$key" `
                    -Message ("{0} '{1}' is byte-identical to the English: `"{2}`"" -f `
                        $locale, $key, (Get-Excerpt $english))
            }
        }
    }
}

function Test-HasTranslatableWord {
    <#
    .SYNOPSIS
        Whether a value contains a word that could meaningfully be translated at all.
    .DESCRIPTION
        Placeholders and format specifiers are removed first, so '{0} - {1}' and '%1$s' are correctly
        seen as wordless. What is left has to contain a run of at least two letters in any script.
        '#RRGGBB' survives this and is baselined rather than skipped, because RGGBB is letters and
        the rule stays mechanical instead of accruing special cases.
    #>
    param([string]$Text)

    $withoutPlaceholders = [regex]::Replace($Text, '\{[^}]*\}', ' ')
    $withoutPlaceholders = [regex]::Replace($withoutPlaceholders, '%[0-9$]*[a-zA-Z]', ' ')

    return $withoutPlaceholders -match '\p{L}{2,}'
}

function Get-Excerpt {
    param([string]$Text)
    $flat = ($Text -replace '\s+', ' ').Trim()
    if ($flat.Length -le 70) { return $flat }
    return $flat.Substring(0, 67) + '...'
}

# ---------------------------------------------------------------------------------------------
# AXIS 2b - PER-KEY FRESHNESS
#
# One `git blame` per file gives every line's commit time. Asking `git log -L` per key would be
# the same answer at roughly a thousand times the number of subprocesses.
# ---------------------------------------------------------------------------------------------
function Get-LineCommitTimes {
    param([Parameter(Mandatory = $true)][string]$RelPath)

    $times = @{}
    $output = & git -C $RepoRoot blame --line-porcelain -- $RelPath 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $output) { return $times }

    $currentLine = 0
    foreach ($line in $output) {
        if ($line -match '^[0-9a-f]{40}\s+\d+\s+(\d+)') {
            $currentLine = [int]$Matches[1]
        }
        elseif ($line -match '^committer-time\s+(\d+)' -and $currentLine -gt 0) {
            $times[$currentLine] = [long]$Matches[1]
        }
    }
    return $times
}

function Get-KeyCommitTimes {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Entries,
        [Parameter(Mandatory = $true)][hashtable]$LineTimes
    )

    $result = @{}
    foreach ($key in $Entries.Keys) {
        $start = $Entries[$key].StartLine
        $end = $Entries[$key].EndLine
        if ($start -le 0) { continue }
        $newest = 0L
        for ($i = $start; $i -le $end; $i++) {
            if ($LineTimes.ContainsKey($i) -and $LineTimes[$i] -gt $newest) { $newest = $LineTimes[$i] }
        }
        if ($newest -gt 0) { $result[$key] = $newest }
    }
    return $result
}

function Test-Freshness {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Config,
        [Parameter(Mandatory = $true)][hashtable]$BaseEntries,
        [Parameter(Mandatory = $true)][hashtable]$LocaleEntries
    )

    $baseLineTimes = Get-LineCommitTimes -RelPath $Config.BaseFile
    if ($baseLineTimes.Count -eq 0) {
        Write-Step 'git blame returned nothing for the English file - skipping the freshness check.'
        Write-Step 'If this is CI, the checkout probably needs fetch-depth: 0.'
        return
    }
    $baseTimes = Get-KeyCommitTimes -Entries $BaseEntries -LineTimes $baseLineTimes

    foreach ($locale in $Config.Locales) {
        if (-not $LocaleEntries.ContainsKey($locale)) { continue }
        $relPath = $Config.LocaleFile -f $locale
        $localeLineTimes = Get-LineCommitTimes -RelPath $relPath
        if ($localeLineTimes.Count -eq 0) { continue }
        $localeTimes = Get-KeyCommitTimes -Entries $LocaleEntries[$locale] -LineTimes $localeLineTimes

        foreach ($key in ($baseTimes.Keys | Sort-Object)) {
            if (-not $BaseEntries[$key].Translatable) { continue }
            if (-not $localeTimes.ContainsKey($key)) { continue }
            if ($baseTimes[$key] -le $localeTimes[$key]) { continue }

            $englishDate = [DateTimeOffset]::FromUnixTimeSeconds($baseTimes[$key]).ToString('yyyy-MM-dd')
            $localeDate = [DateTimeOffset]::FromUnixTimeSeconds($localeTimes[$key]).ToString('yyyy-MM-dd')
            Add-Finding -Severity Warning -Category 'Staleness: translation is behind English' -Platform $Config.Name `
                -Id "$($Config.Kind)/behind-english/$locale/$key" `
                -Message "$locale '$key' was last touched $localeDate but English changed $englishDate"
        }
    }
}

# ---------------------------------------------------------------------------------------------
# AXIS 3 - REFERENCED BUT UNDEFINED
# ---------------------------------------------------------------------------------------------
function Get-ReferencedKeys {
    param([Parameter(Mandatory = $true)][hashtable]$Config)

    $referenced = @{}

    foreach ($glob in $Config.SourceGlobs) {
        $root = Join-Path $RepoRoot $glob
        if (-not (Test-Path -LiteralPath $root)) { continue }

        $files = Get-ChildItem -LiteralPath $root -Recurse -File -Include $Config.SourceExts |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj|build|generated)[\\/]' -and
                           $_.Name -ne 'Strings.Designer.cs' }

        foreach ($file in $files) {
            $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8
            if ([string]::IsNullOrEmpty($text)) { continue }

            # Collapse whitespace before matching. ktfmt wraps long lines and will happily split
            # "R.string.some_very_long_key" across two lines, which defeats a raw line-wise grep.
            $flat = $text -replace '\s+', ' '

            if ($Config.Kind -eq 'android') {
                $patterns = @(
                    '(?<!android\.)\bR\.string\.\s*([A-Za-z0-9_]+)',
                    '(?<!android\.)\bR\.plurals\.\s*([A-Za-z0-9_]+)',
                    '@string/([A-Za-z0-9_]+)',
                    '@plurals/([A-Za-z0-9_]+)'
                )
            }
            else {
                $patterns = @(
                    'Instance\[\s*"([A-Za-z0-9_]+)"\s*\]',
                    # ANY namespace prefix, not just 'local'. This file family uses two -
                    # {local:Localize} 528 times and {conv:Localize} 116 - and matching only the
                    # first left 116 references invisible to this axis, which is precisely the
                    # blind spot it exists to close. (RemEx-fxkg.)
                    '\{\s*[A-Za-z_][A-Za-z0-9_]*:Localize\s+([A-Za-z0-9_]+)',
                    '(?<![A-Za-z0-9_.])Strings\.([A-Z][A-Za-z0-9_]*)'
                )
            }

            foreach ($pattern in $patterns) {
                foreach ($m in [regex]::Matches($flat, $pattern)) {
                    $key = $m.Groups[1].Value
                    if (-not $referenced.ContainsKey($key)) {
                        $referenced[$key] = [System.Collections.Generic.List[string]]::new()
                    }
                    $rel = $file.FullName.Substring($RepoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
                    if (-not $referenced[$key].Contains($rel)) { $referenced[$key].Add($rel) }
                }
            }
        }
    }

    return $referenced
}

function Test-UndefinedKeys {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Config,
        [Parameter(Mandatory = $true)][hashtable]$BaseEntries
    )

    # Members of the generated Strings class that are not resource keys.
    $notKeys = @('ResourceManager', 'Culture')

    $referenced = Get-ReferencedKeys -Config $Config
    Write-Step "Found $($referenced.Count) distinct keys referenced in code."

    foreach ($key in ($referenced.Keys | Sort-Object)) {
        if ($notKeys -contains $key) { continue }
        if ($BaseEntries.ContainsKey($key)) { continue }
        $where = $referenced[$key]
        $shown = if ($where.Count -le 3) { $where -join ', ' } else { ($where[0..2] -join ', ') + " (+$($where.Count - 3) more)" }
        Add-Finding -Severity Error -Category 'Undefined: key used but never declared' -Platform $Config.Name `
            -Id "$($Config.Kind)/undefined/$key" `
            -Message "'$key' is referenced in code but defined in no localization file, so users see the raw key name. Used in: $shown"
    }
}

# ---------------------------------------------------------------------------------------------
# AXIS 4 - PLACEHOLDER PARITY
# ---------------------------------------------------------------------------------------------
function Get-PlaceholderSet {
    <#
        Returns the placeholders in one value as a set of identities, plus anything malformed.
        Identity is the argument index for .resx, and "index:conversion" for Android - see the
        script header for why the two platforms cannot share an extractor.
    #>
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory = $true)][ValidateSet('resx', 'android')][string]$Syntax
    )

    $found = [System.Collections.Generic.HashSet[string]]::new()
    $malformed = [System.Collections.Generic.List[string]]::new()
    if ([string]::IsNullOrEmpty($Text)) {
        return @{ Placeholders = $found; Malformed = $malformed }
    }

    if ($Syntax -eq 'resx') {
        # Hand-scanned rather than regexed, because {{ and }} are literal braces and a regex
        # that does not consume them in pairs will misread "{{0}}" as the placeholder {0}.
        $i = 0
        while ($i -lt $Text.Length) {
            $c = $Text[$i]
            if ($c -eq '{' -and $i + 1 -lt $Text.Length -and $Text[$i + 1] -eq '{') { $i += 2; continue }
            if ($c -eq '}' -and $i + 1 -lt $Text.Length -and $Text[$i + 1] -eq '}') { $i += 2; continue }
            if ($c -eq '{') {
                $close = $Text.IndexOf('}', $i + 1)
                if ($close -lt 0) { $malformed.Add("an opening brace at character $($i + 1) is never closed"); break }
                $inner = $Text.Substring($i + 1, $close - $i - 1)
                # The index runs up to the alignment comma or the format-specifier colon.
                $indexPart = ($inner -split '[,:]', 2)[0]
                if ($indexPart -match '^\d+$') { [void]$found.Add($indexPart) }
                else { $malformed.Add("'{$inner}' is not a valid placeholder") }
                $i = $close + 1
                continue
            }
            if ($c -eq '}') { $malformed.Add("a closing brace at character $($i + 1) has no opening brace"); $i++; continue }
            $i++
        }
    }
    else {
        # %[index$][flags][width][.precision]conversion, and %% for a literal percent.
        # The space flag is deliberately NOT accepted: it would make the "% d" inside ordinary
        # prose such as "100% done" parse as a format specifier.
        $implicit = 0
        foreach ($m in [regex]::Matches($Text, '%%|%(?:(\d+)\$)?[-#+0,(]*\d*(?:\.\d+)?([a-zA-Z])')) {
            if ($m.Value -eq '%%') { continue }
            $conversion = $m.Groups[2].Value
            if ($m.Groups[1].Success) {
                $index = $m.Groups[1].Value
            }
            else {
                # Non-positional specifiers are numbered by the order they appear.
                $implicit++
                $index = "$implicit"
            }
            [void]$found.Add("${index}:${conversion}")
        }
    }

    return @{ Placeholders = $found; Malformed = $malformed }
}

function Get-IndexPart {
    param([Parameter(Mandatory = $true)][string]$Identity)
    # "3:d" -> "3" for Android; "3" -> "3" for resx.
    return ($Identity -split ':', 2)[0]
}

function Test-PlaceholderParity {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Config,
        [Parameter(Mandatory = $true)][hashtable]$BaseEntries,
        [Parameter(Mandatory = $true)][hashtable]$LocaleEntries
    )

    foreach ($key in ($BaseEntries.Keys | Sort-Object)) {
        $english = Get-PlaceholderSet -Text $BaseEntries[$key].Value -Syntax $Config.Kind
        foreach ($m in $english.Malformed) {
            Add-Finding -Severity Error -Category 'Placeholder: malformed in English' -Platform $Config.Name `
                -Id "$($Config.Kind)/placeholder-malformed/en/$key" `
                -Message "English '$key' has a broken placeholder - $m"
        }
        $englishIndices = [System.Collections.Generic.HashSet[string]]::new(
            [string[]]@($english.Placeholders | ForEach-Object { Get-IndexPart $_ }))

        foreach ($locale in $Config.Locales) {
            if (-not $LocaleEntries.ContainsKey($locale)) { continue }
            if (-not $LocaleEntries[$locale].ContainsKey($key)) { continue }

            $localeSet = Get-PlaceholderSet -Text $LocaleEntries[$locale][$key].Value -Syntax $Config.Kind
            foreach ($m in $localeSet.Malformed) {
                Add-Finding -Severity Error -Category 'Placeholder: malformed translation' -Platform $Config.Name `
                    -Id "$($Config.Kind)/placeholder-malformed/$locale/$key" `
                    -Message "$locale '$key' has a broken placeholder - $m"
            }

            $localeIndices = [System.Collections.Generic.HashSet[string]]::new(
                [string[]]@($localeSet.Placeholders | ForEach-Object { Get-IndexPart $_ }))

            $extra = [System.Collections.Generic.HashSet[string]]::new($localeIndices)
            $extra.ExceptWith($englishIndices)
            $missing = [System.Collections.Generic.HashSet[string]]::new($englishIndices)
            $missing.ExceptWith($localeIndices)

            if ($extra.Count -gt 0) {
                $list = ($extra | Sort-Object | ForEach-Object { Format-Placeholder -Index $_ -Syntax $Config.Kind }) -join ', '
                Add-Finding -Severity Error -Category 'Placeholder: locale has one English lacks' -Platform $Config.Name `
                    -Id "$($Config.Kind)/placeholder-extra/$locale/$key" `
                    -Message ("{0} '{1}' uses {2}, which English does not supply - it renders literally or throws: `"{3}`"" -f `
                        $locale, $key, $list, (Get-Excerpt $LocaleEntries[$locale][$key].Value))
            }
            if ($missing.Count -gt 0) {
                $list = ($missing | Sort-Object | ForEach-Object { Format-Placeholder -Index $_ -Syntax $Config.Kind }) -join ', '
                Add-Finding -Severity Error -Category 'Placeholder: locale is missing one' -Platform $Config.Name `
                    -Id "$($Config.Kind)/placeholder-missing/$locale/$key" `
                    -Message ("{0} '{1}' drops {2}, so that value is silently lost in this language only: `"{3}`"" -f `
                        $locale, $key, $list, (Get-Excerpt $LocaleEntries[$locale][$key].Value))
            }

            # Same arguments, different conversion (Android only): nothing crashes, but the
            # number is formatted differently in that language. A warning, not an error.
            if ($Config.Kind -eq 'android' -and $extra.Count -eq 0 -and $missing.Count -eq 0) {
                $enPairs = @($english.Placeholders | Sort-Object)
                $locPairs = @($localeSet.Placeholders | Sort-Object)
                if (($enPairs -join '|') -ne ($locPairs -join '|')) {
                    Add-Finding -Severity Warning -Category 'Placeholder: conversion differs' -Platform $Config.Name `
                        -Id "$($Config.Kind)/placeholder-conversion/$locale/$key" `
                        -Message "$locale '$key' formats the same arguments differently - English uses $($enPairs -join ', '), $locale uses $($locPairs -join ', ')"
                }
            }
        }
    }
}

function Format-Placeholder {
    param(
        [Parameter(Mandatory = $true)][string]$Index,
        [Parameter(Mandatory = $true)][string]$Syntax
    )
    if ($Syntax -eq 'resx') { return "{$Index}" }
    return "%$Index`$..."
}

# ---------------------------------------------------------------------------------------------
# Driver
# ---------------------------------------------------------------------------------------------
Write-Host 'RemEx localization check' -ForegroundColor White
Write-Host "Repository: $RepoRoot" -ForegroundColor DarkGray

$selected = if ($Platform -eq 'all') { @('pc', 'android') } else { @($Platform) }

foreach ($platformKey in $selected) {
    $config = $Platforms[$platformKey]
    Write-Heading "Checking $($config.Name)"

    $basePath = Join-Path $RepoRoot $config.BaseFile
    if (-not (Test-Path -LiteralPath $basePath)) {
        Add-Finding -Severity Error -Category 'Setup' -Platform $config.Name `
            -Id "$($config.Kind)/setup/missing-base-file" `
            -Message "English file not found at $($config.BaseFile). Has the project layout changed?"
        continue
    }

    $baseEntries = Get-LocalizationEntries -Path $basePath -Kind $config.Kind
    Write-Step "English defines $($baseEntries.Count) keys."

    $localeEntries = @{}
    foreach ($locale in $config.Locales) {
        $localePath = Join-Path $RepoRoot ($config.LocaleFile -f $locale)
        if (-not (Test-Path -LiteralPath $localePath)) {
            Add-Finding -Severity Error -Category 'Parity: missing file' -Platform $config.Name `
                -Id "$($config.Kind)/parity-missing-file/$locale" `
                -Message "The '$locale' translation file is missing entirely ($($config.LocaleFile -f $locale))"
            continue
        }
        $localeEntries[$locale] = Get-LocalizationEntries -Path $localePath -Kind $config.Kind
    }
    Write-Step "Loaded $($localeEntries.Count) of $($config.Locales.Count) translation files."

    if ($Axis -in @('all', 'parity')) {
        Write-Step 'Axis 1: key parity across all 9 files...'
        Test-Parity -Config $config -BaseEntries $baseEntries -LocaleEntries $localeEntries
    }

    if ($Axis -in @('all', 'staleness')) {
        Write-Step 'Axis 2: staleness...'
        $history = @{}
        if (-not $NoHistory) { $history = Get-HistoricalEnglishValues -Config $config }
        Test-EnglishSimilarity -Config $config -BaseEntries $baseEntries -LocaleEntries $localeEntries -History $history
        Test-Freshness -Config $config -BaseEntries $baseEntries -LocaleEntries $localeEntries
    }

    if ($Axis -in @('all', 'undefined')) {
        Write-Step 'Axis 3: keys referenced in code but declared nowhere...'
        Test-UndefinedKeys -Config $config -BaseEntries $baseEntries
    }

    if ($Axis -in @('all', 'placeholder')) {
        Write-Step 'Axis 4: placeholders match English...'
        Test-PlaceholderParity -Config $config -BaseEntries $baseEntries -LocaleEntries $localeEntries
    }

    if ($Axis -in @('all', 'untranslated')) {
        Write-Step 'Axis 5: values identical to English...'
        Test-IdenticalToEnglish -Config $config -BaseEntries $baseEntries -LocaleEntries $localeEntries
    }
}

# ---------------------------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------------------------
$baselinePath = Join-Path $PSScriptRoot 'localization-baseline.json'

if ($UpdateBaseline) {
    $payload = [ordered]@{
        comment = @(
            'Findings that already existed when scripts/check-localization.ps1 was introduced, or',
            'that have been deliberately accepted since. They are reported as "known" and do not',
            'fail the build. Anything NOT listed here fails. Shrink this list; do not grow it.',
            'Regenerate with: ./scripts/check-localization.ps1 -UpdateBaseline'
        )
        known   = @($script:Findings | ForEach-Object { $_.Id } | Sort-Object -Unique)
    }
    $payload | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $baselinePath -Encoding utf8
    Write-Heading 'Baseline updated'
    Write-Host "  Wrote $($payload.known.Count) known finding(s) to scripts/localization-baseline.json" -ForegroundColor Yellow
    Write-Host '  Review that diff carefully - every line you added is a defect you chose not to fix.' -ForegroundColor Yellow
    exit 0
}

$baseline = [System.Collections.Generic.HashSet[string]]::new()
if (-not $NoBaseline -and (Test-Path -LiteralPath $baselinePath)) {
    $loaded = Get-Content -LiteralPath $baselinePath -Raw -Encoding utf8 | ConvertFrom-Json
    foreach ($id in @($loaded.known)) { [void]$baseline.Add($id) }
}

$known = @($script:Findings | Where-Object { $baseline.Contains($_.Id) })
$active = @($script:Findings | Where-Object { -not $baseline.Contains($_.Id) })

# A baseline entry that no longer fires means somebody fixed it. Say so, so the file gets pruned
# instead of quietly accumulating permission to be broken again.
$seenIds = [System.Collections.Generic.HashSet[string]]::new()
foreach ($f in $script:Findings) { [void]$seenIds.Add($f.Id) }
$fixed = @($baseline | Where-Object { -not $seenIds.Contains($_) })

Write-Heading 'Results'

if ($known.Count -gt 0) {
    Write-Host "  $($known.Count) known finding(s) suppressed by scripts/localization-baseline.json." -ForegroundColor DarkGray
    Write-Host '  Re-run with -NoBaseline to see them.' -ForegroundColor DarkGray
}
if ($fixed.Count -gt 0 -and $Axis -eq 'all' -and $Platform -eq 'all') {
    Write-Host "  $($fixed.Count) baseline entr(y/ies) no longer occur - somebody fixed them." -ForegroundColor Green
    Write-Host '  Prune them with -UpdateBaseline so the baseline keeps shrinking.' -ForegroundColor Green
}

if ($active.Count -eq 0) {
    Write-Host ''
    Write-Host '  No new localization problems. Every key exists in all 9 files on both platforms,' -ForegroundColor Green
    Write-Host '  every key used in code is declared somewhere, and every translation takes the' -ForegroundColor Green
    Write-Host '  same arguments as its English source.' -ForegroundColor Green

    # THIS IS NOT THE SAME AS "the translations are done", and the difference is what RemEx-0bygp
    # was about. Everything above is a statement about structure. Whether a value was translated at
    # all is axis 5, and its existing findings are in the baseline - so a green run here is
    # compatible with hundreds of strings still sitting in English. Say the number rather than
    # letting the green imply otherwise.
    $untranslated = @($known | Where-Object { $_.Category -like 'Untranslated:*' }).Count
    if ($untranslated -gt 0) {
        Write-Host ''
        Write-Host "  $untranslated string(s) are still byte-identical to English and known to the baseline." -ForegroundColor DarkYellow
        Write-Host '  That is a backlog, not a pass: -Axis untranslated -NoBaseline lists them.' -ForegroundColor DarkYellow
    }

    Write-Output "LOCALIZATION-SUMMARY errors=0 warnings=0 known=$($known.Count)"
    exit 0
}

$errorCount = 0
$warningCount = 0

foreach ($group in ($active | Group-Object Platform, Category | Sort-Object Name)) {
    $severity = $group.Group[0].Severity
    $colour = if ($severity -eq 'Error') { 'Red' } else { 'Yellow' }
    $label = if ($severity -eq 'Error') { 'ERROR' } else { 'WARNING' }
    Write-Host ("`n  [{0}] {1} - {2}  ({3} found)" -f $label, $group.Group[0].Platform, $group.Group[0].Category, $group.Count) -ForegroundColor $colour

    $shown = 0
    foreach ($finding in $group.Group) {
        if ($shown -ge $MaxFindings) { break }
        Write-Host "      - $($finding.Message)"
        $shown++
    }
    if ($group.Count -gt $shown) {
        Write-Host "      ... and $($group.Count - $shown) more not shown (re-run with -MaxFindings $($group.Count) to see them all)" -ForegroundColor DarkGray
    }

    if ($severity -eq 'Error') { $errorCount += $group.Count } else { $warningCount += $group.Count }
}

Write-Host ''
Write-Host "  $errorCount error(s), $warningCount warning(s)." -ForegroundColor White

$categories = @($active | ForEach-Object { $_.Category } | Sort-Object -Unique)

Write-Host ''
Write-Host '  What to do next:' -ForegroundColor White
if ($categories -contains 'Parity: missing translation') {
    Write-Host '   - "missing translation": add the key to that language file. Every user-facing' -ForegroundColor Gray
    Write-Host '     string is a 9-file change; if you cannot translate it well, add the key with' -ForegroundColor Gray
    Write-Host '     the English text as a placeholder and file a bead labelled i18n.' -ForegroundColor Gray
}
if ($categories -contains 'Parity: orphaned translation') {
    Write-Host '   - "orphaned translation": the key was removed from English but left behind.' -ForegroundColor Gray
    Write-Host '     Remove it from that language file too, so all 9 files stay in step.' -ForegroundColor Gray
}
if ($categories -contains 'Parity: empty translation') {
    Write-Host '   - "empty translation": the key is present but has no text, so the user sees' -ForegroundColor Gray
    Write-Host '     nothing at all. Fill it in, or remove the key from all 9 files.' -ForegroundColor Gray
}
if ($categories -contains 'Parity: translated an invariant') {
    Write-Host '   - "translated an invariant": English marks this key translatable="false", so the' -ForegroundColor Gray
    Write-Host '     locale copies are dead weight. Delete them, or drop the translatable attribute' -ForegroundColor Gray
    Write-Host '     if the string really does need translating.' -ForegroundColor Gray
}
if ($categories -contains 'Undefined: key used but never declared') {
    Write-Host '   - "key used but never declared": the app will show the user the raw key name in' -ForegroundColor Gray
    Write-Host '     every language. Either add the key to all 9 files, or fix the typo in the code.' -ForegroundColor Gray
}
if ($categories -like 'Placeholder:*') {
    Write-Host '   - "locale has one English lacks": nothing supplies that argument, so the user' -ForegroundColor Gray
    Write-Host '     of that language sees the placeholder printed literally, or the screen throws.' -ForegroundColor Gray
    Write-Host '     Check how the key is consumed before editing: if the call site does not use' -ForegroundColor Gray
    Write-Host '     string.Format at all, the whole placeholder has to go.' -ForegroundColor Gray
    Write-Host '   - "locale is missing one": that argument is dropped for that language only, so' -ForegroundColor Gray
    Write-Host '     the sentence loses a number or name. Put it back where the grammar wants it -' -ForegroundColor Gray
    Write-Host '     the position may differ from English, which is what indexed placeholders are for.' -ForegroundColor Gray
    Write-Host '   - "malformed": a literal brace must be doubled ({{ }}) in a .resx value, and a' -ForegroundColor Gray
    Write-Host '     literal percent must be doubled (%%) in an Android string.' -ForegroundColor Gray
}
if ($categories -like '*Staleness*') {
    Write-Host '   - Staleness findings are heuristics, not proof. Read the value and decide.' -ForegroundColor Gray
    Write-Host '     A short cognate or a deliberate loanword is fine; an English sentence sitting' -ForegroundColor Gray
    Write-Host '     in the Polish file is not. File a bead labelled i18n for the real ones.' -ForegroundColor Gray
}
Write-Host '   - If a finding is wrong, tune -SimilarityThreshold / -MinSimilarityLength rather' -ForegroundColor Gray
Write-Host '     than deleting the check. If it is right but not yours to fix today, file a bead.' -ForegroundColor Gray

# THE ONLY LINE OF THIS SCRIPT A CALLER CAN READ. Everything above goes through Write-Host, which
# writes to the console and cannot be captured or piped - so verify.ps1 could see the exit code and
# nothing else, and printed "Translations are complete and current" over the top of warnings it had
# no way to know about (RemEx-0bygp). This goes to the success stream on purpose.
Write-Output "LOCALIZATION-SUMMARY errors=$errorCount warnings=$warningCount known=$($known.Count)"

$failed = ($errorCount -gt 0) -or ($StrictStaleness -and $warningCount -gt 0)
if ($failed) { exit 1 }
exit 0

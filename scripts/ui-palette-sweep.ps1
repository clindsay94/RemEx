<#
.SYNOPSIS
    Scripted palette sweep replacing the dead "check all four themes" UI verification axis
    (RemEx-8q7de). See docs/UI-PALETTE-SWEEP.md for why the four-theme axis stopped meaning
    anything and what this sweep checks instead.

.DESCRIPTION
    The old axis was four named themes, each a hand-authored resource dictionary. Since
    RemEx-07jij every palette is GENERATED from a seed colour, so "check all four themes" no
    longer covers the surface that can break — it covers four points on a space with millions.
    The axis this script drives instead: the shipped default preset, plus three adversarial
    seeds (near-white, near-black, max-chroma) picked to stress the generator rather than to
    look good, each crossed with light/dark mode and contrast 0.0/1.0. That is
    1 + 3 seeds x 2 modes x 2 contrasts = 13 cells (-ListCells enumerates them by reading the
    data below, not by restating it here).

    Each cell is captured against every scriptable view (Home, Sensors, Commands, Launcher,
    Processes, Files, Logs, Settings, About) using Remex.Desktop's `--view <Name>` launch
    argument (RemEx-8q7de) — NOT keystroke injection. The original design navigated with
    Ctrl+D1..D7 / Ctrl+OemComma via SendKeys; that is banned in this repo
    (eyes-pass-no-os-keystroke-injection memory) because the nav list items expose no
    InvokePattern for UI Automation to click instead. So this script launches the host ONCE PER
    CELL x VIEW: stop the host, write the profile, start it with --view X, wait for the window,
    screenshot, stop it. RemoteDesktop is not in the view list — it needs a connected phone to
    show anything meaningful and stays a manual cell in the ledger.

    SAFETY: this script rewrites the user's live dashboard_layout.json. It is backed up to
    dashboard_layout.json.sweep-backup before the first write and restored in a finally block. A
    pre-existing backup means a previous run died before restoring — the script REFUSES to start,
    because that backup is the real profile and overwriting it would be the second bug.

    This script never builds. -Start always carries -NoBuild; if the Debug host is not already
    built, run scripts/ui-hotreload.ps1 -Start once by hand first (see its own header for why
    building while a host runs corrupts the artifact it is about to screenshot).

.PARAMETER Out
    Path PREFIX for screenshots and the index. Cell/view captures are written as
    "<Out><Cell>-<View>.png" (plus a same-named UI-tree .txt from ui-snapshot.ps1) and the ledger
    as "<Out>index.md". Default: a fresh timestamped folder under $env:TEMP\remex-ui.

.PARAMETER Cells
    Restrict the run to these cell ids (see -ListCells). Unknown ids are a hard error.

.PARAMETER ListCells
    Print the matrix and exit. Touches nothing.

.PARAMETER DryRun
    Print the per-cell x view capture plan and exit. Touches nothing — no host start/stop, no
    profile read or write, no backup created.

.PARAMETER SettleMs
    Milliseconds to wait after the window appears before screenshotting, so the palette-crossfade
    transition (MainWindow.axaml Classes="palette-crossfade") has finished.

.EXAMPLE
    pwsh scripts/ui-palette-sweep.ps1 -ListCells
    pwsh scripts/ui-palette-sweep.ps1 -DryRun
    pwsh scripts/ui-palette-sweep.ps1 -Cells Default,Chroma-Dark-C1
    pwsh scripts/ui-palette-sweep.ps1
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [string]$Out = (Join-Path $env:TEMP ('remex-ui\sweep-{0:yyyyMMdd-HHmmss}\' -f (Get-Date))),
    [string[]]$Cells,
    [switch]$ListCells,
    [switch]$DryRun,
    [int]$SettleMs = 2500
)

$ErrorActionPreference = 'Stop'

# UI Automation (ui-snapshot.ps1) is Windows-only. Exit cleanly rather than fail everywhere else,
# same convention as the rest of the ui-*.ps1 family.
#
# [System.Runtime.InteropServices.RuntimeInformation], NOT $IsWindows (RemEx-8q7de round 2):
# $IsWindows does not exist before PowerShell 6, so under Windows PowerShell 5.1 - which
# `powershell.exe -File` reaches for by default even ON Windows - the variable is $null, "-not
# $null" is true, and this script reported "Windows-only, exiting cleanly" with exit 0 on the one
# platform it is actually meant to run on. The #Requires above already refuses 5.1 outright; this
# check is what actually decides Windows vs. not, on any version that gets past it.
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    Write-Warning 'ui-palette-sweep.ps1 drives UI Automation, which is Windows-only. Exiting cleanly.'
    exit 0
}

$hotReloadScript = Join-Path $PSScriptRoot 'ui-hotreload.ps1'
$snapshotScript = Join-Path $PSScriptRoot 'ui-snapshot.ps1'

# ═══════════════════════════════════════════════════════════════════════════════════════════════
# THE MATRIX. One copy, as data — docs/UI-PALETTE-SWEEP.md references -ListCells rather than
# duplicating this table, so there is exactly one place a cell can be wrong.
#
# Default is the shipped preset (SeedPresetCatalog.BaseDarkGlass, remex.desktop/Models/
# SeedPreset.cs): baseTheme=BaseDarkGlass, seed #6C4CFF, TonalSpot, dark, contrast 0. The three
# adversarial seeds all ride baseTheme=Dynamic (Models/SeedPreset.cs: Dynamic is the preset that
# keeps whatever seed is written rather than overriding it):
#   Chalk  #F5F5F5 - near-white   (very light lightness)
#   Ink    #0B0B0F - near-black   (very dark lightness)
#   Chroma #00FF00 - pure green   (max chroma / saturation)
# each x Mode(Light,Dark) x Contrast(0.0,1.0) = 3 x 2 x 2 = 12, plus Default = 13 cells.
# ═══════════════════════════════════════════════════════════════════════════════════════════════
$Script:CellMatrix = @(
    [ordered]@{ Id = 'Default';         ThemeId = 'BaseDarkGlass'; Seed = '#6C4CFF'; SchemeVariant = 'TonalSpot'; Mode = 'Dark';  Contrast = 0.0 }

    [ordered]@{ Id = 'Chalk-Light-C0';  ThemeId = 'Dynamic'; Seed = '#F5F5F5'; SchemeVariant = 'TonalSpot'; Mode = 'Light'; Contrast = 0.0 }
    [ordered]@{ Id = 'Chalk-Light-C1';  ThemeId = 'Dynamic'; Seed = '#F5F5F5'; SchemeVariant = 'TonalSpot'; Mode = 'Light'; Contrast = 1.0 }
    [ordered]@{ Id = 'Chalk-Dark-C0';   ThemeId = 'Dynamic'; Seed = '#F5F5F5'; SchemeVariant = 'TonalSpot'; Mode = 'Dark';  Contrast = 0.0 }
    [ordered]@{ Id = 'Chalk-Dark-C1';   ThemeId = 'Dynamic'; Seed = '#F5F5F5'; SchemeVariant = 'TonalSpot'; Mode = 'Dark';  Contrast = 1.0 }

    [ordered]@{ Id = 'Ink-Light-C0';    ThemeId = 'Dynamic'; Seed = '#0B0B0F'; SchemeVariant = 'TonalSpot'; Mode = 'Light'; Contrast = 0.0 }
    [ordered]@{ Id = 'Ink-Light-C1';    ThemeId = 'Dynamic'; Seed = '#0B0B0F'; SchemeVariant = 'TonalSpot'; Mode = 'Light'; Contrast = 1.0 }
    [ordered]@{ Id = 'Ink-Dark-C0';     ThemeId = 'Dynamic'; Seed = '#0B0B0F'; SchemeVariant = 'TonalSpot'; Mode = 'Dark';  Contrast = 0.0 }
    [ordered]@{ Id = 'Ink-Dark-C1';     ThemeId = 'Dynamic'; Seed = '#0B0B0F'; SchemeVariant = 'TonalSpot'; Mode = 'Dark';  Contrast = 1.0 }

    [ordered]@{ Id = 'Chroma-Light-C0'; ThemeId = 'Dynamic'; Seed = '#00FF00'; SchemeVariant = 'TonalSpot'; Mode = 'Light'; Contrast = 0.0 }
    [ordered]@{ Id = 'Chroma-Light-C1'; ThemeId = 'Dynamic'; Seed = '#00FF00'; SchemeVariant = 'TonalSpot'; Mode = 'Light'; Contrast = 1.0 }
    [ordered]@{ Id = 'Chroma-Dark-C0';  ThemeId = 'Dynamic'; Seed = '#00FF00'; SchemeVariant = 'TonalSpot'; Mode = 'Dark';  Contrast = 0.0 }
    [ordered]@{ Id = 'Chroma-Dark-C1';  ThemeId = 'Dynamic'; Seed = '#00FF00'; SchemeVariant = 'TonalSpot'; Mode = 'Dark';  Contrast = 1.0 }
)

# The nine views --view opens (Remex.Desktop.Services.StartupViewArgument.Navigators), in
# Ctrl+D1..D7 / Ctrl+OemComma / (no binding) order (MainWindow.axaml:34-53). RemoteDesktop is
# deliberately absent: it needs a connected phone to show anything and stays a manual cell.
$Script:Views = @('Home', 'Sensors', 'Commands', 'Launcher', 'Processes', 'Files', 'Logs', 'Settings', 'About')
$Script:ManualViews = @('RemoteDesktop')

if ($ListCells) {
    $Script:CellMatrix | ForEach-Object { [pscustomobject]$_ } | Format-Table -AutoSize
    exit 0
}

$knownIds = $Script:CellMatrix | ForEach-Object { $_.Id }

if ($Cells) {
    $unknown = $Cells | Where-Object { $knownIds -notcontains $_ }
    if ($unknown) {
        throw "Unknown cell id(s): $($unknown -join ', '). Run -ListCells to see valid ids."
    }
    # @(...) forces an array even when exactly one cell matches — PowerShell unwraps a
    # single-item pipeline result to the bare object, which would make $selected.Count report
    # the CELL'S OWN key count instead of "1" (e.g. -Cells Default alone reported "6 cell(s)").
    $selected = @($Script:CellMatrix | Where-Object { $Cells -contains $_.Id })
}
else {
    $selected = @($Script:CellMatrix)
}

if ($DryRun) {
    $totalAuto = $selected.Count * $Script:Views.Count
    $totalManual = $selected.Count * $Script:ManualViews.Count
    Write-Host "Palette sweep plan: $($selected.Count) cell(s) x $($Script:Views.Count) view(s) = $totalAuto scripted capture(s), plus $totalManual manual cell(s)." -ForegroundColor Cyan
    foreach ($cell in $selected) {
        foreach ($view in $Script:Views) {
            Write-Host ("  {0} / {1} -> {2}{0}-{1}.png" -f $cell.Id, $view, $Out)
        }
        foreach ($view in $Script:ManualViews) {
            Write-Host ("  {0} / {1} -> manual (not swept)" -f $cell.Id, $view)
        }
    }
    Write-Host "Would write: $($Out)index.md" -ForegroundColor Cyan
    exit 0
}

# ═══════════════════════════════════════════════════════════════════════════════════════════════
# THE REAL RUN. Backup-first / restore-in-finally / refuse-on-stale-backup is not optional: a
# crash between write and restore leaves an adversarial palette as the user's actual profile.
# ═══════════════════════════════════════════════════════════════════════════════════════════════
$profilePath = Join-Path $env:LOCALAPPDATA 'Remex\dashboard_layout.json'
$backupPath = "$profilePath.sweep-backup"

if (Test-Path $backupPath) {
    throw "A backup already exists at '$backupPath' - a previous sweep run died before restoring it. THAT is the real profile. Restore it to '$profilePath' by hand (or delete the backup once you've confirmed it isn't needed) before running the sweep again."
}

if (-not (Test-Path $profilePath)) {
    throw "No profile found at '$profilePath' - nothing to sweep against. Launch RemEx once to create one."
}

# Fails loudly instead of silently restoring/relaunching over a host that could still be reading
# or writing the profile (RemEx-8q7de round 2, CRITICAL). Every -Stop the loop below issues
# carries -NoRelaunch, so by the time control reaches `finally` nothing should be alive - if
# something is, that is exactly the bug this exists to catch rather than paper over.
function Assert-NoRemexProcessAlive([string]$When) {
    $alive = Get-Process -Name 'Remex.Agent', 'Remex.Desktop' -ErrorAction SilentlyContinue
    if ($alive) {
        $names = ($alive | ForEach-Object { "$($_.ProcessName) (pid $($_.Id))" }) -join ', '
        throw "REFUSING TO CONTINUE: a Remex process is still alive $When - $names. The sweep must never restore or relaunch while a host could still be touching the profile."
    }
}

# Remembered so the ORIGINAL state is what gets put back, not an assumption. If nothing was
# running before the sweep started, nothing should be running after it either.
$wasRunningBeforeSweep = [bool](Get-Process -Name Remex.Agent -ErrorAction SilentlyContinue)

$outDir = Split-Path -Parent $Out
if ($outDir -and -not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

$index = [System.Collections.Generic.List[object]]::new()

Copy-Item -Path $profilePath -Destination $backupPath
Write-Host "Backed up '$profilePath' to '$backupPath'." -ForegroundColor Cyan

try {
    foreach ($cell in $selected) {
        Write-Host "── Cell $($cell.Id) ──" -ForegroundColor Yellow

        # Stop BEFORE every profile write, same reasoning as ui-hotreload's own -Start: a running
        # host holds its own idea of the profile and can save over ours mid-sweep. -NoRelaunch:
        # the sweep is about to start ANOTHER host in a moment, so relaunching the installed
        # Release build here would just hand it the file we are mid-write on (RemEx-8q7de round 2).
        & $hotReloadScript -Stop -NoRelaunch | Out-Null

        $profileJson = Get-Content -Raw -Path $profilePath | ConvertFrom-Json
        if (-not $profileJson.PSObject.Properties['customization']) {
            $profileJson | Add-Member -NotePropertyName 'customization' -NotePropertyValue ([pscustomobject]@{})
        }
        $customization = $profileJson.customization

        # Only the customization fields the sweep cares about — everything else in the profile
        # (canvas layout, connection history, sensor alerts...) passes through untouched.
        $customization | Add-Member -NotePropertyName 'schemaVersion'   -NotePropertyValue 2                  -Force
        $customization | Add-Member -NotePropertyName 'baseTheme'       -NotePropertyValue $cell.ThemeId       -Force
        $customization | Add-Member -NotePropertyName 'accentColor'     -NotePropertyValue $cell.Seed          -Force
        $customization | Add-Member -NotePropertyName 'schemeVariant'   -NotePropertyValue $cell.SchemeVariant -Force
        $customization | Add-Member -NotePropertyName 'themeContrast'   -NotePropertyValue $cell.Contrast      -Force
        $customization | Add-Member -NotePropertyName 'themeSeedChroma' -NotePropertyValue 48.0                -Force
        $customization | Add-Member -NotePropertyName 'themeMode'       -NotePropertyValue $cell.Mode          -Force

        # UTF-8 no BOM (matching DashboardLayoutService.JsonOptions' own contract for this file),
        # written to a temp sibling and moved into place (RemEx-8q7de round 2, HIGH). A relaunched
        # host was already stopped -NoRelaunch above, but writing this file is still not something
        # to do non-atomically: Move-Item within the same volume is a rename, so any reader only
        # ever sees the old complete file or the new complete one, never a torn read that
        # DashboardLayoutService.ReadAndMigrate would treat as corrupt and rename to .bak.
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        $tempProfilePath = "$profilePath.sweeptmp"
        [System.IO.File]::WriteAllText($tempProfilePath, ($profileJson | ConvertTo-Json -Depth 20), $utf8NoBom)
        Move-Item -Path $tempProfilePath -Destination $profilePath -Force

        foreach ($view in $Script:Views) {
            & $hotReloadScript -Start -NoBuild -AppArgs "--view $view" | Out-Null

            $proc = $null
            for ($i = 0; $i -lt 20 -and -not $proc; $i++) {
                Start-Sleep -Milliseconds 500
                $proc = Get-Process -Name Remex.Agent -ErrorAction SilentlyContinue
            }
            if (-not $proc) {
                throw "Remex.Agent did not start for cell '$($cell.Id)' view '$view'."
            }

            Start-Sleep -Milliseconds $SettleMs

            $shotPath = '{0}{1}-{2}.png' -f $Out, $cell.Id, $view
            & $snapshotScript -Screenshot -Tree -Out $shotPath | Out-Null

            $index.Add([pscustomobject]@{ Cell = $cell.Id; View = $view; Screenshot = $shotPath; Finding = 'not run' })

            # -NoRelaunch here too - see the pre-write -Stop above. Every stop inside this loop
            # must leave nothing running; only the very last stop, after the loop, may relaunch.
            & $hotReloadScript -Stop -NoRelaunch | Out-Null
        }

        foreach ($view in $Script:ManualViews) {
            $index.Add([pscustomobject]@{ Cell = $cell.Id; View = $view; Screenshot = 'manual'; Finding = 'manual' })
        }
    }
}
finally {
    # A live host reading or writing the profile at the moment of restore is exactly the bug this
    # whole safety net exists to prevent (RemEx-8q7de round 2, CRITICAL) - fail loudly rather than
    # restore underneath it.
    Assert-NoRemexProcessAlive 'immediately before restoring the profile'

    # Restore FIRST, delete the backup only once the restore itself has succeeded — a Remove-Item
    # ahead of a failed Copy-Item would delete the only remaining copy of the real profile.
    Copy-Item -Path $backupPath -Destination $profilePath -Force
    Remove-Item -Path $backupPath
    Write-Host "Restored '$profilePath' from backup." -ForegroundColor Cyan

    Assert-NoRemexProcessAlive 'immediately after restoring the profile'

    # Put the machine back exactly how the sweep found it: relaunch the installed Release build
    # ONLY if one was already running before the sweep began, and only now that the real profile
    # is safely back on disk. Every -Stop during the sweep used -NoRelaunch, so this is the one
    # normal (relaunching) -Stop path in the whole run.
    if ($wasRunningBeforeSweep) {
        & $hotReloadScript -Stop | Out-Null
        Write-Host 'Relaunched the installed Release build (it was running before the sweep began).' -ForegroundColor Cyan
    }
}

$indexPath = "{0}index.md" -f $Out
$lines = @('| Cell | View | Screenshot | Finding |', '|---|---|---|---|')
$lines += $index | ForEach-Object { "| $($_.Cell) | $($_.View) | $($_.Screenshot) | $($_.Finding) |" }
Set-Content -Path $indexPath -Value ($lines -join "`r`n") -Encoding utf8
Write-Host "Wrote index: $indexPath" -ForegroundColor Green

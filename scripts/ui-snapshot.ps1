<#
.SYNOPSIS
    Screenshots the running RemEx window and/or dumps its UI Automation tree (RemEx-1us2w).

.DESCRIPTION
    remex.desktop.tests has NO headless render - every UI test is a text assertion over .axaml
    source. That is why RemEx-b8dxy (the whole shell covered by an opaque SideSheet) and
    RemEx-27a0s (the palette's activation falling through to another app) both shipped past 2939
    green tests. This script is the missing eye.

    The two outputs answer different questions, and the PAIR is what makes a diagnosis:

      -Screenshot  what the user actually sees.
      -Tree        what Avalonia thinks it laid out - control types, names, bounds, offscreen flags.

    An intact tree over a flat-colour screenshot means the shell is being PAINTED OVER, not
    mis-laid-out. That single comparison is what identified RemEx-b8dxy in one step.

.EXAMPLE
    pwsh scripts/ui-snapshot.ps1 -Screenshot -Tree
    pwsh scripts/ui-snapshot.ps1 -Screenshot -Out C:\tmp\shell.png
#>
[CmdletBinding()]
param(
    [switch]$Screenshot,
    [switch]$Tree,

    # Where the PNG goes. The tree is written alongside it with a .txt extension.
    [string]$Out,

    # Restore the window first if it is minimised. A minimised Avalonia window reports its rect as
    # (-32000,-32000), which screenshots as whatever happens to be on screen at that position.
    [switch]$NoRestore,

    # Match a different window of the process (e.g. 'Command Palette').
    [string]$WindowTitle = 'RemEx*'
)

$ErrorActionPreference = 'Stop'
if (-not $Screenshot -and -not $Tree) { $Screenshot = $true; $Tree = $true }

if (-not $Out) {
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) 'remex-ui'
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $Out = Join-Path $dir ('shell-{0:yyyyMMdd-HHmmss}.png' -f (Get-Date))
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

$proc = Get-Process -Name Remex.Agent -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw 'Remex.Agent is not running. Start it with scripts/ui-hotreload.ps1 -Start.' }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)

$target = $null
foreach ($w in $windows) { if ($w.Current.Name -like $WindowTitle) { $target = $w } }
if (-not $target) {
    $names = @(foreach ($w in $windows) { "'" + $w.Current.Name + "'" }) -join ', '
    throw "No window matching '$WindowTitle'. Windows present: $names"
}

if (-not $NoRestore) {
    try {
        $wp = $target.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
        Start-Sleep -Milliseconds 1000
    }
    catch { Write-Verbose "Could not restore the window: $_" }
}

$r = $target.Current.BoundingRectangle
Write-Host ("window '{0}': {1},{2} {3}x{4}" -f $target.Current.Name, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)

if ($Screenshot) {
    $bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
    $g.Dispose()
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)

    # A dead-flat window is the signature of something opaque covering the shell, so say so up front
    # rather than making the reader spot it. Sampled on a 7px lattice - enough to be decisive, cheap
    # enough to stay instant on a 4K window.
    $counts = @{}
    for ($y = 0; $y -lt $bmp.Height; $y += 7) {
        for ($x = 0; $x -lt $bmp.Width; $x += 7) {
            $c = $bmp.GetPixel($x, $y)
            $k = '{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B
            if ($counts.ContainsKey($k)) { $counts[$k]++ } else { $counts[$k] = 1 }
        }
    }
    $bmp.Dispose()
    $total = ($counts.Values | Measure-Object -Sum).Sum
    $top = $counts.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1
    $pct = [math]::Round(100 * $top.Value / $total, 1)
    Write-Host "screenshot: $Out"
    Write-Host ("dominant colour: #{0} covers {1}% of the window" -f $top.Key, $pct)
    if ($pct -ge 90) {
        Write-Warning ("The window is essentially one flat colour (#{0}). If the UIA tree below is " -f $top.Key +
            'intact, the shell is being PAINTED OVER rather than mis-laid-out - see the ' +
            '"Desktop shell - Material.Avalonia template parts" guard in docs/REGRESSION-GUARDS.md.')
    }
}

if ($Tree) {
    $treePath = [System.IO.Path]::ChangeExtension($Out, '.txt')
    $sb = New-Object System.Text.StringBuilder

    function Format-Coord($v) {
        if ([double]::IsInfinity($v) -or [double]::IsNaN($v)) { return 'inf' }
        return [string][int]$v
    }

    function Add-Element($el, $depth) {
        if ($depth -gt 12) { return }
        $pad = ' ' * ($depth * 2)
        try {
            $c = $el.Current
            $ct = $c.ControlType.ProgrammaticName -replace 'ControlType\.', ''
            $rect = $c.BoundingRectangle
            [void]$sb.AppendLine(('{0}{1} id=''{2}'' name=''{3}'' rect=({4},{5} {6}x{7}) offscreen={8} enabled={9}' -f `
                        $pad, $ct, $c.AutomationId, $c.Name,
                (Format-Coord $rect.X), (Format-Coord $rect.Y),
                (Format-Coord $rect.Width), (Format-Coord $rect.Height),
                    $c.IsOffscreen, $c.IsEnabled))
        }
        catch {
            [void]$sb.AppendLine("$pad<unreadable element: $($_.Exception.Message)>")
            return
        }
        foreach ($k in $el.FindAll([System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.Condition]::TrueCondition)) {
            Add-Element $k ($depth + 1)
        }
    }

    Add-Element $target 0
    [System.IO.File]::WriteAllText($treePath, $sb.ToString())
    $lineCount = ($sb.ToString() -split "`n").Count
    Write-Host "uia tree:   $treePath  ($lineCount lines)"
}

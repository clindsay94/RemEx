<#
.SYNOPSIS
    Builds the RemEx Windows installer.

.DESCRIPTION
    1. Reads the canonical version from RemEx.Android/app/version.properties
    2. Patches Directory.Build.props to match
    3. Publishes the desktop client (self-contained, win-x64)
    4. Compiles the Inno Setup script to produce the installer EXE

.PARAMETER SkipPublish
    Skip the dotnet publish step (use existing publish output).

.PARAMETER SkipVersionSync
    Skip syncing Directory.Build.props (use whatever version is already set).

.EXAMPLE
    .\installer\build-installer.ps1
    .\installer\build-installer.ps1 -SkipPublish
#>
param(
    [switch]$SkipPublish,
    [switch]$SkipVersionSync
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot    = Resolve-Path (Join-Path $PSScriptRoot "..")
$VersionFile = Join-Path $RepoRoot "RemEx.Android" "app" "version.properties"
$BuildProps  = Join-Path $RepoRoot "Directory.Build.props"
$IssFile     = Join-Path $PSScriptRoot "RemEx.iss"
$ClientProj  = Join-Path $RepoRoot "Remex.Client.Desktop"

# ------------------------------------------------------------------
# 1. Read version from version.properties
# ------------------------------------------------------------------
if (-not (Test-Path $VersionFile)) {
    Write-Error "version.properties not found at: $VersionFile"
    exit 1
}

$versionProps = Get-Content $VersionFile -Raw | ConvertFrom-StringData
$version = $versionProps["versionName"]

if (-not $version) {
    Write-Error "Could not read versionName from version.properties"
    exit 1
}

Write-Host "Version: $version" -ForegroundColor Cyan

# ------------------------------------------------------------------
# 2. Patch Directory.Build.props
# ------------------------------------------------------------------
if (-not $SkipVersionSync) {
    $content = Get-Content $BuildProps -Raw
    $patched = $content -replace '<Version>[^<]*</Version>', "<Version>$version</Version>"
    if ($content -ne $patched) {
        Set-Content $BuildProps $patched -NoNewline
        Write-Host "Patched Directory.Build.props -> $version" -ForegroundColor Green
    } else {
        Write-Host "Directory.Build.props already at $version" -ForegroundColor DarkGray
    }
}

# ------------------------------------------------------------------
# 3. Publish the desktop client
# ------------------------------------------------------------------
if (-not $SkipPublish) {
    Write-Host "Publishing Remex.Client.Desktop (self-contained, win-x64)..." -ForegroundColor Cyan
    dotnet publish $ClientProj -c Release -r win-x64 --self-contained
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed (exit $LASTEXITCODE)"
        exit 1
    }
    Write-Host "Publish complete." -ForegroundColor Green
}

# ------------------------------------------------------------------
# 4. Compile the Inno Setup script
# ------------------------------------------------------------------
$iscc = Get-Command iscc -ErrorAction SilentlyContinue
$useWine = $false

if (-not $iscc) {
    # Common install locations for Inno Setup 6 (Windows native paths)
    $candidates = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $iscc = $c; break }
    }
}

if (-not $iscc) {
    # Wine prefix locations (Linux)
    $wineCandidates = @(
        "$HOME/.wine/drive_c/users/$env:USER/AppData/Local/Programs/Inno Setup 6/ISCC.exe",
        "$HOME/.wine/drive_c/Program Files (x86)/Inno Setup 6/ISCC.exe",
        "$HOME/.wine/drive_c/Program Files/Inno Setup 6/ISCC.exe"
    )
    foreach ($c in $wineCandidates) {
        if (Test-Path $c) { $iscc = $c; $useWine = $true; break }
    }
}

if (-not $iscc) {
    Write-Error "ISCC.exe (Inno Setup compiler) not found. Install Inno Setup 6 from https://jrsoftware.org/isinfo.php (Windows) or via Wine (Linux)."
    exit 1
}

$isccPath = if ($iscc -is [string]) { $iscc } else { $iscc.Source }

Write-Host "Compiling installer with Inno Setup..." -ForegroundColor Cyan
if ($useWine) {
    # Convert the Linux path to a Wine Windows path for ISCC, then invoke via wine
    $wineIsccPath = & wine winepath -w $isccPath 2>/dev/null
    $wineIssFile  = & wine winepath -w $IssFile   2>/dev/null
    & wine $isccPath "/DAppVersion=$version" $wineIssFile
} else {
    & $isccPath "/DAppVersion=$version" $IssFile
}
if ($LASTEXITCODE -ne 0) {
    Write-Error "iscc failed (exit $LASTEXITCODE)"
    exit 1
}

$outputExe = Join-Path $PSScriptRoot "Output\RemEx-v$version-Setup.exe"
Write-Host ""
Write-Host "=====================================================" -ForegroundColor Green
Write-Host "  Installer built successfully!" -ForegroundColor Green
Write-Host "  Output: $outputExe" -ForegroundColor Green
Write-Host "=====================================================" -ForegroundColor Green

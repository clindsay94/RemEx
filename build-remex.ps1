#!/usr/bin/env pwsh
<#
.SYNOPSIS
    RemEx Unified Build and Packaging Tool.

.DESCRIPTION
    A complete script to clean all project artifacts, restore .NET dependencies,
    compile, publish, and package the RemEx PC app (Windows installer / Linux
    remex-agent package) and the Android app. Consolidates APKs, AABs, and PC
    installers into a single `build_output` folder.

    Every run shows the current version and offers a 5-second y/N chance to change it before
    building. It defaults to NO, so an unattended build never bumps anything. Answer yes and it
    prompts for the new MAJOR.MINOR.PATCH, increments versionCode, and writes both
    remex.android/app/version.properties (Android) and Directory.Build.props (Windows/Linux .NET) —
    the pair remex.desktop.tests/Services/VersionSourceOfTruthTests.cs requires to agree.
    Pass -Version to state the version up front and skip that prompt entirely.

.PARAMETER Version
    Build at this version (MAJOR.MINOR.PATCH), skipping the interactive version prompt.

    If it differs from what version.properties currently holds, the version files are rewritten and
    versionCode is incremented — the same edit the prompt makes, just stated up front instead of
    typed at a prompt. If it matches, nothing is written. Use it to script a release build, or to
    say "yes, this version, don't ask me" on a normal one.

.PARAMETER Config
    Build configuration. Valid options are 'debug' and 'release'. Defaults to 'release'.

.PARAMETER Target
    Build target platform. Valid options are 'android', 'linux', 'windows', or 'all'. Defaults to 'all'.

.PARAMETER NonInteractive
    Skip interactive prompts and use default parameters or passed parameters immediately.

.PARAMETER NoClean
    Skip the clean phase. Keeps existing artifacts/, bin/, and obj/ folders for faster
    incremental rebuilds. Use this when iterating on a single platform and you don't need
    a pristine build environment. Example: -t windows -NoClean

.PARAMETER AdbInstall
    After a successful Android build, push the staged APK to your connected device with
    'adb install -r'. Opt-in and off by default, even for -t android / -t all: a build should
    never touch a device you didn't ask it to.

    Wireless debugging often isn't up on the first look, so the device check runs twice with a
    pause in between. If it still finds nothing, you get a 5-second y/N countdown to retry adb
    or skip; on timeout it skips. Skipping never fails the build — the APK is still staged in
    build_output/android and you can install it by hand.

.PARAMETER InstallLocal
    After a successful Windows build, update your installed copy at "$env:ProgramFiles\RemEx"
    by handing off to scripts/update-local-install.ps1 -SkipPublish, which reuses the publish
    output this script just produced.

    That script is invoked rather than inlined on purpose: it carries
    '#Requires -RunAsAdministrator' (which would otherwise force elevation on every build,
    including Android-only ones), it stops and restarts the RemEx logon task, and it is the
    documented standalone fast-update loop. One copy of that logic, called from two places.

    Release only — update-local-install.ps1 reads artifacts/publish/remex.agent/release_win-x64.

.EXAMPLE
    ./build-remex.ps1 -c release -t all
.EXAMPLE
    ./build-remex.ps1 -v 2.5.0 -t all -c release
.EXAMPLE
    ./build-remex.ps1 -t windows -NoClean
.EXAMPLE
    ./build-remex.ps1 -t android -NoClean -AdbInstall
.EXAMPLE
    ./build-remex.ps1 -t windows-client -NoClean -InstallLocal
.EXAMPLE
    ./build-remex.ps1 (starts interactive wizard if no args are specified)

.LINK
    scripts/update-local-install.ps1
#>
param(
    [Parameter(Mandatory=$false)]
    [Alias("c")]
    [ValidateSet("debug", "release")]
    [string]$Config = "",

    [Parameter(Mandatory=$false)]
    [Alias("t")]
    [ValidateSet("android", "linux", "windows", "all", "windows-client", "installer", "apk")]
    [string]$Target = "",

    # "v" is declared explicitly rather than left to prefix matching: this script's [Parameter()]
    # attributes make it an advanced script, so a bare -v would be ambiguous with the -Verbose
    # common parameter. An exact alias resolves first and wins.
    [Parameter(Mandatory=$false)]
    [Alias("v")]
    [string]$Version = "",

    [Parameter(Mandatory=$false)]
    [switch]$NonInteractive,

    [Parameter(Mandatory=$false)]
    [switch]$NoClean,

    [Parameter(Mandatory=$false)]
    [switch]$BrandAssets,

    [Parameter(Mandatory=$false)]
    [switch]$AdbInstall,

    [Parameter(Mandatory=$false)]
    [switch]$InstallLocal
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Establish cross-platform compatibility helper variables
$IsWin = $IsWindows -or ($env:OS -eq "Windows_NT")
$IsLin = -not $IsWin

# Helper function to join paths compatibly across PowerShell 5.1 and pwsh on Windows/Linux
function Join-Paths {
    param(
        [Parameter(Mandatory=$true, Position=0)]
        [string]$Path,
        [Parameter(Mandatory=$true, ValueFromRemainingArguments=$true)]
        [string[]]$ChildPaths
    )
    $result = $Path
    foreach ($child in $ChildPaths) {
        $cleanChild = $child
        if (-not $IsWin) {
            $cleanChild = $cleanChild -replace '\\', '/'
        } else {
            $cleanChild = $cleanChild -replace '/', '\'
        }
        $result = [System.IO.Path]::Combine($result, $cleanChild)
    }
    return $result
}

# Reads sdk.dir out of a Gradle local.properties file without ConvertFrom-StringData, which
# treats Windows paths (e.g. "E:\Utilities\AndroidSDK") as escape sequences and throws on
# unrecognized ones like "\U". Also unescapes Java Properties' own backslash-escaped form
# (e.g. "E\:\\Utilities\\AndroidSDK"), which Gradle/AGP rewrite the file into on Windows.
function Get-LocalPropertiesSdkDir {
    param([Parameter(Mandatory=$true)][string]$LocalPropertiesPath)
    if (-not (Test-Path $LocalPropertiesPath)) { return $null }
    $line = Get-Content $LocalPropertiesPath | Where-Object { $_ -match '^\s*sdk\.dir\s*=' } | Select-Object -Last 1
    if (-not $line) { return $null }
    $value = ($line -split '=', 2)[1]
    return $value -replace '\\:', ':' -replace '\\\\', '\'
}

# A y/N prompt that gives up on its own after $Seconds. Every prompt in this script that can block
# an otherwise-unattended build goes through here: a build left running in another window must not
# sit forever waiting on a keypress nobody is there to give.
function Read-CountdownChoice {
    param(
        [Parameter(Mandatory=$true)][string]$Prompt,
        [int]$Seconds = 5,
        [bool]$DefaultOnTimeout = $false
    )

    if ($NonInteractive) { return $DefaultOnTimeout }

    # A redirected or headless host has no key-reading RawUI; probe before relying on it.
    try {
        $null = $Host.UI.RawUI.KeyAvailable
    } catch {
        return $DefaultOnTimeout
    }

    try {
        while ($Host.UI.RawUI.KeyAvailable) {
            $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        }

        $defaultLabel = if ($DefaultOnTimeout) { "yes" } else { "no" }
        for ($remaining = $Seconds; $remaining -gt 0; $remaining--) {
            Write-Host ("`r  {0} [y/N] {1}s, default {2}...  " -f $Prompt, $remaining, $defaultLabel) -NoNewline -ForegroundColor Yellow
            $deadline = (Get-Date).AddSeconds(1)
            while ((Get-Date) -lt $deadline) {
                if ($Host.UI.RawUI.KeyAvailable) {
                    $key = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
                    $char = "$($key.Character)"
                    if ($char -match '^[yY]$') { Write-Host ""; return $true }
                    if ($char -match '^[nN]$' -or $key.VirtualKeyCode -eq 27) { Write-Host ""; return $false }
                }
                Start-Sleep -Milliseconds 50
            }
        }
        Write-Host ""
        return $DefaultOnTimeout
    } catch {
        Write-Host ""
        return $DefaultOnTimeout
    }
}

# Directory.Build.props carries the .NET assembly version for the whole solution, but
# remex.android/app/version.properties is the source of truth (remex.desktop.tests
# VersionSourceOfTruthTests asserts the two agree). This pushes one to the other, and is called both
# on every build and immediately after a version change so the Windows side never lags the Android side.
# The one place the version is written. Both routes into a version change — the interactive prompt
# and -Version — land here, so the Android and .NET sides can never be updated by one path and
# missed by the other.
function Set-RemexVersion {
    param(
        [Parameter(Mandatory=$true)][string]$VersionFilePath,
        [Parameter(Mandatory=$true)][string]$BuildPropsPath,
        [Parameter(Mandatory=$true)][string]$NewVersion,
        [Parameter(Mandatory=$true)][int]$NewVersionCode
    )

    # Rewrite the two keys in place rather than regenerating the file, so any comment or extra
    # property someone adds to version.properties later survives a bump.
    $updatedLines = foreach ($line in @(Get-Content $VersionFilePath)) {
        if ($line -match '^\s*versionName\s*=') { "versionName=$NewVersion" }
        elseif ($line -match '^\s*versionCode\s*=') { "versionCode=$NewVersionCode" }
        else { $line }
    }
    Set-Content -Path $VersionFilePath -Value $updatedLines
    Write-Host "  Updated version.properties (Android): versionName=$NewVersion, versionCode=$NewVersionCode" -ForegroundColor Green

    Sync-DirectoryBuildPropsVersion -BuildPropsPath $BuildPropsPath -DesiredVersion $NewVersion
    Write-Host "  Both version files now read $NewVersion. Commit them alongside the build." -ForegroundColor Green
}

function Sync-DirectoryBuildPropsVersion {
    param(
        [Parameter(Mandatory=$true)][string]$BuildPropsPath,
        [Parameter(Mandatory=$true)][string]$DesiredVersion
    )

    if (-not (Test-Path $BuildPropsPath)) {
        Write-Warning "Directory.Build.props not found at $BuildPropsPath; .NET version left unsynced."
        return
    }

    $content = Get-Content $BuildPropsPath -Raw
    $patched = $content -replace '<Version>[^<]*</Version>', "<Version>$DesiredVersion</Version>"
    if ($content -ne $patched) {
        Set-Content $BuildPropsPath $patched -NoNewline
        Write-Host "Synchronized Directory.Build.props to version $DesiredVersion" -ForegroundColor Green
    } else {
        Write-Host "Directory.Build.props is already synced at version $DesiredVersion" -ForegroundColor DarkGray
    }
}

# Force output to support emojis/colored text on Windows PowerShell
if ($IsWin) {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
}

Write-Host "==========================================================" -ForegroundColor Green
Write-Host "                 ⚡ RemEx Build System ⚡                 " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green

# Regenerate PC brand icon assets from remex.branding and exit (does not build the app).
if ($BrandAssets) {
    Write-Host "`nRegenerating RemEx brand icon assets..." -ForegroundColor Cyan
    Write-Host "This redraws icon.png, icon.ico, and the MSIX tiles from the shared brand geometry." -ForegroundColor Gray
    dotnet run --project (Join-Paths $PSScriptRoot "tools" "BrandAssetGen") -c Release
    if ($LASTEXITCODE -ne 0) {
        Write-Host "`nBrand asset generation failed (exit $LASTEXITCODE). See the messages above." -ForegroundColor Red
        Write-Host "What to do next: re-run './build-remex.ps1 -BrandAssets'. If a file was 'SKIP (unknown)', a new packaging asset needs a size rule in remex.branding/MsixAssetPlan.cs." -ForegroundColor Yellow
        exit $LASTEXITCODE
    }
    Write-Host "`nDone. Review changes with 'git status' and commit the updated assets." -ForegroundColor Green
    exit 0
}

# 1. Parameter Parsing and Interactive Flow
$interactive = -not $NonInteractive -and [string]::IsNullOrEmpty($Config) -and [string]::IsNullOrEmpty($Target)

if ($interactive) {
    Write-Host "Entering interactive configuration... (Press Enter to accept defaults)" -ForegroundColor Gray
    
    # Configuration Prompt
    $configInput = Read-Host "Are we building release or debug? [release/debug] (default: release)"
    if ([string]::IsNullOrWhiteSpace($configInput)) {
        $Config = "release"
    } else {
        $Config = $configInput.ToLower().Trim()
    }

    # Target Prompt
    $targetInput = Read-Host "Which platform are you targeting? [android/linux/windows/all] (default: all)"
    if ([string]::IsNullOrWhiteSpace($targetInput)) {
        $Target = "all"
    } else {
        $Target = $targetInput.ToLower().Trim()
    }
} else {
    # Set default values if not provided via arguments
    if ([string]::IsNullOrEmpty($Config)) { $Config = "release" }
    if ([string]::IsNullOrEmpty($Target)) { $Target = "all" }
}

$Config = $Config.ToLower().Trim()
$Target = $Target.ToLower().Trim()

$isInstallerTarget = $false
$skipPublish = $false
$skipInstaller = $false

if ($Target -eq "apk") {
    $Target = "android"
}
elseif ($Target -eq "windows-client") {
    $Target = "windows"
    $skipInstaller = $true
}
elseif ($Target -eq "installer") {
    $Target = "windows"
    $isInstallerTarget = $true
}

# Final validation checks
if ($Config -notin @("debug", "release")) {
    Write-Error "Invalid configuration: '$Config'. Must be 'debug' or 'release'."
    exit 1
}
if ($Target -notin @("android", "linux", "windows", "all")) {
    Write-Error "Invalid target: '$Target'. Must be 'android', 'linux', 'windows', or 'all'."
    exit 1
}

Write-Host "Selected Configuration: " -NoNewline -ForegroundColor DarkCyan
Write-Host "$Config" -ForegroundColor Green
Write-Host "Selected Target(s):     " -NoNewline -ForegroundColor DarkCyan
Write-Host "$Target" -ForegroundColor Green
Write-Host "Clean Phase:            " -NoNewline -ForegroundColor DarkCyan
Write-Host "$(if ($NoClean) { 'Skipped' } else { 'Full' })" -ForegroundColor $(if ($NoClean) { 'Yellow' } else { 'Green' })

# Post-build deployment flags. Both are opt-in; drop them with a warning rather than an error
# when the selected target can't produce what they'd deploy, so '-t windows -AdbInstall' still
# builds instead of dying on a flag that was only ever a convenience.
$buildsAndroid = $Target -eq "android" -or $Target -eq "all"
$buildsWindows = $Target -eq "windows" -or $Target -eq "all"

if ($AdbInstall -and -not $buildsAndroid) {
    Write-Warning "-AdbInstall was requested but target '$Target' does not build an APK. Ignoring it."
    $AdbInstall = $false
}
if ($InstallLocal) {
    if (-not $buildsWindows) {
        Write-Warning "-InstallLocal was requested but target '$Target' does not build the Windows agent. Ignoring it."
        $InstallLocal = $false
    } elseif (-not $IsWin) {
        Write-Warning "-InstallLocal updates a Windows ProgramFiles install and cannot run here. On Linux, reinstall with ./installer/build-linux.sh. Ignoring it."
        $InstallLocal = $false
    } elseif ($Config -ne "release") {
        # update-local-install.ps1 -SkipPublish reads artifacts/publish/remex.agent/release_win-x64.
        # A debug build never writes there, so the handoff would either fail or silently install a
        # stale release. Refuse rather than deploy something other than what was just built.
        Write-Error "-InstallLocal requires -c release; update-local-install.ps1 deploys the release_win-x64 publish and a debug build does not produce one."
        exit 1
    }
}

if ($AdbInstall) {
    Write-Host "Post-build:             " -NoNewline -ForegroundColor DarkCyan
    Write-Host "adb install -r (APK)" -ForegroundColor Green
}
if ($InstallLocal) {
    Write-Host "Post-build:             " -NoNewline -ForegroundColor DarkCyan
    Write-Host "update local RemEx install" -ForegroundColor Green
}

# Locate repository root folder
$RepoRoot = $PSScriptRoot
if ([string]::IsNullOrEmpty($RepoRoot)) {
    $RepoRoot = (Get-Location).Path
}
$BuildOutputDir = Join-Paths $RepoRoot "build_output"

if ($isInstallerTarget) {
    $publishDir = Join-Paths $RepoRoot "artifacts" "publish" "remex.agent" "${Config}_win-x64"
    if (Test-Path $publishDir) {
        $skipPublish = $true
    }
}

# 2. Dynamic Version Retrieval from version.properties
$VersionFile = Join-Paths $RepoRoot "remex.android" "app" "version.properties"
if (-not (Test-Path $VersionFile)) {
    Write-Error "version.properties not found at $VersionFile. Verify your clone directory."
    exit 1
}
$versionProps = Get-Content $VersionFile -Raw | ConvertFrom-StringData
$FileVersion = $versionProps["versionName"]
if ([string]::IsNullOrEmpty($FileVersion)) {
    Write-Error "Could not read versionName from version.properties"
    exit 1
}
$RequestedVersion = $Version    # whatever -Version was given, before $Version becomes the resolved one
$Version = $FileVersion

# Play rejects an upload whose versionCode isn't strictly higher than the last one, so any change to
# versionName drags the code up with it. It is monotonic and independent of versionName.
$VersionCode = 0
$rawVersionCode = if ($versionProps.ContainsKey("versionCode")) { $versionProps["versionCode"] } else { "" }
if (-not [int]::TryParse($rawVersionCode, [ref]$VersionCode)) {
    Write-Error "versionCode in $VersionFile is '$rawVersionCode', which is not an integer. Fix it by hand before building."
    exit 1
}

$BuildPropsPath = Join-Paths $RepoRoot "Directory.Build.props"
$versionPattern = '^\d+\.\d+\.\d+(\.\d+)?$'

Write-Host ""
Write-Host "Current version: " -NoNewline -ForegroundColor DarkCyan
Write-Host "$Version" -NoNewline -ForegroundColor Green
Write-Host "  (versionCode $VersionCode)" -ForegroundColor DarkGray

if (-not [string]::IsNullOrWhiteSpace($RequestedVersion)) {
    # -Version was passed: the answer to "what version?" is already given, so don't ask anything.
    $RequestedVersion = $RequestedVersion.Trim().TrimStart("v", "V")
    if ($RequestedVersion -notmatch $versionPattern) {
        Write-Error "-Version '$RequestedVersion' is not a valid version. Use MAJOR.MINOR.PATCH (e.g. 2.6.0)."
        exit 1
    }

    if ($RequestedVersion -eq $Version) {
        Write-Host "  -Version matches what's on disk; nothing to write." -ForegroundColor DarkGray
    } else {
        Write-Host "  -Version: $Version (code $VersionCode)  ->  $RequestedVersion (code $($VersionCode + 1))" -ForegroundColor Cyan
        $VersionCode = $VersionCode + 1
        Set-RemexVersion -VersionFilePath $VersionFile -BuildPropsPath $BuildPropsPath -NewVersion $RequestedVersion -NewVersionCode $VersionCode
        $Version = $RequestedVersion
    }
}
# Version change checkpoint. This runs on every build that didn't pass -Version, and always defaults
# to "no": a bump is irreversible in the eyes of the Play Store (a versionCode can never be reused),
# so it happens on a deliberate keypress, never by drifting through an unattended build.
elseif (Read-CountdownChoice -Prompt "Change the version number before building?" -Seconds 5 -DefaultOnTimeout $false) {
    $newVersion = ""
    while ($true) {
        $entered = "$(Read-Host "  New version number (blank to keep $Version)")"
        if ([string]::IsNullOrWhiteSpace($entered)) { break }
        $entered = $entered.Trim().TrimStart("v", "V")
        if ($entered -notmatch $versionPattern) {
            Write-Host "  '$entered' isn't a valid version. Use MAJOR.MINOR.PATCH (e.g. 2.6.0)." -ForegroundColor Yellow
            continue
        }
        if ($entered -eq $Version) {
            Write-Host "  That's the current version; keeping it." -ForegroundColor DarkGray
            break
        }
        $newVersion = $entered
        break
    }

    if (-not [string]::IsNullOrEmpty($newVersion)) {
        Write-Host "  $Version (code $VersionCode)  ->  $newVersion (code $($VersionCode + 1))" -ForegroundColor Cyan
        if (-not (Read-CountdownChoice -Prompt "Write this to version.properties and Directory.Build.props?" -Seconds 5 -DefaultOnTimeout $false)) {
            Write-Host "  Version left at $Version." -ForegroundColor DarkGray
        } else {
            $VersionCode = $VersionCode + 1
            Set-RemexVersion -VersionFilePath $VersionFile -BuildPropsPath $BuildPropsPath -NewVersion $newVersion -NewVersionCode $VersionCode
            $Version = $newVersion
        }
    }
} else {
    Write-Host "  Keeping version $Version." -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Resolved Version:       " -NoNewline -ForegroundColor DarkCyan
Write-Host "$Version" -ForegroundColor Green
Write-Host "----------------------------------------------------------" -ForegroundColor Gray

function Get-BashSafeScriptPath {
    param([string]$ScriptPath)

    $content = Get-Content $ScriptPath -Raw
    if ($content.Contains("`r")) {
        $tempPath = Join-Paths ([System.IO.Path]::GetTempPath()) ("remex-" + [System.IO.Path]::GetFileNameWithoutExtension($ScriptPath) + "-lf.sh")
        $normalized = $content -replace "`r`n", "`n" -replace "`r", "`n"
        [System.IO.File]::WriteAllText($tempPath, $normalized, [System.Text.UTF8Encoding]::new($false))
        return $tempPath
    }

    return $ScriptPath
}

function Convert-WindowsPathToWslPath {
    param([string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ($fullPath -match '^(?<Drive>[A-Za-z]):\\(?<Rest>.*)$') {
        $drive = $Matches["Drive"].ToLowerInvariant()
        $rest = $Matches["Rest"] -replace '\\', '/'
        return "/mnt/$drive/$rest"
    }

    $wslPath = (& wsl wslpath -a ($fullPath -replace '\\', '/')).Trim()
    if ([string]::IsNullOrWhiteSpace($wslPath)) {
        throw "Failed to convert Windows path to WSL path: $fullPath"
    }

    return $wslPath
}

# 3. Clean Phase
if ($NoClean) {
    Write-Host "=== Skipping Clean Phase (-NoClean) ===" -ForegroundColor DarkGray
    Write-Host "Existing artifacts/, bin/, obj/, and Gradle cache will be reused." -ForegroundColor DarkGray
    # Always refresh the staging folder so output is current
    if (Test-Path $BuildOutputDir) {
        Remove-Item -Path $BuildOutputDir -Recurse -Force -ErrorAction Stop
    }
    New-Item -ItemType Directory -Force -Path $BuildOutputDir | Out-Null
    Write-Host "----------------------------------------------------------" -ForegroundColor Gray
} else {
    Write-Host "=== Starting Hard Clean Phase ===" -ForegroundColor Yellow

    # Clean Output Dir
    if (Test-Path $BuildOutputDir) {
        Write-Host "Removing existing build_output folder..." -ForegroundColor DarkGray
        Remove-Item -Path $BuildOutputDir -Recurse -Force -ErrorAction Stop
    }
    New-Item -ItemType Directory -Force -Path $BuildOutputDir | Out-Null

    # Clean .NET build output to avoid stale caches. With UseArtifactsOutput (Directory.Build.props)
    # all output is consolidated under artifacts/, so that's the primary target; the recursive bin/obj
    # sweep stays as a safety net for any stray legacy folders (e.g. checkouts from before the switch).
    $artifactsDir = Join-Paths $RepoRoot "artifacts"
    if (Test-Path $artifactsDir) {
        Write-Host "Removing consolidated artifacts/ folder..." -ForegroundColor DarkGray
        Remove-Item -Path $artifactsDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host "Recursively purging any stray .NET bin/obj folders..." -ForegroundColor DarkGray
    $dotNetDirs = Get-ChildItem -Path $RepoRoot -Directory -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq "bin" -or $_.Name -eq "obj" }
    foreach ($dir in $dotNetDirs) {
        if ($null -ne $dir -and (Test-Path $dir.FullName)) {
            try {
                Write-Host "  Removing directory: $($dir.FullName)" -ForegroundColor DarkGray
                Remove-Item -Path $dir.FullName -Recurse -Force -ErrorAction Stop
            } catch {
                Write-Warning "Could not delete: $($dir.FullName). File may be locked by a running process."
            }
        }
    }

    # Clean Android gradle
    if ($Target -eq "android" -or $Target -eq "all") {
        $gradlew = if ($IsWin) { "gradlew.bat" } else { "gradlew" }
        $gradlePath = Join-Paths $RepoRoot "remex.android"
        $gradleCmd = Join-Paths $gradlePath $gradlew
        if (Test-Path $gradleCmd) {
            Write-Host "Running Gradle clean..." -ForegroundColor DarkGray
            Push-Location $gradlePath
            try {
                if ($IsWin) {
                    & $gradleCmd clean
                } else {
                    & bash $gradleCmd clean
                }
                if ($LASTEXITCODE -ne 0) {
                    Write-Error "Gradle clean failed with exit code $LASTEXITCODE"
                    exit 1
                }
            } finally {
                Pop-Location
            }
        }
    }
    Write-Host "Clean phase completed successfully." -ForegroundColor Green
    Write-Host "----------------------------------------------------------" -ForegroundColor Gray
}

# 4. Synchronize version with Directory.Build.props
# A no-op when the version was just changed above; the safety net for a working copy where the two
# files drifted apart on their own (a hand edit, a bad merge).
Sync-DirectoryBuildPropsVersion -BuildPropsPath $BuildPropsPath -DesiredVersion $Version

# 5. Restore .NET Solutions
if ($Target -eq "windows" -or $Target -eq "linux" -or $Target -eq "all") {
    Write-Host "=== Restoring .NET Packages ===" -ForegroundColor Yellow
    $solutionFile = Join-Paths $RepoRoot "RemEx.sln"
    dotnet restore $solutionFile
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet restore failed (exit $LASTEXITCODE)"
        exit 1
    }
    Write-Host "Restore complete." -ForegroundColor Green
    Write-Host "----------------------------------------------------------" -ForegroundColor Gray
}

# Helper function to find Inno Setup compiler
function Find-IsccCompiler {
    $command = Get-Command iscc, iscc.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command -and $command.Source) {
        return $command.Source
    }
    if ($IsWin) {
        $candidates = @(
            "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
            "C:\Program Files\Inno Setup 6\ISCC.exe"
        )
        foreach ($candidate in $candidates) {
            if (Test-Path $candidate) {
                return $candidate
            }
        }
    }
    return $null
}

# ---------------------------------------------------------------------------
# Post-build deployment helpers (-AdbInstall / -InstallLocal)
# ---------------------------------------------------------------------------

# Locate adb: PATH first, then the SDK's platform-tools. Plenty of working Android setups never
# put platform-tools on PATH, and "adb not found" is a useless failure when the SDK dir is already
# known from local.properties / ANDROID_HOME.
function Find-AdbExecutable {
    $command = Get-Command adb -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command -and $command.Source) {
        return $command.Source
    }

    $candidateRoots = @()
    $localPropsPath = Join-Paths $RepoRoot "remex.android" "local.properties"
    $fromProps = Get-LocalPropertiesSdkDir -LocalPropertiesPath $localPropsPath
    if (-not [string]::IsNullOrWhiteSpace($fromProps)) { $candidateRoots += $fromProps.Trim() }
    if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_HOME)) { $candidateRoots += $env:ANDROID_HOME }
    if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_SDK_ROOT)) { $candidateRoots += $env:ANDROID_SDK_ROOT }

    $adbName = if ($IsWin) { "adb.exe" } else { "adb" }
    foreach ($root in $candidateRoots) {
        $candidate = Join-Paths $root "platform-tools" $adbName
        if (Test-Path $candidate) {
            return $candidate
        }
    }
    return $null
}

# Serials of devices adb reports as actually usable. "unauthorized" (RSA prompt not accepted) and
# "offline" (a wireless link that dropped) are deliberately excluded — installing to those fails.
function Get-AdbReadyDevices {
    param([Parameter(Mandatory=$true)][string]$Adb)

    $output = @(& $Adb devices 2>&1)
    $serials = @()
    foreach ($line in $output) {
        $text = "$line".Trim()
        if ($text -match '^(?<Serial>\S+)\s+device$') {
            $serials += $Matches["Serial"]
        }
    }
    # Returned unwrapped on purpose — callers wrap in @() to normalize. Returning ", $serials"
    # would hand back a one-element array *containing* the array, so "no devices" would count as one.
    return $serials
}

# adb exits 0 on some install failures and prints "Failure [INSTALL_FAILED_...]" instead, so the
# output is inspected as well as the exit code.
function Invoke-AdbInstallApk {
    param(
        [Parameter(Mandatory=$true)][string]$Adb,
        [Parameter(Mandatory=$true)][string]$Serial,
        [Parameter(Mandatory=$true)][string]$ApkPath
    )

    Write-Host "Installing $([System.IO.Path]::GetFileName($ApkPath)) -> $Serial ..." -ForegroundColor DarkCyan
    $output = @(& $Adb -s $Serial install -r $ApkPath 2>&1)
    $exitCode = $LASTEXITCODE
    foreach ($line in $output) {
        $text = "$line".Trim()
        if (-not [string]::IsNullOrWhiteSpace($text)) {
            Write-Host "  $text" -ForegroundColor DarkGray
        }
    }
    return ($exitCode -eq 0) -and (($output -join "`n") -notmatch 'Failure')
}

# 6. Targets Compilation
# --- WINDOWS TARGET ---
if ($Target -eq "windows" -or $Target -eq "all") {
    Write-Host "=== Compiling Windows Platform ===" -ForegroundColor Yellow
    
    $clientProj = Join-Paths $RepoRoot "remex.agent" "remex.agent.csproj"
    if (-not $skipPublish) {
        Write-Host "Publishing Remex.Agent ($Config, win-x64)..." -ForegroundColor DarkCyan
        dotnet publish $clientProj -c $Config -r win-x64 --self-contained
        if ($LASTEXITCODE -ne 0) {
            Write-Error "dotnet publish for Windows failed with exit code $LASTEXITCODE"
            exit 1
        }
    } else {
        Write-Host "Skipping dotnet publish as publish directory already exists." -ForegroundColor Green
    }

    # Locate and run Inno Setup Compiler
    if (-not $skipInstaller) {
        $iscc = Find-IsccCompiler
        if ($null -ne $iscc) {
            Write-Host "Building Windows Installer (Inno Setup)..." -ForegroundColor DarkCyan
            $issFile = Join-Paths $RepoRoot "installer" "RemEx.iss"
            $sourceDirArg = "..\artifacts\publish\remex.agent\${Config}_win-x64"
            
            & $iscc "/DAppVersion=$Version" "/DSourceDir=$sourceDirArg" $issFile
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Inno Setup compilation failed with exit code $LASTEXITCODE"
                exit 1
            }

            # Locate and copy built executable
            $installerExe = Join-Paths $RepoRoot "installer" "Output" "RemEx-v$Version-Setup.exe"
            if (Test-Path $installerExe) {
                $winDest = Join-Paths $BuildOutputDir "windows"
                New-Item -ItemType Directory -Force -Path $winDest | Out-Null
                Copy-Item -Path $installerExe -Destination $winDest -Force
                Write-Host "Windows target packaged successfully." -ForegroundColor Green
            } else {
                Write-Error "Installer built but Output file was not found at $installerExe"
                exit 1
            }
        } else {
            Write-Warning "ISCC.exe (Inno Setup 6) was not found in standard paths. Skipping installer packaging."
            Write-Warning "Raw published files are available under artifacts\publish\remex.agent\${Config}_win-x64\"
        }
    }
    Write-Host "----------------------------------------------------------" -ForegroundColor Gray
}

# --- ANDROID TARGET ---
if ($Target -eq "android" -or $Target -eq "all") {
    Write-Host "=== Compiling Android Platform ===" -ForegroundColor Yellow
    
    $gradlew = if ($IsWin) { "gradlew.bat" } else { "gradlew" }
    $gradlePath = Join-Paths $RepoRoot "remex.android"
    $gradleCmd = Join-Paths $gradlePath $gradlew
    $tasks = if ($Config -eq "release") { 
        @("assembleRelease", "bundleRelease", "verifyRemexCoreInReleaseApk")
    } else { 
        @("assembleDebug", "verifyRemexCoreInDebugApk")
    }

    if (-not (Test-Path $gradleCmd)) {
        Write-Error "Gradle wrapper not found at $gradleCmd. Cannot compile Android."
        exit 1
    }

    # Auto-sync local.properties sdk.dir for the current OS. This repo lives on a drive shared
    # between Windows and Linux, so a path written by one OS is wrong on the other. Rather than
    # requiring a manual edit every time you switch platforms, resolve the SDK dir from
    # ANDROID_HOME/ANDROID_SDK_ROOT (which every dev sets per-OS per docs/ANDROID_SETUP.md) and
    # keep local.properties in sync with whichever OS is currently building.
    $envSdkDir = $env:ANDROID_HOME
    if ([string]::IsNullOrEmpty($envSdkDir)) { $envSdkDir = $env:ANDROID_SDK_ROOT }
    if (-not [string]::IsNullOrEmpty($envSdkDir) -and (Test-Path $envSdkDir)) {
        # Forward slashes only: Gradle/AGP accept them fine on Windows too, and this script's own
        # ConvertFrom-StringData reads of local.properties (below) choke on backslash escapes
        # (e.g. "\U" in "E:\Utilities\..." is not a recognized escape sequence).
        $normalizedSdkDir = $envSdkDir -replace '\\', '/'
        $localPropsPath = Join-Paths $gradlePath "local.properties"
        $desiredLine = "sdk.dir=$normalizedSdkDir"
        $existingLines = if (Test-Path $localPropsPath) { @(Get-Content $localPropsPath) } else { @() }
        $currentSdkLine = $existingLines | Where-Object { $_ -match '^\s*sdk\.dir\s*=' } | Select-Object -First 1
        if ($currentSdkLine -ne $desiredLine) {
            $osLabel = if ($IsWin) { "Windows" } else { "Linux" }
            Write-Host "Syncing local.properties sdk.dir for $osLabel build: $normalizedSdkDir" -ForegroundColor DarkCyan
            $keptLines = $existingLines | Where-Object { $_ -notmatch '^\s*sdk\.dir\s*=' }
            Set-Content -Path $localPropsPath -Value ($keptLines + $desiredLine)
        }
    }

    # Proactive Android SDK and NDK dependency resolution
    Write-Host "Verifying Android SDK & NDK build dependencies..." -ForegroundColor DarkGray
    $localPropsPath = Join-Paths $gradlePath "local.properties"
    $sdkDir = Get-LocalPropertiesSdkDir -LocalPropertiesPath $localPropsPath
    if ($sdkDir) {
        if ($IsWin) {
            $sdkDir = $sdkDir -replace '\\+', '\' -replace '/', '\'
        } else {
            $sdkDir = $sdkDir -replace '\\+', '/' -replace '/+', '/'
        }
    }
    if ([string]::IsNullOrEmpty($sdkDir)) {
        $sdkDir = $env:ANDROID_HOME
        if ([string]::IsNullOrEmpty($sdkDir)) {
            $sdkDir = $env:ANDROID_SDK_ROOT
        }
    }

    if (-not [string]::IsNullOrEmpty($sdkDir)) {
        # Check 1: API Level 37 Platform (Required by .NET 10 Sdk targets)
        $apiJar = Join-Paths $sdkDir "platforms" "android-37" "android.jar"
        if (-not (Test-Path $apiJar)) {
            Write-Host "Android API Level 37 platform is missing. Attempting auto-installation..." -ForegroundColor Yellow
            $coreProj = Join-Paths $RepoRoot "remex.core" "remex.core.csproj"
            dotnet build $coreProj -t:InstallAndroidDependencies -f net10.0-android "-p:AndroidSdkDirectory=$sdkDir" "-p:AcceptAndroidSDKLicenses=true"
            if ($LASTEXITCODE -eq 0) {
                Write-Host "Android API Level 37 dependency resolved successfully." -ForegroundColor Green
            } else {
                Write-Warning "Auto-installation of Android API dependencies returned exit code $LASTEXITCODE. Build may fail."
            }
        } else {
            Write-Host "Android API Level 37 platform dependency verified." -ForegroundColor Green
        }

        # Check 2: NDK version 30.0.14904198 (Required for .NET NativeAOT JNI core compiler)
        $requiredNdkVersion = "30.0.14904198"
        $ndkDir = Join-Paths $sdkDir "ndk" $requiredNdkVersion
        if (-not (Test-Path $ndkDir)) {
            Write-Host "Android NDK version $requiredNdkVersion is missing at: $ndkDir" -ForegroundColor Yellow
            $sdkManagerName = if ($IsWin) { "sdkmanager.bat" } else { "sdkmanager" }
            $sdkManager = Join-Paths $sdkDir "cmdline-tools" "latest" "bin" $sdkManagerName
            if (-not (Test-Path $sdkManager)) {
                $sdkManager = Join-Paths $sdkDir "cmdline-tools" "bin" $sdkManagerName
            }
            if (-not (Test-Path $sdkManager)) {
                $sdkManager = Get-ChildItem -Path $sdkDir -Filter $sdkManagerName -Recurse -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName -First 1
            }

            if ($sdkManager -and (Test-Path $sdkManager)) {
                Write-Host "Found sdkmanager at: $sdkManager" -ForegroundColor DarkCyan
                Write-Host "Installing Android NDK version $requiredNdkVersion... (This might take 1-3 minutes)" -ForegroundColor Cyan
                $confirmInput = @("y") * 20
                $confirmInput | & $sdkManager --install "ndk;$requiredNdkVersion"
                if ($LASTEXITCODE -eq 0 -and (Test-Path $ndkDir)) {
                    Write-Host "Android NDK version $requiredNdkVersion resolved successfully." -ForegroundColor Green
                } else {
                    Write-Warning "Android NDK installation finished with exit code $LASTEXITCODE. Native compiler may fail if path is unresolved."
                }
            } else {
                Write-Warning "Could not find $sdkManagerName under $sdkDir. Please install NDK version $requiredNdkVersion manually."
            }
        } else {
            Write-Host "Android NDK version $requiredNdkVersion dependency verified." -ForegroundColor Green
        }
    } else {
        Write-Warning "Could not resolve Android SDK directory. Skipping build dependencies verification."
    }

    Write-Host "Running Gradle tasks '$($tasks -join ' ')'..." -ForegroundColor DarkCyan
    Push-Location $gradlePath
    try {
        if ($IsWin) {
            & $gradleCmd @tasks --stacktrace
        } else {
            & bash $gradleCmd @tasks --stacktrace
        }
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Android build failed. Checking if there are missing Android SDK dependencies..." -ForegroundColor Yellow
            
            # Read SDK Directory from local.properties
            $localPropsPath = Join-Paths $gradlePath "local.properties"
            $sdkDir = Get-LocalPropertiesSdkDir -LocalPropertiesPath $localPropsPath
            if ($sdkDir) {
                if ($IsWin) {
                    $sdkDir = $sdkDir -replace '\\+', '\' -replace '/', '\'
                } else {
                    $sdkDir = $sdkDir -replace '\\+', '/' -replace '/+', '/'
                }
            }

            if ([string]::IsNullOrEmpty($sdkDir)) {
                # Fallback to standard environment variables
                $sdkDir = $env:ANDROID_HOME
                if ([string]::IsNullOrEmpty($sdkDir)) {
                    $sdkDir = $env:ANDROID_SDK_ROOT
                }
            }

            if (-not [string]::IsNullOrEmpty($sdkDir)) {
                Write-Host "Attempting to auto-install missing Android SDK dependencies in: $sdkDir" -ForegroundColor Cyan
                $coreProj = Join-Paths $RepoRoot "remex.core" "remex.core.csproj"
                
                # Run the dependency installer target
                dotnet build $coreProj -t:InstallAndroidDependencies -f net10.0-android "-p:AndroidSdkDirectory=$sdkDir" "-p:AcceptAndroidSDKLicenses=true"
                
                if ($LASTEXITCODE -eq 0) {
                    Write-Host "Successfully installed Android SDK dependencies! Stopping Gradle daemon to release file locks..." -ForegroundColor Green
                    if ($IsWin) {
                        & $gradleCmd --stop
                    } else {
                        & bash $gradleCmd --stop
                    }
                    
                    Write-Host "Retrying Gradle build..." -ForegroundColor Green
                    if ($IsWin) {
                        & $gradleCmd @tasks --stacktrace
                    } else {
                        & bash $gradleCmd @tasks --stacktrace
                    }
                    if ($LASTEXITCODE -ne 0) {
                        Write-Error "Gradle Android build failed again after installing dependencies."
                        exit 1
                    }
                } else {
                    Write-Error "Failed to install missing Android dependencies. Please run 'dotnet build -t:InstallAndroidDependencies -f net10.0-android' manually."
                    exit 1
                }
            } else {
                Write-Error "Gradle Android build failed and could not locate the Android SDK directory to auto-install dependencies."
                exit 1
            }
        }
    } finally {
        Pop-Location
    }

    # Staging built Android APK/AAB files
    $androidDest = Join-Paths $BuildOutputDir "android"
    New-Item -ItemType Directory -Force -Path $androidDest | Out-Null
    
    $apkFound = $false
    $apkSearchPath = Join-Paths $gradlePath "app" "build" "outputs" "apk"
    if (Test-Path $apkSearchPath) {
        $apks = Get-ChildItem -Path $apkSearchPath -Filter "*.apk" -Recurse
        foreach ($apk in $apks) {
            Copy-Item -Path $apk.FullName -Destination $androidDest -Force
            Write-Host "Android APK Staged: $($apk.Name)" -ForegroundColor Green
            $apkFound = $true
        }
    }

    $aabFound = $false
    $aabSearchPath = Join-Paths $gradlePath "app" "build" "outputs" "bundle"
    if (Test-Path $aabSearchPath) {
        $aabs = Get-ChildItem -Path $aabSearchPath -Filter "*.aab" -Recurse
        foreach ($aab in $aabs) {
            Copy-Item -Path $aab.FullName -Destination $androidDest -Force
            Write-Host "Android App Bundle Staged: $($aab.Name)" -ForegroundColor Green
            $aabFound = $true
        }
    }

    if (-not $apkFound -and -not $aabFound) {
        Write-Error "Android build succeeded, but no APK or AAB was found in app build outputs."
        exit 1
    }
    Write-Host "Android target packaged successfully." -ForegroundColor Green
    Write-Host "----------------------------------------------------------" -ForegroundColor Gray
}

# --- LINUX TARGET ---
if ($Target -eq "linux" -or $Target -eq "all") {
    Write-Host "=== Compiling Linux Platform ===" -ForegroundColor Yellow
    
    if ($Config -eq "debug") {
        Write-Warning "Linux build-linux.sh target only natively supports 'Release' configuration. Proceeding with Release build."
    }

    $buildScript = Join-Paths $RepoRoot "installer" "build-linux.sh"
    $bashScript = Get-BashSafeScriptPath -ScriptPath $buildScript
    if ($IsLin) {
        Write-Host "Executing build-linux.sh natively..." -ForegroundColor DarkCyan
        & bash $bashScript
        $linuxBuildExitCode = $LASTEXITCODE
    } else {
        # Check if WSL is available on Windows
        $wslCmd = Get-Command wsl -ErrorAction SilentlyContinue
        if ($wslCmd) {
            Write-Host "WSL detected. Executing Linux build-linux.sh via WSL..." -ForegroundColor DarkCyan
            $wslBuildScript = Convert-WindowsPathToWslPath -Path $bashScript
            & wsl bash $wslBuildScript
            $linuxBuildExitCode = $LASTEXITCODE
        } else {
            Write-Warning "Linux build requires a Linux environment or WSL. Skipping Linux packaging."
            $linuxBuildExitCode = 0 # Skip gracefully without failing the entire script
        }
    }

    if ($linuxBuildExitCode -ne 0) {
        Write-Error "Linux packaging failed with exit code $linuxBuildExitCode"
        exit 1
    }

    # Staging Linux tarball packages
    $linuxStage = Join-Paths $RepoRoot "installer" "Output"
    if (Test-Path $linuxStage) {
        $tarballs = @(Get-ChildItem -Path $linuxStage -Filter "*.tar.gz")
        if ($tarballs.Count -gt 0) {
            $linuxDest = Join-Paths $BuildOutputDir "linux"
            New-Item -ItemType Directory -Force -Path $linuxDest | Out-Null
            foreach ($tar in $tarballs) {
                Copy-Item -Path $tar.FullName -Destination $linuxDest -Force
                Write-Host "Linux Package Staged: $($tar.Name)" -ForegroundColor Green
            }
            Write-Host "Linux target packaged successfully." -ForegroundColor Green
        }
    }
    Write-Host "----------------------------------------------------------" -ForegroundColor Gray
}

# 7. Post-Build Deployment (-AdbInstall / -InstallLocal)
# Both steps run only after every selected target compiled and staged successfully, so a
# deployment never ships a half-built tree. Neither runs unless explicitly asked for.
$deployFailed = $false

# --- ANDROID: adb install -r ---
if ($AdbInstall) {
    Write-Host "=== Installing APK to Device (adb) ===" -ForegroundColor Yellow

    $androidStage = Join-Paths $BuildOutputDir "android"
    $variantSuffix = if ($Config -eq "release") { "-release.apk" } else { "-debug.apk" }
    $stagedApks = @(Get-ChildItem -Path $androidStage -Filter "*.apk" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike "*unsigned*" })
    # Match the built variant exactly — no "any APK will do" fallback. Debug and release are signed
    # with different keys, so installing the wrong one over the other fails with a signature mismatch,
    # and installing a stale variant that happens to be lying in the folder is worse than not installing.
    $apk = $stagedApks | Where-Object { $_.Name -like "*$variantSuffix" } | Select-Object -First 1

    $adb = Find-AdbExecutable

    if ($null -eq $apk) {
        Write-Warning "No signed *$variantSuffix found in $androidStage. Skipping adb install."
    } elseif ($null -eq $adb) {
        Write-Warning "adb was not found on PATH or under <sdk>/platform-tools. Skipping install."
        Write-Warning "Install it with 'sdkmanager platform-tools', or install by hand: adb install -r `"$($apk.FullName)`""
    } else {
        Write-Host "Using adb: $adb" -ForegroundColor DarkGray

        # Wireless debugging frequently isn't discovered on the first look — the daemon may still be
        # starting, or mDNS hasn't resolved the paired device yet. So: check, pause, check again,
        # and only then ask the human.
        $devices = @(Get-AdbReadyDevices -Adb $adb)
        if ($devices.Count -eq 0) {
            Write-Host "No device on the first check. Wireless debugging often needs a moment — retrying..." -ForegroundColor DarkGray
            Start-Sleep -Seconds 3
            $devices = @(Get-AdbReadyDevices -Adb $adb)
        }

        while ($devices.Count -eq 0) {
            Write-Host "Still no authorized device." -ForegroundColor Yellow
            Write-Host "  Check that wireless debugging is on and the phone is paired (Developer options > Wireless debugging)." -ForegroundColor DarkGray
            if (-not (Read-CountdownChoice -Prompt "Retry adb?" -Seconds 5 -DefaultOnTimeout $false)) {
                Write-Host "Skipping install. The APK is staged at $($apk.FullName)" -ForegroundColor DarkGray
                break
            }
            $devices = @(Get-AdbReadyDevices -Adb $adb)
            if ($devices.Count -eq 0) {
                Start-Sleep -Seconds 2
                $devices = @(Get-AdbReadyDevices -Adb $adb)
            }
        }

        if ($devices.Count -gt 0) {
            Write-Host "Ready device(s): $($devices -join ', ')" -ForegroundColor Green
            # Always target a specific serial. Bare 'adb install' aborts with "more than one device"
            # the moment an emulator is also running, which is a confusing way to fail.
            foreach ($serial in $devices) {
                if (Invoke-AdbInstallApk -Adb $adb -Serial $serial -ApkPath $apk.FullName) {
                    Write-Host "Installed on $serial." -ForegroundColor Green
                } else {
                    Write-Warning "adb install failed on $serial. See the adb output above."
                    $deployFailed = $true
                }
            }
        }
    }
    Write-Host "----------------------------------------------------------" -ForegroundColor Gray
}

# --- WINDOWS: update the local install ---
# Handed off to scripts/update-local-install.ps1 rather than reimplemented here. That script owns
# stopping the RemEx logon task, copying the whole publish folder (code lives in the managed DLLs,
# not in the native Remex.Agent.exe shim) and restarting it — and it requires elevation, which this
# build script must not.
if ($InstallLocal) {
    Write-Host "=== Updating Local RemEx Install ===" -ForegroundColor Yellow

    $updateScript = Join-Paths $RepoRoot "scripts" "update-local-install.ps1"
    $isElevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)

    if (-not (Test-Path $updateScript)) {
        Write-Warning "update-local-install.ps1 not found at $updateScript. Skipping local install update."
    } elseif (-not $isElevated) {
        Write-Warning "Updating the install writes to ProgramFiles and needs an elevated shell. Skipping."
        Write-Warning "Re-run from an admin terminal, or run it yourself: pwsh `"$updateScript`" -SkipPublish"
        $deployFailed = $true
    } else {
        # -SkipPublish reuses the publish this script produced a moment ago instead of building twice.
        # Reset LASTEXITCODE first: the script ends without an explicit 'exit 0', so a stale code from
        # an earlier native command would otherwise be read as its result. The try/catch is because it
        # runs under $ErrorActionPreference = 'Stop', where its own Write-Error paths throw.
        $global:LASTEXITCODE = 0
        try {
            & $updateScript -SkipPublish
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "update-local-install.ps1 failed (exit $LASTEXITCODE). Your install may be partially updated."
                $deployFailed = $true
            } else {
                Write-Host "Local install updated." -ForegroundColor Green
            }
        } catch {
            Write-Warning "update-local-install.ps1 failed: $($_.Exception.Message)"
            Write-Warning "Your install may be partially updated. Re-run: pwsh `"$updateScript`" -SkipPublish"
            $deployFailed = $true
        }
    }
    Write-Host "----------------------------------------------------------" -ForegroundColor Gray
}

# 8. Premium Visual Summary
Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "            ✨ BUILD COMPLETED SUCCESSFULLY ✨            " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "Configuration: $Config" -ForegroundColor DarkCyan
Write-Host "Target(s):     $Target" -ForegroundColor DarkCyan
Write-Host "Version:       $Version" -ForegroundColor DarkCyan
Write-Host "Clean:         $(if ($NoClean) { 'Skipped (-NoClean)' } else { 'Full' })" -ForegroundColor DarkCyan
Write-Host "Output Folder: $BuildOutputDir" -ForegroundColor DarkCyan
if ($AdbInstall -or $InstallLocal) {
    $deployLabel = if ($deployFailed) { "One or more steps FAILED (see warnings above)" } else { "OK" }
    Write-Host "Deployment:    $deployLabel" -ForegroundColor $(if ($deployFailed) { 'Red' } else { 'DarkCyan' })
}
Write-Host "----------------------------------------------------------" -ForegroundColor Gray

# Retrieve staged files
$stagedFiles = @(Get-ChildItem -Path $BuildOutputDir -File -Recurse -ErrorAction SilentlyContinue)
if ($stagedFiles.Count -eq 0) {
    Write-Warning "No build artifacts were found in build_output. Check script parameters or warnings."
} else {
    foreach ($file in $stagedFiles) {
        $relativePath = $file.FullName.Replace($BuildOutputDir, "").TrimStart("\").TrimStart("/")
        $sizeMB = [Math]::Round(($file.Length / 1MB), 2)
        Write-Host "  • [Size: $sizeMB MB]  $relativePath" -ForegroundColor Cyan
    }
}
Write-Host "==========================================================" -ForegroundColor Green

# The build itself succeeded — artifacts are staged and valid. But if a deployment step you
# explicitly asked for failed, exiting 0 would report a lie to whatever ran this.
if ($deployFailed) {
    Write-Host "The build succeeded, but a requested deployment step did not. Exiting non-zero." -ForegroundColor Red
    exit 1
}
exit 0

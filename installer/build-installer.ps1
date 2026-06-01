#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds RemEx installer artifacts with automatic platform detection.

.DESCRIPTION
    Target=Auto chooses the correct packaging flow by environment:
      - Windows / WSL: Windows installer (Inno Setup)
      - Linux: Linux client/host packages via build-linux.sh

.PARAMETER Target
    Auto (default), Windows, or Linux.

.PARAMETER SkipPublish
    Windows target only: skip dotnet publish and use existing win-x64 publish output.

.PARAMETER SkipVersionSync
    Windows target only: skip syncing Directory.Build.props with version.properties.

.PARAMETER SkipClient
    Linux target only: pass --skip-client to build-linux.sh.

.PARAMETER SkipHost
    Linux target only: pass --skip-host to build-linux.sh.

.PARAMETER ForwardedArgs
    Additional args forwarded to build-linux.sh (Linux target only).

.EXAMPLE
    pwsh ./installer/build-installer.ps1

.EXAMPLE
    pwsh ./installer/build-installer.ps1 -Target Windows -SkipPublish

.EXAMPLE
    pwsh ./installer/build-installer.ps1 -Target Linux -- --skip-client
#>
param(
    [ValidateSet("Auto", "Windows", "Linux")]
    [string]$Target = "Auto",
    [switch]$SkipPublish,
    [switch]$SkipVersionSync,
    [switch]$SkipClient,
    [switch]$SkipHost,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ForwardedArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-IsWsl {
    if (-not $IsLinux) {
        return $false
    }

    if (Test-Path "/proc/sys/fs/binfmt_misc/WSLInterop") {
        return $true
    }

    try {
        $versionText = Get-Content "/proc/version" -Raw
        return $versionText -match "(?i)microsoft|wsl"
    } catch {
        return $false
    }
}

function Resolve-TargetPlatform {
    param([string]$RequestedTarget)

    if ($RequestedTarget -ne "Auto") {
        return $RequestedTarget
    }

    if ($IsWindows -or (Test-IsWsl)) {
        return "Windows"
    }

    if ($IsLinux) {
        return "Linux"
    }

    throw "Unsupported platform for Target=Auto. Specify -Target Windows or -Target Linux explicitly."
}

function Invoke-LinuxBuild {
    param(
        [string]$BuildLinuxScript,
        [string[]]$LinuxArgs
    )

    if (-not (Test-Path $BuildLinuxScript)) {
        Write-Error "Linux build script not found: $BuildLinuxScript"
        exit 1
    }

    if ($SkipPublish -or $SkipVersionSync) {
        Write-Warning "-SkipPublish and -SkipVersionSync are Windows-only flags and will be ignored for Linux target."
    }

    $bashScript = Get-BashSafeScriptPath -ScriptPath $BuildLinuxScript
    Write-Host "Target: Linux (using installer/build-linux.sh)" -ForegroundColor Cyan
    if ($IsWindows -and (Get-Command wsl -ErrorAction SilentlyContinue)) {
        $wslBuildScript = Convert-WindowsPathToWslPath -Path $bashScript
        & wsl bash $wslBuildScript @LinuxArgs
    } else {
        & bash $bashScript @LinuxArgs
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Linux packaging failed (exit $LASTEXITCODE)"
        exit 1
    }
}

function Get-BashSafeScriptPath {
    param([string]$ScriptPath)

    $content = Get-Content $ScriptPath -Raw
    if ($content.Contains("`r")) {
        $tempPath = Join-Path ([System.IO.Path]::GetTempPath()) ("remex-" + [System.IO.Path]::GetFileNameWithoutExtension($ScriptPath) + "-lf.sh")
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

function Find-Iscc {
    param([bool]$IsWslEnvironment)

    $command = Get-Command iscc, iscc.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command -and $command.Source) {
        return [pscustomobject]@{
            Path          = $command.Source
            UseWine       = $false
            UseWslInterop = $false
        }
    }

    if ($IsWslEnvironment) {
        $wslCandidates = @(
            "/mnt/c/Program Files (x86)/Inno Setup 6/ISCC.exe",
            "/mnt/c/Program Files/Inno Setup 6/ISCC.exe"
        )
        foreach ($candidate in $wslCandidates) {
            if (Test-Path $candidate) {
                return [pscustomobject]@{
                    Path          = $candidate
                    UseWine       = $false
                    UseWslInterop = $true
                }
            }
        }
    }

    if ($IsWindows) {
        $windowsCandidates = @(
            "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
            "C:\Program Files\Inno Setup 6\ISCC.exe"
        )
        foreach ($candidate in $windowsCandidates) {
            if (Test-Path $candidate) {
                return [pscustomobject]@{
                    Path          = $candidate
                    UseWine       = $false
                    UseWslInterop = $false
                }
            }
        }
    }

    $homeDir = $env:HOME
    $userName = if ($env:USER) { $env:USER } else { $env:USERNAME }
    $wineCandidates = @(
        "$homeDir/.wine/drive_c/users/$userName/AppData/Local/Programs/Inno Setup 6/ISCC.exe",
        "$homeDir/.wine/drive_c/Program Files (x86)/Inno Setup 6/ISCC.exe",
        "$homeDir/.wine/drive_c/Program Files/Inno Setup 6/ISCC.exe"
    )
    foreach ($candidate in $wineCandidates) {
        if (Test-Path $candidate) {
            return [pscustomobject]@{
                Path          = $candidate
                UseWine       = $true
                UseWslInterop = $false
            }
        }
    }

    return $null
}

function Invoke-WindowsBuild {
    param(
        [string]$RepoRoot,
        [string]$Version,
        [string]$BuildProps,
        [string]$IssFile,
        [string]$ClientProj,
        [bool]$IsWslEnvironment
    )

    $extraArgs = @($ForwardedArgs | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

    if ($SkipClient -or $SkipHost) {
        Write-Warning "-SkipClient and -SkipHost apply only to Linux target and will be ignored for Windows target."
    }
    if ($extraArgs.Count -gt 0) {
        Write-Error "Unexpected extra arguments for Windows target: $($extraArgs -join ' ')"
        exit 1
    }

    if (-not $SkipVersionSync) {
        $content = Get-Content $BuildProps -Raw
        $patched = $content -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"
        if ($content -ne $patched) {
            Set-Content $BuildProps $patched -NoNewline
            Write-Host "Patched Directory.Build.props -> $Version" -ForegroundColor Green
        } else {
            Write-Host "Directory.Build.props already at $Version" -ForegroundColor DarkGray
        }
    }

    if (-not $SkipPublish) {
        Write-Host "Publishing Remex.Client.Desktop (self-contained, win-x64)..." -ForegroundColor Cyan
        dotnet publish $ClientProj -c Release -r win-x64 --self-contained
        if ($LASTEXITCODE -ne 0) {
            Write-Error "dotnet publish failed (exit $LASTEXITCODE)"
            exit 1
        }
        Write-Host "Publish complete." -ForegroundColor Green
    }

    $isccInfo = Find-Iscc -IsWslEnvironment:$IsWslEnvironment
    if ($null -eq $isccInfo) {
        Write-Error "ISCC.exe not found. Install Inno Setup 6 (Windows) or install/configure Inno Setup under Wine (Linux)."
        exit 1
    }

    Write-Host "Target: Windows installer (Inno Setup)" -ForegroundColor Cyan
    if ($isccInfo.UseWine) {
        if (-not (Get-Command wine -ErrorAction SilentlyContinue)) {
            Write-Error "Wine is required to execute ISCC.exe from Linux but was not found in PATH."
            exit 1
        }

        $wineIssFile = (& wine winepath -w $IssFile).Trim()
        if ([string]::IsNullOrWhiteSpace($wineIssFile)) {
            Write-Error "Failed to convert ISS path for Wine: $IssFile"
            exit 1
        }

        & wine $isccInfo.Path "/DAppVersion=$Version" $wineIssFile
    } elseif ($isccInfo.UseWslInterop) {
        $winIsccPath = (& wslpath -w $isccInfo.Path).Trim()
        $winIssFile = (& wslpath -w $IssFile).Trim()
        $cmdLine = ('"{0}" "/DAppVersion={1}" "{2}"' -f $winIsccPath, $Version, $winIssFile)
        & cmd.exe /c $cmdLine
    } else {
        & $isccInfo.Path "/DAppVersion=$Version" $IssFile
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "iscc failed (exit $LASTEXITCODE)"
        exit 1
    }

    $outputExe = Join-Path $PSScriptRoot "Output\RemEx-v$Version-Setup.exe"
    Write-Host ""
    Write-Host "=====================================================" -ForegroundColor Green
    Write-Host "  Windows installer built successfully!" -ForegroundColor Green
    Write-Host "  Output: $outputExe" -ForegroundColor Green
    Write-Host "=====================================================" -ForegroundColor Green
}

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$VersionFile = Join-Path $RepoRoot "RemEx.Android" "app" "version.properties"
$BuildProps = Join-Path $RepoRoot "Directory.Build.props"
$IssFile = Join-Path $PSScriptRoot "RemEx.iss"
$ClientProj = Join-Path $RepoRoot "Remex.Client.Desktop"
$BuildLinuxScript = Join-Path $PSScriptRoot "build-linux.sh"
$isWsl = Test-IsWsl
$resolvedTarget = Resolve-TargetPlatform -RequestedTarget $Target

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
$environmentName = "Unknown"
if ($IsWindows) {
    $environmentName = "Windows"
} elseif ($isWsl) {
    $environmentName = "WSL"
} elseif ($IsLinux) {
    $environmentName = "Linux"
}
Write-Host "Environment: $environmentName" -ForegroundColor DarkGray
Write-Host "Resolved target: $resolvedTarget" -ForegroundColor DarkGray

if ($resolvedTarget -eq "Linux") {
    $linuxArgs = @()
    if ($SkipClient) { $linuxArgs += "--skip-client" }
    if ($SkipHost) { $linuxArgs += "--skip-host" }
    $extraArgs = @($ForwardedArgs | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($extraArgs.Count -gt 0) { $linuxArgs += $extraArgs }
    Invoke-LinuxBuild -BuildLinuxScript $BuildLinuxScript -LinuxArgs $linuxArgs
    exit 0
}

Invoke-WindowsBuild `
    -RepoRoot $RepoRoot `
    -Version $version `
    -BuildProps $BuildProps `
    -IssFile $IssFile `
    -ClientProj $ClientProj `
    -IsWslEnvironment:$isWsl
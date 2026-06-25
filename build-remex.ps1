#!/usr/bin/env pwsh
<#
.SYNOPSIS
    RemEx Unified Build and Packaging Tool.

.DESCRIPTION
    A complete script to clean all project artifacts, restore .NET dependencies,
    compile, publish, and package RemEx Desktop (Windows/Linux) and Android apps.
    Consolidates APKs, AABs, and desktop installers into a single `build_output` folder.

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

.EXAMPLE
    ./build-remex.ps1 -c release -t all
.EXAMPLE
    ./build-remex.ps1 -t windows -NoClean
.EXAMPLE
    ./build-remex.ps1 (starts interactive wizard if no args are specified)
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

    [Parameter(Mandatory=$false)]
    [switch]$NonInteractive,

    [Parameter(Mandatory=$false)]
    [switch]$NoClean
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

# Force output to support emojis/colored text on Windows PowerShell
if ($IsWin) {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
}

Write-Host "==========================================================" -ForegroundColor Green
Write-Host "                 ⚡ RemEx Build System ⚡                 " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green

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
$Version = $versionProps["versionName"]
if ([string]::IsNullOrEmpty($Version)) {
    Write-Error "Could not read versionName from version.properties"
    exit 1
}
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
$BuildProps = Join-Paths $RepoRoot "Directory.Build.props"
if (Test-Path $BuildProps) {
    $content = Get-Content $BuildProps -Raw
    $patched = $content -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"
    if ($content -ne $patched) {
        Set-Content $BuildProps $patched -NoNewline
        Write-Host "Synchronized Directory.Build.props to version $Version" -ForegroundColor Green
    } else {
        Write-Host "Directory.Build.props is already synced at version $Version" -ForegroundColor DarkGray
    }
}

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

    # Proactive Android SDK and NDK dependency resolution
    Write-Host "Verifying Android SDK & NDK build dependencies..." -ForegroundColor DarkGray
    $sdkDir = $null
    $localPropsPath = Join-Paths $gradlePath "local.properties"
    if (Test-Path $localPropsPath) {
        $localProps = Get-Content $localPropsPath -Raw | ConvertFrom-StringData
        $sdkDir = $localProps["sdk.dir"]
        if ($sdkDir) {
            if ($IsWin) {
                $sdkDir = $sdkDir -replace '\\+', '\' -replace '/', '\'
            } else {
                $sdkDir = $sdkDir -replace '\\+', '/' -replace '/+', '/'
            }
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
            $sdkDir = $null
            if (Test-Path $localPropsPath) {
                $localProps = Get-Content $localPropsPath -Raw | ConvertFrom-StringData
                $sdkDir = $localProps["sdk.dir"]
                if ($sdkDir) {
                    if ($IsWin) {
                        $sdkDir = $sdkDir -replace '\\+', '\' -replace '/', '\'
                    } else {
                        $sdkDir = $sdkDir -replace '\\+', '/' -replace '/+', '/'
                    }
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

# 7. Premium Visual Summary
Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "            ✨ BUILD COMPLETED SUCCESSFULLY ✨            " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "Configuration: $Config" -ForegroundColor DarkCyan
Write-Host "Target(s):     $Target" -ForegroundColor DarkCyan
Write-Host "Version:       $Version" -ForegroundColor DarkCyan
Write-Host "Clean:         $(if ($NoClean) { 'Skipped (-NoClean)' } else { 'Full' })" -ForegroundColor DarkCyan
Write-Host "Output Folder: $BuildOutputDir" -ForegroundColor DarkCyan
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
exit 0

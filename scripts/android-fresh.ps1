param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$Install,
    [switch]$NoRerun,
    [switch]$UseConfigurationCache
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$gradleRoot = Join-Path $repoRoot "RemEx.Android"
$gradlew = Join-Path $gradleRoot "gradlew.bat"

if (-not (Test-Path $gradlew)) {
    throw "Gradle wrapper was not found: $gradlew"
}

$task = switch ("$Configuration|$($Install.IsPresent)") {
    "Debug|True" { "remexFreshInstallDebug" }
    "Debug|False" { "remexFreshAssembleDebug" }
    "Release|True" { "remexFreshAssembleRelease" }
    "Release|False" { "remexFreshAssembleRelease" }
}

$args = @($task)
if (-not $NoRerun) {
    $args += "--rerun-tasks"
}
if (-not $UseConfigurationCache) {
    $args += "--no-configuration-cache"
}
$args += "--stacktrace"

Write-Host "Running Gradle task '$task' from $gradleRoot" -ForegroundColor Cyan
Write-Host "Command: $gradlew $($args -join ' ')" -ForegroundColor DarkCyan

Push-Location $gradleRoot
try {
    & $gradlew @args
    if ($LASTEXITCODE -ne 0) {
        throw "Gradle task failed with exit code $LASTEXITCODE"
    }

    if ($Install -and $Configuration -eq "Release") {
        Write-Host "Installing signed Release APK..." -ForegroundColor Cyan
        $apkPath = Join-Path $gradleRoot "app\build\outputs\apk\release\app-release.apk"
        if (Test-Path $apkPath) {
            & adb install -r $apkPath
            if ($LASTEXITCODE -ne 0) {
                throw "ADB installation failed with exit code $LASTEXITCODE"
            }
        } else {
            throw "Release APK not found at $apkPath"
        }
    }
}
finally {
    Pop-Location
}

Write-Host "Completed successfully." -ForegroundColor Green

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
    "Release|True" {
        throw "Install is only supported for Debug. Use -Configuration Release without -Install."
    }
    default { "remexFreshAssembleRelease" }
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
}
finally {
    Pop-Location
}

Write-Host "Completed successfully." -ForegroundColor Green

#!/usr/bin/env pwsh
# Runs the .NET test projects with coverage and writes an HTML + text summary to artifacts/coverage.
# Needs: dotnet tool install -g dotnet-reportgenerator-globaltool
param(
    [string[]]$Projects = @('remex.core.tests', 'remex.agent.tests', 'remex.desktop.tests')
)
$ErrorActionPreference = 'Stop'
# pwsh -File hands a comma list over as one string.
$Projects = $Projects -split ',' | ForEach-Object Trim | Where-Object { $_ }
$root = Split-Path $PSScriptRoot -Parent
$raw = Join-Path $root 'artifacts/coverage/raw'
$report = Join-Path $root 'artifacts/coverage/report'

# reportgenerator merges every file under raw, so stale runs would skew the result.
Remove-Item $raw, $report -Recurse -Force -ErrorAction SilentlyContinue

$failed = @()
foreach ($p in $Projects) {
    dotnet test (Join-Path $root "$p/$p.csproj") --settings (Join-Path $root 'coverage.runsettings') `
        --collect 'XPlat Code Coverage' --results-directory $raw
    if ($LASTEXITCODE -ne 0) { $failed += $p }
}

reportgenerator "-reports:$raw/**/coverage.cobertura.xml" "-targetdir:$report" '-reporttypes:Html;TextSummary'
Get-Content (Join-Path $report 'Summary.txt') -TotalCount 20

if ($failed) { Write-Error "Tests failed in: $($failed -join ', ')" }

#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Verify that the MCP tools RemEx's instructions MANDATE are actually CALLABLE.

.DESCRIPTION
    This repo has now shipped the same failure three times: a subsystem reported
    health it did not have, and the instructions kept mandating it long after it
    stopped working.

      * memory-store, retired 2026-08-09 - its skills and SessionStart banner went
        on claiming it was live after the server stopped connecting.
      * The GitNexus block in AGENTS.md, which drifted until it instructed agents
        to do the exact opposite of what the code did.
      * gitnexus and token-savior, wiped out of ~/.claude.json somewhere between
        2026-08-15 and 2026-08-20 and not noticed for nine days, because their
        PreToolUse/PostToolUse hooks kept firing. The capability looked present.
        The tool surface was gone.

    Every one of those would have been caught by comparing MANDATED against
    CALLABLE. That is all this script does.

    It cannot invoke an MCP tool - it is a shell script, not a session - so it
    checks the layer underneath: is each mandated server DEFINED where Claude Code
    actually reads, and does its command resolve on disk. -Full adds the real
    liveness probe via `claude mcp list`.

.PARAMETER Quick
    Skip `claude mcp list`. Local invariants only. This is the hook mode: it still
    catches the ~/.claude.json wipe, which is the failure that actually happened.

.PARAMETER Full
    Run `claude mcp list` and require each mandated server to report Connected.
    Slower - it health-checks every remote server too. Use before trusting the
    routing matrix, and in CI.

.PARAMETER Hook
    Emit terse output and always exit 0. A session must never be blocked by its
    own diagnostics.

.EXAMPLE
    ./scripts/check-mcp-health.ps1 -Full
#>
[CmdletBinding()]
param(
    [switch]$Quick,
    [switch]$Full,
    [switch]$Hook
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$script:Problems = @()
$script:Warnings = @()

function Add-Problem([string]$Message) { $script:Problems += $Message }
function Add-Warning([string]$Message) { $script:Warnings += $Message }

function Write-Section([string]$Title) {
    if (-not $Hook) { Write-Host "`n== $Title" -ForegroundColor Cyan }
}

# ---------------------------------------------------------------------------
# 1. What does the repo claim to own?
# ---------------------------------------------------------------------------
Write-Section 'Repo-owned server definitions (.mcp.json)'

$McpJsonPath = Join-Path $RepoRoot '.mcp.json'
if (-not (Test-Path $McpJsonPath)) {
    Add-Problem ".mcp.json is missing from $RepoRoot. The server set is not version-controlled; a wipe will be silent again (RemEx-56fu.6)."
    $Mandated = @('gitnexus', 'token-savior')
} else {
    $McpJson = Get-Content $McpJsonPath -Raw | ConvertFrom-Json
    $Mandated = @($McpJson.mcpServers.PSObject.Properties.Name)
    if (-not $Hook) { Write-Host "  mandated: $($Mandated -join ', ')" }
}

# ---------------------------------------------------------------------------
# 2. Does each mandated server's command resolve on THIS machine?
#    .mcp.json defaults to Connor's Windows paths; Linux uses the overrides.
# ---------------------------------------------------------------------------
Write-Section 'Command resolution'

function Resolve-ServerCommand([string]$Spec) {
    # Expand ${VAR} / ${VAR:-default} the way Claude Code does.
    $expanded = [regex]::Replace($Spec, '\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-(.*?))?\}', {
        param($m)
        $name = $m.Groups[1].Value
        $fallback = if ($m.Groups[2].Success) { $m.Groups[2].Value } else { '' }
        $value = [Environment]::GetEnvironmentVariable($name)
        if ([string]::IsNullOrEmpty($value)) { $fallback } else { $value }
    })
    return $expanded
}

foreach ($name in $Mandated) {
    if (-not (Test-Path $McpJsonPath)) { break }
    $raw = $McpJson.mcpServers.$name.command
    $cmd = Resolve-ServerCommand $raw
    $ok = (Test-Path -LiteralPath $cmd -ErrorAction SilentlyContinue) -or
          ($null -ne (Get-Command $cmd -ErrorAction SilentlyContinue))
    if ($ok) {
        if (-not $Hook) { Write-Host "  OK    $name -> $cmd" -ForegroundColor Green }
    } else {
        Add-Problem "$name command does not resolve: '$cmd'. On Linux set REMEX_GITNEXUS_BIN / REMEX_TOKEN_SAVIOR_BIN (see .mcp.json)."
    }
}

# ---------------------------------------------------------------------------
# 3. THE DECOY CHECK. Two files claim to define MCP servers. Only one is read.
#    The dead one is the one a human is most likely to open.
# ---------------------------------------------------------------------------
Write-Section 'Config decoy'

$UserSettings = Join-Path $HOME '.claude/settings.json'
if (Test-Path $UserSettings) {
    $settings = Get-Content $UserSettings -Raw | ConvertFrom-Json
    $decoy = $settings.PSObject.Properties.Name -contains 'mcpServers'
    if ($decoy) {
        $names = @($settings.mcpServers.PSObject.Properties.Name)
        Add-Warning "~/.claude/settings.json has an 'mcpServers' block ($($names -join ', ')) that Claude Code DOES NOT READ. It is inert and it is what made the 2026-08 outage invisible for nine days. Delete it or comment it, do not maintain it."
    } else {
        if (-not $Hook) { Write-Host '  OK    no inert mcpServers block in settings.json' -ForegroundColor Green }
    }
}

# ---------------------------------------------------------------------------
# 4. Is each mandated server defined where Claude Code DOES read?
#    This is the check that would have caught the actual wipe.
# ---------------------------------------------------------------------------
Write-Section 'Live config (~/.claude.json)'

$ClaudeJson = Join-Path $HOME '.claude.json'
if (-not (Test-Path $ClaudeJson)) {
    Add-Problem "~/.claude.json not found. Cannot verify server definitions."
} else {
    $live = Get-Content $ClaudeJson -Raw | ConvertFrom-Json

    $userScope = @()
    if ($live.PSObject.Properties.Name -contains 'mcpServers' -and $live.mcpServers) {
        $userScope = @($live.mcpServers.PSObject.Properties.Name)
    }

    $localScope = @()
    $approved = @()
    foreach ($key in @($RepoRoot, ($RepoRoot -replace '\\', '/'))) {
        $proj = $live.projects.PSObject.Properties | Where-Object { $_.Name -eq $key }
        if ($proj) {
            $v = $proj.Value
            if ($v.PSObject.Properties.Name -contains 'mcpServers' -and $v.mcpServers) {
                $localScope += @($v.mcpServers.PSObject.Properties.Name)
            }
            if ($v.PSObject.Properties.Name -contains 'enabledMcpjsonServers' -and $v.enabledMcpjsonServers) {
                $approved += @($v.enabledMcpjsonServers)
            }
        }
    }

    foreach ($name in $Mandated) {
        $where = @()
        if ($userScope -contains $name)  { $where += 'user' }
        if ($localScope -contains $name) { $where += 'local' }
        if ($approved -contains $name)   { $where += 'project(.mcp.json, approved)' }

        if ($where.Count -gt 0) {
            if (-not $Hook) { Write-Host "  OK    $name defined at: $($where -join ', ')" -ForegroundColor Green }
        } elseif (Test-Path $McpJsonPath) {
            Add-Warning "$name is declared in .mcp.json but has not been approved for this project yet. Claude Code prompts once per project - approve it via /mcp, or it will not load."
        } else {
            Add-Problem "$name is not defined in ~/.claude.json at any scope, and there is no .mcp.json. The tools CLAUDE.md mandates for it are uncallable."
        }
    }
}

# ---------------------------------------------------------------------------
# 5. Index freshness. A stale index answers confidently from a dead snapshot,
#    which is its own species of reporting health you do not have.
# ---------------------------------------------------------------------------
Write-Section 'Index freshness'

$gitnexusCmd = $null
if (Test-Path $McpJsonPath) {
    $gitnexusCmd = Resolve-ServerCommand $McpJson.mcpServers.'gitnexus'.command
}
if ($gitnexusCmd -and ((Test-Path -LiteralPath $gitnexusCmd -ErrorAction SilentlyContinue) -or (Get-Command $gitnexusCmd -ErrorAction SilentlyContinue))) {
    try {
        $status = & $gitnexusCmd status 2>&1 | Out-String
        $indexed = ([regex]::Match($status, 'Indexed commit:\s*(\S+)')).Groups[1].Value
        $current = ([regex]::Match($status, 'Current commit:\s*(\S+)')).Groups[1].Value
        if ($indexed -and $current) {
            if ($indexed -eq $current) {
                if (-not $Hook) { Write-Host "  OK    gitnexus index at $indexed (up to date)" -ForegroundColor Green }
            } else {
                $behind = (& git -C $RepoRoot rev-list --count "$indexed..HEAD" 2>$null)
                Add-Warning "gitnexus index is stale: indexed $indexed, HEAD $current$(if ($behind) { " ($behind commits behind)" }). It will answer from that snapshot without saying so. Run: gitnexus analyze"
            }
        }
    } catch {
        Add-Warning "Could not read gitnexus status: $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------------------
# 6. Real liveness probe. -Full only: `claude mcp list` health-checks every
#    remote server too, so it is too slow for a SessionStart hook.
# ---------------------------------------------------------------------------
if ($Full -and -not $Quick) {
    Write-Section 'Liveness (claude mcp list)'
    try {
        $listing = & claude mcp list 2>&1 | Out-String
        foreach ($name in $Mandated) {
            $line = ($listing -split "`n") | Where-Object { $_ -match "^\s*$([regex]::Escape($name))\s*:" } | Select-Object -First 1
            if (-not $line) {
                Add-Problem "$name does not appear in 'claude mcp list' at all. Its tools are uncallable regardless of what CLAUDE.md says."
            } elseif ($line -notmatch 'Connected') {
                Add-Problem "$name is present but not Connected: $($line.Trim())"
            } else {
                if (-not $Hook) { Write-Host "  OK    $name Connected" -ForegroundColor Green }
            }
        }
    } catch {
        Add-Warning "Could not run 'claude mcp list': $($_.Exception.Message)"
    }
} elseif (-not $Hook -and -not $Quick) {
    Write-Host "`n  (pass -Full to probe actual connectivity via 'claude mcp list')" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------
if ($Hook) {
    if ($script:Problems.Count -gt 0) {
        Write-Host "[mcp-health] $($script:Problems.Count) problem(s): $($script:Problems -join ' | ')"
    }
    foreach ($w in $script:Warnings) { Write-Host "[mcp-health] warn: $w" }
    exit 0
}

Write-Host ''
foreach ($w in $script:Warnings) { Write-Host "WARN  $w" -ForegroundColor Yellow }
foreach ($p in $script:Problems) { Write-Host "FAIL  $p" -ForegroundColor Red }

if ($script:Problems.Count -eq 0 -and $script:Warnings.Count -eq 0) {
    Write-Host 'MCP health: mandated servers are defined, resolvable and fresh.' -ForegroundColor Green
    exit 0
}
if ($script:Problems.Count -eq 0) {
    Write-Host "MCP health: $($script:Warnings.Count) warning(s), no failures." -ForegroundColor Yellow
    exit 0
}
Write-Host "MCP health: $($script:Problems.Count) failure(s). The routing matrix in CLAUDE.md is NOT true right now." -ForegroundColor Red
exit 1

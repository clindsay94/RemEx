<#
.SYNOPSIS
  SessionStart hook: compact replacement for `bd prime --hook-json` (RemEx-wcvck).

.DESCRIPTION
  `bd prime` prints every `bd remember` entry in full: about 80 KB at ~90 memories. Claude Code
  moves hook output over ~10 KB into a file and shows a 2 KB preview, so the memories were either
  invisible or cost ~20k tokens to read back in. This prints the workflow essentials plus memory
  TITLES only. The titles are descriptive slugs; `bd memories <keyword>` fetches the full text on
  demand.

  Do not "repair" this back to `bd prime` via `bd setup claude`: that also writes a beads block
  into a root CLAUDE.md, which InstructionFileTests rejects.
#>
$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$keys = @()
$raw = (& bd -C $repo memories --json 2>$null | Out-String)
if ($raw.Trim()) {
    $mem = $raw | ConvertFrom-Json
    # Some entries are aliases whose value is only another key; list the real ones.
    $keys = @($mem.PSObject.Properties |
        Where-Object { "$($_.Value)" -notmatch '^[a-z0-9-]+$' } |
        ForEach-Object Name | Sort-Object)
}

$lines = @(
    '# Beads (bd) is the task tracker'
    '- Before coding: `bd create "<title>" -t bug|feature|task|chore -p 0-4`, then `bd update <id> --claim`. `bd close <id>` before reporting done. No TodoWrite / TaskCreate / markdown TODOs.'
    '- Find work: `bd ready`, `bd show <id>`, `bd list --status open`. Priority: 0 critical ... 4 backlog.'
    '- Record a lesson: `bd remember --key <slug> "SYMPTOM -> CAUSE -> FIX"` (<= 4 lines). Read one: `bd memories <keyword>`.'
    ''
    "## Project memories ($($keys.Count)): titles only"
    'Before working in an area, run `bd memories <keyword>` for any title that matches it.'
) + $keys

@{ hookSpecificOutput = @{ hookEventName = 'SessionStart'; additionalContext = ($lines -join "`n") } } |
    ConvertTo-Json -Compress -Depth 4

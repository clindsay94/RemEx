# PowerShell script to fix symlinks for Claude Code

$claudeDir = "$HOME\.claude"
$agentsDir = "$HOME\.agents"

# 1. Clean up the capitalized/incorrect links if they exist
Write-Host "Cleaning up old links..."
Remove-Item -Force -ErrorAction SilentlyContinue "$claudeDir\Personas"
Remove-Item -Force -ErrorAction SilentlyContinue "$claudeDir\Skills"
Remove-Item -Force -ErrorAction SilentlyContinue "$claudeDir\personas"
Remove-Item -Force -ErrorAction SilentlyContinue "$claudeDir\skills"

# 2. Create the correct lowercase symlinks
Write-Host "Creating new lowercase symlinks..."
try {
    New-Item -ItemType SymbolicLink -Path "$claudeDir\agents" -Target "$agentsDir\personas" -ErrorAction Stop
    Write-Host "Success: Linked ~/.claude/agents -> ~/.agents/personas" -ForegroundColor Green
} catch {
    Write-Warning "Failed to create SymbolicLink for agents. Error: $_"
    Write-Host "Attempting Junction fallback..."
    New-Item -ItemType Junction -Path "$claudeDir\agents" -Target "$agentsDir\personas"
}

try {
    New-Item -ItemType SymbolicLink -Path "$claudeDir\skills" -Target "$agentsDir\skills" -ErrorAction Stop
    Write-Host "Success: Linked ~/.claude/skills -> ~/.agents/skills" -ForegroundColor Green
} catch {
    Write-Warning "Failed to create SymbolicLink for skills. Error: $_"
    Write-Host "Attempting Junction fallback..."
    New-Item -ItemType Junction -Path "$claudeDir\skills" -Target "$agentsDir\skills"
}

# 3. Verification
Write-Host "`nVerifying current links in ~/.claude:"
Get-Item "$claudeDir\agents", "$claudeDir\skills" -ErrorAction SilentlyContinue | Select-Object Name, LinkType, Target

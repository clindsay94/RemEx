#!/usr/bin/env bash
# Linux/CachyOS entry point for the MCP health check.
#
# This is a shim, not a second implementation, and that is deliberate. The whole
# point of RemEx-56fu.6 is that two copies of the same claim drift apart until one
# of them is lying - which is how ~/.claude/settings.json ended up advertising
# seven MCP servers that Claude Code never read. Reimplementing this check in bash
# would recreate exactly that shape. The repo already requires pwsh on Linux for
# scripts/verify.ps1, so there is one implementation and both platforms run it.
#
# Set the two overrides in your shell profile before running:
#   export REMEX_GITNEXUS_BIN=gitnexus
#   export REMEX_TOKEN_SAVIOR_BIN="$HOME/.venv/bin/token-savior"
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if ! command -v pwsh >/dev/null 2>&1; then
    echo "check-mcp-health: pwsh not found on PATH." >&2
    echo "  RemEx's verification scripts are PowerShell on both platforms." >&2
    echo "  CachyOS: paru -S powershell-bin" >&2
    exit 127
fi

exec pwsh -NoProfile -File "$SCRIPT_DIR/check-mcp-health.ps1" "$@"

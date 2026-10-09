#!/usr/bin/env bash
# Setup script for a Claude Code CLOUD environment (Ubuntu 24.04, runs as root before Claude starts).
# Paste this into: Claude desktop app > Code > Cloud > environment settings > Setup script.
# Rules of the cloud setup script: it must exit 0 and finish in about 5 minutes, or it isn't cached.
# Every optional step ends in `|| true` so one failed download can't block the session.
# UNTESTED: none of these installs has been run in a cloud VM yet. Check the first session's output.
# Network access must be Full (or Custom with dot.net, packages.microsoft.com, github.com,
# raw.githubusercontent.com, registry.npmjs.org) for the downloads below.

export DEBIAN_FRONTEND=noninteractive

# --- .NET 10 SDK (not preinstalled in cloud VMs) ---
if ! command -v dotnet >/dev/null 2>&1; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
    && ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet || true
fi

# --- PowerShell 7 (repo scripts and hooks call `pwsh`) ---
if ! command -v pwsh >/dev/null 2>&1; then
  curl -fsSL https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -o /tmp/ms-prod.deb \
    && dpkg -i /tmp/ms-prod.deb \
    && apt-get update -qq \
    && apt-get install -y -qq powershell || true
fi

# --- beads (`bd`): the repos track work with it ---
if ! command -v bd >/dev/null 2>&1; then
  npm install -g @beads/bd >/dev/null 2>&1 \
    || curl -fsSL https://raw.githubusercontent.com/steveyegge/beads/main/scripts/install.sh | bash || true
fi

# --- Code-intelligence MCP servers (same three as the local setup) ---
# token-savior (PyPI package token-savior-recall), gitnexus and context-mode (npm).
# Registered at USER scope on this VM rather than via a committed .mcp.json, because RemEx
# deliberately has no repo .mcp.json (.claude/CLAUDE.md, 2026-09-10).
# UNTESTED: whether cloud sessions read ~/.claude.json written by this script. Check /mcp in a session.
uv tool install token-savior-recall >/dev/null 2>&1 || true
npm install -g gitnexus context-mode >/dev/null 2>&1 || true
export PATH="$HOME/.local/bin:$PATH"
if command -v claude >/dev/null 2>&1; then
  claude mcp add --scope user token-savior -- token-savior >/dev/null 2>&1 || true
  claude mcp add --scope user gitnexus -- gitnexus mcp >/dev/null 2>&1 || true
  claude mcp add --scope user context-mode -- context-mode >/dev/null 2>&1 || true
else
  # No `claude` CLI on the VM: write the entries into ~/.claude.json directly.
  CJ="$HOME/.claude.json"
  [ -s "$CJ" ] || echo '{}' > "$CJ"
  jq '.mcpServers = ((.mcpServers // {}) + {
        "token-savior": {"type":"stdio","command":"token-savior","args":[]},
        "gitnexus":     {"type":"stdio","command":"gitnexus","args":["mcp"]},
        "context-mode": {"type":"stdio","command":"context-mode","args":[]}})' "$CJ" > /tmp/cj.json \
    && mv /tmp/cj.json "$CJ" || true
fi

# --- Personal rules: ~/.claude/CLAUDE.md does not travel to cloud sessions, so write a Linux version ---
mkdir -p /root/.claude
cat > /root/.claude/CLAUDE.md <<'EOF'
# Global instructions (Connor): cloud-session edition

A repo's own CLAUDE.md wins on anything project-specific. This is a trimmed copy of Connor's global
file for Linux cloud sessions, written by the environment setup script.

## Environment
- This is a cloud VM (Ubuntu). Repos must work on case-sensitive paths: copy exact case from `ls` or `git status`.
- Scripts are `pwsh` (PowerShell 7) or have a `.sh` twin. Python is uv-managed: `uv run python`, `uv pip install`, never bare `pip`.
- Write multi-line scripts to a file and run the file, never as a heredoc.
- Code intelligence: read structurally, edit literally. MCP tools are deferred, so load them with ToolSearch first.
  token-savior (`find_symbol`, `get_function_source`, `get_edit_context`, `get_dependents`, `search_codebase`)
  for symbols and callers; gitnexus (`query`, `context`, `impact`, `detect_changes`) for flows and blast radius;
  context-mode (`ctx_execute`, `ctx_batch_execute`, `ctx_search`) for builds, tests and logs you'll filter.
  Read a file (or the range you need) before editing it. Run `impact` before cross-cutting edits.
- If gitnexus says there is no index, run `npx gitnexus analyze --skip-agents-md` (the flag is mandatory).
  If a code-intelligence tool is missing (check `/mcp`), say so and fall back to Read, Grep and Glob.

## Workflow
- In repos with `.beads/`, use beads (`bd`): `bd create` before coding, `bd update <id> --claim`, `bd close <id>` before reporting done. No markdown TODO lists.
- Commit messages use a conventional-commit prefix plus the bead ID, e.g. `fix(pairing): ... (RemEx-xxxx)`.
- Never bump a version number (.NET `<Version>`, Android versionCode/versionName) without asking first.
- Push to a branch and open a PR; never push to main.

## Communication
Be plain and direct. Report failures along with their output, and don't hedge results you've verified.
EOF

exit 0

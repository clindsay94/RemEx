# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

RemEx is a cross-platform remote PC management tool. It consists of a .NET 10 / Avalonia desktop client, a headless ASP.NET host service, and a native Android app (Kotlin + Jetpack Compose). The `Remex.Core` library is shared across all targets and is also compiled as a NativeAOT JNI native library (`libRemexCore.so`) for Android.

## Build & Run

```powershell
# Run host service
dotnet run --project Remex.Host

# Run desktop client
dotnet run --project Remex.Client.Desktop

# Run all tests
dotnet test Remex.sln

# Unified build (all platforms, release)
pwsh ./build-remex.ps1 -c release -t all

# Android only — hardened fresh build
.\scripts\android-fresh.ps1 -Configuration Release

# Linux packages (run from repo root; uses WSL on Windows)
./installer/build-linux.sh
```

## Architecture

```
Remex.Core/              Shared models, messages, validation, Guards, serialization
                         ↳ Also compiled as libRemexCore.so (NativeAOT JNI) for Android
Remex.Host/              Headless ASP.NET service: Minimal APIs, WebSocket, mDNS
Remex.Client/            Shared Avalonia UI: views, viewmodels, controls, services, themes
Remex.Client.Desktop/    Desktop entry point (Windows / Linux) — thin wrapper over Remex.Client
RemEx.Android/           Native Android app — Kotlin + Jetpack Compose + JNI → libRemexCore.so
```

### Communication Protocols

| Protocol | Endpoint | Port | Purpose |
|---|---|---|---|
| WSS | `/ws` | 5005 | Telemetry, power commands, pairing, file transfer |
| WSS | `/ws/desktop` | 5005 | H.264 / MJPEG remote desktop stream |
| TCP (TLS) | — | 8338 | External script command ingress |
| Named Pipe | `RemExLocalIPC` | — | Local IPC between client and Windows Service |

All messages over `/ws` use the `RemexMessage` JSON envelope with `protocolVersion: 2`. Pairing uses ECDH P-256 + 6-digit PIN; clients then pin the host certificate SPKI hash.

### Key Directories in Remex.Client

`Views/` and `ViewModels/` follow standard MVVM. `Services/` holds connection, layout, telemetry, and theme services. `Themes/` has the four glassmorphic themes (CyberNOC, Monolith, SolarFlare, BaseDarkGlass). `Localization/` drives live 8-language switching without restart.

## Versioning

- **.NET projects**: centrally managed in `Directory.Build.props` (`<Version>`)
- **Android**: managed in `RemEx.Android/app/version.properties` (`versionName` / `versionCode`)
- `build-remex.ps1` syncs `Directory.Build.props` from `version.properties` automatically

## Coding Conventions

### Async
**Do NOT use `ConfigureAwait(false)` anywhere.** Neither Avalonia nor ASP.NET Core uses `SynchronizationContext`. CA2007 is suppressed in `.editorconfig`. See `docs/ASYNC_GUIDELINES.md`.

### Null Safety
Nullable reference types are enabled in all projects. Use `Guard.NotNull(arg)` (from `Remex.Core/Guards/Guard.cs`) in constructors for required dependencies. Use `GetRequiredService<T>()` (not `GetService<T>()`) for DI resolution. See `docs/NULL_SAFETY_GUIDELINES.md`.

### Validation
All network-facing input must be validated through the shared validation helpers in `Remex.Core/Validation/`. See `docs/VALIDATION_GUIDELINES.md`.

## Android Prerequisites

- Android SDK API Level 37 platform required
- NDK version **30.0.14904198** required for NativeAOT JNI compilation
- `build-remex.ps1` auto-installs both via `sdkmanager` if absent
- Set `ANDROID_HOME` or configure `RemEx.Android/local.properties` (`sdk.dir=...`)

## Host Diagnostics

On Linux, run `dotnet run --project Remex.Host -- --doctor` to check PipeWire/X11/VAAPI prerequisites.

<!-- agent-team:start -->
## Agent Team & Communication

See global instructions: `~/.claude/CLAUDE.md`

**Project-level coordination for RemEx 2.0:**
- Root mission control: `/home/connorl/RemEx/AGENTS.md` — phase gates, chokepoint files, master plan
- Sub-project playbooks: `Remex.Core/AGENTS.md`, `Remex.Client.Desktop/AGENTS.md`, etc.

Read the relevant sub-project `AGENTS.md` before touching files in that directory.
<!-- agent-team:end -->

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **RemEx** (10520 symbols, 20401 relationships, 300 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> If any GitNexus tool warns the index is stale, run `npx gitnexus analyze` in terminal first.

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `gitnexus_impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `gitnexus_detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `gitnexus_query({query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `gitnexus_context({name: "symbolName"})`.

## Never Do

- NEVER edit a function, class, or method without first running `gitnexus_impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `gitnexus_rename` which understands the call graph.
- NEVER commit changes without running `gitnexus_detect_changes()` to check affected scope.

## Resources

| Resource | Use for |
|----------|---------|
| `gitnexus://repo/RemEx/context` | Codebase overview, check index freshness |
| `gitnexus://repo/RemEx/clusters` | All functional areas |
| `gitnexus://repo/RemEx/processes` | All execution flows |
| `gitnexus://repo/RemEx/process/{name}` | Step-by-step execution trace |

## CLI

| Task | Read this skill file |
|------|---------------------|
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus/gitnexus-cli/SKILL.md` |

<!-- gitnexus:end -->


<!-- BEGIN BEADS INTEGRATION v:1 profile:minimal hash:970c3bf2 -->
## Beads Issue Tracker

This project uses **bd (beads)** for issue tracking. Run `bd prime` to see full workflow context and commands.

### Quick Reference

```bash
bd ready              # Find available work
bd show <id>          # View issue details
bd update <id> --claim  # Claim work
bd close <id>         # Complete work
```

### Rules

- Use `bd` for ALL task tracking — do NOT use TodoWrite, TaskCreate, or markdown TODO lists
- Run `bd prime` for detailed command reference and session close protocol
- Use `bd remember` for persistent knowledge — do NOT use MEMORY.md files

**Architecture in one line:** issues live in a local Dolt DB; sync uses `refs/dolt/data` on your git remote; `.beads/issues.jsonl` is a passive export. See https://github.com/gastownhall/beads/blob/main/docs/SYNC_CONCEPTS.md for details and anti-patterns.

## Agent Context Profiles

The managed Beads block is task-tracking guidance, not permission to override repository, user, or orchestrator instructions.

- **Conservative (default)**: Use `bd` for task tracking. Do not run git commits, git pushes, or Dolt remote sync unless explicitly asked. At handoff, report changed files, validation, and suggested next commands.
- **Minimal**: Keep tool instruction files as pointers to `bd prime`; use the same conservative git policy unless active instructions say otherwise.
- **Team-maintainer**: Only when the repository explicitly opts in, agents may close beads, run quality gates, commit, and push as part of session close. A current "do not commit" or "do not push" instruction still wins.

## Session Completion

This protocol applies when ending a Beads implementation workflow. It is subordinate to explicit user, repository, and orchestrator instructions.

1. **File issues for remaining work** - Create beads for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Update issue status** - Close finished work, update in-progress items
4. **Handle git/sync by active profile**:
   ```bash
   # Conservative/minimal/default: report status and proposed commands; wait for approval.
   git status

   # Team-maintainer opt-in only, unless current instructions forbid it:
   git pull --rebase
   bd dolt push
   git push
   git status
   ```
5. **Hand off** - Summarize changes, validation, issue status, and any blocked sync/commit/push step

**Critical rules:**
- Explicit user or orchestrator instructions override this Beads block.
- Do not commit or push without clear authority from the active profile or the current user request.
- If a required sync or push is blocked, stop and report the exact command and error.
<!-- END BEADS INTEGRATION -->

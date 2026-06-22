## RemEx Architecture & Project Rules

These rules apply to ALL agents working in this repository. They are not overridable by individual session context.

### Host = PC, Client = Android. End of Story.

- `Remex.Host` is the **entire PC side** — Windows Service/daemon plus all PC functionality. Android connects TO this.
- `RemEx.Android` is the **only** network client. Nothing else is a client.
- `Remex.Client/` and `Remex.Client.Desktop/` are legacy folders being phased out. Do NOT add new code there.
- Connection is always Android → PC, always non-loopback.
- If you find old references to a "desktop client" connecting to a "desktop host", update them to reflect the current architecture.

<!-- AUTO-MANAGED: build-commands -->
### Build & Run

`build-remex.ps1` is the canonical cross-platform entry point (runs under `pwsh` on Windows and Linux).

```powershell
# Full release build for all platforms
./build-remex.ps1 -c release -t all

# Platform-specific targets
./build-remex.ps1 -t windows        # publish + Inno Setup installer
./build-remex.ps1 -t android        # APK + AAB via Gradle
./build-remex.ps1 -t linux          # tar.gz via installer/build-linux.sh (WSL on Windows)

# Target aliases
./build-remex.ps1 -t installer      # Windows installer only (skips publish if artifacts/ exists)
./build-remex.ps1 -t windows-client # Windows publish only (skips installer)
./build-remex.ps1 -t apk            # alias for -t android

# Incremental rebuild (skip clean, reuse artifacts/)
./build-remex.ps1 -t windows -NoClean
```

**Build output layout:**
- Intermediate / publish: `artifacts/` (UseArtifactsOutput — not per-project `bin/` or `obj/`)
  - Windows publish: `artifacts/publish/RemEx.Host/{Config}_win-x64/`
- Final distributables: `build_output/windows/`, `build_output/android/`, `build_output/linux/`

**Android prerequisites** (auto-installed by the script if missing):
- Android SDK API Level 37
- NDK version `30.0.14904198`
- Requires `ANDROID_HOME` env var or `RemEx.Android/local.properties` (`sdk.dir=...`)

**Version sync:** script reads `versionName` from `RemEx.Android/app/version.properties` and patches `Directory.Build.props` automatically on every run.
<!-- END AUTO-MANAGED -->

### MCP Tool Discipline

Before reaching for `grep`, `Read`, or raw `Bash`, consult the decision matrix in `CLAUDE.md`:
- **Find / read symbols** → `token-savior: find_symbol`, `get_function_source`, `get_dependencies`
- **Before ANY edit** → `gitnexus: impact` (upstream blast radius)
- **Explore flows / concepts** → `gitnexus: query` or `gitnexus: context`
- **Large command output / data processing** → `context-mode: ctx_execute`
- **Generating >3 new/changed files** → `agy -p "prompt"` (Gemini/antigravity headless)

### Cross-Platform Parity

Every PC-side change must work on **both Windows and CachyOS/Linux**. The repo lives on a shared drive. New scripts need a `pwsh`-compatible path or a `.sh` equivalent. `build-remex.ps1` is the canonical entry point for both OSes.

### Code Quality

No lazy code. Use the most correct, robust, maintainable approach. No stub methods, `TODO:` bodies, or "good enough for now" placeholders. Check existing infrastructure (`Remex.Core/Guards`, `Remex.Core/Validation`) before writing new utilities.

### Docs & CHANGELOG on Every Change

Every code change must also update `CHANGELOG.md` (Keep a Changelog format). Update affected XML doc comments, `docs/` files, and version numbers when warranted. A task is not complete until docs are updated.

### Beads for Task Tracking

Use `bd` for ALL task tracking. Create an issue before writing code. Claim it. Close it when done. Never use TodoWrite, TaskCreate, or markdown TODO lists. Run `bd prime` for the full workflow context.

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **RemEx** (11183 symbols, 21595 relationships, 300 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

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

<!-- BEGIN BEADS CODEX SETUP: generated by bd setup codex -->
## Beads Issue Tracker

Use Beads (`bd`) for durable task tracking in repositories that include it. Use the `beads` skill at `.agents/skills/beads/SKILL.md` (project install) or `~/.agents/skills/beads/SKILL.md` (global install) for Beads workflow guidance, then use the `bd` CLI for issue operations.

### Quick Reference

```bash
bd ready                # Find available work
bd show <id>            # View issue details
bd update <id> --claim  # Claim work
bd close <id>           # Complete work
bd prime                # Refresh Beads context
```

### Rules

- Use `bd` for all task tracking; do not create markdown TODO lists.
- Run `bd prime` when Beads context is missing or stale. Codex 0.129.0+ can load Beads context automatically through native hooks; use `/hooks` to inspect or toggle them.
- Keep persistent project memory in Beads via `bd remember`; do not create ad hoc memory files.

**Architecture in one line:** issues live in a local Dolt DB; sync uses `refs/dolt/data` on your git remote; `.beads/issues.jsonl` is a passive export. See https://github.com/gastownhall/beads/blob/main/docs/SYNC_CONCEPTS.md for details and anti-patterns.
<!-- END BEADS CODEX SETUP -->

<!-- AUTO-MANAGED: project-description -->
## Overview

Remote Execution (RemEx) is a cross-platform PC remote management tool. Architecture is **Android (client) → PC (host)**, always non-loopback. `Remex.Host` is the entire PC side (Windows Service / Linux daemon plus all PC functionality). `RemEx.Android` is the only network client. `Remex.Core` is shared across all targets and is also compiled as a NativeAOT JNI native library (`libRemexCore.so`) for Android.

<!-- END AUTO-MANAGED -->

<!-- AUTO-MANAGED: build-commands -->
## Build & Development Commands

- `dotnet run --project Remex.Host` — run the PC host service (Android connects to this).
- `dotnet test Remex.sln` — run all tests.
- `pwsh ./build-remex.ps1 -c release -t all` — unified cross-platform release build (canonical entry point).
- `.\scripts\android-fresh.ps1 -Configuration Release` — hardened fresh Android build.
- `./installer/build-linux.sh` — build Linux packages (uses WSL on Windows).
- `dotnet run --project Remex.Host -- --doctor` — check Linux PipeWire/X11/VAAPI prerequisites.

<!-- END AUTO-MANAGED -->

<!-- AUTO-MANAGED: architecture -->
## Architecture

- `Remex.Core/` — shared models, messages, validation, Guards, serialization; also compiled as `libRemexCore.so` (NativeAOT JNI) for Android. Must stay NativeAOT-safe.
- `Remex.Host/` — the PC side: Windows Service / Linux daemon, ASP.NET Minimal APIs, WebSocket, mDNS. Runs as LocalSystem in Session 0 on Windows.
- `RemEx.Android/` — the only client: Kotlin + Jetpack Compose + JNI → `libRemexCore.so`.
- `Remex.Client/`, `Remex.Client.Desktop/` — legacy, being phased out; do not add new code.

Protocols: WSS `/ws` (port 5005, telemetry/power/pairing/file transfer), WSS `/ws/desktop` (port 5005, H.264/MJPEG remote desktop), TCP+TLS (8338, external script ingress), Named Pipe `RemExLocalIPC` (local service IPC). Messages use the `RemexMessage` JSON envelope with `protocolVersion: 2`. Pairing uses ECDH P-256 + 6-digit PIN, then SPKI certificate pinning.

<!-- END AUTO-MANAGED -->

<!-- AUTO-MANAGED: conventions -->
## Code Conventions

- Do NOT use `ConfigureAwait(false)` anywhere (CA2007 suppressed).
- Nullable reference types enabled everywhere; use `Guard.NotNull(arg)` and `GetRequiredService<T>()`.
- Validate all network-facing input via `Remex.Core/Validation/`.
- `Remex.Core` must be NativeAOT-safe: no reflection, no dynamic codegen, source-generated JSON only.
- On Windows, `Remex.Host` runs as LocalSystem in Session 0: never use `HKCU`/`%APPDATA%`; use `HKLM` and `ProgramData`; keep correct Named Pipe ACLs.
- All user-facing strings in `Remex.Host` go through `Localization/` (8 languages, live switching).
- Versions: .NET in `Directory.Build.props`; Android in `RemEx.Android/app/version.properties`.

<!-- END AUTO-MANAGED -->

<!-- AUTO-MANAGED: patterns -->
## Detected Patterns

- MVVM in `Remex.Host` (`Views/`, `ViewModels/`, `Services/`); four glassmorphic themes (CyberNOC, Monolith, SolarFlare, BaseDarkGlass) — verify UI changes across all four.
- Cross-platform parity (Windows ↔ CachyOS/Linux) required for every PC-side change; each `.ps1` needs a `pwsh`-compatible path or `.sh` equivalent.
- Every change updates `CHANGELOG.md` (Keep a Changelog) and affected docs.

<!-- END AUTO-MANAGED -->

<!-- AUTO-MANAGED: git-insights -->
## Git Insights

- Active development branch: `2.0` (main branch for PRs: `main`).
- Hottest areas by recent history: `Remex.Host` (PC side), `RemEx.Android`, `Remex.Host.Native.Linux`, with `Remex.Client` being phased out.
- Gitignored (do not commit): AI tool dirs (`.gemini/`, `.superpowers/`, `.antigravitycli/`), `.claude/auto-memory/dirty-files*`, `.claude/settings.local.json`, `.beads/proxieddb/`, `.beads-credential-key`, `.dolt/`, `*.db`. Only `.beads/issues.jsonl` is tracked (passive Beads export).

<!-- END AUTO-MANAGED -->

<!-- AUTO-MANAGED: best-practices -->
## Best Practices

- Run `gitnexus_impact` before editing any symbol; warn on HIGH/CRITICAL risk; run `gitnexus_detect_changes()` before committing.
- Prefer `token-savior` / `gitnexus` / `context-mode` MCP tools over raw `grep`/`Read`/`Bash` for analysis (see decision matrix in `CLAUDE.md`).
- No placeholder/stub code; file a beads issue for out-of-scope work and implement in-scope correctly.
- Coordinate any change to pairing, certificate pinning, the `RemexMessage` envelope, or Named Pipe security across both Android and host — these are security-critical.

<!-- END AUTO-MANAGED -->

<!-- AUTO-MANAGED: release-gate -->
## RemEx 2.0 Release Gate

**Status: NO-SHIP.** Full ordered edit plan: `REMEX_2.0_FINAL.md` (read-only audit — no code was modified by the audit itself).

### Confirmed Ship-Blockers (do not ship until resolved)

1. **PROTO-1 (RemEx-htt)** — `RemexNetworkListener` binds `IPAddress.Any` on 8338 and dispatches SHUTDOWN/RESTART/SLEEP/LOCK with **zero client authentication**. Any device on the LAN can power-control the PC.
2. **PAIR-5 (RemEx-a75) + PAIR-2 (RemEx-lhd)** — `/start-pairing` and `/pairing-pin` are reachable by unauthenticated remote callers; PIN verification has no brute-force throttle over a 10-minute session.
3. **IPC-1 (RemEx-m1i)** — `RemExLocalIPC` pipe ACL grants `Everyone` read/write; any local user can read the live pairing PIN or issue power commands.

### P0 Beads (all must be closed before ship)

`RemEx-htt` PROTO-1 · `RemEx-a75` PAIR-5 · `RemEx-m1i` IPC-1 · `RemEx-lhd` PAIR-2 · `RemEx-dta` PAIR-3 · `RemEx-n6u` IPC-2 · `RemEx-4ky` PROTO-2 · `RemEx-288` PROTO-3 · `RemEx-e3z` JNI-1 · `RemEx-9m1` JNI-2 · `RemEx-ii3` RD-1 · `RemEx-fs5` RD-3 · `RemEx-bqc` RD-2 · `RemEx-a13` NSD-1

### P1 Beads (required for quality 2.0)

`RemEx-3n6` PAIR-1 · `RemEx-rc4` PAIR-4 · `RemEx-irl` IPC-4 · `RemEx-qg2` IPC-5 · `RemEx-oj8` IPC-6 · `RemEx-4ic` IPC-3 · `RemEx-ngs` NSD-4 · `RemEx-i8x` NSD-5 · `RemEx-kx4` RD-5 · `RemEx-aa0` RD-4 · `RemEx-4uy` PROTO-4 · `RemEx-jny` PROTO-5

### Security Areas — Heightened Caution

When touching any of these files, treat as security-critical and require user sign-off:
- `Remex.Core/Services/Network/RemexNetworkListener.cs` — unauthenticated 8338 command channel (PROTO-1/2); AUTHENTICATED-REMOTE fix in progress (keep `IPAddress.Any`, add PairedClientRegistry token gate)
- `RemEx.Host/Services/IPC/LocalIpcServerService.cs` — Everyone-writable pipe leaking PIN (IPC-1/2/3)
- `RemEx.Host/Services/Security/PairingService.cs` — no PIN throttle, HMAC compares string not bytes (PAIR-2/6)
- `RemEx.Host/Services/Security/CertificateService.cs` — host private key world-readable (PAIR-3)
- `RemEx.Host/Services/Security/PairedClientRegistry.cs` — clientId alone authenticates reconnect (PAIR-1)
- `RemEx.Host/HostBootstrapper.cs` — pairing endpoints open to remote callers (PAIR-5)
- `Remex.Core/Native/JniHelper.cs` + `AndroidNativeExports.cs` — pending JNI exceptions abort JVM (JNI-1/2)

### Cross-Platform Rule for All Orders

Every order touching Windows ACL APIs (`PipeSecurity`, `WindowsIdentity`, `FileSecurity`, SIDs) **must** be guarded with `OperatingSystem.IsWindows()` and include a Linux branch using `UnixFileMode`/`SetUnixFileMode 0600` (owner-only). Validate on Windows **and** CachyOS before closing the bead.

### Design Decisions — Resolved 2026-06-22 (user-confirmed)

- **8338 channel (PROTO-1): AUTHENTICATED-REMOTE.** Keep `IPAddress.Any`; do NOT bind loopback. The host runs as LocalSystem in Session 0 so remote commands + telemetry work with no user logged in — restricting to loopback breaks the product's core purpose. Fix: require a paired-client identity (`PairedClientRegistry` token) before `ExecuteCommandAsync` dispatch.
- **IPC / Session-0 model: confirmed unchanged.** Remote commands and telemetry flow through the Session-0 service directly; they do NOT traverse the named pipe. Pipe ACL orders (IPC-1/IPC-5/IPC-6) govern only the local tray/dashboard UI (interactive user) talking to the service. Fixing the pipe ACL has no bearing on the headless "works without login" remote requirement.
- **`Remex.Client` removal (new bead `RemEx-d8s`): NOT a clean delete.** `Remex.Host` still references `Remex.Client.Services` in `Program.cs`, `StartupRegistrationService.cs`, `SessionKeepUnlockedService.cs`, `DesktopIconExtractionService.cs`. Migrate all still-used types into `RemEx.Host`/`Remex.Core` first, then delete the legacy UI and remove `Remex.Client` + `Remex.Client.Tests` from `Remex.sln`. `RemEx-d8s` subsumes NSD-6 (`RemEx-00x`). Sequence AFTER all P0/P1 security fixes.

### Definition of Done (release)

Every P0 and P1 bead closed via an applied order from `REMEX_2.0_FINAL.md`, with:
- Green build on Windows, Linux (CachyOS via `build-remex.ps1`), and Android (`scripts/android-fresh.ps1`)
- Green tests (`dotnet test Remex.sln`) plus new regression tests named in each order's DoD
- Cross-platform parity verified for every order touching ACL/file-permission/native code
- `CHANGELOG.md` updated under `Security`/`Fixed`/`Changed`
- `protocolVersion` bump coordinated only if a wire-format break is taken (PAIR-1 can avoid it via additive optional fields)

<!-- END AUTO-MANAGED -->

<!-- MANUAL -->
## Custom Notes

Add project-specific notes here. This section is never auto-modified.

<!-- END MANUAL -->

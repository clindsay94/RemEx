# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 🛑 STRICT MCP SERVER ROUTING FOR TOKEN CONSERVATION

To strictly conserve context window tokens and prevent context compaction loops, you MUST utilize the following three MCP servers for all codebase analysis, command execution, and architectural exploration. NEVER read full files, raw logs, or execute un-sandboxed shell commands when these tools are available.

### 1. `token-savior` (Structural Codebase Indexing)
**Do not read raw files to understand codebase logic.** `token-savior` indexes the codebase structurally, cutting token usage by ~97%.
* **Symbol-Level Navigation:** Use `find_symbol`, `get_edit_context`, and `get_function_source` instead of `read_file` or global `grep`. Request isolated logic rather than dumping massive files into the context window.
* **Smart Dependencies:** Use `get_dependencies` and `get_change_impact` to trace relationships. Do not attempt to reverse-engineer imports through sequential, manual file reads.

### 2. `gitnexus` (Graph RAG & Architectural Awareness)
**Do not manually traverse call chains.** `gitnexus` precomputes the repository's knowledge graph, avoiding the multi-step graph exploration that burns excessive tokens.
* **One-Shot Blast Radius:** Use the `impact` tool to instantly analyze upstream and downstream dependencies before modifying code.
* **Process-Grouped Search:** Use `query` to retrieve complete execution flows and functional clusters rather than running brute-force keyword searches across the repository.
* **360° Context:** Use the `context` tool to retrieve a complete map of a single symbol's incoming calls, outgoing dependencies, and process participation in one single turn.

### 3. `context-mode` (Tool Sandboxing & Virtualization)
**Do not run standard shell commands that yield massive outputs.** `context-mode` sandboxes executions and indexes the data externally, yielding up to 98% context savings.
* **Virtualized Execution:** Route potentially noisy scripts or terminal commands through `ctx_execute` or `ctx_execute_file`. Only the stdout summary hits your context, keeping raw logs out. 
* **Write Code, Don't Process Data:** Treat yourself as a code generator, not a data parser. If you need to count, filter, or analyze large numbers of files, write a short script via `ctx_execute` to do it locally instead of loading all the files into context.
* **Indexed Storage for Heavy Data:** For massive test failures, access logs, or browser snapshots, use `ctx_index` or `fetch_and_index`. Store the data in the local SQLite FTS5 database and use `ctx_search` (BM25) to retrieve only the relevant lines you need.

### 4. Bulk Code Generation Workflow (`antigravity` & `agy-splitter`)
When generating large scaffolds, boilerplates, or multi-file modules, DO NOT write the files directly using local LLM output tokens. Instead, delegate the heavy lifting to the Antigravity CLI (`agy`) powered by Gemini.

- **Command Shorthand:** `/bulk-write [generation requirements]` (Maps to `~/.claude/commands/bulk-write.md`)
- **Execution Sequence:** When `/bulk-write` is invoked, you must execute the following via `ctx_execute`:
  1. Trigger generation and pipe to a temporary file: `agy [parameters] > .bulk_raw.txt`
  2. Split the output into valid files: `agy-splitter .bulk_raw.txt`
  3. Automatically delete `.bulk_raw.txt` once file extraction is verified.

### Rules for Bulk Code Generation
- **Strict Delimiters:** You must explicitly instruct Gemini/Antigravity to prefix every new file block with the EXACT string format: `// FILE: path/to/file.ext`. Do not allow markdown headers, backticks, or alternative comment syntax for file paths, or the `agy-splitter` regex will fail.
- **Verify via Diffs:** Do NOT read the generated files back into the primary context window. Use `git diff` to evaluate the generated code.
- **Edit by Exception:** Act purely as a reviewer. Only intervene or edit the resulting files directly if the diff reveals structural hallucinations or logic flaws.

### MCP Tool Decision Matrix

Use this table before reaching for `grep`, `Read`, or raw `Bash`:

| Task | Required Tool | Never Instead |
|------|--------------|---------------|
| Find a symbol / class / method | `token-savior: find_symbol` | `grep` or Read full files |
| Read a function's body only | `token-savior: get_function_source` | Read the whole file |
| What calls this function? | `token-savior: get_dependents` | Manual import tracing |
| What does this function call? | `token-savior: get_dependencies` | Sequential file reads |
| Before editing ANY symbol | `gitnexus: impact` (upstream) | Edit without checking |
| Explore a concept / execution flow | `gitnexus: query` | Keyword grep across repo |
| Full 360° context on one symbol | `gitnexus: context` | Multiple sequential Reads |
| Run command with potentially large output | `context-mode: ctx_execute` | Raw Bash into context |
| Count / filter / aggregate data | `context-mode: ctx_execute` | Load all data into context |
| Generate / rewrite >3 files at once | `agy -p "..."` (see §4 above) | Write each file directly |

**`agy` quick usage for large multi-file changes:**
```bash
agy -p "Generate X. Prefix every file block with // FILE: path/to/file.ext" > .bulk_raw.txt
agy-splitter .bulk_raw.txt   # split into real files on disk
rm .bulk_raw.txt             # clean up
git diff                     # grade — do NOT Read generated files back into context
```
Only intervene via `Edit`/`Write` if the diff reveals hallucinations or logic flaws.


## Project Overview

Remote Execution (RemEx) is a cross-platform PC remote management tool. **Architecture: Android (Client) → PC (Host). The connection is always non-loopback Android-to-PC.** `remex.agent` is the **entire PC side** — Windows Service/daemon plus all PC-side functionality, combining what were formerly separate host and desktop projects. `remex.android` is the Android mobile client and the **only** network client. `Remex.Core` is shared across all targets and is also compiled as a NativeAOT JNI native library (`libRemexCore.so`) for Android.

> **There is no desktop client.** `remex.desktop/` and `remex.desktop/` are legacy folders being phased out — do not add new code there. If you encounter references to a PC-side client connecting to a PC-side host, those are outdated. The PC runs `remex.agent` only. The Android app is the only client.

## Build & Run

```powershell
# Run PC host service (Android connects to this — this IS the entire PC side)
dotnet run --project remex.agent

# NOTE: remex.desktop is a legacy entry point merged into remex.agent. Do not use for new work.

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
remex.core/         Shared models, messages, validation, Guards, serialization
                    ↳ Also compiled as libRemexCore.so (NativeAOT JNI) for Android
remex.agent/         ★ THE PC SIDE — single elevated interactive-session app + all PC functionality
                    ↳ Combines the former host service + desktop UI into ONE process. No Windows
                      Service: it runs in the signed-in user's session, always elevated, auto-started
                      by a Task Scheduler logon task (Windows) or an XDG autostart .desktop (Linux, RemEx-aep.7).
                    ↳ ASP.NET Minimal APIs, WebSocket, mDNS. Android connects TO this.
remex.android/      ★ THE ONLY CLIENT — Kotlin + Jetpack Compose + JNI → libRemexCore.so
                    ↳ Android phone app. Connects to remex.agent on the PC. Nothing else is a client.
remex.desktop/       LEGACY — remnant folder being phased out. Do not add new code here.
```

### Communication Protocols

| Protocol | Endpoint | Port | Purpose |
|---|---|---|---|
| WSS | `/ws` | 5005 | Telemetry, power commands, pairing, file transfer |
| WSS | `/ws/desktop` | 5005 | H.264 / MJPEG remote desktop stream |
| TCP (TLS) | — | 8338 | External script command ingress |

> The former `RemExLocalIPC` / `RemExHostControl` named pipes are **gone** (RemEx-aep). The UI and host
> live in one process, so the UI resolves host services straight from DI via `EmbeddedHostServiceLocator`.

All messages over `/ws` use the `RemexMessage` JSON envelope with `protocolVersion: 2`. Pairing uses ECDH P-256 + 6-digit PIN; clients then pin the host certificate SPKI hash.

### High-Risk Code Areas

The following areas are **security-critical or tightly coupled between `remex.agent` and `remex.android`**. Changes here require explicit user sign-off and must be coordinated across both sides of the connection:

- **Pairing flow** (`PairingHandler`, `PairedClientRegistry`) — ECDH P-256 key exchange and PIN verification. `PairedClientRegistry` is the ONLY authentication path in production (non-loopback). Breakage silently bricks all device pairing with no clear error on either end.
- **Certificate pinning** — Android pins the host's SPKI hash at pairing time. If the host cert changes without a re-pair, the connection is permanently refused until the user re-pairs. Never regenerate or rotate certs silently.
- **`RemexMessage` envelope / `protocolVersion`** — Wire format changes must be backward-compatible or require a `protocolVersion` bump AND a coordinated Android + host release. Mismatched versions cause silent deserialization failures.
- **Elevation + cert ACLs** (`app.manifest`, `CertificateService`, `PairedClientRegistry`) — `remex.agent` MUST start elevated (`requireAdministrator`). An elevated token keeps FullControl over the machine-wide `cert.pfx` / `paired_clients.json` (ACL = LocalSystem + Administrators, inheritance disabled). A non-elevated start gets Administrators as deny-only, fails to read `cert.pfx`, and would brick every SPKI-pinned pairing. Never ship a path that auto-starts non-elevated. `CertificateService` has a brick canary: it logs Critical and refuses to regenerate when an existing `cert.pfx` is unreadable.

### Key Directories in remex.agent

`Views/` and `ViewModels/` follow standard MVVM. `Services/` holds connection, layout, telemetry, and theme services. `Themes/` has the four glassmorphic themes (CyberNOC, Monolith, SolarFlare, BaseDarkGlass). `Localization/` drives live 8-language switching without restart.

## Versioning

- **.NET projects**: centrally managed in `Directory.Build.props` (`<Version>`)
- **Android**: managed in `remex.android/app/version.properties` (`versionName` / `versionCode`)
- `build-remex.ps1` syncs `Directory.Build.props` from `version.properties` automatically

## Coding Conventions

### Async
**Do NOT use `ConfigureAwait(false)` anywhere.** Neither Avalonia nor ASP.NET Core uses `SynchronizationContext`. CA2007 is suppressed in `.editorconfig`. See `docs/ASYNC_GUIDELINES.md`.

### Null Safety
Nullable reference types are enabled in all projects. Use `Guard.NotNull(arg)` (from `remex.core/Guards/Guard.cs`) in constructors for required dependencies. Use `GetRequiredService<T>()` (not `GetService<T>()`) for DI resolution. See `docs/NULL_SAFETY_GUIDELINES.md`.

### Validation
All network-facing input must be validated through the shared validation helpers in `remex.core/Validation/`. See `docs/VALIDATION_GUIDELINES.md`.

### NativeAOT Constraints (`Remex.Core`)

`Remex.Core` is compiled as a NativeAOT JNI library (`libRemexCore.so`) for Android. Code in `Remex.Core` **must be NativeAOT-safe** or the Android build will break at link time — often with no obvious connection to the change you made. Hard rules:

- **No reflection** — `typeof(T).GetMethod(...)`, `Activator.CreateInstance`, `JsonSerializer` with non-source-generated options, etc. are all forbidden.
- **No dynamic code generation** — no `System.Linq.Expressions` compilation, no `Emit`, no runtime type building.
- **Trimming-safe** — use `[DynamicallyAccessedMembers]` and `[RequiresUnreferencedCode]` where necessary. The build has trimming enabled; unannotated reflection silently disappears.
- **Source-generated JSON** — use `[JsonSerializable]` + `JsonSerializerContext` for any new serializable types. Do not use `JsonSerializer.Serialize<T>(obj)` without a source-gen context.
- If you're unsure whether something is NativeAOT-safe, check `Remex.Core` for existing patterns before writing new code.

### Elevated interactive-session app (`remex.agent` on Windows)

`remex.agent` runs **in the signed-in user's interactive session, always elevated (high integrity)** — NOT as a Windows Service and NOT in Session 0. It is auto-started by a Task Scheduler logon task (`scripts/autostart-remex.ps1`, task name `RemEx`, `RunLevel=Highest`, `LogonType=InteractiveToken`) so it starts elevated at sign-in with no UAC prompt. (RemEx-aep.) Implications:

- **Capture + input work directly** — being inside the session, screen capture and `SendInput` reach the user's desktop; HIGH→HIGH UIPI is permitted so input reaches elevated windows. There is no session bridging or `CreateProcessAsUser`.
- **Machine-wide config still uses `HKLM` / `ProgramData`** — `cert.pfx`, `paired_clients.json`, and `CaptureBackendPreference` stay machine-wide so they are stable across logins and protected by the elevated-only ACL (see High-Risk Areas). `HKCU` / `%APPDATA%` are now valid for genuinely user-scoped state, but keep security-sensitive state machine-wide.
- **Elevation is load-bearing, never weaken it** — see the Elevation + cert ACLs high-risk note. A medium-integrity start bricks pairings.
- **No Windows Service, no named pipes** — `LocalIpcServerService`, `RemExLocalIPC`, `HostControlServer/Client`, `AgentCoordinator`, `SessionBridgingCommandService`, and `WindowsActiveSession` were deleted. The UI resolves host services in-process via `EmbeddedHostServiceLocator`.

### Localization

All user-facing strings in `remex.agent` (UI labels, tooltips, error messages, notifications) **must** go through the localization system in `Localization/`. The app supports 8 languages with live switching — hardcoded English strings are a regression. Rules:

- Add new strings to the appropriate `.resx` / localization file, not inline in code or XAML.
- Never use `string.Format` or interpolation directly in UI-bound properties; use localized format strings.
- If a string is purely internal (logs, exception messages, developer-facing), it may stay in English without localization.

### Protocol Versioning

`RemexMessage` carries `protocolVersion: 2`. If you make a breaking change to the wire format:
1. Bump `protocolVersion` in both `remex.agent` and `remex.android`.
2. Coordinate the release — a version mismatch between host and Android causes silent deserialization failures, not clean errors.
3. Non-breaking additions (new optional fields) do not require a bump, but document them in CHANGELOG.md.

## Android Prerequisites

- Android SDK API Level 37 platform required
- NDK version **30.0.14904198** required for NativeAOT JNI compilation
- `build-remex.ps1` auto-installs both via `sdkmanager` if absent
- Set `ANDROID_HOME` or configure `remex.android/local.properties` (`sdk.dir=...`)

## Host Diagnostics

On Linux, run `dotnet run --project remex.agent -- --doctor` to check PipeWire/X11/VAAPI prerequisites.

## Cross-Platform Parity (Windows ↔ CachyOS/Linux)

This repo lives on a shared drive and must work equally on **Windows** and **CachyOS/Linux**. Any change that touches the PC side (remex.agent, scripts, installers, build tooling) **must maintain parity**:

- Every `.ps1` script must work under `pwsh` on Linux **or** have a `.sh` equivalent that does the same thing.
- Never hardcode Windows-only paths. Use path helpers or environment variables.
- `build-remex.ps1` is the canonical cross-platform build entry point. New build steps must be added for both platforms.
- Before closing a task: verify the change works on both platforms, or explicitly note which OS was tested and file a follow-up beads issue for the other.

## Code Quality Standards

**No lazy code.** Every implementation must be the most correct, robust, and maintainable approach for the task. Rules:

- Use `gitnexus: query` and `token-savior` to understand existing patterns **before** writing new code. Match the codebase's conventions.
- No placeholder implementations, stub methods, `TODO:` bodies, or "good enough for now" code. If a full implementation is out of scope, file a beads issue and implement what IS in scope correctly.
- Prefer correctness over speed-to-write. If there's a real tradeoff, explain it.
- Use existing infrastructure before rolling new ones: `remex.core/Guards`, `remex.core/Validation`, `GetRequiredService<T>()`, etc.

## Documentation & CHANGELOG Maintenance

**Update docs on every change.** No task is complete until:

1. **CHANGELOG.md** has an entry under the correct version heading (Keep a Changelog format: `Added`, `Changed`, `Fixed`, `Removed`, `Security`).
2. Affected XML doc comments, README sections, or `docs/` guideline files are updated.
3. `AGENTS.md` / `CLAUDE.md` are updated if project structure, tooling, or conventions changed.
4. `Directory.Build.props` and `remex.android/app/version.properties` are bumped if the change warrants a version increment.

## User Experience Standards

The target user **may not be technical**. Every user-facing element must be:

- **Plain English** — no jargon, no abbreviations, no assumed knowledge in scripts, installers, UI tooltips, or error messages.
- **Hand-holdy** — scripts print friendly status messages and tell the user exactly what to do when something fails. Always provide a "what to do next" step.
- **Consistent** — `build-remex.ps1` is the canonical entry point for all major build/install operations. All major operations should be accessible from it, not buried in sub-scripts.
- **Theme-safe** — any UI change must be verified across all four themes: CyberNOC, Monolith, SolarFlare, BaseDarkGlass. Each has distinct contrast ratios and background treatments; a change that looks fine on one can break another.

## Beads Issue Tracking (`bd`)

Beads is the task tracker for this repo. It replaces TODO lists, markdown task files, and ad-hoc notes entirely.

**Mandatory workflow:**
1. `bd create` — file an issue **before** writing any code
2. `bd update <id> --claim` — claim it when you start
3. `bd close <id>` — close it when done (before reporting complete)

**Key commands:**
```bash
bd ready                           # find unblocked work
bd show <id>                       # see full issue + dependencies
bd create --title="..." --description="..." --type=task|bug|feature --priority=0-4
bd remember "insight"              # persist cross-session knowledge
bd dolt push                       # sync issues to remote (part of session close)
```

**Rules:**
- NEVER use TodoWrite, TaskCreate, or markdown TODO lists.
- NEVER say "done" without running `bd close` on completed issues.
- Priority scale: 0=critical, 1=high, 2=medium, 3=low, 4=backlog.

<!-- agent-team:start -->
## Agent Team & Communication

See global instructions: `~/.claude/CLAUDE.md`

**Project-level coordination for RemEx 2.0:**
- Root mission control: `AGENTS.md` in this repo — project rules, architecture, phase gates
- Sub-project playbooks: `remex.core/AGENTS.md`, `remex.agent/AGENTS.md`, etc.

Read the relevant sub-project `AGENTS.md` before touching files in that directory.
<!-- agent-team:end -->

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **RemEx** (11498 symbols, 24534 relationships, 300 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

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


<!-- BEGIN BEADS INTEGRATION v:1 profile:minimal hash:ccf33ec3 -->
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

## Session Completion

**When ending a work session**, you MUST complete ALL steps below. Work is NOT complete until `git push` succeeds.

**MANDATORY WORKFLOW:**

1. **File issues for remaining work** - Create issues for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Update issue status** - Close finished work, update in-progress items
4. **PUSH TO REMOTE** - This is MANDATORY:
   ```bash
   git pull --rebase
   bd dolt push
   git push
   git status  # MUST show "up to date with origin"
   ```
5. **Clean up** - Clear stashes, prune remote branches
6. **Verify** - All changes committed AND pushed
7. **Hand off** - Provide context for next session

**CRITICAL RULES:**
- Work is NOT complete until `git push` succeeds
- NEVER stop before pushing - that leaves work stranded locally
- NEVER say "ready to push when you are" - YOU must push
- If push fails, resolve and retry until it succeeds
<!-- END BEADS INTEGRATION -->

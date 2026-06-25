## RemEx Architecture & Project Rules

These rules apply to ALL agents working in this repository. They are not overridable by individual session context.

### Host = PC, Client = Android. End of Story.

- `remex.agent` is the **entire PC side** — Windows Service/daemon plus all PC functionality. Android connects TO this.
- `remex.android` is the **only** network client. Nothing else is a client.
- `remex.desktop/` and `remex.desktop/` are legacy folders being phased out. Do NOT add new code there.
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
  - Windows publish: `artifacts/publish/remex.agent/{Config}_win-x64/`
- Final distributables: `build_output/windows/`, `build_output/android/`, `build_output/linux/`

**Android prerequisites** (auto-installed by the script if missing):
- Android SDK API Level 37
- NDK version `30.0.14904198`
- Requires `ANDROID_HOME` env var or `remex.android/local.properties` (`sdk.dir=...`)

**Version sync:** script reads `versionName` from `remex.android/app/version.properties` and patches `Directory.Build.props` automatically on every run.

**Android Gradle tasks (direct):**
- `./gradlew remexFreshAssembleDebug` / `remexFreshAssembleRelease` — build without bumping version.
- `./gradlew remexPublishRelease` — bumps patch version (`versionCode+1`, minor+1, patch→0) and writes back to `version.properties` before building. Use only for actual releases.
- Signing: reads `remex.signing.*` keys from `local.properties`; falls back to debug signing if absent (safe for local test builds).
- `libRemexCore.so` is resolved from `artifacts/bin/remex.core/<config>_net10.0-android_android-arm64/native/` (UseArtifactsOutput layout) with fallback to legacy `bin/`. APK output named `RemEx-V${versionName}-${variant}.apk`.
<!-- END AUTO-MANAGED -->

<!-- AUTO-MANAGED: active-loops -->
### Remote Desktop Frame Fix — CLOSED

**Root cause (resolved):** `RemoteDesktopHandler` formerly counted any non-empty pixel buffer as a captured frame. `DxgiDesktopCapture` returned its cached `_lastFrame`/`_lastRawFrame` indistinguishably on `DXGI_ERROR_ACCESS_LOST`; `DuplicationReinitThrottle` could replay a stale buffer for up to 8 s with `consecutiveFailures` never incrementing — client showed a frozen frame under a live cursor.

**Fixes landed:**
- **RemEx-hmj (CLOSED):** Deferred init — `DxgiDesktopCapture` no longer calls `DuplicateOutput` in its constructor; the slot is opened on first actual capture via `EnsureInitialized()`. Eliminates idle-slot-hold contention with Windows RDP.
- **STALE_CACHE_ON_ACCESS_LOST (CLOSED):** `IScreenCaptureService` now returns `ScreenCaptureResult { Pixels, IsLive }`. `RemoteDesktopHandler` only resets `consecutiveFailures` on `IsLive = true`; stale replays now propagate to the coded-error path. `FakeScreenCaptureService` covers the `IsLive = false` path in `RemoteDesktopHandlerTests`.
- **RemEx-960 (CLOSED):** Display-power pause — `WindowsDisplayPowerMonitor` (sealed, `[SupportedOSPlatform("windows")]`) hosts a message-only HWND on a dedicated background thread (`RemEx-DisplayPowerMonitor`) and registers `GUID_CONSOLE_DISPLAY_STATE`. `WindowsScreenCaptureService` forwards `IScreenCaptureService.IsDisplayPoweredOff` from it; the capture loop skips DXGI polling while the monitor is asleep, driving re-init attempts to zero during display-off. `WindowsDisplayPowerMonitor` fails open: if the window/notification cannot be created, `IsDisplayOff` stays `false` and capture behaves exactly as before.

### WGC Capture Backend — CLOSED (RemEx-g6r)

**What landed:** Windows screen capture now uses a **WGC → DXGI → GDI** backend ladder. Windows.Graphics.Capture (WGC) is the preferred per-monitor path; DXGI Desktop Duplication is the fallback; GDI is the last resort. This targets the recurring DXGI driver-wedge / black-screen / cursor-only freeze bug class (`RemEx-crk`, `DXGI_ERROR_ACCESS_LOST`, `RemEx-ltd`) without any change to the wire protocol, H.264 encoder, Android client, or pairing.

**New project:** `remex.agent.windows` (TFM `net10.0-windows10.0.19041.0`) — isolates all WinRT code so the Linux build is completely unaffected. Referenced from `remex.agent.csproj` only under the Windows condition, behind the `WGC_CAPTURE` compile constant. Do NOT add Linux or cross-platform code to this project.

**Key components:**
- `WgcDesktopCapture` — WGC implementation (`GraphicsCaptureItem` + `Direct3D11CaptureFramePool`). Captures cursor-less (`IsCursorCaptureEnabled = false`); yellow border suppressed (`IsBorderRequired = false` via `IGraphicsCaptureSession3`, no-ops cleanly on older Windows). Implements `IWgcCaptureSource`.
- `IWgcCaptureSource` — new interface in `remex.core/Services/` defining the WGC contract; keeps WinRT out of Core itself.
- `CaptureBackendPreference` — reads `HKLM\SOFTWARE\RemEx\Capture\Backend`; parses `Auto` (default) | `Wgc` | `Dxgi` | `Gdi`; fails open to `Auto` on any bad/missing value. Registered in DI via `HostBootstrapper` (Windows only). New operator knobs for capture must go here, not hardcoded.
- `CaptureScaling` — **moved from `remex.agent` to `Remex.Core`** so both `remex.agent` and `remex.agent.windows` share the encoder-dimension formula. Do not move it back or duplicate it.
- `WindowsScreenCaptureService` — drives the WGC → DXGI → GDI ladder using `CaptureBackendPreference`.

**Testing seam:** WGC and DXGI both require an interactive session + GPU and are not Session 0 testable. CI coverage is at the `IScreenCaptureService` seam (`FakeScreenCaptureService`) and the HKLM preference parse (`CaptureBackendPreferenceTests`). The real backend matrix requires manual interactive validation.

**Agent rules (carry forward to all work in this area):**
- `DxgiDesktopCapture` is `sealed` + `[SupportedOSPlatform("windows")]` + P/Invokes GPU — uninstantiable headless/in Session 0. Tests must use `FakeScreenCaptureService`, not the live class.
- `WgcDesktopCapture` is likewise `[SupportedOSPlatform("windows")]` and requires WinRT + GPU — never instantiate in tests; always use `FakeScreenCaptureService`.
- `WindowsDisplayPowerMonitor` is also `[SupportedOSPlatform("windows")]` and requires a real message pump — never instantiate it in tests. The `IScreenCaptureService.IsDisplayPoweredOff` property defaults to `false`; fakes and Linux backends inherit that default automatically.
- `remex.agent.windows` must stay Windows-only. Never add cross-platform or Linux code there.
- `CaptureScaling` lives in `Remex.Core` (NativeAOT-safe). Do not move it back to `remex.agent` or create a duplicate.
- `CaptureBackendPreference` is the single source of truth for operator capture knobs. New per-backend toggles go there.
- Path casing is load-bearing on Linux: source dir is `remex.agent` (capital E-x); test projects are `remex.agent.tests` / `remex.core.tests` (lower e-x); solution is `Remex.sln`. Wrong casing passes on Windows, breaks on CachyOS.
- Pre-existing test failures in `PairedClientRegistry`/`RemoteDesktopAuth`/`PairingHandler` (issue `RemEx-jgw`) are filtered from gates; gates assert a minimum test count so a zero-match filter cannot show green.
- **Test host safe doubles (RemEx-21g):** `RemexHostFactory` default-registers three safe doubles FIRST in DI: `FakeScreenCaptureService` (pure managed, no DXGI/D3D/GDI), `NoOpInteractiveSessionGuard` (no `tscon` call), `NoOpSystemCommandService` (no lock/reboot/shutdown). Defined in `SafeHostTestDoubles.cs`. No integration test touches the GPU, locks the session, or runs a power command.
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

No lazy code. Use the most correct, robust, maintainable approach. No stub methods, `TODO:` bodies, or "good enough for now" placeholders. Check existing infrastructure (`remex.core/Guards`, `remex.core/Validation`) before writing new utilities.

### Docs & CHANGELOG on Every Change

Every code change must also update `CHANGELOG.md` (Keep a Changelog format). Update affected XML doc comments, `docs/` files, and version numbers when warranted. A task is not complete until docs are updated.

### Beads for Task Tracking

Use `bd` for ALL task tracking. Create an issue before writing code. Claim it. Close it when done. Never use TodoWrite, TaskCreate, or markdown TODO lists. Run `bd prime` for the full workflow context.

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **RemEx** (9878 symbols, 17414 relationships, 300 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

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

Remote Execution (RemEx) is a cross-platform PC remote management tool. Architecture is **Android (client) → PC (host)**, always non-loopback. `remex.agent` is the entire PC side (Windows Service / Linux daemon plus all PC functionality). `remex.android` is the only network client. `Remex.Core` is shared across all targets and is also compiled as a NativeAOT JNI native library (`libRemexCore.so`) for Android.

<!-- END AUTO-MANAGED -->

<!-- AUTO-MANAGED: build-commands -->
## Build & Development Commands

- `dotnet run --project remex.agent` — run the PC host service (Android connects to this).
- `dotnet test Remex.sln` — run all tests.
- `pwsh ./build-remex.ps1 -c release -t all` — unified cross-platform release build (canonical entry point).
- `.\scripts\android-fresh.ps1 -Configuration Release` — hardened fresh Android build.
- `./installer/build-linux.sh` — build Linux packages (uses WSL on Windows).
- `dotnet run --project remex.agent -- --doctor` — check Linux PipeWire/X11/VAAPI prerequisites.

<!-- END AUTO-MANAGED -->

<!-- AUTO-MANAGED: architecture -->
## Architecture

- `remex.core/` — shared models, messages, validation, Guards, serialization; also compiled as `libRemexCore.so` (NativeAOT JNI) for Android. Must stay NativeAOT-safe.
  - `Validation/CoordinateValidation` — `ClampAbsolute(float, int)` / `ClampDelta(float, int)`: sanitize untrusted float coordinates from remote clients before pixel cast (rejects NaN/±Infinity).
  - `Native/RemexDesktopClient` — singleton (`RemexDesktopClient.Current`) WebSocket client for `/ws/desktop`; consumed by `remex.desktop` (legacy). Lives in Core and is NativeAOT-compiled — must remain NativeAOT-safe. Events: `FrameReceived`, `CursorShapeReceived`, `Disconnected`.
- `remex.agent/` — the PC side: Windows Service / Linux daemon, ASP.NET Minimal APIs, WebSocket, mDNS. Runs as LocalSystem in Session 0 on Windows.
  - `AgentCoordinator` (root, `IHostedService`) — orchestrates the command-agent web host lifecycle; polls for canonical port availability (500ms / 30s, TIME_WAIT clearance) before reclaim to prevent `CreateApplication`'s port-fallback loop from silently binding port+1; yields/reclaims the web host to/from the GUI host via `HostControlServer`; injectable test seam (port-checker `Func<int,bool>`, app factory `Func<WebApplication>`, poll interval, pipe name).
  - `Handlers/` — `PairingHandler` (ECDH P-256 handshake), `RemoteDesktopHandler` (codec negotiation, H.264/MJPEG streaming; default 120 FPS target (`MaxTargetFps = 120`, `_targetFps = 120`); takes `IInteractiveSessionGuard`; per-session `sessionGuardClientId`; streams `desktop_cursor_shape`/`desktop_cursor_state`; enforces `StreamSerial` on send; throttles keyframe reinits to 5s cooldown; static `_ffmpegAvailableCache` probed once at construction), `PingPongHandler` (keep-alive).
  - `Services/Security/` — `PairingService` (ECDH state machine), `PairedClientRegistry` (proof-of-possession reconnect auth), `PairingThrottle` (per-IP rate limiting), `CertificateService` (SPKI management), `TransportTrust` (network-path trust classifier: loopback / Tailscale / LAN — gates PIN auto-fetch).
  - `Services/Network/` — `PairedClientChannelAuthenticator` (8338 TCP channel auth gate via `PairedClientRegistry`).
  - `Services/IPC/` — `LocalIpcServerService` (`RemExLocalIPC` pipe, privileged-action gate), `HostControlServer` (`RemExHostControl` pipe, headless agent ↔ GUI port-handoff coordination).
  - `Services/Session/` — `IInteractiveSessionGuard` / `WindowsInteractiveSessionGuard` (ref-counted engage/disengage; `tscon` console-reconnect; `[SupportedOSPlatform("windows")]`); `ISessionKeepUnlockedService` (writes/reads `ProgramData\RemEx\keep-session-unlocked.flag`; wired to in-app Settings toggle, Windows-only, off by default, with localized security warning); `SessionGuardPolicy` (unit-tested pure decision logic: `Reconnect` vs `None`).
  - `Services/RemoteDesktop/` — `IH264Encoder` / `FFmpegH264Encoder` (FFmpeg subprocess, bounded channels, on-demand keyframe).
  - `Services/ScreenCapture/` — `DxgiDesktopCapture` (DXGI Desktop Duplication API, Windows 10/11, MPO/GPU-composited content; deferred init via `EnsureInitialized()` on first capture — no idle-slot hold at construction); `DuplicationReinitThrottle` (exponential-backoff gate for `DuplicateOutput` re-init; prevents driver-wedge on display power transitions); `WindowsScreenCaptureService` wraps captures as `ScreenCaptureResult { Pixels, IsLive }` — GDI path always `IsLive = true`; DXGI path sets `IsLive = false` on stale `_lastFrame` replays. `IScreenCaptureService` (in `Remex.Core`) defines `ScreenCaptureResult` and the capture contract.
- `remex.android/` — the only client: Kotlin + Jetpack Compose + JNI → `libRemexCore.so`.
  - `data/NsdDiscoveryManager` — mDNS discovery (`_remex._tcp.`); API 34+ uses concurrent `registerServiceInfoCallback`, pre-34 serialises via process-wide `resolveMutex`.
  - `security/PinnedHostStore` — Tink AES-256-GCM AEAD encrypted storage for paired host SPKI hashes and PAIR-1 reconnect secrets; two DataStores (`remex_pinned_hosts`, `remex_reconnect_secrets`); Android Keystore-backed keyset; corruption self-recovery.
  - `ui/screens/H264StreamDecoder` — `MediaCodec` async hardware decoder; bounded backlog (4 frames), `onKeyframeNeeded` / `onInitFailure` callbacks.
  - `ui/screens/RemoteDesktopViewModel` — stream config, display-target selection, cursor shape overlay, frame-arrival watchdog.
  - `ui/screens/RemoteDesktopScreen` — Jetpack Compose UI, gesture handling (tap/scroll/pinch), immersive full-screen.
  - `ui/screens/ConnectionViewModel` — NSD discovery lifecycle; `discoveryJob: Job?` ensures one in-flight discovery at a time.
- `remex.desktop/`, `remex.desktop/` — legacy, being phased out; do not add new code.

Protocols: WSS `/ws` (port 5005, telemetry/power/pairing/file transfer), WSS `/ws/desktop` (port 5005, H.264/MJPEG remote desktop), TCP+TLS 8338 (external script ingress — requires paired `clientId` via `PairedClientChannelAuthenticator`; `CommandRequest` JSON must include `ClientId` field), Named Pipe `RemExLocalIPC` (local service IPC), Named Pipe `RemExHostControl` (agent↔GUI port handoff). Messages use the `RemexMessage` JSON envelope with `protocolVersion: 2`. Pairing uses ECDH P-256 + 6-digit PIN, then SPKI certificate pinning. Wire message types include `MessageTypes.DesktopKeyframeRequest` (`"desktop_keyframe_request"`) for client-to-host on-demand IDR keyframe requests.

<!-- END AUTO-MANAGED -->

<!-- AUTO-MANAGED: conventions -->
## Code Conventions

- Do NOT use `ConfigureAwait(false)` anywhere (CA2007 suppressed).
- Nullable reference types enabled everywhere; use `Guard.NotNull(arg)` and `GetRequiredService<T>()`.
- Validate all network-facing input via `remex.core/Validation/`.
- `Remex.Core` must be NativeAOT-safe: no reflection, no dynamic codegen, source-generated JSON only.
- On Windows, `remex.agent` runs as LocalSystem in Session 0: never use `HKCU`/`%APPDATA%`; use `HKLM` and `ProgramData`; keep correct Named Pipe ACLs.
- All user-facing strings in `remex.agent` go through `Localization/` (8 languages, live switching).
- Versions: .NET in `Directory.Build.props`; Android in `remex.android/app/version.properties`.

<!-- END AUTO-MANAGED -->

<!-- AUTO-MANAGED: patterns -->
## Detected Patterns

- MVVM in `remex.agent` (`Views/`, `ViewModels/`, `Services/`); four glassmorphic themes (CyberNOC, Monolith, SolarFlare, BaseDarkGlass) — verify UI changes across all four.
- Cross-platform parity (Windows ↔ CachyOS/Linux) required for every PC-side change; each `.ps1` needs a `pwsh`-compatible path or `.sh` equivalent.
- Every change updates `CHANGELOG.md` (Keep a Changelog) and affected docs.
- **Proof-of-possession reconnect auth**: `PairedClientRegistry` stores a 32-byte ECDH/HKDF session key per client; reconnect auth is HMAC-over-nonce challenge, NOT bare clientId lookup. `RegisterClient(string, byte[])` is the production path.
- **Bounded channel drop in H.264 pipeline**: `FFmpegH264Encoder` uses bounded `Channel<T>` (drop-newest for input, drop-oldest for output); `H264StreamDecoder` uses a bounded backlog (4 frames, drop-oldest). On overflow both fire a keyframe-needed callback to recover stream sync rather than accumulating stale frames.
- **On-demand keyframe recovery**: `IH264Encoder.RequestKeyframe()` / `ConsumeKeyframeRequest()` atomic flag consumed by the capture loop; `H264StreamDecoder.onKeyframeNeeded` callback on Android. Both ends coordinate to recover from decoder desync without waiting a full GOP.
- **`IInteractiveSessionGuard` check before streaming**: `RemoteDesktopHandler` checks `IHostCapabilitiesProvider.SupportsRemoteDesktop` and the session guard before starting a desktop stream; sends structured `DesktopErrorCodes` on failure (not generic WebSocket close).
- **`WindowsInteractiveSessionGuard` ref-count + session ownership model**: `EngageForRemoteControl(clientId)` / `DisengageFromRemoteControl(clientId)` maintain an `_engaged` HashSet; the first engage / last disengage triggers action. `_heldSessionId` is set ONLY when the guard itself performs a `tscon` console-reconnect — sessions that were already usable are left untouched on both engage and disengage so the guard never locks a session it did not unlock. `SessionGuardPolicy.Decide(snapshot)` returns `SessionGuardAction.Reconnect` or `None`; the policy refuses to act on the host's own session (own `ProcessSessionId != 0` check) to avoid bricking the interactive desktop. Every unlock and re-lock is audit-logged with the client identity. **Security-sensitive**: while engaged the PC is unlocked without a password — feature is off by default; enabled via `ProgramData\RemEx\keep-session-unlocked.flag` containing `1`, written by `ISessionKeepUnlockedService` (in-app toggle shows a localized security warning). `[SupportedOSPlatform("windows")]`; test double is `NoOpInteractiveSessionGuard`.
- **`EvaluateDesktopAuth` pre-auth for `/ws/desktop`**: `HostBootstrapper.EvaluateDesktopAuth` enforces: loopback → allow unconditionally; non-loopback → must have paired `clientId` (PairedClientRegistry) AND `protocolVersion >= 2`. Unknown or missing clientId → 401/403; old protocol → 400; newer-than-host → 200 (forward compat).
- **`PairingThrottle` per-IP rate limiting**: singleton sliding-window throttle applied to `/start-pairing` and `pairing_complete`. Loopback callers bypass (local UI is trusted). Cryptographic jitter on retry hint. `PairingService` additionally caps failed HMAC attempts at 5 per session with a ~120s session timeout.
- **`NsdDiscoveryManager` API-level strategy**: API 34+ uses concurrent, cancellable `registerServiceInfoCallback`; pre-34 serialises resolves process-wide via a `Mutex` (NsdManager pre-34 allows only one in-flight resolve). Always acquires a `WifiManager.MulticastLock` for mDNS reliability.
- **Frame-arrival watchdog in `RemoteDesktopViewModel`**: arms on stream start, resets on every decoded frame, triggers reconnect if no frame arrives within stall timeout. Backstops H.264 decoder-init silent-death path.
- **`CoordinateValidation` float sanitization**: All absolute pointer coordinates use `CoordinateValidation.ClampAbsolute(float, int)` and all relative deltas use `CoordinateValidation.ClampDelta(float, int)` before casting to `int`. Rejects NaN/±Infinity; clamps to valid pixel bounds. Regression tests in `remex.core.tests/CoordinateValidationTests.cs` (RD-8).
- **`AndroidNativeExports` dual-lock model**: `PairingSyncRoot` (separate from the high-frequency `SyncRoot`) serializes pairing-session state transitions so a concurrent `StartPairing`/`SubmitPin` call from a second Java thread waits rather than disposing-then-using the active `ClientWebSocket` (JNI-4). JNI string marshalling (`ReadJString`) happens inside the `Export` guard so managed throws are caught before escaping `[UnmanagedCallersOnly]` (JNI-5).
- **`MdnsDiscoveryService` SRV validation**: Before composing the `ws://` URL from untrusted multicast data, validates SRV port >= 1 and resolved host passes `Uri.CheckHostName != Unknown` (NSD-6).
- **`RemExLocalIPC` ACL error surfacing**: `UnauthorizedAccessException` on pipe open returns a distinct "Permission denied" `CommandResponse` rather than collapsing into the generic `IPC Error` path, giving users an actionable message (IPC-8).
- **`ConnectionViewModel` single in-flight discovery**: `discoveryJob: Job?` tracks the active NSD coroutine; `startDiscovery()` cancels any prior job before launching so overlapping manual + self-heal calls do not stack NSD resolves or multicast-lock cycles (RemEx-4bb).
- **`SyncRemexCoreSoTask` ELF verification**: Content-tracks `sourceCandidates` as Gradle inputs (prevents stale `.so` on `-NoClean` builds) and validates the `.so` is AArch64 ELF (magic `0x7F454C46` + `EI_CLASS=2` + `e_machine=0xB7`) before copying into the APK (RemEx-l79 / RemEx-hht).
- **`DuplicationReinitThrottle` DXGI re-init throttle**: `DxgiDesktopCapture` gates all `TryReinitializeDuplication` / `DuplicateOutput` calls through `DuplicationReinitThrottle` (backoff: 1s base, 8s max, exponential escalation). On `DXGI_ERROR_ACCESS_LOST`, at most one re-init attempt is made per backoff window; confirmed-healthy frames (real frame or `WAIT_TIMEOUT`) call `RecordHealthyFrame()` to reset. Prevents the "display-off storm" that wedged DWM + NVIDIA driver at stream frame rate (RemEx-crk). Clock-injected for deterministic unit tests.
- **`ScreenCaptureResult.IsLive` stale-replay signal (RemEx-hmj / STALE_CACHE_ON_ACCESS_LOST — CLOSED)**: `IScreenCaptureService` capture methods return `ScreenCaptureResult { Pixels, IsLive }`. `IsLive = true` means a fresh real frame was produced; `IsLive = false` means the cached `_lastFrame` was replayed (e.g. on `DXGI_ERROR_ACCESS_LOST` during the `DuplicationReinitThrottle` backoff window). `RemoteDesktopHandler` only resets `consecutiveFailures` on `IsLive = true`; stale replays now propagate to the coded-error path. `DxgiDesktopCapture` deferred init (`EnsureInitialized()` on first capture, not in constructor) eliminates the idle-slot-hold that blocked Windows RDP when RemEx was idle. GDI fallback path always returns `IsLive = true` (never cached). Tested via `FakeScreenCaptureService` in `RemoteDesktopHandlerTests`.
- **`PinnedHostStore` reconnect-secret persistence (PAIR-1/RemEx-xuo)**: After a successful Android pairing, `RemexClientManager` extracts the `reconnectSecret` from the `OK:hostId|spki|reconnectSecret` result and calls `PinnedHostStore.setReconnectSecret(context, hostId, ...)` + `setReconnectSecret(context, host, ...)`. On reconnect, `getReconnectSecret()` supplies the secret to `RemexCoreClient` to answer the host's proof-of-possession challenge; without a stored secret, the host rejects the reconnect and forces a re-pair. Secrets live in a dedicated DataStore (`remex_reconnect_secrets`, separate from `remex_pinned_hosts`) encrypted via Tink AES-256-GCM AEAD with `hostId` as associated data.
- **`TransportTrust` PIN auto-fetch gate**: Host-side `TransportTrust.IsTrustedForPinAutoFetch(remote, local)` and Android-side `TransportTrust.canAutoFetchPin(context, host)` must agree for PIN auto-fill to work end-to-end. Host allows PIN auto-fetch when caller is loopback OR both remote and local addresses are Tailscale CGNAT (`100.64.0.0/10` / `fd7a:115c:a1e0::/48`) — requiring both ends defeats a LAN attacker spoofing a `100.64.x.x` source. Android allows PIN auto-fetch for loopback OR (Tailscale address / `*.ts.net` MagicDNS hostname) AND `TRANSPORT_VPN` active — VPN-active check is mandatory; a Tailscale-looking address with no live tunnel must NOT unlock auto-fetch. Host handles IPv4-mapped addresses (`::ffff:100.64.x.x`) for Kestrel. Android `requiresLocalNetworkAccess(host)` returns `false` for loopback/Tailscale/`*.ts.net` targets, gating `NEARBY_WIFI_DEVICES`/`ACCESS_LOCAL_NETWORK` runtime permission requests — changes here can silently break Tailscale users (spurious permission prompts) or open LAN permission gates. Both sides are security-critical and must be kept in sync; changes require explicit user sign-off.
- **Interactive-host IPC/pairing fallback (RemEx-dqj/RemEx-sgj)**: When `remex.agent` runs as the signed-in user (not LocalSystem), `LocalIpcServerService` falls back to `TryGetSelfAsActiveConsoleUserSid` — grants privileged commands only when the host process is itself in the active console session as a real (non-system) logon. `PairedClientRegistry.RestrictStorePermissionsWindows` grants full control to the current user (not only LocalSystem + Administrators) so `SetAccessControl` succeeds without `SeRestorePrivilege`. Both paths are identity-gated; a host in a non-console session (RDP, fast-user-switch, Session 0) still cannot authorize another session's identity.
- **`isMulticastReachableHost` mDNS guard (RemEx-fkz)**: `RemexClientManager` gates self-healing mDNS discovery behind `isMulticastReachableHost(host)`, which returns `false` for Tailscale/CGNAT (100.64.0.0/10) and public IPs. Prevents spamming Android's local-network permission prompt when the saved host is a VPN or public address. Private LAN (10.x, 172.16–31.x, 192.168.x), link-local (169.254.x), and non-IP hostnames all pass as multicast-reachable.
- **`PinnedHostStore` Tink AEAD corruption recovery**: `aead()` uses a double-checked lock; on init failure (lock-screen key invalidation, app-data cleared with Keystore intact, etc.) it clears the `remex_tink_prefs` SharedPreferences keyset, clears both DataStores, and retries — preventing a permanently bricked app. Keyset is Android Keystore-backed; no deprecated `EncryptedSharedPreferences` or `MasterKey` APIs.
- **`StreamSerial` stale frame guard (RemEx-gim)**: `RemoteDesktopHandler` send loop drops any buffered frame whose `StreamSerial` no longer matches the session's current serial (host-authoritative), closing the race window where the capture thread could swap an old-serial frame in just after the buffer was cleared on a target switch.
- **Keyframe throttle cooldown**: `RemoteDesktopHandler` throttles keyframe-driven encoder reinits to at most one per 5s. The first request (or the first after the cooldown expires) triggers a real reinit + SPS/PPS+IDR; requests arriving inside the cooldown are swallowed (the decoder re-requests if still desynced). Any legitimate rebuild (target switch, quality/fps/scale change) also satisfies the cooldown. Stream Metrics log reports `Throttled keyframe reinits: N` so a flood is visible at a glance.
- **`ClipCursor` host cursor confinement (Windows, RemEx)**: While a single monitor is being streamed, `WindowsInputSimulationService` confines the host cursor to the streamed display via Win32 `ClipCursor`, re-applied on each cursor-shape tick (~10 Hz, because Windows releases the clip on display/desktop/foreground changes). Released when streaming stops, cancels, or the client disconnects. No-op when streaming the full virtual desktop and on Linux.
- **Native cursor shape streaming**: Host streams `desktop_cursor_shape` (BGRA bitmap + hotspot, sent on change) and `desktop_cursor_state` (position/visibility, ~60 Hz). `_drawCursor` is gated off in `RemoteDesktopHandler` when `SupportsCursorShape` is true, so host-side compositing is disabled. Requires JNI callbacks `onDesktopCursorState`/`onDesktopCursorShape` across the NativeAOT boundary. Legacy hosts/clients fall back to generic-arrow overlay.
- **`RemoteDesktopFrameEnvelope` per-frame codec routing (RemEx-w5v)**: Client opts in via `supportsFrameEnvelope`; host prepends a small `RDXF` header to each frame tagging codec + stream serial. Android routes each frame to the decoder that produced it (by the per-frame tag) rather than a separately-updated "active codec" flag that could lag a frame behind, eliminating the silent black gap on codec switch. Legacy hosts sending untagged frames fall back to negotiated-codec routing.
- **`PanFollowCalculator` zoom pan-follow (`RemoteDesktopScreen`)**: When zoomed past 1×, the host streams cursor position at ~60 Hz; the picture glides to keep the host cursor on-screen using edge-deadzone tracking (mirrors Windows App behavior). The remote-desktop screen no longer forces landscape — it rotates with the device. Cursor visibility is gated by the `hostCursorVisible` flag (not a sentinel coordinate); negative coordinates from monitors at a negative virtual-desktop origin are treated as valid.
- **Hybrid precision frame pacing (`RemoteDesktopHandler`)**: The capture loop coarse-sleeps via `Task.Delay` for the bulk of each frame interval, then busy-spins with `Thread.SpinWait` for the final few milliseconds, achieving the target 120 FPS without a global timer resolution change (`timeBeginPeriod`). Fully localized to the capture loop; benefits Linux pacing as well.
- **`RemoteDesktopHandler` static FFmpeg availability cache**: `_ffmpegAvailableCache` (static `bool?`) is probed once at handler construction — not per-stream-start — behind `_ffmpegCacheLock`. Avoids repeated process-spawn checks; the cache persists for the process lifetime.
- **`DesktopErrorCodes` localized error surface (RemEx-728)**: Host tags `errorText` with a stable `DesktopErrorCodes` code (backward-compatible — English text remains the fallback; native client forwards the field unchanged). Android maps the code to a localized string (8 languages). Codes: `capture_unavailable`, `capture_stopped`, `target_unavailable`, `display_switch_unsupported`, `runtime_unavailable`. Rich Windows capture diagnostics remain untranslated by design.
- **`AgentCoordinator` canonical-port reclaim**: `StartWebHostAsync` polls `HostBootstrapper.IsPortFree(RemexConstants.DefaultPort)` at 500ms intervals for up to 30s before calling `CreateApplication`, preventing the port-fallback loop from silently binding port+1 (e.g. 5006) when the GUI host's socket is still in TIME_WAIT. If the port is not free after 30s, proceeds with a warning log. Gate is idempotent (`_app` null check + `SemaphoreSlim`). On startup failure the half-started app is disposed so `_app` resets to null and a subsequent reclaim attempt can create a fresh instance. Test seam: injectable port-checker (`Func<int,bool>`), app factory (`Func<WebApplication>`), poll interval (`int`), pipe name (`string?`).
- **`mapLocalToHost` nullable Offset (RemEx-ubm)**: Android `RemoteDesktopScreen.mapLocalToHost` returns `Offset?` (null only on error/degenerate cases); all seven call sites skip their action on null. Negative coordinates (monitor at negative virtual-desktop origin) are valid; cursor visibility is carried as its own `hostCursorVisible` flag, never encoded as a sentinel coordinate.

<!-- END AUTO-MANAGED -->

<!-- AUTO-MANAGED: git-insights -->
## Git Insights

- Active development branch: `2.0` (main branch for PRs: `main`).
- Hottest areas by recent history: `remex.agent` (PC side), `remex.android`, `remex.agent.native.linux`, with `remex.desktop` being phased out.
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

**Status: P0/P1 GATE MET — all 14 P0 beads and all 12 original P1 beads are CLOSED.** The release is conditionally shippable on Windows. Remaining open items are a Linux runtime-parity validation (RemEx-lr9, P1, environment-blocked) and out-of-scope follow-ups (RemEx-d8s client removal, RemEx-5i9 >60fps investigation, two deferred perf beads). Full ordered edit plan archived in `REMEX_2.0_FINAL.md`.

### Release Status Summary

| Gate | Result |
|------|--------|
| All P0 beads closed | PASSED (14/14) |
| All P1 beads closed | PASSED (12/12) |
| Linux runtime parity | PENDING (RemEx-lr9 — in-progress, environment-blocked on Windows dev host) |
| Deferred perf (RD-6/RD-7) | DEFERRED — measurement-gated, logged on bead |
| remex.desktop removal | DEFERRED (RemEx-d8s, P2) |

### P0 Beads — ALL CLOSED

| Bead | Issue | Status |
|------|-------|--------|
| `RemEx-htt` PROTO-1 | 8338 channel unauthenticated | **CLOSED** — `PairedClientChannelAuthenticator` |
| `RemEx-a75` PAIR-5 | `/start-pairing` open to remote | **CLOSED** — `PairingThrottle` |
| `RemEx-lhd` PAIR-2 | PIN brute-force / 10-min window | **CLOSED** — `PairingThrottle` + 5-attempt cap |
| `RemEx-dta` PAIR-3 | Host private key world-readable | **CLOSED** — `CertificateService` PFX 0600 permissions |
| `RemEx-m1i` IPC-1 | Pipe ACL `Everyone` writable | **CLOSED** — `LocalIpcServerService` ACL restricted to interactive user + LocalSystem |
| `RemEx-n6u` IPC-2 | IPC pipe command auth bypass | **CLOSED** — `LocalIpcServerService` privileged-action gate |
| `RemEx-4ky` PROTO-2 | Protocol version not enforced | **CLOSED** — `HostBootstrapper.EvaluateDesktopAuth` enforces `protocolVersion >= 2` |
| `RemEx-288` PROTO-3 | Pairing endpoint missing auth gate | **CLOSED** — pairing endpoint auth hardened |
| `RemEx-e3z` JNI-1 | JNI exceptions abort JVM | **CLOSED** — `[UnmanagedCallersOnly]` export guard catches managed exceptions |
| `RemEx-9m1` JNI-2 | JNI null deref / unsafe marshal | **CLOSED** — JNI marshalling hardened |
| `RemEx-ii3` RD-1 | H.264 encoder backpressure | **CLOSED** — `FFmpegH264Encoder` bounded channels |
| `RemEx-fs5` RD-3 | H.264 output unbounded queue | **CLOSED** — `FFmpegH264Encoder` drop-oldest output channel |
| `RemEx-bqc` RD-2 | H.264 keyframe / decoder recovery | **CLOSED** — `RemoteDesktopHandler` + `H264StreamDecoder` |
| `RemEx-a13` NSD-1 | NSD discovery reliability | **CLOSED** — `NsdDiscoveryManager` API 34+/pre-34 strategy |

### P1 Beads — ALL CLOSED

| Bead | Issue | Status |
|------|-------|--------|
| `RemEx-3n6` PAIR-1 | clientId-only reconnect auth | **CLOSED** — `PairedClientRegistry` proof-of-possession |
| `RemEx-rc4` PAIR-4 | Reconnect secret world-readable | **CLOSED** — `paired_clients.json` + reconnect-secret file 0600 permissions |
| `RemEx-irl` IPC-4 | IPC pipe open to non-interactive users | **CLOSED** — ACL restricted |
| `RemEx-qg2` IPC-5 | IPC pipe system-wide write access | **CLOSED** — ACL scoped to current interactive user SID |
| `RemEx-oj8` IPC-6 | IPC-6 Linux pipe permissions | **CLOSED** — Linux branch uses `UnixFileMode` 0600 |
| `RemEx-4ic` IPC-3 | IPC-3 pipe auth gap | **CLOSED** — pipe auth hardened |
| `RemEx-ngs` NSD-4 | NSD-4 discovery hardening | **CLOSED** |
| `RemEx-i8x` NSD-5 | NSD-5 Linux virtual-interface filtering | **CLOSED** — `MdnsAdvertisingService` Linux virtual-interface filter |
| `RemEx-kx4` RD-5 | H.264 decoder output format change | **CLOSED** — `RemoteDesktopHandler` + `H264StreamDecoder` |
| `RemEx-aa0` RD-4 | FFmpeg pump cancellation on dispose | **CLOSED** — `FFmpegH264Encoder` `_processCts` |
| `RemEx-4uy` PROTO-4 | PROTO-4 hardening | **CLOSED** |
| `RemEx-jny` PROTO-5 | PROTO-5 hardening | **CLOSED** |

### P2/P3 Beads (closed via 2026-06-22 hardening)

| Bead | Issue | Status |
|------|-------|--------|
| `RemEx-q6u` RD-8 | Hostile float coordinates (NaN/∞ → arbitrary pixel) | **CLOSED** — `CoordinateValidation.ClampAbsolute` / `ClampDelta` |
| `RemEx-8ay` JNI-4 | Concurrent pairing race (dual `ClientWebSocket` dispose) | **CLOSED** — `PairingSyncRoot` in `AndroidNativeExports` |
| `RemEx-85i` JNI-5 | JNI string marshalling outside `Export` guard | **CLOSED** — `ReadJString` moved inside `Export` |
| `RemEx-ymb` JNI-3 | JNI-3 | **CLOSED** |
| `RemEx-4bb` NSD-overlap | Overlapping NSD resolves / multicast-lock cycles | **CLOSED** — `discoveryJob` cancel-before-relaunch in `ConnectionViewModel` |
| `RemEx-l79` / `RemEx-hht` | Stale / wrong-arch `.so` packaged into APK | **CLOSED** — content-tracked inputs + AArch64 ELF header check in `SyncRemexCoreSoTask` |
| `RemEx-b3m` IPC-8 | `UnauthorizedAccessException` collapsed into generic IPC error | **CLOSED** — dedicated catch in `RemExLocalIPC` |
| `RemEx-00x` NSD-6 | Untrusted mDNS SRV port/host injected into WebSocket URL | **CLOSED** — `MdnsDiscoveryService` SRV validation |
| `RemEx-29e` PAIR-6 | HMAC comparison not constant-time | **CLOSED** — constant-time raw-byte HMAC in `PairingService` |
| `RemEx-xk9` PAIR-7 | PAIR-7 | **CLOSED** |
| `RemEx-79h` IPC-7 | IPC-7 | **CLOSED** |
| `RemEx-kjq` RD-2 const | RD-2 const | **CLOSED** |
| `RemEx-lbk` | 8338 docs P1 follow-up | **CLOSED** |

### Post-Release-Gate Fixes (closed after 2.0 gate)

| Bead | Issue | Status |
|------|-------|--------|
| `RemEx-i9e` | Pairing PIN auto-populate broken over Tailscale | **CLOSED** — `TransportTrust` host-side classifier; PIN served to verified Tailscale tunnels (`100.64.0.0/10` / `fd7a:115c:a1e0::/48`) in addition to loopback |
| `RemEx-dqj` | Privileged IPC commands rejected when host runs interactively | **CLOSED** — `LocalIpcServerService.TryGetSelfAsActiveConsoleUserSid` fallback for non-LocalSystem console-session host |
| `RemEx-sgj` | Pairing persistence throws `InvalidOperationException` when host runs interactively | **CLOSED** — `PairedClientRegistry.RestrictStorePermissionsWindows` uses assignable owner; catch extended |
| `RemEx-xuo` | Android reconnect rejected (no proof-of-possession secret) | **CLOSED** — `RemexClientManager` persists reconnect secret via `PinnedHostStore`; clients must re-pair once |
| `RemEx-0ov` | Android crashes on mDNS discovery (`RejectedExecutionException` kills process) | **CLOSED** — `NsdDiscoveryManager` executor uses `DiscardPolicy` instead of `AbortPolicy` |
| `RemEx-l6o` | Keep-session-unlocked feature + interactive-host guard fix (locking own session on monitor switch) | **CLOSED** — `WindowsInteractiveSessionGuard` ref-counted; `_heldSessionId` only set when guard performs the reconnect; `ISessionKeepUnlockedService` + in-app toggle with localized security warning |

| `RemEx-ubm` | Pointer (0,0) corner silently dropped / phantom corner click | **CLOSED** — `RemoteDesktopHandler.EnqueuePointerSampleAsInputEvent` uses absolute logical position directly (including 0,0); `mapLocalToHost` returns `Offset?` (null on error only) |
| `RemEx-x0b` | H264 decoder init failure left a permanently-black stream | **CLOSED** — `H264StreamDecoder` signals ViewModel on init failure; ViewModel marks streaming stopped and triggers backoff reconnect |
| `RemEx-5t4` | Silent black screen with no error when host is slow to produce first frame | **CLOSED** — frame-arrival watchdog in `RemoteDesktopViewModel` triggers reconnect after ~7s silence |
| `RemEx-gim` | Stale frame from previous stream target shipped after target switch | **CLOSED** — send loop drops frames with mismatched `StreamSerial`; serial is host-authoritative |
| `RemEx-w5v` | Codec switch (H.264 ↔ MJPEG) briefly misrouted frames (silent black gap) | **CLOSED** — `RemoteDesktopFrameEnvelope` `RDXF` per-frame header tags codec + serial; `supportsFrameEnvelope` client capability |
| `RemEx-728` | Host stream errors shown in English only | **CLOSED** — `DesktopErrorCodes` stable codes on host; Android maps to localized strings (8 languages) |
| `RemEx-hmk` | Legacy HKCU Run key started a MEDIUM-integrity competing host instance (UIPI input block) | **CLOSED** — `StartupRegistrationService` no longer creates the HKCU Run entry; interactive host removes any legacy entry on startup |
| `RemEx-3um` | Android Gradle build broken on repeat/clean runs (native-lib copy destination repointed away from ABI-folder, `@InputFile` failing on fresh clean, `clean` racing against build tasks) | **CLOSED** — three `build.gradle.kts` fixes: copy destination reverted to ABI-folder convention, `@Internal` on `generatedArm64So`/`mergedArm64So`, single `mustRunAfter` ordering rule |

### Deferred Beads (measurement-gated, not release blockers)

| Bead | Issue | Status |
|------|-------|--------|
| `RemEx-p0l` RD-6 | Per-frame heap allocations on DXGI capture hot path | **DEFERRED** — measurement-gated; profile under load before addressing |
| `RemEx-m3a` RD-7 | MJPEG path forces per-frame StateFlow emission → Compose recomposition storm | **DEFERRED** — measurement-gated |
| `RemEx-bct` | Android: special-keys toolbar (latching modifiers, F-keys, nav block) | **DEFERRED** — feature, not a release blocker |

### Remaining Open Follow-ups

| Bead | Priority | Description |
|------|----------|-------------|
| `RemEx-lr9` | P1 — IN PROGRESS | CachyOS/Linux runtime parity validation (IPC pipe 0600, CertPFX 0600, paired_clients.json 0600, MdnsAdvertising virtual-iface filter). Code compiles cross-platform; runtime validation environment-blocked on Windows dev host. |
| `RemEx-d8s` | P2 — open | Remove `remex.desktop` entirely — migrate Host-used services into Host/Core, delete legacy UI. Sequence after all P0/P1 fixes. |
| `RemEx-5i9` | P3 — open | Android RD: investigate >60fps ceiling (DXGI capture / display-refresh bound, not codec). |

### Security Areas — Heightened Caution

When touching any of these files, treat as security-critical and require user sign-off:
- `remex.core/Services/Network/RemexNetworkListener.cs` — 8338 channel; `PairedClientChannelAuthenticator` gates dispatch (PROTO-1 closed); all PROTO P0/P1 beads closed
- `remex.core/Services/Network/MdnsDiscoveryService.cs` — SRV port + host validated before WebSocket URL composition (NSD-6 closed); all NSD P0/P1 beads closed
- `remex.core/Validation/CoordinateValidation.cs` — sanitizes untrusted float coordinates from remote clients; any change here affects all pointer/scroll/drag security
- `remex.agent/Services/IPC/LocalIpcServerService.cs` — pipe ACL now restricted to interactive user + LocalSystem (IPC-1 closed); all IPC P0/P1 beads closed
- `remex.agent/Services/Security/PairingService.cs` — ECDH state machine with 5-attempt cap and ~120s timeout; constant-time raw-byte HMAC implemented (PAIR-6 closed); all PAIR P0/P1 beads closed
- `remex.agent/Services/Security/CertificateService.cs` — SPKI hash management; PFX file permissions 0600 (PAIR-3 closed)
- `remex.agent/Services/Security/PairedClientRegistry.cs` — proof-of-possession reconnect auth implemented (PAIR-1 closed); reconnect-secret file 0600 (PAIR-4 closed)
- `remex.agent/HostBootstrapper.cs` — `EvaluateDesktopAuth` enforces paired clientId + protocolVersion ≥ 2 for `/ws/desktop` (PAIR-5 closed, PROTO-2 closed)
- `remex.core/Native/JniHelper.cs` + `AndroidNativeExports.cs` — JNI-1/2/3/4/5 all closed; export guard catches managed exceptions before escaping `[UnmanagedCallersOnly]`
- `remex.android/app/src/main/java/com/clindsay94/remex/security/PinnedHostStore.kt` — Tink AES-256-GCM AEAD encrypted SPKI hashes and PAIR-1 reconnect secrets; two DataStores; Android Keystore-backed; corruption self-recovery. Changes here affect all client-side certificate pinning and reconnect auth.
- `remex.agent/Services/Security/TransportTrust.cs` — gates pairing PIN auto-fetch to loopback and verified Tailscale tunnels; must stay in sync with Android's `TransportTrust.kt`; breakage silently opens PIN to LAN callers or breaks tunnel auto-fill.
- `remex.agent/Services/Session/WindowsInteractiveSessionGuard.cs` — unlocks the PC (reconnects RDP-disconnected session to console via `tscon`) while a remote-desktop stream is active; while engaged the session is accessible without a password. Feature is off by default; changes to engage/disengage logic, `_heldSessionId` ownership, or `SessionGuardPolicy` require explicit user sign-off.

### Cross-Platform Rule for All Orders

Every order touching Windows ACL APIs (`PipeSecurity`, `WindowsIdentity`, `FileSecurity`, SIDs) **must** be guarded with `OperatingSystem.IsWindows()` and include a Linux branch using `UnixFileMode`/`SetUnixFileMode 0600` (owner-only). Linux runtime validation pending RemEx-lr9 (environment-blocked on Windows dev host).

### Design Decisions — Resolved 2026-06-22 (user-confirmed)

- **8338 channel (PROTO-1): AUTHENTICATED-REMOTE.** Keep `IPAddress.Any`; do NOT bind loopback. The host runs as LocalSystem in Session 0 so remote commands + telemetry work with no user logged in — restricting to loopback breaks the product's core purpose. Fix: require a paired-client identity (`PairedClientRegistry` token) before `ExecuteCommandAsync` dispatch. **`PairedClientChannelAuthenticator` implements this — CLOSED.**
- **IPC / Session-0 model: confirmed unchanged.** Remote commands and telemetry flow through the Session-0 service directly; they do NOT traverse the named pipe. Pipe ACL orders (IPC-1/IPC-5/IPC-6) govern only the local tray/dashboard UI (interactive user) talking to the service. All IPC beads now closed.
- **`remex.desktop` removal (bead `RemEx-d8s`): NOT a clean delete.** `remex.agent` still references `remex.desktop.Services` in `Program.cs`, `StartupRegistrationService.cs`, `SessionKeepUnlockedService.cs`, `DesktopIconExtractionService.cs`. Migrate all still-used types into `remex.agent`/`Remex.Core` first, then delete the legacy UI and remove `remex.desktop` + `remex.desktop.tests` from `Remex.sln`. Sequence AFTER all P0/P1 security fixes (which are now done).

### Definition of Done (release)

Every P0 and P1 bead closed — COMPLETE. Remaining criteria for final sign-off:
- Green build on Windows (verified), Linux (CachyOS via `build-remex.ps1` — compile-verified; runtime pending RemEx-lr9), and Android (`scripts/android-fresh.ps1`)
- Green tests (`dotnet test Remex.sln`) — 428+ pass on Windows (includes 8 `DuplicationReinitThrottle` unit tests + new `RemoteDesktopHandlerTests` for `IsLive = false` stale-replay path)
- Cross-platform parity verified for all ACL/file-permission/native code (Windows + Android verified; Linux runtime pending RemEx-lr9)
- `CHANGELOG.md` updated under `Security`/`Fixed`/`Changed`
- `protocolVersion` bump coordinated only if a wire-format break is taken

<!-- END AUTO-MANAGED -->

<!-- MANUAL -->
## Custom Notes

Add project-specific notes here. This section is never auto-modified.

<!-- END MANUAL -->

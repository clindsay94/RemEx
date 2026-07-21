## RemEx Architecture & Project Rules

These rules apply to ALL agents working in this repository. They are not overridable by individual session context.

### Host = PC, Client = Android. End of Story.

- `remex.agent` is the **entire PC side** — a single interactive elevated user-session app (always elevated via `requireAdministrator` manifest; auto-started by an elevated Task Scheduler logon task) that provides all PC functionality. Android connects TO this.
- `remex.android` is the **only** network client. Nothing else is a client.
- `remex.desktop/` holds PC-side UI code only (Views/ViewModels/Localization) and is compiled directly into `remex.agent` via a real `<ProjectReference>` — it is NOT a standalone app and NOT being removed. "Legacy" refers only to the leftover pre-rename folder/namespace name. A prior removal effort (`RemEx-d8s`) was closed without deleting it; do not add new *standalone-client* code there, but the existing UI code is live and required.
- Connection is always Android → PC, always non-loopback.
- If you find old references to a "desktop client" connecting to a "desktop host" (i.e. a *separate* headless host process), update them — that separate-process architecture doesn't exist. `remex.desktop`'s UI code itself is current, not one of these stale references.

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

### Carry-Forward Rules (distilled from closed 2.0-era work)

Full narratives and bead-by-bead history: `docs/OLD DOCS/AGENTS-2.0-archive.md`. These rules
survive because breaking them reintroduces a known, hard-to-diagnose failure.

**Windows capture:**
- Backend ladder is WGC → DXGI → GDI (`WindowsScreenCaptureService`). Operator capture knobs go ONLY in `CaptureBackendPreference` (HKLM). `CaptureScaling` lives in `Remex.Core` — do not move or duplicate it.
- `remex.agent.windows` is Windows-only WinRT isolation — never add cross-platform or Linux code there.
- WinRT interop: `CreateForMonitor`/`CreateForWindow` take `IID_IGraphicsCaptureItem` (constant in `WgcDesktopCapture.cs`), NOT the runtimeclass GUID — the wrong GUID silently returns E_NOINTERFACE and WGC falls back as if never tried. Any WinRT ABI `IntPtr` crossing into a CsWinRT API must use `MarshalInspectable<T>.FromAbi`, never `Marshal.GetObjectForIUnknown`.
- `DxgiDesktopCapture` / `WgcDesktopCapture` / `WindowsDisplayPowerMonitor` are GPU/session-bound: tests use `FakeScreenCaptureService` (`SafeHostTestDoubles.cs` registers safe doubles first in `RemexHostFactory`), never the live classes.

**Remote-desktop stream:**
- All frame/cursor pacing routes through `PrecisionPacer`; DXGI re-init routes through `DuplicationReinitThrottle`. No `Task.Delay`-only pacing loops; never call `TryReinitializeDuplication` at frame rate.
- FPS ceilings route through `DesktopConfig.MaxTargetFps`/`PacedMaxFps` (Android mirror: `RemoteDesktopViewModel.DESKTOP_MAX_FPS`/`DESKTOP_FPS_PACED_MAX`) — never reintroduce a hardcoded 120/360.
- Wire magic bytes: `"RDXF"` = frame envelope, `"RDXC"` = binary cursor — never reuse either. Capability additions stay additive/gated; no `protocolVersion` bump unless breaking.
- Windows GPU encode: `h264_nvenc_bgra` (BGRA fed directly to NVENC) is the ONLY supported GPU path. Never reintroduce `-vf hwupload_cuda,scale_cuda` — prebuilt Windows ffmpeg lacks the RGB→semiplanar kernel; it passes init then dies at runtime (0 fps black screen, fallback never fires).

**Linux capture/portal:**
- `OpenPipeWireRemote` MUST be called on the same D-Bus connection that owns the portal session (sender-scoped fd). A fresh connection is rejected by the portal and capture silently degrades to a ~1 FPS fallback.
- The portal capture session stays warm for the PROCESS lifetime (`LinuxCaptureSessionLifetime`). Never reintroduce refcount-zero teardown or idle-grace closing — restore-after-close yields a connected stream that never produces buffers (RemEx-lq6h).
- Keep the `SPA_FORMAT_VIDEO_maxFramerate` [1,120] choice-range in `pipewire_capture.c`'s EnumFormat pod — dropping it silently reinstates KWin's ~12 FPS damage-driven cadence.

**Android remote desktop:**
- SurfaceView zoom/pan uses `Modifier.layout`, never `graphicsLayer` (which cannot move a native surface). The H.264 `AndroidView`'s `key()` must include `imageSize` alongside stream dims — a surface created against transient geometry freezes its content scale.
- Preset changes go through `applyDesktopPreset(...)` atomically — never set quality/fps/scale individually. Stream start/stop, keyboard, and FPS toggles live in the fullscreen overlay; do not re-add a unified control bar to `RemoteDesktopScreen.kt`.

**Session guard:** `WindowsInteractiveSessionGuard` only re-locks sessions it actually unlocked — it must never disconnect its own session (black-screen + access-denied-input failure mode).

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


<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **RemEx** (15432 symbols, 31381 relationships, 300 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

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

<!-- AUTO-MANAGED: project-description -->
## Overview

Remote Execution (RemEx) is a cross-platform PC remote management tool. Architecture is **Android (client) → PC (host)**, always non-loopback. `remex.agent` is the entire PC side — a **single interactive elevated user-session app** (no Windows Service; always elevated via `requireAdministrator`, auto-started by a Task Scheduler logon task on Windows). `remex.android` is the only network client. `Remex.Core` is shared across all targets and is also compiled as a NativeAOT JNI native library (`libRemexCore.so`) for Android.

<!-- END AUTO-MANAGED -->

<!-- AUTO-MANAGED: build-commands -->
## Build & Development Commands

- `dotnet run --project remex.agent` — run the PC host service (Android connects to this).
- `dotnet test Remex.sln` — run all tests.
- `pwsh ./build-remex.ps1 -c release -t all` — unified cross-platform release build (canonical entry point).
- `pwsh ./scripts/android-fresh.ps1 -Configuration Release` — hardened fresh Android build; runs under `pwsh` on both Windows and Linux (resolves `gradlew.bat` vs the POSIX `gradlew` via `$IsWindows`, invoking the latter through `sh` since git may not preserve its executable bit on a shared drive).
- `./installer/build-linux.sh` — build Linux packages (uses WSL on Windows).
- `dotnet run --project remex.agent -- --doctor` — check Linux PipeWire/X11/VAAPI prerequisites.

<!-- END AUTO-MANAGED -->

<!-- AUTO-MANAGED: architecture -->
## Architecture

- `remex.core/` — shared models, messages, validation, Guards, serialization; also compiled as `libRemexCore.so` (NativeAOT JNI) for Android. Must stay NativeAOT-safe.
  - `Validation/CoordinateValidation` — `ClampAbsolute(float, int)` / `ClampDelta(float, int)`: sanitize untrusted float coordinates from remote clients before pixel cast (rejects NaN/±Infinity).
  - `Native/RemexDesktopClient` — singleton (`RemexDesktopClient.Current`) WebSocket client for `/ws/desktop`; consumed by `remex.desktop` (legacy). Lives in Core and is NativeAOT-compiled — must remain NativeAOT-safe. Events: `FrameReceived`, `CursorShapeReceived`, `Disconnected`.
  - `Models/DesktopCursorBinaryEnvelope` — 32-byte binary cursor-position packet: magic `"RDXC"`, `Write()`/`TryRead()`, signed X/Y (negative virtual-desktop origins supported), `BinaryPrimitives`/spans (NativeAOT-safe). Demuxed from `"RDXF"` frame envelope and raw NAL start codes by leading 4 bytes. Gated by `SupportsBinaryCursor` capability; no `protocolVersion` bump.
  - `Services/BgraFrameConverter` — `TryConvertNoScale(IntPtr src, int rowPitch, int width, int height, double scale)`: row-wise `Marshal.Copy` fast path for BGRA32 staging textures (WGC/DXGI); returns `null` when a downscale is needed (caller falls back to GDI+ bilinear). No `System.Drawing`; NativeAOT-safe.
- `remex.agent/` — the PC side: a single elevated interactive-session Avalonia app + in-process ASP.NET host (Minimal APIs, WebSocket, mDNS). Runs inside the signed-in user's session, always elevated; `Program.Main` owns the embedded host's Start/Stop and a `Local\RemExGuiHost` single-instance mutex. No Windows Service, no second process. (RemEx-aep)
  - `Handlers/` — `PairingHandler` (ECDH P-256 handshake), `RemoteDesktopHandler` (codec negotiation, H.264/MJPEG streaming; default 120 FPS target, max clamp `MaxTargetFps = DesktopConfig.MaxTargetFps` (360, was a literal 120 — RemEx-z377); `_targetFps = 120`); takes `IInteractiveSessionGuard`; per-session `sessionGuardClientId`; streams `desktop_cursor_shape`/`desktop_cursor_state`; enforces `StreamSerial` on send; throttles keyframe reinits to 5s cooldown; static `_ffmpegAvailableCache` probed once at construction), `PingPongHandler` (keep-alive).
  - `Services/Security/` — `PairingService` (ECDH state machine), `PairedClientRegistry` (proof-of-possession reconnect auth), `PairingThrottle` (per-IP rate limiting), `CertificateService` (SPKI management), `TransportTrust` (network-path trust classifier: loopback / Tailscale / LAN — gates PIN auto-fetch).
  - `Services/Network/` — `PairedClientChannelAuthenticator` (8338 TCP channel auth gate via `PairedClientRegistry`).
  - Local IPC is **gone** (RemEx-aep): no `RemExLocalIPC` / `RemExHostControl` pipes, no `LocalIpcServerService`/`HostControlServer`/`AgentCoordinator`. The desktop UI (same process) resolves host services from DI via `remex.desktop`'s `EmbeddedHostServiceLocator`.
  - `Services/Session/` — `IInteractiveSessionGuard` / `WindowsInteractiveSessionGuard` (ref-counted keep-awake only via `SetThreadExecutionState`; no tscon/disconnect — we live inside the session; `[SupportedOSPlatform("windows")]`); `ISessionKeepUnlockedService` (writes/reads `ProgramData\RemEx\keep-session-unlocked.flag`; wired to in-app Settings toggle, Windows-only, off by default, with localized security warning; gates the keep-awake in `RemoteDesktopHandler`).
  - `Services/RemoteDesktop/` — `IH264Encoder` / `FFmpegH264Encoder` (FFmpeg subprocess, bounded channels, on-demand keyframe); `PrecisionPacer` (hybrid coarse-sleep + `Thread.SpinWait` pacer, absolute timeline, shared by video frame loop and cursor loop — see Patterns).
  - `Services/ScreenCapture/` — `DxgiDesktopCapture` (DXGI Desktop Duplication API, Windows 10/11, MPO/GPU-composited content; deferred init via `EnsureInitialized()` on first capture — no idle-slot hold at construction); `DuplicationReinitThrottle` (exponential-backoff gate for `DuplicateOutput` re-init; prevents driver-wedge on display power transitions); `WindowsScreenCaptureService` wraps captures as `ScreenCaptureResult { Pixels, IsLive }` — GDI path always `IsLive = true`; DXGI path sets `IsLive = false` on stale `_lastFrame` replays; implements `WarmUpCapture()` to prime DXGI via `_dxgi.TryRecover()` AND select the WGC monitor (so `GraphicsCaptureItem.Size` is populated before the first `GetScreenSize()` call); `GetScreenSize()` reports dimensions of whichever backend actually serves the active target (WGC → DXGI → GDI); `ActiveMonitorOrigin()` supplies the monitor's virtual-desktop (possibly negative) origin for absolute cursor mapping. Host logs `"RD bootstrap: streaming WxH @ (L,T) via {backend}"` at connect time. (RemEx-6my, RemEx-4k4) `IScreenCaptureService` (in `Remex.Core`) defines `ScreenCaptureResult`, the capture contract, and `WarmUpCapture()` (default no-op; call once per client connection before `GetScreenSize()`).
  - `Services/Command/WindowsSystemCommandService` (in `Remex.Core`) is registered directly as `ISystemCommandService` — lock/monitor-off/sign-out take effect in-session (no `SessionBridgingCommandService` / `WindowsActiveSession` bridge; both deleted with the Session-0 model). `AppLauncherService` launches via a normal ShellExecute on the user's desktop.
- `remex.android/` — the only client: Kotlin + Jetpack Compose + JNI → `libRemexCore.so`.
  - `data/NsdDiscoveryManager` — mDNS discovery (`_remex._tcp.`); API 34+ uses concurrent `registerServiceInfoCallback`, pre-34 serialises via process-wide `resolveMutex`.
  - `security/PinnedHostStore` — Tink AES-256-GCM AEAD encrypted storage for paired host SPKI hashes and PAIR-1 reconnect secrets; two DataStores (`remex_pinned_hosts`, `remex_reconnect_secrets`); Android Keystore-backed keyset; corruption self-recovery.
  - `ui/screens/H264StreamDecoder` — `MediaCodec` H.264 decoder in **SYNCHRONOUS mode**: a single dedicated `H264DecodeLoop` thread polls `dequeueInputBuffer`/`dequeueOutputBuffer` (async `setCallback` is deliberately NOT used — on the deferred-configure path the Qualcomm c2 decoder reaches RUNNING but `onInputBufferAvailable` never fires, so the codec is never fed and the stream stays black). Renders to a **SurfaceView**; bounded backlog (`MAX_INPUT_BACKLOG = 6`, drop-oldest, then `onKeyframeNeeded`). Deferred `configure()` — waits for the first SPS+PPS-bearing IDR, then configures with explicit `csd-0` (SPS) / `csd-1` (PPS); the codec adopts the SPS-declared resolution so a wrong width/height hint can't matter. **Mid-stream SPS reconfigure (RemEx-aep):** when a later AU carries an SPS whose raw bytes differ from the configured `csd-0` (cheap `containsNalType` pre-check keeps P-frames on the fast path), it `stop()/configure()/start()`s for the new resolution — this is the fix for the scale-up black screen. `KEY_MAX_INPUT_SIZE` is sized for the full-screen (4K-bounded, 8 MiB-capped) max, not the initial hint, and `KEY_MAX_WIDTH`/`KEY_MAX_HEIGHT` are set for adaptive playback (explicit reconfigure is the fallback). MUST NOT set `KEY_COLOR_FORMAT` (Qualcomm `c2.qti.avc.decoder` rejects `COLOR_FormatSurface` → zero output), `KEY_LOW_LATENCY`, or `KEY_OPERATING_RATE` (shrink the DPB pool → stall). Pre-keyframe P-frames dropped; IDR units queued with `BUFFER_FLAG_KEY_FRAME`; `onInitFailure` on non-transient decoder error (owner reconnects). (RemEx-bqc / RemEx-kx4 / RemEx-x0b / RemEx-aep / #2b)
  - `ui/screens/RemoteDesktopViewModel` — stream config, display-target selection, cursor shape overlay, frame-arrival watchdog. `desktopMetaReady` signal gates the orientation-aware initial fit until the host's real stream metadata (dimensions, origin, backend) arrives; prevents the initial zoom computing against a placeholder resolution.
  - `ui/screens/RemoteDesktopScreen` — Jetpack Compose UI, gesture handling (tap/scroll/pinch), immersive full-screen.
  - `ui/screens/ConnectionViewModel` — NSD discovery lifecycle; `discoveryJob: Job?` ensures one in-flight discovery at a time.
- `remex.desktop/` — PC-side UI code only, compiled directly into `remex.agent` via a real `<ProjectReference>`; permanent, not being removed (see "Host = PC, Client = Android" above).

Protocols: WSS `/ws` (port 5005, telemetry/power/pairing/file transfer), WSS `/ws/desktop` (port 5005, H.264/MJPEG remote desktop), TCP+TLS 8338 (external script ingress — requires paired `clientId` via `PairedClientChannelAuthenticator`; `CommandRequest` JSON must include `ClientId` field). The former `RemExLocalIPC` / `RemExHostControl` named pipes are gone (single process; UI↔host is in-process DI). Messages use the `RemexMessage` JSON envelope with `protocolVersion: 2`. Pairing uses ECDH P-256 + 6-digit PIN, then SPKI certificate pinning. Wire message types include `MessageTypes.DesktopKeyframeRequest` (`"desktop_keyframe_request"`) for client-to-host on-demand IDR keyframe requests.

<!-- END AUTO-MANAGED -->

<!-- AUTO-MANAGED: conventions -->
## Code Conventions

- Do NOT use `ConfigureAwait(false)` anywhere (CA2007 suppressed).
- Nullable reference types enabled everywhere; use `Guard.NotNull(arg)` and `GetRequiredService<T>()`.
- Validate all network-facing input via `remex.core/Validation/`.
- `Remex.Core` must be NativeAOT-safe: no reflection, no dynamic codegen, source-generated JSON only.
- On Windows, `remex.agent` runs elevated INSIDE the signed-in user's session (not Session 0, no service). Keep security-sensitive machine-wide state (`cert.pfx`, `paired_clients.json`, `CaptureBackendPreference`) in `HKLM`/`ProgramData` so it survives across logins and stays under the elevated-only ACL; never weaken the `requireAdministrator` manifest (a medium-integrity start bricks SPKI-pinned pairings).
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
- **`H264StreamDecoder` deferred SPS/PPS configure (device-portable)**: The Android decoder does NOT call `MediaCodec.configure()` on construction. It waits for the first access unit that carries NAL type 7 (SPS) + type 8 (PPS) — i.e. an IDR keyframe — then configures MediaCodec with explicit `csd-0` (SPS) / `csd-1` (PPS) before calling `start()`. Reason: relying on the codec to auto-detect inline SPS/PPS works on some hardware but silently wedges others (no output produced, input buffers never freed, backlog fills, per-frame keyframe-request flood). Supplying SPS as `csd-0` also forces the codec to adopt the SPS-declared resolution, making a stale width/height hint harmless. P-frames arriving before the first IDR are dropped silently — the host emits an IDR every 60 frames on its own, so `onKeyframeNeeded` is NOT flooded during startup. (#2b decode-stall)
- **`H264StreamDecoder` forbidden `MediaFormat` keys (Surface-output decoders)**: NEVER set `KEY_COLOR_FORMAT` (Qualcomm `c2.qti.avc.decoder` rejects `COLOR_FormatSurface` `0x7F000789` with "configureIntf failed 95 / ? is not a supported pixel format" → zero output, silently black stream + per-frame keyframe flood), `KEY_LOW_LATENCY`, or `KEY_OPERATING_RATE` (these shrink the decoder's output/DPB pool to ~2 buffers; with a SurfaceView, output buffers are held until the Surface consumer latches them — with a 2-buffer pool the codec exhausts output after ~2 frames and in async mode stops offering input buffers → classic "Works: Q:2/Done:2 then stall" black screen). Only `KEY_PRIORITY=0` (real-time hint, safe), `KEY_MAX_INPUT_SIZE` (sized for 1440p+ IDR frames), `csd-0`, and `csd-1` are set. (#2b decode-stall, Qualcomm output-buffer starvation)
- **`H264StreamDecoder` dedicated HandlerThread for MediaCodec callbacks**: `setCallback` MUST be called as `setCallback(callback, Handler(HandlerThread("H264DecoderCb").also { it.start() }.looper))` — NOT as `setCallback(callback)` or `setCallback(callback, null)`. The null/no-handler form delivers callbacks on the calling thread's looper, which is the main/UI looper when the decoder is constructed from a Compose coroutine or ViewModel. Under connection load (Compose recomposition + SurfaceView frame routing), the main looper is saturated and `onInputBufferAvailable` is starved — the codec is never fed input and the stream stays permanently black with 0 input-buffer callbacks despite the codec appearing healthy. The dedicated HandlerThread ("H264DecoderCb") is started before `setCallback` and terminated via `thread.quitSafely()` in `release()`.
- **On-demand keyframe recovery**: `IH264Encoder.RequestKeyframe()` / `ConsumeKeyframeRequest()` atomic flag consumed by the capture loop; `H264StreamDecoder.onKeyframeNeeded` callback on Android. Both ends coordinate to recover from decoder desync without waiting a full GOP.
- **`IInteractiveSessionGuard` check before streaming**: `RemoteDesktopHandler` checks `IHostCapabilitiesProvider.SupportsRemoteDesktop` and the session guard before starting a desktop stream; sends structured `DesktopErrorCodes` on failure (not generic WebSocket close).
- **`WindowsInteractiveSessionGuard` ref-count keep-awake model**: `EngageForRemoteControl(clientId)` / `DisengageFromRemoteControl(clientId)` maintain an `_engaged` HashSet; the first engage / last disengage triggers action. On engage, calls `SetThreadExecutionState(ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED | ES_CONTINUOUS)` to keep the interactive session alive while streaming; on last disengage, clears the flag. No `tscon`, no `WTSDisconnect` — the guard lives inside the user session and never reconnects or disconnects it. `SessionGuardPolicy` and `SessionGuardAction` are deleted. Every engage/disengage is audit-logged with the client identity. **Security-sensitive**: while engaged the screen will not lock — feature is off by default; enabled via `ProgramData\RemEx\keep-session-unlocked.flag` containing `1`, written by `ISessionKeepUnlockedService` (in-app toggle shows a localized security warning). `[SupportedOSPlatform("windows")]`; test double is `NoOpInteractiveSessionGuard`.
- **`EvaluateDesktopAuth` pre-auth for `/ws/desktop`**: `HostBootstrapper.EvaluateDesktopAuth` enforces: loopback → allow unconditionally; non-loopback → must have paired `clientId` (PairedClientRegistry) AND `protocolVersion >= 2`. Unknown or missing clientId → 401/403; old protocol → 400; newer-than-host → 200 (forward compat).
- **Pairing brute-force defense**: `PairingService` caps failed HMAC attempts at 5 per session with a ~120s session timeout — **this is the active protection**. (The former `PairingThrottle` per-IP sliding-window class was removed in RemEx-0xp0: its only call site was the now-deleted `/start-pairing` endpoint, and — confirmed by grep — it was never DI-registered, so `GetService` always returned null and it never actually ran. A real per-IP cross-session throttle on the `/ws` pairing path is tracked as a follow-up bead.)
- **`NsdDiscoveryManager` API-level strategy**: API 34+ uses concurrent, cancellable `registerServiceInfoCallback`; pre-34 serialises resolves process-wide via a `Mutex` (NsdManager pre-34 allows only one in-flight resolve). Always acquires a `WifiManager.MulticastLock` for mDNS reliability.
- **Frame-arrival watchdog in `RemoteDesktopViewModel`**: arms on stream start, resets on every decoded frame, triggers reconnect if no frame arrives within stall timeout. Backstops H.264 decoder-init silent-death path.
- **`CoordinateValidation` float sanitization**: All absolute pointer coordinates use `CoordinateValidation.ClampAbsolute(float, int)` and all relative deltas use `CoordinateValidation.ClampDelta(float, int)` before casting to `int`. Rejects NaN/±Infinity; clamps to valid pixel bounds. Regression tests in `remex.core.tests/CoordinateValidationTests.cs` (RD-8).
- **`AndroidNativeExports` dual-lock model**: `PairingSyncRoot` (separate from the high-frequency `SyncRoot`) serializes pairing-session state transitions so a concurrent `StartPairing`/`SubmitPin` call from a second Java thread waits rather than disposing-then-using the active `ClientWebSocket` (JNI-4). JNI string marshalling (`ReadJString`) happens inside the `Export` guard so managed throws are caught before escaping `[UnmanagedCallersOnly]` (JNI-5).
- **`MdnsDiscoveryService` SRV validation**: Before composing the `ws://` URL from untrusted multicast data, validates SRV port >= 1 and resolved host passes `Uri.CheckHostName != Unknown` (NSD-6).
- **`RemExLocalIPC` ACL error surfacing**: `UnauthorizedAccessException` on pipe open returns a distinct "Permission denied" `CommandResponse` rather than collapsing into the generic `IPC Error` path, giving users an actionable message (IPC-8).
- **`ConnectionViewModel` single in-flight discovery**: `discoveryJob: Job?` tracks the active NSD coroutine; `startDiscovery()` cancels any prior job before launching so overlapping manual + self-heal calls do not stack NSD resolves or multicast-lock cycles (RemEx-4bb).
- **`SyncRemexCoreSoTask` ELF verification**: Content-tracks `sourceCandidates` as Gradle inputs (prevents stale `.so` on `-NoClean` builds) and validates the `.so` is AArch64 ELF (magic `0x7F454C46` + `EI_CLASS=2` + `e_machine=0xB7`) before copying into the APK (RemEx-l79 / RemEx-hht).
- **`DuplicationReinitThrottle` DXGI re-init throttle**: `DxgiDesktopCapture` gates all `TryReinitializeDuplication` / `DuplicateOutput` calls through `DuplicationReinitThrottle` (backoff: 1s base, 8s max, exponential escalation). On `DXGI_ERROR_ACCESS_LOST`, at most one re-init attempt is made per backoff window; confirmed-healthy frames (real frame or `WAIT_TIMEOUT`) call `RecordHealthyFrame()` to reset. Prevents the "display-off storm" that wedged DWM + NVIDIA driver at stream frame rate (RemEx-crk). Clock-injected for deterministic unit tests.
- **`ScreenCaptureResult.IsLive` stale-replay signal (RemEx-hmj / STALE_CACHE_ON_ACCESS_LOST — CLOSED)**: `IScreenCaptureService` capture methods return `ScreenCaptureResult { Pixels, IsLive }`. `IsLive = true` means a fresh real frame was produced; `IsLive = false` means the cached `_lastFrame` was replayed (e.g. on `DXGI_ERROR_ACCESS_LOST` during the `DuplicationReinitThrottle` backoff window). `RemoteDesktopHandler` only resets `consecutiveFailures` on `IsLive = true`; stale replays now propagate to the coded-error path. `DxgiDesktopCapture` deferred init (`EnsureInitialized()` on first capture, not in constructor) eliminates the idle-slot-hold that blocked Windows RDP when RemEx was idle. GDI fallback path always returns `IsLive = true` (never cached). Tested via `FakeScreenCaptureService` in `RemoteDesktopHandlerTests`. **`WarmUpCapture()` (RemEx-6my, RemEx-4k4):** `RemoteDesktopHandler` calls `_screenCapture.WarmUpCapture()` once per client connection (before `SendCurrentStreamBootstrapAsync`). On Windows it does two things: (1) primes DXGI via `_dxgi.TryRecover()` so DXGI is initialized before `GetScreenSize()` is called; (2) selects the WGC monitor so `GraphicsCaptureItem.Size` is populated immediately (before the first frame). `GetScreenSize()` now reports the dimensions of whichever backend ACTUALLY serves the active target (WGC → DXGI → GDI) — not always DXGI. `ActiveMonitorOrigin()` supplies the monitor's virtual-desktop origin (possibly negative) for absolute cursor mapping. Without this priming, `GetScreenSize()` on a WGC-served monitor returned DXGI/GDI probe bounds that disagreed with the WGC frame — causing mis-framed first connect (RemEx-4k4). `IScreenCaptureService.WarmUpCapture()` is a default-interface no-op; backends that initialize eagerly need not override it. New backends with deferred init or a size that differs from DXGI MUST override and prime their dimensions in `WarmUpCapture()`.
- **`PinnedHostStore` reconnect-secret persistence (PAIR-1/RemEx-xuo)**: After a successful Android pairing, `RemexClientManager` extracts the `reconnectSecret` from the `OK:hostId|spki|reconnectSecret` result and calls `PinnedHostStore.setReconnectSecret(context, hostId, ...)` + `setReconnectSecret(context, host, ...)`. On reconnect, `getReconnectSecret()` supplies the secret to `RemexCoreClient` to answer the host's proof-of-possession challenge; without a stored secret, the host rejects the reconnect and forces a re-pair. Secrets live in a dedicated DataStore (`remex_reconnect_secrets`, separate from `remex_pinned_hosts`) encrypted via Tink AES-256-GCM AEAD with `hostId` as associated data.
- **`TransportTrust` PIN auto-fetch gate**: Host-side `TransportTrust.IsTrustedForPinAutoFetch(remote, local)` and Android-side `TransportTrust.canAutoFetchPin(context, host)` must agree for PIN auto-fill to work end-to-end. Host allows PIN auto-fetch when caller is loopback OR both remote and local addresses are Tailscale CGNAT (`100.64.0.0/10` / `fd7a:115c:a1e0::/48`) — requiring both ends defeats a LAN attacker spoofing a `100.64.x.x` source. Android allows PIN auto-fetch for loopback OR (Tailscale address / `*.ts.net` MagicDNS hostname) AND `TRANSPORT_VPN` active — VPN-active check is mandatory; a Tailscale-looking address with no live tunnel must NOT unlock auto-fetch. Host handles IPv4-mapped addresses (`::ffff:100.64.x.x`) for Kestrel. Android `requiresLocalNetworkAccess(host)` returns `false` for loopback/Tailscale/`*.ts.net` targets, gating `NEARBY_WIFI_DEVICES`/`ACCESS_LOCAL_NETWORK` runtime permission requests — changes here can silently break Tailscale users (spurious permission prompts) or open LAN permission gates. Both sides are security-critical and must be kept in sync; changes require explicit user sign-off.
- **`isMulticastReachableHost` mDNS guard (RemEx-fkz)**: `RemexClientManager` gates self-healing mDNS discovery behind `isMulticastReachableHost(host)`, which returns `false` for Tailscale/CGNAT (100.64.0.0/10) and public IPs. Prevents spamming Android's local-network permission prompt when the saved host is a VPN or public address. Private LAN (10.x, 172.16–31.x, 192.168.x), link-local (169.254.x), and non-IP hostnames all pass as multicast-reachable.
- **`PinnedHostStore` Tink AEAD corruption recovery**: `aead()` uses a double-checked lock; on init failure (lock-screen key invalidation, app-data cleared with Keystore intact, etc.) it clears the `remex_tink_prefs` SharedPreferences keyset, clears both DataStores, and retries — preventing a permanently bricked app. Keyset is Android Keystore-backed; no deprecated `EncryptedSharedPreferences` or `MasterKey` APIs.
- **`StreamSerial` stale frame guard (RemEx-gim)**: `RemoteDesktopHandler` send loop drops any buffered frame whose `StreamSerial` no longer matches the session's current serial (host-authoritative), closing the race window where the capture thread could swap an old-serial frame in just after the buffer was cleared on a target switch.
- **Keyframe throttle cooldown**: `RemoteDesktopHandler` throttles keyframe-driven encoder reinits to at most one per 5s. The first request (or the first after the cooldown expires) triggers a real reinit + SPS/PPS+IDR; requests arriving inside the cooldown are swallowed (the decoder re-requests if still desynced). Any legitimate rebuild (target switch, quality/fps/scale change) also satisfies the cooldown. Stream Metrics log reports `Throttled keyframe reinits: N` so a flood is visible at a glance.
- **`ClipCursor` host cursor confinement (Windows, RemEx)**: While a single monitor is being streamed, `WindowsInputSimulationService` confines the host cursor to the streamed display via Win32 `ClipCursor`, re-applied on each cursor-shape tick (~10 Hz, because Windows releases the clip on display/desktop/foreground changes). Released when streaming stops, cancels, or the client disconnects. No-op when streaming the full virtual desktop and on Linux.
- **Native cursor shape streaming**: Host streams `desktop_cursor_shape` (BGRA bitmap + hotspot, sent on change, JSON) and cursor position/visibility at 60–90 Hz. `_drawCursor` is gated off in `RemoteDesktopHandler` when `SupportsCursorShape` is true, so host-side compositing is disabled. When `SupportsBinaryCursor` is negotiated, cursor POSITION travels as a `DesktopCursorBinaryEnvelope` binary packet (`"RDXC"`, 32 bytes) instead of a JSON `desktop_cursor_state` message — zero per-message allocation on both sides (see `DesktopCursorBinaryEnvelope` pattern below). Cursor SHAPE stays JSON (rare, large). Requires JNI callbacks across the NativeAOT boundary. Legacy hosts/clients fall back to the JSON path.
- **`RemoteDesktopFrameEnvelope` per-frame codec routing (RemEx-w5v)**: Client opts in via `supportsFrameEnvelope`; host prepends a small `RDXF` header to each frame tagging codec + stream serial. Android routes each frame to the decoder that produced it (by the per-frame tag) rather than a separately-updated "active codec" flag that could lag a frame behind, eliminating the silent black gap on codec switch. Legacy hosts sending untagged frames fall back to negotiated-codec routing.
- **Self-healing H.264 codec recovery, envelope-gated (RemEx-lq6h)**: `RemoteDesktopHandler` no longer permanently demotes `_activeCodec` to MJPEG on a failed encoder rebuild. For envelope-capable sessions (`UseFrameEnvelope`) it keeps the negotiated codec, tags frames `DesktopCodecKind.Mjpeg` while the encoder is down, and retries H.264 on a 3s cooldown (`nextH264RetryMs`) — safe only because the `RDXF` per-frame tag lets the client route each frame correctly regardless of what codec is "active". Envelope-less legacy clients keep the OLD permanent-demotion behavior (they can't route a mixed stream). `FFmpegH264Encoder.ProbeCache` pairs with this: failed probe verdicts now expire after `FailedProbeRetryMs = 30_000` (positive verdicts still cache forever) so a transient probe failure during display churn can't pin a geometry to MJPEG until restart.
- **`LinuxCaptureSessionLifetime` warm-for-process-lifetime portal session (REGRESSION GUARD, RemEx-lq6h)**: The Linux portal capture session (and its PipeWire stream) is opened once and kept alive for the **process** lifetime, not torn down when the last client disconnects — `ReleaseAsync` decrements the refcount but never calls `StopInternalAsync` at zero. Closing a KDE ScreenCast session and reopening it shortly after (exactly what disconnect→reconnect and monitor-switch do) reliably yields a stream KWin reports as valid but that never produces a buffer for minutes. Teardown now only happens on `OnPortalSessionLost` (compositor killed the session) or `DisposeAsync` (process shutdown). Cold starts verify first-frame production (`WaitForFirstFrameAsync`, 3s) and recreate the portal session once on failure before giving up. **NEVER reintroduce refcount-zero teardown** — it reintroduces the multi-minute dead-stream bug. Known side effect: KDE's screen-sharing indicator stays on for the life of the process.
- **Drift-free absolute mouse via unified portal session (RemEx-lq6h)**: `LinuxInputSimulationService.MoveMouse` first tries `LinuxCaptureSessionLifetime.TryInjectPointerMotionAbsolute(x, y)`, which maps the point into the active ScreenCast stream's coordinate space and calls `LinuxPortalRemoteDesktopSessionService.TryNotifyPointerMotionAbsolute` (D-Bus `NotifyPointerMotionAbsolute` on the SAME session that owns capture — compositor-clamped, no cumulative drift). Only falls back to the old relative-delta emulation when no session is active. `RemoteDesktopHandler.ClampToActiveBounds(x, y)` additionally clamps every absolute pointer target to `_screenCapture.GetScreenSize()` bounds before it reaches `IInputSimulationService.MoveMouse` on ALL platforms, so an overshooting client coordinate can never drive the cursor onto an unstreamed monitor.
- **Signature-guarded raw/JPEG frame cache (`LinuxScreenCaptureService`, RemEx-lq6h)**: `_lastRawFrame` / `_lastJpegFrame` are records carrying `(ActiveLeft, ActiveTop, ActiveWidth, ActiveHeight, Scale)` alongside the cached bytes; a cache is replayed only when that full signature (including offset — two monitors can share dimensions) still matches the current active target. `CaptureRawScreenLiveAsync` returns `ScreenCaptureResult { Pixels, IsLive }` (parity with Windows): a same-target replay on a static screen is `IsLive = true` (PipeWire is damage-driven), a geometry-stale cache is never replayed. Raw output always uses `CaptureScaling.ScaledEven`, even at `scale = 1.0`, so an odd-sized monitor/crop can't desync the H.264 encoder's fixed rawvideo input size.
- **`PanFollowCalculator` zoom pan-follow (`RemoteDesktopScreen`)**: When zoomed past 1×, the host streams cursor position at ~60 Hz; the picture glides to keep the host cursor on-screen using edge-deadzone tracking (mirrors Windows App behavior). The remote-desktop screen no longer forces landscape — it rotates with the device. Cursor visibility is gated by the `hostCursorVisible` flag (not a sentinel coordinate); negative coordinates from monitors at a negative virtual-desktop origin are treated as valid.
- **`PrecisionPacer` hybrid frame pacing (REGRESSION GUARD)**: Single source of truth for remote-desktop stream and cursor pacing (`remex.agent/Services/RemoteDesktop/PrecisionPacer`). Coarse-sleeps via `Task.Delay` for the bulk of each interval, then busy-spins with `Thread.SpinWait` for the final ~16 ms — beating the Windows OS timer floor (~15.6 ms) that would cap a bare `Task.Delay(8)` to ~60 FPS instead of 120. Absolute timeline: per-tick overruns shorten the NEXT wait rather than accumulating drift. Call `Reset()` after any pause or backoff so recovery doesn't burst through a backlog of missed ticks. No global `timeBeginPeriod`; benefits Linux pacing too. NEVER replace with bare `Task.Delay` in any stream or cursor loop — the regression is silent. (`docs/REMOTE_DESKTOP_PERFORMANCE.md` was deleted as stale planning-doc housekeeping — this entry is now the durable record; do not search for that file.)
- **`RemoteDesktopHandler` static FFmpeg availability cache**: `_ffmpegAvailableCache` (static `bool?`) is probed once at handler construction — not per-stream-start — behind `_ffmpegCacheLock`. Avoids repeated process-spawn checks; the cache persists for the process lifetime.
- **`DesktopCursorBinaryEnvelope` binary cursor protocol (RD-E)**: 32-byte fixed binary packet over `/ws/desktop` binary channel, magic `"RDXC"` (distinct from `"RDXF"` frame envelope and Annex-B NAL start code `00 00 00 01`). Layout (little-endian): `magic[4] version[1] flags[1] reserved[2] X[int32] Y[int32] shapeSerial[int64] streamSerial[int64]`. X/Y are SIGNED (monitors at negative virtual-desktop origins). Receiver demuxes on leading 4 bytes; if not `"RDXC"`, treats as video frame. Cursor SHAPE stays JSON (rare/large). Gated by `SupportsBinaryCursor` client capability — no `protocolVersion` bump; older hosts/clients stay on JSON path. NativeAOT-safe (`BinaryPrimitives`/spans). JNI delivery as `byte[]`, parsed via `ByteBuffer` on Android. (`DesktopCursorBinaryEnvelope`, `RemoteDesktopHandler.SendCursorBinaryAsync`, `AndroidNativeExports`, `RemexDesktopClient`, `RemexClientManager`, `RemoteDesktopViewModel`.)
- **`BgraFrameConverter` GDI-free BGRA fast path (RD-C, REGRESSION GUARD)**: `BgraFrameConverter.TryConvertNoScale(IntPtr src, int rowPitch, int width, int height, double scale)` reads a mapped BGRA32 staging texture (WGC or DXGI) into a tightly-packed `byte[]` via row-wise `Marshal.Copy`, honoring GPU row pitch (which can exceed `width*4`). Returns `null` when a downscale is needed — caller falls back to GDI+ bilinear. Lives in `Remex.Core`; NativeAOT-safe. REGRESSION GUARD: the previous path wrapped every frame in a `System.Drawing.Bitmap`, ran `Graphics.DrawImage`, and copied via `LockBits` — allocating multi-MB objects per frame. Do NOT reintroduce `Bitmap`/`Graphics.DrawImage` on the hot capture path.
- **`DesktopErrorCodes` localized error surface (RemEx-728)**: Host tags `errorText` with a stable `DesktopErrorCodes` code (backward-compatible — English text remains the fallback; native client forwards the field unchanged). Android maps the code to a localized string (8 languages). Codes: `capture_unavailable`, `capture_stopped`, `target_unavailable`, `display_switch_unsupported`, `runtime_unavailable`. Rich Windows capture diagnostics remain untranslated by design.
- **`mapLocalToHost` nullable Offset (RemEx-ubm)**: Android `RemoteDesktopScreen.mapLocalToHost` returns `Offset?` (null only on error/degenerate cases); all call sites (touch, tap, cursor overlay, L/M/R click buttons) skip their action on null. Negative coordinates (monitor at negative virtual-desktop origin) are valid; cursor visibility is carried as its own `hostCursorVisible` flag, never encoded as a sentinel coordinate.
- **SurfaceView zoom/pan MUST use `Modifier.layout`, NOT `graphicsLayer` (INVARIANT)**: `graphicsLayer { scaleX/scaleY = zoomFactor; translationX/Y = panOffset }` does NOT scale or move a SurfaceView's native surface — the system composites it at its LAYOUT BOUNDS; `graphicsLayer` is a draw-time transform that only affects the Compose placeholder rectangle. Applying zoom/pan via `graphicsLayer` leaves the H.264 image tiny/letterboxed and stranded in black while input mapping (`mapLocalToHost`), cursor overlay, and pan-follow all correctly use the zoom — symptom: "panning is correct but the video is rendered too small / cropped." Fix: apply zoom/pan via `Modifier.layout { measurable, constraints -> ... }` — measure the SurfaceView at `contentRect() * zoomFactor` and `place()` it centered + panOffset. This sizing matches `mapHostToLocal` exactly, keeping video, cursor overlay, input, and pan-follow aligned. The decode buffer is pinned by `holder.setFixedSize(streamPixelWidth, streamPixelHeight)`; the compositor scales that fixed buffer to the layout bounds with no surface churn. The MJPEG fallback (a Compose `Image`) still uses `graphicsLayer` correctly — only the SurfaceView needs layout-based scaling. Became latent until fit-to-height (RD-A3) made the default zoom > 1. NEVER apply zoom/pan to the H.264 SurfaceView via `graphicsLayer`.
- **Android IME/keyboard state MUST use IME insets, NOT focus state (RemEx-46q)**: In `RemoteDesktopScreen`, the on-screen keyboard state (`isRemoteKeyboardOpen`) is derived from live IME insets — `WindowInsets.ime.getBottom(LocalDensity.current) > 0` — NOT from `BasicTextField` focus (`onFocusChanged { isRemoteKeyboardOpen = it.isFocused }`). The back gesture hides the IME without clearing focus, so the focus-based approach caused `requestFocus()` to be a no-op and the keyboard could never be re-summoned. The keyboard toggle button calls `LocalSoftwareKeyboardController.show()` / `.hide()` alongside `requestFocus()`, guaranteeing the IME opens/closes even when the field was already focused. NEVER drive soft-keyboard visibility from Compose focus state in a remote-desktop or similar IME-controlled screen.

<!-- END AUTO-MANAGED -->

<!-- AUTO-MANAGED: git-insights -->
## Git Insights

- Active development branch: `2.0` (main branch for PRs: `main`).
- Hottest areas by recent history: `remex.agent` (PC side), `remex.android`, `remex.agent.native.linux`. `remex.desktop` sees little independent change but is not being removed — it's the permanent PC-side UI project.
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
## RemEx 2.0 Release Gate — MET (historical)

2.0 shipped; the repo is now at 2.4.x. The full gate record (P0–P3 bead tables, design
decisions, definition of done) is archived at `docs/OLD DOCS/AGENTS-2.0-archive.md`.
Two decisions from it still bind:
- **Port 8338 stays `IPAddress.Any` (authenticated-remote).** Android connects from a separate device — loopback-binding breaks all remote access.
- **`remex.desktop` stays.** RemEx-d8s closed WITHOUT deleting it; it is a live `<ProjectReference>` of `remex.agent`.
<!-- END AUTO-MANAGED -->

<!-- MANUAL -->
## Custom Notes

Add project-specific notes here. This section is never auto-modified.

<!-- END MANUAL -->

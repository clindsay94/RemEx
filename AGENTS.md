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
- `WindowsScreenCaptureService` — drives the WGC → DXGI → GDI ladder using `CaptureBackendPreference`. As of RemEx-hvqv, WGC monitor-selection outcomes are logged at Information (selected) / Warning (failed / unresolved device name), de-duped via `_lastWgcSelectionStateKey` so the per-frame hot path logs once-per-state rather than at frame rate. Previously these logs were at Debug (invisible in both the in-memory `/debug/logs` sink and the Windows Event Log, which floor at Information and Warning respectively).

**Testing seam:** WGC and DXGI both require an interactive session + GPU and are not headless-testable. CI coverage is at the `IScreenCaptureService` seam (`FakeScreenCaptureService`) and the HKLM preference parse (`CaptureBackendPreferenceTests`). The real backend matrix requires manual interactive validation.

**Agent rules (carry forward to all work in this area):**
- `DxgiDesktopCapture` is `sealed` + `[SupportedOSPlatform("windows")]` + P/Invokes GPU — uninstantiable headless/in Session 0. Tests must use `FakeScreenCaptureService`, not the live class.
- `WgcDesktopCapture` is likewise `[SupportedOSPlatform("windows")]` and requires WinRT + GPU — never instantiate in tests; always use `FakeScreenCaptureService`.
- **WinRT interop IID pitfall (RemEx-hvqv):** `IGraphicsCaptureItemInterop::CreateForMonitor` and `CreateForWindow` take the **interface** IID (`IID_IGraphicsCaptureItem = 79c3f95b-31f7-4ec2-a464-632ef5d30760`), NOT the runtimeclass GUID from `typeof(GraphicsCaptureItem)`. Passing the runtimeclass GUID silently returns `E_NOINTERFACE` — WGC falls back to DXGI/GDI as if it were never tried, with no obvious error. Always use the `IID_IGraphicsCaptureItem` constant defined in `WgcDesktopCapture.cs` for any future `CreateForMonitor`/`CreateForWindow` calls.
- **WinRT ABI pointer marshaling (RemEx-hvqv):** The `IDirect3DDevice` for the WGC frame pool must be projected via `WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(ptr)`, NOT `Marshal.GetObjectForIUnknown(ptr) as IDirect3DDevice`. The latter yields a legacy `__ComObject` that CsWinRT cannot re-marshal, causing `Direct3D11CaptureFramePool.CreateFreeThreaded` to throw `"Failed to create a CCW ... IDirect3DDevice: the specified cast is not valid"`. **RULE: any WinRT ABI `IntPtr` crossing into a CsWinRT projected API must use the CsWinRT `FromAbi` marshalers (`MarshalInspectable<T>.FromAbi` or `<Type>.FromAbi`) — never `Marshal.GetObjectForIUnknown`.**
- `WindowsDisplayPowerMonitor` is also `[SupportedOSPlatform("windows")]` and requires a real message pump — never instantiate it in tests. The `IScreenCaptureService.IsDisplayPoweredOff` property defaults to `false`; fakes and Linux backends inherit that default automatically.
- `remex.agent.windows` must stay Windows-only. Never add cross-platform or Linux code there.
- `CaptureScaling` lives in `Remex.Core` (NativeAOT-safe). Do not move it back to `remex.agent` or create a duplicate.
- `CaptureBackendPreference` is the single source of truth for operator capture knobs. New per-backend toggles go there.
- Path casing is load-bearing on Linux: source dir is `remex.agent` (capital E-x); test projects are `remex.agent.tests` / `remex.core.tests` (lower e-x); solution is `Remex.sln`. Wrong casing passes on Windows, breaks on CachyOS.
- Pre-existing test failures in `PairedClientRegistry`/`RemoteDesktopAuth`/`PairingHandler` (issue `RemEx-jgw`) are filtered from gates; gates assert a minimum test count so a zero-match filter cannot show green.
- **Test host safe doubles (RemEx-21g):** `RemexHostFactory` default-registers three safe doubles FIRST in DI: `FakeScreenCaptureService` (pure managed, no DXGI/D3D/GDI), `NoOpInteractiveSessionGuard` (no `tscon` call), `NoOpSystemCommandService` (no lock/reboot/shutdown). Defined in `SafeHostTestDoubles.cs`. No integration test touches the GPU, locks the session, or runs a power command.
- **Diagnosing WGC not serving (RemEx-hvqv):** Check Information/Warning logs for the WGC selection outcome — `_lastWgcSelectionStateKey` de-dupes these so they appear once per state change, not at frame rate. Three states: `ok:{deviceName}` (WGC selected), `fail:{deviceName}:{error}` (TrySelectMonitor failed), `unresolved:{displayId}` (Monitor mode but no Win32 device name resolved). If only DXGI/GDI is serving and no WGC Warning appears, the target is likely virtual-desktop mode (expected — WGC is per-monitor only).

### remex.agent Execution Model — Interactive Elevated App (RemEx-aep, all 6 phases landed)

**Status:** Phases 1–6 are committed on branch `2.0` and the build/tests are green (agent 291/291). Phase 1 (elevation manifest + logon-task auto-start), Phase 2 (second-process deletion), Phase 3 (collapse `LocalIpcServerService`/`RemExLocalIPC` to in-process DI), Phase 4 (Session-0 cleanup + keep-awake-only session guard), Phase 5 (Android `H264StreamDecoder` mid-stream SPS reconfigure — the headline image fix — + UIPI canary), Phase 6 (docs). **Pending:** Phase 1's runtime gate (`RemEx-aep.1`) — full checklist at `docs/superpowers/plans/aep1-runtime-verification-checklist.md` (deploy + sign-out/in: single High-integrity instance, no UAC, cert reused/SPKI unchanged, brick canary silent; `bd close RemEx-aep.1` when all assertions pass). The **Linux follow-up** (`RemEx-aep.7`) is **done**, and `RemEx-u0oc` completed the parity: Linux ships a single `remex-agent` package (`installer/linux/agent-install.sh` installs to `~/.local/share/remex-agent` with an XDG autostart `~/.config/autostart/remex-agent.desktop`, `Exec=… --minimized`, mirroring the Windows logon task). The headless SYSTEM `remex-host.service` was **removed** (pre-login power commands are a non-goal on Linux, as on Windows); the installer uninstalls it from existing machines and migrates a root-owned `/var/lib/remex/cert.pfx` to the per-user store SPKI-intact. The Linux cert path is per-user (`~/.local/share/Remex/cert.pfx`, beside `paired_clients.json`). The P0/P1 Linux security perms were runtime-validated on CachyOS (`RemEx-lr9`, closed).

**What changed:** remex.agent is no longer a Windows Service. It is a single interactive elevated user-session Avalonia app, always elevated (`requireAdministrator` in `app.manifest`), auto-started by an elevated Task Scheduler logon task (task name: `"RemEx"`, LogonType=InteractiveToken, RunLevel=HighestAvailable).

**Deleted subsystems (do not reference in new code or docs):**
- `AgentCoordinator` — orchestrated the former two-process split (gone)
- `HostControlServer` / `HostControlClient` — Named Pipe IPC between the old service and desktop app (gone)
- `InteractiveDesktopHostLauncher` — launched the desktop app from Session 0 (gone)
- `--agent` / `RunAgent` / `UseWindowsService` Program.cs paths (gone)

**Surviving startup paths in `Program.cs`:**
- Normal launch: single-instance Mutex guard → embedded `WebApplication` host → Avalonia GUI. A second instance defers to the running one.
- `--doctor [--fix]`: Linux prerequisite report; exits without launching UI.

**Auto-start:** `StartupRegistrationService` manages a Task Scheduler task named `"RemEx"` (name must stay stable — `autostart-remex.ps1` and verification steps query this exact name). `RemoveLegacyWindowsRunKey()` cleans up any legacy HKCU Run entry on startup; a lingering Run key would start a competing medium-integrity instance, win the single-instance guard, and reintroduce the UIPI input block (RemEx-hmk).

**Elevation rationale — `requireAdministrator` is load-bearing, do not remove it:**
1. **Cert safety:** high integrity keeps FullControl over `cert.pfx` / `paired_clients.json`. A medium-integrity start gets Administrators as deny-only, regenerates the cert, and bricks all SPKI-pinned Android pairings.
2. **UIPI:** HIGH→HIGH `SendInput` is permitted; a medium-integrity sender is silently dropped by Windows UIPI, breaking remote input injection.
3. **HKLM writes:** `CaptureBackendPreference` and other machine-wide settings require elevation.

**Session 0 rules no longer apply:** remex.agent runs in the interactive user session. HKCU and `%APPDATA%` are valid for user-scoped state. Machine-wide config (`cert.pfx`, `paired_clients.json`, `CaptureBackendPreference`) stays in HKLM/ProgramData for correctness across re-logins.

### Remote Desktop 2.0 Infrastructure — CLOSED (batch landed in [Unreleased])

Key new components agents must know before touching remote desktop, capture, pairing, or session-guard code:

**Binary cursor protocol (`DesktopCursorBinaryEnvelope`, magic `"RDXC"`):**
- High-frequency cursor position (60–90×/s) travels as a fixed 32-byte binary packet over `/ws/desktop` binary channel, demultiplexed from H.264 frames by magic bytes. Do NOT reuse `"RDXC"` or `"RDXF"` (frame envelope magic).
- Capability-gated via `DesktopClientCapabilities.SupportsBinaryCursor`. **No `protocolVersion` bump** — older hosts/clients stay on the JSON `desktop_cursor_state` path automatically.
- NativeAOT-safe (`BinaryPrimitives`/spans). Requires Android rebuild when the JNI layer changes.
- Key symbols: `DesktopCursorBinaryEnvelope`, `RemoteDesktopHandler.SendCursorBinaryAsync`, `RemexDesktopClient`, `AndroidNativeExports`.

**`BgraFrameConverter` — no System.Drawing on the capture→encode hot path:**
- When `scale = 1.0`, reads the mapped staging texture into a tightly-packed buffer via a single row-wise `Marshal.Copy` (no Bitmap, no GDI+ blit). Downscale path keeps GDI+ bilinear resampler. Output byte-count is asserted equal to the encoder's `-s WxH` and unit-tested.
- For maximum FPS: capture at full resolution (`scale 1.0`) and let the phone downscale for display — NVENC handles full-res easily.
- Key symbols: `BgraFrameConverter`, `WgcDesktopCapture`, `DxgiDesktopCapture`.

**FPS ceiling raised 120 → 360 — shared `DesktopConfig.MaxTargetFps`/`PacedMaxFps` constants (RemEx-z377):**
- `DesktopConfig.MaxTargetFps = 360` is now the single source of truth for both the wire clamp (`DesktopConfig.TargetFps` init clamps to this, not a literal `120`) and the host cap (`RemoteDesktopHandler.MaxTargetFps = DesktopConfig.MaxTargetFps`). Android mirrors it as `RemoteDesktopViewModel.DESKTOP_MAX_FPS = 360`. Always route new FPS ceilings through these constants — never reintroduce a hardcoded `120`/`360`.
- `DesktopConfig.PacedMaxFps = 240` / Android `DESKTOP_FPS_PACED_MAX = 240` is the display cutover point: any value above it renders as the localized "Unlimited" string instead of a number (desktop `FpsDisplayConverter`; inline `targetFps > DESKTOP_FPS_PACED_MAX` checks in `ConnectionScreen`/`RemoteDesktopScreen`). 240 already exceeds every consumer display refresh and the H.264 encoder is throughput-bound below it at real resolutions, so the exact value above it is immaterial to the user — while the frame pacer stays safe (1000/360 ≈ 2.78 ms/tick).
- Backward-compatible with **no `protocolVersion` bump**: a host built before this change still clamps an incoming `360` straight back to `120` via its own unchanged literal — safe degradation, not a wire break.
- Android's `DesktopPreset.UNLIMITED` bundle (`DESKTOP_PRESET_BUNDLES`) now requests the real `DESKTOP_MAX_FPS` (360) instead of being pinned at 120.
- Desktop capture defaults changed alongside this: `DashboardProfile.StreamQuality`/`StreamScale` now default to 100/1.0 (was 75/0.5).
- Key symbols: `DesktopConfig.MaxTargetFps`/`PacedMaxFps`, `RemoteDesktopHandler.MaxTargetFps`, `RemoteDesktopViewModel.DESKTOP_MAX_FPS`/`DESKTOP_FPS_PACED_MAX`, `FpsDisplayConverter`, `DashboardProfile`.

**`PrecisionPacer` — shared hybrid precision wait:**
- Coarse `Task.Delay` + `Thread.SpinWait` busy-spin for the final milliseconds. Eliminates the ~15.6 ms Windows OS timer floor that previously capped 120 FPS targets at ~64 Hz.
- Shared by the video-frame loop AND the cursor loop (`RemoteDesktopHandler`). Do not introduce separate `Task.Delay`-only pacing loops — route through `PrecisionPacer`.

**`DuplicationReinitThrottle` — DXGI re-init rate limiter:**
- One `DuplicateOutput` attempt per backoff window (1 s → 8 s escalating, reset on any confirmed-healthy frame). Prevents NVIDIA DWM wedge from back-to-back re-init storms on display power transitions.
- 8 unit tests cover the throttle logic. Do not call `TryReinitializeDuplication` at frame-rate frequency.
- Key symbols: `DuplicationReinitThrottle`, `DxgiDesktopCapture`.

**`TransportTrust` — host-side Tailscale pairing PIN awareness:**
- `/pairing-pin` is now served when the caller is on loopback **or** when both caller and host-side address are Tailscale IPs (`100.64.0.0/10` or `fd7a:115c:a1e0::/48`). `/start-pairing` remains loopback-only.
- Requiring the host-side address to also be Tailscale defeats a LAN attacker spoofing a `100.64.x.x` source via the LAN IP. Plain LAN/internet stay closed (404).
- As of 2.3.0 (RemEx-1t0b) the `/ws` `pairing_pin_request` message shares this **exact same** `IsTrustedForPinAutoFetch` gate (computed at the `/ws` map site and passed into `PingPongHandler`/`PairingHandler`). It is an ASI-compliant replacement for the Android app's old trust-all HTTP fetch; it only *relays* an already-active PIN and never creates a session. `GET /pairing-pin` is retained one release for shipped ≤2.2.4 apps.
- Key symbols: `TransportTrust`, `HostBootstrapper`, `PingPongHandler`, `PairingHandler`.

**Keep-session-unlocked (security-sensitive, Windows-only):**
- Settings toggle (off by default) that keeps the interactive session usable while a remote-desktop client is connected. Backed by `ISessionKeepUnlockedService` writing `ProgramData\RemEx\keep-session-unlocked.flag`. Prominent 8-language security warning shown while enabled.
- `WindowsInteractiveSessionGuard` only reconnects/re-locks a session it **actually unlocked** — never disconnects its own session (`Process.GetCurrentProcess().SessionId`). A guard that disconnects the host's own session triggers the "black screen + access denied input" failure mode.
- Key symbols: `IInteractiveSessionGuard`, `WindowsInteractiveSessionGuard`, `SessionGuardSettings`, `ISessionKeepUnlockedService`, `SettingsViewModel`.

**Wire protocol carry-forward rules:**
- All capability additions are additive/gated — no `protocolVersion` bump unless breaking.
- `"RDXF"` = frame envelope magic (opt-in via `supportsFrameEnvelope`); `"RDXC"` = binary cursor magic. Never reuse either.
- Legacy hosts that send untagged frames: client falls back to negotiated-codec routing automatically.

**Linux portal PipeWire fast path — sender-scoped fd, CLOSED (RemEx-82fk):**
- **Root cause:** `xdg-desktop-portal` scopes a RemoteDesktop/ScreenCast session's PipeWire node ACL to the D-Bus connection that created it. The native capture bridge previously called `OpenPipeWireRemote` on its own fresh sd-bus connection, which the portal rejects as a different sender — the ACL-protected screencast node was invisible, so capture silently fell back to a ~1 FPS per-frame spectacle+ffmpeg full-desktop path.
- **Fix:** `LinuxPortalRemoteDesktopSessionService.StartSessionAsync` now calls `OpenPipeWireRemote` on the **same session-owning `Tmds.DBus.Connection`** used for `CreateSession`/`SelectSources`/`Start`, obtaining a portal-scoped fd that is threaded through `PortalStartResult.PipeWireFd` → `LinuxCaptureSessionCoordinator` → `LinuxPipeWireFrameSource`. Native side gained `remex_pw_session_create_v3(pw_fd, ...)` which connects PipeWire using this caller-provided fd (dup'd); `v2`/`v1` remain as fallbacks. **Never call `OpenPipeWireRemote` on a newly-opened connection — it must reuse the connection that owns the portal session.**
- **KDE persist_mode fallback (`SelectSources`, RemEx-82fk CLOSED):** `SelectSources` requests `persist_mode=PersistUntilRevoked` (+ replays a saved `restore_token`) so reconnects skip the "Share your screen" prompt, for legacy portal-v1 sessions and ScreenCast-only fallback sessions. `xdg-desktop-portal-kde` rejects persistence specifically for RemoteDesktop sessions (`DBusException` with `ErrorName == "org.freedesktop.portal.Error.InvalidArgument"`, or message containing `"persist"`); on that error the call is retried once with `requestPersist=false` instead of failing the whole session.
- **KDE prompt-free reconnect via RemoteDesktop portal v2 `SelectDevices` persistence (RemEx-mswt, CLOSED):** `xdg-desktop-portal-kde` rejects ScreenCast-level persistence on remote-desktop sessions by design, so the `SelectSources` fallback above kept capture alive on KDE but re-prompted "Share your screen?" on every connect. RemoteDesktop portal **version 2** (`xdg-desktop-portal` ≥ 1.16; Plasma 6, verified live on Plasma 6.7.2 / xdg-desktop-portal 1.22) moved session persistence to `SelectDevices` instead — `persist_mode` + `restore_token` go there, and the refreshed token comes back in the `Start` results (existing save/replay path unchanged). `LinuxPortalRemoteDesktopSessionService` now calls a new `GetPortalInterfaceVersionAsync` (plain D-Bus `Properties.Get` on the RemoteDesktop interface's `version` property; defaults to `1` — the lowest live version — if unreadable) and requests persistence in `SelectDevices` when `>= 2`, **skipping** `SelectSources` persistence in that case (KDE would reject it — RemEx-82fk). v1 portals and ScreenCast-only fallback sessions keep the legacy `SelectSources` path. **Both** the `SelectDevices` and `SelectSources` persist requests retry once without persistence on a hard rejection, so a failed persist request can never take down capture. First connect after this still prompts once to mint the token; every reconnect after that is prompt-free.

**Linux RD: NVENC engagement + per-monitor crop via post-capture cropping (RemEx-nadp, CLOSED):** Root cause: on Wayland/dual-monitor a 5120×1440 full-virtual-desktop frame exceeds NVENC's practical fast path and the stream fell back to ~13 fps CPU MJPEG. Fix (`LinuxScreenCaptureService`, `LinuxJpegEncoder`): capture always grabs the full virtual-desktop frame; both encoders take optional `cropX/cropY/cropW/cropH` params and, when non-zero, `SKImage.Subset` the frame to the active target's rect before scale/encode — private `LinuxScreenCaptureService.TryGetActiveCrop(frameWidth, frameHeight, out x,y,w,h)` computes that rect from `_activeLeft/_activeTop/_activeWidth/_activeHeight` and is called from both the PipeWire raw-capture path and the JPEG path. Per-monitor capture no longer requires the capture backend itself to target a monitor — it works on Wayland, on any display server, and survives mid-session/topology-refresh with an active PipeWire session (the old restriction — Monitor mode blocked whenever a PipeWire coordinator was running or the display server wasn't X11 — is removed). `SetCaptureCoordinator` defaults a **fresh** multi-monitor PipeWire session to `DesktopCaptureMode.Monitor` on the primary display (crop to ~2560×1440) instead of `VirtualDesktop`, to stay under the 4096px H.264 limit and land on NVENC's fast path without a CPU downscale by default — but (RemEx-lq6h) an already-chosen monitor target (explicit client selection, or one carried over from a prior connection) now **survives session reopen**: `SetCaptureCoordinator` only applies the primary-monitor default when `_activeTarget` has no still-existing display, instead of unconditionally resetting to primary on every reconnect. New KDE/Wayland display enumeration: `TryDetectWithKScreenDoctor()` runs `kscreen-doctor -o` and parses per-output geometry via `ParseKScreenDisplays`/`KScreenGeometryRegex` — tried FIRST in `DetectScreenSize()` on Wayland, ahead of xrandr, because KWin is not a wlroots compositor (`wlr-randr` fails) and XWayland's `xrandr` view of KWin outputs is unreliable for per-monitor geometry, whereas `kscreen-doctor` talks to KScreen directly and reports true per-output geometry + the priority-1 primary. **Native PipeWire framerate negotiation:** `pipewire_capture.c` (`pw_thread_func`) also adds an explicit `SPA_FORMAT_VIDEO_maxFramerate` choice-range `[1,120]` (default 120) to the `SPA_PARAM_EnumFormat` pod, alongside the pixel-format enum — without it, KWin's ScreenCast implementation serves frames on a slow, damage-driven cadence (~12 fps observed) independent of on-screen activity or encoder speed, capping the stream regardless of the NVENC/crop fixes above. Same technique OBS and gnome-remote-desktop use to get a real framerate out of KWin. **Do not drop this param when touching the EnumFormat pod** — omitting it silently reintroduces the KWin FPS cap. Verified live on CachyOS/KDE: H.264 at 67–71 FPS per monitor and 49–57 FPS on the full 5120×1440 desktop.

**Linux RD: self-healing H.264, warm portal session, drift-free absolute mouse, single cursor (RemEx-lq6h, CLOSED):** Four release-blocker fixes landed together, verified live on CachyOS/KDE across reconnects and monitor switches (477 tests pass):
- **No permanent MJPEG demotion.** `RemoteDesktopHandler`'s stream loop used to demote `_activeCodec` to MJPEG forever on the first failed encoder rebuild (any transient GPU/driver/portal churn during a monitor switch or reconnect). It now keeps the negotiated codec: on a failed rebuild it tags frames `DesktopCodecKind.Mjpeg` (safe only because envelope-capable clients — `UseFrameEnvelope` — route each frame by its own per-frame codec tag) and retries H.264 on a `h264RetryCooldownMs = 3000` cooldown (`nextH264RetryMs`), logged via `LogWarning`. Envelope-less legacy clients (no `supportsFrameEnvelope`) keep the OLD permanent MJPEG fallback — they route frames only by the negotiated codec, so a mixed stream would misroute on them.
- **Failed FFmpeg encoder probes now expire.** `FFmpegH264Encoder.ProbeCache` changed from `ConcurrentDictionary<string, bool>` to `ConcurrentDictionary<string, (bool Ok, long AtMs)>`. Positive verdicts are still cached for the process lifetime; a **negative** verdict is retried after `FailedProbeRetryMs = 30_000`. Previously one transient 5s probe timeout during display churn permanently pinned that `codec:widthxheight@fps` key to MJPEG until process restart.
- **Raw/JPEG frame caches are now signature-guarded and liveness-aware.** `LinuxScreenCaptureService._lastRawFrame` / `_lastJpegFrame` are `record`s carrying `(ActiveLeft, ActiveTop, ActiveWidth, ActiveHeight, Scale)` alongside the bytes; a cached frame is only replayed when that signature still matches the CURRENT active target (offset included — two monitors can share dimensions). Replaying a stale-target frame after a monitor switch used to desync the H.264 encoder's fixed rawvideo input size into an endless reinit storm. New `CaptureRawScreenLiveAsync` (parity with Windows' `IScreenCaptureService.IsLive`) returns `ScreenCaptureResult { Pixels, IsLive }`: a same-target replay on a static screen (PipeWire is damage-driven, so no-new-frame is expected) is `IsLive = true`; a geometry-stale cache is never replayed (`IsLive = false`) instead of silently showing the wrong screen. Raw output dimensions use `CaptureScaling.ScaledEven(baseW/H, scale)` even AT `scale = 1.0` — an odd-sized monitor/crop must still even-align or the H.264 encoder's fixed input size mismatches the buffer.
- **`LinuxCaptureSessionLifetime` keeps the portal capture session warm for the PROCESS lifetime**, not just while a client is connected — the single biggest behavior change. Closing a KDE ScreenCast session and restoring it shortly after (exactly what Android's disconnect→reconnect and monitor-switch flows do) reliably yields a stream where `pw_stream_connect` succeeds and KWin reports it valid, but **no buffer ever arrives, for minutes** — only a process restart reliably recovered. `ReleaseAsync` no longer tears down the coordinator/portal at refcount 0 (it now just decrements and returns `Task.CompletedTask`); teardown only happens on portal session loss (`OnPortalSessionLost`, which also nulls `_startTask` so the next `AcquireAsync` cold-starts) or process shutdown (`DisposeAsync`). **REGRESSION GUARD: do not reintroduce refcount-zero teardown** — it reintroduces the dead-stream bug. Side effect (intentional, documented): KDE's screen-sharing indicator stays on for the life of the agent process, honestly reflecting that the machine remains remotely controllable. Cold starts now verify first-frame production via `WaitForFirstFrameAsync` (3s timeout, consumes+returns the frame to `ArrayPool`) before wiring the stream in; a failed verification recreates the portal session **once** and re-verifies before giving up.
- **Drift-free absolute mouse injection.** New chain: `LinuxInputSimulationService.MoveMouse` (constructor now takes an optional `LinuxCaptureSessionLifetime? captureLifetime`) tries `_captureLifetime.TryInjectPointerMotionAbsolute(x, y)` FIRST — this picks the portal stream whose compositor-space rect contains the point (or falls back to `LinuxScreenCaptureService.GetVirtualDesktopBounds()` when the portal omits stream geometry), converts to stream-relative coordinates, and calls `LinuxPortalRemoteDesktopSessionService.TryNotifyPointerMotionAbsolute(streamNodeId, streamX, streamY)` — a fire-and-forget D-Bus `NotifyPointerMotionAbsolute` call on the SAME unified RemoteDesktop+ScreenCast session that owns capture. The compositor clamps natively, so there is no cumulative drift. Only when this fails (no active session — e.g. no stream yet, or on other backends) does `MoveMouse` fall back to the old relative-delta emulation (`_lastVirtualX/_lastVirtualY` tracker), which is now itself always clamped. New `RemoteDesktopHandler.ClampToActiveBounds(x, y)` clamps every absolute pointer target to `_screenCapture.GetScreenSize()` bounds **before** it reaches `IInputSimulationService.MoveMouse` on ALL platforms (not Linux-only) — a client coordinate that overshoots the streamed display can never drive the cursor onto a monitor the remote user isn't viewing, and on Linux specifically can never desync the relative-motion fallback tracker.
- **Single cursor.** `LinuxPortalRemoteDesktopSessionService` constructor gained a `PortalCursorMode cursorMode = PortalCursorMode.Hidden` parameter (was hardcoded `Embedded` in the `SelectSources` cursor_mode variant) — Android always renders its own cursor from the streamed `desktop_cursor_shape`/`desktop_cursor_state`, so a compositor-embedded cursor was a SECOND cursor baked into every frame. `Embedded` remains selectable for legacy host-composited clients.

**Android `RemoteDesktopScreen` — key facts for agents (commits `31afdcb`–`8f43b4c` and series):**
- **Unified control bar is gone.** The L/M/R click buttons, scroll, and zoom overlays were deleted (`31afdcb`). Do not re-add them to `RemoteDesktopScreen.kt`.
- **Start/Stop stream buttons live in the fullscreen overlay** (`8f43b4c`). Do not add stream start/stop controls to the non-fullscreen UI — the fullscreen overlay is the canonical home for these actions.
- **`RemoteDesktopUiState.streamRequested`** drives an immediate rotation to landscape on "Start" tap, before the stream is actually live. It is distinct from `isStreaming`.
- **`lastSentAbsHost`** is the authoritative field for the last host-coordinate pointer position. Click actions (left/middle/right) must read this field, not raw pointer position, to avoid coordinate drift (RemEx-gd7).
- **`ContentRect`** maps the actual video content area within the view box: H.264 fills the full box (stretched), MJPEG is letterboxed via `ContentScale.Fit`. Any gesture-to-host-coordinate translation must go through `ContentRect`.
- **`lastFitKey`** tracks which `"$displayToken/$streamWidth/$streamHeight/$isLandscape"` tuple has received the orientation-aware initial zoom (encodes real stream dimensions, not just display token + orientation). Landscape phone → `zoomFactor = 1.0` (whole desktop visible, pinch to zoom in). Portrait phone → fit-to-height so the full host height fills the screen and the user pans L/R. Resets to `""` on stream stop; re-fires on display switch, phone rotation, or when the host sends authoritative metadata with different dimensions (resolution/monitor/DPI change mid-stream). Gated by `desktopMetaReady` — fit does NOT fire against the 1920×1080 placeholder. `LaunchedEffect` keys on `uiState.selectedDisplayToken` so it triggers on display switch (RD-A3, RemEx-4k4, commit `ad49f1d`).
- **FPS overlay toggle is in the fullscreen control row** (RemEx-46q). `onToggleFpsOverlay` callback is wired there — do not add it elsewhere. It was orphaned when the unified control bar was removed; the fullscreen row is now the only caller.
- **Keyboard toggle is in the fullscreen overlay row** (RemEx-46q). The keyboard must be reachable in immersive mode; the fullscreen overlay is the canonical home for it alongside Start/Stop stream and FPS overlay.
- **IME re-invoke pattern — `toggleRemoteKeyboard` lambda** (RemEx-46q): use `InputMethodManager.showSoftInput()` as belt-and-braces alongside `keyboardController.show()`. A hidden `BasicTextField` retains Compose focus after a back-gesture IME dismiss, so a bare `requestFocus()/show()` is a no-op. The platform `InputMethodManager` call forces the IME up reliably in that state.
- **Quality presets rebuilt to spec — `DesktopPreset` / `DESKTOP_PRESET_BUNDLES`** (RemEx-vj31, `RemoteDesktopViewModel`): the old 3-chip Performance/Balanced/Crisp set (labels/semantics didn't match the product plan) is replaced by named `{quality, targetFps, scale}` bundles: **Unlimited** (100%/`DESKTOP_MAX_FPS` (360, was 120 — RemEx-z377)/100% scale — the uncapped ceiling this pipeline can't always sustain; persistent "may vary" info caption + one-time Snackbar overflow warning on first select), **Smooth & Sharp** (95%/120fps/50% scale, **new default**), **Balanced** (85%/60fps/75% scale), **Data Saver** (60%/30fps/65% scale), **Custom** (reveals raw quality/fps/scale sliders — the scale slider is new; previously scale was only reachable via presets). Always call `applyDesktopPreset(preset, quality, fps, scale)` to change a named preset (atomic persist+push in one call — do not set quality/fps/scale individually); `selectCustomPreset()` switches to Custom without changing the underlying values. The preset id persists in DataStore (`desktop_preset` key) alongside quality/fps/scale so the sheet reopens on the correct chip instead of re-deriving it from raw numbers (which couldn't distinguish Custom from a coincidental match); installs upgrading from the old 3-preset system (no persisted preset id yet) resolve to Custom rather than falsely claiming a named preset that doesn't match their leftover values.
- **"PC keys" modifier bar decoupled from the soft keyboard, gained Ctrl/AltGr + F-key/nav grid — `ModifierState`/`cycleModifier`/`computeChordApplication`** (RemEx-yi8o; RemEx-9krr, RemEx-bct CLOSED; `RemoteDesktopViewModel`/`RemoteDesktopScreenContent`/`RemoteDesktopChordApplication.kt`): the bar now toggles independently via `pcKeysBarVisible` (previously shown only alongside the IME, which could cover it) and renders above the IME via `imePadding()`. Shift/Ctrl/Alt/Win/**AltGr** (`VK_ALTGR = 165`/`VK_RMENU`, a fifth latching chip) are latching chips with 3-state cycling (`ModifierState`: OFF → ARMED (applies to the next keypress only) → LOCKED (held on the host until cycled back to OFF), `cycleModifier(vk)`). The modifier-wrap decision was extracted from `sendKeyPress` into a pure `computeChordApplication(modifierStates, physicallyDownModifiers, keyCode, modifierVirtualKeyCodes)` (new `RemoteDesktopChordApplication.kt`, unit-tested by `RemoteDesktopChordTest` on plain JVM — no ViewModel/DataStore/network involved). A single alphanumeric keystroke typed while a modifier is armed/locked routes as a real chord (Ctrl+C/V/A/Z, etc.) via `sendKeyPress`/`asciiAlphanumericVirtualKeyCode`/`singleCharacterInsertion`, instead of literal text; every other edit (multi-character, punctuation, autocomplete) keeps the existing text path unchanged. An expand chevron on the compact bar reveals a `FlowRow` grid of F1-F12 + Home/End/PgUp/PgDn/Insert, routed through the same plain keypress path the existing utility keys use (an armed/locked modifier wraps these for free, e.g. Ctrl+Home, with no new chord logic) — this closed out `RemEx-bct`'s remaining scope (the toggle buttons/latching modifiers/chord routing it also asked for had already shipped via RemEx-yi8o). F1-F12 labels are literal (never localized by any keyboard vendor); the 5 nav keys + AltGr + the chevron got real `cd_key_*`/`cd_more_keys` content-description strings across all 8 locales. **RemEx-rimh (fixed, ship-day polish pass):** an ARMED modifier used to silently persist onto an unrelated later keystroke whenever the next input was text injection (multi-character insert/autocomplete) rather than a plain single-character keypress, because text injection bypasses the key-event path entirely and could never consume the armed state. Fixed by `RemoteDesktopViewModel.spendArmedModifiers()`, called after every text-injection send: resets any ARMED modifier to OFF (LOCKED modifiers untouched) without emitting key events. **Not yet verified on real hardware** (RemEx-9krr is closed, but this specific verification is called out as pending in its close-reason): the Android chord sends right-Alt alone (extended-flagged, see the `WindowsInputSimulationService` entry below) — real AltGr hardware emits left-Ctrl + right-Alt together, and character-layer translation (€, @, {, etc.) may key off that combination, so this might not unlock the AltGr layer; needs confirmation on a real Windows target. If it doesn't work, file a new bead rather than reopening 9krr.
- **Windows synthetic input: `KEYEVENTF_EXTENDEDKEY` on AltGr/Home/End/PgUp/PgDn/Insert (RemEx-9krr, `WindowsInputSimulationService.IsExtendedVirtualKey`):** `KeyDown`/`KeyUp` previously sent every key with `dwFlags = 0` (down) / `KEYEVENTF_KEYUP` (up) only — no extended-key handling existed anywhere in the file. Per Win32 docs, `VK_RMENU` (0xA5, AltGr) needs this flag or Windows can't reliably tell it apart from left-Alt. Scoped to ONLY the VKs newly introduced by the AltGr chip and F-key/nav grid above — arrows/Delete/RCONTROL already ship without the flag and already work, so they are intentionally left untouched here. Linux required no change (`LinuxInputEventTranslator` already maps AltGr/F-keys/nav-keys correctly).
- **Fullscreen overlay row polish (RemEx-klq CLOSED; `RemoteDesktopScreen`):** the fullscreen top-end action row (Settings/FPS/Keyboard/PC-keys/Exit/Stop) had three near-identical inline `filledTonalIconButtonColors` blocks and repeated `16.dp`/`8.dp` padding literals; replaced with `FullscreenOverlayEdgePadding`/`FullscreenOverlayIconSpacing` constants + a shared `toggleIconButtonColors(active)` helper. Action order now matches the windowed `TopAppBar` for the actions both share (Keyboard, PC-keys, Settings, fullscreen-exit, Stop/Play); the fullscreen-only FPS toggle is slotted in without disturbing that shared order. Also fixed the fullscreen Settings button's hardcoded `"Settings"` content description to use the existing localized `R.string.cd_settings` (was never translated). **Open finding, not resolved here:** the two rows still have different action *sets* (Reset/zoom windowed-only, FPS fullscreen-only) — whether either belongs on both surfaces is an unresolved rendering/UX call.
- **1440p FPS bottleneck: CPU color-convert in `FFmpegH264Encoder`, not NVENC core (RemEx-dptu CLOSED):** Plain `h264_nvenc` appends `-pix_fmt yuv420p`, forcing ffmpeg to CPU-swscale (BGRA→YUV) every frame — ~20 ms at 1440p → ~35 fps cap while the RTX GPU idles. **Fix landed:** `FFmpegH264Encoder.Initialize()` tries `"h264_nvenc_bgra"` first in `codecsToTry`; its args feed BGRA directly to NVENC (`-c:v h264_nvenc`, no `-vf`, no `-pix_fmt yuv420p`) — NVENC ingests `bgra`/`bgr0` natively via fixed-function hardware color convert (~1 ms). Falls through to `"h264_nvenc"` (CPU path) if NVENC is unavailable. Check `ActiveCodecName` in the host log: `"h264_nvenc_bgra"` = GPU/fast (unblocks 120 fps at full res); `"h264_nvenc"` = CPU/~35 fps cap. **RULE: do NOT reintroduce `-vf hwupload_cuda,scale_cuda=format=nv12` (or any RGB→YUV CUDA filter) for the Windows GPU path.** Every prebuilt Windows ffmpeg (Gyan + BtbN) ships `--enable-cuda-llvm` whose Clang-compiled `scale_cuda` lacks the RGB→semiplanar kernel — it passes init then dies at runtime (`CUDA_ERROR_NOT_FOUND` / "Unsupported conversion: rgb0 -> semiplanar8"), producing a 0 fps black screen. The init-time codec fallback never fires because the process dies after the pipeline starts. NVENC native BGRA is the only supported GPU path on Windows.
- **Preset label strings** (`remote_desktop_preset_*`, `remote_desktop_presets_label`) live in `remex.android/app/src/main/res/values/strings.xml` (default locale only — 8 locale files still need translation; follow-up bead filed). This still applies to the rebuilt Unlimited/Smooth & Sharp/Balanced/Data Saver/Custom labels above. Do not hardcode preset label text in Kotlin/Compose. By contrast, the PC-keys-bar content-description strings (`cd_key_ctrl`, `cd_show_pc_keys`, etc.) ARE translated across all 8 locales — new user-facing strings should follow that bar, not the preset-label gap.
- **Linux H.264 codec fallback — deterministic via one-shot capability probe (RemEx-h038 CLOSED):** A rawvideo-pipe ffmpeg only opens its encoder when the first frame arrives, so the old 900ms early-exit watch in `TryStartFFmpeg` could never see an encoder-open failure — `h264_vaapi` on NVIDIA (no VAAPI *encode* entrypoints; the driver is decode-only) logged `"successfully initialized"` then died on the first real frame, silently degrading the session to MJPEG (the Linux FPS regression). `FFmpegH264Encoder.Initialize()` now calls `ProbeCodec`/`RunEncoderProbe` once per codec before starting the real pipe: it encodes a single black frame using `BuildEncoderArgs` (shared with the real run — only the output tail differs: `-frames:v 1 -f null -` for the probe vs `-flush_packets 1 -f h264 -` for the stream) and reads the exit code. Verdicts are cached in a static `ProbeCache` keyed by `codec:widthxheight@fps` (qp excluded — it never decides openability), since `RemoteDesktopHandler` reinitializes the encoder on every on-demand keyframe. The Linux codec ladder also gained the Windows NVENC-BGRA fast path and is now `h264_nvenc_bgra → h264_nvenc → h264_vaapi → libx264` (Linux half of RemEx-whfp) — NVENC ingests BGRA natively, skipping the per-frame CPU swscale that plain `h264_nvenc` pays. Verified on CachyOS/RTX 5080: the vaapi probe exits 218 (correctly skipped), nvenc probes exit 0 at 4096×1152@120. **Failed-verdict TTL (RemEx-lq6h):** `ProbeCache` is now `ConcurrentDictionary<string, (bool Ok, long AtMs)>` — positive verdicts still cache for the process lifetime, but a negative verdict is retried after `FailedProbeRetryMs = 30_000` instead of being pinned forever; see the self-healing H.264 entry above.
- **H.264 `AndroidView` must recreate on settled box geometry, not just resolution (RemEx-4fv3):** A `SurfaceView`'s content sublayer freezes its buffer→view scale at creation time. If the video `Box` (`imageSize`) is transiently smaller when the H.264 `AndroidView`'s `SurfaceView` is first created — e.g. during capture_unavailable→reconnect churn on a fresh connect, when the box is briefly ~half height — the content stays locked to that stale geometry (rendered at ~0.5x in the top-left quadrant) even after the box grows to full size. Compose re-layout (rotation) does NOT refresh a frozen content scale; only a teardown+recreate of the `SurfaceView` does. The `key()` wrapping the H.264 `AndroidView` factory must include `imageSize` alongside `streamPixelWidth`/`streamPixelHeight` so the surface rebuilds once the box settles. `imageSize` is stable during steady-state streaming (zoom/pan is applied via a separate `Modifier.layout` without a rebuild), so this does not churn the decoder.

### Post-2.0 Ship-Day UI Polish Pass (`remex.android` screens, bead RemEx-87vl)

A four-track audit (M3 motion, splash screens, user text, UX edge cases) fixed everything low-risk on the spot; deferred/riskier findings live in `docs/PRD-2.1-polish-backlog.md` — **gitignored** via the `docs/prd-*.md` pattern, so it is local-only and never appears in `git log`/`git diff`/PRs. Read it directly rather than searching git history for it.

- **Hang fixes:** `PairingScreen.kt`'s `SubmitPairingPin`/`StartPairing` now wrap in `withTimeout(15_000)` (new `pairing_error_timeout` string) — previously could hang forever with no escape; Cancel is now always enabled so it stays the user's way out of a stuck loading state. `FileTransferViewModel.loadRemoteRoots()`/browse-request gained matching 30s `CompletableDeferred` timeout watchdogs (`file_transfer_pin_timeout`), mirroring the pre-existing manage-ops timeout pattern.
- **Localization/a11y bugs:** `ConnectionScreen.kt`'s paired-status color check compared `status == "Connected"` against an already-localized display string (broken on non-English locales) — now uses the `isConnected` boolean. Hardcoded "Host discovered: X" snackbar → `host_discovered_snackbar`. `TaskManagerScreen.kt` clear-search icon and `PairingScreen.kt` error icon gained real content descriptions (were `null`/hardcoded English). All new string keys from this pass are translated in the default locale + all 8 locale files.
- **Stale-error fix:** `ConnectionViewModel` now clears `_connectionError` whenever `isConnected` emits true, so a successful (re)connect can't show "Connected" next to a leftover error card from a prior failed attempt.
- **M3 motion standardization:** `FaqScreen`, `PairingScreen`, `PersonalizationScreen`, `QrScannerScreen`, `RemoteControlScreen` AnimatedVisibility/`animateContentSize` blocks switched from ad-hoc `spring(StiffnessMediumLow)`/`tween(200)` specs to `MaterialTheme.motionScheme.fastSpatialSpec()`/`fastEffectsSpec()`. Use this pattern for any new AnimatedVisibility in these screens, not hand-picked spring/tween values.
- **Stable LazyColumn keys:** `RemoteControlScreen`/`SettingsScreen` `items(...)` calls now pass a stable `key = { ... }`. Do the same for any new LazyColumn added to these screens.
- **`SplashScreen.kt` theme + timing fixes:** title/subtitle colors were hardcoded `Color.White`/a fixed hex instead of theme-aware (`MaterialTheme.colorScheme.onBackground`, with a luminance-based fallback for the CosmicZoom subtitle) — the splash previously assumed a dark background always, breaking on light themes. Haptic "thud" is now gated to `splashStyle == "CosmicZoom"` only (RemexCommand has no lightning strike to sync it to). `drawStylizedRLogo` scale now multiplies by `density.density` — the raw-px canvas art wasn't scaling with the dp-sized text and rendered ~3x too small on real (non-1.0x-density) devices.
- **`DashboardScreen.kt` battery fix:** `ConnectionOrbCard`'s infinite glow color transition now only runs while connected/connecting — it previously ran unconditionally, burning battery even while fully disconnected.
- **`QrScannerScreen.kt`:** QR-pairing setup failures no longer surface the raw native exception message to the user; the exception is logged instead and a generic localized error is shown.
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

This project is indexed by GitNexus as **RemEx** (15382 symbols, 31258 relationships, 300 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

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
## RemEx 2.0 Release Gate

**Status: P0/P1 GATE MET — all 14 P0 beads and all 12 original P1 beads are CLOSED.** The release is conditionally shippable on Windows. The Linux runtime-parity validation (RemEx-lr9, P1) is CLOSED — runtime-validated on CachyOS. Remaining open items are out-of-scope follow-ups (RemEx-d8s client removal, RemEx-5i9 >60fps investigation, two deferred perf beads). The `REMEX_2.0_FINAL.md` ordered edit plan was deleted as stale 2.0 planning housekeeping once shipped (RemEx-z377 commit) — do not search for it; this section is now the durable record.

### Release Status Summary

| Gate | Result |
|------|--------|
| All P0 beads closed | PASSED (14/14) |
| All P1 beads closed | PASSED (12/12) |
| Linux runtime parity | PASSED (RemEx-lr9 — runtime-validated on CachyOS, closed 2026-07-01) |
| Deferred perf (RD-6/RD-7) | DEFERRED — measurement-gated, logged on bead |
| remex.desktop removal | DECIDED AGAINST (RemEx-d8s closed 2026-07-03 without deleting the folder — it's the permanent PC-side UI project) |

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
| `RemEx-46q` | Android soft keyboard could not be re-summoned after back-gesture dismissal | **CLOSED** — `isRemoteKeyboardOpen` now derived from IME insets (`WindowInsets.ime.getBottom > 0`), not focus; keyboard button calls `LocalSoftwareKeyboardController.show()/.hide()` alongside `requestFocus()` (`RemoteDesktopScreen`) |
| `RemEx-gd7` | L/M/R click buttons in Android fullscreen control bar sent clicks to `(0,0)` instead of cursor position | **CLOSED** — buttons now call `mapLocalToHost(Offset(cursorX, cursorY))`; click skipped if cursor position unavailable (`RemoteDesktopScreen`) |
| `RemEx-4k4` | First-connect mis-framing: wrong monitor dimensions on initial stream | **CLOSED** — `WarmUpCapture()` now selects WGC monitor + primes DXGI; `GetScreenSize()` serves backend-derived dims (WGC→DXGI→GDI); `ActiveMonitorOrigin()` for cursor mapping; Android `desktopMetaReady` gates initial fit; `lastFitKey` encodes real stream dimensions |
| `RemEx-nadp` | Linux/Wayland RD stuck ~13fps CPU MJPEG (NVENC idle) on full 5120×1440 dual-monitor frame | **CLOSED** — per-monitor post-capture crop (`LinuxScreenCaptureService.TryGetActiveCrop`) keeps a single monitor under the 4096px H.264 limit; `kscreen-doctor` KDE/Wayland display enumeration; native `pipewire_capture.c` `maxFramerate` request fixes KWin's ~12fps damage-driven cadence; fresh multi-monitor sessions default to primary monitor. Verified 67–71 FPS/monitor, 49–57 FPS full desktop on CachyOS/KDE |
| `RemEx-lq6h` | Linux RD: permanent MJPEG fallback after any transient encoder failure, mouse drift, doubled cursor | **CLOSED** — self-healing H.264 (3s retry cooldown, MJPEG-tagged frames for envelope-capable clients, 30s-expiring negative probe cache); `LinuxCaptureSessionLifetime` keeps the portal session warm for the process lifetime (fixes multi-minute dead-stream on reconnect); drift-free absolute mouse via `NotifyPointerMotionAbsolute` on the unified portal session + `RemoteDesktopHandler.ClampToActiveBounds` (all platforms); `cursor_mode=Hidden` removes the doubled cursor. See the Linux RD entry above for full detail |
| `RemEx-bct` | Android special-keys toolbar deferred remaining scope (F1-F12 + Home/End/PgUp/PgDn/Insert expand grid) | **CLOSED** — implemented in `RemoteDesktopScreen.kt`/`RemoteDesktopViewModel.kt`; toggle buttons, latching modifiers, and chord routing this bead also asked for had already shipped via RemEx-yi8o. Compiles clean; no device verification performed (headless env). See `RemEx-9krr` (closed, but real-hardware verification still pending per its close-reason) for the related AltGr gap |

### Deferred Beads (measurement-gated, not release blockers)

| Bead | Issue | Status |
|------|-------|--------|
| `RemEx-p0l` RD-6 | Per-frame heap allocations on DXGI capture hot path | **DEFERRED** — measurement-gated; profile under load before addressing |
| `RemEx-m3a` RD-7 | MJPEG path forces per-frame StateFlow emission → Compose recomposition storm | **DEFERRED** — measurement-gated |

### Remaining Open Follow-ups

| Bead | Priority | Description |
|------|----------|-------------|
| `RemEx-d8s` | P2 — **CLOSED** (verify with `bd show RemEx-d8s` before citing) | Was "remove remex.desktop entirely." Closed without deleting the folder — `remex.desktop` remains a live `<ProjectReference>` of `remex.agent`. Do not describe removal as pending. |
| `RemEx-5i9` | P3 — open | Android RD: investigate >60fps ceiling (DXGI capture / display-refresh bound, not codec). |
| `RemEx-87vl` (docs) | P1–P3 — open | Post-2.0 polish backlog deferred from the ship-day audit (see "Post-2.0 Ship-Day UI Polish Pass" above for what already landed). Full itemized list in `docs/PRD-2.1-polish-backlog.md` (gitignored, local file — not in git history): guided re-pair flow on cert change, Wake PC failure feedback, splash skip/rotation/Android-12 SplashScreen API, M3 success-color token, nav fade-duration consistency, host/server/daemon→"PC" terminology sweep, jargon rewrites, dead desktop resx strings. |

### Security Areas — Heightened Caution

When touching any of these files, treat as security-critical and require user sign-off:
- `remex.core/Services/Network/RemexNetworkListener.cs` — 8338 channel; `PairedClientChannelAuthenticator` gates dispatch (PROTO-1 closed); all PROTO P0/P1 beads closed
- `remex.core/Services/Network/MdnsDiscoveryService.cs` — SRV port + host validated before WebSocket URL composition (NSD-6 closed); all NSD P0/P1 beads closed
- `remex.core/Validation/CoordinateValidation.cs` — sanitizes untrusted float coordinates from remote clients; any change here affects all pointer/scroll/drag security
- `remex.agent/Services/Security/PairingService.cs` — ECDH state machine with 5-attempt cap and ~120s timeout; constant-time raw-byte HMAC implemented (PAIR-6 closed); all PAIR P0/P1 beads closed
- `remex.agent/Services/Security/CertificateService.cs` — SPKI hash management; PFX file permissions 0600 (PAIR-3 closed)
- `remex.agent/Services/Security/PairedClientRegistry.cs` — proof-of-possession reconnect auth implemented (PAIR-1 closed); reconnect-secret file 0600 (PAIR-4 closed)
- `remex.agent/HostBootstrapper.cs` — `EvaluateDesktopAuth` enforces paired clientId + protocolVersion ≥ 2 for `/ws/desktop` (PAIR-5 closed, PROTO-2 closed)
- `remex.core/Native/JniHelper.cs` + `AndroidNativeExports.cs` — JNI-1/2/3/4/5 all closed; export guard catches managed exceptions before escaping `[UnmanagedCallersOnly]`
- `remex.android/app/src/main/java/com/clindsay94/remex/security/PinnedHostStore.kt` — Tink AES-256-GCM AEAD encrypted SPKI hashes and PAIR-1 reconnect secrets; two DataStores; Android Keystore-backed; corruption self-recovery. Changes here affect all client-side certificate pinning and reconnect auth.
- `remex.agent/Services/Security/TransportTrust.cs` — gates pairing PIN auto-fetch to loopback and verified Tailscale tunnels; must stay in sync with Android's `TransportTrust.kt`; breakage silently opens PIN to LAN callers or breaks tunnel auto-fill.
- `remex.agent/Services/Session/WindowsInteractiveSessionGuard.cs` — calls `SetThreadExecutionState` to keep the interactive session alive (no sleep/lock) while a remote-desktop stream is engaged; no `tscon`/`WTSDisconnect`. Feature is off by default (gated by `ISessionKeepUnlockedService` flag); while engaged the screen will not auto-lock. Changes to engage/disengage logic require explicit user sign-off.

### Cross-Platform Rule for All Orders

Every order touching Windows ACL APIs (`PipeSecurity`, `WindowsIdentity`, `FileSecurity`, SIDs) **must** be guarded with `OperatingSystem.IsWindows()` and include a Linux branch using `UnixFileMode`/`SetUnixFileMode 0600` (owner-only). Linux runtime validation is complete — runtime-validated on CachyOS (RemEx-lr9, closed).

### Design Decisions — Resolved 2026-06-22 (user-confirmed)

- **8338 channel (PROTO-1): AUTHENTICATED-REMOTE.** Keep `IPAddress.Any`; do NOT bind loopback. The Android client connects from a SEPARATE device over the network, so loopback-binding would break all remote access — the product's core purpose. (Historical note: this decision predated RemEx-aep, which made the host an elevated in-session app; pre-login operation is no longer a goal, but the not-loopback conclusion stands because the client is always remote.) Fix: require a paired-client identity (`PairedClientRegistry` token) before `ExecuteCommandAsync` dispatch. **`PairedClientChannelAuthenticator` implements this — CLOSED.**
- **`remex.desktop` removal (bead `RemEx-d8s`): CLOSED without deleting the folder.** `remex.agent` still references `remex.desktop.Services` in `Program.cs`, `StartupRegistrationService.cs`, `SessionKeepUnlockedService.cs`, `DesktopIconExtractionService.cs`, and has a real `<ProjectReference>` to `remex.desktop.csproj`. Current, intended end state: `remex.desktop` stays as the permanent PC-side UI project, compiled into `remex.agent`. Do not treat this as a still-pending removal.

### Definition of Done (release)

Every P0 and P1 bead closed — COMPLETE. Remaining criteria for final sign-off:
- Green build on Windows (verified), Linux (CachyOS via `build-remex.ps1` — compile-verified; runtime-validated, RemEx-lr9 closed), and Android (`scripts/android-fresh.ps1`)
- Green tests (`dotnet test Remex.sln`) — 428+ pass on Windows (includes 8 `DuplicationReinitThrottle` unit tests, `RemoteDesktopHandlerTests` for `IsLive = false` stale-replay path, `BgraFrameConverterTests`, and `DesktopCursorBinaryEnvelopeTests`)
- Cross-platform parity verified for all ACL/file-permission/native code (Windows + Android verified; Linux runtime-validated on CachyOS, RemEx-lr9 closed)
- `CHANGELOG.md` updated under `Security`/`Fixed`/`Changed`
- `protocolVersion` bump coordinated only if a wire-format break is taken

<!-- END AUTO-MANAGED -->

<!-- MANUAL -->
## Custom Notes

Add project-specific notes here. This section is never auto-modified.

<!-- END MANUAL -->

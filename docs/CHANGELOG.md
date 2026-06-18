# Changelog

All notable changes to RemEx will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Added
- Remote desktop: **opt-in "keep session unlocked while connected"** (off by default, Windows). When enabled, RemEx keeps the signed-in session usable for the life of a remote-desktop connection — reconnecting a disconnected/locked session to the console (via `tscon`, run by the Session-0 service) so input works even after a Microsoft "Windows App" (RDP) client disconnects and locks it, holding off idle-lock, and re-locking (disconnecting) the session when the last client disconnects. It is security-sensitive — while engaged it unlocks the PC without a password — so it stays disabled unless explicitly turned on, engages only for an authenticated remote-desktop stream, and audit-logs every unlock/re-lock. Currently enabled via a `ProgramData\RemEx\keep-session-unlocked.flag` file containing `1`; an in-app toggle with a security warning is a follow-up. Pure decision logic (`SessionGuardPolicy`) is unit-tested. (`IInteractiveSessionGuard`, `WindowsInteractiveSessionGuard`, `SessionGuardSettings`, `RemoteDesktopHandler`.)
- Remote desktop now **confines the host cursor to the streamed display** while a single monitor is being streamed, so the pointer can't wander onto another monitor the remote user can't see (where it would just disappear). Implemented host-side via Win32 `ClipCursor`, re-applied on each cursor tick (Windows releases the clip on display/desktop/foreground changes) and released when streaming stops, cancels, or the client disconnects. No-op when streaming the full virtual desktop, and on Linux. (`IInputSimulationService`, `WindowsInputSimulationService`, `RemoteDesktopHandler`.)
- Remote desktop now **pans to follow the cursor while zoomed in**: when you zoom past 1× and the host cursor nears the edge of the view, the picture glides to keep it on screen (edge-deadzone tracking that mirrors the Microsoft Windows App), instead of letting the cursor disappear off the edge. With this in place, the remote-desktop screen **no longer forces landscape** — it rotates with the device like the rest of the app. (`PanFollowCalculator`, `RemoteDesktopScreen`.)
- Remote desktop now defaults to **120 FPS** streaming on a fresh install across both the Windows/Linux host (`DesktopConfig`, `RemoteDesktopHandler`) and the Android client (`RemoteDesktopConfigState`, `SettingsManager` DataStore defaults, connection-screen slider). High-refresh phones get the full frame rate with no manual settings change.

### Changed
- Remote desktop cursor now moves **smoothly instead of stepping**: the host streams the cursor position at ~60 Hz (up from 10 Hz; the cursor shape sync and the `ClipCursor` confinement stay throttled to ~10 Hz to avoid hammering the OS), and the Android client animates the cursor overlay toward each received position with a critically-damped spring, snapping on (re)appearance or display switch so it never slides across the screen. (`RemoteDesktopHandler`, `RemoteDesktopScreen`.)
- Remote desktop host frame pacing rewritten to a **hybrid precision wait**: it coarse-sleeps via `Task.Delay` for the bulk of each frame interval, then busy-spins with `Thread.SpinWait` for the final few milliseconds. A bare `Task.Delay` rounds up to the OS timer resolution (~15.6 ms on Windows), which oversleeps an 8.33 ms (120 FPS) interval to ~15.6 ms and capped the achievable rate near 60 FPS. The new pacing is fully localized — no global timer changes (`timeBeginPeriod`) — so it also benefits Linux pacing.

### Fixed
- Remote desktop: the interactive input host is now relaunched into the **currently active console session** rather than being assumed present whenever a host exists in *any* session. Previously a GUI host left behind in a disconnected session — e.g. after a Microsoft "Windows App" (RDP) client disconnected — counted as "already running", so RemEx never spawned a working host in the session that actually owns the input desktop, and remote input stayed dead. The launcher now checks the host is in `WTSGetActiveConsoleSessionId()`'s session. (Partial fix for the RDP-disconnect input failure; the *locked*-session case is addressed by the opt-in keep-session-unlocked feature.) (`InteractiveDesktopHostLauncher`, `WindowsActiveSession`.)
- Remote desktop: the H.264 video no longer **stretches/squishes to fill the screen** — it now letterboxes to preserve the source aspect ratio, so a landscape desktop viewed in portrait looks correct instead of horizontally compressed. A `TextureView` always scales its content to its bounds, so the previous `fillMaxSize` distorted the picture; it is now sized via `Modifier.aspectRatio` to the stream's dimensions (the H.264 equivalent of MJPEG's `ContentScale.Fit`), and `contentRect()` letterboxes for both codecs so input, cursor overlay, and zoom/pan stay aligned. (`RemoteDesktopScreen`.)
- Remote desktop: the cursor now renders on a **secondary display positioned left of or above the primary monitor**, where it previously never appeared at all (not even the fallback arrow). The Android client overloaded a negative coordinate as a "cursor hidden" sentinel (`_hostCursorX = -1f`) and `mapHostToLocal` rejected any negative coordinate — but a monitor at a negative virtual-desktop origin has legitimately negative cursor coordinates, so a real cursor there collided with the sentinel and was dropped. Visibility is now carried as its own `hostCursorVisible` flag (the host already reports it via `IsCursorInRegion`), negative coordinates are treated as valid, and both the cursor overlay and the zoom pan-follow gate on the flag. As a result the cursor also correctly shows **only while it is on the streamed display** and hides when it moves onto another monitor. (`RemoteDesktopViewModel`, `RemoteDesktopScreen`.)
- Remote keyboard: typing **emoji and other non-BMP characters** (some CJK-extension glyphs, etc.) through the host now works instead of being garbled or dropped. `WindowsInputSimulationService.TypeText` iterated `foreach (char c in text)` and sent each UTF-16 code unit as its own `KEYEVENTF_UNICODE` down+up, so a surrogate pair went out as high-down, high-up, low-down, low-up. Windows only composes a surrogate pair when the two key-downs arrive consecutively, and the intervening key-up broke it. A surrogate pair is now emitted as both key-downs followed by both key-ups, sent in a single `SendInput` batch (extracted into a platform-neutral, unit-tested `UnicodeTextInput.BuildKeyEventGroups` helper). Ordinary BMP text — virtually all typing — is unchanged.
- Remote desktop input: a stylus/S Pen tap landing exactly on the host's **top-left pixel (0,0)** — the hot corner, Start button, or a maximized window's system menu — is no longer silently dropped, and absolute finger/mouse actions no longer fire a **phantom (0,0) corner click** when the surface isn't ready yet. The Android `mapLocalToHost` previously returned `Offset.Zero` for *both* its three not-ready/degenerate error cases *and* the legitimate host origin, so callers couldn't tell a real corner mapping from a failure. It now returns a nullable `Offset?` (null only on the error cases), and all seven call sites (stylus contact, stylus hover, absolute drag-down, absolute move, absolute tap-click, absolute long-press right-click) skip their action on null instead of sending bogus coordinates. (`RemoteDesktopScreen.kt`)
- Remote mouse/keyboard input now works against **elevated (admin) windows** such as Windows Terminal "Run as administrator". Previously the interactive GUI host — which injects input via `SendInput` — was started by the per-user HKCU Run key at **medium integrity**, and Windows UIPI silently drops input from a lower-integrity process against a higher-integrity foreground window (so control "stopped working" whenever an elevated window was focused, with `SendInput` even reporting success). On Windows, the Session-0 service now launches the GUI host with the signed-in user's **linked full-admin token** (HIGH integrity) via a new `InteractiveDesktopHostLauncher`, using `GetTokenInformation(TokenLinkedToken)` → `DuplicateTokenEx` → `CreateProcessAsUser`. Falls back to the user's default token for standard users / when no linked token exists. (Linux is unaffected — UIPI is Windows-specific.)
- Remote desktop input on secondary monitors: absolute pointer input (S Pen hover, direct-touch taps/clicks) now lands correctly on non-primary displays. The Android client already maps touch/stylus points into **absolute** virtual-desktop coordinates (`mapLocalToHost` adds the host-reported `desktopLeft`/`desktopTop`), but the host's `DispatchInput` re-added the same offset — double-applying it. On a monitor with a non-zero virtual origin this drove the cursor off-screen, so Windows clamped it to the desktop edge (the reported "hover sticks to the far-left of the primary screen on Display 2"). The host no longer re-adds the offset; the now-dead `_desktopLeft`/`_desktopTop` fields were removed. The primary monitor (origin 0,0) was unaffected, which is why the bug only showed on secondary displays.
- Remote desktop cursor now renders the **true native Windows cursor shape** on the Android client, moving smoothly even over a static desktop. The client advertises `supportsCursorState`/`supportsCursorShape` and the host streams the real cursor bitmap (`desktop_cursor_shape`, BGRA pixels, sent on change) plus lightweight live position/visibility (`desktop_cursor_state`); the client decodes the bitmap and draws it at the hotspot. Host-side cursor compositing is now gated off whenever the client supports shapes (`_drawCursor = config.DrawCursor && !SupportsCursorShape`). This fixes two regressions from the earlier host-composite-into-frame approach: the cursor intermittently disappearing, and the cursor appearing frozen while dragging over a static screen — both caused by the host only re-encoding frames when desktop pixels change, so a mouse-only move never refreshed the composited cursor. The end-to-end path required new JNI callbacks (`onDesktopCursorState`/`onDesktopCursorShape`) across the NativeAOT boundary. Legacy hosts/clients fall back to the generic-arrow overlay.
- Android build: the Gradle native-library sync and verify tasks (`syncRemexCore{Debug,Release}So`, `verifyRemexCoreIn{Debug,Release}Apk`) now locate `libRemexCore.so` under the repo-wide `artifacts/` output layout in addition to the legacy per-project `bin/` layout. Since `Directory.Build.props` enables `UseArtifactsOutput`, the NativeAOT output moved to `artifacts/bin/Remex.Core/<config>_net10.0-android_android-arm64/native/`, which the hardcoded `bin/` paths could no longer find — causing `Published Release libRemexCore.so not found` and a failed Android build even though the `.so` built successfully.
- Android build: `verifyRemexCoreIn{Debug,Release}Apk` no longer fails task-property validation on a fresh/clean build. The `apkDirectory` property was annotated `@InputDirectory`, which forced Gradle to require `build/outputs/apk/<variant>` to exist before the producing `assemble<Variant>` dependency had run, erroring with `directory ... doesn't exist`. It is now `@Internal` (the directory is produced by a dependency and the task has no outputs, so it is validated at execution time instead).

---

## [2.0.0] — 2026-06-02

### Added
- Zero-latency hardware-accelerated H.264 video streaming pipeline for remote desktop, utilizing host hardware encoders on Windows/Linux (NVENC, QSV, AMF, VAAPI, libx264) and client hardware decoders (`MediaCodec`) on Android.
- Universal compatibility fallback that gracefully degrades to the high-performance MJPEG streaming pipeline if FFmpeg or hardware encoders are absent or fail on the host.
- Zero-latency Annex B packet slicer on the host utilizing Access Unit Delimiter (AUD `0x00 0x00 0x00 0x01 0x09`) markers for sub-millisecond frame slicing.
- Direct zero-copy surface composition on Android using Compose `AndroidView` interop with `TextureView` and hardware native `Surface` decoding.
- Native cross-platform clipboard support in the desktop client to copy the canvas dashboard snapshot bitmap directly onto the system clipboard.
- Modernized Android application launcher icon (`ic_launcher` and themed monochrome variant), replacing the generic Android green bot template with a premium cyber-dark grid background and electric-gold lightning bolt foreground vector drawables.
- Injected a gorgeous high-tech colorful ANSI ASCII startup banner inside the `Remex.Host` initialization sequence to display active ports, host platform details, and startup state beautifully.
- Implemented a welcome splash screen animation library in the personalization settings, enabling users to choose their preferred boot intro.
- Designed the cinematic "Cosmic Zoom" splash screen, showcasing a radiating hyperdrive starfield, a slow-zooming stenciled neon letter "R" outline, an instant high-voltage white/cyan screen flash coupled with a physical screen vibration/shudder effect, followed by the gold-orange gradient lightning bolt and full title fade-in materialization.
- End-to-end encrypted transport (TLS 1.3 / WSS) for all client-host communication
- Cryptographic device pairing replacing plaintext access keys (ECDH P-256 + 6-digit PIN)
- SHA-256 SPKI certificate pinning on client
- Remote file transfer with SHA-256 integrity verification (browse, upload, download, cancel)
- Android file-transfer hosting (shared folders on device accessible to host)
- 8 Quick Settings tiles on Android (Lock, Shutdown, Restart, Restart to UEFI, Wake on LAN, Sleep, Hibernate, Monitor Off)
- Two-stage haptic feedback on Android (sent vs acknowledged)
- Battery optimization onboarding on Android
- Firebase Crashlytics NDK integration
- Target SDK 37 (Android 17) support with Local Network permission flow
- Linux remote desktop input via Wayland portal integration

### Changed
- Remote desktop streaming pipeline optimized to decouple background frame capture producer from WebSocket send consumer via a non-blocking latest-frame buffer.
- Windows screen capture service now supports dynamic host-side cursor rendering (`drawCursor` config parameter), allowing host cursor drawing to be disabled when client drawing is enabled, completely avoiding DXGI CPU/bandwidth overhead on static screens.
- Linux screen capture service timeout caching implemented to bypass slow fallback shell tools during static PipeWire frames.
- Protocol version field added to `RemexMessage`; 1.x clients fail loudly
- Material3 dependency moved from alpha to stable
- Windows Installer (Inno Setup) updated with new branding and versioning
- Linux build scripts updated with `New-REMEX.png` icon priority
- Remote desktop pointer batches now use flattened JSON structure for efficiency

### Fixed
- Agent fails to reliably reclaim the canonical port (5005) after the GUI host exits: `AgentCoordinator.StartWebHostAsync` now polls for port availability (up to 30 s) before calling `HostBootstrapper.CreateApplication`, preventing the port-fallback loop from silently drifting onto 5006+ during the GUI host's socket TIME_WAIT window. A belt-and-suspenders warning is logged if the bound port is not canonical. The partial-failure path (exception during `StartAsync`) now disposes and nulls `_app` so the idempotency guard resets and subsequent reclaim attempts can create a fresh instance.
- Parameter-binding errors and path separator compatibility issues in `build-remex.ps1` build script when running on Windows (PowerShell Core) and Linux.
- Settings view freeze on Linux (UI-thread marshalling)
- SavedStatus continuation off UI thread
- DiscoverHostsAsync HostAddress assignment off UI thread
- async-void crash hazard in `OnShowSetAlertRequested`
- Sensor `AlertTriggered` event subscription leak on reconnect
- Duplicate XAML style block in `CanvasView.axaml`
- `RefreshSensors` running on every Settings open/close
- Hardcoded "Sort by:" string in `TaskManagerScreen`
- Snapshot clipboard copies file path; redesigned as "Copy Path" with accurate label
- Remote desktop input from Android on Linux (Wayland pointer events now injected correctly)
- S-Pen hover event crash on Android
- Linux xrandr parser robustness for exotic display configurations
- Client pairing state now persists across restarts; paired client IDs survive reconnect
- All 11 high-severity security audit findings resolved

### Security
- Plaintext access keys are no longer transmitted on the wire
- DataStore exclusion from Auto Backup verified via `data_extraction_rules.xml`
- Network security config disables cleartext traffic on Android
- ECDH curve switched to NIST P-256 (built-in .NET) for better stability over NSec/X25519
- First-time pairing now requires explicit trust gesture; unknown certificates fail closed
- WebSocket authorization gate enforces pairing on all `/ws/desktop` connections

---

## [1.11.0] - 2026-04-15

### Added
- **Haptic Feedback System:** New vibration feedback for all Android interactions
- **Theme Variants:** CyberNOC, Monolith, SolarFlare premium themes
- **Dynamic Color Generator:** Intelligent color scheme generation for themes
- **LinuxInputSimulationService:** Complete Linux input simulation implementation
- **Enhanced DesktopMeta:** Extended platform-specific metadata support
- **Mouse Overlay Improvements:** Better visibility and responsiveness controls
- **Screen Categories:** Improved organization of remote control screens
- **Expanded Localization:** 1,660+ new string resources (full coverage for 8 languages)

### Changed
- **SettingsManager:** Complete architectural refactor for better persistence
- **RemoteDesktopScreen:** Major UI overhaul with optimized touch handling
- **RemoteControlScreen:** Enhanced responsiveness and visual feedback
- **RemoteMouseScreen:** Performance improvements with reduced re-renders
- **Theme System:** Improved color management and consistency
- **UI Layer:** Better null safety and input validation throughout
- **WindowsInputSimulationService:** Comprehensive refactor for improved robustness

### Improved
- Android touch gesture recognition and accuracy
- Desktop client rendering performance
- Cross-platform localization consistency
- Settings persistence and data integrity
- Remote desktop stream responsiveness
- Input timing and synchronization

### Fixed
- Various UI re-render performance issues
- Settings migration from v1.10.0
- Theme loading consistency
- Null reference handling in ViewModels
- Input validation edge cases

### Technical Details
- **Files Modified:** 56
- **Lines Added:** 6,260
- **Lines Removed:** 3,436
- **Net Change:** +2,824 lines
- **Android versionCode:** 11
- **Commit:** 4fb723b

---

## [1.10.0] - 2026-03-XX

### Added
- Full Linux integration with native capture and telemetry services
- QR Code pairing for instant device configuration
- Glassmorphic dashboard with dark glass design
- 8-language support with live localization switching
- Interactive 9-page OS-adaptive tutorial
- Free-form 4,000x4,000 canvas for sensor card arrangement

### Key Features
- GPU-accelerated remote desktop streaming
- HWInfo (Windows) and lmsensors (Linux) integration
- Optional shared-secret authentication
- Strict input validation across all network layers
- Async/await patterns with null safety

---

## [1.9.0] - 2026-02-XX

### Added
- Production readiness audit and hardening
- Comprehensive validation guidelines
- Enhanced error handling and recovery

---

## Version Support

| Version | Status | Release Date | End of Support |
|---------|--------|--------------|----------------|
| 2.0.0 | **Current** | Jun 2, 2026 | Jun 2, 2027 |
| 1.11.0 | Maintained | Apr 15, 2026 | Apr 15, 2027 |
| < 1.11.0 | Unsupported | - | - |

---

## Release Process

Releases follow [Semantic Versioning](https://semver.org/):
- **MAJOR** version for incompatible API changes
- **MINOR** version for new functionality (backwards compatible)
- **PATCH** version for bug fixes

See the entries above for the latest 2.0 release notes and support status.

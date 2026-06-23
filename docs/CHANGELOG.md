# Changelog

All notable changes to RemEx will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Added
- Remote desktop: **opt-in "keep session unlocked while connected"** (off by default, Windows). When enabled, RemEx keeps the signed-in session usable for the life of a remote-desktop connection — reconnecting a disconnected/locked session to the console (via `tscon`, run by the Session-0 service) so input works even after a Microsoft "Windows App" (RDP) client disconnects and locks it, holding off idle-lock, and re-locking (disconnecting) the session when the last client disconnects. It is security-sensitive — while engaged it unlocks the PC without a password — so it stays disabled unless explicitly turned on, engages only for an authenticated remote-desktop stream, and audit-logs every unlock/re-lock. Currently enabled via a `ProgramData\RemEx\keep-session-unlocked.flag` file containing `1`; an in-app toggle with a security warning is a follow-up. Pure decision logic (`SessionGuardPolicy`) is unit-tested. An **in-app Settings toggle** (Windows-only, off by default) now turns it on/off, with a prominent, fully-localized (8-language) security warning shown while enabled; the toggle writes the `ProgramData` flag via a new `ISessionKeepUnlockedService`. Live unlock/re-lock still requires on-PC verification. (`IInteractiveSessionGuard`, `WindowsInteractiveSessionGuard`, `SessionGuardSettings`, `ISessionKeepUnlockedService`, `SettingsViewModel`, `SettingsView`, `RemoteDesktopHandler`.) (RemEx-l6o)
- Remote desktop now **confines the host cursor to the streamed display** while a single monitor is being streamed, so the pointer can't wander onto another monitor the remote user can't see (where it would just disappear). Implemented host-side via Win32 `ClipCursor`, re-applied on each cursor tick (Windows releases the clip on display/desktop/foreground changes) and released when streaming stops, cancels, or the client disconnects. No-op when streaming the full virtual desktop, and on Linux. (`IInputSimulationService`, `WindowsInputSimulationService`, `RemoteDesktopHandler`.)
- Remote desktop now **pans to follow the cursor while zoomed in**: when you zoom past 1× and the host cursor nears the edge of the view, the picture glides to keep it on screen (edge-deadzone tracking that mirrors the Microsoft Windows App), instead of letting the cursor disappear off the edge. With this in place, the remote-desktop screen **no longer forces landscape** — it rotates with the device like the rest of the app. (`PanFollowCalculator`, `RemoteDesktopScreen`.)
- Remote desktop now defaults to **120 FPS** streaming on a fresh install across both the Windows/Linux host (`DesktopConfig`, `RemoteDesktopHandler`) and the Android client (`RemoteDesktopConfigState`, `SettingsManager` DataStore defaults, connection-screen slider). High-refresh phones get the full frame rate with no manual settings change.

### Changed
- Project `README.md` rewritten from a one-line stub into a comprehensive, visually polished landing page — hero/badges, "What's New in 2.0" matrix, feature tour, a color-styled Mermaid architecture diagram, protocol/security/theme tables, system-requirements + getting-started + build instructions, a Documentation hub linking the `docs/` set, project layout, and roadmap. It is the entry point new users reach from the RemEx app. Logo stored at the stable `docs/assets/remex-logo.png` path (decoupled from the legacy `Remex.Client/` asset folder).
- Remote desktop cursor now moves **smoothly instead of stepping**: the host streams the cursor position at ~60 Hz (up from 10 Hz; the cursor shape sync and the `ClipCursor` confinement stay throttled to ~10 Hz to avoid hammering the OS), and the Android client animates the cursor overlay toward each received position with a critically-damped spring, snapping on (re)appearance or display switch so it never slides across the screen. (`RemoteDesktopHandler`, `RemoteDesktopScreen`.)
- Remote desktop host frame pacing rewritten to a **hybrid precision wait**: it coarse-sleeps via `Task.Delay` for the bulk of each frame interval, then busy-spins with `Thread.SpinWait` for the final few milliseconds. A bare `Task.Delay` rounds up to the OS timer resolution (~15.6 ms on Windows), which oversleeps an 8.33 ms (120 FPS) interval to ~15.6 ms and capped the achievable rate near 60 FPS. The new pacing is fully localized — no global timer changes (`timeBeginPeriod`) — so it also benefits Linux pacing.
- The two divergent local-IPC stacks are **collapsed into one**: `LocalIpcServerService` over a single `RemExLocalIPC` pipe (one framing, one ACL, one shared pipe-name constant). The redundant `IpcHostServer` / `RemexIPC` newline-protocol server and its duplicate hosted-service registration are removed, with `LaunchApp` folded into the unified command dispatch (cross-session via `IAppLauncherService`). (IPC-4 RemEx-irl; IPC-5 RemEx-qg2 absorbed.)

### Fixed
- Pairing, Windows: the **pairing PIN now displays (and pairing succeeds) when RemEx.Host runs interactively** as the signed-in user, not only when it runs as the LocalSystem service. The local-IPC privileged-command gate (`GETPAIRINGPIN`, `GENERATEPAIRINGPIN`, `STARTPAIRING`, shutdown/restart, …) resolves "the interactive console user" via `WTSQueryUserToken`, which requires the `SE_TCB_NAME` privilege that only LocalSystem holds. When the merged Avalonia host was launched directly by the user (e.g. with the Session-0 service stopped), that lookup failed with error 1314 (`PRIVILEGE_NOT_HELD`), the gate could not identify the console user, and it rejected **every** privileged command — so the host UI's 2-second `GETPAIRINGPIN` poll was denied, no PIN was ever shown, and Android pairing failed with "invalid PIN". The gate now falls back to the host process's own identity **only when that process is itself running in the active console session as a real (non-system) logon** — secure because such a host *is* the signed-in console user — while the LocalSystem-service path is unchanged. A host in a non-console session (RDP, fast-user-switch, Session 0) still cannot authorize another session's identity. Regression introduced by the IPC-1 pipe-hardening (`fe71162`). (`LocalIpcServerService.IsConnectedClientInteractiveUser`, `TryGetSelfAsActiveConsoleUserSid`.) (RemEx-dqj)
- Pairing, Windows: **completing a pairing while RemEx.Host runs interactively no longer throws and fails to persist**. Hardening the paired-clients store (`paired_clients.json`, which holds per-client reconnect secrets) set the file *owner* to LocalSystem; assigning an owner other than yourself requires `SeRestorePrivilege`, which the host lacks when launched by the signed-in user rather than the LocalSystem service — so `SetAccessControl` threw `InvalidOperationException` ("The security identifier is not allowed to be the owner of this object"). That exception type was outside the method's "best-effort" catch, so it escaped `PersistToDisk` → `RegisterClient` and aborted pairing persistence (and failed 11 host tests). The store now takes an *assignable* owner (the current user when not running as LocalSystem, LocalSystem when it is) and grants full control to LocalSystem + Administrators + (interactively) the signed-in user; the best-effort catch now also swallows `InvalidOperationException` so permission-hardening can never block pairing. (`PairedClientRegistry.RestrictStorePermissionsWindows`, `RestrictStorePermissions`.) (RemEx-sgj)
- Android, discovery: the app **no longer crashes ("RemEx has stopped") during mDNS service discovery**, which manifested as the connection "session closed" / crash-restart loop on every connect attempt. `NsdDiscoveryManager.resolveServiceModern` (the Android 14+ `ServiceInfoCallback` path) handed `NsdManager` a single-thread `Executor` and shut it down in `cleanup()` immediately after `unregisterServiceInfoCallback()`. Because unregister is asynchronous, `NsdManager` still posts a final callback (e.g. `onServiceInfoCallbackUnregistered`) to that executor afterward; the executor's default `AbortPolicy` then threw `RejectedExecutionException` on the system `ConnectivityThread` — an uncatchable FATAL that killed the whole process. The executor is now a single-thread `ThreadPoolExecutor` configured with `DiscardPolicy`, so a task posted after shutdown is silently dropped instead of crashing the app. (`NsdDiscoveryManager`.) (RemEx-0ov)
- Android, pairing: **reconnect now authenticates** instead of every request being rejected as unpaired ("pairing handshake required" / host "awaiting proof-of-possession"). The 2.0 host hardening (PAIR-1) requires proof-of-possession on reconnect — the host challenges a returning client with a nonce and expects `HMAC-SHA256(reconnectSecret, nonce)` back — and the host + C# native client implement it, but the **Kotlin layer never persisted the reconnect secret** that pairing returns (`OK:hostId|spki|reconnectSecret`) nor supplied it on connect. So the native client had no secret, `RespondToReconnectChallenge` early-returned, no `ReconnectProof` was sent, and the host rejected every `process_list_request`. (Pre-2.0 a bare clientId authenticated, which is why opening Task Manager — sending the first command — appeared to "do the handshake".) `RemexClientManager` now persists the secret per-host (Tink-encrypted via `PinnedHostStore`) on pairing and includes it as `reconnectSecret` in the native init request on connect. Clients paired before this fix must re-pair once to store the secret. (`RemexClientManager`, `PinnedHostStore`.) (RemEx-xuo)
- Remote desktop, Windows: **DXGI Desktop Duplication no longer hard-locks the host to a black screen** (black screen with only the mouse cursor visible; Ctrl+Alt+Del and the Win+Ctrl+Shift+B GPU-reset hotkey both unable to recover). When a display powered off on idle, a fullscreen app flipped, or a secure desktop appeared, `AcquireNextFrame` returned `DXGI_ERROR_ACCESS_LOST` on every frame, and `TryReinitializeDuplication` re-created the duplication on each loss while only backing off when re-init *threw*. Because the capture methods return the last cached frame on access-loss, the streaming loop counted each as a successful capture and never engaged its own failure-backoff — so `DuplicateOutput` was called at the full stream frame rate (up to 120 Hz) against a display mid power-state transition, which on some NVIDIA drivers wedged DWM (`dwmredir.dll`) and the kernel display driver. Re-initialization is now rate-limited by a new `DuplicationReinitThrottle`: one attempt per backoff window, escalating 1 s → 8 s on consecutive losses and reset by any confirmed-healthy frame (a real frame or a no-change timeout), so a powering-off display is poked a handful of times instead of thousands. Pure throttle logic is unit-tested (8 tests). (`DxgiDesktopCapture`, `DuplicationReinitThrottle`.) (RemEx-crk)
- Remote desktop input: a **finger drag that maps to the host's top-left (0,0)** no longer silently fails to move the cursor. The host's pointer bridge inferred "absolute vs relative" from `LogicalX != 0 || LogicalY != 0`, so an absolute move to the legitimate `(0,0)` corner was treated as "no coordinate" and dropped. It now prefers an explicit relative delta when present and otherwise uses the absolute logical coordinates directly (including `0,0`); contact-start presses are likewise anchored at the logical position instead of falling back to the current cursor location. (`RemoteDesktopHandler.EnqueuePointerSampleAsInputEvent`.) (RemEx-ubm)
- Remote desktop: an **H.264 decoder that fails to initialize** no longer leaves a permanently-black stream with no feedback. The Android `H264StreamDecoder` previously only logged the failure; it now signals the ViewModel, which marks streaming stopped and triggers a backoff reconnect (surfacing a clear "connection lost" message after retries). (`H264StreamDecoder`, `RemoteDesktopViewModel`, `RemoteDesktopScreen`.) (RemEx-x0b)
- Remote desktop: a **display/target switch where the host is slow to produce frames** (StartDesktopStream returns but no frames arrive and no error is emitted) now self-recovers. The Android client arms a frame-arrival watchdog on stream start, reset by every decoded frame, that triggers a reconnect after ~7 s of silence — covering the previously-unrecoverable "silent black screen, no error" case. (`RemoteDesktopViewModel`.) (RemEx-5t4)
- Remote desktop over a VPN: the **local-network discovery permission prompt no longer reappears on a loop over an active connection**. The reconnect self-healing ran mDNS discovery (which triggers Android's "RemEx wants to connect to a device on your local network" prompt) even on Tailscale/CGNAT and public addresses where local multicast can't reach the host; it now runs only for private-LAN/link-local/hostname targets, and manual discovery is suppressed while already connected. (`RemexClientManager`, `ConnectionViewModel`.) (RemEx-fkz)
- Remote desktop: a **stale frame from a previous stream target can no longer ship after a target switch**. The host send loop now drops any buffered frame whose `StreamSerial` no longer matches the session's current serial (host-authoritative), closing the window where the capture thread could swap an old-serial frame in just after the buffer was cleared. (`RemoteDesktopHandler`.) (RemEx-gim)
- Windows autostart: the legacy per-user **HKCU `Run` "RemEx" launch-at-login entry is no longer created and is proactively removed**. The Session-0 service now owns elevated autostart (it spawns the HIGH-integrity GUI host); a lingering Run key started a competing MEDIUM-integrity instance that won the single-instance guard and reintroduced the UIPI input block. `StartupRegistrationService` no longer reports launch-at-login as user-managed on Windows (the GUI toggle hides), and the interactive host removes any legacy entry on startup. Linux XDG autostart is unchanged. (`StartupRegistrationService`, `Program`.) (RemEx-hmk)
- Remote desktop: a **codec switch (H.264 ↔ MJPEG) no longer briefly misroutes frames**. The client now opts into the host's per-frame envelope (`supportsFrameEnvelope`) — a small `RDXF` header tagging each frame with its codec and stream serial — and routes every frame to the decoder that produced it, instead of by a separately-updated "active codec" flag that could lag a frame behind. A JPEG fed to the H.264 decoder (or vice-versa) during the switch window previously produced a silent black gap. Legacy hosts that send untagged frames still work (the client falls back to negotiated-codec routing). Target switch stays disabled, so the stream serial is never reset mid-flight. (`RemoteDesktopFrameEnvelope`, `RemoteDesktopViewModel`, `DesktopFrameEnvelope`.) (RemEx-w5v)
- Remote desktop: host-generated stream errors (capture unavailable/stopped, target unavailable, display-switch unsupported, runtime unavailable) are now **shown in the user's language** instead of English-only. The host tags `errorText` with a stable `DesktopErrorCodes` code (backward-compatible — the English text remains the fallback and the native client forwards the field unchanged), and the Android client maps the code to a localized string (8 languages), substituting the frame count where applicable. Rich Windows capture diagnostics remain untranslated by design. (`DesktopErrorCodes`, `RemoteDesktopHandler`, `RemoteDesktopViewModel`, Android `strings.xml`.) (RemEx-728)
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
- Native JNI boundary: a **pending Java exception is now cleared before any further JNI call** at the managed↔unmanaged export boundary, and the export error path is fully non-throwing (degrade to a pre-serialized constant / `IntPtr.Zero`), preventing `SIGABRT` / NativeAOT-runtime aborts of the Android app when a marshalling call such as `GetStringChars` faults. (`JniHelper.ReadJString`, `AndroidNativeExports.Export`.) (JNI-1 RemEx-e3z, JNI-2 RemEx-9m1)
- Remote desktop: a **stalled hardware encoder or slow client no longer freezes the whole stream**. The host encoder's blocking stdin write on the capture thread is replaced with a bounded drop-frame `Channel` feeding a dedicated async writer that observes cancellation, so disconnect tears the writer down promptly and back-pressure drops frames instead of hanging. (`FFmpegH264Encoder`, `RemoteDesktopHandler`.) (RD-1 RemEx-ii3)
- Remote desktop: **unbounded encoder memory growth** when the consumer falls behind is fixed — the encoded-frame queue is a bounded drop-oldest `Channel` and the Annex-B accumulator is capped (reset past 8 MB without an AUD cut, guarding against malformed/desynced input). (`FFmpegH264Encoder`.) (RD-3 RemEx-fs5)
- Remote desktop: the ffmpeg **stderr reader is cancelled and torn down with the encoder**, so it can no longer touch a disposed logger/process or accumulate orphaned reader tasks during encoder-rebuild churn (e.g. dragging the quality slider). (`FFmpegH264Encoder`.) (RD-4 RemEx-aa0)
- Remote desktop: **dropped decoder input frames no longer cause ~1 s of green corruption**, and a desynced decoder recovers. The Android decoder uses `MediaCodec` async-callback mode with a bounded input queue (no silent drops) and requests a fresh keyframe on error/desync; the host implements **real on-demand IDR** (encoder reinit with forced-IDR codec flags) in place of the previous no-op. (`H264StreamDecoder`, `RemoteDesktopViewModel`, `FFmpegH264Encoder`, `RemoteDesktopHandler`.) (RD-2 RemEx-bqc)
- Remote desktop: a **mid-stream host resolution change no longer garbles the picture** — the Android decoder is (re)created from the host-reported pixel dimensions in frame meta (authoritative after the 4096 clamp) and handles `INFO_OUTPUT_FORMAT_CHANGED` / SPS-PPS-on-IDR reconfiguration. (`H264StreamDecoder`, `RemoteDesktopViewModel`.) (RD-5 RemEx-kx4)
- Discovery: **overlapping NSD resolves no longer fail with `FAILURE_ALREADY_ACTIVE`** or leak a pending resolve — the Android resolver serializes resolves with a mutex and is cancellable on both phases (pre-API-34), fixing the race between manual discovery and reconnect self-heal. (`NsdDiscoveryManager`.) (NSD-1 RemEx-a13)
- Discovery: the host **re-advertises mDNS on network change** (DHCP renew / NIC switch / VPN up-down) instead of advertising once at startup, and **advertises on all preferred interfaces** with Linux-aware virtual-interface filtering (`docker`/`virbr`/`tailscale`/`wg`/`veth`), so multi-NIC and Linux hosts stay discoverable on the client's segment. (`MdnsAdvertisingService`.) (NSD-4 RemEx-ngs, NSD-5 RemEx-i8x)
- Local IPC is now a **length-prefixed framed protocol** (4-byte big-endian length, 1 MB cap) shared by the server and every client, so payloads over 8192 bytes and chunked writes parse correctly and oversize frames are rejected before allocation. (`RemExLocalIPC`, `LocalIpcServerService`, IPC clients.) (IPC-3 RemEx-4ic)
- Protocol-version acceptance is now a **single shared policy** (`ProtocolVersionPolicy`) applied consistently to `/ws` and `/ws/desktop`, replacing two divergent ad-hoc checks (`< 2` vs `!= "2"`). (`ProtocolVersionPolicy`, `PingPongHandler`, `HostBootstrapper`.) (PROTO-4 RemEx-4uy)
- Native JNI pairing exports are now **race-free and exception-safe**. `StartPairingNative`/`SubmitPairingPinNative` serialize all transitions of the pairing-session statics under a dedicated lock (kept separate from the high-frequency frame/callback lock) so a concurrent call from a second Java thread can no longer dispose-then-use the active `ClientWebSocket` (`ObjectDisposedException`); their jstring marshalling now runs inside the export boundary guard so a managed throw is caught instead of escaping `[UnmanagedCallersOnly]`; and the frame/string callback paths clear any pending Java exception on an allocation-failure early return so it can't bleed into the next callback on the shared dispatcher env. (`AndroidNativeExports`.) (JNI-4 RemEx-8ay, JNI-5 RemEx-85i, JNI-3 RemEx-ymb)
- Local IPC server: the backoff `Task.Delay` calls in the accept-loop catch blocks are now cancellation-guarded, so service shutdown exits the loop cleanly instead of surfacing a cancelled task from `ExecuteAsync`. (`LocalIpcServerService`.) (IPC-7 RemEx-79h)
- Local IPC client: an **ACL denial is now distinguished from "no server running"** — `RemExLocalIPC.SendCommandAsync` returns a typed permission error (and the legacy PIN query emits a diagnostic naming the exception type) instead of collapsing every failure into a generic `IPC Error`/`null`, so the UI can surface an actionable message. (`RemExLocalIPC`, `IpcPairingPinQueryService`.) (IPC-8 RemEx-b3m)
- Discovery: `ConnectionViewModel.discoverHost()` now **cancels any in-flight discovery before relaunching** (cancel-previous via a tracked `Job`), so overlapping manual + self-heal calls no longer stack NSD resolves or multicast-lock cycles. (`ConnectionViewModel`.) (NSD-3 RemEx-4bb)
- The loopback gate on `GET /pairing-pin` / `POST /start-pairing` now treats a **null `RemoteIpAddress` as non-loopback** (reject with `404`) instead of throwing `ArgumentNullException` inside the handler. (`HostBootstrapper`.) (PAIR-5 follow-up)
- Android build: the Gradle `.so` sync now **rejects a non-AArch64 / non-ELF `libRemexCore.so`** before it is packaged (validates the ELF magic, 64-bit class, and `EM_AARCH64` machine), restricts candidates to the requested configuration with case-insensitive path matching, and tracks the published `.so` as a **content-hashed task input** so an `-NoClean` build re-syncs when the library changes instead of staying `UP-TO-DATE` and packaging a stale `.so`. (`build.gradle.kts`.) (JNI-6 RemEx-hht, RemEx-l79)
- Remote desktop: the on-demand keyframe-request wire type is now the shared typed Core constant `MessageTypes.DesktopKeyframeRequest` instead of a stray host-local literal. (`RemexMessage`, `RemoteDesktopHandler`.) (RemEx-kjq)

### Security
- Pairing endpoints **gated to loopback** (PAIR-5, RemEx-a75): `GET /pairing-pin` and `POST /start-pairing` now reject any non-loopback caller with a `404` (not advertising the endpoint). Previously these disclosed the active pairing PIN — the out-of-band pairing secret — to unauthenticated remote callers, enabling remote takeover. The host tray reads the PIN over local IPC, not HTTP. (`HostBootstrapper`.)
- **PIN brute-force throttle** (PAIR-2, RemEx-lhd): the PIN verification path now counts failed attempts per session and burns the session (forcing a brand-new PIN) after 5 mismatches, the pairing session lifetime is cut from 10 minutes to 2 minutes, and a per-IP sliding-window throttle (`PairingThrottle`) bounds repeated `/start-pairing` attempts. Closes the online-grinding window against a 6-digit PIN. (`PairingService`, `PairingThrottle`, `HostBootstrapper`.)
- **Reconnect proof-of-possession** (PAIR-1, RemEx-3n6): a persisted `clientId` is no longer a bearer credential. At pairing completion the host binds each client to a 32-byte reconnect secret (the ECDH/HKDF session key) in `PairedClientRegistry`; on every reconnect the host sends a random nonce and requires `HMAC-SHA256(secret, nonce)` back, verified with `CryptographicOperations.FixedTimeEquals`, before trusting the connection. Replaying a captured `clientId` without the secret now fails. Added as **new optional handshake messages** (`reconnect_challenge` / `reconnect_proof`) — **no `protocolVersion` bump**; legacy secret-less clients are challenged and must re-pair. Requires a coordinated Android + Host release. (`PairedClientRegistry`, `PairingService`, `PairingHandler`, `PingPongHandler`, Core `PairingClient`/`RemexNativeClient`, `RemexMessage`/`PairingMessages`.)
- **Hardened `paired_clients.json` permissions** (PAIR-4, RemEx-rc4): the registry store now holds reconnect secrets, so after every write it is locked to the service identity only — Unix `0600` (owner read/write) on Linux/macOS and an ACL granting only LocalSystem + Administrators (inheritance disabled) on Windows. (`PairedClientRegistry`.)
- **Authenticated 8338 command channel** (PROTO-1, RemEx-htt): the external TCP command port — which executes shutdown/restart/sleep/lock — previously dispatched after a *server-only* TLS handshake with **zero client authentication**, so any device on the network could power-control the PC. Commands now carry a paired-client `ClientId` validated against `PairedClientRegistry` before dispatch (default-deny, mirroring the `/ws` gate); unauthenticated callers get `Unauthorized` and the connection closes, and the channel **fails closed** when no authenticator is registered. The listener stays bound to all interfaces by design (authenticated-remote — the Session-0 service must serve commands with no user logged in). (`RemexNetworkListener`, `ICommandChannelAuthenticator`, `PairedClientChannelAuthenticator`, `CommandRequest.ClientId`.)
- **8338 flood / slow-loris bounds** (PROTO-2, RemEx-4ky): the command listener caps concurrent connections via a configurable `SemaphoreSlim` (`Remex:CommandMaxConcurrent`, default 16, reject-at-capacity) and applies handshake/read timeouts (`Remex:CommandHandshakeTimeoutSeconds` / `CommandReadTimeoutSeconds`) through linked `CancellationTokenSource`s, so a slow or idle peer can no longer wedge the channel or multiply `MaxPayloadSize` allocations. (`RemexNetworkListener`.)
- **Oversize `/ws` message no longer leaks connection state** (PROTO-3, RemEx-288): a frame exceeding the 4 MB cap previously threw an uncaught `InvalidOperationException` that bypassed cleanup, orphaning the telemetry stream task and leaking file-transfer/pairing state. The serializer drains and returns null on oversize, and the handler's cleanup now runs in a `finally`. (`MessageSerializer`, `PingPongHandler`.)
- **Host private key no longer world-readable** (PAIR-3, RemEx-dta): the TLS PFX is written atomically with restrictive permissions set **before** any key bytes touch disk (Unix `0600`; Windows ACL: LocalSystem + Administrators), closing the TOCTOU window in which a non-admin local user could read the SPKI-pinned private key. (`CertificateService`.)
- **Local IPC pipe locked down + caller verified** (IPC-1, RemEx-m1i): the live `RemExLocalIPC` pipe granted **Everyone** read/write and returned the active pairing PIN (and accepted power commands) to any local user. The `Everyone` ACE is removed — the pipe grants only LocalSystem (FullControl) + Interactive (ReadWrite) on Windows (`0600` owner-only on Linux) — and secret-returning/state-changing commands additionally verify the connected client is the interactive console user via pipe impersonation. (`LocalIpcServerService`.)
- **Bounded IPC accept loop** (IPC-2, RemEx-n6u): the IPC server handles each client on its own task and re-accepts immediately, with a per-connection read timeout, so a hung/silent local client can no longer wedge the PIN/power channel. (`LocalIpcServerService`.)
- **Explicit ACL on the host-control handoff pipe** (IPC-6, RemEx-oj8): the `RemExHostControl` port-handoff pipe carries an explicit DACL (LocalSystem FullControl + Interactive ReadWrite on Windows; Unix perms on Linux) instead of the default — the Session-0 service (LocalSystem) and the HIGH-integrity interactive GUI host run in different security contexts, so this meaningfully tightens the handoff. (`HostControlServer`.)
- **Pairing acknowledgement HMAC compared as raw bytes** (PAIR-6, RemEx-29e): the host now decodes the client's acknowledgement HMAC from base64 and compares the raw fixed-length bytes with `CryptographicOperations.FixedTimeEquals`, failing closed on malformed base64 — instead of comparing the base64 *text* (encoding artifacts) and risking a throw out of the verify path. Mirrors the client side. (`PairingService`.)
- **No crypto/exception detail leaked to the pairing peer** (PAIR-7, RemEx-xk9): a malformed client ECDH key (or any failure on the pairing-complete path) now returns a generic error to the peer — the wrong-PIN message on the complete path so a crypto/parse error is indistinguishable from a bad PIN — with full detail logged host-side only, instead of interpolating `ex.Message` into the peer-facing error. (`PairingHandler`.)
- **Untrusted pointer samples clamped before integer cast** (RD-8, RemEx-q6u): incoming `DesktopPointerSample` coordinates/deltas (network-supplied floats) are validated through a new `CoordinateValidation` helper — non-finite values (NaN/±Infinity) are rejected and values are clamped to the active stream pixel bounds — before the `(int)` cast, so a hostile sample can no longer wrap into an arbitrary `MoveMouse` coordinate. (`CoordinateValidation`, `RemoteDesktopHandler`.)
- **mDNS-discovered host fields validated before URL build** (NSD-6, RemEx-00x): `MdnsDiscoveryService` now rejects an invalid SRV port and a target that isn't a valid IP/DNS name (`Uri.CheckHostName`) before composing the `ws://` URL from untrusted multicast responder data. (`MdnsDiscoveryService`.)
- **8338 `ClientId` requirement documented for external scripts** (PROTO-1 follow-up, RemEx-lbk): `docs/API_CONTRACTS.md` §4 and `docs/SECURITY.md` now accurately describe that every external-script `CommandRequest` on port 8338 must carry a paired `ClientId` (default-deny; unauthenticated senders get `Unauthorized`), replacing the prior inaccurate "same-IP within 24h" description. No first-party client uses 8338, so this is a documentation/integration concern only.

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

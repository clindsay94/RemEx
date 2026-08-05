# RemEx Regression Guards

Every rule here exists because breaking it reintroduced a real, hard-to-diagnose failure. Most of
these bugs presented as *silence* — a black screen, a dead stream, a bricked pairing — with no
exception, no log line, and nothing pointing back at the change that caused them. That is why they
are written down instead of left to code review.

**This file is hand-maintained.** It replaced an auto-generated block in `AGENTS.md` that drifted out
of sync with the code and, in one case, ended up instructing agents to do the exact opposite of what
the code does. Nothing regenerates this file, so nothing can silently overwrite it.

## Rules for editing this file

- **Anchor every claim.** Cite `path/to/File.ext:line` (or a symbol name) so the next reader can
  verify a guard in one grep instead of re-reading a subsystem.
- **Record the failure, not just the rule.** "Use X" rots into cargo cult. "Use X because Y produced
  a black screen on <device>" survives, because it tells a future reader what evidence would be
  needed to overturn it.
- **Delete a guard when the reason genuinely expires** — and say so in the commit message. A guard
  nobody can justify is worse than no guard.
- Line numbers drift. If an anchor is stale but the guard still holds, fix the anchor. If the guard
  itself no longer matches the code, **stop and work out which one is wrong** before editing either.

---

## Windows capture

- **Backend ladder is WGC → DXGI → GDI** (`WindowsScreenCaptureService`). Operator capture knobs go
  ONLY in `CaptureBackendPreference` (HKLM). `CaptureScaling` lives in `Remex.Core` — do not move or
  duplicate it.
- **`remex.agent.windows` is Windows-only WinRT isolation.** Never add cross-platform or Linux code
  there.
- **WinRT interop GUID:** `CreateForMonitor` / `CreateForWindow` take `IID_IGraphicsCaptureItem` (the
  constant in `WgcDesktopCapture.cs`), NOT the runtimeclass GUID. The wrong GUID silently returns
  `E_NOINTERFACE` and WGC falls back as if it had never been tried. Any WinRT ABI `IntPtr` crossing
  into a CsWinRT API must use `MarshalInspectable<T>.FromAbi`, never `Marshal.GetObjectForIUnknown`.
- **Never instantiate the live capture classes in tests.** `DxgiDesktopCapture`, `WgcDesktopCapture`
  and `WindowsDisplayPowerMonitor` are GPU/session-bound. Tests use `FakeScreenCaptureService`;
  `SafeHostTestDoubles.cs` registers safe doubles first in `RemexHostFactory`.

### `DuplicationReinitThrottle` — DXGI re-init backoff

`DxgiDesktopCapture` gates every `TryReinitializeDuplication` / `DuplicateOutput` call through
`DuplicationReinitThrottle` (1s base, 8s max, exponential). On `DXGI_ERROR_ACCESS_LOST` at most one
re-init is attempted per backoff window; a confirmed-healthy frame (real frame or `WAIT_TIMEOUT`)
calls `RecordHealthyFrame()` to reset.

**Failure it prevents:** the "display-off storm" that wedged DWM and the NVIDIA driver by retrying
`DuplicateOutput` at stream frame rate (RemEx-crk). Clock-injected so the backoff is unit-testable.

### `ScreenCaptureResult.IsLive` — stale-replay signal

Capture methods return `ScreenCaptureResult { Pixels, IsLive }`. `IsLive = false` means the cached
`_lastFrame` was replayed (e.g. during a `DuplicationReinitThrottle` backoff window).
`RemoteDesktopHandler` resets `consecutiveFailures` only on `IsLive = true`, so stale replays reach
the coded-error path instead of masquerading as health. The GDI path never caches and always reports
`IsLive = true`. (RemEx-hmj)

### `WarmUpCapture()` — prime before measuring

`RemoteDesktopHandler` calls `_screenCapture.WarmUpCapture()` once per client connection, before
`SendCurrentStreamBootstrapAsync`. On Windows it primes DXGI via `_dxgi.TryRecover()` **and** selects
the WGC monitor so `GraphicsCaptureItem.Size` is populated before the first `GetScreenSize()`.

**Failure it prevents:** without priming, `GetScreenSize()` on a WGC-served monitor returned DXGI/GDI
probe bounds that disagreed with the actual WGC frame — a mis-framed first connect (RemEx-4k4,
RemEx-6my). `IScreenCaptureService.WarmUpCapture()` is a default-interface no-op; **any new backend
with deferred init, or whose size can differ from DXGI, MUST override it.**

---

## Remote desktop stream (host)

### `PrecisionPacer` — hybrid frame pacing

Single source of truth for stream and cursor pacing
(`remex.agent/Services/RemoteDesktop/PrecisionPacer`). Coarse-sleeps the bulk of each interval, then
busy-spins with `Thread.SpinWait` for the final **~2 ms**, beating the ~15.6 ms Windows timer floor
that would otherwise cap a bare `Task.Delay(8)` at ~60 FPS instead of 120.

**The margin was 16 ms until RemEx-ccen** — larger than a whole tick at 90 Hz (11.1 ms) or 120 Hz
(8.3 ms). The coarse sleep therefore never ran and the pacer spun the *entire* interval, burning
~100% of a core for the life of every stream (measured: 99% before, 18% after).

Shrinking the margin is only safe because the coarse sleep is now a high-resolution waitable timer
(`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`, Win10 1803+). **Where that timer cannot be created the
pacer keeps the old 16 ms margin** — a small margin over a 15.6 ms-granular sleep overshoots the tick
and drops the frame rate.

The pacer owns a native handle and is `IDisposable`; both loops hold it with `using`. It runs an
absolute timeline, so a per-tick overrun shortens the *next* wait rather than accumulating drift.
Call `Reset()` after any pause or backoff so recovery doesn't burst through a backlog of missed
ticks. No global `timeBeginPeriod`. Benefits Linux pacing too.

**NEVER replace with a bare `Task.Delay` in any stream or cursor loop — the regression is silent.**
(`docs/REMOTE_DESKTOP_PERFORMANCE.md` was deleted as stale planning-doc housekeeping; this entry is
the durable record. Do not go looking for that file.)

### Windows GPU encode — `h264_nvenc_bgra` only

BGRA is fed directly to NVENC. This is the **only** supported GPU path.

**Never reintroduce `-vf hwupload_cuda,scale_cuda`.** Prebuilt Windows ffmpeg lacks the
RGB→semiplanar kernel: the pipeline passes initialization and then dies at runtime — 0 fps, black
screen, and the fallback never fires because init appeared to succeed.

### Wire magic bytes

`"RDXF"` = frame envelope. `"RDXC"` = binary cursor. **Never reuse either.** Capability additions
stay additive and gated; no `protocolVersion` bump unless the change is genuinely breaking.

### FPS ceilings

Route through `DesktopConfig.MaxTargetFps` / `PacedMaxFps` (Android mirror:
`RemoteDesktopViewModel.DESKTOP_MAX_FPS` / `DESKTOP_FPS_PACED_MAX`). Never reintroduce a hardcoded
120 or 360.

### `StreamSerial` — stale frame guard

The `RemoteDesktopHandler` send loop drops any buffered frame whose `StreamSerial` no longer matches
the session's current serial (host-authoritative). Closes the race where the capture thread swaps an
old-serial frame in just after the buffer was cleared on a target switch. (RemEx-gim)

### Keyframe throttle cooldown

Keyframe-driven encoder reinits are throttled to at most one per 5s. The first request (or the first
after the cooldown expires) triggers a real reinit plus SPS/PPS+IDR; requests inside the cooldown are
swallowed, and the decoder re-requests if still desynced. Any legitimate rebuild (target switch,
quality/fps/scale change) also satisfies the cooldown. The Stream Metrics log reports
`Throttled keyframe reinits: N` so a flood stays visible.

### Self-healing codec recovery is envelope-gated

`RemoteDesktopHandler` does not permanently demote `_activeCodec` to MJPEG on a failed encoder
rebuild. For envelope-capable sessions (`UseFrameEnvelope`) it keeps the negotiated codec, tags
frames `DesktopCodecKind.Mjpeg` while the encoder is down, and retries H.264 on a 3s cooldown
(`nextH264RetryMs`).

**This is safe only because the `RDXF` per-frame tag lets the client route each frame correctly
regardless of which codec is "active".** Envelope-less legacy clients keep the OLD permanent-demotion
behavior — they cannot route a mixed stream. `FFmpegH264Encoder.ProbeCache` pairs with this: failed
probe verdicts expire after `FailedProbeRetryMs = 30_000` (positive verdicts still cache forever), so
a transient probe failure during display churn can't pin a geometry to MJPEG until restart.
(RemEx-lq6h)

### `BgraFrameConverter` — GDI-free BGRA fast path

`TryConvertNoScale(IntPtr src, int rowPitch, int width, int height, double scale)` reads a mapped
BGRA32 staging texture (WGC or DXGI) into a tightly-packed `byte[]` via row-wise `Marshal.Copy`,
honoring GPU row pitch (which can exceed `width * 4`). Returns `null` when a downscale is needed —
the caller falls back to GDI+ bilinear. Lives in `Remex.Core`; NativeAOT-safe.

**Failure it prevents:** the previous path wrapped every frame in a `System.Drawing.Bitmap`, ran
`Graphics.DrawImage`, and copied via `LockBits` — allocating multi-MB objects **per frame**. Do NOT
reintroduce `Bitmap` or `Graphics.DrawImage` on the hot capture path. (RD-C)

---

## Linux capture / portal

### `OpenPipeWireRemote` must use the session's own D-Bus connection

It MUST be called on the same D-Bus connection that owns the portal session (the fd is
sender-scoped). A fresh connection is rejected by the portal and capture silently degrades to a
~1 FPS fallback.

### `LinuxCaptureSessionLifetime` — warm for the process lifetime

The portal capture session and its PipeWire stream are opened once and kept alive for the **process**
lifetime, not torn down when the last client disconnects. `ReleaseAsync` decrements the refcount but
never calls `StopInternalAsync` at zero.

**Failure it prevents:** closing a KDE ScreenCast session and reopening it shortly after — exactly
what disconnect→reconnect and monitor-switch do — reliably yields a stream KWin reports as valid but
which never produces a buffer, for *minutes*. Teardown now happens only on `OnPortalSessionLost`
(compositor killed it) or `DisposeAsync` (process shutdown). Cold starts verify first-frame
production (`WaitForFirstFrameAsync`, 3s) and recreate the portal session once before giving up.

**NEVER reintroduce refcount-zero teardown.** Known, accepted side effect: KDE's screen-sharing
indicator stays on for the life of the process. (RemEx-lq6h)

### Keep the `maxFramerate` choice-range in the EnumFormat pod

`SPA_FORMAT_VIDEO_maxFramerate` `[1,120]` in `pipewire_capture.c`. Dropping it silently reinstates
KWin's ~12 FPS damage-driven cadence.

### Drift-free absolute mouse via the unified portal session

`LinuxInputSimulationService.MoveMouse` first tries
`LinuxCaptureSessionLifetime.TryInjectPointerMotionAbsolute(x, y)`, which maps the point into the
active ScreenCast stream's coordinate space and calls
`LinuxPortalRemoteDesktopSessionService.TryNotifyPointerMotionAbsolute` — D-Bus
`NotifyPointerMotionAbsolute` on the SAME session that owns capture, so it is compositor-clamped with
no cumulative drift. It falls back to relative-delta emulation only when no session is active.

Separately, `RemoteDesktopHandler.ClampToActiveBounds(x, y)` clamps every absolute pointer target to
`_screenCapture.GetScreenSize()` bounds on **all** platforms, so an overshooting client coordinate can
never drive the cursor onto an unstreamed monitor. (RemEx-lq6h)

### Signature-guarded frame cache

`LinuxScreenCaptureService`'s `_lastRawFrame` / `_lastJpegFrame` carry
`(ActiveLeft, ActiveTop, ActiveWidth, ActiveHeight, Scale)` alongside the bytes. A cache is replayed
only when that **full** signature still matches the current target — the offset matters, because two
monitors can share dimensions. Raw output always uses `CaptureScaling.ScaledEven`, even at
`scale = 1.0`, so an odd-sized monitor or crop can't desync the H.264 encoder's fixed rawvideo input
size. (RemEx-lq6h)

---

## Android — H.264 decoder

`remex.android/app/src/main/java/com/clindsay94/remex/ui/screens/H264StreamDecoder.kt`

### Synchronous mode only — never async `setCallback` (INVARIANT)

The decoder is driven by a single dedicated thread,
`Thread({ runDecodeLoop() }, "H264DecodeLoop")` (`:94`, started at `:120`), which polls
`dequeueInputBuffer` / `dequeueOutputBuffer` itself (`:245`, `:199`).

Async mode (`MediaCodec.setCallback`) is **deliberately not used and must not be reintroduced.** On
this app's deferred-configure path the Qualcomm `c2` decoder reaches RUNNING with input buffers ready
but `onInputBufferAvailable` never fires, so the codec is never fed and the stream stays permanently
black.

Measured on a Galaxy S24 Ultra: **0 callbacks on the main looper AND 0 on a dedicated
`HandlerThread`.** Moving callback delivery off the main looper does *not* fix it — "just give it its
own HandlerThread" is not an escape hatch. Synchronous polling sidesteps callback delivery entirely.
Rationale is in the class KDoc at `:36-40`; the only `setCallback` token in the file is that comment,
not a call site.

> This guard previously appeared in `AGENTS.md` **inverted** — it mandated the HandlerThread that was
> measured not to work. If you find that wording anywhere else, it is wrong; delete it.

### Deferred SPS/PPS configure

`MediaCodec.configure()` is NOT called on construction. The decode thread waits for the first access
unit carrying SPS (NAL 7) + PPS (NAL 8) — an IDR — then configures with explicit `csd-0` / `csd-1`
before `start()`.

**Why:** relying on the codec to auto-detect inline SPS/PPS works on some hardware and silently
wedges others (no output, input buffers never freed, backlog fills, per-frame keyframe-request
flood). Supplying SPS as `csd-0` also forces the codec to adopt the SPS-declared resolution, making a
stale width/height hint harmless. P-frames before the first IDR are dropped silently — the host emits
an IDR every 60 frames on its own, so `onKeyframeNeeded` is not flooded during startup. (#2b)

### Mid-stream SPS reconfigure

When a later access unit carries an SPS whose raw bytes differ from the configured `csd-0` (a cheap
`containsNalType` pre-check keeps P-frames on the fast path), the decoder does
`stop()` / `configure()` / `start()` for the new resolution. This is the fix for the scale-up black
screen. (RemEx-aep)

### Forbidden `MediaFormat` keys (Surface-output decoders)

**NEVER set:**

- `KEY_COLOR_FORMAT` — Qualcomm `c2.qti.avc.decoder` rejects `COLOR_FormatSurface` (`0x7F000789`)
  with *"configureIntf failed 95 / ? is not a supported pixel format"* → zero output, a silently
  black stream, and a per-frame keyframe flood.
- `KEY_LOW_LATENCY` and `KEY_OPERATING_RATE` — these shrink the output/DPB pool to ~2 buffers. With a
  SurfaceView, output buffers are held until the Surface consumer latches them; with a 2-buffer pool
  the codec exhausts output after ~2 frames and stops offering input. Classic
  *"Works: Q:2/Done:2 then stall"* black screen.

Only `KEY_PRIORITY=0` (real-time hint, safe), `KEY_MAX_INPUT_SIZE`, `csd-0` and `csd-1` are set.
`KEY_MAX_INPUT_SIZE` is sized for the full-screen (4K-bounded, 8 MiB-capped) maximum — not the initial
hint — and `KEY_MAX_WIDTH` / `KEY_MAX_HEIGHT` are set for adaptive playback, with explicit
reconfigure as the fallback.

### Bounded input backlog

`MAX_INPUT_BACKLOG = 6` (`:16`), drop-oldest, then `onKeyframeNeeded`. The host side pairs with this:
`FFmpegH264Encoder` uses bounded `Channel<T>` (drop-newest for input, drop-oldest for output). On
overflow both ends fire a keyframe-needed callback to recover stream sync rather than accumulating
stale frames.

### Render target

Renders directly to a **SurfaceView**'s `holder.surface` — SurfaceFlinger's consumer accepts the
decoder's native graphic buffers. A TextureView's GL `SurfaceTexture` does not.

---

## Android — remote desktop UI

### SurfaceView zoom/pan MUST use `Modifier.layout`, never `graphicsLayer` (INVARIANT)

`graphicsLayer { scaleX/scaleY = zoomFactor; translationX/Y = panOffset }` does **not** scale or move
a SurfaceView's native surface. The system composites the surface at its **layout bounds**;
`graphicsLayer` is a draw-time transform affecting only the Compose placeholder rectangle.

**Symptom when violated:** the H.264 image is tiny or letterboxed and stranded in black, while input
mapping (`mapLocalToHost`), the cursor overlay, and pan-follow all track correctly — i.e. *"panning is
correct but the video renders too small / cropped."*

**Correct approach:** apply zoom/pan via `Modifier.layout { measurable, constraints -> ... }` —
measure the SurfaceView at `contentRect() * zoomFactor` and `place()` it centered plus `panOffset`.
This sizing matches `mapHostToLocal` exactly, keeping video, cursor overlay, input and pan-follow
aligned. The decode buffer is pinned by `holder.setFixedSize(streamPixelWidth, streamPixelHeight)` and
the compositor scales that fixed buffer to the layout bounds with no surface churn.

The MJPEG fallback (a Compose `Image`) still uses `graphicsLayer` correctly — only the SurfaceView
needs layout-based scaling. This was latent until fit-to-height (RD-A3) made the default zoom > 1.

### The H.264 `AndroidView`'s `key()` must include `imageSize`

Alongside the stream dimensions. A surface created against transient geometry freezes its content
scale.

### Keyboard state from IME insets, never focus

In `RemoteDesktopScreen`, `isRemoteKeyboardOpen` is derived from live IME insets
(`WindowInsets.ime.getBottom(LocalDensity.current) > 0`), NOT from `BasicTextField` focus.

**Why:** the back gesture hides the IME without clearing focus, so a focus-based approach made
`requestFocus()` a no-op and the keyboard could never be re-summoned. The toggle button calls
`LocalSoftwareKeyboardController.show()` / `.hide()` alongside `requestFocus()`, guaranteeing the IME
opens and closes even when the field was already focused.

**Never drive soft-keyboard visibility from Compose focus state** in a remote-desktop or similar
IME-controlled screen. (RemEx-46q)

### Preset changes are atomic

Go through `applyDesktopPreset(...)`. Never set quality/fps/scale individually. Stream start/stop,
keyboard and FPS toggles live in the fullscreen overlay — do not re-add a unified control bar to
`RemoteDesktopScreen.kt`.

### `mapLocalToHost` returns a nullable `Offset`

Null only on error or degenerate cases; every call site (touch, tap, cursor overlay, L/M/R click
buttons) skips its action on null. Negative coordinates are **valid** — a monitor can sit at a
negative virtual-desktop origin. Cursor visibility is carried by its own `hostCursorVisible` flag and
must never be encoded as a sentinel coordinate. (RemEx-ubm)

---

## Wire protocol and native message routing

### A new client-bound message type MUST be routed to the phone

Inbound `/ws` messages reach Kotlin **only** if `AndroidNativeExports.OnNativeMessageReceived` (in
`Remex.Core`, compiled into `libRemexCore.so`) forwards them to a JNI callback. File messages forward
by `file_*` prefix, so any `file_*` type is covered automatically — but a **non-`file_` client-bound
type still needs its own callback wiring.**

**A type the router does not recognize is silently dropped, with no error on either side.** This
exact stale-allowlist gap bricked all of v3 file transfer with *"Peer did not respond"* (RemEx-y6x6).

Always test the round trip on a real device after adding a client-bound message type. Compiling and
passing unit tests proves nothing here — the failure is in the delivery path, not the code.

### `pairing_pin_response` is deliberately NOT routed — do not "fix" it

This host→client reply is intentionally *not* routed through `OnNativeMessageReceived`. It arrives on
the pairing `/ws` socket, which only `PairingClient` reads, and is consumed synchronously as the
return value of the `FetchPairingPinNative` native export. It therefore needs no JNI callback and
**cannot** be silently dropped by construction.

**Do not add it to the router.** It looks like an oversight against the rule above; it is not. This
is recorded because the rule above makes adding it the obvious "fix". (RemEx-1t0b)

### `protocolVersion` bumps must be coordinated

`RemexMessage` carries `protocolVersion: 2`. A breaking wire-format change requires bumping it in
**both** `remex.agent` and `remex.android` and coordinating the release — a mismatch causes silent
deserialization failures, not clean errors. Non-breaking additions (new optional fields) need no
bump, but document them in `CHANGELOG.md`.

---

## Security

> **These surfaces are tightly coupled between `remex.agent` and `remex.android`. Changes here need
> explicit user sign-off and must be coordinated across both sides of the connection.** Breakage is
> silent on both ends — there is no clean error to read.

### The pairing flow is the only authentication path

`PairingHandler` + `PairedClientRegistry` implement ECDH P-256 key exchange and PIN verification.
**`PairedClientRegistry` is the ONLY authentication path in production** (non-loopback). Breaking it
silently bricks all device pairing with no clear error on either end.

### Never regenerate or rotate the host certificate silently

Android pins the host's SPKI hash at pairing time. **If the host cert changes without a re-pair, the
connection is permanently refused until the user re-pairs** — there is no recovery path from the
phone. `CertificateService` carries a brick canary: it logs Critical and refuses to regenerate when an
existing `cert.pfx` is unreadable. Do not "helpfully" clear that state.

### `TransportTrust` — PIN auto-fetch gate (both sides must agree)

Host `TransportTrust.IsTrustedForPinAutoFetch(remote, local)` and Android
`TransportTrust.canAutoFetchPin(context, host)` must agree or PIN auto-fill breaks end to end.

- **Host** allows auto-fetch when the caller is loopback, OR when **both** remote and local addresses
  are Tailscale CGNAT (`100.64.0.0/10` / `fd7a:115c:a1e0::/48`). Requiring *both* ends defeats a LAN
  attacker spoofing a `100.64.x.x` source. Handles IPv4-mapped addresses (`::ffff:100.64.x.x`) for
  Kestrel.
- **Android** allows auto-fetch for loopback, OR for a Tailscale address / `*.ts.net` MagicDNS
  hostname **AND** `TRANSPORT_VPN` active. The VPN-active check is mandatory: a Tailscale-looking
  address with no live tunnel must NOT unlock auto-fetch.

`requiresLocalNetworkAccess(host)` returns `false` for loopback/Tailscale/`*.ts.net` targets, gating
the `NEARBY_WIFI_DEVICES` / `ACCESS_LOCAL_NETWORK` runtime permission requests. Changes here silently
break Tailscale users (spurious permission prompts) or open LAN permission gates.

**Both sides are security-critical and must be kept in sync; changes require explicit user sign-off.**

### `PinnedHostStore` — Tink AEAD corruption recovery

`aead()` uses a double-checked lock. On init failure — lock-screen key invalidation, app data cleared
with the Keystore intact — it clears the `remex_tink_prefs` SharedPreferences keyset, clears both
DataStores, and retries. **Without this the app is permanently bricked.** The keyset is Android
Keystore-backed; no deprecated `EncryptedSharedPreferences` or `MasterKey` APIs.

### `PinnedHostStore` — reconnect-secret persistence

After a successful pairing, `RemexClientManager` extracts `reconnectSecret` from the
`OK:hostId|spki|reconnectSecret` result and stores it via `setReconnectSecret`. On reconnect,
`getReconnectSecret()` supplies it to `RemexCoreClient` to answer the host's proof-of-possession
challenge. **Without a stored secret the host rejects the reconnect and forces a re-pair.** Secrets
live in a dedicated DataStore (`remex_reconnect_secrets`, separate from `remex_pinned_hosts`),
encrypted with Tink AES-256-GCM AEAD using `hostId` as associated data. (PAIR-1 / RemEx-xuo)

### Proof-of-possession reconnect auth

`PairedClientRegistry` stores a 32-byte ECDH/HKDF session key per client. Reconnect auth is an
HMAC-over-nonce challenge, **NOT** a bare clientId lookup. `RegisterClient(string, byte[])` is the
production path.

### `EvaluateDesktopAuth` — pre-auth for `/ws/desktop`

`HostBootstrapper.EvaluateDesktopAuth` enforces: loopback → allow unconditionally; non-loopback →
must have a paired `clientId` (`PairedClientRegistry`) AND `protocolVersion >= 2`. Unknown or missing
clientId → 401/403. Old protocol → 400. Newer-than-host → 200 (forward compat).

### Pairing brute-force defense

`PairingService` caps failed HMAC attempts at 5 per session with a ~120s session timeout. **This is
the active protection.** The former `PairingThrottle` per-IP sliding-window class was removed in
RemEx-0xp0: its only call site was the deleted `/start-pairing` endpoint and it was never
DI-registered, so `GetService` always returned null and it never actually ran. A real per-IP
cross-session throttle on the `/ws` pairing path is tracked as a follow-up bead.

### `CoordinateValidation` — float sanitization

All absolute pointer coordinates go through `CoordinateValidation.ClampAbsolute(float, int)` and all
relative deltas through `ClampDelta(float, int)` before the cast to `int`. Rejects NaN and ±Infinity;
clamps to valid pixel bounds. Regression tests in `remex.core.tests/CoordinateValidationTests.cs`.
(RD-8)

### `MdnsDiscoveryService` — SRV validation

Before composing a `ws://` URL from untrusted multicast data, validate that the SRV port is >= 1 and
that the resolved host passes `Uri.CheckHostName != Unknown`. (NSD-6)

### `AndroidNativeExports` — dual-lock model

`PairingSyncRoot` (separate from the high-frequency `SyncRoot`) serializes pairing-session state
transitions, so a concurrent `StartPairing` / `SubmitPin` from a second Java thread waits rather than
disposing-then-using the active `ClientWebSocket` (JNI-4). JNI string marshalling (`ReadJString`)
happens inside the `Export` guard so managed throws are caught before escaping
`[UnmanagedCallersOnly]` (JNI-5).

---

## Session guard

### `WindowsInteractiveSessionGuard` — ref-counted keep-awake only

`EngageForRemoteControl(clientId)` / `DisengageFromRemoteControl(clientId)` maintain an `_engaged`
HashSet; the first engage and last disengage trigger the action. On engage it calls
`SetThreadExecutionState(ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED | ES_CONTINUOUS)`; on last
disengage it clears the flag.

**No `tscon`, no `WTSDisconnect`.** The guard lives inside the user session and must never disconnect
or reconnect it — doing so produces a black screen plus access-denied input. It only ever re-locks a
session it actually unlocked. `SessionGuardPolicy` and `SessionGuardAction` are deleted. Every
engage/disengage is audit-logged with the client identity.

**Security-sensitive:** while engaged the screen will not lock. The feature is off by default and is
enabled via `ProgramData\RemEx\keep-session-unlocked.flag` containing `1`, written by
`ISessionKeepUnlockedService` (the in-app toggle shows a localized security warning).
`[SupportedOSPlatform("windows")]`; the test double is `NoOpInteractiveSessionGuard`.

`RemoteDesktopHandler` checks `IHostCapabilitiesProvider.SupportsRemoteDesktop` and the session guard
before starting a stream, and sends a structured `DesktopErrorCodes` value on failure — not a generic
WebSocket close.

---

## Discovery and build

### `NsdDiscoveryManager` — API-level strategy

API 34+ uses concurrent, cancellable `registerServiceInfoCallback`. Pre-34 serialises resolves
process-wide via a `Mutex`, because `NsdManager` pre-34 allows only one in-flight resolve. Always
acquires a `WifiManager.MulticastLock` for mDNS reliability.

### `isMulticastReachableHost` — mDNS self-heal gate

`RemexClientManager` gates self-healing mDNS discovery behind `isMulticastReachableHost(host)`, which
returns `false` for Tailscale/CGNAT (`100.64.0.0/10`) and public IPs. Prevents spamming Android's
local-network permission prompt when the saved host is a VPN or public address. Private LAN
(`10.x`, `172.16–31.x`, `192.168.x`), link-local (`169.254.x`), and non-IP hostnames all pass.
(RemEx-fkz)

### `ConnectionViewModel` — single in-flight discovery

`discoveryJob: Job?` tracks the active NSD coroutine; `startDiscovery()` cancels any prior job before
launching, so overlapping manual and self-heal calls do not stack NSD resolves or multicast-lock
cycles. (RemEx-4bb)

### `SyncRemexCoreSoTask` — ELF verification

Content-tracks `sourceCandidates` as Gradle inputs (prevents a stale `.so` on `-NoClean` builds) and
validates that the `.so` is AArch64 ELF (magic `0x7F454C46`, `EI_CLASS=2`, `e_machine=0xB7`) before
copying it into the APK. (RemEx-l79 / RemEx-hht)

---

## Frame-arrival watchdog

`RemoteDesktopViewModel` arms a watchdog on stream start, resets it on every decoded frame, and
triggers a reconnect if no frame arrives within the stall timeout. This backstops the H.264
decoder-init silent-death path — the one where everything reports healthy and nothing renders.

`desktopMetaReady` gates the orientation-aware initial fit until the host's real stream metadata
(dimensions, origin, backend) arrives, preventing the initial zoom from computing against a
placeholder resolution.

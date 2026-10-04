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

**`Reset()` goes AFTER the backoff's `await`, never before it.** It anchors `_nextTickMs` to the
clock at the moment it is called, and `WaitForNextTickAsync` only steps `_nextTickMs += interval` —
it never re-anchors. So `Reset(); await Task.Delay(500, ct);` ends the pause anchored 500 ms in the
past and then returns a **zero wait once per missed tick — roughly 60 of them at 120 FPS**, which is
the capture-and-encode backlog burst this call exists to prevent, arriving by way of the call that
prevents it. Both backoff sites in `RemoteDesktopHandler`'s capture loop (the display-powered-off
pause and the `consecutiveFailures >= 5` capture backoff) had the ordering inverted, while the
comment at the second one already claimed the burst was prevented. Pinned by
`remex.agent.tests/PacerResetOrderingTests.cs`, which asserts the ordering, that a backoff still
resets at all, and that each `Reset()` sits directly below an awaited delay — a behavioural test of
the pacer alone cannot see this, because the pacer is correct either way.

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

### The libei/EIS sender is a no-op stub — never let the router select it

`remex.agent.native.linux/src/libei_sender.c` dlopens libei and returns `REMEX_OK` from **every**
send function while discarding all arguments (its own comments say "In a complete implementation
this would..."). If anything ever routes pointer/keyboard/scroll into it, **all remote input
silently vanishes** — no error, no log line, no failing test; the stream keeps painting and the
cursor simply stops moving.

It is unreachable by construction today, and that is the only thing keeping it safe:
`HostBootstrapper.cs:152` registers `LinuxInputSimulationService` as `IInputSimulationService`;
`LinuxInputBackendRouter` — the only type that touches `LinuxEisInputService` — is never registered
or constructed in production, and `SetRouter` has zero callers outside its own declaration, so
`_router` stays null forever. Independently, `OpenEisSender` has zero callers, and `_available` only
flips inside `TryOpen`, which only `OpenEisSender` calls.

**Before switching the Linux RD input backend to libei/EIS**, either implement the sender for real
with tests, or make `remex_eis_sender_create` return `REMEX_ERR_EIS_UNAVAILABLE` so the router can
never select it. Do not wire the router up "just to see". (RemEx-whxz, reachability established in
the RemEx-892l review 2026-07-25)

---

## Android — H.264 decoder

`remex.android/app/src/main/java/com/clindsay94/remex/ui/screens/H264StreamDecoder.kt`

### Synchronous mode only — never async `setCallback` (INVARIANT)

The decoder is driven by a single dedicated thread,
`Thread({ runDecodeLoop() }, "H264DecodeLoop")` (`:89`, started at `:124`), which polls
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
`scanNalTypes` pre-check keeps P-frames on the fast path), the decoder does
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

### Two fingers ALWAYS scroll the PC — the zoom level never gates it (RemEx-pp4cm.10)

`remex.android/.../ui/screens/RemoteDesktopScreen.kt:1628-1735` (`when (TwoFingerRouting.route(twoFingerIntent))`)
sends a classified `Scroll` gesture to `onSendMouseScroll` unconditionally; only a `Pinch`
(`TwoFingerRoute.ZoomAndPan`, `:1630`) touches `zoomFactor`/`panOffset`, and it pans by the fingers'
midpoint travel (`:1669`). The decision lives in `TwoFingerGestureClassifier.kt` (`TwoFingerRouting`).

**Symptom when violated:** "two-finger scroll stopped working" in PORTRAIT only. Portrait opens
fit-to-height at about 3x, and the old `if (zoomFactor > 1.05f)` branch on the Scroll intent turned
every two-finger drag into a view pan with zero PC sends. Landscape (1x) looked fine, so the bug hid.

**Rule:** never add a zoom/pan condition to the Scroll branch. A zoomed view moves by pinch-and-drag
and by cursor pan-follow only. `RemoteDesktopTwoFingerScrollTest.zoomedPortrait_sendsScroll` (AVD)
and `TwoFingerRoutingTest` pin it.

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

### Routines: every host→phone type rides the `routine_` prefix forward

`remex.core/Native/AndroidNativeExports.cs:2084` (`OnNativeMessageReceived`), audience entries at
`remex.core/Messages/MessageAudience.cs:140-143`; pinned by `HostToClientRoutingTests`,
`MessageAudienceTests` and the Kotlin `RoutineMessageRoutingTest` — RemEx-pp0rt.3.

`routine_sync_result`, `routine_step_result`, `routine_notify` and `routine_run_report` reach Kotlin
only through the one `StartsWith("routine_", …)` forward to `RemexCallback.onRoutineMessage`. Remove
or narrow it and the host answers a step request, the send succeeds, and the phone times the step out
as "no answer from the PC" — the RemEx-y6x6 failure with a misleading message on top. **Every new
host→phone routines type must start with `routine_`** (the locked phone→host id `routines_sync` is the
only exception, which is why its reply is `routine_sync_result`). The host must also never send a
`routine_*` type to a client without `ClientCapabilities.supportsRoutines`: an older phone's router has
no such forward and drops it in silence.

### Routines: no `required` members on routine wire payloads, and every slot is lenient

`remex.core/Messages/RemexMessage.cs:416-450` (the nine `[JsonConverter(LenientRoutinePayloadConverter<…>)]`
slots), `remex.core/Messages/Routines/LenientRoutineConverters.cs`; pinned by
`RoutinesSyncMalformedPayloadTests` and `RoutineNoRequiredMembersTests` — RemEx-pp0rt.3, spec T18.

A `required` member missing from the JSON, or any field of the wrong JSON type, makes System.Text.Json
throw; `MessageSerializer.Deserialize` then returns a null envelope and `PingPongHandler` drops the
**whole session**, so one bad routine from a buggy or hostile phone would disconnect it on every
reconnect. Routine payloads therefore have no `required` members, triggers and steps are flat records
(an unknown `type` deserializes and is refused by `RoutineValidator`), and the lenient converters turn
an unreadable routine into a malformed placeholder (rejected alone, `invalid_field`) and an unreadable
payload into a null slot on a non-null envelope. The Kotlin reader (`RoutineJson`) applies the same
strict-type rule so both sides reject the same documents — the shared fixtures pin it.

### Routines: `routine_step_result` has its own flow, and the routine collector subscribes in `initialize`

`remex.android/.../RemexClientManager.kt` `_routineStepResults`, `onRoutineMessage` (splits by
type) and the `CoroutineStart.UNDISPATCHED` collector in the `@Synchronized` `initialize`; pinned by
`RoutineStepResultRoutingTest` — RemEx-pp0rt.5, S1a Kotlin review finding 1.

Both flows are `replay = 0` with `DROP_OLDEST`. Two silent failures follow from that. (1) With no
subscriber, `tryEmit` succeeds and the value is simply gone, so the collector that feeds history must
already be subscribed when the first message can arrive: it is launched UNDISPATCHED from
`initialize`, which runs before any connect, and the worker calls `initialize` itself because a
background trigger can start a fresh process. (2) A step result sharing a buffer with run-report
pages is evicted by a burst of pages: the PC did the step, answered, and the phone recorded
`step_timeout` ("no answer from the PC"). **Never route `routine_step_result` back onto the general
flow**, and keep the runner subscribing to it BEFORE it sends the request
(`RoutineRunner.awaitStepResult`), since there is no replay to catch an early answer.

### Routines: a phone run is at most once — a step is recorded `running` before it starts

`remex.android/.../routines/RoutineRunner.kt` `executeAttached` (the restart check, and the one
`try` that covers everything after the record exists); pinned by `RoutineRunnerTest` ("a retried
worker whose run already started a step…", "a throwing persist…", "a budget that runs out during a
destructive host step…") — RemEx-pp0rt.5, spec §8.2, §8.6.

WorkManager retries a worker the system stopped, with the same input. A runner that simply started
again from step 0 would send `SHUTDOWN` twice, or re-open an app, with nothing in history to say
why. So every step is persisted `running` before it executes, a retry that finds a started step
records `interrupted_phone` and stops, and a `CancellationException` that is not our own cancel is
rethrown with the record left `running` (the retry or the start-up sweep,
`RoutineRepository.sweepInterrupted`, then reports it). Do not "tidy" that catch into a generic
failure record: it would turn every system stop into a false `internal_error` and lose the retry.

Every OTHER exception after the record exists must end the run (`internal_error`) through the normal
finish path, with `RoutineWorker`'s catch calling `RoutineRepository.abandonRun` as the backstop. A
throw that escaped left the record `running`; the sweep only runs on the first load, so every later
start was skipped `already_running` until the process died. And whenever a run ends by the budget
or an external stop while a host step is in flight, `routine_cancel` goes out under
`NonCancellable` first: otherwise the phone gives up while the PC's 15 s countdown carries on and
powers off. `after_power_off` is keyed on a power-off step that actually SUCCEEDED, never on the
definition, so a simulated, conflict-skipped or cancelled one does not silence the steps after it.
Host capabilities for a step come from `RemexClientManager.hostInfoForConnection` (cleared on every
connect and disconnect, checked against the authenticated epoch and host identity), never from the
replaying `hostCapabilities`, which can still hold the previous PC's `supportsRoutines`.

### Routines: a notification's Open reaches the run through `RoutineOpenRequests`, from BOTH activity entry points

`remex.android/.../MainActivity.kt` (`RoutineOpenRequests.offer(intent)` in `onCreate` AND the
`onNewIntent` override), `ui/navigation/AppNavigation.kt` (the `pendingRoutineOpen` effect) and
`ui/routines/RoutinesScreen.kt` (the effect that consumes it) — RemEx-pp0rt.6, spec 1.7.

The S1c notifications open `MainActivity` with `FLAG_ACTIVITY_SINGLE_TOP | FLAG_ACTIVITY_CLEAR_TOP`
and two extras. When the app is already running, the intent arrives in `onNewIntent`, never
`onCreate`; drop that override and "See what happened" on a failed run just brings the app forward on
whatever screen it was on, with no log line. The request is a replaying `StateFlow` that only
`RoutinesScreen` consumes, and AppNavigation navigates to Routines only once the current route is not
Splash or Tutorial (both navigate onward by themselves and would be stranded under it); the effect is
keyed on `currentDestination` so it retries when the splash finishes. Consuming it in AppNavigation
instead loses it whenever Routines is not composed yet. `offer` strips the extras so a later
configuration change cannot reopen the same run.

The editor's app picker reads the PC's launcher entries straight from `launcher_sync`
(`RoutinesViewModel.parseLauncher`) because a `launchApp` step stores the PC's `AppEntry.Id` GUID,
which the App Launcher screen's own `AppEntry` model drops. An entry without a 36-character `id` is
left out, so if the PC ever stops serialising `id`, the picker shows "Connect to your PC to choose an
app" while connected, and no routine can open an app. Keep `AppEntry.Id` on the wire.


### Routines: the countdown window must not depend on `MainWindow`

`remex.desktop/Services/Routines/AvaloniaRoutineUi.cs:56-67` (ownerless `new RoutineCountdownWindow(…)`
then `ShowCentred()`), `remex.desktop/Views/RoutineCountdownWindow.axaml.cs`; pinned by
`RoutineCountdownSurfaceTests.TheCountdownWindowNeverReachesForMainWindow` and
`RoutineCountdownWindowRenderTests` — RemEx-pp0rt.4, spec §8.6.

The countdown before a routine's shut down, restart, sign out, sleep or hibernate is the person at the
PC's only chance to stop it, and it most often fires while RemEx sits in the tray after a `--minimized`
logon start, the state in which `MainWindow` was never constructed (P1-29, see the `ConsentRoutePolicy`
guard below). Give the window an owner, show it with `ShowDialog`, or route it through
`desktop.MainWindow`, and it either throws or never appears; the coordinator records that as
`countdown_unseen`, the 15 s elapse, and the PC shuts down with nothing on screen. No exception reaches
anyone. Keep it a top-level, ownerless, topmost window, and keep the tray "Cancel routine" item
(`RoutineCountdownTrayState`) armed before the window is attempted, so a window that fails still leaves a
way to cancel.

**Any close before the countdown ends is a cancel; `IsProgrammatic` is not a safe signal, because
Avalonia-drawn caption buttons close programmatically.** The window uses
`ExtendClientAreaToDecorationsHint`, so its X is drawn by Avalonia and calls `Window.Close()` itself: a
real mouse click on it arrives as `IsProgrammatic = true`. The first version cancelled only when
`!e.IsProgrammatic`, so Alt+F4 cancelled but the X closed the window while the agent's 15 s ran on, and
SHUTDOWN was issued at 15 s (RemEx-pp0rt.16, live test 2026-09-27). `RoutineCountdownWindow.OnClosing`
(`RoutineCountdownWindow.axaml.cs:106`) now cancels on every close unless the countdown has already
ended; the only exempt close is the coordinator's, through `AvaloniaRoutineUi.CloseWindowOnUiThread` →
`CloseAfterCountdownEnded()` (`AvaloniaRoutineUi.cs:129`), which marks the countdown ended before it
closes. Never key the rule on who called `Close()`. The window also has no Minimize or Maximize
(`CanMinimize`/`CanMaximize="False"`): a minimized countdown hides the only warning. Pinned by
`RoutineCountdownWindowRenderTests.ClosingTheWindowWhileTheCountdownRunsCancelsExactlyOnce` (the real
`Close()` path), `TheCoordinatorsCloseAfterTheCountdownEndedDoesNotCancel`,
`ThroughTheRoutineUiTheCoordinatorsCloseDoesNotCancelButAnyEarlierCloseDoes` and
`ItCannotBeMinimizedOrMaximized`.

### Routines: a test run never issues a destructive verb

`remex.agent/Services/Routines/RoutineStepExecutor.cs:234` (`if (execution.TestRun)` returns `simulated`)
before the pre-issue announcement and before `IRoutinePowerExecutor`), and
`RoutineStepRequestHandler.cs:140` (`PresenceConfirmed: false` for every wire request); pinned by
`TestRunSimulationTests` and `CountdownCancelTests.EveryWireInitiatedStepCountsDown` — RemEx-pp0rt.4,
spec D7, T21, T22.

The phone's Test button exists so a routine ending in SHUTDOWN can be tried without shutting down. The
check sits AFTER the countdown (a test must still show it) and BEFORE the verb, and it must stay ahead
of every branch that can issue one, including a confirmed Run now: `testRun` may only ever make the host
do less. Moving it below the dry-run branch or the "succeeded before the verb" send turns a test into a
real shutdown whose history then says `simulated`. The same file is also why no wire field may map to
`presenceConfirmed`: it skips the countdown, so it is set in-process only, by the PC's own confirmed Run
now (S4).

### Routines: Run now's presence flag has no wire representation

`remex.desktop/Services/Routines/IRoutinesHost.cs:89` (`RunNowAsync(…, bool presenceConfirmed)`, the only
entry that can set it), `remex.agent/Services/Routines/RoutineHostService.cs:284` (passes it through as
`manual.pcRunNow`) and `:507` (`PresenceConfirmed: false` for `routine_run_request`); pinned by
`RunNowCountdownBypassTests` (`NoWirePayloadCanExpressPresence`, `APhoneRunRequestAlwaysCountsDown`,
`ATriggerAlwaysCountsDown`) — RemEx-pp0rt.9, spec D3, T21.

`presenceConfirmed` is the one thing that lets a routine shut down, restart, sign out, sleep or
hibernate the PC without the 15 s countdown. It means "the person at this PC just confirmed it in
`ConfirmationDialogHost`", which is only true for the PC's own Run now. Add a field to
`routine_run_request`, `routine_step_request` or `routines_sync` that maps onto it, or derive it from a
source string the phone sends, and any paired phone (a lost one, a buggy one) can power the PC off with
nobody given the chance to press Cancel; nothing logs it as unusual, because the verb path is the normal
one. A confirmation dialog that cannot show must pass false (fail closed). The runner never decides the
countdown itself: it only passes this flag through to `RoutineStepExecutor`.

### Routines: the session source owns its own message-only window

`remex.agent/Services/Routines/RoutineWindowsSources.cs:139` (`WtsSessionStateSource.Pump`: its own
thread, `CreateWindowExW(… HWND_MESSAGE …)` at `:167`, `WTSRegisterSessionNotification` at `:175`);
pinned by `WtsSessionStateSourceTests` — RemEx-pp0rt.9, spec §8.5.3.

`pc.session` routines ("lock the PC → sleep") hear lock and unlock through `WM_WTSSESSION_CHANGE`, which
Windows delivers only to a window registered for it. Hooking the Avalonia main window instead looks
equivalent and is silent when it fails: after a `--minimized` logon start `MainWindow` may never be
constructed (see the countdown-window guard above), so no window is registered, no edge ever arrives and
every session routine simply never fires, with no error anywhere. Keep the dedicated thread and the
message-only window. **Keep the window class unique per source too**: a class carries the window
procedure of whoever registered it, and a second source reusing a per-process class name ran on the first
source's collected delegate, which terminated the test host ("callback on a garbage collected delegate").

### Routines: logind Lock/Unlock signals are requests, not state

`remex.agent/Services/Routines/RoutineLinuxSources.cs:33` (`LogindParsing.ParseLockedHint`, only the
`LockedHint` property of `org.freedesktop.login1.Session`) and `:396` (the `PropertiesChanged` match
rule); pinned by `LogindSessionSourceTests` — RemEx-pp0rt.9, spec §8.5.3.

logind's Session `Lock()` / `Unlock()` signals are logind asking the screen locker to act: they are sent
whether or not a locker is running or obeys, and a lock the locker starts on its own (idle timeout, the
user's shortcut) never produces one. Subscribing to them fires "on lock" routines for locks that never
happened and misses real ones, and the only symptom is routines running at the wrong times or not at all.
`LockedHint` is set by the locker once the screen really is locked. Follow the property through
`PropertiesChanged` (re-reading it when it is only invalidated), never the signals.

### Routines: the PC Routines page resolves its host on every use, never caching "no host"

`remex.desktop/ViewModels/RoutinesViewModel.cs:240` (`AttachHost`: `_resolveHost()` until it returns a
host, then one `Changed` subscription) and `remex.desktop/ViewModels/ShellViewModel.cs:1572`
(`EnsureRoutinesViewModel`, resolving through `EmbeddedHostServiceLocator.TryResolve`); pinned by
`RoutinesViewModelTests.AHostPublishedAfterThePageWasBuiltIsPickedUpOnTheNextRefresh` — RemEx-pp0rt.9, S4b.

`IRoutinesHost` is registered in the embedded host's container, which is published after the host
starts. The shell builds the page's view model lazily and keeps it for the session, so a view model that
captured the host once at construction (or cached a null) would show "Routines aren't available" for the
whole session whenever the page was first opened before the host came up, while the routines themselves
kept running behind it. Resolve through the delegate until a host appears; subscribe to `Changed` exactly
once. `Changed` fires on a background thread: every refresh goes through the posted, coalesced
`OnHostChanged`, never straight into the collections.

### Routines: the page passes presence only after a confirmation that actually showed

`remex.desktop/ViewModels/RoutinesViewModel.cs:535` (`RunNowAsync`: `OnConfirmationRequested is null` or a
false result returns before `host.RunNowAsync`; `presenceConfirmed = true` at `:560` only after a true
result) and `remex.desktop/Views/RoutinesView.axaml.cs` (`ConfirmationDialogHost.ForTinted(this)`, which
returns false with no visible parent window); pinned by `RoutinesViewModelTests` (`ADeclinedConfirmationRunsNothing`,
`NoConfirmationWiredMeansNoRunFailClosed`, `RunNowWithADestructiveStepConfirmsAndAConfirmationIsPresence`) —
RemEx-pp0rt.9, spec D3, T21, R-UX-36.

This is the UI half of the guard above. The main window hides to the tray, so the page can exist with no
visible window; `ConfirmationDialogHost` then declines instead of throwing. Treating a missing delegate or
a declined dialog as "run it anyway, with the countdown" looks safe but is not what the person asked for,
and treating it as "run it with presence" removes the 15 s Cancel with nobody at the PC. A destructive
routine runs from this page only after a dialog the person answered Yes to; anything else runs nothing.

### Routines: the drawer page is five edits that must agree

`remex.desktop/Views/ShellView.axaml:844` (the `Tag="10"` item after Commands) and `:1109` (the
`RoutinesViewModel` DataTemplate), `remex.desktop/Views/ShellView.axaml.cs:857` (`case 10`),
`remex.desktop/ViewModels/ShellViewModel.cs:383` (`_drawerNavOrder`) and
`remex.desktop/Converters/NavIndexToTitleConverter.cs` (`10 => "Nav_Routines"`, or the app-bar title goes
blank on the page); pinned by `NavIndexToTitleConverterTests`, `ShellNavRoutinesTests`,
`ShellNavListTests.TheTagSetInMarkupMatchesTheCaseSetInActivateNavItem`,
`ShellViewModelNavDirectionTests.DirectionOrderMatchesTheDrawerOrderInShellViewXaml` and
`ShellNavEntranceTests` — RemEx-pp0rt.9, R-UX-02 to R-UX-04.

Each one fails quietly on its own: a Tag with no `case` is a dead click in Release (`Debug.Fail` compiles
out), a view model with no DataTemplate renders as its type name, a Tag missing from `_drawerNavOrder`
ranks below the drawer so every move to Routines slides the wrong way, and an eleventh item without its
own `nth-child` style flashes in unanimated. The stagger step is 18 ms so the last of the eleven still
ends by 300 ms.

### Routines: the phone never sends `routines_sync` revision 0, and forget flushes BEFORE the pins go

`remex.android/.../routines/RoutineSyncClient.kt` `runSession` (the `snapshot.revision <= 0` skip) and
`forgetPc`; the forget calls in `ui/screens/ConnectionViewModel.kt` `unpairKnownHost` and the per-address
Unpair in `ui/screens/ConnectionScreen.kt`; pinned by `RoutineSyncClientTest` ("forget flush tells the
connected PC...", "a phone with no PC routines never sends anything") — RemEx-pp0rt.12, spec §7.4.5, T8.

Revision 0 means "this phone keeps no books for that PC": it never had a PC-run routine there, or it was
just forgotten. The PC treats a sync for an owner it does not know as a new owner, so sending revision 0
right after a forget would quietly recreate the set the flush just deleted, and the PC would go on running
routines the phone no longer shows. The flush also needs the pinned connection, so every flow that calls
`PinnedHostStore.forgetHost` must call `Routines.forgetPc` / `forgetPcAt` FIRST; moved after it, the flush
can never reach the PC and every forget becomes the 30-day owner-absent residual with nothing in the log.

### Routines: a certificate repair re-keys the PC's routines, and the re-key never lowers the revision

`remex.android/.../routines/RoutineRepository.kt` `rekeyHost`, called from the cert-repair migration
branch of `ui/screens/ConnectionViewModel.kt` `recordHostAsLastConnected` (next to
`migrateKnownHostIdentity`); pinned by `RoutineRepositoryRekeyTest` — RemEx-pp0rt.15.

A confirmed repair is NOT a forget: the pins are cleared without `Routines.forgetPc`, because it is the
same machine and the PC keeps its routines per phone (`clientId`), not per PC identity. But the new pin is
a new `HostIdentity`, so routines and their `hostSync` entry left on the old one are stranded: nothing
ever authenticates as that identity again, the phone never syncs that PC, the PC keeps running the last
set, and owner-absent suspension never fires because the phone keeps connecting. The re-key moves both in
one write and bumps `localRevision` PAST the old books with `ackedRevision` reset, so this connection
sends the full set and the PC replaces its copy. Resetting the revision to 0 instead is silent the other
way: revision 0 is never sent (guard above), and a low one draws `stale_revision` from the PC.

### Routines: a pending "Switch and run" belongs to the next authenticated connection only

`remex.android/.../routines/RoutineSyncClient.kt` `consumePendingSwitchRun` (called first in
`runSession`) and `cancelPendingSwitchRun` (from `ConnectionScreen`'s `DisposableEffect`); pinned by
`RoutineSyncClientTest` ("a pending switch-run dies with a connection to another PC...") — RemEx-pp0rt.12
review.

The pending run is taken by the first authenticated session after it was armed, whichever PC that is,
and runs only if that PC is the target. Keeping it pending across a connection to another PC means any
later reconnect to the target inside the window, for any reason, starts the routine, and a routine that
ends in SHUTDOWN runs without anyone asking for it again. The countdown still shows, but nobody expects
it. Leaving Connection drops it as well.

### Routines: sync answers and run reports are handled inside the one routine collector, which never blocks

`remex.android/.../routines/RoutineInboundHandler.kt` and `RoutineSyncClient.onSyncResult` /
`onRunReport`; pinned by `RoutineSyncClientTest` — RemEx-pp0rt.12.

`routine_sync_result` and `routine_run_report` are handled on the single long-lived collector started in
`RemexClientManager.initialize`. Anything that waits there (a retry back-off, a resend, a wait for a run's
answer) stalls every later routine message, and because the flow is `replay = 0` with `DROP_OLDEST`, a
burst behind a stalled collector is dropped, not queued: history pages vanish and a Run on PC reports
"no answer". Back-offs and resends are launched in the connection's sync session scope, never awaited in
the handler. Neither handler may throw either, or the collection ends for the life of the process.

### Routines: sensor routines must hold telemetry demand or they never see a sample

`remex.agent/Services/Routines/RoutineSensorSource.cs:83` (`SensorTriggerSource.SetArmed`: one
`AcquireDemand()` lease at `:109` while at least one `pc.sensor` routine is armed, released when none is)
and `remex.agent/Services/Routines/RoutineHostService.cs:440` (`Rearm` passing the armed set on every
store change and pause); pinned by `SensorTriggerSourceTests.DemandIsHeldOnlyWhileASensorRoutineIsArmed`
and `SensorRoutineHostTests.AnArmedSensorRoutineHoldsDemandAndPauseReleasesIt` — RemEx-pp0rt.10, spec
§8.5.1, §14 S5.

The telemetry sampler idles whenever nothing holds a lease (perf audit P0-10, `SamplingDemand`) and an
idle sampler publishes nothing. `TelemetryPublished` is then simply never raised, so a sensor routine that
only subscribes looks correct in every test that publishes by hand, and on a real PC with no window open
and no phone streaming it sits armed forever and never fires, with no error and no log line. Hold the
lease from the sensor source, only while something is armed (an unconditional lease brings back the idle
1 Hz poll on every PC). The sync validator's catalog lookup (`RoutineSensorCatalog`) takes its own short
lease for the same reason: without it an idle PC would reject every sensor routine as `sensor_unavailable`.

### Routines: a held message is on disk before the live send, and leaves only on ack or expiry

`remex.agent/Services/Routines/RoutineNotifyQueue.cs:164` (`NotifyAsync`: `SaveAsync` before
`TrySendAsync`), `AckAsync` (owner-scoped removal) and `SweepAsync` (one-hour expiry); flushed from
`RoutineSyncHandler.DrainAsync` after the sync result; pinned by `RoutineNotifyQueueTests` —
RemEx-pp0rt.10, spec §7.3.5, §17 Q6, T17.

"Notify the phone, then shut down" is the headline pattern: the step after the notify can take the PC
down within a second, so a message saved after the send attempt (or only in memory) is lost exactly when
it matters, with the step already recorded `succeeded`. A live send is not proof of display either: the
item stays until `routine_notify_ack` names it, and an ack is applied only to the sending phone's items,
so one phone can never clear another's. The file goes through `IRoutineStateFiles` (atomic, same ACL and
trust check as `routines.json`, T14); an untrusted file is set aside, never loaded. An expiry for a run
that is still going is held (`DeferredExpiries`, on disk) and applied by `RunEndedAsync` once the runner
has saved and reported the final record: writing it earlier is overwritten by the runner's next save.


### `protocolVersion` bumps must be coordinated

`RemexMessage` carries `protocolVersion: 2`. A breaking wire-format change requires bumping it in
**both** `remex.agent` and `remex.android` and coordinating the release — a mismatch causes silent
deserialization failures, not clean errors. Non-breaking additions (new optional fields) need no
bump, but document them in `CHANGELOG.md`.

### PC transfer queue: the pump must survive a throwing item or handler

`FileTransferQueue.PumpLoopAsync` (`FileTransferQueue.cs:590`) guards each run and resets `_pumping` in a `finally`. An escaped throw once ended the fire-and-forget pump with `_pumping` still set, and every later transfer sat at "Queued" forever with no error or log (RemEx-ostqe).

### Never announce `file_transfer_complete` before the peer has acked the data

Bulk file data travels on `/ws/files`; `file_transfer_complete` travels on the control `/ws`. **TCP
orders bytes only within one connection, never between two.** A sender that announces completion the
instant its last frame is *enqueued* lets the tiny completion overtake the still-in-flight bulk
bytes. The receiver then tears its sink down and finalizes a zero-byte transfer, reporting
*"Transfer incomplete."* while the data is literally still arriving.

**All three senders must drain first, and all three now do:**

- C# host → phone: `TransferSessionManager.WaitForFinalAckAsync` (`TransferSessionManager.cs:1729`),
  called at `TransferSessionManager.cs:1640` before the completion is sent.
- Kotlin phone → host, upload: `FileTransferEngine.runUpload` (`FileTransferEngine.kt:320`).
- Kotlin phone → host, **download-serving**: `FileHostHandler.beginHostSend`, the
  `while (session.committedOffset < sent)` loop before `sendComplete`. Added by `RemEx-xrb2v`; this
  sender had the defect for three beads after the other two were fixed. It was the only one never
  observed failing in the field, because the PC-side receiver is faster than the phone-side one — so
  "we have not seen it" was never evidence that this one was sound.

**Bound the wait on what was SENT, never on the declared length.** `node.length` is read before the
file is opened and is not trustworthy: `FileSystemFacade` returns `DocumentFile.length()`, which is
**0** whenever a SAF provider omits `COLUMN_SIZE`, and it goes stale if the file is appended to or
truncated in between. The first version of this fix waited `while (committedOffset < size)` and had
two silent modes — a declared 0 made the wait a no-op and left the bug fully live, and a declared
length larger than the file waited forever, because there is deliberately no deadline.

**A DECLARED SIZE OF ZERO MEANS "UNKNOWN", NOT "EMPTY". Do not reconcile against it.** This is a
contract with the other end, written down there: `TransferSessionManager.cs:59` — *"a declared size of
ZERO is legitimate — a phone reports it for a content URI whose length it cannot read"* — and the PC
gates both its overshoot bound (`:509`) and its completion check (`:555`) on `ExpectedSize > 0`. So
`beginHostSend` reconciles only a length that was actually reported:

```kotlin
if (size > 0 && sent != size) throw IllegalStateException(...)
```

The second version of this fix made that throw **unconditional**, and would have failed every
download from a provider that omits `COLUMN_SIZE` — a path that works today, whose only defect was
the missing drain. A bead about completion ordering would have taken out a working transfer path.
Bounding the drain on `sent` is what makes the unknown-size case correct without needing a declared
length at all.

**The `final` flag comes from a one-chunk read-ahead**, not from `size`, and the two halves depend on
each other: the PC acks on `Final || interval` (`TransferSessionManager.cs:1375`), so on an
unknown-size transfer a wrongly-flagged last frame would strand a sub-interval tail even with the
drain bounded correctly. The old `sent + read >= size` marked the *first* frame final on a provider
reporting 0, and no frame final at all on a stale larger size.

In production the reconcile is shrink-only in practice: for growth the PC throws *"overshot its
declared size"* the moment bytes exceed `ExpectedSize` and that ERROR cancels the send first. Belt
and braces, not dead code — but that is why the growth branch never appears in logs.

**Why this one hid so long, and what it costs to test.** `FileHostHandler` documents itself as pure
logic over injected seams, but the send loop reached past its injected `FileFrameChannel` to call the
`FileTransferChannelClient` singleton, which has no websocket under test and refuses every frame — so
the loop threw before reaching anything worth asserting. And the sha used `android.util.Base64`,
which returns **null** under `isReturnDefaultValues`, so `sendComplete` threw and no completion was
ever emitted in a unit test. Both were fixed to make the guard testable: `sendData` moved onto the
`FileFrameChannel` interface, and this one call switched to `java.util.Base64` (minSdk is 34; output
is byte-identical). If either is reverted, the drain becomes unobservable again.

**The assertion has to be negative.** "A complete was sent and named the right hash" passes just as
happily when the complete overtook the data — every fake answers from memory, so the ordering the bug
produces is the ordering a positive-only test sees. Pin *"no completion has been sent while the peer
has acked nothing"*: `FileHostHandlerTest.downloadSend_doesNotAnnounceComplete_untilThePeerHasAckedEveryByte`
and `downloadSend_withAPartialAck_isStillWaiting` on the Kotlin side, `HostSendDrainTests.cs` on the
C# side.

**The measured mutation set for this sender**, all restored byte-identical, all on the release
variant. Re-run these rather than inventing new ones; each was written after an earlier version of it
came back green against a defect that was really there:

| Mutation | Failures |
|---|---|
| Delete the drain loop | 5 |
| Bound the drain on `size` instead of `sent` | 1 |
| Remove the `size > 0` gate on the reconcile | 1 |
| Derive `final` from `size` again | 1 |
| Drop the `CancellationException` rethrow | 1 |
| Deliver an ERROR frame from inside `registerSink` | 1 |
| Delete the backpressure loop | 1 |
| Invert the backpressure comparison | 7 |
| Re-inline `MAX_UNACKED_BYTES` over the seam | 1 |

**The `HostSendSession.ackSignal` half of this was an uncaught mutation; it no longer is (`RemEx-3uv7s`).**
Flipping it from `CONFLATED` to `RENDEZVOUS` used to leave every test green: under
`Dispatchers.Unconfined` an ack resumes the sender inline, so it is always already parked when the
next ack arrives — the "token arrives with nobody waiting" case the buffer exists for was
unreachable that way, and reaching it re-entrantly deadlocked the test thread instead of failing.
The buffer is load-bearing regardless of any test: without it an ack landing between the condition
check and the park is dropped and the transfer freezes mid-file with no error either side. It is now
pinned two ways. The capacity is a named constant, `ACK_SIGNAL_CAPACITY` at `FileHostHandler.kt:124`,
which `HostSendSession.ackSignal` (`FileHostHandler.kt:1555`) is built from rather than from a
literal, and which the wait itself — extracted into the free function `awaitAck` at
`FileHostHandler.kt:142` — takes as a parameter so the no-waiter race can be driven directly and
synchronously: `FileHostHandlerTest.ackSignalGap_conflatedDoesNotLoseATokenThatArrivesBeforeAnyoneParks`
builds a channel from that same constant, delivers a token via `trySend` before anything is parked in
`receive()`, and asserts it survives; it goes red under the `RENDEZVOUS` flip. Because that test pins
the constant and not the call site, a second test,
`FileHostHandlerTest.ackSignal_isBuiltFromTheCapacityConstant_notALiteral`, source-scans
`FileHostHandler.kt` for the `ackSignal = Channel<Unit>(...)` construction and fails if anything but
`ACK_SIGNAL_CAPACITY` appears there — this is what catches someone typing `Channel.RENDEZVOUS`
directly at the call site, which the first test cannot.

**The upload direction remains note-guarded only, and covering it is out of scope for RemEx-3uv7s.**
`FileTransferEngine.kt:261` builds its own `ackSignal` as `Channel<Unit>(Channel.CONFLATED)` for
`UploadSendLoop`, and since `RemEx-yi7id`, `UploadSendLoopTest` runs under `Dispatchers.Unconfined`
too, so the same flip on that channel also stays green today. Treat that one channel's capacity as
guarded by this note, not by a test, until it gets the same treatment.

Two of those were green until the *tests* were fixed, not the code: bounding on `size` was
unfalsifiable while the reconcile ran unconditionally, and the `final`-flag mutation passed against a
five-byte fixture where the first and last frame are the same frame. If a mutation here comes back
green, suspect the fixture before concluding the code is covered.

**The backpressure wait is NOT this wait.** It only blocks once outstanding unacked bytes exceed
8 MB, so every transfer *smaller* than that reaches the completion without ever forcing an ack round
trip. On the Kotlin sender that cap is now an injectable constructor parameter defaulting to
`FileTransferLimits.MAX_UNACKED_BYTES`, so the branch is reachable from a test (`RemEx-68wwl`); the
C# sender's cap is now the same shape: an injectable init-only seam, `MaxUnackedBytes` at
`TransferSessionManager.cs:96`, defaulting to `FileTransferLimits.MaxUnackedBytes` and compared
against in `StreamSenderAsync` at `TransferSessionManager.cs:1554`. `HostSendBackpressureTests`
covers both halves — the sender stopping at the cap and resuming once an ack lowers outstanding
bytes back under it (`RemEx-xefvb`). `FileTransferEngine.runUpload` on the upload path is now covered
the same way: the loop is extracted into `UploadSendLoop`, an injectable collaborator (the object
singleton itself has no constructor to seam a defaulted cap onto), exercised by
`UploadSendLoopTest` (`RemEx-yi7id`). Any value
set there must exceed the peer's 4 MB ack interval or the sender deadlocks in silence — a smaller cap
is valid only against a fake that acks by hand. That inverse
sizing is what made this look like a flaky feature rather than a bug: large pushes incidentally
survived because backpressure had already drained them, while every screenshot failed. Measured on a
353,985-byte screenshot push — both data frames dropped as *"No sink"* (RemEx-zd8ws; the phone had
learned the same lesson as RemEx-y6x6 and the C# sender never got the fix).

The wait is an **idle window**, not a deadline (`AckDrainIdleTimeout`, `TransferSessionManager.cs:139`):
up to 8 MB may still be draining, so any total budget safe on a slow link is useless as a backstop.
A dead socket does not depend on it — `RunChannelAsync`'s teardown cancels the send session.

Guarded by `HostSendDrainTests`. Its discriminating assertion is *negative* — with the final ack
withheld, the sender must still be blocked. Asserting only on the final ordering does not catch a
regression here, because once the ack is delivered a fixed and a broken sender emit identical
control traffic.

### `TransferSessionManager` is the only production writer of `transfer_queue.json`

`TransferQueueService` is registered as a singleton in `HostBootstrapper.cs` and must stay resolved by
`TransferSessionManager`'s public constructor. For two beads it was resolved by **nothing**:
RemEx-kow1 gave it per-write staging names and RemEx-njzcx gave it an orphan sweep above the
existence check, and both hardened a store that no running host ever constructed, so
`%ProgramData%\Remex\transfer_queue.json` was never written on a real machine while
`remex.desktop/ViewModels/FileTransferQueue.cs` documented cross-restart resume as the host's job.

**The failure presents as a full green suite.** `TransferQueueServiceTests` and
`HostStateAtomicWriteTests` both instantiate the service directly, so every property of the store was
covered while the store itself was unreachable — the "stranded pure half" AGENTS.md warns about,
with the defect at the join rather than in either half. A test that calls `Enqueue` itself cannot see
this; `remex.agent.tests/TransferQueueWiringTests.cs` drives the real transfer path and then reads the
FILE, and pins the constructor parameter by reflection so dropping the join fails even though every
behavioural test still passes it in through the internal test seam.

Bookkeeping is written on STATE TRANSITIONS only, never per data frame — each write is a
stage-and-rename, and resume re-derives its offset from the staging partial's length, not from the
queue. `Done` and `Cancelled` entries are removed (the queue is a work list, not a history); `Failed`
is kept so a restart can still show it. A host push (`PushFileAsync`) is recorded as mode
`download`, because `TransferQueueService.DirectionOf` maps `push` to **Inbound** — that token means
the phone pushing to the host, and the queue's "one active per direction" rule is about which way the
bytes travel.

---

## Desktop shell — Material.Avalonia template parts

### An unset property is not a neutral property (INVARIANT)

`remex.desktop/Views/ShellView.axaml:1169-1171` — `material:SideSheet#SettingsSideSheet` **must**
carry `Background="Transparent"`.

The sheet spans the whole shell (`Grid.Row="0" Grid.RowSpan="3"`) and is declared *after* the app
bar, the drawer and the page host. `Background` template-binds to `PART_RootBorder`, which wraps the
sliding panel **and** the scrim's remainder — so a real colour there paints the entire shell rather
than the 440px panel. That much was already known, and the guard written for it asserted that no
`Background=` attribute appears on the element at all.

That guard shipped a blank app (RemEx-b8dxy, P0). **Absent is not the same as transparent**: with the
attribute gone, Material.Avalonia 3.19.0's `ControlTheme` default reaches the same
`PART_RootBorder`, and that default is an opaque `MaterialPaperBrush` — a dead-flat `#303030`, the
same brush `MainWindow.axaml:19` and `Themes/Chrome/WindowChrome.axaml:7` already had to neutralise
for the window decorations. The *closed* sheet therefore covered the app bar, the drawer and the
page host. Measured on the live window: uniform `#FF303030` everywhere except the gear FAB and the
snackbar host, which are the only siblings declared later.

Layout, focus and hit-testing were untouched — the UI Automation tree was completely intact, with
every nav item and page element present at the right coordinates. The failure was purely paint, so
there was no exception, no log line, and 2939 green tests. `remex.desktop.tests` has **no headless
render**, so no test in this repo can see a covered shell; the attribute itself is the only guard,
pinned by `ShellSettingsSideSheetTests.TheSideSheetPaintsNothingOnItself_ButMustSaySoExplicitly`.

`Transparent` rather than `null`: `null` stops `PART_RootBorder` hit-testing and breaks the scrim's
click-to-dismiss. Same reason `WindowChrome.axaml:37`'s `PART_TitleBar` is `Transparent`.

**The general rule for every Material.Avalonia template-part override:** before concluding a
property should be *absent*, check what the theme puts there in your absence. This is the same trap
as the scrim's priority bug (`ShellView.axaml:162-176`) on a different axis — there the override lost
to an activated selector, here the absence lost to a plain default.

### Mica is requested as `Transparent` + our own DWM call, never the `Mica` hint (INVARIANT)

`remex.desktop/Services/MicaBackdrop.cs` and the Mica branch of `MainWindow.axaml.cs`'s
`OnCustomizationApplied` — the window asks for `WindowTransparencyLevel.Transparent`, never
`.Mica`, and `MicaBackdrop.TryApply` calls
`DwmSetWindowAttribute(hwnd, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, 2 /* DWMSBT_MAINWINDOW */)`
itself once the window has a handle.

On Windows 11 26200 with Avalonia 12.1.1, `TransparencyLevelHint = Mica` makes
`ActualTransparencyLevel` report Mica while Avalonia never actually asks DWM for it —
`DWMWA_SYSTEMBACKDROP_TYPE` reads back `0` (the legacy `DWMWA_MICA_EFFECT`, 1029, no longer
exists on this OS) and Avalonia paints its own flat layer instead. This is the same shape as the
2026-08-27 probe on RemEx-z94c7 that first retired Mica: window pixels invariant to the desktop
wallpaper underneath, with no exception and no log line — the transparency API reports success
throughout. The re-probe (mica-spike, task 1, RemEx-rq0xl, 2026-09-16) confirmed the mechanism:
with the `Mica` hint, `DWMWA_SYSTEMBACKDROP_TYPE` read `0` at every phase; requesting `Transparent`
instead and forcing `DwmSetWindowAttribute(38, MAINWINDOW)` from inside the app produced a real,
wallpaper-derived tint (`#23151A` on the rainbow-wallpaper monitor vs. a flat `#111418` base) —
`mica-spike-mode4.log`,
`.superpowers/sdd/2026-09-13-personalize-tabs/eyes/audit/`.

Requesting the `Mica` hint again — even alongside the DWM call — silently reintroduces the flat
covering layer and no test in this suite has a headless render to catch it; only the source can be
pinned, by `MicaBackdropTests` and `MainWindowBackdropTests`.

### `RenderTransform` is not a keyframe-animatable property (INVARIANT)

`remex.desktop/Views/HomeView.axaml:53-137` — the six staggered entrance keyframe `Style.Animations`
blocks animate `TranslateTransform.Y`, never `RenderTransform` itself.

Avalonia has no keyframe animator registered for `RenderTransform` (a `TransformOperations` value).
The first cut of this bead animated it directly and crashed inside `Animation.InterpretKeyframes` on
the very first launch of `HomeView` — a P0 (RemEx-qolhg): the app never reached a usable window, with
no exception surfaced anywhere useful. `TranslateTransform.Y` is a plain `double` property with a
registered animator, and keyframes on it produce the identical visual slide.
`HomeViewEntranceTests.cs:33-61` pins the `nth-child` style count against the actual parsed XAML (so a
section added to or removed from `DashboardSections` forces a deliberate edit here) and documents the
crash so nobody re-tries the `RenderTransform` shortcut.

### Palette-transition suppression must carry an activator AND be declared after the crossfade

`remex.desktop/App.axaml:231` (`Window.palette-crossfade.palette-transition-suppressed`) and `:1265`
(the "chrome drag suppression … MUST STAY LAST" block, suppressor styles at :1288-1302) — RemEx-zgtn1.

Avalonia's `StyleInstance.GetPriority` returns `StyleTrigger` for any style carrying a class activator
and `Style` otherwise, and PRIORITY IS COMPARED BEFORE APPLICATION ORDER. A suppression selector with
no activator loses to an activated crossfade style no matter where it is declared; two selectors that
both carry an activator tie at `StyleTrigger`, and the LATER declaration then wins. The chrome-
transition suppressor was originally written next to the `Window` pair near the top of `App.axaml` —
it looked correct, and a test asserting only that it existed stayed green — while being completely
inert, because every chrome style it needed to outrank (`.card`, `.primary`, `Ellipse.status-dot`, …)
is declared later and carries the same activator tier. Under reduced motion this meant chrome colour
transitions kept animating with the setting supposedly off. `PaletteTransitionSuppressionTests.cs:68`
(`SuppressionOutranksTheCrossfade_ByCarryingAnActivatorAndComingLast`) asserts both conditions —
activator present, declared last — precisely because a fully green suite shipped this regression the
first time. Any new style that installs a `Transitions` collection on a class RemEx suppresses must be
added ABOVE this block, never after it.

### Aurora's static `Opacity` must equal its first keyframe, and its animation must be class-gated

`remex.desktop/Controls/DashboardBackgroundControl.axaml:127-180` — the three `AuroraLayerN` rectangles
and their `.aurora-animated` styles (RemEx-ddynd). Reduced motion is implemented by REMOVING the `aurora-animated` class (bound to
`!IsReducedMotion`), which stops the keyframe animation and lets the property fall back to the
rectangle's own `Opacity`. If that static value drifts from the first keyframe, switching reduced
motion on visibly jumps the mesh; if the `Style` selector loses `.aurora-animated`, the mesh keeps
animating with the setting supposedly off — silently, exactly like the chrome-suppression failure
above. `AuroraMeshTests.ReducedMotionFreezesTheMeshAtItsFirstKeyframeInsteadOfHidingIt`
(`remex.desktop.tests/Controls/AuroraMeshTests.cs:137`) pins both halves; a layer added later must
satisfy the same pairing.

### Theme switch removes only the tracked base-theme dictionary, never the whole `Themes/` folder

`remex.desktop/Services/ThemeService.cs` — `ThemeDictionaryPrefix` (:861), `BaseThemeSources` (:874), `SwapBaseTheme` (:922) — RemEx-gcqw5.

A theme switch may remove exactly the base-theme file it is replacing (matched against the literal set
of base-theme URIs), never anything else living under `Themes/` — that folder also holds
`Chrome/WindowChrome.axaml` (merged by `MainWindow.axaml`) and `Shared/FallbackPalette.axaml`. An
earlier version cleared every merged dictionary that was not the override dictionary: it did swap the
theme, but it also dropped `WindowChrome` and `FallbackPalette` on the FIRST switch, never at startup,
with no exception and no log line. `ThemeSwapMergedDictionaryTests.cs`'s
`ANonThemeMergedDictionarySurvivesAFullSwitchAcrossEveryPreset` (:85) asserts a non-theme
dictionary, `WindowChrome`, and the live customization overrides all survive a full cycle through
every preset, and that a key inside the surviving dictionary still resolves through its parent — the
earlier bug left the dictionary in the list but emptied, which a bare "is it still present" assertion
would not have caught.

### Profile writes to disk must be atomic, and a fallback profile must never be persisted

`remex.desktop/Services/DashboardLayoutService.cs` — `LoadAsyncCore` (:377), `SaveAsync` (:546),
`WriteProfileAtomicallyAsync` (:832); `DashboardLayoutClobberTests.cs` — RemEx-8y3qy.

A profile write goes to a temp sibling file, `Flush(true)`s it, then `File.Move`s it over the real
path with retry, because `File.Move` onto a locked destination throws `UnauthorizedAccessException` on
Windows and a bare overwrite can race a reader. `LoadAsyncCore` never persists a fallback profile it
had to synthesise from a failed read — writing one back would make a transient read failure permanent.
`SaveAsync` rethrows on failure rather than swallowing it, so a caller cannot mistake a silently-failed
save for a successful one. The observed failure: the theme reverted to defaults seconds after launch,
because an earlier path treated a fallback load as good enough to save over the real profile.

### `ui-hotreload.ps1 -Stop` relaunches the installed Release host unless `-NoRelaunch`

`scripts/ui-hotreload.ps1` (`Stop-Remex`) and `scripts/ui-palette-sweep.ps1` — RemEx-8q7de review.

The default behaviour of `-Stop` is to bring the installed Release host back up, because that is the
right default for a developer stopping hot-reload to look at something and then wanting their app
back. Any script that stops the host to touch **per-user state** underneath it — a profile file, a
palette override — must pass `-NoRelaunch` and wait for the process to actually exit before writing,
or the relaunching host can read or overwrite the file out from under the script mid-write. The sweep
script's own early draft did not: without `-NoRelaunch`, it would have written an adversarial palette
cell and then had the relaunched host immediately load and re-save the user's *real* profile over it,
racing the sweep's own restore-from-backup step in `finally`.

### The overlay drawer is closed at launch — anything armed "at attach" inside it runs unseen

`remex.desktop/Views/ShellView.axaml.cs` — `ArmNavEntranceOnFirstOpen` (:105);
`remex.desktop.tests/Views/ShellNavEntranceTests.cs` — RemEx-alwfa.2 slice 2.

The nav entrance animation is a once-per-process effect, and the drawer is not open when `ShellView`
attaches. Arming the effect at attach time burns the one-shot while the drawer is still closed, so it
never plays for the user at all — the once-per-process slot was spent on an audience of nobody. The
fix arms it on the FIRST transition of `IsDrawerOpen` to `true` instead. This also means a
`FillMode="Backward"` entrance style cannot be delay-armed after the fact without a visible flash:
`FillMode="Backward"` holds the 0% keyframe until its `Delay` elapses, but only from the moment the
animation is attached — arm it late and the control has already been sitting at its *post*-animation
state, so attaching then produces a visible snap back to the 0% frame before it re-animates forward.

### A posted focus move-in must re-check the overlay is still effectively visible before landing

`remex.desktop/Views/ShellView.axaml.cs` — `OnOverlayToggled` (:511);
`remex.desktop.tests/Views/ShellOverlayFocusTests.cs` — RemEx-ddk6b.

Focus restoration on an overlay closing is posted (`Dispatcher.UIThread.Post`), so by the time it runs
the overlay it was meant for may have been reopened, replaced by another overlay, or the window may
have moved on entirely. The posted callback re-checks that the overlay it captured focus for is still
the effectively-visible one before acting. Restoring focus on close is scoped just as narrowly: only
when the current focus is `null` or still inside the overlay that is closing — never by unconditionally
restoring the last-remembered control, which could steal focus away from `RemoteDesktopView` if the
user had already clicked into it while the overlay was mid-close.

### A ripple started in `PointerPressed` never draws if the same handler hides the window synchronously

`remex.desktop/Views/TrayBalloonWindow.axaml.cs` — `PressSettleDuration` (:53);
`remex.desktop.tests/Views/TrayBalloonMaterialToastTests.cs` — RemEx-alwfa.3 review.

Material's ripple effect is scheduled to draw on a later compositor frame, not synchronously inside
`PointerPressed`. A handler that hides or closes the window in the same `PointerPressed` callback tears
down the visual tree before that frame happens, so the ripple that was supposed to give click feedback
never appears — the interaction reads as unresponsive even though the click itself worked.
`PressSettleDuration` (180ms) delays the window's hide until after the ripple has had a chance to draw
and settle, so the click still visibly registers before the window goes away.

### The tray flyout's cards `ScrollViewer` AND toolbar `ItemsControl` must keep an explicit `MaxHeight` IN TRANSIENT MODE (INVARIANT)

`remex.desktop/Views/TrayFlyoutWindow.axaml` — the cards row's `ScrollViewer`, named `CardsScrollViewer`
(:139-144, `MaxHeight="{x:Static svc:TrayFlyoutGeometry.CardsMaxHeight}"` as its XAML/transient default),
and the toolbar row's `ItemsControl` (:201-203, `MaxHeight="{x:Static svc:TrayFlyoutGeometry.ToolbarContentMaxHeight}"`,
`ClipToBounds="True"`, Flyout D2 .2, RemEx-4kv0g.18.6);
`remex.desktop/Views/TrayFlyoutWindow.axaml.cs` — `ApplyMode` sets `CardsScrollViewer.MaxHeight` at
runtime, per mode (the toolbar row's cap is NOT relaxed by `ApplyMode` — it applies in both pinned and
transient mode, since the toolbar row is fixed-height either way, unlike the cards row);
`remex.desktop/Services/TrayFlyoutGeometry.cs` — `CardsMaxHeight`, `ToolbarMaxHeight` (the OUTER-budget
constant, margin included — used only in `CardsMaxHeight`/`MinHeight`'s own arithmetic, never in XAML),
`ToolbarContentMaxHeight` (the XAML `MaxHeight` value itself — content only, no margin; see that
constant's own doc for why the two must not be the same number), `ToolbarTopMargin`, `DefaultWidth`,
`HeaderHeight`, `CardRowHeight`, `FixedMargins`, `ChromeSideInset`/`ScrollBarAllowance`/`CardPitch`/`CardsPanelInset`;
`remex.desktop.tests/Views/TrayFlyoutSurfaceTests.cs`, `remex.desktop.tests/TrayFlyoutGeometryTests.cs` —
RemEx-4kv0g.18.2, extended RemEx-4kv0g.18.6.

The transient popup uses `SizeToContent.Height` (`TrayFlyoutWindow.axaml.cs`'s `ApplyMode`), which gives
the window no natural bound of its own — it just grows to whatever its content measures. A `ScrollViewer`
with no `MaxHeight` has no bound either, so without one a long pinned-sensor list grows the popup past the
screen with many pins and nothing throws: no exception, no log line, just a window taller than the
monitor. `CardsMaxHeight` is that cap; its XML doc on `TrayFlyoutGeometry` carries the full arithmetic
(header + toolbar + margins subtracted from `TrayFlyoutGeometryValidator.MaxHeight`, rounded down to a
whole number of 200×150 card rows) so it can be re-derived if the window's chrome changes. Since
RemEx-8tm8l the cards widen past 200 to fill each row (`TrayFlyoutGeometry.CardSlotWidth`, bound to the
cards `WrapPanel`'s `ItemWidth`), but their height stays 150 on purpose: a height that followed the
width would make this cap and `MinHeight` stop being whole card rows.

**The toolbar `ItemsControl`'s `WrapPanel` has the identical problem, for the identical reason
(RemEx-4kv0g.18.6).** Unlike the cards row, there is no scroll affordance on this row at all — enough
ticked launcher apps push the toolbar to a third wrapped line, and without a `MaxHeight` that third line
just grows the popup, silently, exactly the way an unbounded cards list used to. `ToolbarContentMaxHeight`
(80, two content rows) is what actually caps it in XAML; a third row's shortcuts are clipped by
`ClipToBounds="True"`, not scrollable — this is a deliberate difference from the cards row, not an
oversight: a toolbar row is meant to stay a single glance, and Personalize's own Apps checklist (Flyout
D2 .2) is where a user manages which apps are ticked if the row is crowded, not a scrollbar on the popup
itself.

**`ToolbarMaxHeight` (96) MUST NEVER BE THE XAML `MaxHeight` VALUE (fix round 1, RemEx-4kv0g.18.6
review, MEDIUM).** Avalonia's `MaxHeight` bounds an element's own content box — it does not include the
element's externally-applied `Margin`. `ToolbarMaxHeight` folds the toolbar row's `Margin="0,16,0,0"`
into its number (16 margin + two 40px rows = 96) because the *surrounding* budget arithmetic
(`CardsMaxHeight`, `MinHeight`) needs the row's whole occupied footprint, margin included. Wiring that
same 96 into the `ItemsControl`'s own `MaxHeight` attribute left 16px of slack inside the cap — the
control's two real rows (80px) fit under a 96px content-box limit with room to spare, so a third row
started rendering and got clipped 16px into itself instead of not rendering at all. `ToolbarContentMaxHeight`
(`ToolbarMaxHeight` minus `ToolbarTopMargin`, = 80) is the content-only number XAML actually needs.
Pinned by `TrayFlyoutGeometryTests.ToolbarContentMaxHeightIsExactlyTwoContentRows`.

**`CardsMaxHeight` applies ONLY while transient — never while pinned (fix round 2).** The first cut of
this guard left it capping the pinned case too, via the cards row's own `RowDefinition` switching to
star-sized while the `ScrollViewer`'s XAML `MaxHeight` attribute stayed in force underneath it: on a
resized-tall pinned popup (measured at 894×787) the cards region still topped out at ~486, with rows
hidden behind a needless scrollbar and ~50px of empty space below the tiles. `ApplyMode` now relaxes
`CardsScrollViewer.MaxHeight` to `double.PositiveInfinity` whenever the window is pinned, so the
star-sized row genuinely takes whatever height the resize leaves free, and re-tightens it to
`TrayFlyoutGeometry.CardsMaxHeight` on every transition back to transient.

Also fix round 2: `DefaultWidth` (528) and `TrayFlyoutGeometryValidator.MaxWidth` (944) must stay wide
enough for two and four 212px card columns respectively — see `TrayFlyoutGeometry`'s
`ChromeSideInset`/`ScrollBarAllowance`/`CardPitch`/`CardsPanelInset` XML docs and
`TrayFlyoutGeometryTests.DefaultWidthFitsTwoCardColumns`/`MaxWidthFitsFourCardColumns`. The flyout's
geometry (position, size, pin state) persists to `C:\ProgramData\RemEx\tray_flyout_layout.json` —
machine-wide, not per-user (`RemexDataPaths.ResolveDirectory` relocates Windows stores there).

**`MinHeight` must fit at least a header, a toolbar row and one full card row (fix round 3, closes
RemEx-4kv0g.18.4, Flyout D2 .2, RemEx-4kv0g.18.6).** It was 240 from the flyout's very first cut — small
enough that a pinned, resized-down popup showed the header and the toolbar row with NO card row beneath
them at all, even with sensors pinned. A popup whose entire reason to exist (the cards) can be resized
away completely is not a floor, it is a hole. `TrayFlyoutGeometryValidator.MinHeight` is now a compile-time
constant expression built from `TrayFlyoutGeometry.HeaderHeight + TrayFlyoutGeometry.CardRowHeight +
TrayFlyoutGeometry.ToolbarMaxHeight + TrayFlyoutGeometry.FixedMargins` (= 382, not a bare literal that
could drift from the pieces it is built from) — see that constant's own derivation comment.
`TrayFlyoutWindow.axaml`'s `MinHeight="382"` duplicates it, for the same reason `DefaultWidth`/`MaxWidth`
are duplicated below. Pinned by `TrayFlyoutGeometryTests.MinHeightShowsOneCardRow`.

This is also why the geometry limits are duplicated in XAML and `TrayFlyoutGeometry`/
`TrayFlyoutGeometryValidator` on purpose (see the comment at `TrayFlyoutWindow.axaml:17-22`): Avalonia
needs literal `Width`/`Height`/`MinWidth`/`MinHeight`/`MaxWidth`/`MaxHeight` values in XAML, so those and
the C# constants have to be changed together — the duplication is intentional, not drift to be cleaned up.

### The Personalize sheet must keep an explicit height bound (INVARIANT)

`remex.desktop/Views/ShellView.axaml` — `material:SideSheet.SideSheetContent` presents
`<views:PersonalizationPanelView>` directly (RemEx-4kv0g.4.3; previously a `ScrollViewer` wrapped it),
with a `PersonalizationPanelView.MaxHeight` `MultiBinding` through `SubtractHeightConverter`
(`ConverterParameter="5"`, inputs `SettingsSideSheet.Bounds.Height` and `SettingsSheetHeader.Bounds.Height`
— the header now includes the LANGUAGE card, so its `Bounds.Height` covers that for free);
`remex.desktop/Views/PersonalizationPanelView.axaml` — a `Grid RowDefinitions="*,Auto"`, the
UserControl's ONLY child, no wrapping `StackPanel`, with the `TabControl` in the star row and the
Reset footer in the `Auto` row (RemEx-4kv0g.4.3 fix round 1); `remex.desktop/Views/Personalize/*.axaml`
— each of the five tabs roots in its own `ScrollViewer HorizontalScrollBarVisibility="Disabled"`;
`remex.desktop.tests/Views/ShellSettingsSideSheetTests.cs` (`TheSideSheetContent_HasABoundedScrollableHeight`,
`ThePersonalizeHost_HasNoWrappingScrollViewer`, `EveryPersonalizeTabFile_RootsInItsOwnScrollViewer`),
`remex.desktop.tests/Views/PersonalizationSheetLayoutTests.cs`.

**The gutter on the bounded element has to be `Padding`, never `Margin`.** Avalonia's `MeasureCore`
clamps a control to its `MaxHeight` BEFORE adding margin, so a `Margin` on `PersonalizationPanelView`
sits OUTSIDE the bound and overflows the sheet by exactly twice its value, while `Padding` (a
`UserControl` is a `ContentControl`) sits INSIDE the bound and is measured as part of what `MaxHeight`
clamps — margin sits outside the bound, padding inside; the gutter must be padding. Review round 3's
HIGH finding was `Margin="20"` on that element, overflowing the sheet by 40px.

`material:SideSheet` presents `SideSheetContent` through `PART_SideContentPresenter`, a child of a
vertical `StackPanel#PART_SideSheetPanel` — and a vertical `StackPanel` measures every child with
INFINITE available height regardless of what height the `StackPanel` itself was given. Without an
explicit `MaxHeight` on the content, it sizes itself to its full content instead of scrolling, and
`PART_RootBorder` is `ClipToBounds="False"`, so nothing even clips it — it runs off the window. This
is the same failure mode `ShellView`'s own history already lived once (its `ScrollViewer.MaxHeight`,
review round 1, HIGH) and the tray flyout's `CardsScrollViewer`/toolbar `ItemsControl` guard above
lives for the identical reason: an unbounded scrolling surface inside Avalonia is not a smaller bug,
it is no bound at all.

**The bound has to reach the `TabControl`, not stop at the `UserControl` wrapping it.** A `MaxHeight`
on `PersonalizationPanelView` only constrains what its OWN content is measured against — if that
content were a `StackPanel`, the same infinite-height StackPanel quirk described above would
reproduce ONE LEVEL DEEPER, inside the host itself, and the selected tab's content would grow
unbounded again despite the outer `MaxHeight` looking correct. This is why `PersonalizationPanelView.axaml`'s
root is a `Grid RowDefinitions="*,Auto"` whose star row hands the bound straight to the `TabControl`
and whose `Auto` row holds the Reset footer below it (RemEx-4kv0g.4.3 fix round 1) — see that file's
own comment — and why any future change that adds MORE sibling content next to the `TabControl` must
keep using a `Grid` with a star row for the `TabControl`, never a `StackPanel`.

---

## Dashboard layout — card colour presets

### `ApplyPersistedSensorState` — a null `CardTheme` keeps the live theme (INVARIANT)

`remex.desktop/ViewModels/CanvasDashboardViewModel.cs:1457` (`ApplyPersistedSensorState`). A
`CardState.CardTheme` of `null` means *this source carries no colour information* and the loader
must leave `SensorViewModel.Theme` alone. Only an explicit theme changes it: the `"Default"` preset
(`SensorCardTheme.Presets[0]`, the follow-theme state) resets a card to the palette, any other preset
applies verbatim.

**The failure (2026-09-13).** Spec B's whole-branch review misread the pre-branch loader
(`if (state.CardTheme is not null) sensor.Theme = state.CardTheme;`) as "always reset", and the
fix round made it reset to follow-theme on null. A profile whose cards carried no themes then
reached `ApplyProfile` from the host's layout copy, every one of Connor's 44 cards silently reset to
follow-theme, `ApplyProfile`'s save wrote the theme-less cards over the per-user file, and the five
rotating autosaves were all taken after the wipe. No exception, no log line — the canvas just
looked "themed". Presets were rebuilt by hand from screenshots and a two-day-old export.

**What produced the theme-less profile was never found, but its route is gone.** The only path that
handed `ApplyProfile` a layout other than this device's own was the host's mirror copy
(`host_dashboard_layout.json`), sent back on connect and by the dashboard's Sync button. RemEx-sydzo
removed the button, the mirror and its messages. `ApplyProfile` is now reached only from
`ReloadFromPersistedLayout` after a savefile import, and an older savefile can still carry theme-less
cards, so every write path below still assumes such a profile can arrive.

**The other half.** `remex.desktop/ViewModels/CanvasCardViewModel.cs:293` (`ToCardState`) writes a
themed card as the explicit `"Default"` preset, **never as null** — null would make "themed"
indistinguishable from "unknown" on the way back in. Pinned by
`remex.desktop.tests/ViewModels/CardThemePersistenceTests.cs`
(`ALiveSunsetSensor_KeepsSunset_WhenTheIncomingStateCarriesNoColourInformation`,
`AThemedSensor_WritesCardThemeAsTheExplicitDefaultPreset_NeverNull`). If a future change needs null
to mean something else, it must first make every layout source carry themes. (RemEx-4kv0g.3)

### Theme-less layouts are merged, never written over a themed one (INVARIANT)

`ApplyProfile`'s trailing save
(`remex.desktop/ViewModels/CanvasDashboardViewModel.cs`, the `_layoutService.RequestSave(localBase with
{ Cards = carried, … })` at the end of the method, ≈:1870) used to write the incoming cards verbatim
into the per-user file. Pre-spec-B that was survivable only because the ViewModels still held the
presets and the next `TriggerSave` pushed them back — a latent hazard the loader change turned into
a wipe.

That write now goes through `CardThemeMerge.PreserveThemes` (`remex.core/Models/CardThemeMerge.cs`):
a card arriving with `CardTheme == null` takes the theme the destination already holds for it (by
`CardId`, then `SensorId`); a card arriving with a theme wins. `ApplyProfile` merges against the live sensors
first, then the per-user file. Pinned at the call site by
`remex.desktop.tests/ViewModels/CardThemeSurvivesSyncTests.cs`
(`AHostSyncWithNoCardThemesLeavesTheLiveSensorAndThePerUserFileOnTheirPresets`), and for the helper
by `remex.core.tests/CardThemeMergeTests.cs`. Do not "simplify" the call site back to a verbatim
`profile.Cards`. The host store's half of this guard went with the store (RemEx-sydzo). (RemEx-4kv0g.3)

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

- **Host** allows auto-fetch only when **both** remote and local addresses are Tailscale CGNAT
  (`100.64.0.0/10` / `fd7a:115c:a1e0::/48`). Requiring *both* ends defeats a LAN attacker spoofing a
  `100.64.x.x` source. Handles IPv4-mapped addresses (`::ffff:100.64.x.x`) for Kestrel.
- **Android** allows auto-fetch only for a Tailscale address / `*.ts.net` MagicDNS hostname **AND**
  `TRANSPORT_VPN` active. The VPN-active check is mandatory: a Tailscale-looking address with no live
  tunnel must NOT unlock auto-fetch.
- **Loopback is NOT trusted for the PIN, on either side — do not add it back (RemEx-fd7e).** It used to
  be, "for the PC's own UI". But the UI runs in the agent process and reads the PIN in-process
  (`ConnectionViewModel.AttachEmbeddedPairingService` → `IPairingService.TryGetActivePinInfo`; the old
  `IpcPairingPinQueryService` wrapper was deleted in RemEx-f2dwg); it never used the socket. So
  the loopback branch served only *other* local processes — including unelevated ones — handing them
  the live PIN of the elevated agent's open pairing window. Nothing at the socket tells the UI apart
  from them. `LoopbackPairingPinTests` (host, incl. the real `/ws` map site) and
  `TransportTrustPinAutoFetchTest` (Android) fail if the loopback branch comes back. Note this is only
  the PIN gate: loopback still satisfies the `/ws` pairing gate (`isLoopback` at the map site), which
  is a separate decision (RemEx-4215 / RemEx-4u0d).

`requiresLocalNetworkAccess(host)` returns `false` for loopback/Tailscale/`*.ts.net` targets, gating
the `NEARBY_WIFI_DEVICES` / `ACCESS_LOCAL_NETWORK` runtime permission requests. Changes here silently
break Tailscale users (spurious permission prompts) or open LAN permission gates.

**Both sides are security-critical and must be kept in sync; changes require explicit user sign-off.**

### `PinnedHostStore` — Tink AEAD corruption recovery

`aead()` uses a double-checked lock. On init failure — lock-screen key invalidation, app data cleared
with the Keystore intact — it clears the `remex_tink_prefs` SharedPreferences keyset, clears both
DataStores, and retries. **Without this the app is permanently bricked.** The keyset is Android
Keystore-backed; no deprecated `EncryptedSharedPreferences` or `MasterKey` APIs.

### Routine store — its own keyset, a REPORTED reset, and an unreadable store is never overwritten

`remex.android/.../routines/RoutineAndroidStorage.kt:118` (`recover`, the `KEY_LOSS_MARKER` at
`:166`), `routines/RoutineRepository.kt:178` (unreadable → no writes), `:219` (`reportKeyLoss`),
`routines/RoutineStoreDocument.kt:90` (`decode` returns null, never a default); pinned by
`RoutineRepositoryTest`, `RoutineStoreDocumentTest`, `BackupRulesRoutineExclusionTest` —
RemEx-pp0rt.5, spec §6.7, §6.8, T12.

The routines use a separate Tink keyset (`remex_routines_keyset` in `remex_routines_tink_prefs`),
so `PinnedHostStore` above is untouched and one keyset's failure cannot cost the other. Its recovery
mirrors the pairing store's — clear the keyset and all three routine stores, retry — with two
differences, each of which exists because the failure would otherwise be invisible. **The reset is
reported:** a marker is committed to the prefs file BEFORE the stores are cleared, and the repository
turns it into the `store_reset` banner and history record before clearing it; a lost pin re-pairs
visibly, but a lost routine just never fires again. **An unreadable document is never replaced**:
a store that decrypts but does not parse, or a known key of the wrong type, loads as `UNREADABLE`
and every write is refused until the user picks Reset (the same rule as "Profile writes to disk must
be atomic, and a fallback profile must never be persisted" above). All three stores and the prefs file are excluded from cloud backup and
device transfer in both rule files; a routine store restored without its keyset reads as corrupt.

### `NfcRoutineActivity` must keep `DISPATCH_NFC_MESSAGE` (INVARIANT)

`remex.android/app/src/main/AndroidManifest.xml:212-221` (the activity, its
`android:permission` at `:214`), `routines/nfc/NfcRoutineActivity.kt`, `routines/nfc/NfcRoutineTag.kt:78`
(`NfcTokenVerifier.verify`); pinned by `RoutineManifestExportTest`, `NfcTokenVerifierTest` —
RemEx-pp0rt.7, spec §8.3.2, T1-T3.

Tag dispatch only reaches an EXPORTED activity, so the NFC tag target is the one routine component
that is exported, and the only thing between "any app on the phone" and "run this routine" is
`android:permission="android.permission.DISPATCH_NFC_MESSAGE"`, which only the system NFC service
holds. Drop it, or copy the `remex://routine` intent filter onto any other component, and every app
can fire `remex://routine/<id>?t=...` at RemEx. Nothing breaks visibly when that happens: tags keep
working, which is exactly why it would not be noticed. Every other routine component (the shortcut
target, the confirm activity, the widget receiver, the network receiver, the notification action
receiver) stays `exported="false"`; the manifest test fails if one flips. The one other allowlisted
export is the widget's configure activity (launchers start it directly): it can only bind a widget of
RemEx's own provider to a routine the person picks, and the test scans it for any path to a run. Behind the permission the
tag is still untrusted: the token is compared in constant time, a locked phone is refused
(`nfc_device_locked`), and a routine runs at most once per 10 s per tag tap.

### A routine shortcut runs only with a valid HMAC `sig` (INVARIANT)

`routines/manual/RoutineShortcutActivity.kt:35` (the check), `routines/manual/RoutineShortcutSignature.kt:27`
(`verify`, constant time), the key from `RoutineSecretStore.shortcutKey`; pinned by
`ShortcutSignatureTest`, `RoutineManifestExportTest` — RemEx-pp0rt.7, spec §8.3.3, T1, R-SEC-02.

`RoutineShortcutActivity` is not exported, and it STILL verifies `sig = HMAC-SHA256(shortcutKey,
routineId)` before it runs anything. The launcher replays whatever intent the shortcut was published
with, forever: a shortcut for a routine that was deleted and whose id came back, one published before
a keyset reset, or one some launcher bug hands over with altered extras must not run a routine the
user never pinned. A missing or wrong `sig` opens the routines list with "That shortcut no longer
works" and runs nothing. Do not "simplify" this to an id lookup because the component is private; the
signature is what binds a shortcut to the routine it was made for.

### Home presence: PendingIntent network callbacks only report availability, so leave detection needs the fallback (INVARIANT)

`remex.android/.../routines/home/PresenceRegistrations.kt:22` (`periodicCheck`), `:62`
(`PresenceRegistrar.apply`), `routines/home/HomePresence.kt:243` (`sync`); pinned by
`NetworkRegistrationTest` (`LeaveDetectionScheduling`), `PresenceStateMachineTest` — RemEx-pp0rt.8,
spec §8.3.1 "Event sources", R-SYS-15.

`registerNetworkCallback(request, PendingIntent)` delivers only the "a network is available" edge
("Action to perform when the network is available"). It is how a dead RemEx process hears about
arriving home. It is NOT how it hears about leaving: with mobile data always on, walking out of Wi-Fi
range makes no new network available, so no intent is ever sent, and a `home.leave` routine would
simply never fire while RemEx is not running. Nothing logs that; the routine just stays silent. The
two other sources exist for exactly that case: the in-process `NetworkCallback` (`onLost`) while the
process lives, and the 15-minute `routine-presence-check` periodic work, scheduled only while a home
is HOME and an enabled leave routine exists (and cancelled otherwise, for the §12 battery budget).
Do not drop the periodic check as "redundant with the callback", and do not widen it to run when no
leave routine exists.

### Home presence: registrations die on reboot and on update (INVARIANT)

`AndroidManifest.xml:245` (`RoutineNetworkReceiver`: BOOT_COMPLETED, MY_PACKAGE_REPLACED),
`AndroidManifest.xml:63` (`RemexApplication`), `routines/home/HomePresence.kt:78` (`onProcessStart`),
`:243` (`sync`, `force`), `:357` (the receiver; `forgetPresence` at `:382`),
`routines/home/PresenceRegistrations.kt:62` (`PresenceRegistrar.apply`), `:110`
(`PresenceEventRouter.actionsFor`); pinned by `NetworkRegistrationTest`, `PresenceStateMachineTest` —
RemEx-pp0rt.8, spec §8.3.1, R-SYS-16.

The platform drops every network callback an app registered when the phone reboots and when the app
is updated, and a PendingIntent registration is not restored by anything. So the restart paths
re-register with `force`: process start (`RemexApplication`, only when armed), `BOOT_COMPLETED` and
`MY_PACKAGE_REPLACED` never trust the stored "applied" plan, because that record is exactly what
survives a reboot while the registration does not. After a reboot the presence is reset to UNKNOWN,
and UNKNOWN only becomes AWAY on a qualifying foreign network (stamped with the time, so a home join
within 300 s is a flap), so a phone that reboots at home learns "home" without firing `home.arrive`
(T20, L9). Removing the receiver, its intent filters or `RECEIVE_BOOT_COMPLETED` presents as home
routines that worked yesterday and never fire again.

**And the opposite mistake loops.** Re-registering the PendingIntent makes ConnectivityService drop
the old request and deliver "available" AT ONCE for a Wi-Fi that already matches. If anything that
the broadcast or an evaluation triggers re-registers, the result is broadcast -> register ->
broadcast forever, and with `REPLACE` on the evaluation work each broadcast also cancelled the
evaluation it had queued, so presence never moved: silent, and a battery drain (S3 review BLOCKER).
So: the network broadcast only queues an evaluation (`APPEND_OR_REPLACE`), an evaluation never
registers, an unchanged plan applies nothing, the plan is stored before registering (so the
registration's own broadcast finds it armed), and every path checks the plain-pref armed flag first
(§12: nothing armed reads no store and loads no native core).

### `PinnedHostStore` — reconnect-secret persistence

After a successful pairing, `RemexClientManager` extracts `reconnectSecret` from the
`OK:hostId|spki|reconnectSecret` result and stores it via `setReconnectSecret`. On reconnect,
`getReconnectSecret()` supplies it to `RemexCoreClient` to answer the host's proof-of-possession
challenge. **Without a stored secret the host rejects the reconnect and forces a re-pair.** Secrets
live in a dedicated DataStore (`remex_reconnect_secrets`, separate from `remex_pinned_hosts`),
encrypted with Tink AES-256-GCM AEAD using `hostId` as associated data. (PAIR-1 / RemEx-xuo)

**Resolve the secret SPKI-alias FIRST, address alias only as the legacy fallback — on BOTH channels.**
Pairing writes the same secret under three aliases (`hostId`, host address, SPKI hash), each sealed
with its own alias as associated data, so they are three independent records rather than three views
of one. Only the SPKI record is refreshed by *every* pairing; the address record is refreshed only by
a pairing that happened to use that address. So a re-pair reached over a different address — LAN today,
Tailscale tomorrow — leaves a STALE secret under the old address key.

Both consumers must resolve in that order: `RemexClientManager.kt` (control `/ws`, RemEx-060g) and
`FileTransferChannelClient.resolveReconnectSecret` (binary `/ws/files`, RemEx-6bfyt). The second one
was missed for a release, and reverting either to a bare `getReconnectSecret(context, host)` compiles
fine and reintroduces the failure.

**It presents as silence about the wrong subsystem.** The stale secret is a real secret, so the client
computes a well-formed HMAC and reports its channel open; the host refuses proof-of-possession, never
registers the channel, and the transfer fails much later with *"The binary file channel is not
connected."* — a message naming a socket, while `/ws` keeps streaming telemetry because it held the
fresh secret. A phone that is visibly connected cannot move a byte, and nothing anywhere says
"wrong credential". Keep the address alias as the fallback: pairings predating RemEx-060g have no SPKI
record, and requiring one would brick them rather than cost them a re-pair.

### `ConsentRoutePolicy.Route` — the branch ORDER is the rule

`remex.agent/Services/FileTransfer/ConsentRoutePolicy.cs`. Three checks, and each one must stay where
it is: **asker-gone → deny**, then **kind** (`full_browse` → Desktop), then **capability**.

- **Deny first.** A kind check placed ahead of the connected check turns a deny into a PC dialog for
  exactly the request where durable trust is at stake — a user answering "allow" would be granting
  whole-filesystem access to a device that is not there.
- **Kind before capability.** Full browse is a standing grant over the whole machine and is authorised
  at the machine, whether or not the phone could render the prompt (Connor's decision, 2026-08-10,
  RemEx-6bfyt). Per-file consent stays on the phone, because that is the case where a PC prompt waits
  in front of nobody (RemEx-mneb, the failure that produced the phone route).
- **Ordinal.** Only the exact `full_browse` token diverts; an unknown kind keeps the old capability
  behaviour rather than falling into the PC branch by accident.

Reversing kind and capability **compiles cleanly and passes most of the suite**, and the resulting
failure presents as a transfer refused with nothing saying why.

**The Desktop route requires a SURFACED owner window, and it must go through
`BringMainWindowToFront()`.** `App.axaml.cs` `ShowFileConsentDialogAsync` calls that helper BEFORE
reading `desktop.MainWindow` — never checks it for non-null first, never uses it as a gate. Avalonia's
`ShowDialog` throws on a non-visible parent, and RemEx can be in FOUR distinct states here (P1-29
added the fourth):

- **never constructed** — a `--minimized` logon start (`scripts/autostart-remex.ps1`) now defers
  building `MainWindow` entirely, not just skips showing it (P1-29). `BringMainWindowToFront()`'s
  `desktop.MainWindow ??= new MainWindow {...}` is what makes this safe — it is load-bearing for
  EVERY consumer, not an edge case fallback, and any code that reads `desktop.MainWindow` without
  going through this helper first can no longer assume it is non-null;
- **constructed but never shown** — the pre-P1-29 minimized-start state, still reachable if
  something else constructs the window before it is ever displayed;
- **hidden to tray** — close-to-tray;
- **minimized** — which reports `IsVisible == true`, so a `Show()`-only guard skips it entirely and
  `Activate()` alone leaves it in the taskbar.

That third one is why a local `Show()`/`Activate()` pair is not good enough and the helper is
mandatory: all three of its steps are load-bearing and none implies another (see its own XML doc, and
RemEx-b3bi). The catch denies fail-closed with no reason code, byte-identical to the user tapping
Deny — so the prompt nobody could see becomes a refusal nobody can explain, in whichever window state
was missed. This was harmless while the Desktop route only served pre-capability phones; routing full
browse here made it the only path for that grant. `OpenMainWindowHasOneCopyTests` does **not** catch a
partial copy: it scans only files that already set `MainWindow.WindowState`, so a two-step copy is
invisible to it.

### Proof-of-possession reconnect auth

`PairedClientRegistry` stores a 32-byte ECDH/HKDF session key per client. Reconnect auth is an
HMAC-over-nonce challenge, **NOT** a bare clientId lookup. `RegisterClient(string, byte[])` is the
production path.

### `PhoneFileRelay` — the PC browsing a phone accepts replies only from the phone it asked

`remex.agent/Services/FileTransfer/PhoneFileRelay.cs` (`TryDeliverReply`, `Connection.SendAsync`),
called from the eight `file_*_response` cases in `PingPongHandler.HandleAsync` — RemEx-xt0af,
widened to `file_manage_request` in RemEx-fgmne.

The PC's File Transfer screen sends `file_*` requests down a paired phone's session: the read-only ones,
and, only to a phone whose owner allows it, `file_manage_request`. The rules below make that safe, and each
one fails SILENTLY if dropped — the screen just shows a listing, or a rename quietly works on a phone whose
owner never agreed:

- **Replies from loopback or an unproven session are refused** before anything else (RemEx-4215's
  rule). Without it any local process, unelevated included, can open `/ws` on 127.0.0.1 and put its own
  file list on screen under the phone's name. The call site must pass the connection's REAL
  `isLoopback`/`identityProven`: `PhoneRelayReplyDispatchTests.AReplyFromAPinPairedLoopbackConnection_IsNotDelivered`
  drives a PIN-paired loopback connection through `HandleAsync` and goes red if the case block
  hardcodes `isLoopback: false` (defect-injected in RemEx-xt0af).
- **The sender must still be paired** when the reply arrives (`IsClientPaired`, re-checked in
  `TryDeliverReply`), so a reply in flight across an unpair is dropped.
- **A reply is matched only against requests sent to the SENDER's own client id** (the `continue` in
  `TryDeliverReply`'s loop). Delete it and a second paired phone that guesses a request id answers for
  the first. `PhoneFileRelayTests.Reply_FromADifferentPairedPhone_IsDropped` goes red (defect-injected
  in RemEx-xt0af).
- **Only the allowlisted request types are relayed** (`RelayedRequests`, derived from `RequestForReply`,
  `PhoneFileRelay.cs:58-79`): the seven read-only ones plus `file_manage_request`. `file_root_manage_request`
  (which folders a phone shares) and hashing stay out; `TheAllowlist_HoldsTheSevenReadOnlyTypesAndManage_AndNotRootManagement`
  and `RootManagement_StaysRefused_EvenWhenThePhoneAllowsChanges` pin that. Widening it again is a product
  decision, not a fix.
- **Outbound requests carry no `ClientId`.**
- **A `file_manage_request` goes out only after THIS phone's own roots reply said `pcChanges: true`**
  (`Connection.SendAsync`, the `_phoneAllowsChanges` gate at `PhoneFileRelay.cs:530-540`, set in `TryComplete`
  at `PhoneFileRelay.cs:605`, which only a reply that already passed `TryDeliverReply`'s loopback, proven and
  paired checks can reach). Delete the gate and the PC offers and sends renames and deletes to a phone whose
  owner never turned them on, and to every older phone. `Manage_BeforeThePhoneHasSaidItAllowsChanges_...`,
  `Manage_WhenThePhoneSwitchIsOff_...` and `APlantedRootsReply_FromLoopbackOrAnUnprovenSession_CannotTurnManageOn`
  go red (gate defect-injected in RemEx-fgmne).
- **And its operation, names and paths are validated before the wire** (`ManageRefusal`,
  `PhoneFileRelay.cs:447`): one of five operations, a root id, `FilePathValidation.IsValidRemoteName` /
  `IsValidRemoteRelativePath`, and never the shared folder itself.
  `Manage_WithAnUnsafeNameOrPathOrOperation_IsRefusedEvenWhenThePhoneAllowsChanges` covers each case.

**On the phone, the switch is the guard, and it is checked first.** `FileHostHandler.handleManage`
(`FileHostHandler.kt:399`, `if (!rootsProvider.isPcChangeAllowed())`) refuses every operation, mkdir
included, BEFORE anything is resolved, and `AndroidFileTransferHost` reads the person's
`pcMayChangeFilesFlow` into `pcMayChangeFiles` (`AndroidFileTransferHost.kt:68`, default `false`) and serves
it through `isPcChangeAllowed()` (`:176`), which also gates `canRename/canMove/canDelete` on each root
(`:151-153`). The interface default is `false` (`FileSystemFacade.kt`, `SharedRootsProvider.isPcChangeAllowed`),
so a provider that forgets to override it refuses everything instead of allowing it. Remove the check and the
PC's relay gate becomes the only thing standing between a PC and the phone's files: the PC relay can be
stale (the person turned the switch off while the screen was open) and cannot be trusted by a forged request.
`FileHostHandlerTest.everyManageOperation_withTheSwitchOff_isRefusedWithAPlainReasonAndChangesNothing` and
`theSwitch_isReadOnEveryRequest_notOnceAtStartup` go red (defect-injected in RemEx-fgmne). The same function
refuses `..`/backslash/NUL/empty-segment paths (`SharedPathPolicy.segments`, `:420`) and the shared folder
itself (`SHARED_FOLDER_ITSELF_MESSAGE`, `:431`) before the resolver runs. It also refuses the whole-device
(full-browse) volume (`FULL_BROWSE_READ_ONLY_MESSAGE`: only folders the person shared by name are writable
from the PC; `theWholeDeviceView_isReadOnlyFromThePc_forEveryOperation`), and never sends a SAF provider's
exception text to the PC (`reportManageFailure`; the text can hold `/storage/emulated/0/...`).

**Copy, move and replace on the phone** (`FileHostHandler.copyOrMove`) must never touch what exists until the
new bytes are safe: a destination that is the source is refused, a folder is never replaced, and a replace
copies to a temporary sibling first and only then swaps the old file out (restoring it on any failure); the
partial target is deleted in a `finally` on failure AND cancellation, and the copy runs on the host's IO scope
in chunks that call `ensureActive()` so it cannot hold up the control-message collector. Each is pinned by a
`FileHostHandlerTest` case (`copyOrMoveOntoItself_...`, `aReplaceNeverDeletesAFolder_...`,
`aReplaceThatFailsMidCopy_...`, `aCopyThatIsCancelled_...`, `aLongCopy_doesNotHoldUpTheRequestsBehindIt`;
each defect-injected red in RemEx-fgmne). Names the PC invents are checked with `isSafeNewName` /
`FilePathValidation.IsValidRemoteName` (UTF-8 bytes, no control or Unicode format characters); names of files
that already exist use the lenient rule so a file with U+200D in its name can still be browsed and deleted.

On the phone, `SharedPathPolicy` (both `SafFileSystemFacade.resolve` and the v2 `resolveDocument`)
resolves only roots the person shares RIGHT NOW. `fromTreeUri` opens any tree the app still holds a
grant for, so removing the root check reopens the whole-device folder after the person turned
whole-device browsing off. The RULES are pinned by `SharedPathPolicyTest`; the two CALL SITES are
SAF-bound and have no unit test, so a removed call there stays green — check them by eye.

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

`EngageForRemoteControl(clientId)` / `Disengage(clientId)` maintain an `_engaged`
HashSet; the first engage and last disengage trigger the action. On engage it takes a handle-based
power request (`PowerRequestKeepAwakeBackend`: `PowerCreateRequest` + `PowerSetRequest` for
`PowerRequestSystemRequired` and `PowerRequestDisplayRequired`); on last disengage it clears both and
closes the handle.

**Do not go back to `SetThreadExecutionState`.** Its state is per calling thread, and engage and
disengage run on different thread-pool workers (`RemoteDesktopHandler` awaits the whole stream in
between), so the release from the second thread cleared nothing and the hold outlived the session
(PERF-TRACKER P1-10). Confirm by hand with `powercfg /requests` (admin): the host process is listed
under DISPLAY and SYSTEM while a client is connected, and gone after.

**No `tscon`, no `WTSDisconnect`.** The guard lives inside the user session and must never disconnect
or reconnect it — doing so produces a black screen plus access-denied input. It never changes lock
state; it only holds the keep-awake request. `SessionGuardPolicy` and `SessionGuardAction` are deleted. Every
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

Resolves via the concurrent, cancellable `registerServiceInfoCallback` — unconditionally, because
minSdk is 34. **Always acquires a `WifiManager.MulticastLock` for mDNS reliability**; that is the
half of this guard that still bites, and dropping it makes discovery fail silently on some networks.

The pre-34 half expired rather than being violated: this used to fall back to `resolveService()`
serialised behind a process-wide `Mutex`, because pre-34 allows only one in-flight resolve and a
second returns `FAILURE_ALREADY_ACTIVE`. At minSdk 34 that branch could not be selected on any
device the app installs on, so it was deleted with the mutex in RemEx-jcl4p. **If minSdk ever moves
DOWN, the mutex must come back** — the constraint is real, it is just unreachable.

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

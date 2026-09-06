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

### Never announce `file_transfer_complete` before the peer has acked the data

Bulk file data travels on `/ws/files`; `file_transfer_complete` travels on the control `/ws`. **TCP
orders bytes only within one connection, never between two.** A sender that announces completion the
instant its last frame is *enqueued* lets the tiny completion overtake the still-in-flight bulk
bytes. The receiver then tears its sink down and finalizes a zero-byte transfer, reporting
*"Transfer incomplete."* while the data is literally still arriving.

**All three senders must drain first, and all three now do:**

- C# host → phone: `TransferSessionManager.WaitForFinalAckAsync` (`TransferSessionManager.cs:1427`),
  called at `TransferSessionManager.cs:1356` before the completion is sent.
- Kotlin phone → host, upload: `FileTransferEngine.runUpload` (`FileTransferEngine.kt:323`).
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
contract with the other end, written down there: `TransferSessionManager.cs:60` — *"a declared size of
ZERO is legitimate — a phone reports it for a content URI whose length it cannot read"* — and the PC
gates both its overshoot bound (`:345`) and its completion check (`:385`) on `ExpectedSize > 0`. So
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
each other: the PC acks on `Final || interval` (`TransferSessionManager.cs:1149`), so on an
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

**One mutation in this function is known NOT to be caught, and it is recorded rather than hidden:**
flipping `HostSendSession.ackSignal` from `CONFLATED` to `RENDEZVOUS` leaves every test green
(`RemEx-3uv7s`). Under `Dispatchers.Unconfined` an ack resumes the sender inline, so it is always
already parked when the next ack arrives — the "token arrives with nobody waiting" case the buffer
exists for is unreachable, and reaching it re-entrantly deadlocks the test thread instead of failing.
The buffer is load-bearing: without it an ack landing between the condition check and the park is
dropped and the transfer freezes mid-file with no error either side.

Two of those were green until the *tests* were fixed, not the code: bounding on `size` was
unfalsifiable while the reconcile ran unconditionally, and the `final`-flag mutation passed against a
five-byte fixture where the first and last frame are the same frame. If a mutation here comes back
green, suspect the fixture before concluding the code is covered.

**The backpressure wait is NOT this wait.** It only blocks once outstanding unacked bytes exceed
8 MB, so every transfer *smaller* than that reaches the completion without ever forcing an ack round
trip. On the Kotlin sender that cap is now an injectable constructor parameter defaulting to
`FileTransferLimits.MAX_UNACKED_BYTES`, so the branch is reachable from a test (`RemEx-68wwl`); the
C# sender at `TransferSessionManager.cs:1314` still reads the raw const and is **still uncovered**
(`RemEx-xefvb`), as is `FileTransferEngine.runUpload` on the upload path (`RemEx-yi7id`). Any value
set there must exceed the peer's 4 MB ack interval or the sender deadlocks in silence — a smaller cap
is valid only against a fake that acks by hand. That inverse
sizing is what made this look like a flaky feature rather than a bug: large pushes incidentally
survived because backpressure had already drained them, while every screenshot failed. Measured on a
353,985-byte screenshot push — both data frames dropped as *"No sink"* (RemEx-zd8ws; the phone had
learned the same lesson as RemEx-y6x6 and the C# sender never got the fix).

The wait is an **idle window**, not a deadline (`AckDrainIdleTimeout`, `TransferSessionManager.cs:110`):
up to 8 MB may still be draining, so any total budget safe on a slow link is useless as a backstop.
A dead socket does not depend on it — `RunChannelAsync`'s teardown cancels the send session.

Guarded by `HostSendDrainTests`. Its discriminating assertion is *negative* — with the final ack
withheld, the sender must still be blocked. Asserting only on the final ordering does not catch a
regression here, because once the ack is delivered a fixed and a broken sender emit identical
control traffic.

---

## Desktop shell — Material.Avalonia template parts

### An unset property is not a neutral property (INVARIANT)

`remex.desktop/Views/ShellView.axaml:1136-1137` — `material:SideSheet#SettingsSideSheet` **must**
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

`remex.desktop/App.axaml:199` (`Window.palette-crossfade.palette-transition-suppressed`) and `:1163-
1176` (the "chrome drag suppression … MUST STAY LAST" block) — RemEx-zgtn1.

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

`remex.desktop/Controls/DashboardBackgroundControl.axaml` — the three `AuroraLayerN` rectangles
(RemEx-ddynd). Reduced motion is implemented by REMOVING the `aurora-animated` class (bound to
`!IsReducedMotion`), which stops the keyframe animation and lets the property fall back to the
rectangle's own `Opacity`. If that static value drifts from the first keyframe, switching reduced
motion on visibly jumps the mesh; if the `Style` selector loses `.aurora-animated`, the mesh keeps
animating with the setting supposedly off — silently, exactly like the chrome-suppression failure
above. `AuroraMeshTests.ReducedMotionFreezesTheMeshAtItsFirstKeyframeInsteadOfHidingIt` pins both
halves; a layer added later must satisfy the same pairing.

### Theme switch removes only the tracked base-theme dictionary, never the whole `Themes/` folder

`remex.desktop/Services/ThemeService.cs:600-660` (`ThemeDictionaryPrefix`, `BaseThemeSources`, `SwapBaseTheme`) — RemEx-gcqw5.

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

`remex.desktop/Services/DashboardLayoutService.cs` — `LoadAsyncCore` (:297), `SaveAsync` (:424),
`WriteProfileAtomicallyAsync` (:693); `DashboardLayoutClobberTests.cs` — RemEx-8y3qy.

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

`remex.desktop/Views/ShellView.axaml.cs` — `OnOverlayToggled` (:482);
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
`BringMainWindowToFront()`.** `App.axaml.cs` `ShowFileConsentDialogAsync` calls that helper before
`ShowDialog`. Avalonia's `ShowDialog` throws on a non-visible parent, and RemEx normally runs with
`MainWindow` constructed but not surfaced, in three distinct states:

- **never shown** — the logon task starts it `--minimized` (`scripts/autostart-remex.ps1`);
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

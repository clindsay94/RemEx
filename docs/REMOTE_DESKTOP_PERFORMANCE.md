# Remote Desktop — Performance & Correctness Notes

> **Purpose.** This is the regression playbook for the remote-desktop stream. Each item below is a real
> bug we hit and fixed; if smoothness, FPS, the cursor, or input ever regresses, start here. Every fix has
> an anchor comment in the code tagged with its `RD-x` id — grep for it.

## Pipeline at a glance

```
PC host (remex.agent / remex.agent.windows)                 Android client (remex.android + libRemexCore)
─────────────────────────────────────────                  ──────────────────────────────────────────────
capture  →  convert to BGRA  →  H.264 encode  →  send  ──►  receive (/ws/desktop, binary)
(WGC/DXGI/GDI)  (BgraFrameConverter)  (NVENC/ffmpeg)         ├─ "RDXC" magic → cursor packet → ByteBuffer → overlay
   │                                                         └─ else → H.264 frame → MediaCodec → SurfaceView
   └─ cursor position (separate ~90 Hz loop) ──────────────► cursor overlay (Compose, drawn in DRAW phase)
```

Two **decoupled** streams share the one `/ws/desktop` WebSocket: H.264 video frames (binary) and cursor
position (binary `"RDXC"` packet when negotiated, else JSON text). They are demultiplexed by the leading
4 bytes — `"RDXC"` = cursor, anything else (a NAL start code `00 00 00 01` or the `"RDXF"` frame envelope)
= video.

---

## The footguns (and how to verify each)

### RD-C — FPS ceiling from `System.Drawing`/GDI+ conversion  *(highest impact)*
**Symptom:** stream caps near ~30 FPS regardless of GPU/encoder; micro-stutter.
**Cause:** the capture→encode step converted every frame to raw BGRA via `System.Drawing` — a GDI+
`Graphics.DrawImage` software blit plus 1–3 multi-MB `Bitmap`/`byte[]` allocations **per frame**. That CPU
+ GC cost, *not* the hardware NVENC encoder, is the ceiling.
**Fix:** `Remex.Core/Services/BgraFrameConverter.cs` — when no resample is needed, copy the mapped staging
texture straight into a tightly-packed buffer with a row-wise `Marshal.Copy` (no Bitmap, no blit). Wired
into `WgcDesktopCapture.EncodeToRawBgra` and `DxgiDesktopCapture.EncodeToRawBgra` (the latter only when
`drawCursor == false`).
**Invariant:** the output buffer length **must** equal `CaptureScaling.ScaledEven(w) * ScaledEven(h) * 4`.
A one-byte mismatch desyncs the rawvideo stdin pipe and **NVENC emits zero frames (black screen)**.
**Best config:** on a capable GPU, capture at **`scale 1.0`** (full resolution) — that hits the fast copy
path, NVENC encodes full res trivially, and the phone downscales for display via fit-to-height (RD-A3).
The downscale path (`scale < 1`) still uses GDI+ bilinear; replacing it with GPU scaling is the next lever.
**Verify:** the host logs `Stream Metrics: ... Avg Capture+Encode: N ms` every 5 s. With the fast path at
`scale 1.0`, capture+encode should be a few ms (not ~33 ms). Unit test: `BgraFrameConverterTests`.

### RD-A1 — cursor Hz capped by the OS timer
**Symptom:** cursor feels slightly steppy; cursor stream tops out ~64 Hz despite a 90 Hz target.
**Cause:** `await Task.Delay(11)` rounds **up** to the Windows timer resolution (~15.6 ms) → ~64 Hz.
**Fix:** `PrecisionPacer` (hybrid coarse `Task.Delay` + `Thread.SpinWait`), shared by the video-frame loop
and the cursor loop in `RemoteDesktopHandler`. **Do not** reintroduce a bare `Task.Delay` in either loop —
it silently caps the rate (frames near 60 FPS, cursor near 64 Hz).
**Verify:** instrument the loop with a `Stopwatch` and log the tick delta; should be ~11 ms, not ~15.6 ms.

### RD-B — Compose recomposition thrashing
**Symptom:** high CPU/battery/thermal while the cursor moves; recomposition counts spike.
**Cause:** the animated cursor position (`animatedCursorX.value`) was read in the **composition** phase,
forcing a full recompose of the screen subtree on every animation frame.
**Fix:** in `RemoteDesktopScreen.kt`, the visibility *gate* stays in composition but the animated-position
read + `mapHostToLocal` happen **inside the `Canvas` draw lambda** (draw phase) → redraw without recompose.
**Invariant:** never read `animatedCursorX/Y.value`, `panOffset*`, or `zoomFactor` in the composable body
for the cursor overlay — read them in the draw scope.
**Verify:** Android Studio Layout Inspector → recomposition counts stay flat while moving the host cursor.

### RD-A2 — pan-follow epsilon not density-independent
**Symptom:** sub-pixel jitter at the pan clamp on high-DPI phones when heavily zoomed.
**Cause:** a hardcoded `0.5f` pixel epsilon in the pan-follow deadzone.
**Fix:** density-scaled `with(density) { 0.75.dp.toPx() }` in `RemoteDesktopScreen.kt`. The epsilon is
**load-bearing** — it stops the follow animation restarting forever when the cursor sits past the max-pan
clamp (`PanFollowCalculator.compute` returns the same target every tick). Don't remove it.

### RD-A3 — tiny initial view
**Symptom:** opening the remote screen shows a tiny letterboxed strip (wide/ultrawide host on a phone).
**Cause:** `zoomFactor` started at a flat `1f` with no fit.
**Fix:** a one-time `LaunchedEffect` in `RemoteDesktopScreen.kt` sets `zoomFactor = imageSize.height /
contentRect().h` (fill height) once the real stream dimensions arrive (`didInitialFit` guard). No-op (~1×)
when the content already fills height (e.g. a 16:9 host in landscape). Manual pinch/pan afterward is kept.

### RD-D — negative-coordinate monitors unreachable by input
**Symptom:** S-Pen hover / clicks can't reach a monitor positioned **left of / above** the primary; the
cursor sticks at the edge.
**Cause:** the host clamped absolute input coordinates to `[0, width-1]`, discarding the streamed display's
(negative) virtual-desktop origin — so any negative coordinate floored to 0.
**Fix:** `CoordinateValidation.ClampToRange(value, minInclusive, maxExclusive)` clamps to
`[desktopLeft, desktopLeft+width)` / `[desktopTop, desktopTop+height)` in
`RemoteDesktopHandler.EnqueuePointerSampleAsInputEvent`. **Negative virtual-desktop coordinates are valid —
never floor them at 0.** The anti-spoofing guard (reject NaN/∞/out-of-range, RD-8/RemEx-q6u) is preserved.
`WindowsInputSimulationService.MoveMouse` already normalizes against the full virtual desktop
(`MOUSEEVENTF_VIRTUALDESK`), so once the coordinate survives the clamp it injects correctly.
**Verify:** stream the left monitor; hover the S-Pen edge-to-edge — the host cursor tracks the full range.

### RD-E — JSON cursor GC churn → binary protocol
**Symptom:** micro-stutter from GC pressure at 60–90 cursor updates/s.
**Cause:** cursor position was a JSON `desktop_cursor_state` message; on Android the JNI bridge even
re-serialized it to a JSON string before `JSONObject`-parsing it.
**Fix:** `DesktopCursorBinaryEnvelope` (32-byte `"RDXC"` packet) over the binary channel; crosses JNI as a
`byte[]` parsed with `ByteBuffer`. Capability-gated (`supportsBinaryCursor`); falls back to JSON for older
peers. **Invariant:** keep `"RDXC"` distinct from the video framing; X/Y stay **signed** (negative
monitors). Cursor *shape* stays JSON. NativeAOT-safe (`BinaryPrimitives`). Test:
`DesktopCursorBinaryEnvelopeTests` (round-trip + H.264-demux safety).

### RD-F — UIPI: cursor frozen under an elevated/admin window  *(diagnose first)*
**Symptom:** the remote cursor won't move at all while a UAC-elevated (admin) window is in the foreground.
**Cause:** Windows **UIPI** drops synthetic input from a process at *lower* integrity than the foreground
window. The input-injector host must run at HIGH (or SYSTEM) integrity. The Session-0 service tries to
launch it HIGH via the user's linked admin token (`InteractiveDesktopHostLauncher` →
`WindowsActiveSession.TryLaunch(elevate: true)`), but that only works for a **split-token admin**.
**Diagnose:** check the host log for `Session guard: no linked elevated token (TokenElevationType=N)`:
- `N=3` (Limited / split admin) → it *should* launch HIGH. If input still fails, look for a **competing
  medium-integrity host instance** winning the single-instance guard (e.g. a leftover `HKCU\…\Run "RemEx"`
  entry — see `StartupRegistrationService`). Confirm the running host's integrity in Process Explorer.
- `N=1` (Default) while you are an admin → UAC Admin Approval Mode is likely **off** (everything runs at
  one integrity; a service-launched medium host can't drive an elevated window).
- `N=2` (Full) → the host should already be HIGH.
**Durable fix options** (security-critical — needs sign-off): (a) a signed helper with `uiAccess="true"`
installed under Program Files (the canonical remote-control bypass; needs Authenticode signing); (b) a
SYSTEM-integrity headless injector spawned into the session (no signing; works for standard users too);
(c) just remove the competing instance / repair the linked-token launch.

---

## Cross-platform parity
The capture fast-path (RD-C) and UIPI (RD-F) are Windows-only. The `PrecisionPacer` (RD-A1) and the binary
cursor protocol (RD-E) are shared and affect Linux too — verify the Linux build after changing them.

## Quick verification checklist
1. `dotnet test Remex.sln` — `BgraFrameConverterTests` + `DesktopCursorBinaryEnvelopeTests` green.
2. Android: `./gradlew :app:compileDebugKotlin` then a full assemble (the JNI `onDesktopCursorBinary([B)`
   signature must match the C# registration in `AndroidNativeExports`).
3. Live: stream one monitor → FPS well above 30 (target 60+); S-Pen hover reaches the left monitor; opening
   view fills phone height; cursor smooth with flat recomposition counts; input drives an admin terminal
   (after the RD-F fix path is chosen).

# Remediation PRD — PipeWire Wire-Up & Reconnect Session Cleanup

**Document Status:** Draft
**Target Application:** RemEx 2.0 — Linux Host + Android Client
**Author:** PRD Architect
**Date:** 2026-05-17
**Target Implementer:** Coding-Lead-Implementer
**Source:** Live ADB session diagnostics 2026-05-17 (`/tmp/remex-android.log`, `/tmp/remex-desktop.log`); diagnostic plan `/home/connorl/.claude/plans/okay-using-adb-to-noble-willow.md`
**Branch:** `2.0`

---

## 1. Executive Summary

- **The Problem:** Android client streaming from a KDE Plasma 6 Wayland host on a 5120x1440 display delivers approximately **0.1 fps** (frame intervals of 5–30 seconds), versus a target of ≥20 fps. Two coupled defects together cause the floor:
  1. The PipeWire capture coordinator (`LinuxCaptureSessionCoordinator`) exists in the codebase but is **never instantiated and never wired into `LinuxScreenCaptureService`** — `SetCaptureCoordinator(...)` has zero callers in the entire tree. Every captured frame falls through to the legacy shell-tool path, which spawns `spectacle` and `ffmpeg` subprocesses and round-trips a temp PNG/JPEG file *per frame*. Live log line: `Linux screen capture initialized: display=wayland-0, server=Wayland, resolution=5120x1440, primaryTool=/usr/bin/spectacle, fallback=/usr/bin/ffmpeg`. The string `"pipewire"` never appears as `BackendName` in the metadata sent to the client.
  2. The host-side `/ws/desktop` route handler holds **no per-client session registry**. When the Android client retries on its 1/2/4/8/16-second backoff, the previous WebSocket's TCP FIN is never observed by the server, so the old `StreamFramesAsync` loop's `webSocket.State == WebSocketState.Open` check stays true forever. New connections stack on top of orphaned ones. Live evidence (`/tmp/remex-desktop.log`): **6 instances of "Remote desktop client connected" against only 1 "Remote desktop client disconnected"**, all with the same `clientId=51a41dca814549c49585d95ca11771dc`. Five orphan frame loops race the compositor in parallel.

- **The Fix:** Two independent but synergistic tracks.
  - **Track A — PipeWire Wire-Up:** Introduce a singleton `LinuxCaptureSessionLifetime` that opens an `xdg-desktop-portal` ScreenCast session on the first `/ws/desktop` connection, owns the `LinuxCaptureSessionCoordinator`, and injects it into `LinuxScreenCaptureService`. Encode the resulting BGRA/RGBA PipeWire frames to JPEG in-process using SkiaSharp so the existing MJPEG wire protocol and Android `BitmapFactory` decoder are untouched.
  - **Track B — Reconnect Cleanup:** Introduce a singleton `DesktopSessionRegistry` keyed by `clientId`. Before a new `/ws/desktop` route invocation starts the handler, cancel the prior session's `CancellationTokenSource` and *await* its drain to completion. Old `StreamFramesAsync` exits before the new one begins.

- **Impact:** Eliminates ~99% of per-frame producer cost on KDE Wayland. Restores the in-flight stream invariant violated by client reconnects. Brings Linux Wayland desktop streaming to parity with the Windows DXGI path.

- **Risk Level:** **Medium.** Track A introduces new portal permission prompts, a new singleton service, a packaging dependency (`libremex_linux_bridge.so`), and a new managed package (`SkiaSharp`). Track B touches the WebSocket route registration. Both are scoped to Linux + the desktop streaming path; Windows DXGI and X11 fallback are unaffected.

---

## 2. Goals & Non-Goals

### Goals
- Achieve **≥20 fps sustained** on the KDE Plasma 6 Wayland host with display 5120x1440, with `scale=0.5, quality=50, targetFps=30` (the current Android default per the live log).
- Make the host's `BackendName` field on `DesktopMeta` report `"pipewire"` when the native bridge is available, so the Android client can verify the fast path is live.
- Guarantee **at most one active `StreamFramesAsync` loop per `clientId`** at any moment in time.
- Keep the existing legacy shell-tool path as a runtime fallback when the PipeWire path cannot initialize (missing `libremex_linux_bridge.so`, portal refused, native session create failure).

### Non-Goals
- **Wire-protocol rewrite.** The on-the-wire frame format remains JPEG. No new `desktop_frame` envelope, no format tag, no header. The Android decoder is not modified in this PR.
- **Android-side reconnect tuning.** Backoff timings, max retries, and the JNI-side `RemexCoreClient.StartDesktopStream` close sequencing are out of scope. Track B's host-side registry is sufficient to make the Android contribution non-blocking.
- **Windows / DXGI changes.** `WindowsScreenCaptureService` and `DxgiDesktopCapture` are not touched.
- **X11 performance.** The X11 legacy path keeps spawning `scrot`/`ffmpeg` per frame. Tracked separately.
- **Multi-monitor composition.** On a host with multiple physical displays, the portal returns multiple PipeWire node IDs; v1 uses `nodeIds[0]` only (matches the current placeholder logic in `LinuxCaptureSessionCoordinator.cs:69`). Multi-monitor capture is a follow-up.
- **Raw-format wire path.** Sending uncompressed BGRA over WebSocket and decoding without JPEG is the natural follow-up to this PRD but is explicitly deferred.

---

## 3. Two-Track Scope

| Track | Title | Independence | Why both are needed |
|-------|-------|--------------|---------------------|
| A | PipeWire capture coordinator wire-up + in-process JPEG encoding | Ships independently. Without Track B, the per-frame fps boost will still be masked by N orphan loops competing for the single PipeWire session. | Track A alone improves per-frame cost from ~1 second (spectacle+ffmpeg) to <16 ms (PipeWire+Skia encode), unlocking the 20+ fps target. |
| B | Per-clientId session registry with await-cancel-takeover | Ships independently. Without Track A, fixes the orphan-loop bug but each remaining loop still uses the slow legacy path. | Track B alone eliminates 5x compositor contention and restores correctness of reconnect semantics. |

Both tracks must land for the user-visible fps target. Track B is essential because Track A's gains are otherwise hidden by stacked loops contending for a single portal session.

---

## 4. Implementation Delta

### Files to Modify
- `Remex.Host/Remex.Host.csproj` — add `<PackageReference Include="SkiaSharp" Version="2.88.8" />` and `<PackageReference Include="SkiaSharp.NativeAssets.Linux" Version="2.88.8" />`. Reason: in-process JPEG encoding for PipeWire frames; no `libgdiplus` runtime dep, performance parity with native `libjpeg-turbo`.
- `Remex.Host/HostBootstrapper.cs` — (a) register the two new singletons in the `OperatingSystem.IsLinux()` branch (Track A); (b) register the session registry singleton (Track B); (c) modify the `/ws/desktop` lambda at lines 248–289 to (i) acquire/release a session-lifetime refcount (Track A) and (ii) call `registry.TakeOverAsync(clientId, ...)` before starting the handler (Track B).
- `Remex.Host/Services/ScreenCapture/LinuxScreenCaptureService.cs` — (a) replace the current "return raw frame bytes" fast path at lines 80–101 with a JPEG-encoded path that consumes a `LinuxFrameSnapshot` and produces a `byte[]` of JPEG; (b) on PipeWire encode failure, fall through to the legacy path (existing behavior at lines 95–100 is preserved).
- `Remex.Host/Handlers/RemoteDesktopHandler.cs` — narrowly extend `HandleAsync(...)` to accept an externally-provided `CancellationToken` that is wired to the session registry's CTS (Track B). The handler does not itself manage the registry; it only honors the linked CT and propagates cancellation correctly when the outer registry cancels.
- `installer/build-linux.sh` — add a build step that invokes the `Remex.Host.Native.Linux/CMakeLists.txt` build, then copies the resulting `libremex_linux_bridge.so` into the host stage directory.
- `installer/linux/host-install.sh` — print a one-line warning if `libpipewire-0.3-0` (or distro-equivalent) is missing on the install host. **Do not** install it automatically; the user installs OS packages.

### Files to Create
- `Remex.Host/Services/RemoteDesktop/Linux/Capture/LinuxCaptureSessionLifetime.cs` — singleton owning the portal session and `LinuxCaptureSessionCoordinator`, refcounted by active `/ws/desktop` connections. (Track A)
- `Remex.Host/Services/RemoteDesktop/Linux/Capture/LinuxJpegEncoder.cs` — static helper that encodes a `LinuxFrameSnapshot` (BGRA/RGBA/NV12) to JPEG bytes via SkiaSharp, parameterized by quality and scale. (Track A)
- `Remex.Host/Services/RemoteDesktop/DesktopSessionRegistry.cs` — singleton, OS-agnostic, keyed by `clientId`, holding `ConcurrentDictionary<string, DesktopSessionEntry>`. (Track B)
- `Remex.Host.Tests/LinuxCaptureSessionLifetimeTests.cs` — unit tests for refcount transitions (0→1 starts, N→0 stops).
- `Remex.Host.Tests/DesktopSessionRegistryTests.cs` — unit tests for takeover ordering and same-clientId race.
- `Remex.Host.Tests/LinuxJpegEncoderTests.cs` — unit tests that feed a synthetic 256x256 BGRA buffer and assert the output is a valid SOI/EOI JPEG and the decoded pixels match within tolerance.

### Files to Delete
- None. The legacy shell-tool path is preserved for fallback.

### Destructive Changes
- The semantics of `LinuxScreenCaptureService.CaptureScreenAsync(...)` lines 80–101 change: on the PipeWire fast path, the returned bytes are now JPEG-encoded (previously they were the raw native pixel buffer with a `TODO` comment to encode later). No external caller currently relies on the old "raw bytes" behavior — `RemoteDesktopHandler.StreamFramesAsync` already treats the return value as `jpegBytes`. This is a contract repair, not a breaking change. Removed comment: "the encoder stage (RemoteDesktopHandler) is responsible for JPEG encoding. When the encoder is updated to accept LinuxFrameSnapshot directly, this path will bypass the intermediate copy." That comment becomes obsolete.

### Dependency Updates
- **New NuGet package:** `SkiaSharp` v2.88.8 + `SkiaSharp.NativeAssets.Linux` v2.88.8. Adds approximately 6 MB to the host's published `linux-x64` self-contained output. Verified compatible with .NET 10 and Linux x64.
- **New runtime OS dependency:** `libpipewire-0.3-0` (Debian/Ubuntu) / `pipewire` (Arch, Fedora). Already present on every modern Linux desktop that runs Wayland. The native bridge `libremex_linux_bridge.so` dlopens it; absence triggers automatic fallback to the legacy path.
- **Existing native bridge must now be packaged:** `Remex.Host.Native.Linux/libremex_linux_bridge.so`. Confirmed via filesystem audit that the current `installer/build-linux.sh` does **not** build or copy this `.so`; the published `/home/connorl/publishedremex/` directory has no `libremex*` file. This is a *blocker* for Track A correctness and is addressed below in Phase 1.

---

## 5. Step-by-Step Execution Plan

The order within each phase matters; cross-phase ordering between A and B is independent (either can ship first).

### Phase 0 — Pre-flight (one-time, both tracks)

1. **Run impact analysis per `AGENTS.md`:**
   - `gitnexus_impact({target: "LinuxScreenCaptureService.CaptureScreenAsync", direction: "upstream"})`
   - `gitnexus_impact({target: "RemoteDesktopHandler.HandleAsync", direction: "upstream"})`
   - `gitnexus_impact({target: "HostBootstrapper.CreateApplication", direction: "upstream"})`
   - Report blast radius to the user. Expected: HIGH risk on `HandleAsync` (it is the only desktop streaming entrypoint).
2. **Confirm the native bridge builds:** `cd Remex.Host.Native.Linux && cmake -B build -DCMAKE_BUILD_TYPE=Release && cmake --build build`. Required output: `build/libremex_linux_bridge.so`. If this fails, halt; the entire Track A is gated on the native bridge.

### Phase 1 — Native Bridge Packaging (Track A prerequisite)

1. Open `installer/build-linux.sh`.
2. Find the "── Host ──" section starting at line 78.
3. Immediately *after* the `dotnet publish "$HOST_PROJ" ...` invocation (line 86) and *before* the `cp -r "$HOST_PUBLISH/." "$HOST_STAGE/"` (line 92), insert a native-bridge build step:
   - Run `cmake -B "$REPO_ROOT/Remex.Host.Native.Linux/build" -S "$REPO_ROOT/Remex.Host.Native.Linux" -DCMAKE_BUILD_TYPE=Release`.
   - Run `cmake --build "$REPO_ROOT/Remex.Host.Native.Linux/build" --target remex_linux_bridge`.
   - Copy `$REPO_ROOT/Remex.Host.Native.Linux/build/libremex_linux_bridge.so` into `$HOST_PUBLISH/` so it ends up in the published output alongside `Remex.Host` and the other native deps.
   - Fail the build script with `exit 1` if the `.so` is missing after the build.
4. Verify `nm -D --defined-only $HOST_PUBLISH/libremex_linux_bridge.so | grep remex_pw_session_create` returns a symbol; this confirms the P/Invoke target is exported.
5. Update `installer/linux/host-install.sh` to print a probe line: after the install completes, run `ldd $INSTALL_DIR/libremex_linux_bridge.so | grep -E "(not found|pipewire)"` and emit a warning if PipeWire is missing. Do **not** apt-install anything.

### Phase 2 — Track A: PipeWire Coordinator Wire-Up

#### Phase 2.1 — JPEG Encoder

1. Create `Remex.Host/Services/RemoteDesktop/Linux/Capture/LinuxJpegEncoder.cs`.
2. Declare `[SupportedOSPlatform("linux")] internal static class LinuxJpegEncoder`.
3. Public method signature:
   ```csharp
   public static byte[] Encode(
       LinuxFrameSnapshot frame,
       int quality,           // 1..100, the caller already clamps
       double scale,          // 0.25..1.0, applied during encode
       ILogger logger,
       out string formatTag); // "BGRA", "RGBA", "RGB", or "" on unsupported
   ```
4. Map `frame.Format` (a DRM fourcc or `SPA_VIDEO_FORMAT_*` code) to the equivalent `SkiaSharp.SKColorType`:
   - DRM fourcc `BGRx` (0x34325842) / `BGRA` (0x41524742) → `SKColorType.Bgra8888`.
   - DRM fourcc `RGBx` (0x34325852) / `RGBA` (0x41424752) → `SKColorType.Rgba8888`.
   - `SPA_VIDEO_FORMAT_BGRA` (= 12) → `SKColorType.Bgra8888`. `SPA_VIDEO_FORMAT_RGBA` (= 11) → `SKColorType.Rgba8888`.
   - `SPA_VIDEO_FORMAT_NV12` (= 22) → log warning, return `Array.Empty<byte>()` and `formatTag = ""`. NV12 conversion is out of scope (KDE typically negotiates BGRA; NV12 implies hardware-accelerated capture which we do not request).
   - Anything else → log warning with the fourcc as a 4-char string and the SPA code, return empty.
5. Build the `SKImageInfo` using `frame.Width`, `frame.Height`, mapped `SKColorType`, and `SKAlphaType.Premul` (assume premultiplied per PipeWire convention; if frames look wrong in QA, switch to `Unpremul`).
6. Pin the buffer:
   - When `frame.Data is not null`: pin the `byte[]` via `GCHandle.Alloc(frame.Data, GCHandleType.Pinned)`, get `IntPtr` via `AddrOfPinnedObject()`, then use `SKImage.FromPixels(info, ptr, frame.Stride)`.
   - When `frame.Data is null` and `frame.RawData != IntPtr.Zero`: use `frame.RawData` directly (caller guarantees lifetime until the next `ReleaseFrame`).
7. Apply scale:
   - When `scale >= 0.99`, skip resizing and encode directly.
   - Otherwise, compute `targetW = (int)(frame.Width * scale)` and `targetH = (int)(frame.Height * scale)`, create a destination `SKBitmap`, and use `SKBitmap.ScalePixels(...)` with `SKFilterQuality.Medium` (bilinear; matches Windows `InterpolationMode.Bilinear` at `DxgiDesktopCapture.cs:444`).
8. Encode:
   ```csharp
   using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
   return data.ToArray();
   ```
9. Wrap the entire body in `try/catch` for `Exception` and `OutOfMemoryException`. Log at `Warning`. Return `Array.Empty<byte>()` on any throw so `LinuxScreenCaptureService` can fall through to the legacy path on the next tick.
10. Release the `GCHandle` in `finally`.

#### Phase 2.2 — Session Lifetime Singleton

1. Create `Remex.Host/Services/RemoteDesktop/Linux/Capture/LinuxCaptureSessionLifetime.cs`.
2. Declare `[SupportedOSPlatform("linux")] public sealed class LinuxCaptureSessionLifetime : IAsyncDisposable`.
3. Constructor injects:
   - `ILogger<LinuxCaptureSessionLifetime> logger`
   - `IScreenCaptureService screenCapture` (resolved as `LinuxScreenCaptureService` at runtime)
4. Private fields:
   - `private readonly object _gate = new();`
   - `private int _refcount;`
   - `private LinuxPortalRemoteDesktopSessionService? _portal;`
   - `private LinuxCaptureSessionCoordinator? _coordinator;`
   - `private Task<bool>? _startTask;` (cached so concurrent 0→1 callers share a single startup)
5. Public method: `Task<bool> AcquireAsync(CancellationToken ct)`.
   - Under lock: `_refcount++`.
   - If `_refcount == 1`: kick off `_startTask = StartInternalAsync(ct)`.
   - Await `_startTask`. Return its result.
6. Public method: `Task ReleaseAsync()`.
   - Under lock: `_refcount--`.
   - If `_refcount == 0`: stop and dispose the coordinator + portal, set `_screenCapture` (cast to `LinuxScreenCaptureService`) coordinator to `null`, clear `_startTask`.
   - If `_refcount > 0`: return completed task.
7. Private `StartInternalAsync(CancellationToken ct)`:
   - Construct `_portal = new LinuxPortalRemoteDesktopSessionService(appId: "com.clindsay94.RemEx", logger: ...)`.
   - Construct `_coordinator = new LinuxCaptureSessionCoordinator(_portal, ...)`.
   - `await _coordinator.StartAsync(ct)`.
   - If exception: log Error, set both fields back to `null`, return `false`.
   - On success: call `((LinuxScreenCaptureService)_screenCapture).SetCaptureCoordinator(_coordinator)`.
   - Log Info: "PipeWire capture lifetime active (coordinator wired into LinuxScreenCaptureService)." Return `true`.
8. `DisposeAsync` forces a final stop regardless of refcount (for process shutdown).
9. Subscribe to `_portal.SessionLost` event: on fire, the coordinator's existing `OnPortalSessionLost` already cancels the capture loop; this class additionally clears the injection so the legacy path takes over until the next acquire/restart cycle.

#### Phase 2.3 — Inject JPEG Encoder Into LinuxScreenCaptureService

1. Open `Remex.Host/Services/ScreenCapture/LinuxScreenCaptureService.cs`.
2. Locate lines 79–101 (the "Stage 2 fast path: PipeWire native capture" block).
3. Replace the body of the `if (_captureCoordinator is { IsRunning: true })` block so that after `WaitForNextFrameAsync(...)` returns a non-null frame:
   - Call `LinuxJpegEncoder.Encode(frame, quality, scale, _logger, out var formatTag)`.
   - If the returned `byte[]` is non-empty, return it.
   - If empty: log `LogDebug("PipeWire frame produced but JPEG encode returned empty (format={Format}); falling back this tick.", formatTag)` and fall through to the legacy path.
4. Do not modify the legacy path (lines 103–151). It remains the fallback.
5. The class's `BackendName` property (line 62) remains as-is — it already reports `"pipewire"` whenever `_captureCoordinator is not null`. After Track A's lifetime wire-up runs, this property will start returning `"pipewire"` to clients.

#### Phase 2.4 — Register and Acquire/Release in HostBootstrapper

1. Open `Remex.Host/HostBootstrapper.cs`.
2. In the `OperatingSystem.IsLinux()` DI branch at lines 71–79, *after* `AddSingleton<IScreenCaptureService, LinuxScreenCaptureService>()`, add:
   ```csharp
   builder.Services.AddSingleton<LinuxCaptureSessionLifetime>();
   ```
3. In the `/ws/desktop` map lambda at lines 248–289, between `using var ws = await context.WebSockets.AcceptWebSocketAsync();` (line 281) and the `using var handler = new RemoteDesktopHandler(...)` (line 282), insert:
   ```csharp
   var lifetime = context.RequestServices.GetService<LinuxCaptureSessionLifetime>();
   var captureStarted = false;
   if (lifetime is not null)
   {
       try { captureStarted = await lifetime.AcquireAsync(context.RequestAborted); }
       catch (Exception ex) { authLogger.LogWarning(ex, "PipeWire lifetime acquire failed; falling back to legacy capture."); }
   }
   ```
4. Wrap the existing `await handler.HandleAsync(...)` call in a `try / finally` that calls `await lifetime.ReleaseAsync()` (when lifetime is non-null and `captureStarted` was true) in the `finally` block. The release must run even on exception so the refcount stays accurate.
5. On non-Linux platforms `GetService<LinuxCaptureSessionLifetime>()` returns null (it is only registered in the Linux branch), so the new code is a no-op for Windows and macOS.

### Phase 3 — Track B: Reconnect Session Cleanup

#### Phase 3.1 — DesktopSessionRegistry

1. Create `Remex.Host/Services/RemoteDesktop/DesktopSessionRegistry.cs`.
2. Declare `public sealed class DesktopSessionRegistry`.
3. Inner type:
   ```csharp
   private sealed record DesktopSessionEntry(
       CancellationTokenSource Cts,
       TaskCompletionSource DrainedSignal);
   ```
4. Private field: `private readonly ConcurrentDictionary<string, DesktopSessionEntry> _sessions = new();`
5. Constructor accepts `ILogger<DesktopSessionRegistry> logger`.
6. Public method: `Task<CancellationTokenSource> TakeOverAsync(string clientId, TimeSpan drainTimeout, CancellationToken ct)`:
   - When `string.IsNullOrEmpty(clientId)`: the loopback case. Use a synthetic key `"__loopback__:" + Guid.NewGuid()` so loopback connections never collide with each other.
   - Build `var newCts = CancellationTokenSource.CreateLinkedTokenSource(ct)`.
   - Build `var entry = new DesktopSessionEntry(newCts, new TaskCompletionSource())`.
   - `var prior = _sessions.AddOrUpdate(clientId, entry, (key, existing) => entry)`.
   - When `!ReferenceEquals(prior, entry)` (i.e., there was a prior):
     - Log Info: "Taking over desktop session for clientId={ClientIdPrefix}; cancelling prior loop."
     - `await prior.Cts.CancelAsync();`
     - `await Task.WhenAny(prior.DrainedSignal.Task, Task.Delay(drainTimeout, ct))` — bounded wait.
     - If the timeout fires, log Warning: "Prior session for clientId={ClientIdPrefix} did not drain within {Timeout}ms; proceeding anyway." (Drain timeout default: 2000 ms.)
     - Dispose `prior.Cts`.
   - Return `newCts`.
7. Public method: `void MarkDrained(string clientId, CancellationTokenSource ownedCts)`:
   - Looks up the entry; if its `Cts` reference-equals `ownedCts`, removes it from the dictionary and sets `DrainedSignal.TrySetResult()`.
   - Otherwise it's a stale registration; just signal drain without removing (the new entry is already in place).
8. Thread safety: `ConcurrentDictionary.AddOrUpdate` provides atomic swap. The `CancellationTokenSource` cancel is idempotent.

#### Phase 3.2 — Wire Registry Into /ws/desktop Route

1. In `HostBootstrapper.cs`, after the existing singletons (around line 102) add:
   ```csharp
   builder.Services.AddSingleton<DesktopSessionRegistry>();
   ```
2. In the `/ws/desktop` route lambda, immediately after the `EvaluateDesktopAuth` block has returned 200 (line 280 area) and **before** `AcceptWebSocketAsync`:
   ```csharp
   var registry = context.RequestServices.GetRequiredService<DesktopSessionRegistry>();
   using var sessionCts = await registry.TakeOverAsync(
       clientId,
       TimeSpan.FromMilliseconds(2000),
       context.RequestAborted);
   ```
3. Pass `sessionCts.Token` (not `context.RequestAborted`) as the cancellation token into `handler.HandleAsync(ws, sessionCts.Token)`.
4. After `handler.HandleAsync` returns (in a `finally`), call `registry.MarkDrained(clientId, sessionCts)`. This must run even on exception.
5. Verify the final wiring order:
   1. Auth check.
   2. `TakeOverAsync` (cancels prior, awaits its drain).
   3. `AcceptWebSocketAsync` (accept the new socket only after the prior loop has stopped, so the screenCapture singleton is uncontended).
   4. `lifetime.AcquireAsync` (Track A — increments refcount; only the first session pays the portal cost).
   5. `handler.HandleAsync(ws, sessionCts.Token)`.
   6. `finally`: `lifetime.ReleaseAsync`, `registry.MarkDrained`.

#### Phase 3.3 — Honor Cancellation in StreamFramesAsync (no semantic change)

1. Open `Remex.Host/Handlers/RemoteDesktopHandler.cs`.
2. Verify lines 233 and 305: the `while` and the `Task.Delay` already accept the linked CT. No code change needed — the new external CT (now sourced from `sessionCts`) flows through `HandleAsync`'s parameter into `StreamFramesAsync` via `streamCts` at line 177. **The Track B fix works without modifying the handler** because the cancellation token is already plumbed end-to-end; what changes is the *source* of that token (registry, not `context.RequestAborted`).
3. Add an explicit `_logger.LogInformation("Remote desktop client disconnected (clientId={ClientIdPrefix}).", ...)` at line 221 if the `clientId` isn't currently surfaced — confirm via re-read.

### Phase 4 — Tests (both tracks)

1. **`LinuxJpegEncoderTests.cs`** — feed a synthetic 256x256 BGRA buffer; assert returned bytes start with `0xFF 0xD8` (SOI) and end with `0xFF 0xD9` (EOI); assert decoded image dimensions match scale. Skipped on non-Linux via `[OSSkipCondition]` or equivalent xUnit gate.
2. **`LinuxCaptureSessionLifetimeTests.cs`** — fake `LinuxScreenCaptureService` (just verify `SetCaptureCoordinator` is called with non-null on first acquire and null on last release). Cover: acquire/acquire/release/release sequence (start only once, stop only on second release). Race test: 10 concurrent `AcquireAsync` calls produce one start.
3. **`DesktopSessionRegistryTests.cs`** — three scenarios:
   - Single client, single connection: takeover from no prior is a no-op.
   - Same `clientId` reconnect: second `TakeOverAsync` cancels the first CTS and blocks until the first calls `MarkDrained`.
   - Drain timeout: second `TakeOverAsync` returns after 2s if the first never drains, with a Warning log assertion.
4. **`RemoteDesktopHandlerTests.cs`** (existing file) — add a regression test that simulates external CTS cancellation mid-stream and asserts the inner CTS (`streamCts` at line 177) propagates and the loop exits within 200 ms.

### Phase 5 — Pre-commit verification

1. Run `gitnexus_detect_changes()` per `AGENTS.md` and confirm the affected symbols match the inventory in Section 4.
2. `dotnet build` from repo root — zero warnings on the modified files.
3. `dotnet test Remex.Host.Tests` — all new tests pass; existing tests unchanged.

---

## 6. Edge Cases & Constraints

### Edge Case 1 — Portal denied
- **Scenario:** User closes the KDE portal dialog or denies screencast permission.
- **Expected behavior:** `LinuxCaptureSessionCoordinator.StartAsync` throws (per `LinuxPortalRemoteDesktopSessionService.cs:90`). `LinuxCaptureSessionLifetime.StartInternalAsync` catches it, returns `false`, leaves the coordinator unset. `LinuxScreenCaptureService` continues to use the legacy spectacle path. No user-facing crash; performance falls back to the ~0.1 fps regime. Log line: `LogError("Portal session creation failed.")` (existing) plus `LogWarning("PipeWire lifetime acquire failed; falling back to legacy capture.")`.

### Edge Case 2 — Native library absent
- **Scenario:** `libremex_linux_bridge.so` not shipped (e.g., user built from source without running the new CMake step).
- **Expected behavior:** `LinuxPipeWireFrameSource.TryOpen()` catches `DllNotFoundException` at lines 92–98, returns `false`. `LinuxCaptureSessionCoordinator.RunCaptureLoopAsync` sees `!_frameSource.IsNativeAvailable` at line 152 and sleeps 500 ms in a loop. The capture channel never receives a frame; `WaitForNextFrameAsync` times out after 80 ms; `LinuxScreenCaptureService` falls through to legacy. No crash. Detect via log line: `"libremex_linux_bridge.so not found. PipeWire capture unavailable."` (existing).

### Edge Case 3 — Multiple monitors
- **Scenario:** Host has 2 physical displays; user selects both in the KDE portal picker.
- **Expected behavior:** `LinuxPortalRemoteDesktopSessionService.ParseNodeIds` (lines 226–247) returns 2 node IDs; `LinuxCaptureSessionCoordinator` at line 69 uses only `nodeIds[0]`. The other display is ignored. Document this in `docs/KNOWN_LIMITATIONS.md`. The live test target is a 5120x1440 display — `xrandr --current` on the test host returns this as a single virtual desktop bound, so monitor count must be verified during QA before claiming the target is met (`xrandr --listactivemonitors`).

### Edge Case 4 — Same `clientId` concurrent connects (double-tap)
- **Scenario:** Network glitch makes Android open a second WebSocket before the first observes failure.
- **Expected behavior:** Both arrive at the route lambda. `ConcurrentDictionary.AddOrUpdate` atomically installs the second entry. The first's `TakeOverAsync` already returned (it was the first arrival), so the takeover only fires for the *second* arrival, which cancels the first. End state: one active loop, matches Track B's invariant.

### Edge Case 5 — Missing `clientId` (loopback)
- **Scenario:** The embedded host in `Remex.Client.Desktop/Program.cs` connects over loopback without supplying `clientId` (per `HostBootstrapper.cs:320–324`, loopback bypasses paired-client checks). Multiple loopback clients should not cancel each other.
- **Expected behavior:** `TakeOverAsync` synthesizes a unique key per call (`"__loopback__:" + Guid.NewGuid()`). No cross-cancellation between loopback sessions.

### Edge Case 6 — Pixel format outside `BGRA`/`RGBA`
- **Scenario:** Compositor negotiates an unsupported format (e.g., NV12, or 16-bit, or unknown fourcc).
- **Expected behavior:** `LinuxJpegEncoder.Encode` returns empty `byte[]` and logs Warning with the fourcc. `LinuxScreenCaptureService` falls through to legacy path for that tick. Repeated failures over 5 consecutive frames trigger the existing "Screen capture failing consistently" error path in `RemoteDesktopHandler` at line 286.

### Edge Case 7 — Cancellation race in drain
- **Scenario:** The prior `StreamFramesAsync` is in the middle of an `await webSocket.SendAsync` when the registry cancels.
- **Expected behavior:** `WebSocket.SendAsync(..., ct)` throws `OperationCanceledException`. The existing `catch (OperationCanceledException) { break; }` at line 267 exits the loop. The `using` block in `HandleAsync` (line 177) disposes the linked CTS. The `finally` at line 219 logs the disconnect. The outer route's `finally` calls `MarkDrained`. Total drain time should be well under 200 ms; the 2 s timeout in `TakeOverAsync` is generous.

### Edge Case 8 — Refcount underflow
- **Scenario:** `LinuxCaptureSessionLifetime.ReleaseAsync` is called more times than `AcquireAsync` (programming error).
- **Expected behavior:** Add `Debug.Assert(_refcount >= 0)` and clamp to 0. Log Error if it underflows in Release builds. This is a sentinel for caller bugs, not a runtime hazard.

### Performance Constraint
- **Per-frame budget at 30 fps, scale 0.5, quality 50 on 5120x1440:** 33 ms total. Allocation: PipeWire frame acquire (≤16 ms), SkiaSharp scale + encode (target ≤10 ms for 2560×720 BGRA → JPEG quality 50), WebSocket send (≤5 ms on LAN), throttle margin (≥2 ms). Skia JPEG benchmarks on a modern x86-64 CPU (Ryzen-class) hit ≤8 ms for a 1080p quality-50 encode; the 2560×720 target is similar pixel count and within budget.

### Concurrency Constraint
- Only one `LinuxCaptureSessionCoordinator` exists per process at a time. `LinuxScreenCaptureService._captureCoordinator` is read/written without a lock currently — that is safe under single-writer (Lifetime) and many-reader (capture path) provided the writer publishes under release semantics. The `volatile` keyword on the field is not required because reference assignment is atomic on x86-64 and the field is only read in steady state; however, **add `volatile` defensively** to silence reordering concerns and to make the intent explicit. This is a one-line addition to `LinuxScreenCaptureService.cs:33`.

---

## 7. Testing & Validation

### Test Scenario 1 — FPS uplift (Track A)
- **Given:** KDE Plasma 6 Wayland host, single 5120x1440 display, `libremex_linux_bridge.so` present, Android client paired.
- **When:** User opens the Remote Desktop screen and moves the mouse continuously for 30 seconds at `scale=0.5, quality=50, targetFps=30`.
- **Then:** Android `RemoteDesktopVM` FPS indicator reports ≥20 fps sustained; desktop log shows `BackendName="pipewire"` in the `desktop_meta` message and never logs "PipeWire frame not available; falling back".
- **Procedure:** Repeat the diagnostic procedure from `/home/connorl/.claude/plans/okay-using-adb-to-noble-willow.md` Steps 2–5. Save the two log files to `/tmp/remex-{android,desktop}-postfix.log`.

### Test Scenario 2 — Reconnect stress (Track B)
- **Given:** Streaming is active.
- **When:** Force-kill the Android process 5 times in 10 seconds (`adb shell am force-stop com.clindsay94.remex`), letting it auto-relaunch each time.
- **Then:** At any instant, only one `Remote desktop client connected` line exists without a matching `Remote desktop client disconnected` in the desktop log. The new log line `Taking over desktop session for clientId=...; cancelling prior loop.` should appear 4 times. Final state: 5 connects, 5 disconnects, exactly one active loop running.

### Test Scenario 3 — X11 regression
- **Given:** Host running X11 (e.g., GNOME on Xorg or KDE forced to X11 via session selector).
- **When:** Client connects and streams.
- **Then:** `LinuxCaptureSessionLifetime` is registered but its `AcquireAsync` may still succeed (portal works on X11 too) or fail back to legacy (`scrot`). Either way, frames must arrive. Verify `BackendName` is either `"pipewire"` or `null` (legacy), never crashes. The pre-existing `LinuxScreenCaptureServiceTests` (51 lines) must still pass.

### Test Scenario 4 — Windows DXGI regression
- **Given:** Host on Windows 10/11 with DXGI capture.
- **When:** Same client streams.
- **Then:** No code in `WindowsScreenCaptureService` or `DxgiDesktopCapture` was touched. The `OperatingSystem.IsLinux()` branch in `HostBootstrapper` is the only place the new singletons register, so on Windows `GetService<LinuxCaptureSessionLifetime>()` returns null and the route lambda's `if (lifetime is not null)` guard skips both acquire and release. Existing Windows fps target unchanged.

### Test Scenario 5 — Portal denial
- **Given:** KDE Plasma 6 Wayland host.
- **When:** First client connects; user denies the portal screencast dialog.
- **Then:** Log lines include `Portal session creation failed.` and `PipeWire lifetime acquire failed; falling back to legacy capture.`. The route continues, the handler runs, the legacy path produces frames at ~0.1 fps (unchanged from pre-fix). No crash, no orphan session in registry.

### Unit Tests Required
- `LinuxJpegEncoderTests` — synthetic BGRA → valid JPEG; happy path; unsupported format returns empty; null `Data` with valid `RawData` works.
- `LinuxCaptureSessionLifetimeTests` — 0→1 starts, N→0 stops, concurrent acquires share one start, failed start returns false and stays unset.
- `DesktopSessionRegistryTests` — takeover cancels prior CTS; same-clientId double takeover; loopback synthetic-key isolation; drain timeout.
- `RemoteDesktopHandlerTests` — external CTS cancellation propagates to inner loop and exits within 200 ms.

### Regression Check
- All tests in `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` (358 lines) must still pass.
- All tests in `Remex.Host.Tests/LinuxScreenCaptureServiceTests.cs` (51 lines) must still pass.
- `Remex.Core.Tests` and `Remex.Client.Tests` are unaffected and must still pass.

---

## 8. Risks

| # | Risk | Likelihood | Mitigation |
|---|------|------------|------------|
| 1 | Portal permission dialog fires every time the *first* `/ws/desktop` connection arrives, frustrating the user on every host restart. | High on KDE (no persistent permission by default); the portal supports `--persist-mode=2` but we currently send `--persist-mode=0` (per `LinuxPortalRemoteDesktopSessionService.cs:175`). | v1 keeps `persist-mode=0`. Follow-up PRD adds persistence with restoreToken plumbing in `PortalStartResult`. Mitigate UX by emitting a one-time toast on the Android client when `captureBackend != "pipewire"` for 30s after start: "Tap the KDE permission prompt on the host to enable hardware-accelerated capture." (Toast is a follow-up; v1 ships with no client-side change.) |
| 2 | `libremex_linux_bridge.so` fails to build on user systems (missing `linux/uinput.h`, exotic distro). | Low — CMakeLists already declares only `pthread` + uinput headers as hard deps. | `installer/build-linux.sh` fails loudly if the `.so` is missing post-build. User reads the build error and installs `linux-libc-dev` (Debian) or equivalent. Document in `docs/KNOWN_LIMITATIONS.md`. |
| 3 | SkiaSharp's `SKBitmap.ScalePixels` produces visually different output than `ffmpeg -vf scale=` (the legacy path), causing a regression complaint about "image quality looks different." | Medium. The legacy `spectacle → ffmpeg` chain uses ffmpeg's default scaler (lanczos for downscale at the resolutions in play). Skia's `SKFilterQuality.Medium` is bilinear. | Acceptable for v1 — the user is moving *from* ~0.1 fps. Document the change in `docs/CHANGELOG.md`. If complaints arise, switch to `SKFilterQuality.High` (mipmap + bicubic) — costs ~1–2 ms extra per frame, still within budget. |
| 4 | Pixel format negotiated by KDE's portal turns out to be NV12 (hardware-accelerated path), which `LinuxJpegEncoder` rejects. | Low for KDE Plasma 6 default config; Plasma's screencast backend on x86-64 typically delivers BGRA. | NV12 detection logs a clear warning. If we observe it in QA, the follow-up is to add an NV12-to-BGRA shader-side conversion in the native bridge before publishing to the channel. |
| 5 | Multi-monitor host loses one display because we only consume `nodeIds[0]`. | Medium for desktop users; the QA host is single-display 5120x1440. | v1 documents the limitation. Follow-up adds a `LinuxMultiNodeCoordinator` that round-robins among nodes or stitches them. |
| 6 | The new `await prior.Cts.CancelAsync(); await prior.DrainedSignal.Task` introduces a 2s worst-case latency on legitimate reconnects when the prior loop is hung (e.g., compositor frozen). | Low. The 2s drain timeout caps the wait. | The 2s timeout is bounded; on timeout we proceed anyway with a warning log. The new session takes over even if the old one is wedged. |
| 7 | Track A and Track B land in the same PR; if either rolls back, the other may not function as designed. | Low. They are functionally independent. | The PRD allows shipping either independently. If shipping together, the rollback plan (Section 9) covers both. |
| 8 | The host is running as a `systemd --user` service (per `installer/linux/remex-host.service` + `host-install.sh:33`), which means D-Bus session bus access and portal access work. Running under a different lifetime (e.g., system service or remote SSH without a session) would break portal acquisition. | Medium for non-standard deployments. | The installer already configures `systemctl --user`. Document the constraint in `docs/KNOWN_LIMITATIONS.md`: "PipeWire capture requires an active user session with `DBUS_SESSION_BUS_ADDRESS`. Headless/system-service deployments fall back to legacy capture." |

---

## 9. Rollout Plan

### Versioning & feature flag
- The branch is `2.0`. No feature flag. Both tracks ship in the next 2.0 release build.
- `docs/CHANGELOG.md` gets a new entry under the next-release header documenting both tracks and the new runtime dependency on PipeWire.

### Installer / packaging
1. `installer/build-linux.sh` is updated per Phase 1 to build and stage `libremex_linux_bridge.so` into the host package.
2. `installer/linux/host-install.sh` adds a post-install probe message for missing PipeWire libs but does **not** install OS packages.
3. The Windows installer (`installer/build-installer.ps1` + `RemEx.iss`) is not touched.

### Deployment sequence
1. PR with Track A only: ship, validate fps uplift on a single client.
2. PR with Track B only: ship, validate orphan-loop elimination.
3. Combined release: bundle both into the 2.0 release branch.
4. After QA validation on the live KDE host, advertise the change in `docs/CHANGELOG.md`.

### Rollback Plan
- **Trigger:** Either (a) `Remex.Host` build fails on linux-x64 release publish, (b) FPS regression on Windows DXGI path (suggests bootstrap wiring leaked), or (c) >5% of test runs in the new test suite are flaky.
- **Action:**
  - Track A rollback: `git revert` the commit that adds `LinuxCaptureSessionLifetime` registration in `HostBootstrapper.cs` and the new files. The `SetCaptureCoordinator` API on `LinuxScreenCaptureService` remains harmless when nobody calls it.
  - Track B rollback: `git revert` the commit that introduces `DesktopSessionRegistry` and reverts the route lambda to use `context.RequestAborted` directly.
  - SkiaSharp package reference can stay; it adds disk but no behavior change without callers.
- **Data Rollback:** None required. No schema changes. No persistent state.

---

## 10. Out of Scope (Explicit)

- Raw-format wire protocol (BGRA over WebSocket, no JPEG round-trip). Tracked as follow-up: "DesktopFrameEnvelope V2 with `format` and `stride` fields."
- Windows DXGI improvements (already meets target).
- X11 capture performance (legacy `scrot`/`ffmpeg`/`spectacle` paths remain unchanged).
- Android-client reconnect backoff tuning at `RemoteDesktopViewModel.kt:367–388`. The host registry makes this contribution non-blocking.
- Native-side WebSocket close behavior in `RemexCoreClient.StartDesktopStream` (JNI). Tracked separately if QA observes WebSocket lingering on the Android side.
- Multi-monitor capture composition (one PipeWire node consumed in v1).
- Portal persistence (restoreToken) to suppress the screencast permission dialog on every restart.
- NV12 (hardware-format) frame handling.
- Per-frame H.264 / VP8 / AV1 encoding (future codec negotiation).

---

## 11. Glossary

- **Portal / xdg-desktop-portal:** The Freedesktop D-Bus service that mediates security-sensitive operations (screen capture, file open, input injection) for sandboxed and non-sandboxed apps alike. On KDE the backend is `xdg-desktop-portal-kde`.
- **ScreenCast portal:** The portal interface `org.freedesktop.portal.ScreenCast` (`SelectSources`, `Start`) that yields PipeWire node IDs for the requested displays. Separate from the RemoteDesktop portal even though they can share a session handle.
- **RemoteDesktop portal:** The portal interface `org.freedesktop.portal.RemoteDesktop` (`CreateSession`, `SelectDevices`, `NotifyPointerMotion*`) used for *input injection*. Implemented in `LinuxPortalInputInjector`. Separate code path from the screencast wire-up in Track A.
- **PipeWire node:** A producer endpoint inside the PipeWire graph. Each captured display corresponds to one node ID returned by the portal.
- **DRM fourcc / SPA format code:** Pixel format identifiers from `linux/drm_fourcc.h` (4-char codes packed into `uint32_t`) and from PipeWire's `spa_video_format` enum, respectively. The native bridge reports one of them in `LinuxFrameBufferDescriptor.Format`.
- **clientId:** A 32-char hex string generated by the Android client per install (`SettingsManager.getOrCreateClientId`), passed in `/ws/desktop?clientId=...` query, used by `PairedClientRegistry` and (new) `DesktopSessionRegistry`.

---

## 12. References — verified during this PRD's drafting

- `Remex.Host/Services/ScreenCapture/LinuxScreenCaptureService.cs` lines 33, 62, 69, 80–101 (PipeWire fast path + null coordinator).
- `Remex.Host/Handlers/RemoteDesktopHandler.cs` lines 75–223 (`HandleAsync`), 225–307 (`StreamFramesAsync`).
- `Remex.Host/HostBootstrapper.cs` lines 71–79 (Linux DI branch), 248–289 (`/ws/desktop` map).
- `Remex.Host/Services/RemoteDesktop/Linux/Capture/LinuxCaptureSessionCoordinator.cs` lines 49, 63–74, 124–142 (start/stop), 180–187 (`OnPortalSessionLost`).
- `Remex.Host/Services/RemoteDesktop/Linux/Capture/LinuxPipeWireFrameSource.cs` lines 71–105 (`TryOpen`), 111–128 (`AcquireFrame`).
- `Remex.Host/Services/RemoteDesktop/Linux/Portal/LinuxPortalRemoteDesktopSessionService.cs` lines 73–128 (`StartSessionAsync`), 226–247 (node-id parsing).
- `Remex.Host/Services/Input/LinuxInputSimulationService.cs` lines 29–78 (`LinuxPortalInputInjector` lifecycle is *separate* from the new ScreenCast lifetime — confirmed).
- `Remex.Host.Native.Linux/CMakeLists.txt` — confirms native bridge builds standalone with `pthread` + uinput as only hard deps.
- `installer/build-linux.sh` — confirms no native bridge step is currently invoked; this PRD adds it in Phase 1.
- `installer/linux/host-install.sh` — confirms `systemctl --user` lifetime; portal access works.
- `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopViewModel.kt` lines 367–388 (reconnect backoff — informational; not modified).
- `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexClientManager.kt` lines 40–46 (1-slot drop-oldest frame channel — informational), 316–324 (frame arrival callback — informational; not modified).
- `/tmp/remex-desktop.log` — `Linux screen capture initialized: ... resolution=5120x1440, primaryTool=/usr/bin/spectacle`; 6 "Remote desktop client connected", 1 "disconnected".
- `/tmp/remex-android.log` — corroborates ~0.1 fps observation.
- `/home/connorl/.claude/plans/okay-using-adb-to-noble-willow.md` — full diagnostic procedure.

---

## 13. Assumptions Log

- **Assumption 1 — SkiaSharp choice:** SkiaSharp 2.88.8 + SkiaSharp.NativeAssets.Linux 2.88.8 are the right NuGet packages for in-process JPEG encoding in `Remex.Host`. The user's note that "SkiaSharp is already shipped" referred to `libSkiaSharp.so` in `/home/connorl/publishedremex/`, which is shipped *transitively* by the Desktop client (Avalonia). Verified in the source: `Remex.Host.csproj` has no SkiaSharp reference today. Adding it explicitly in the Host project is required to make the link real. SixLabors.ImageSharp is the alternate option; SkiaSharp is chosen for performance.
- **Assumption 2 — Pixel format:** KDE Plasma 6 negotiates BGRA8888 by default for portal ScreenCast on x86-64 without hardware acceleration hints. NV12/DMA-BUF paths are not exercised in v1.
- **Assumption 3 — Single display in QA:** The live test target with `resolution=5120x1440` is one virtual desktop bound from `xrandr --current`. Whether that is one ultrawide or two stacked monitors is not directly observable from the log; the PRD's multi-monitor edge case (Edge Case 3) covers both shapes by limiting v1 to `nodeIds[0]`.
- **Assumption 4 — Drain timeout 2s:** Empirically chosen. The legacy capture path can take up to ~3 s per frame (spectacle + ffmpeg) under load, so 2 s may occasionally hit the timeout during a wedged-old-loop scenario. The timeout-and-proceed behavior is safe (the registry installs the new entry atomically before awaiting the old drain), so a missed drain only produces a warning log, not a correctness issue.
- **Assumption 5 — Coordinator field is sole writer:** `LinuxScreenCaptureService._captureCoordinator` is read by the capture path (any thread) and written by `LinuxCaptureSessionLifetime` (the singleton). No other code path writes the field. Adding `volatile` is a defensive belt-and-suspenders measure, not a correctness fix.
- **Assumption 6 — `context.RequestAborted` is not a sufficient CT for Track B:** This is correct because the prior connection's `context.RequestAborted` is bound to the prior `HttpContext`, not the new one. The route lambda for the *new* connection has its own `context.RequestAborted`, which has nothing to do with the prior's. Hence the registry must hold its own linked CTS that is independent of either context's `RequestAborted`.

---

## 14. Questions & Clarifications

- [ ] Dev Q1: Should the Android client display a UI badge when `captureBackend == "pipewire"` is reported in `desktop_meta`? (Answer: out of scope for this PRD; track in a follow-up UX ticket.)
- [ ] Dev Q2: Does the QA target host actually have one ultrawide monitor or two? (Answer: Run `xrandr --listactivemonitors` on the host during QA. If two, document in the QA run; if one, no action.)
- [ ] Dev Q3: Should `LinuxCaptureSessionLifetime` proactively restart the portal session on `SessionLost` rather than tear down? (Answer: For v1, on `SessionLost` we tear down and let the next `AcquireAsync` reopen. Auto-restart is a follow-up.)
- [ ] Dev Q4: What is the maximum acceptable JPEG quality bump from the legacy ffmpeg path? Skia at quality=50 produces measurably different output. (Answer: For v1, match the user-supplied quality value 1:1 with Skia's encoder; do not auto-bump. If QA reports visible regression, switch encoder filter quality from Medium to High.)

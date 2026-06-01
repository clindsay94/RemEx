# Remote Desktop Display Target Design

## Summary

RemEx should move from implicit, platform-specific desktop capture behavior to a shared remote-desktop model where the client explicitly chooses what surface to stream:

- **all displays** as one stitched virtual desktop
- **one specific monitor** selected from a host-provided display list

This must work the same way on Windows and Linux. The host should never guess a default capture surface once the new protocol is in use. A remote desktop session starts only after the client sends an explicit capture target.

This design also fixes the cursor-authority ambiguity that would otherwise make multi-monitor support feel unreliable. The host remains the source of truth for pointer state, but RemEx clients should render the cursor as a client-side overlay using host metadata instead of relying on per-frame server-side cursor composition.

## Problem Statement

The current remote desktop feature has three issues that directly affect quality:

1. **Windows capture is effectively primary-monitor-first today.**
   - DXGI is hardcoded to `EnumOutputs(adapter, 0)`.
   - The GDI fallback uses primary-screen sizing instead of virtual-desktop bounds.
2. **Linux and Windows do not expose a shared monitor-targeting contract.**
   - That makes the client UX inconsistent and pushes platform quirks upward.
3. **Cursor ownership is ambiguous.**
   - `DesktopConfig.DrawCursor` allows either host-side cursor composition or client-side overlay.
   - That split is fragile today, and the current Windows host-side drawing logic uses virtual-desktop coordinates against per-monitor bitmaps, which breaks on non-primary monitors.

If RemEx wants a remote desktop feature that feels deliberate and trustworthy, monitor targeting, active-surface metadata, input mapping, and cursor behavior must all be designed together.

## Goals

- Give users a clear, first-class choice between **all displays** and **one monitor**.
- Make Windows and Linux use the same protocol and failure semantics.
- Keep input mapping correct for negative-origin and non-primary monitor layouts.
- Make cursor behavior deterministic and visually correct on every active surface.
- Preserve the existing codec and capture fallback paths while monitor support is added.
- Support monitor switching during an active session without forcing a full reconnect.

## Non-Goals

- Simultaneous multi-stream viewing with one independent video stream per monitor.
- Replacing the current transport or forcing all clients onto H.264 in the same change.
- Redesigning the entire remote desktop stack beyond display targeting, cursor consistency, and the required protocol/client updates.
- Silent backward-compatibility fallback where the host picks a monitor for the client.

## Constraints To Preserve

The implementation plan must preserve the behavior fixed in commit `b1d7710`:

- H.264 startup arguments that emit AUD NAL units for the current reader model
- DXGI transient-loss recovery and `TryRecover()` backoff behavior
- throttled capture/logging behavior during secure-desktop or capture-loss scenarios
- the fix for per-frame `CopyFromScreen` exception spam

It must also preserve both current capture paths:

- `CaptureRawScreenAsync` for the H.264 pipeline
- `CaptureScreenAsync` for JPEG/MJPEG fallback

Any new display-targeting logic must feed both paths so codec fallback does not regress.

## Approaches Considered

### Approach 1: Virtual Desktop Only

Always capture the stitched desktop that spans all active monitors.

**Pros**

- smallest protocol change
- simple client UX
- input mapping stays close to current virtual-desktop assumptions

**Cons**

- users cannot focus on one monitor
- wastes bandwidth and pixels when the remote task is on one display
- does not solve the product expectation that serious remote desktop software lets the user choose a monitor

### Approach 2: Per-Monitor Only

Require the client to choose a specific monitor and stream only that monitor.

**Pros**

- efficient for common remote-control sessions
- simplest active-surface model once a monitor is selected

**Cons**

- removes the useful “show me everything” case
- feels artificially limited compared with mature remote desktop tools

### Approach 3: Explicit Capture Target With Both Modes

Use one shared protocol where the client explicitly selects either:

- `virtual_desktop`
- `monitor` + `displayId`

**Pros**

- best user experience
- one shared contract for Windows and Linux
- enables polished switching UX without hidden host defaults

**Cons**

- touches shared models, host handlers, both capture backends, and client UI

## Recommendation

Use **Approach 3**.

That gives RemEx the best product outcome: a simple mental model for users, consistent cross-platform behavior, and a clean protocol that can support switching, persistence, and future capabilities without host-specific hacks.

## Protocol Design

### Capture Target Contract

`DesktopConfig` should gain a required capture-target section:

- `captureMode`: `virtual_desktop` or `monitor`
- `displayId`: required when `captureMode == monitor`

The host must reject `desktop_start` or `desktop_config` requests that do not provide a valid explicit target.

### Display Enumeration

Add a dedicated request/response pair for display discovery before streaming begins:

- `desktop_display_query`
- `desktop_display_list`

The display list response should contain one entry per active monitor with:

- `displayId`
- `name`
- `isPrimary`
- `desktopLeft`
- `desktopTop`
- `logicalWidth`
- `logicalHeight`
- `pixelWidth`
- `pixelHeight`

`displayId` only needs to be stable enough for one connected host session. It does not need to survive every hardware reconfiguration forever.

### Active Surface Metadata

`DesktopMeta` and `DesktopStreamDescriptor` should describe the **currently selected capture surface**, not always the whole desktop.

For both `virtual_desktop` and `monitor` modes:

- `DesktopLeft` / `DesktopTop` are the selected surface origin in host virtual-desktop coordinates
- `ScreenWidth` / `ScreenHeight` remain the encoded frame dimensions for compatibility
- `LogicalWidth` / `LogicalHeight` describe the active logical capture surface
- `PixelWidth` / `PixelHeight` describe the actual transmitted frame size
- `StreamSerial` increments when the active target changes

This preserves the existing mapping model while making monitor switching explicit and safe.

### Error Semantics

The host must send explicit errors for:

- missing capture mode
- `monitor` mode without `displayId`
- invalid or stale `displayId`
- display removal after a target has been selected
- capture backend failure for the chosen target

The host must **not** silently fall back to the primary monitor or another available display.

## Cursor Architecture

### Authority Model

RemEx should standardize on **host-reported cursor metadata + client-rendered cursor overlay** for first-party clients.

That means:

- the host remains the authority for cursor position and shape state
- the client renders the visible cursor
- the streamed bitmap/frame does not include a separately composited cursor for normal first-party operation

This is the best fit for scaling, monitor switching, and low-latency visual correctness.

### Coordinate Model

`DesktopMeta.CursorX` and `CursorY` should describe the pointer relative to the **active capture surface**, not as raw virtual-desktop absolute coordinates.

That keeps cursor rendering aligned with monitor mode and virtual-desktop mode using the same client logic:

- render cursor inside the active frame using relative coordinates
- translate pointer input back into host coordinates by adding `DesktopLeft` / `DesktopTop`

### Windows Capture Detail

For Windows DXGI capture, cursor extraction should use Desktop Duplication pointer primitives (`PointerPosition` and pointer-shape data) instead of relying on the current GDI `DrawIconEx` path.

That gives the implementation a clean source of truth for pointer position and shape and avoids the current off-canvas coordinate bug on non-primary monitors.

If temporary host-side composition is retained during migration, it must subtract the active surface origin before drawing onto a monitor-local bitmap.

## Host Architecture

### Shared Contract

Keep `IScreenCaptureService` as the platform seam, but extend the host-side capture model so each platform can:

1. enumerate active displays
2. resolve a requested capture target
3. capture either the selected monitor or the stitched virtual desktop
4. report active-surface metadata consistently

`RemoteDesktopHandler` remains platform-agnostic and becomes responsible for:

- validating target selection
- asking the capture service to switch targets
- emitting refreshed `DesktopMeta` and `DesktopStreamDescriptor`
- incrementing `StreamSerial`
- rejecting invalid target changes without hidden fallback

### Windows

Windows capture should support:

- per-monitor DXGI capture for a selected output
- stitched virtual-desktop capture using virtual-desktop bounds when requested
- GDI fallback with the same active-surface contract and origin handling

Virtual-desktop mode must support negative monitor origins and mixed layouts.

### Linux

Linux capture should expose the same target-selection model:

- enumerate active displays
- capture one selected monitor
- capture the stitched virtual desktop when requested

Linux must match Windows for target validation, metadata semantics, and explicit failure behavior even if the underlying capture mechanisms differ.

## Client Experience

### Start Flow

Starting remote desktop becomes:

1. request display list
2. show capture picker
3. send explicit `DesktopConfig`
4. start streaming

The picker should show:

- **All displays**
- each individual monitor with a friendly label
- a primary-display badge where relevant
- resolution information

### Remembered Choice

The client should remember the last-used target **per host** as a convenience, but still send an explicit mode every session. Persistence is client-side only; it must not reintroduce host-side implicit defaults.

### In-Session Switching

Users should be able to switch between **All displays** and a specific monitor during an active session from the remote desktop toolbar/menu.

When switching:

- the host updates the active target
- `DesktopMeta` and `DesktopStreamDescriptor` are resent
- `StreamSerial` increments
- the client briefly shows a switching/loading state and resets stale per-stream state

### Display Topology Changes

If the host display list changes while streaming:

- the client refreshes the available target list
- the current target remains active only if it is still valid
- otherwise the host sends an explicit error and the client prompts for a new selection

## Input Mapping

Input translation should remain based on the active surface bounds:

- client interaction is relative to the rendered frame
- host mapping translates into active logical coordinates
- final host dispatch adds `DesktopLeft` / `DesktopTop` to reach virtual-desktop absolute coordinates

This matches the current direction of `DispatchInput` and keeps negative-origin and non-primary layouts correct as long as all metadata is tied to the selected surface.

## Quality Bar And Testing

### Model and Protocol Tests

- capture target is required
- monitor mode requires a valid `displayId`
- invalid and stale `displayId` values fail explicitly
- display list messages serialize and round-trip correctly

### Handler Tests

- `RemoteDesktopHandler` starts correctly in `virtual_desktop` mode
- `RemoteDesktopHandler` starts correctly in `monitor` mode
- switching targets updates metadata and increments `StreamSerial`
- no implicit primary-monitor fallback occurs
- display-removal and invalid-target cases return explicit errors

### Platform Tests

Windows:

- display enumeration returns multiple outputs correctly
- virtual-desktop bounds include negative origins
- monitor-target capture uses the requested output
- both raw and JPEG capture paths honor the selected target
- DXGI recovery/backoff behavior still works after target loss

Linux:

- display enumeration and target validation follow the same contract
- both raw and JPEG-equivalent capture paths honor the selected target
- target switching preserves metadata semantics and explicit error behavior

### Client Tests

- display picker populates correctly
- last-used target is remembered per host
- switching targets resets stream state cleanly
- stale selections are invalidated when the host topology changes
- cursor overlay stays aligned in both monitor mode and virtual-desktop mode

### Manual Validation

- mixed-resolution monitor setups
- monitors positioned left of primary, above primary, and in negative coordinates
- switching between all-displays and single-monitor modes
- disconnecting a selected monitor during a session
- verifying cursor position near monitor edges
- verifying input accuracy after switching targets

## Implementation Guidance For Planning

The implementation plan should treat this as one feature slice with tightly related sub-work:

1. shared protocol/models for display enumeration and explicit target selection
2. host handler changes for target validation and active-surface metadata
3. Windows capture support for monitor and virtual-desktop targets
4. Linux capture parity for the same target model
5. client picker/switching UX and remembered selection
6. cursor authority alignment so first-party clients render the cursor consistently

It should not expand into transport redesign, multi-monitor simultaneous streaming, or mandatory H.264 adoption across every client.

## Recommended Next Step

Write an implementation plan that sequences protocol work first, then host target support, then client UX and cursor alignment, while preserving the capture resilience and codec-fallback behavior already fixed on the current branch.

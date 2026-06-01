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

The host must reject `desktop_start` requests that do not provide a valid explicit target.

To avoid ambiguity with existing quality/FPS updates, target changes should not reuse generic `desktop_config`. Add a dedicated request for active-target changes:

- `desktop_target_switch`

`desktop_config` remains the message for stream-quality and related session settings. `desktop_target_switch` is the only in-session message that changes the active display target.

### Display Enumeration

Add a dedicated request/response pair for display discovery before streaming begins:

- `desktop_display_query`
- `desktop_display_list`

`desktop_display_list` should also include:

- `displayListVersion`
- `supportedCaptureModes`
- `targetSwitchMode` (`seamless`, `reselection_required`, or `unsupported`)
- `enumerationMode` (`direct`, `consent_required`, or `virtual_desktop_only`)
- `cursorTransportMode` (`metadata_overlay` or `embedded_only`)

The display list response should contain one entry per active monitor with:

- `displayId`
- `persistentDisplayKey`
- `name`
- `isPrimary`
- `desktopLeft`
- `desktopTop`
- `logicalWidth`
- `logicalHeight`
- `pixelWidth`
- `pixelHeight`

`displayId` is a session-local runtime handle and must not be reused for a different physical/logical display for the lifetime of the connected host session.

`persistentDisplayKey` is the best durable identity the host can provide for reconnect-time selection memory. On Windows this should prefer a connector/monitor identity derived from display device metadata. On Linux it should prefer compositor or connector identity when available and fall back to a heuristic composed from monitor name, origin, size, and primary status when no stronger identity exists.

`displayListVersion` increments whenever the host topology changes. `desktop_start` and `desktop_target_switch` must echo the version they were built against. The host rejects requests built against an older version so stale selections fail explicitly instead of resolving to the wrong target.

On Wayland/portal-backed runtimes, `desktop_display_query` is allowed to trigger the necessary host-managed consent/session creation step when `enumerationMode == consent_required`. If the runtime still cannot expose individual monitor identities after consent, the host must return `enumerationMode == virtual_desktop_only`, advertise only `virtual_desktop` in `supportedCaptureModes`, and omit per-monitor entries rather than inventing unstable monitor IDs.

`desktop_display_query` must have explicit failure outcomes. If consent is denied, cancelled, timed out, or the portal/backend is unavailable, the host returns a query error and does not start or partially start a remote desktop stream. The client remains in the picker/error state and may retry the query or abort without any half-open remote desktop session being created.

### Active Surface Metadata

`DesktopMeta` and `DesktopStreamDescriptor` should describe the **currently selected capture surface**, not always the whole desktop.

For both `virtual_desktop` and `monitor` modes:

- `DesktopLeft` / `DesktopTop` are the selected surface origin in host virtual-desktop coordinates
- `ScreenWidth` / `ScreenHeight` remain the encoded frame dimensions for compatibility
- `LogicalWidth` / `LogicalHeight` describe the active logical capture surface
- `PixelWidth` / `PixelHeight` describe the actual transmitted frame size
- `StreamSerial` increments when the active target changes
- `StreamSerial` also increments whenever the active surface geometry changes, even if the selected target remains logically the same

This preserves the existing mapping model while making monitor switching explicit and safe.

### Error Semantics

The host must classify failures as either **start-request validation errors**, **active-target loss**, or **transient capture loss**.

The host must reject the request immediately for:

- missing capture mode
- `monitor` mode without `displayId`
- invalid or stale `displayId`

The host must **not** silently fall back to the primary monitor or another available display.

If a selected target disappears or becomes permanently unavailable during an active session, the host must send an explicit **active-target-lost** notification, stop frame delivery, and move the remote desktop session into a **no active target** state. The connection remains open and may accept a new `desktop_target_switch` request; a reconnect is not required.

If the backend encounters a non-recoverable failure for the current selected target during an active session, it should use the same **active-target-lost** path rather than silently switching to another target.

Transient capture loss is different. Recoverable DXGI/portal/compositor loss must keep the session alive, preserve the selected target, and continue using the existing retry/backoff and throttled-logging behavior. During transient loss, the host may emit a non-terminal status notification, but it must not convert temporary backend loss into a terminal monitor-selection failure unless recovery is definitively no longer possible.

### Topology Change Notifications

Display topology changes require an explicit protocol signal. Add:

- `desktop_display_list_changed`

This notification tells the client to re-query the display list. It should include:

- `displayListVersion`
- whether the current active target is still valid

If topology changes affect the active surface geometry, the host must resend `DesktopMeta` and `DesktopStreamDescriptor` and increment `StreamSerial`, even if the user is still viewing the same logical target.

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

### Cursor Protocol Payload

Client-rendered cursor mode requires explicit cursor metadata, not just `CursorX` and `CursorY`.

Add:

- `desktop_cursor_state`
- `desktop_cursor_shape`

For the new display-target protocol used by first-party clients, `DesktopConfig.DrawCursor` should be treated as disabled and host-side cursor composition should remain off. `desktop_cursor_state` becomes the authoritative runtime cursor feed.

`DesktopMeta.CursorX` and `CursorY` may continue to mirror the latest relative pointer coordinates only as a transitional/back-compat field for older readers. First-party clients implementing this design should render from `desktop_cursor_state`, not from `DesktopMeta.CursorX` and `CursorY`.

`desktop_cursor_state` should contain:

- `cursorSerial`
- `shapeSerial`
- `visible`
- `relativeX`
- `relativeY`
- `hotspotX`
- `hotspotY`

`desktop_cursor_shape` should contain:

- `shapeSerial`
- `width`
- `height`
- `hotspotX`
- `hotspotY`
- `pixelFormat` (ARGB for first-party clients)
- `shapeBytes`

The host must send an initial cursor bootstrap immediately after stream start and after every `StreamSerial` change:

- one `desktop_cursor_state` with the current position/visibility
- one `desktop_cursor_shape` for the active `shapeSerial`

Clients cache the last cursor shape by `shapeSerial` and only replace the cached bitmap when the shape changes. If shape capture is temporarily unavailable, the host should continue sending position and visibility state, and the client should fall back to a standard local arrow cursor rather than showing no cursor.

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

For Wayland/portal-backed capture, the design must account for user-mediated source selection:

- the host should map portal-provided sources into the shared display-list contract when the compositor exposes monitor sources
- `desktop_display_query` may perform the consent/session-creation work needed to obtain those sources
- if switching targets requires restarting the underlying portal capture session or renewed user consent, that is acceptable, but it must be surfaced explicitly rather than pretending switching is seamless
- if the runtime cannot provide stable monitor-specific sources, Linux must explicitly degrade to `enumerationMode == virtual_desktop_only` instead of pretending monitor mode exists
- the host should advertise whether in-session switching is seamless or requires capture-source reselection so the client can present the right UX
- when first-party cursor overlay mode is used, the Linux backend must request metadata/hidden-cursor capture from the portal/backend when that capability exists
- if the runtime only supports embedded-cursor capture, the host must advertise `cursorTransportMode == embedded_only`, keep the cursor inside the captured frame, and first-party clients must disable their overlay for that session to avoid double cursors

The shared protocol remains the same even when the Linux backend needs a capture-session restart behind the scenes.

## Client Experience

### Start Flow

Starting remote desktop becomes:

1. request display list
2. show capture picker
3. send explicit `DesktopConfig`
4. start streaming

If `enumerationMode == consent_required`, step 1 may involve a host-managed local consent flow before the final display list is returned. If `enumerationMode == virtual_desktop_only`, the client should present only the all-displays path for that host/runtime.

The picker should show:

- **All displays**
- each individual monitor with a friendly label
- a primary-display badge where relevant
- resolution information

### Remembered Choice

The client should remember the last-used target **per host** as a convenience, but still send an explicit mode every session. Persistence is client-side only; it must not reintroduce host-side implicit defaults.

The remembered selection should store:

- `captureMode`
- `persistentDisplayKey` when the mode is `monitor`

On reconnect, the client should resolve the stored `persistentDisplayKey` against the latest display list. If no exact match exists, the client should discard the stale remembered monitor and require a fresh explicit user choice instead of guessing.

### In-Session Switching

Users should be able to switch between **All displays** and a specific monitor during an active session from the remote desktop toolbar/menu.

When switching:

- the client sends `desktop_target_switch`
- the request includes the current `displayListVersion`
- the host updates the active target
- `DesktopMeta` and `DesktopStreamDescriptor` are resent
- `StreamSerial` increments
- the client briefly shows a switching/loading state and resets stale per-stream state

If `targetSwitchMode == reselection_required`, the UI should present that clearly and keep the session in an explicit switching state until the host confirms the new target or reports an `active-target-lost` or validation error. If `targetSwitchMode == unsupported`, the client should hide in-session switching while still allowing target choice before session start.

### Display Topology Changes

If the host display list changes while streaming:

- the client refreshes the available target list
- the current target remains active only if it is still valid
- otherwise the host sends `active-target-lost`, pauses the stream in the **no active target** state, and the client prompts for a new explicit selection

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

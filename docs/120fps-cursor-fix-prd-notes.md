# PRD Notes: 120 FPS and Native Cursor Fix

## Raw Requirements
- Fix the missing cursor on Monitor 2.
- Ensure the cursor shown is the native Windows cursor, not a drawn Android overlay.
- Fix the 50 FPS cap so the stream runs at a smooth 60 FPS.
- Support 120 FPS to match the user's phone display capabilities.
- Make 120 FPS the default setting for both the host and the Android client.
- Verify Linux remote desktop side to ensure 120 FPS is achievable there as well.

## Constraints
- Must not degrade stream quality.
- Must remain compatible with existing architectures (DXGI/PipeWire/Fallback).
- Must avoid changing global system settings (like `timeBeginPeriod`) if possible to prevent side-effects.

## Inferred Patterns (from codebase)
| Edge Case | Source | Pattern Applied |
|-----------|--------|-----------------|
| Frame Pacing | `RemoteDesktopHandler.cs` | `await Task.Delay` calculation based on target FPS |
| Cursor Overrides | `RemoteDesktopViewModel.kt` | Android sends `drawCursor = false` on connect |
| Configuration Defaults | `DesktopConfig.cs` | Default target FPS is 10 (host) and 30 (client) |

## Edge Cases
### Auto-handled (following codebase patterns)
- If the host physical monitor is 60Hz, 120 FPS target will simply repeat frames (DXGI `AcquireNextFrame` block behavior).

### Confirmed by User
- Physical monitors are 240Hz and 360Hz, so native 120Hz+ output is not a bottleneck.

## Research Findings
- **Cursor Bug:** The Android client explicitly overrides native cursor compositing (`put("drawCursor", false)`). This forces Android to draw its own fake cursor. Android fails to calculate the cursor offset properly for Monitor 2 (`desktopLeft`), making the fake cursor invisible.
- **FPS Bug:** `Task.Delay` on Windows has a default resolution of 15.6ms. A required sleep of 11ms (for 60 FPS) will oversleep to ~15.6ms, causing the total loop to exceed 16.6ms and capping the framerate at ~48 FPS. The same logic is shared for Linux pacing.

## Architecture Options

- **Option A (System Timer P/Invoke):** Call `timeBeginPeriod(1)` to increase Windows timer resolution.
  - Pros: Easy to implement, makes `Task.Delay` accurate to 1ms.
  - Cons: Global system side-effect, drains battery faster on laptops, Windows 11 deprecated it.
- **Option B (Hybrid Precision Sleep):** Use `Task.Delay` for intervals > 16ms, then `Stopwatch` + `Thread.SpinWait` for the remaining milliseconds.
  - Pros: Completely localized, zero global side-effects, accurate to the sub-millisecond, works perfectly on Linux too.
  - Cons: `SpinWait` uses slightly more CPU for a few milliseconds per frame.

**Selected**: Option B - Safest and most precise method without altering global OS behavior.

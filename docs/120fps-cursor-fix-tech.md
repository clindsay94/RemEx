# Technical Design: 120 FPS and Native Cursor Fix

> Status: COMPLETE
> Last updated: 2026-06-16

## Overview
The architecture solves two main issues: overriding the Android-side cursor draw instruction to re-enable Host-composited cursors, and bypassing the Windows thread sleep resolution limit via a hybrid `Task.Delay` and `SpinWait` algorithm to hit 120 FPS timings precisely.

## Key Components

### 1. `RemoteDesktopHandler.cs` (Host)
This component manages the cross-platform frame loop and sleeps between capturing frames to maintain the `TargetFps`. 
* **The Bug:** `await Task.Delay(sleep)` is used. If `sleep` is 11ms, Windows forces it to sleep for a minimum of 15.6ms, ruining the frame pacing.
* **The Fix:** Implement a hybrid sleep. Yield the thread via `Task.Delay(sleep - 15)` if the sleep duration is greater than 16ms, then use a high-precision `Thread.SpinWait` in a tight loop monitoring a `Stopwatch` for the final few milliseconds.

### 2. `RemoteDesktopViewModel.kt` (Android Client)
This component generates the initial `DesktopConfig` payload sent to the host.
* **The Bug:** It explicitly writes `put("drawCursor", false)`, preventing the host from compositing the cursor.
* **The Fix:** Change this payload to `put("drawCursor", true)`.

### 3. Shared Defaults Configuration
* **Host (`DesktopConfig.cs` & `RemoteDesktopHandler.cs`):** Initializing `_targetFps` default to 120 instead of 30.
* **Client (`RemoteDesktopConfigState`):** Initializing `targetFps` default to 120 instead of 30.

## API Design
No API signatures are changed. The JSON payload structure for `DesktopConfig` remains unchanged, only the values sent within it are updated.

## Data Flow
1. Android connects and sends `{ "targetFps": 120, "drawCursor": true }`.
2. `RemoteDesktopHandler` parses this and begins capturing.
3. Host captures frame, natively draws cursor over it.
4. `StreamFramesAsync` calculates how many milliseconds to wait until the next frame is needed (e.g., 8.33ms).
5. The Hybrid wait triggers a `SpinWait` directly since 8.33 < 16, hitting the target precisely.
6. The exact 120Hz frame pacing is maintained and streamed back to the client.

## Implementation Details
The hybrid wait in `RemoteDesktopHandler.cs` must check for cancellation requests `!ct.IsCancellationRequested` inside the `SpinWait` loop to ensure the stream can shut down cleanly without deadlocking.

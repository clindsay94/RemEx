# PRD: 120 FPS and Native Cursor Fix

> Status: COMPLETE
> Last updated: 2026-06-16

## Table of Contents
- [Problem Statement](#problem-statement)
- [Goals and Non-Goals](#goals-and-non-goals)
- [Success Criteria](#success-criteria)
- [Scope](#scope)
- [Requirements](#requirements)
- [User Flows](#user-flows)
- [Implementation Plan](#implementation-plan)

---

## Problem Statement
The RemEx remote desktop experience currently suffers from two major degradations:
1. **Broken Cursor on Multi-Monitor Setups:** The cursor completely disappears when moved to secondary monitors. On the primary monitor, an incorrect (Android-drawn) generic cursor is displayed instead of the true native Windows cursor.
2. **Artificial 50 FPS Bottleneck:** Despite supporting high-refresh-rate displays and powerful hardware, the streaming framerate is artificially capped at ~45-50 FPS due to frame pacing inaccuracies in the Windows Host application.

## Goals and Non-Goals
### Goals
- Ensure the native Windows OS cursor is correctly composited directly into the video stream for all monitors.
- Completely eliminate the 48 FPS frame pacing bottleneck in the host application without modifying global OS behaviors.
- Formally support and default to 120 FPS streaming on both the Android client and the Windows/Linux hosts.

### Non-Goals
- Changing the video encoding architecture (H.264/MJPEG remain the same).
- Rewriting the Android rendering pipeline.

## Success Criteria
- The true Windows cursor is visible across all connected monitors natively.
- The Android client natively reports `120 FPS` as its target FPS configuration without the user having to change default settings.
- Assuming the physical host monitor is 120Hz or higher, the RemEx connection achieves and reliably sustains a 120 FPS transmission rate with an accurate frame pacing interval of ~8.33ms.

## Scope
### In Scope
- Modifying `RemoteDesktopHandler.cs` (Host) to implement a high-precision sub-millisecond sleep algorithm for frame pacing.
- Removing Android's explicit `put("drawCursor", false)` payload.
- Bumping the `_targetFps` defaults from 30/10 to 120 on the Host (`DesktopConfig.cs` and `RemoteDesktopHandler.cs`).
- Bumping `targetFps` default from 30 to 120 on the Client (`RemoteDesktopConfigState`).

### Out of Scope
- Changes to `DxgiDesktopCapture` implementation logic.
- Adding arbitrary >120 FPS support (e.g. 240 FPS), as current mobile displays top out at 120Hz.

## Requirements
**Functional Requirements:**
- **Host Frame Pacing:** The frame loop (`StreamFramesAsync`) must accurately sleep to sub-millisecond precision, bypassing the Windows OS 15.6ms thread sleep limitation.
- **Client Configuration:** The Android client must not override the host's `DrawCursor` configuration to `false`.
- **Defaults:** Both platforms must default to 120 FPS targets on fresh installs or connections.

## User Flows
- A user connects to their PC using the Android RemEx app.
- They immediately see their actual Windows cursor on both Monitor 1 and Monitor 2.
- The stream dynamically scales up to 120 FPS by default, taking full advantage of the phone's 120Hz display screen, providing completely fluid interactions.

## Implementation Plan
See `120fps-cursor-fix-tech.md` for technical design details.

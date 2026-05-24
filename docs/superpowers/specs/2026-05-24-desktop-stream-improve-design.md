# Desktop Stream Improvement Design

## Summary

RemEx's current remote desktop path is optimized for correctness and portability, not peak throughput. On Linux, the host currently captures full PipeWire frames, JPEG-encodes them synchronously with SkiaSharp, and sends them over the existing WebSocket channel as MJPEG. The Android client decodes the JPEG frames into `Bitmap`s and renders them in Compose.

That design is simple and robust, but it scales poorly at large desktop sizes such as 5120x1440. The immediate improvement path is to reduce unnecessary full-frame work and tighten pacing in the existing MJPEG pipeline. The long-term improvement path is to add a hardware-backed video stream path while keeping MJPEG as a compatibility fallback.

## Current Baseline

### Host

- `Remex.Host/Handlers/RemoteDesktopHandler.cs`
  - advertises `Codec = Mjpeg`
  - captures one frame per loop
  - sends each encoded frame as a binary WebSocket message
  - throttles after work is complete based on `targetFps`
- `Remex.Host/Services/ScreenCapture/LinuxScreenCaptureService.cs`
  - waits up to 80 ms for a PipeWire frame
  - falls back to legacy capture tools when needed
- `Remex.Host/Services/RemoteDesktop/Linux/Capture/LinuxCaptureSessionCoordinator.cs`
  - acquires frames with a 50 ms timeout
  - keeps only the latest frame in a single-slot channel
- `Remex.Host/Services/RemoteDesktop/Linux/Capture/LinuxJpegEncoder.cs`
  - performs full-frame JPEG encoding in software
  - optionally scales before encode using SkiaSharp

### Client

- `RemEx.Android/.../RemoteDesktopViewModel.kt`
  - decodes incoming JPEG bytes into `Bitmap`
  - reuses `BitmapFactory.Options` and `inBitmap` where possible
  - displays FPS based on decoded frame cadence

## Problem Statement

At high resolutions, the current design spends too much time on work that repeats for every frame:

1. capture and copy large pixel buffers
2. optionally scale those buffers
3. JPEG-compress the entire frame in software
4. send the full compressed image over WebSocket
5. decode the full image on Android

This makes RemEx more sensitive to host CPU cost than to network bandwidth. Fast local networking helps less than expected because the dominant cost is repeated full-frame processing.

## Goals

- Improve practical FPS on large desktops without regressing reliability.
- Preserve the current cross-platform desktop stream as a fallback path.
- Keep the next improvement stage incremental enough to ship in slices.
- Create a path toward GPU-backed encoding for high-end systems.

## Non-Goals

- Replacing the entire remote desktop stack in one step.
- Making Linux, Windows, and Android all switch to a new codec at once.
- Introducing a transport that requires a full networking rewrite before any gain is realized.

## Approaches

### Approach 1: Optimize the Existing MJPEG Pipeline

Keep MJPEG and WebSockets, but make the current path substantially cheaper.

Possible changes:

- tighten PipeWire wait/pacing so the stream loop does not inherit avoidable 50-80 ms stalls
- lower default quality/scale for very large desktops using adaptive presets
- skip encode/send when the frame is unchanged or nearly unchanged
- add region-of-interest / dirty-rectangle support so small screen changes do not force full-frame JPEG every tick
- move JPEG encode off the hot request loop into a dedicated producer/consumer pipeline
- add instrumentation for capture time, encode time, send time, decode time, dropped frames, and average frame size

**Pros**

- smallest architectural change
- preserves current compatibility
- fastest route to visible improvement

**Cons**

- still fundamentally full-frame image streaming
- still CPU-heavy at high resolutions
- ceiling remains lower than hardware video encode

### Approach 2: Hybrid Stream Pipeline With Smarter Compression

Keep the existing WSS stream model, but upgrade the payload format before jumping to full video codecs.

Possible changes:

- keyframe + delta frame model
- tile-based updates
- optional PNG/WebP lossless tiles for text/UI-heavy regions and JPEG for photo-heavy regions
- separate cursor stream from image frames everywhere
- content-aware presets for desktop UI versus gaming/video content

**Pros**

- significantly more efficient than full-frame MJPEG
- lower implementation risk than WebRTC or a full media stack
- still fits the current RemEx command channel architecture fairly well

**Cons**

- custom protocol complexity increases sharply
- client and host both need more state management
- still not as efficient as mature hardware video stacks

### Approach 3: Hardware-Backed Video Encode Path

Introduce a new stream mode based on H.264/HEVC/AV1 using hardware encoders where available, while preserving MJPEG as fallback.

Likely shape:

- Linux: PipeWire capture -> GPU/accelerated encoder if feasible, otherwise software fallback
- Windows: DXGI capture -> NVENC / AMF / Quick Sync path where supported
- Android: hardware decoder first
- transport options:
  - keep existing control/input on WSS and add a separate media path
  - or adopt WebRTC for media while keeping RemEx pairing/control protocol

**Pros**

- biggest performance upside
- best fit for high-resolution / high-FPS streaming
- actually uses hardware like RTX-class GPUs effectively

**Cons**

- highest implementation complexity
- codec, capability, transport, and synchronization work all expand
- platform-specific encoder support increases maintenance cost

## Recommendation

Use a **hybrid roadmap**:

1. **Short term:** optimize the current MJPEG path so 1440p+ desktops stop wasting work.
2. **Medium term:** add smarter frame-diff / tile-based behavior if MJPEG optimization alone is not enough.
3. **Long term:** add a hardware video stream path for high-performance devices, with MJPEG kept as fallback.

This sequence gives RemEx a practical near-term win without betting the entire desktop stack on a single large rewrite.

## Proposed Roadmap

### Phase 1: Instrument and Remove Obvious Throughput Loss

Add timing and counters around:

- PipeWire frame wait time
- capture-to-encode handoff
- JPEG encode duration
- WebSocket send duration
- Android decode duration
- effective FPS and frame drop counts

Then implement:

- separate capture, encode, and send stages
- latest-frame semantics between stages
- adaptive quality/scale guardrails for ultra-wide and 4K-class desktops
- faster defaults for large resolutions
- early exit when a frame is unchanged

**Expected outcome:** better FPS with minimal protocol change and clear evidence about where time is being spent.

### Phase 2: Reduce Full-Frame Work

If Phase 1 still leaves the stream in the low teens on large desktops, add:

- dirty-rectangle or tile-based change detection
- explicit keyframe + delta update protocol
- independent cursor updates and smaller non-cursor frame payloads

**Expected outcome:** desktop/UI workloads become much cheaper than video/game workloads, which is a better match for typical remote-control usage.

### Phase 3: Add a Video Codec Path

Add a negotiated codec capability model:

- MJPEG: universal fallback
- H.264: first hardware-backed target
- HEVC or AV1: later optional targets

Keep input/control on the current RemEx channel and isolate the media layer so the rest of the app does not need a full rewrite.

**Expected outcome:** high-end hardware can finally translate into materially higher FPS and better bitrate efficiency.

## Architecture Notes

### Stream Capability Negotiation

The stream descriptor should evolve from "codec is MJPEG" to "host supports these modes":

- MJPEG
- MJPEG with delta tiles
- H.264
- HEVC
- AV1

The client should choose the best mutually supported mode rather than assuming one static codec.

### Pipeline Boundaries

Break the current hot loop into explicit stages:

1. **capture stage**
2. **frame preparation stage** (crop, scale, diff, metadata)
3. **encode stage**
4. **transport stage**
5. **client decode/render stage**

Each stage should expose metrics and bounded buffering so bottlenecks are measurable and backpressure is intentional.

### Transport

Near term:

- keep WebSockets for MJPEG and incremental protocol changes

Long term:

- evaluate whether media should stay on WSS or move to a dedicated media transport such as WebRTC
- keep control/input/pairing separate from the media implementation either way

## Risks

- Dirty-rectangle or tile protocols can become complex quickly if not carefully bounded.
- Hardware codec work can explode in scope if transport, negotiation, fallback, and synchronization are redesigned simultaneously.
- A custom delta-image format may become a dead end if it grows too close to "homegrown video codec."

## Suggested Success Metrics

- 5120x1440 desktop reaches materially higher FPS than the current low-teens baseline
- host encode time is no longer the dominant frame cost in the default configuration
- Android decode/render stays stable without excessive memory churn
- stream quality adapts predictably instead of relying on manually extreme settings like `quality=100`, `scale=1`, `fps=360`

## Recommended Next Step

Plan and implement **Phase 1 only** first:

- add end-to-end timing instrumentation
- refactor the host stream loop into staged capture/encode/send work
- tune defaults and pacing for large desktops
- document measured before/after results

That phase is small enough to execute safely and will show whether RemEx needs only better MJPEG engineering or a true codec upgrade.

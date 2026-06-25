# H.264 Android Decoder Investigation Plan

## Objective
Trace the H.264 remote-desktop decode path from native JNI frame callback through Kotlin to MediaCodec to identify why every frame is rejected (black screen + ~50/sec keyframe requests).

## Investigation Sequence

### Phase 1: Locate Frame Entry Point (JNI Boundary)
- Find the native JNI callback that delivers H.264 frame bytes from libRemexCore.so
- Search RemexCoreClient.kt for callback registration, listener patterns
- Identify method name and parameter types (raw bytes, timestamps, flags)
- Trace where this lands in RemoteDesktopViewModel.kt

**Key Questions:**
- What is the callback method name? (e.g., onFrameReceived, NotifyJavaFrame)
- Is it a listener/interface or direct JNI method?
- Are frame bytes delivered as ByteArray or ByteBuffer?

### Phase 2: Frame Envelope (RDXF) Parsing & Gating Logic
- Open RemoteDesktopFrameEnvelope.kt
- Find RDXF header structure and parsing logic
- **CRITICAL:** Check if non-keyframe frames are DROPPED until first keyframe is seen
- Identify codec tag extraction, keyframe flag detection, stream serial parsing
- Quote any conditional logic that gates frame delivery to decoder

**Key Questions:**
- Does the code skip frames with keyframe==false before first seen-keyframe?
- What is RDXF version, codec tag format, keyframe flag position?
- Is there a gating boolean/state machine that locks frames?

### Phase 3: H264StreamDecoder Constructor & Dimensions
- Open H264StreamDecoder.kt
- Find constructor and how width/height args are set
- Trace the width/height values back to their source:
  - Host screen size?
  - Frame envelope metadata?
  - Hardcoded fallback?
- Check for mismatch with actual encoded resolution (2560x1440)

**Key Questions:**
- Where do width/height come from? (constructor caller location)
- Is there a hardcoded default if values are missing?
- Could width/height be 0 or incorrect at decoder init time?

### Phase 4: SPS/PPS (Codec-Specific Data) Handling
- Check H264StreamDecoder.decodeFrame() call path
- Search for "csd-0", "csd-1", "BUFFER_FLAG_CODEC_CONFIG" references
- Confirm whether SPS/PPS is set on MediaFormat during setup
- Look for NALU parsing (SPS detection via type == 7)

**Key Questions:**
- Is SPS/PPS ever extracted from stream and fed to MediaFormat?
- Is BUFFER_FLAG_CODEC_CONFIG ever set?
- Are SPS/PPS NALUs fed separately or as part of I-frame data?

### Phase 5: Keyframe-Flag Gating Between Native & Decoder
- Map the complete path: native callback → ViewModel → FrameEnvelope → H264StreamDecoder
- Identify EVERY conditional that could discard a frame before it reaches decodeFrame()
- Note any state variables that track "first keyframe seen"
- Trace MediaCodec queueInputBuffer() calls

**Key Questions:**
- Is there a state machine blocking frames until first keyframe?
- Does discarding every non-initial-keyframe explain the black screen?
- What happens to frames queued to MediaCodec if decoder is not initialized?

## Files to Read (In Order)
1. RemexCoreClient.kt - Find JNI callback entry point
2. RemoteDesktopViewModel.kt - Trace callback landing and frame routing
3. RemoteDesktopFrameEnvelope.kt - Parse RDXF header and gating logic
4. H264StreamDecoder.kt - Check constructor args, SPS/PPS setup, decodeFrame() path

## Output Format
For each finding:
- **File path: line number(s)**
- **Code snippet** (quoted, 1-3 lines max)
- **Explanation** (1-2 sentences)

## Hypothesis to Test
**Primary:** Frames are dropped by gating logic until first keyframe is seen. Because the host sends IDR frames frequently (keyframe requests), every single I-frame is accepted but all P-frames are discarded. If the decoder has not been fed SPS/PPS, even I-frames fail to decode, resulting in black screen + rapid keyframe re-requests as the client waits for the magic first keyframe that will never arrive without SPS/PPS.

**Secondary:** Decoder dimensions (width/height) are hardcoded or wrong, causing MediaCodec to reject frames at a different resolution.

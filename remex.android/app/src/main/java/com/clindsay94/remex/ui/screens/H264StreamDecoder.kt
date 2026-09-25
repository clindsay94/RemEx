package com.clindsay94.remex.ui.screens

import android.media.MediaCodec
import android.media.MediaFormat
import android.util.Log
import android.view.Surface
import java.nio.ByteBuffer
import java.util.concurrent.LinkedBlockingDeque
import java.util.concurrent.TimeUnit

private const val TAG = "H264StreamDecoder"

// Bounded backlog of encoded access units waiting to be fed to the codec. A real-time stream only
// needs a couple of frames buffered; if the decoder briefly stalls we keep the newest few, drop the
// oldest, and ask the host for a keyframe so the next decodable point recovers cleanly. (RemEx-bqc)
private const val MAX_INPUT_BACKLOG = 6

/**
 * While the decoder waits for its first SPS/PPS-carrying IDR, re-request an on-demand keyframe on
 * this cadence. The first request still fires immediately (RemEx-p7fz); the retries cover a request
 * being lost (e.g. swallowed by the host's post-start reinit cooldown) so the wait can never be
 * open-ended. The host throttles floods on its side, so a retry inside its cooldown is merely
 * counted, never harmful. (RemEx-vj7b)
 */
private const val INITIAL_KEYFRAME_RETRY_MS = 2000L

// H.264 NAL unit types (nal_unit_type = first byte after the start code, low 5 bits).
private const val NAL_TYPE_IDR = 5   // IDR slice (keyframe)
private const val NAL_TYPE_SPS = 7   // Sequence Parameter Set
private const val NAL_TYPE_PPS = 8   // Picture Parameter Set

/**
 * Low-latency, hardware-accelerated H.264 Annex B video stream decoder.
 *
 * Uses MediaCodec in SYNCHRONOUS mode driven by a single dedicated decode thread that polls
 * [MediaCodec.dequeueInputBuffer] / [MediaCodec.dequeueOutputBuffer]. We deliberately do NOT use async
 * mode ([MediaCodec.setCallback]): on this app's deferred-configure path, the Qualcomm c2 decoder
 * reaches RUNNING with input buffers ready but `onInputBufferAvailable` never fires (verified on a
 * Galaxy S24 Ultra — 0 callbacks on both the main looper and a dedicated HandlerThread), so the codec
 * is never fed and the stream stays black. Synchronous polling sidesteps callback delivery entirely.
 *
 * **Codec-specific data (csd) + deferred configure.** The decode thread waits for the first access
 * unit carrying SPS (NAL 7) + PPS (NAL 8) — an IDR keyframe — then configures with explicit
 * `csd-0`/`csd-1` and starts. Configuring from the stream's own SPS also makes the codec adopt the
 * SPS-declared resolution, so a wrong width/height hint cannot matter. Access units before the first
 * keyframe are dropped (a P-frame is undecodable with no reference). IDR access units are queued with
 * [MediaCodec.BUFFER_FLAG_KEY_FRAME].
 *
 * Renders directly to the provided [surface] (a SurfaceView's holder.surface — SurfaceFlinger's
 * consumer accepts the decoder's native graphic buffers; a TextureView's GL SurfaceTexture does not).
 *
 * Do NOT set KEY_COLOR_FORMAT (surface dictates it), KEY_LOW_LATENCY, or KEY_OPERATING_RATE on the
 * format — the Qualcomm c2 decoder rejects them ("configureIntf failed 95 / not a supported pixel
 * format") and/or shrinks its buffer pool. (#2b decode-stall)
 */
class H264StreamDecoder(
    private val width: Int,
    private val height: Int,
    private val surface: Surface,
    /**
     * Invoked once if the hardware decoder fails to create/configure or the decode loop dies. The
     * owner should surface an error and/or trigger a reconnect rather than render black. (RemEx-x0b)
     */
    private val onInitFailure: (() -> Unit)? = null,
    /**
     * Invoked when the decoder drops input (backlog overflow / no free input buffer) and needs a fresh
     * decodable point. The owner asks the host for an on-demand IDR so the stream resyncs. (RemEx-bqc)
     */
    private val onKeyframeNeeded: (() -> Unit)? = null
) {
    @Volatile private var running = true

    // Encoded access units awaiting the decode thread (oldest at head). Thread-safe.
    /**
     * One queued access unit, as a RANGE into the array the frame arrived in.
     *
     * Carrying (bytes, offset, length) instead of a trimmed array is what lets the envelope's 28-byte
     * header be skipped without copying the whole frame a second time (RemEx-t8ku). Safe to retain
     * across threads because the JNI layer allocates a fresh array per frame and never reuses it.
     *
     * No path copies the whole access unit any more (perf audit P3-16): [findNalUnits] works on the
     * range directly and returns absolute indices into [bytes], so the initial configure and a
     * mid-stream SPS change copy out only the SPS and PPS NALs they hand to the codec as csd.
     */
    private data class AccessUnit(val bytes: ByteArray, val offset: Int, val length: Int)

    private val frameQueue = LinkedBlockingDeque<AccessUnit>()

    private val decodeThread = Thread({ runDecodeLoop() }, "H264DecodeLoop")

    // Written only on the decode thread; sampled cross-thread by the ViewModel's decode-progress
    // watchdog, hence @Volatile.
    @Volatile private var renderedFrames = 0

    /** Frames actually rendered to the surface — the watchdog's decode-progress signal. (RemEx-vj7b) */
    val renderedFrameCount: Int
        get() = renderedFrames

    // Adaptive-playback / input-buffer upper bound: the host's full-screen (scale 1.0) frame. We don't
    // know the exact monitor size here, so bound to 4K — that covers essentially all PC displays; the
    // explicit mid-stream reconfigure handles anything larger or decoders without adaptive support.
    private val maxWidth = maxOf(width, 3840)
    private val maxHeight = maxOf(height, 2160)
    // Compressed IDRs are far smaller than a raw frame, so an 8 MiB ceiling is a safe upper bound for
    // any resolution up to ~4K while keeping the input-buffer pool reasonable — and never under-sized
    // for a scale-up keyframe (the original bug). (RemEx-aep Phase 5)
    private val maxInputSize =
        (maxWidth.toLong() * maxHeight * 3 / 2).coerceIn(4L * 1024 * 1024, 8L * 1024 * 1024).toInt()

    // The csd-0 (SPS) bytes the codec is currently configured with. Used to detect a mid-stream SPS
    // change (the host rebuilt its encoder at a new capture scale) so we can reconfigure. Decode-thread only.
    private var configuredCsd0: ByteArray? = null

    // Count of access units fed to the codec (queueInputBuffer succeeded) whose decoded output hasn't
    // been drained yet. Decode-thread only. Used to pick the input-poll timeout each iteration: while
    // anything is in flight, output can complete asynchronously between iterations, so we must not sit
    // in a long blocking poll on the input queue instead of checking for it (RemEx-4j8ls round 2 —
    // round 1's fixed drain-before-poll call ran microseconds after the prior iteration's after-feed
    // drain and could never catch a frame that finished decoding in between). Reset to 0 whenever the
    // codec is stopped/reconfigured, since any in-flight buffers are discarded with it.
    private var inFlight = 0

    init {
        decodeThread.start()
    }

    /**
     * Hands one H.264 Annex B access unit to the decode thread. Non-blocking, callable from any thread.
     * Bounds the backlog: if full, drops the oldest and asks the host for a keyframe so recovery is
     * explicit rather than an unbounded memory grow or a torn GOP. (RemEx-bqc)
     */
    fun decodeFrame(bytes: ByteArray, offset: Int, length: Int) {
        if (length <= 0 || !running) {
            return
        }
        while (frameQueue.size >= MAX_INPUT_BACKLOG) {
            if (frameQueue.pollFirst() == null) break
            Log.w(TAG, "Input backlog full ($MAX_INPUT_BACKLOG); dropped oldest frame, requesting keyframe.")
            onKeyframeNeeded?.invoke()
        }
        frameQueue.offerLast(AccessUnit(bytes, offset, length))
    }

    private fun runDecodeLoop() {
        var codec: MediaCodec? = null
        try {
            codec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            Log.i(TAG, "MediaCodec H.264 decoder created (sync); awaiting SPS/PPS to configure (hint ${width}x$height).")
            val info = MediaCodec.BufferInfo()
            // Drain all currently-available decoded output to the surface (non-blocking,
            // timeoutUs=0). Called once per iteration, right after feeding input — see the
            // poll-timeout comment below for why a separate pre-poll drain isn't needed.
            fun drainOutput(codec: MediaCodec) {
                var outIndex = codec.dequeueOutputBuffer(info, 0)
                while (outIndex >= 0) {
                    val render = (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG) == 0
                    codec.releaseOutputBuffer(outIndex, render)
                    if (render) {
                        if (renderedFrames == 0) {
                            Log.i(TAG, "First decoded frame rendered to surface — decode pipeline is live.")
                        }
                        renderedFrames++
                        if (inFlight > 0) inFlight--
                    }
                    outIndex = codec.dequeueOutputBuffer(info, 0)
                }
                // outIndex < 0 is INFO_TRY_AGAIN_LATER / INFO_OUTPUT_FORMAT_CHANGED / buffers-changed —
                // rendering goes straight to the Surface. Instrument the format change: the codec's
                // output format is the AUTHORITATIVE decoded geometry (coded size + crop). Logging it on
                // every change lets us compare against the host's desktop_meta dims and the Compose
                // contentRect to pin down the mid-session zoom/black geometry mismatch. (RemEx-x3eb diag)
                if (outIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    val of = codec.outputFormat
                    fun geti(k: String) = if (of.containsKey(k)) of.getInteger(k) else -1
                    val cw = geti(MediaFormat.KEY_WIDTH)
                    val ch = geti(MediaFormat.KEY_HEIGHT)
                    val cl = geti("crop-left"); val cr = geti("crop-right")
                    val ct = geti("crop-top"); val cb = geti("crop-bottom")
                    val dispW = if (cr >= cl && cl >= 0) cr - cl + 1 else cw
                    val dispH = if (cb >= ct && ct >= 0) cb - ct + 1 else ch
                    Log.i(TAG, "OUTPUT_FORMAT_CHANGED: coded=${cw}x$ch crop=[$cl,$ct..$cr,$cb] display=${dispW}x$dispH")
                }
            }
            var configured = false
            // When we last asked the host for an on-demand IDR while waiting to configure. A decoder
            // created mid-GOP (e.g. an imageSize-driven SurfaceView rebuild) would otherwise sit
            // black until the host's next PERIODIC keyframe (RemEx-p7fz) — and a single request can
            // be lost to the host's post-start reinit cooldown, so retry on a slow cadence instead
            // of one-shot. 0L baseline → the first undecodable AU still requests immediately. (RemEx-vj7b)
            var lastConfigureKeyframeRequestMs = 0L

            while (running) {
                if (!configured) {
                    // Wait (briefly, interruptibly) for the next access unit; configure on the first one
                    // that carries SPS + PPS. Drop everything before that (no reference to decode).
                    val queued = frameQueue.pollFirst(50, TimeUnit.MILLISECONDS) ?: continue
                    // Rare path (pre-configure): full NAL index scan over the AU's range, in place.
                    val au = queued.bytes
                    val nals = findNalUnits(au, queued.offset, queued.length)
                    val sps = nals.firstOrNull { it.type == NAL_TYPE_SPS }
                    val pps = nals.firstOrNull { it.type == NAL_TYPE_PPS }
                    if (sps == null || pps == null) {
                        // We're receiving frames but joined mid-GOP (this AU is a P-frame — no SPS/PPS,
                        // undecodable with no reference). Rather than wait up to a full GOP for the host's
                        // next periodic IDR (black screen the whole time), ask for an on-demand keyframe
                        // so we configure from the very next AU. The throttle avoids flooding the host
                        // with a request per dropped P-frame while still retrying if an earlier request
                        // was lost. (RemEx-p7fz, RemEx-vj7b)
                        val nowMs = android.os.SystemClock.elapsedRealtime()
                        if (nowMs - lastConfigureKeyframeRequestMs >= INITIAL_KEYFRAME_RETRY_MS) {
                            lastConfigureKeyframeRequestMs = nowMs
                            onKeyframeNeeded?.invoke()
                        }
                        continue // keep waiting for the SPS/PPS-carrying IDR
                    }
                    val csd0 = au.copyOfRange(sps.start, sps.end)
                    val csd1 = au.copyOfRange(pps.start, pps.end)
                    applyFormatAndStart(codec, csd0, csd1)
                    configured = true
                    Log.i(TAG, "MediaCodec H.264 decoder configured+started (sync) from SPS/PPS (hint ${width}x$height, csd0=${csd0.size}B, csd1=${csd1.size}B; adaptive max ${maxWidth}x$maxHeight)")
                    feedInput(codec, queued, isKeyFrame = nals.any { it.type == NAL_TYPE_IDR })
                    continue
                }

                // Feed at most one input per iteration (block briefly so we don't spin when idle).
                // The poll timeout depends on whether anything fed is still awaiting output:
                //
                // - inFlight > 0: at least one access unit is mid-decode and its output can complete
                //   asynchronously at any time (MediaCodec decode is async even in this sync-polling
                //   driver — nvenc's `-tune ll` and libx264's `zerolatency` reliably disable B-frames,
                //   but vaapi/amf (and possibly qsv) do not set `-bf 0` and can reorder — see
                //   docs/PERF-TRACKER.md's P1-8 row). Keep the original short 2ms timeout so a frame
                //   that finishes decoding between iterations is drained promptly instead of sitting
                //   behind a long poll.
                // - inFlight == 0: nothing is pending, so there is nothing for a shorter timeout to
                //   catch sooner. Use the longer 20ms timeout here to cut idle wakeups (500/sec at 2ms
                //   down to 50/sec at 20ms, a 10x reduction) with no latency cost, since there is no
                //   in-flight frame it could be delaying.
                //
                // (Simpler, lower-risk alternative to a full output-wait redesign — see RemEx-4j8ls
                // round 2 review: a fixed drain-before-poll call cannot work because it runs
                // microseconds after the prior iteration's own after-feed drain.)
                val pollTimeoutMs = if (inFlight > 0) 2L else 20L
                val au = frameQueue.pollFirst(pollTimeoutMs, TimeUnit.MILLISECONDS)
                if (au != null) {
                    // ONE scan answers both per-frame questions — does this AU carry an SPS (reconfigure
                    // check) and an IDR slice (key-frame flag) — where there used to be two full scans
                    // of every P-frame, since a P-frame contains neither and so never exited either
                    // scan early (perf audit P3-16). It still stops as soon as both have been seen, so
                    // an IDR (SPS first, IDR slice a few NALs later) is only scanned a few bytes deep.
                    val nalTypes = scanNalTypes(au.bytes, au.offset, au.length, stopWhenSeen = SPS_AND_IDR_BITS)
                    val isKeyFrame = nalTypes.hasNalType(NAL_TYPE_IDR)
                    // A mid-stream SPS change means the host rebuilt its encoder at a new capture scale.
                    // Reconfigure so the decoder adopts the new resolution; otherwise a larger scale-up
                    // IDR no longer fits the input buffer / layout and the stream goes black or garbage.
                    if (!nalTypes.hasNalType(NAL_TYPE_SPS) ||
                        !maybeReconfigureForNewSps(codec, au, isKeyFrame)
                    ) {
                        feedInput(codec, au, isKeyFrame)
                    }
                }

                // Drain again after feeding — covers output produced by the AU just fed.
                drainOutput(codec)
            }
        } catch (e: InterruptedException) {
            // release() interrupted us — normal shutdown.
        } catch (e: Exception) {
            if (running) {
                Log.e(TAG, "H.264 decode loop failed: ${e.message}", e)
                onInitFailure?.invoke()
            }
        } finally {
            try { codec?.stop() } catch (_: Exception) { /* best effort */ }
            try { codec?.release() } catch (_: Exception) { /* best effort */ }
            Log.i(TAG, "MediaCodec H.264 decoder released.")
        }
    }

    /**
     * Dequeues an input buffer (short wait) and queues [au] into it, flagged
     * [MediaCodec.BUFFER_FLAG_KEY_FRAME] when [isKeyFrame] (the AU carries an IDR slice — the caller
     * has already scanned it). Runs on the decode thread.
     */
    private fun feedInput(codec: MediaCodec, au: AccessUnit, isKeyFrame: Boolean) {
        val inIndex = try {
            codec.dequeueInputBuffer(10_000) // 10 ms
        } catch (e: IllegalStateException) {
            return
        }
        if (inIndex < 0) {
            // No free input buffer right now — drop and ask for a keyframe so we resync from the next
            // IDR rather than feeding a gap.
            onKeyframeNeeded?.invoke()
            return
        }
        try {
            val inputBuffer = codec.getInputBuffer(inIndex) ?: return
            inputBuffer.clear()
            inputBuffer.put(au.bytes, au.offset, au.length)
            val flags = if (isKeyFrame) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0
            codec.queueInputBuffer(inIndex, 0, au.length, System.nanoTime() / 1000, flags)
            inFlight++
        } catch (e: RuntimeException) {
            // IllegalStateException (codec released) or BufferOverflowException (AU > input buffer,
            // guarded by KEY_MAX_INPUT_SIZE but stay defensive) — drop and recover.
            Log.w(TAG, "queueInputBuffer failed: ${e.message}")
        }
    }

    /** Builds the decode MediaFormat from the stream's own SPS/PPS, sized for the adaptive max. */
    private fun buildFormat(csd0: ByteArray, csd1: ByteArray): MediaFormat =
        MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
            // Size the input buffer for the host's full-screen (scale 1.0) IDR, not the initial hint —
            // a scale-up keyframe must still fit (this is the fix for the scale-up black screen).
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, maxInputSize)
            // Adaptive playback: pre-size for the max so most scale changes are absorbed without a hard
            // reconfigure. Decoders without FEATURE_AdaptivePlayback ignore these keys; the explicit
            // reconfigure in maybeReconfigureForNewSps is the guaranteed fallback.
            setInteger(MediaFormat.KEY_MAX_WIDTH, maxWidth)
            setInteger(MediaFormat.KEY_MAX_HEIGHT, maxHeight)
            // The codec adopts the SPS-declared resolution from csd-0, so the width/height hint above
            // need not be exact — a stale hint cannot matter.
            setByteBuffer("csd-0", ByteBuffer.wrap(csd0))
            setByteBuffer("csd-1", ByteBuffer.wrap(csd1))
        }

    /** Configures + starts the codec from the given csd and records the active SPS. Decode thread only. */
    private fun applyFormatAndStart(codec: MediaCodec, csd0: ByteArray, csd1: ByteArray) {
        codec.configure(buildFormat(csd0, csd1), surface, null, 0)
        codec.start()
        configuredCsd0 = csd0
    }

    /**
     * If [au] carries an SPS whose bytes differ from the currently-configured csd-0, reconfigures the
     * codec (stop → configure → start) for the new resolution, feeds [au] (an IDR) into the fresh codec,
     * and returns true. Returns false when there is no SPS or it is unchanged, so the caller feeds [au]
     * normally. Comparing raw SPS NAL bytes avoids fragile Exp-Golomb parsing. Only called for an AU
     * the caller's cheap [scanNalTypes] pre-check found an SPS in, which keeps P-frames on the fast
     * path. On the host, a rebuilt encoder emits a fresh SPS/PPS + IDR, so the stream's own SPS is
     * authoritative — no separate dimension protocol is needed. A stop/configure/start failure propagates to the decode loop's catch, which signals
     * onInitFailure so the owner reconnects. (RemEx-aep Phase 5)
     */
    private fun maybeReconfigureForNewSps(codec: MediaCodec, queued: AccessUnit, isKeyFrame: Boolean): Boolean {
        // In place on the RANGE (P3-16): absolute indices into queued.bytes, no whole-AU copy.
        val au = queued.bytes
        val nals = findNalUnits(au, queued.offset, queued.length)
        val sps = nals.firstOrNull { it.type == NAL_TYPE_SPS } ?: return false
        val newCsd0 = au.copyOfRange(sps.start, sps.end)
        if (newCsd0.contentEquals(configuredCsd0)) return false // same SPS — no resolution change
        val pps = nals.firstOrNull { it.type == NAL_TYPE_PPS } ?: return false
        val csd1 = au.copyOfRange(pps.start, pps.end)
        Log.i(TAG, "SPS changed mid-stream (csd0 ${configuredCsd0?.size}B -> ${newCsd0.size}B); reconfiguring decoder.")
        codec.stop()
        inFlight = 0 // stop() discards any buffers that were mid-decode
        applyFormatAndStart(codec, newCsd0, csd1)
        feedInput(codec, queued, isKeyFrame)
        return true
    }

    /** Releases native resources and stops the decode thread. */
    fun release() {
        running = false
        decodeThread.interrupt()
        // The decode loop's finally block stops + releases the codec on its own thread.
    }
}

// --- Annex B NAL parsing -----------------------------------------------------------------------
// Top-level and internal (not private to the class) so the JVM unit tests can drive them directly;
// they touch no Android API.

private const val SPS_AND_IDR_BITS = (1 shl NAL_TYPE_SPS) or (1 shl NAL_TYPE_IDR)

/** True if the [scanNalTypes] bitmask has [type]'s bit set. */
internal fun Int.hasNalType(type: Int): Boolean = (this and (1 shl type)) != 0

/**
 * A NAL unit located within an access unit. [start] is the index of its start-code first byte and
 * [end] is exclusive; both are ABSOLUTE indices into the array that was scanned, not relative to the
 * scanned range's offset.
 */
internal class NalUnit(val start: Int, val end: Int, val type: Int)

/**
 * Splits the Annex B range `b[offset, offset + length)` into NAL units on 3- or 4-byte start codes
 * (00 00 01 / 00 00 00 01). Each unit's [NalUnit.start] is the index of the start code so a slice
 * [start, end) is a self-contained, start-code-prefixed NAL suitable for csd-0/csd-1. Nothing before
 * [offset] is ever read (a 4-byte code's leading 0x00 is only looked for inside the range), so bytes
 * ahead of the AU — the frame envelope header — cannot leak into a NAL.
 */
internal fun findNalUnits(b: ByteArray, offset: Int, length: Int): List<NalUnit> {
    val starts = ArrayList<Int>(8)
    val headers = ArrayList<Int>(8)
    val n = offset + length
    var i = offset
    while (i + 2 < n) {
        if ((b[i].toInt() and 0xFF) == 0 &&
            (b[i + 1].toInt() and 0xFF) == 0 &&
            (b[i + 2].toInt() and 0xFF) == 1
        ) {
            // Start code core is 00 00 01 at i. Include a preceding 0x00 (4-byte code) in the unit.
            val scStart = if (i > offset && (b[i - 1].toInt() and 0xFF) == 0) i - 1 else i
            starts.add(scStart)
            headers.add(i + 3)
            i += 3
        } else {
            i++
        }
    }
    val result = ArrayList<NalUnit>(starts.size)
    for (k in starts.indices) {
        val headerIdx = headers[k]
        if (headerIdx >= n) continue
        val end = if (k + 1 < starts.size) starts[k + 1] else n
        val type = b[headerIdx].toInt() and 0x1F
        result.add(NalUnit(starts[k], end, type))
    }
    return result
}

/**
 * The NAL unit types present in `b[offset, offset + length)`, as a bitmask (bit `t` set = at least one
 * NAL of type `t`; test with [hasNalType]). Stops as soon as every bit in [stopWhenSeen] has been
 * seen. For each type it answers exactly what the old per-type `containsNalType` scan answered — same
 * start-code walk, same header-byte bound — but one pass serves every type the caller asks about.
 */
internal fun scanNalTypes(b: ByteArray, offset: Int, length: Int, stopWhenSeen: Int): Int {
    val n = offset + length
    var seen = 0
    var i = offset
    while (i + 3 < n) {
        if ((b[i].toInt() and 0xFF) == 0 &&
            (b[i + 1].toInt() and 0xFF) == 0 &&
            (b[i + 2].toInt() and 0xFF) == 1
        ) {
            seen = seen or (1 shl (b[i + 3].toInt() and 0x1F))
            if ((seen and stopWhenSeen) == stopWhenSeen) return seen
            i += 3
        } else {
            i++
        }
    }
    return seen
}

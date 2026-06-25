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
    private val frameQueue = LinkedBlockingDeque<ByteArray>()

    private val decodeThread = Thread({ runDecodeLoop() }, "H264DecodeLoop")

    // Touched only on the decode thread.
    private var renderedFrames = 0

    init {
        decodeThread.start()
    }

    /**
     * Hands one H.264 Annex B access unit to the decode thread. Non-blocking, callable from any thread.
     * Bounds the backlog: if full, drops the oldest and asks the host for a keyframe so recovery is
     * explicit rather than an unbounded memory grow or a torn GOP. (RemEx-bqc)
     */
    fun decodeFrame(bytes: ByteArray) {
        if (bytes.isEmpty() || !running) {
            return
        }
        while (frameQueue.size >= MAX_INPUT_BACKLOG) {
            if (frameQueue.pollFirst() == null) break
            Log.w(TAG, "Input backlog full ($MAX_INPUT_BACKLOG); dropped oldest frame, requesting keyframe.")
            onKeyframeNeeded?.invoke()
        }
        frameQueue.offerLast(bytes)
    }

    private fun runDecodeLoop() {
        var codec: MediaCodec? = null
        try {
            codec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            Log.i(TAG, "MediaCodec H.264 decoder created (sync); awaiting SPS/PPS to configure (hint ${width}x$height).")
            val info = MediaCodec.BufferInfo()
            var configured = false

            while (running) {
                if (!configured) {
                    // Wait (briefly, interruptibly) for the next access unit; configure on the first one
                    // that carries SPS + PPS. Drop everything before that (no reference to decode).
                    val au = frameQueue.pollFirst(50, TimeUnit.MILLISECONDS) ?: continue
                    val nals = findNalUnits(au)
                    val sps = nals.firstOrNull { it.type == NAL_TYPE_SPS }
                    val pps = nals.firstOrNull { it.type == NAL_TYPE_PPS }
                    if (sps == null || pps == null) {
                        continue // not a keyframe AU — keep waiting (host emits an IDR every 60 frames)
                    }
                    val csd0 = au.copyOfRange(sps.start, sps.end)
                    val csd1 = au.copyOfRange(pps.start, pps.end)
                    val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
                        // A 1440p+ IDR can exceed the default input buffer; size it explicitly.
                        setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, maxOf(width * height * 3 / 2, 4 * 1024 * 1024))
                        setByteBuffer("csd-0", ByteBuffer.wrap(csd0))
                        setByteBuffer("csd-1", ByteBuffer.wrap(csd1))
                    }
                    codec.configure(format, surface, null, 0)
                    codec.start()
                    configured = true
                    Log.i(TAG, "MediaCodec H.264 decoder configured+started (sync) from SPS/PPS (hint ${width}x$height, csd0=${csd0.size}B, csd1=${csd1.size}B)")
                    feedInput(codec, au)
                    continue
                }

                // Feed at most one input per iteration (block briefly so we don't spin when idle).
                val au = frameQueue.pollFirst(2, TimeUnit.MILLISECONDS)
                if (au != null) {
                    feedInput(codec, au)
                }

                // Drain all currently-available decoded output to the surface.
                var outIndex = codec.dequeueOutputBuffer(info, 0)
                while (outIndex >= 0) {
                    val render = (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG) == 0
                    codec.releaseOutputBuffer(outIndex, render)
                    if (render) {
                        if (renderedFrames == 0) {
                            Log.i(TAG, "First decoded frame rendered to surface — decode pipeline is live.")
                        }
                        renderedFrames++
                    }
                    outIndex = codec.dequeueOutputBuffer(info, 0)
                }
                // outIndex < 0 is INFO_TRY_AGAIN_LATER / INFO_OUTPUT_FORMAT_CHANGED / buffers-changed —
                // nothing to do (rendering goes straight to the Surface).
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

    /** Dequeues an input buffer (short wait) and queues [au] into it. Runs on the decode thread. */
    private fun feedInput(codec: MediaCodec, au: ByteArray) {
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
            inputBuffer.put(au)
            val flags = if (containsNalType(au, NAL_TYPE_IDR)) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0
            codec.queueInputBuffer(inIndex, 0, au.size, System.nanoTime() / 1000, flags)
        } catch (e: RuntimeException) {
            // IllegalStateException (codec released) or BufferOverflowException (AU > input buffer,
            // guarded by KEY_MAX_INPUT_SIZE but stay defensive) — drop and recover.
            Log.w(TAG, "queueInputBuffer failed: ${e.message}")
        }
    }

    /** Releases native resources and stops the decode thread. */
    fun release() {
        running = false
        decodeThread.interrupt()
        // The decode loop's finally block stops + releases the codec on its own thread.
    }

    // --- Annex B NAL parsing -------------------------------------------------------------------

    /** A NAL unit located within an access unit. [start] is the index of its start-code first byte. */
    private class NalUnit(val start: Int, val end: Int, val type: Int)

    /**
     * Splits an Annex B buffer into NAL units on 3- or 4-byte start codes (00 00 01 / 00 00 00 01).
     * Each unit's [NalUnit.start] is the index of the start code so a slice [start, end) is a
     * self-contained, start-code-prefixed NAL suitable for csd-0/csd-1.
     */
    private fun findNalUnits(b: ByteArray): List<NalUnit> {
        val starts = ArrayList<Int>(8)
        val headers = ArrayList<Int>(8)
        val n = b.size
        var i = 0
        while (i + 2 < n) {
            if ((b[i].toInt() and 0xFF) == 0 &&
                (b[i + 1].toInt() and 0xFF) == 0 &&
                (b[i + 2].toInt() and 0xFF) == 1
            ) {
                // Start code core is 00 00 01 at i. Include a preceding 0x00 (4-byte code) in the unit.
                val scStart = if (i > 0 && (b[i - 1].toInt() and 0xFF) == 0) i - 1 else i
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

    /** True if [b] contains at least one NAL unit of [type]. */
    private fun containsNalType(b: ByteArray, type: Int): Boolean {
        val n = b.size
        var i = 0
        while (i + 3 < n) {
            if ((b[i].toInt() and 0xFF) == 0 &&
                (b[i + 1].toInt() and 0xFF) == 0 &&
                (b[i + 2].toInt() and 0xFF) == 1
            ) {
                if ((b[i + 3].toInt() and 0x1F) == type) return true
                i += 3
            } else {
                i++
            }
        }
        return false
    }
}

package com.clindsay94.remex.ui.screens

import android.media.MediaCodec
import android.media.MediaFormat
import android.util.Log
import android.view.Surface
import java.util.ArrayDeque

private const val TAG = "H264StreamDecoder"

// Bounded backlog of encoded access units waiting for a free MediaCodec input buffer. A real-time
// stream only needs a couple of frames buffered; if the hardware decoder briefly stalls we keep the
// newest few and drop the oldest, then ask the host for a keyframe so the next decodable point
// recovers cleanly. NEVER silently drop without signalling recovery. (RemEx-bqc)
private const val MAX_INPUT_BACKLOG = 4

/**
 * Low-latency, hardware-accelerated H.264 Annex B video stream decoder.
 *
 * Uses MediaCodec in ASYNCHRONOUS mode ([MediaCodec.setCallback]): the codec hands us input-buffer
 * indices and decoded output via callbacks instead of us polling [MediaCodec.dequeueInputBuffer].
 * Encoded frames arrive faster than buffers free up under load, so we hold them in a small bounded
 * queue and feed them as input buffers become available — this removes the old "dequeueInputBuffer
 * returned -1, drop the frame" path that caused ~1s of green corruption on a transient backlog.
 * (RemEx-bqc)
 *
 * On a decoder error or desync the [onKeyframeNeeded] callback fires so the owner can ask the host
 * for an on-demand IDR; [INFO_OUTPUT_FORMAT_CHANGED] and SPS/PPS-carrying IDRs are handled so the
 * codec can reconfigure its output mid-stream. (RemEx-kx4)
 */
class H264StreamDecoder(
    private val width: Int,
    private val height: Int,
    private val surface: Surface,
    /**
     * Invoked once if the hardware decoder fails to initialize (createDecoderByType / configure /
     * start threw) or hits an unrecoverable async error. Without this, an init failure would leave a
     * published-but-dead decoder whose decodeFrame() no-ops forever — a silently black stream with no
     * recovery. The owner should surface an error and/or trigger a reconnect. (RemEx-x0b)
     */
    private val onInitFailure: (() -> Unit)? = null,
    /**
     * Invoked when the decoder drops input (bounded backlog overflow) or recovers from a transient
     * codec error and needs a fresh decodable point. The owner sends a keyframe-request to the host,
     * which emits an on-demand IDR so the stream resyncs instead of staying corrupt for a full GOP.
     * (RemEx-bqc)
     */
    private val onKeyframeNeeded: (() -> Unit)? = null
) {
    private var decoder: MediaCodec? = null
    private var isConfigured = false

    // MediaCodec is NOT thread-safe. In async mode the codec's callbacks run on its own handler
    // thread while decodeFrame() is driven from a background coroutine (Dispatchers.Default) and
    // release() from the main thread on surface teardown. Serialize every codec touch AND the shared
    // input-buffer state through this lock so the codec can never be stopped/released underneath an
    // in-flight queueInputBuffer (which aborts natively, SIGABRT).
    private val codecLock = Any()

    // Encoded access units waiting for a free input buffer (oldest at head). Guarded by codecLock.
    private val pendingFrames = ArrayDeque<ByteArray>(MAX_INPUT_BACKLOG)

    // Input-buffer indices the codec has handed us that we haven't filled yet (async mode). Guarded
    // by codecLock. We pair a free index with a pending frame; whichever arrives second triggers a
    // queueInputBuffer.
    private val freeInputBuffers = ArrayDeque<Int>()

    init {
        initializeDecoder()
    }

    private fun initializeDecoder() {
        try {
            val codec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            decoder = codec
            val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
                setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
                setInteger(MediaFormat.KEY_COLOR_FORMAT, 0x7F000789) // COLOR_FormatSurface
                // Real-time priority and operating rate let the hardware decoder pre-allocate
                // resources and schedule at lower latency (hints, not guarantees).
                setInteger(MediaFormat.KEY_PRIORITY, 0) // 0 = real-time
                setFloat(MediaFormat.KEY_OPERATING_RATE, 60f)
            }

            // ASYNC MODE: setCallback() MUST be called before configure(). After this, we never call
            // dequeueInputBuffer/dequeueOutputBuffer — buffers come through the callbacks below.
            codec.setCallback(callback)
            codec.configure(format, surface, null, 0)
            codec.start()
            isConfigured = true
            Log.i(TAG, "MediaCodec H.264 decoder successfully started ($width x $height, async)")
        } catch (e: Exception) {
            Log.e(TAG, "Failed to initialize native MediaCodec decoder: ${e.message}", e)
            release()
            // Signal the owner so the dead stream surfaces an error / reconnects instead of hanging
            // black forever. (RemEx-x0b)
            onInitFailure?.invoke()
        }
    }

    private val callback = object : MediaCodec.Callback() {
        override fun onInputBufferAvailable(codec: MediaCodec, index: Int) {
            synchronized(codecLock) {
                if (!isConfigured || decoder !== codec) {
                    return
                }
                // If a frame is already waiting, fill this buffer immediately; otherwise remember the
                // free index for the next decodeFrame() call.
                val frame = pendingFrames.pollFirst()
                if (frame != null) {
                    submit(codec, index, frame)
                } else {
                    freeInputBuffers.addLast(index)
                }
            }
        }

        override fun onOutputBufferAvailable(
            codec: MediaCodec,
            index: Int,
            info: MediaCodec.BufferInfo
        ) {
            synchronized(codecLock) {
                if (!isConfigured || decoder !== codec) {
                    return
                }
                try {
                    // render=true draws the decoded frame directly on the Surface.
                    codec.releaseOutputBuffer(index, true)
                } catch (e: IllegalStateException) {
                    Log.w(TAG, "releaseOutputBuffer failed: ${e.message}")
                }
            }
        }

        override fun onOutputFormatChanged(codec: MediaCodec, format: MediaFormat) {
            // The decoder negotiated a new output format mid-stream — this fires when the host's
            // resolution changes and the new SPS/PPS (carried on the next IDR) is parsed, so the codec
            // reconfigures its internal surface buffers automatically. We just log it; rendering keeps
            // working because the output goes straight to the Surface. (RemEx-kx4)
            Log.i(TAG, "MediaCodec output format changed: $format")
        }

        override fun onError(codec: MediaCodec, e: MediaCodec.CodecException) {
            Log.e(TAG, "MediaCodec error (recoverable=${e.isRecoverable}, transient=${e.isTransient}): ${e.message}")
            if (e.isTransient) {
                // Transient (e.g. resource pressure) — the codec will recover on its own; just ask the
                // host for a keyframe so the next frame is a clean decodable point.
                onKeyframeNeeded?.invoke()
                return
            }
            // Non-transient: surface the failure so the owner reconnects/rebuilds the decoder instead
            // of rendering black. release() is idempotent and safe to call from the callback thread.
            synchronized(codecLock) {
                isConfigured = false
            }
            onInitFailure?.invoke()
        }
    }

    /**
     * Feeds an H.264 Annex B NAL unit / access unit into the hardware decoder. Non-blocking: the
     * frame is queued and submitted as soon as the codec offers a free input buffer. If the bounded
     * backlog is full (decoder stalled), the OLDEST queued frame is evicted and a keyframe is
     * requested so recovery is explicit — frames are never silently dropped without resync. (RemEx-bqc)
     */
    fun decodeFrame(bytes: ByteArray) = synchronized(codecLock) {
        val codec = this.decoder ?: return@synchronized
        if (!isConfigured) return@synchronized

        // Prefer to submit straight into a free input buffer if one is waiting.
        val freeIndex = freeInputBuffers.pollFirst()
        if (freeIndex != null) {
            submit(codec, freeIndex, bytes)
            return@synchronized
        }

        // No free buffer right now — queue it. Bound the backlog: if full, drop the oldest and ask the
        // host for a keyframe so we resync from the next IDR rather than decoding a torn GOP.
        if (pendingFrames.size >= MAX_INPUT_BACKLOG) {
            pendingFrames.pollFirst()
            Log.w(TAG, "Input backlog full ($MAX_INPUT_BACKLOG); dropped oldest frame, requesting keyframe.")
            onKeyframeNeeded?.invoke()
        }
        pendingFrames.addLast(bytes)
    }

    /** Caller must hold [codecLock]. Copies [bytes] into the codec input buffer and queues it. */
    private fun submit(codec: MediaCodec, index: Int, bytes: ByteArray) {
        try {
            val inputBuffer = codec.getInputBuffer(index) ?: return
            inputBuffer.clear()
            inputBuffer.put(bytes)
            val presentationTimeUs = System.nanoTime() / 1000
            codec.queueInputBuffer(index, 0, bytes.size, presentationTimeUs, 0)
        } catch (e: IllegalStateException) {
            // Codec stopped/released between the callback and here — drop and let recovery proceed.
            Log.w(TAG, "queueInputBuffer failed: ${e.message}")
        }
    }

    /**
     * Releases native resources.
     */
    fun release() = synchronized(codecLock) {
        isConfigured = false
        pendingFrames.clear()
        freeInputBuffers.clear()
        try {
            decoder?.stop()
        } catch (e: Exception) { /* best effort */ }

        try {
            decoder?.release()
        } catch (e: Exception) { /* best effort */ }

        decoder = null
        Log.i(TAG, "MediaCodec H.264 decoder released.")
    }
}

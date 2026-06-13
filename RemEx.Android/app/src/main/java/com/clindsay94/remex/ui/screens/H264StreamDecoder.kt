package com.clindsay94.remex.ui.screens

import android.media.MediaCodec
import android.media.MediaFormat
import android.util.Log
import android.view.Surface
import java.nio.ByteBuffer

private const val TAG = "H264StreamDecoder"

/**
 * Low-latency, hardware-accelerated H.264 Annex B video stream decoder.
 * Feeds NAL units directly into Android's MediaCodec and renders to a Surface.
 */
class H264StreamDecoder(
    private val width: Int,
    private val height: Int,
    private val surface: Surface
) {
    private var decoder: MediaCodec? = null
    private var isConfigured = false
    private val bufferInfo = MediaCodec.BufferInfo()

    init {
        initializeDecoder()
    }

    private fun initializeDecoder() {
        try {
            decoder = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
                // Low-latency configuration hints
                setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
                // Set native output surface
                setInteger(MediaFormat.KEY_COLOR_FORMAT, 0x7F000789) // COLOR_FormatSurface
            }

            decoder?.configure(format, surface, null, 0)
            decoder?.start()
            isConfigured = true
            Log.i(TAG, "MediaCodec H.264 decoder successfully started ($width x $height)")
        } catch (e: Exception) {
            Log.e(TAG, "Failed to initialize native MediaCodec decoder: ${e.message}", e)
            release()
        }
    }

    /**
     * Feeds an H.264 Annex B NAL unit or frame packet into the hardware decoder.
     */
    fun decodeFrame(bytes: ByteArray) {
        val decoder = this.decoder ?: return
        if (!isConfigured) return

        try {
            val inputBufferIndex = decoder.dequeueInputBuffer(10000) // 10ms timeout
            if (inputBufferIndex >= 0) {
                val inputBuffer = decoder.getInputBuffer(inputBufferIndex)
                if (inputBuffer != null) {
                    inputBuffer.clear()
                    inputBuffer.put(bytes)
                    
                    // Queue input buffer with current presentation time
                    val presentationTimeUs = System.nanoTime() / 1000
                    decoder.queueInputBuffer(
                        inputBufferIndex,
                        0,
                        bytes.size,
                        presentationTimeUs,
                        0
                    )
                }
            }

            // Drain decoded frames immediately to achieve zero latency
            drainOutputBuffers()
        } catch (e: Exception) {
            Log.e(TAG, "Error decoding H.264 packet: ${e.message}")
        }
    }

    private fun drainOutputBuffers() {
        val decoder = this.decoder ?: return
        try {
            var outputBufferIndex = decoder.dequeueOutputBuffer(bufferInfo, 0)
            while (outputBufferIndex >= 0) {
                // Release output buffer with render=true to draw directly on Surface
                decoder.releaseOutputBuffer(outputBufferIndex, true)
                outputBufferIndex = decoder.dequeueOutputBuffer(bufferInfo, 0)
            }
        } catch (e: Exception) {
            Log.w(TAG, "Error draining MediaCodec output: ${e.message}")
        }
    }

    /**
     * Releases native resources.
     */
    fun release() {
        isConfigured = false
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

package com.clindsay94.remex.ui.screens

/**
 * Client-side reader for the host's binary desktop-frame envelope.
 *
 * Wire format mirrors `Remex.Core.Models.DesktopFrameEnvelope` (C#), little-endian:
 *
 * ```
 * offset  size  field
 *   0      4    magic  = "RDXF"
 *   4      1    version = 1
 *   5      1    codec   (0 = Mjpeg, 1 = H264)  → DesktopCodecKind ordinal
 *   6      1    flags   (bit 0 = key frame)
 *   7      1    reserved
 *   8      8    streamSerial (Int64 LE)
 *  16      8    sequence     (Int64 LE)
 *  24      4    payloadLength (Int32 LE)
 *  28      …    payload
 * ```
 *
 * The host only emits this when the client advertises `supportsFrameEnvelope = true`. Routing each
 * frame by its OWN codec tag (rather than a separately-updated "active codec" flag) eliminates the
 * mid-switch misroute where a JPEG could be fed to the H.264 decoder, or an H.264 NAL to the JPEG
 * decoder, during the window before the codec-change metadata is applied. (RemEx-w5v)
 */
object RemoteDesktopFrameEnvelope {
    private const val HEADER_SIZE = 28
    private const val VERSION: Byte = 1

    /** Codec strings match [RemoteDesktopViewModel]'s `_activeCodecState` convention. */
    const val CODEC_MJPEG = "Mjpeg"
    const val CODEC_H264 = "H264"

    /**
     * A parsed envelope. The payload is described as a RANGE INTO [bytes] rather than handed over as
     * its own array: stripping the 28-byte header with `copyOfRange` duplicated every frame in full a
     * second time, on top of the unavoidable native->Java copy — roughly 12 MB/s of extra garbage at
     * 60 fps and 200 KB frames (RemEx-t8ku). Both consumers take (array, offset, length):
     * `BitmapFactory.decodeByteArray` and `MediaCodec`'s `inputBuffer.put`.
     *
     * Safe to hold without copying because the JNI layer allocates a fresh array per frame and never
     * reuses it — see `RemexClientManager.onFrameReceived`. If that ever changes, the H.264 path is
     * where it will bite, because that one QUEUES the range for a different thread to consume later.
     */
    data class Frame(
            val codec: String,
            val streamSerial: Long,
            val sequence: Long,
            val isKeyFrame: Boolean,
            val bytes: ByteArray,
            val payloadOffset: Int,
            val payloadLength: Int,
    )

    /**
     * Parses [bytes] as an envelope, or returns null if it is not one (legacy host that sends raw,
     * untagged frames — the caller falls back to routing by the negotiated codec).
     */
    fun tryRead(bytes: ByteArray): Frame? {
        if (bytes.size < HEADER_SIZE) return null
        if (bytes[0] != 'R'.code.toByte() ||
                        bytes[1] != 'D'.code.toByte() ||
                        bytes[2] != 'X'.code.toByte() ||
                        bytes[3] != 'F'.code.toByte()
        ) {
            return null
        }
        if (bytes[4] != VERSION) return null

        val payloadLength = readInt32Le(bytes, 24)
        if (payloadLength < 0 || bytes.size != HEADER_SIZE + payloadLength) return null

        val codec =
                when (bytes[5].toInt()) {
                    1 -> CODEC_H264
                    else -> CODEC_MJPEG
                }
        val flags = bytes[6].toInt()

        return Frame(
                codec = codec,
                streamSerial = readInt64Le(bytes, 8),
                sequence = readInt64Le(bytes, 16),
                isKeyFrame = (flags and 0x01) != 0,
                bytes = bytes,
                payloadOffset = HEADER_SIZE,
                payloadLength = payloadLength,
        )
    }

    private fun readInt32Le(b: ByteArray, off: Int): Int =
            (b[off].toInt() and 0xFF) or
                    ((b[off + 1].toInt() and 0xFF) shl 8) or
                    ((b[off + 2].toInt() and 0xFF) shl 16) or
                    ((b[off + 3].toInt() and 0xFF) shl 24)

    private fun readInt64Le(b: ByteArray, off: Int): Long {
        var v = 0L
        for (i in 7 downTo 0) {
            v = (v shl 8) or (b[off + i].toLong() and 0xFF)
        }
        return v
    }
}

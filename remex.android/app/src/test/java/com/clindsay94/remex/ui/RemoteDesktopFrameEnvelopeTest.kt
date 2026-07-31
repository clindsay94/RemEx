package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.RemoteDesktopFrameEnvelope
import java.nio.ByteBuffer
import java.nio.ByteOrder
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Covers the desktop frame envelope reader, and in particular that it hands back a RANGE rather than
 * a copy.
 *
 * Stripping the 28-byte header with `copyOfRange` duplicated every frame in full a second time, on
 * top of the unavoidable native->Java copy — roughly 12 MB/s of extra garbage at 60 fps and 200 KB
 * frames (RemEx-t8ku). The parse itself is easy to get right and easy to regress silently: an
 * off-by-one in the offset produces a corrupt frame, not an exception.
 */
class RemoteDesktopFrameEnvelopeTest {

    private fun envelope(
            codec: Int = 1,
            flags: Int = 1,
            version: Byte = 1,
            magic: String = "RDXF",
            streamSerial: Long = 7,
            sequence: Long = 42,
            payload: ByteArray = byteArrayOf(9, 8, 7, 6, 5),
            declaredLength: Int? = null,
    ): ByteArray {
        val buffer = ByteBuffer.allocate(28 + payload.size).order(ByteOrder.LITTLE_ENDIAN)
        buffer.put(magic.toByteArray(Charsets.US_ASCII))
        buffer.put(version)
        buffer.put(codec.toByte())
        buffer.put(flags.toByte())
        buffer.put(0)
        buffer.putLong(streamSerial)
        buffer.putLong(sequence)
        buffer.putInt(declaredLength ?: payload.size)
        buffer.put(payload)
        return buffer.array()
    }

    @Test
    fun `reads header fields`() {
        val frame = RemoteDesktopFrameEnvelope.tryRead(envelope())!!

        assertEquals(RemoteDesktopFrameEnvelope.CODEC_H264, frame.codec)
        assertEquals(7L, frame.streamSerial)
        assertEquals(42L, frame.sequence)
        assertTrue(frame.isKeyFrame)
    }

    @Test
    fun `payload is a range into the original array, not a copy`() {
        // THE POINT OF RemEx-t8ku. If this ever goes back to copyOfRange the assertion below fails
        // and the 12 MB/s comes back silently — nothing else would notice.
        val bytes = envelope()

        val frame = RemoteDesktopFrameEnvelope.tryRead(bytes)!!

        assertSame(bytes, frame.bytes)
        assertEquals(28, frame.payloadOffset)
        assertEquals(5, frame.payloadLength)
    }

    @Test
    fun `range points at the payload bytes`() {
        val payload = byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8)
        val frame = RemoteDesktopFrameEnvelope.tryRead(envelope(payload = payload))!!

        val sliced =
                frame.bytes.copyOfRange(
                        frame.payloadOffset,
                        frame.payloadOffset + frame.payloadLength
                )

        assertArrayEquals(payload, sliced)
    }

    @Test
    fun `non-key frame clears the flag`() {
        assertTrue(!RemoteDesktopFrameEnvelope.tryRead(envelope(flags = 0))!!.isKeyFrame)
    }

    @Test
    fun `codec 0 is mjpeg`() {
        assertEquals(
                RemoteDesktopFrameEnvelope.CODEC_MJPEG,
                RemoteDesktopFrameEnvelope.tryRead(envelope(codec = 0))!!.codec
        )
    }

    @Test
    fun `rejects a legacy untagged frame`() {
        // Legacy hosts send raw frames; the caller falls back to the negotiated codec, so this must
        // return null rather than misparse arbitrary pixel data as a header.
        assertNull(RemoteDesktopFrameEnvelope.tryRead(ByteArray(500) { it.toByte() }))
    }

    @Test
    fun `rejects a short buffer`() {
        assertNull(RemoteDesktopFrameEnvelope.tryRead(ByteArray(10)))
    }

    @Test
    fun `rejects an unknown version`() {
        assertNull(RemoteDesktopFrameEnvelope.tryRead(envelope(version = 2)))
    }

    @Test
    fun `rejects a declared length that does not match the buffer`() {
        // A truncated or over-long frame must not yield a range that runs off the end of the array —
        // that would be an IndexOutOfBounds inside MediaCodec or BitmapFactory, far from here.
        assertNull(RemoteDesktopFrameEnvelope.tryRead(envelope(declaredLength = 99)))
        assertNull(RemoteDesktopFrameEnvelope.tryRead(envelope(declaredLength = -1)))
    }

    @Test
    fun `accepts an empty payload`() {
        val frame = RemoteDesktopFrameEnvelope.tryRead(envelope(payload = ByteArray(0)))!!

        assertEquals(28, frame.payloadOffset)
        assertEquals(0, frame.payloadLength)
    }
}

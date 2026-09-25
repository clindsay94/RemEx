package com.clindsay94.remex.service

import okio.ByteString.Companion.toByteString
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Round-trips [FileFrameCodec] and verifies the on-wire layout matches the PC host's
 * `Remex.Core.Models.FileFrameCodec` byte-for-byte: `[int32-LE header length][UTF-8 JSON][payload]`,
 * with JSON keys `kind`/`transferId`/`offset`/`length`/`final`/`committedOffset`/`error`.
 */
class FileFrameCodecTest {

    @Test
    fun dataFrame_roundTrips() {
        val payload = ByteArray(256) { (it % 251).toByte() }
        val env =
            FileFrameEnvelope(
                kind = FileFrameKinds.DATA,
                transferId = "abc-123",
                offset = 65536L,
                length = payload.size,
                final = true,
            )
        val frame = FileFrameCodec.wrap(env, payload)
        val decoded = FileFrameCodec.tryRead(frame)
        assertNotNull(decoded)
        assertEquals(FileFrameKinds.DATA, decoded!!.envelope.kind)
        assertEquals("abc-123", decoded.envelope.transferId)
        assertEquals(65536L, decoded.envelope.offset)
        assertEquals(payload.size, decoded.envelope.length)
        assertTrue(decoded.envelope.final)
        assertNull(decoded.envelope.committedOffset)
        assertArrayEquals(payload, decoded.payload)
    }

    @Test
    fun ackFrame_carriesCommittedOffset_andEmptyPayload() {
        val env =
            FileFrameEnvelope(
                kind = FileFrameKinds.ACK,
                transferId = "t1",
                committedOffset = 4L * 1024 * 1024,
            )
        val frame = FileFrameCodec.wrap(env, ByteArray(0))
        val decoded = FileFrameCodec.tryRead(frame)
        assertNotNull(decoded)
        assertEquals(FileFrameKinds.ACK, decoded!!.envelope.kind)
        assertEquals(4L * 1024 * 1024, decoded.envelope.committedOffset)
        assertEquals(0, decoded.payload.size)
    }

    @Test
    fun errorFrame_roundTrips() {
        val env = FileFrameEnvelope(FileFrameKinds.ERROR, "t2", error = "boom")
        val decoded = FileFrameCodec.tryRead(FileFrameCodec.wrap(env, ByteArray(0)))
        assertNotNull(decoded)
        assertEquals("boom", decoded!!.envelope.error)
    }

    @Test
    fun headerLengthPrefix_isLittleEndianInt32() {
        val env = FileFrameEnvelope(FileFrameKinds.DATA, "x", offset = 0, length = 3)
        val payload = byteArrayOf(1, 2, 3)
        val frame = FileFrameCodec.wrap(env, payload)
        val headerLen =
            (frame[0].toInt() and 0xFF) or
                ((frame[1].toInt() and 0xFF) shl 8) or
                ((frame[2].toInt() and 0xFF) shl 16) or
                ((frame[3].toInt() and 0xFF) shl 24)
        // The header is JSON so its length varies, but the payload must be the last `count` bytes.
        assertEquals(frame.size, 4 + headerLen + payload.size)
        val tail = frame.copyOfRange(frame.size - 3, frame.size)
        assertArrayEquals(payload, tail)
    }

    @Test
    fun noCopyOverload_matchesFullBufferWrap() {
        val big = ByteArray(1000) { it.toByte() }
        val env = FileFrameEnvelope(FileFrameKinds.DATA, "id", offset = 10, length = 500, final = false)
        val a = FileFrameCodec.wrap(env, big.copyOfRange(0, 500))
        val b = FileFrameCodec.wrap(env, big, 0, 500)
        assertArrayEquals(a, b)
    }

    // ── P4-5: the ByteString send/receive paths must stay byte-identical to the ByteArray ones ──

    @Test
    fun wrapToByteString_isByteIdenticalToWrap_smallAndSegmented() {
        // 300 bytes stays one okio segment; 300 KiB crosses the 4 KiB segmenting threshold, so
        // readByteString hands back a SegmentedByteString - the shape the live send path produces.
        for (size in intArrayOf(0, 300, 300 * 1024)) {
            val big = ByteArray(size + 20) { (it % 253).toByte() }
            val env = FileFrameEnvelope(FileFrameKinds.DATA, "seg", offset = 7, length = size, final = true)
            val expected = FileFrameCodec.wrap(env, big, 10, size)
            val actual = FileFrameCodec.wrapToByteString(env, big, 10, size).toByteArray()
            assertArrayEquals("size=$size", expected, actual)
        }
    }

    @Test
    fun tryReadByteString_decodesTheSameAsTheByteArrayOverload() {
        for (size in intArrayOf(0, 300, 300 * 1024)) {
            val payload = ByteArray(size) { (it % 249).toByte() }
            val env = FileFrameEnvelope(FileFrameKinds.DATA, "rt", offset = 4096L, length = size, final = false)
            // Built through the ByteString wrap so a large frame arrives SEGMENTED, as OkHttp's reader
            // hands it over for any message past 4 KiB.
            val frame = FileFrameCodec.wrapToByteString(env, payload, 0, size)
            val decoded = FileFrameCodec.tryRead(frame)
            assertNotNull("size=$size", decoded)
            assertEquals(env, decoded!!.envelope)
            assertArrayEquals("size=$size", payload, decoded.payload)
            assertEquals(FileFrameCodec.tryRead(frame.toByteArray()), decoded)
        }
    }

    @Test
    fun tryReadByteString_malformedFrames_returnNull() {
        assertNull(FileFrameCodec.tryRead(ByteArray(2).toByteString()))
        assertNull(FileFrameCodec.tryRead(byteArrayOf(127, 0, 0, 0, '{'.code.toByte()).toByteString()))
        val notJson = "nope".toByteArray()
        val frame = ByteArray(4 + notJson.size)
        frame[0] = notJson.size.toByte()
        notJson.copyInto(frame, 4)
        assertNull(FileFrameCodec.tryRead(frame.toByteString()))
        // A negative header length (high bit set) must not throw.
        assertNull(FileFrameCodec.tryRead(byteArrayOf(0, 0, 0, 0x80.toByte(), 1, 2).toByteString()))
    }

    @Test
    fun malformedFrames_returnNull() {
        assertNull(FileFrameCodec.tryRead(ByteArray(2))) // truncated prefix
        // header length longer than the frame
        val bad = byteArrayOf(127, 0, 0, 0, '{'.code.toByte())
        assertNull(FileFrameCodec.tryRead(bad))
        // valid prefix, invalid JSON header
        val notJson = "nope".toByteArray()
        val frame = ByteArray(4 + notJson.size)
        frame[0] = notJson.size.toByte()
        notJson.copyInto(frame, 4)
        assertNull(FileFrameCodec.tryRead(frame))
    }
}

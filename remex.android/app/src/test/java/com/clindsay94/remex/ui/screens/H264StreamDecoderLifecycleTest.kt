package com.clindsay94.remex.ui.screens

import android.view.Surface
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.mockito.Mockito.mock

/**
 * Lifecycle of the decoder on the JVM, where the stubbed android.jar hands back a null codec: the
 * first SPS/PPS access unit makes configure fail, which is the real "hardware decoder could not be
 * created/configured" path (RemEx-x0b). Once the decode thread has died nothing drains the backlog,
 * so the drop-oldest bound (RemEx-bqc) can be observed deterministically.
 */
class H264StreamDecoderLifecycleTest {
    private val failures = AtomicInteger()
    private val failed = CountDownLatch(1)
    private val keyframes = AtomicInteger()
    private val decoders = mutableListOf<H264StreamDecoder>()

    private fun newDecoder(): H264StreamDecoder =
        H264StreamDecoder(
            width = 1280,
            height = 720,
            surface = mock(Surface::class.java),
            onInitFailure = { failures.incrementAndGet(); failed.countDown() },
            onKeyframeNeeded = { keyframes.incrementAndGet() },
        ).also { decoders += it }

    @After
    fun tearDown() {
        decoders.forEach { it.release() }
    }

    private fun H264StreamDecoder.joinDecodeThread() {
        val field = H264StreamDecoder::class.java.getDeclaredField("decodeThread").apply { isAccessible = true }
        val thread = field.get(this) as Thread
        thread.join(2000)
        assertFalse("decode thread should have exited", thread.isAlive)
    }

    private fun idr(): ByteArray =
        bytes(0, 0, 0, 1, 0x67, 0x42, 0x00, 0x1F, 0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80, 0, 0, 0, 1, 0x65, 0x88, 0x84)

    private fun pFrame(): ByteArray = bytes(0, 0, 0, 1, 0x41, 0x9A, 0x00)

    private fun bytes(vararg v: Int) = ByteArray(v.size) { v[it].toByte() }

    private fun H264StreamDecoder.failAndWait() {
        val au = idr()
        decodeFrame(au, 0, au.size)
        assertTrue("onInitFailure should fire when the codec cannot be configured", failed.await(2, TimeUnit.SECONDS))
        joinDecodeThread()
    }

    @Test
    fun `a decoder that cannot be configured reports init failure exactly once`() {
        val decoder = newDecoder()
        decoder.failAndWait()
        decoder.release()
        assertEquals(1, failures.get())
    }

    @Test
    fun `the backlog holds six frames and the seventh drops the oldest and asks for a keyframe`() {
        val decoder = newDecoder()
        decoder.failAndWait()
        val p = pFrame()
        repeat(6) { decoder.decodeFrame(p, 0, p.size) }
        assertEquals("six queued frames fit the backlog", 0, keyframes.get())
        decoder.decodeFrame(p, 0, p.size)
        assertEquals(1, keyframes.get())
        decoder.decodeFrame(p, 0, p.size)
        assertEquals("each further overflow drops one more and asks again", 2, keyframes.get())
    }

    @Test
    fun `empty access units are ignored and never count toward the backlog`() {
        val decoder = newDecoder()
        decoder.failAndWait()
        val p = pFrame()
        repeat(20) { decoder.decodeFrame(p, 0, 0) }
        repeat(6) { decoder.decodeFrame(p, 0, p.size) }
        assertEquals(0, keyframes.get())
    }

    @Test
    fun `release twice is harmless, raises no failure, and later frames are ignored`() {
        val decoder = newDecoder()
        decoder.release()
        decoder.release()
        decoder.joinDecodeThread()
        assertEquals("a requested shutdown is not an init failure", 0, failures.get())
        val p = pFrame()
        repeat(10) { decoder.decodeFrame(p, 0, p.size) }
        assertEquals("a released decoder queues nothing", 0, keyframes.get())
    }
}

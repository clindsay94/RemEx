package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.MouseMoveThrottle
import kotlin.math.abs
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Covers the Remote Mouse trackpad's delta accumulation and send throttle (RemEx-3uhp).
 *
 * The trackpad used to call `onMouseMove(dragAmount.x.toInt(), …)` on every pointer frame. That is
 * two bugs at once: each frame's delta was truncated INDEPENDENTLY, so a drag slower than a pixel per
 * frame moved the pointer nowhere at all; and every frame did two JSONObject builds, a toString, a
 * JNI marshal and a native deserialize on the main thread, up to 120 times a second.
 *
 * The interesting constraint is that the throttle gates SENDING and not ACCUMULATING. Relative deltas
 * are not like the stylus hover positions the Remote Desktop screen throttles by dropping the
 * callback — drop a delta and that movement is gone permanently, and the pointer ends up somewhere
 * the user's finger never asked for. So these tests care mostly about one property: whatever goes in
 * comes out, eventually and in whole pixels.
 */
class MouseMoveThrottleTest {

    private companion object {
        const val THROTTLE = 33L
    }

    @Test
    fun `the first movement is sent immediately`() {
        // No warm-up delay: a throttle that made the user wait 33ms for the pointer to react at the
        // start of every gesture would be felt as lag on exactly the movement they notice most.
        val throttle = MouseMoveThrottle(THROTTLE)

        val move = throttle.onDelta(5f, 3f, nowMs = 1000L)

        assertEquals(5, move?.x)
        assertEquals(3, move?.y)
    }

    @Test
    fun `sub-pixel drags eventually move the pointer`() {
        // THE PRECISION BUG. Six frames of 0.2px each is 1.2px of real movement. Truncating per
        // frame — the old behaviour — yields six zeroes and a pointer that does not budge no matter
        // how long the user drags.
        val throttle = MouseMoveThrottle(THROTTLE)
        var totalX = 0

        var now = 1000L
        repeat(6) {
            throttle.onDelta(0.2f, 0f, now)?.let { totalX += it.x }
            now += THROTTLE // give the throttle every chance to send
        }

        assertEquals("0.2px x6 must deliver a whole pixel, not nothing", 1, totalX)
    }

    @Test
    fun `movement inside the interval is withheld, not discarded`() {
        // The distinction the whole design turns on. Three frames arrive too quickly to send, and
        // the fourth — once the interval has passed — must carry ALL of it, not just its own delta.
        val throttle = MouseMoveThrottle(THROTTLE)
        throttle.onDelta(10f, 0f, 1000L) // sent immediately

        assertNull(throttle.onDelta(4f, 0f, 1005L))
        assertNull(throttle.onDelta(4f, 0f, 1010L))
        assertNull(throttle.onDelta(4f, 0f, 1015L))

        val move = throttle.onDelta(0f, 0f, 1000L + THROTTLE)
        assertEquals("all three withheld deltas must arrive together", 12, move?.x)
    }

    @Test
    fun `a long drag delivers every pixel it was given`() {
        // The property that matters, over a realistic gesture: 240 frames of an awkward fractional
        // delta at 120Hz. Anything that drops or double-counts a frame shows up here as drift.
        val throttle = MouseMoveThrottle(THROTTLE)
        val perFrame = 1.7f
        var sent = 0
        var now = 0L

        repeat(240) {
            throttle.onDelta(perFrame, 0f, now)?.let { sent += it.x }
            now += 8L // ~120Hz
        }
        throttle.flush(now)?.let { sent += it.x }

        val expected = perFrame * 240
        assertTrue(
                "sent $sent px for ${expected}px of movement — drift beyond the sub-pixel remainder",
                abs(expected - sent) < 1f
        )
    }

    @Test
    fun `a two-second drag sends about sixty messages, not two hundred and forty`() {
        // The perf premise stated as a contract rather than left implied. Every one of these sends
        // is two JSONObjects, a toString, a JNI marshal and a native deserialize; the bug was doing
        // that 120 times a second. Asserting the COUNT is what would catch someone "simplifying" the
        // throttle away while every property test above still passed.
        //
        // The expected number is 48, not the 60 that 2000/33 suggests, and the difference is worth
        // knowing: a send can only happen ON a frame, so a 33ms threshold actually fires every 40ms
        // — the next 8ms frame at or past it. The real rate is always the frame interval rounded up,
        // never the nominal one. Anyone retuning THROTTLE should expect the same quantisation.
        val throttle = MouseMoveThrottle(THROTTLE)
        var sends = 0
        var now = 0L

        repeat(240) { // 2 seconds at 120Hz
            throttle.onDelta(2f, 2f, now)?.let { sends++ }
            now += 8L
        }

        assertEquals("240 frames in, one send per 40ms out", 48, sends)
        assertTrue("must be a large reduction on the 240 unthrottled sends", sends < 240 / 4)
    }

    @Test
    fun `a new gesture starting soon after the last one is not delayed`() {
        // flush() clears the deadline, so back-to-back flicks each respond on their first frame.
        // Otherwise the second flick's opening movement is withheld for up to a full interval, which
        // is exactly the start-of-gesture lag the immediate first send exists to prevent.
        val throttle = MouseMoveThrottle(THROTTLE)
        throttle.onDelta(20f, 0f, 1000L)
        throttle.flush(1005L) // first flick ends

        val firstFrameOfNextFlick = throttle.onDelta(6f, 0f, 1010L)

        assertEquals("second flick must move immediately", 6, firstFrameOfNextFlick?.x)
    }

    @Test
    fun `negative movement accumulates the same way`() {
        // Truncation toward zero is symmetric only because the emitted whole part is SUBTRACTED
        // rather than the accumulator being cleared. Dragging left must not behave differently from
        // dragging right.
        val throttle = MouseMoveThrottle(THROTTLE)
        var total = 0
        var now = 0L

        repeat(10) {
            throttle.onDelta(-0.5f, 0f, now)?.let { total += it.x }
            now += THROTTLE
        }
        throttle.flush(now)?.let { total += it.x }

        assertEquals(-5, total)
    }

    @Test
    fun `flush delivers the tail of a gesture regardless of timing`() {
        // Without this the last fraction of every drag — up to a full interval of movement — sits in
        // the accumulator until the user drags again, so every gesture stops slightly short.
        val throttle = MouseMoveThrottle(THROTTLE)
        throttle.onDelta(9f, 9f, 1000L)
        assertNull(throttle.onDelta(7f, 7f, 1002L))

        val tail = throttle.flush(1003L)

        assertNotNull("a drag that ends mid-interval must still deliver its movement", tail)
        assertEquals(7, tail?.x)
        assertEquals(7, tail?.y)
    }

    @Test
    fun `flush with nothing pending sends nothing`() {
        // Drag end fires on every gesture including taps. Emitting a zero move each time would put
        // the per-frame JSON and JNI cost straight back, one message per tap.
        val throttle = MouseMoveThrottle(THROTTLE)
        throttle.onDelta(3f, 3f, 1000L)

        assertNull(throttle.flush(1001L))
    }

    @Test
    fun `a stalled sub-pixel drag is not pushed further away each frame`() {
        // The clock is stamped only when something is actually sent. Stamping it on a no-op would
        // mean a very slow drag reset its own deadline every frame and never reached the threshold —
        // reintroducing the stuck pointer through the back door.
        val throttle = MouseMoveThrottle(THROTTLE)
        throttle.onDelta(10f, 0f, 1000L) // stamps the clock

        // Frames too small to produce a pixel, arriving continuously.
        var now = 1001L
        repeat(20) {
            assertNull(throttle.onDelta(0.04f, 0f, now))
            now += 2L
        }

        // Once they add up past a pixel, and the interval has long passed, it must send.
        val move = throttle.onDelta(0.4f, 0f, now)
        assertEquals(1, move?.x)
    }
}

package com.clindsay94.remex

import com.clindsay94.remex.ui.screens.RelativeTime
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * "Just now" instead of "0 minutes ago" (3.0 comb, zero-minutes-ago; RemEx-pp4cm.3).
 * DateUtils at minute resolution prints "0 minutes ago" for anything under a minute, which is the
 * common case right after connecting.
 */
class RelativeTimeTest {
    private val now = 1_000_000_000L

    @Test
    fun `under a minute is just now`() {
        assertTrue(RelativeTime.isJustNow(now, now))
        assertTrue(RelativeTime.isJustNow(now - 59_999, now))
    }

    @Test
    fun `a minute or more is not just now`() {
        assertFalse(RelativeTime.isJustNow(now - 60_000, now))
        assertFalse(RelativeTime.isJustNow(now - 3_600_000, now))
    }

    @Test
    fun `a stamp slightly in the future after a clock change is still just now`() {
        assertTrue(RelativeTime.isJustNow(now + 5_000, now))
    }

    @Test
    fun `the ticker wakes on the next minute boundary`() {
        assertEquals(60_000L, RelativeTime.millisUntilNextMinute(120_000))
        assertEquals(1L, RelativeTime.millisUntilNextMinute(119_999))
        assertEquals(30_000L, RelativeTime.millisUntilNextMinute(150_000))
        assertTrue(RelativeTime.millisUntilNextMinute(1_234_567_891) in 1..60_000)
    }
}

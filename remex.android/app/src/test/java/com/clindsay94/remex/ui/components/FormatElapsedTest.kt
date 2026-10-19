package com.clindsay94.remex.ui.components

import org.junit.Assert.assertEquals
import org.junit.Test

/** The now-playing clock: m:ss under an hour, h:mm:ss from an hour up. */
class FormatElapsedTest {

    @Test
    fun underAnHour_isMinutesAndSeconds() {
        assertEquals("0:00", formatElapsed(0L))
        assertEquals("0:59", formatElapsed(59_000L))
        assertEquals("59:59", formatElapsed(3_599_000L))
    }

    @Test
    fun fromAnHour_addsTheHour() {
        assertEquals("1:00:00", formatElapsed(3_600_000L))
        assertEquals("1:35:12", formatElapsed(5_712_000L))
    }

    @Test
    fun aNegativeReading_clampsToZero() {
        assertEquals("0:00", formatElapsed(-5_000L))
    }
}

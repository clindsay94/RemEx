package com.clindsay94.remex

import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.widget.WidgetDataCache
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The widget's render interval, which now also gates its cache write (RemEx-o02rf).
 *
 * **HONEST ABOUT WHAT THIS DOES AND DOES NOT COVER.** The throttle decision and the interval
 * arithmetic are covered here. The COROUTINE WIRING is not, and cannot be from a JVM unit test — it
 * needs a Context, SettingsManager, GlanceAppWidgetManager, SharedPreferences and a global
 * SharedFlow. So these specific things are uncovered and are stated rather than implied: that the
 * cache write sits inside the throttle at all, that it happens BEFORE the render that reads it, that
 * the interval is seeded from the default, and that the settings collector exists to update it. Each
 * of those can be broken with every test below still green.
 */
class WidgetRenderIntervalTest {

    @Test
    fun `the default is a legal value of the setting`() {
        // Guards the DEFAULT being usable as a setting value, NOT the render rate — an earlier version
        // of this comment claimed the latter and was wrong: the seed goes through renderIntervalMs,
        // which clamps, so a sub-floor default could never produce a fast render. It would still be a
        // bug elsewhere, because SettingsManager uses it as the fallback inside a coerceIn.
        assertTrue(
                "default ${SettingsManager.WIDGET_TELEMETRY_POLL_DEFAULT}s is below the " +
                        "${SettingsManager.WIDGET_TELEMETRY_POLL_MIN}s floor",
                SettingsManager.WIDGET_TELEMETRY_POLL_DEFAULT >=
                        SettingsManager.WIDGET_TELEMETRY_POLL_MIN
        )
    }

    @Test
    fun `a freshly placed widget renders on the very first reading`() {
        // lastRenderMs of 0 means "nothing rendered yet". Seeding it with the current time instead is
        // the obvious-looking refactor, and it would leave a newly placed widget blank for a whole
        // interval - thirty seconds by default, ten minutes at the maximum setting.
        assertTrue(WidgetDataCache.shouldRender(nowMs = 1_700_000_000_000L, lastRenderMs = 0L, intervalMs = 600_000L))
    }

    @Test
    fun `the first-render case does not depend on the clock being a wall clock`() {
        // The subtraction alone appears to cover lastRenderMs == 0, but only because the caller passes
        // a wall clock of ~1.7e12. SystemClock.elapsedRealtime() is the more correct clock for
        // throttling and starts near zero at boot, and under it a freshly rebooted phone would show a
        // blank widget for a whole interval. Small nowMs is the case that catches the guarantee being
        // arithmetic rather than a rule.
        assertTrue(WidgetDataCache.shouldRender(nowMs = 5_000L, lastRenderMs = 0L, intervalMs = 30_000L))
    }

    @Test
    fun `the reading is saved before the widget is drawn from it`() {
        // **THE ONE WAY TO BREAK THIS BEAD THAT WOULD NOT ANNOUNCE ITSELF.** refreshHardwareWidgets
        // draws FROM the cache, so swapping the two calls renders the previous interval's numbers
        // forever - on schedule, at the right rate, with no log and no crash. Every other regression
        // here fails toward the old behaviour or writes a warning. A comment stops a careful reader
        // reordering two lines; it does not stop a formatter or a merge resolution.
        val order = mutableListOf<String>()

        runBlocking {
            WidgetDataCache.renderTick(
                    "{\"cpu\":1}",
                    write = { order.add("write:$it") },
                    render = { order.add("render") }
            )
        }

        assertEquals(listOf("write:{\"cpu\":1}", "render"), order)
    }

    @Test
    fun `a reading inside the interval does not render`() {
        // THE WHOLE BEAD IN ONE ASSERTION. Telemetry arrives at ~1 Hz; at the default interval
        // twenty-nine out of every thirty must do nothing at all - no render, and since RemEx-o02rf
        // no disk write either.
        assertFalse(WidgetDataCache.shouldRender(nowMs = 1_000_000L, lastRenderMs = 999_000L, intervalMs = 30_000L))
    }

    @Test
    fun `the interval boundary is inclusive`() {
        // Exactly one interval must render. `>` instead of `>=` would delay every render by one
        // telemetry tick forever - invisible in use, and a slow drift away from the configured rate.
        assertTrue(WidgetDataCache.shouldRender(nowMs = 1_030_000L, lastRenderMs = 1_000_000L, intervalMs = 30_000L))
        assertFalse(WidgetDataCache.shouldRender(nowMs = 1_029_999L, lastRenderMs = 1_000_000L, intervalMs = 30_000L))
    }

    @Test
    fun `a configured interval converts to milliseconds`() {
        assertEquals(30_000L, WidgetDataCache.renderIntervalMs(30))
        assertEquals(600_000L, WidgetDataCache.renderIntervalMs(SettingsManager.WIDGET_TELEMETRY_POLL_MAX))
    }

    @Test
    fun `anything under the floor is raised to it`() {
        // The settings flow already clamps, so this is the second line rather than the first — it
        // covers the seeded path and any future caller that has not been through the flow. Zero and
        // negative matter because a corrupted or hand-edited preference reaches here as an Int, and
        // an interval of zero turns the throttle off completely: back to a write and a render every
        // second, which is the exact behaviour this bead removed.
        val floorMs = SettingsManager.WIDGET_TELEMETRY_POLL_MIN * 1000L
        assertEquals(floorMs, WidgetDataCache.renderIntervalMs(0))
        assertEquals(floorMs, WidgetDataCache.renderIntervalMs(-5))
        assertEquals(floorMs, WidgetDataCache.renderIntervalMs(SettingsManager.WIDGET_TELEMETRY_POLL_MIN - 1))
    }

    @Test
    fun `the floor itself is returned unchanged`() {
        // Boundary, not decoration: coerceAtLeast is inclusive, and an off-by-one to `>` would push
        // the minimum-interval case up a whole second while every other value stayed correct.
        assertEquals(
                SettingsManager.WIDGET_TELEMETRY_POLL_MIN * 1000L,
                WidgetDataCache.renderIntervalMs(SettingsManager.WIDGET_TELEMETRY_POLL_MIN)
        )
    }
}

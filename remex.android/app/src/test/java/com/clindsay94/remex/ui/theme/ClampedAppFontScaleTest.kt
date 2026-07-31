package com.clindsay94.remex.ui.theme

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * RemEx-95ls: the in-app font scale multiplies .sp sizes that the system scale multiplies
 * again, so the combined scale is clamped to MAX_COMBINED_FONT_SCALE — by reducing only the
 * app's own contribution, never the system's.
 */
class ClampedAppFontScaleTest {

    @Test
    fun `full in-app range is available at system scale 1`() {
        assertEquals(1.4f, clampedAppFontScale(1.4f, 1.0f), 0.0001f)
        assertEquals(0.85f, clampedAppFontScale(0.85f, 1.0f), 0.0001f)
    }

    @Test
    fun `combined scale is capped at system Largest`() {
        // 1.3 (system Largest) x clamped app scale must not exceed the ceiling.
        val clamped = clampedAppFontScale(1.4f, 1.3f)
        assertEquals(MAX_COMBINED_FONT_SCALE, 1.3f * clamped, 0.001f)
    }

    @Test
    fun `app scale never counteracts the system setting`() {
        // API 34+ non-linear scaling: system alone can exceed the ceiling; the app multiplier
        // floors at 1.0 rather than shrinking text the system asked to enlarge.
        assertEquals(1.0f, clampedAppFontScale(1.4f, 2.0f), 0.0001f)
    }

    @Test
    fun `reduction requests always pass through`() {
        assertEquals(0.85f, clampedAppFontScale(0.85f, 2.0f), 0.0001f)
    }

    @Test
    fun `degenerate system scale is ignored`() {
        assertEquals(1.4f, clampedAppFontScale(1.4f, 0f), 0.0001f)
    }
}

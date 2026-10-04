package com.clindsay94.remex.data

import androidx.datastore.preferences.core.emptyPreferences
import androidx.datastore.preferences.core.mutablePreferencesOf
import androidx.datastore.preferences.core.preferencesOf
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The app background setting (RemEx-pp4cm.17): what is stored, what an upgrade reads, and when an
 * animated style is allowed to move.
 */
class BackgroundStylesTest {

    // ── The setting: default, migration, round trip ───────────────────────────────────────────

    @Test
    fun `a fresh install has no background`() {
        val read = SettingsManager.personalizationFrom(emptyPreferences())
        assertEquals(BackgroundStyles.None, read.backgroundStyle)
        assertEquals(BackgroundIntensity.DEFAULT, read.backgroundIntensity, 0f)
        assertEquals(BackgroundStyles.None, SettingsManager.PersonalizationPreferences().backgroundStyle)
    }

    @Test
    fun `an upgrading phone that saved its palette before still reads None`() {
        // The migration case: the stored personalization predates the background keys entirely.
        val before = preferencesOf(
            SettingsManager.THEME_PALETTE_KEY to "custom",
            SettingsManager.THEME_STYLE_KEY to "vibrant",
            SettingsManager.THEME_CONTRAST_KEY to 0.5f,
        )
        val read = SettingsManager.personalizationFrom(before)
        assertEquals(BackgroundStyles.None, read.backgroundStyle)
        assertFalse(BackgroundStyles.isVisible(read.backgroundStyle))
        assertEquals("vibrant", read.themeStyle)
    }

    @Test
    fun `every style and intensity written reads back unchanged`() {
        for (style in BackgroundStyles.All) {
            for (intensity in listOf(BackgroundIntensity.MIN, 0.37f, BackgroundIntensity.DEFAULT, BackgroundIntensity.MAX)) {
                val prefs = mutablePreferencesOf()
                SettingsManager.writeBackground(prefs, style, intensity)
                val read = SettingsManager.personalizationFrom(prefs)
                assertEquals(style, read.backgroundStyle)
                assertEquals("$style @ $intensity", intensity, read.backgroundIntensity, 0f)
            }
        }
    }

    @Test
    fun `a style the app does not know reads as None, and intensity stays in range`() {
        val prefs = preferencesOf(
            SettingsManager.BACKGROUND_STYLE_KEY to "plasma",
            SettingsManager.BACKGROUND_INTENSITY_KEY to 7f,
        )
        val read = SettingsManager.personalizationFrom(prefs)
        assertEquals(BackgroundStyles.None, read.backgroundStyle)
        assertEquals(BackgroundIntensity.MAX, read.backgroundIntensity, 0f)

        val written = mutablePreferencesOf()
        SettingsManager.writeBackground(written, "plasma", -3f)
        assertEquals(BackgroundStyles.None, written[SettingsManager.BACKGROUND_STYLE_KEY])
        assertEquals(BackgroundIntensity.MIN, written[SettingsManager.BACKGROUND_INTENSITY_KEY]!!, 0f)
    }

    @Test
    fun `the picker offers None, six textures and three animated styles with no repeats`() {
        assertEquals(BackgroundStyles.None, BackgroundStyles.All.first())
        assertEquals(6, BackgroundStyles.Textures.size)
        assertEquals(3, BackgroundStyles.Animated.size)
        assertEquals(10, BackgroundStyles.All.size)
        assertEquals(BackgroundStyles.All.size, BackgroundStyles.All.toSet().size)
        assertTrue(BackgroundStyles.Animated.all(BackgroundStyles::isAnimated))
        assertFalse(BackgroundStyles.Textures.any(BackgroundStyles::isAnimated))
    }

    @Test
    fun `intensity detents snap inside the radius and never trap a drag`() {
        assertEquals(0.5f, BackgroundIntensity.snap(0.52f), 0f)
        assertEquals(1.0f, BackgroundIntensity.snap(0.985f), 0f)
        assertEquals(0.4f, BackgroundIntensity.snap(0.4f), 0f)
        // Past the radius the drag leaves the detent.
        assertEquals(0.54f, BackgroundIntensity.snap(0.54f), 0f)
        assertEquals(BackgroundIntensity.MIN, BackgroundIntensity.snap(-4f), 0f)
    }

    // ── The pause rules ────────────────────────────────────────────────────────────────────────

    @Test
    fun `an animated style moves only while visible, with animations on and Battery Saver off`() {
        for (style in BackgroundStyles.Animated) {
            assertTrue(style, BackgroundMotionPolicy.shouldAnimate(style, appVisible = true, reducedMotion = false, batterySaver = false))
            assertFalse("not visible: $style", BackgroundMotionPolicy.shouldAnimate(style, appVisible = false, reducedMotion = false, batterySaver = false))
            assertFalse("reduced motion: $style", BackgroundMotionPolicy.shouldAnimate(style, appVisible = true, reducedMotion = true, batterySaver = false))
            assertFalse("battery saver: $style", BackgroundMotionPolicy.shouldAnimate(style, appVisible = true, reducedMotion = false, batterySaver = true))
            assertFalse("covered: $style", BackgroundMotionPolicy.shouldAnimate(style, appVisible = true, reducedMotion = false, batterySaver = false, covered = true))
        }
    }

    @Test
    fun `every combination of the stop rules stops it, and a texture or None never animates`() {
        for (visible in listOf(true, false)) for (reduced in listOf(true, false)) for (saver in listOf(true, false)) {
            val expected = visible && !reduced && !saver
            assertEquals(expected, BackgroundMotionPolicy.shouldAnimate(BackgroundStyles.Aurora, visible, reduced, saver))
            for (still in listOf(BackgroundStyles.None) + BackgroundStyles.Textures) {
                assertFalse(still, BackgroundMotionPolicy.shouldAnimate(still, visible, reduced, saver))
            }
        }
    }

    @Test
    fun `frames are capped at 30 per second`() {
        assertEquals(30, BackgroundMotionPolicy.MAX_FPS)
        val first = 5_000_000_000L
        assertTrue("the first frame is always due", BackgroundMotionPolicy.frameDue(first, Long.MIN_VALUE))
        assertFalse("a vsync 16 ms later is skipped", BackgroundMotionPolicy.frameDue(first + 16_000_000L, first))
        assertTrue("33.4 ms later draws", BackgroundMotionPolicy.frameDue(first + 33_400_000L, first))
    }
}

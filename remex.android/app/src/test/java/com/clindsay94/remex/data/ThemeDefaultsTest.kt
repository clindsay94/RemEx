package com.clindsay94.remex.data

import androidx.datastore.preferences.core.emptyPreferences
import androidx.datastore.preferences.core.preferencesOf
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test

/**
 * A phone with no stored theme choices starts on the RemEx default scheme, the PC's `BaseDarkGlass`
 * preset (RemEx-wqo7a.3), and anything the user has saved is read back unchanged.
 */
class ThemeDefaultsTest {

    @Test
    fun `a fresh PersonalizationPreferences is the RemEx default scheme`() {
        val fresh = SettingsManager.PersonalizationPreferences()
        assertEquals("#6C4CFF", fresh.themeSeedColor)
        assertEquals("tonal_spot", fresh.themeStyle)
        assertEquals("dark", fresh.themeMode)
        assertEquals(0.0f, fresh.themeContrast, 0f)
        // "custom" is the palette that paints through colorSchemeFromSeed, not wallpaper colours.
        assertEquals("custom", fresh.themePalette)
        assertFalse(fresh.dynamicColor)
    }

    @Test
    fun `an empty DataStore read yields the same RemEx default`() {
        val read = SettingsManager.personalizationFrom(emptyPreferences())
        assertEquals(SettingsManager.PersonalizationPreferences(), read)
        assertEquals("#6C4CFF", read.themeSeedColor)
        assertEquals("custom", read.themePalette)
        assertEquals("dark", read.themeMode)
        assertFalse(read.dynamicColor)
    }

    @Test
    fun `stored theme choices win over the defaults`() {
        val stored =
                preferencesOf(
                        SettingsManager.THEME_MODE_KEY to "system",
                        SettingsManager.THEME_PALETTE_KEY to "default",
                        SettingsManager.THEME_STYLE_KEY to "vibrant",
                        SettingsManager.THEME_SEED_COLOR_KEY to "#386A20",
                        SettingsManager.THEME_SEED_CHROMA_KEY to 30f,
                        SettingsManager.THEME_CONTRAST_KEY to 0.5f,
                        SettingsManager.DYNAMIC_COLOR_KEY to true,
                )
        val read = SettingsManager.personalizationFrom(stored)
        assertEquals("system", read.themeMode)
        assertEquals("default", read.themePalette)
        assertEquals("vibrant", read.themeStyle)
        assertEquals("#386A20", read.themeSeedColor)
        assertEquals(30f, read.themeSeedChroma, 0f)
        assertEquals(0.5f, read.themeContrast, 0f)
        assertEquals(true, read.dynamicColor)
    }

    @Test
    fun `one stored choice moves only that choice`() {
        val read =
                SettingsManager.personalizationFrom(
                        preferencesOf(SettingsManager.DYNAMIC_COLOR_KEY to true)
                )
        assertEquals(true, read.dynamicColor)
        // Only the stored key moved; the rest is still the RemEx default.
        assertEquals("#6C4CFF", read.themeSeedColor)
        assertEquals("custom", read.themePalette)
    }

    @Test
    fun `a phone that saved before keeps its wallpaper colours when the dynamic key was never written`() {
        // savePersonalization stores mode + palette but not dynamic colour; before 3.0 the missing key
        // meant wallpaper colours. Reading it as the new default (off) would drop this user onto the
        // static fallback scheme, neither what they had nor the RemEx default.
        val read =
                SettingsManager.personalizationFrom(
                        preferencesOf(
                                SettingsManager.THEME_MODE_KEY to "system",
                                SettingsManager.THEME_PALETTE_KEY to "default",
                        )
                )
        assertEquals(true, read.dynamicColor)
        assertEquals("default", read.themePalette)
        assertEquals("system", read.themeMode)
        assertEquals("#6750A4", read.themeSeedColor)
        assertEquals(48.0f, read.themeSeedChroma, 0f)
    }

    @Test
    fun `the default chroma paints exactly the default seed`() {
        // The custom path rebuilds the seed as Hct.from(hue, chroma, tone); the default chroma must
        // round-trip to the seed itself, or the phone (and what it syncs to the PC) drifts off it.
        val painted =
                ThemeSyncSeedResolver.applyChroma(
                        ThemeDefaults.THEME_SEED_ARGB,
                        ThemeDefaults.THEME_SEED_CHROMA,
                )
        assertEquals("#6C4CFF", ThemeSync.toHexRgb(painted))
    }
}

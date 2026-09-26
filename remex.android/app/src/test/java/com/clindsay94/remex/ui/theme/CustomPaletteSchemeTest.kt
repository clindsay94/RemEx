package com.clindsay94.remex.ui.theme

import com.clindsay94.remex.data.ThemeSyncSeedResolver
import androidx.compose.ui.graphics.Color
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

/**
 * Live-check B1: the Color Studio preview ignored the vibrancy (chroma) and contrast sliders. The
 * preview and [RemExTheme] now both paint a custom palette through [customPaletteScheme], so these
 * pin that every custom-palette axis actually reaches the scheme.
 */
class CustomPaletteSchemeTest {

    private val seed = 0xFF3F7FBF.toInt()

    private fun scheme(chroma: Float = 48f, contrast: Float = 0f, style: String = "tonal_spot", dark: Boolean = false) =
            customPaletteScheme(seed, chroma, dark, style, contrast)

    @Test
    fun `vibrancy changes the scheme`() {
        assertNotEquals(scheme(chroma = 10f).primaryContainer, scheme(chroma = 100f).primaryContainer)
    }

    @Test
    fun `contrast changes the scheme`() {
        assertNotEquals(scheme(contrast = -1f).primaryContainer, scheme(contrast = 1f).primaryContainer)
    }

    @Test
    fun `variant and dark mode change the scheme`() {
        assertNotEquals(scheme(style = "tonal_spot").primary, scheme(style = "vibrant").primary)
        assertNotEquals(scheme(dark = false).primaryContainer, scheme(dark = true).primaryContainer)
    }

    @Test
    fun `seed substitution matches what theme sync sends the PC`() {
        val expected =
                colorSchemeFromSeed(
                        Color(ThemeSyncSeedResolver.applyChroma(seed, 72f)),
                        darkTheme = true,
                        style = "expressive",
                        contrast = 0.5
                )
        val actual = scheme(chroma = 72f, contrast = 0.5f, style = "expressive", dark = true)
        assertEquals(expected.primary, actual.primary)
        assertEquals(expected.primaryContainer, actual.primaryContainer)
        assertEquals(expected.surface, actual.surface)
    }
}

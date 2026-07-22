package com.clindsay94.remex.ui.theme

import androidx.compose.ui.graphics.Color
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * RemEx-ou53: success roles must be generated through the same DynamicScheme pipeline as the
 * rest of the theme — tracking dark mode and the contrast setting — instead of fixed hex pairs
 * that ignored both. Hue must stay in the green family (success is semantic).
 */
class CustomColorsForSchemeTest {

    private fun hueOf(color: Color): Float {
        val r = color.red
        val g = color.green
        val b = color.blue
        val max = maxOf(r, g, b)
        val min = minOf(r, g, b)
        val delta = max - min
        if (delta == 0f) return 0f
        val hue = when (max) {
            r -> 60f * (((g - b) / delta) % 6f)
            g -> 60f * (((b - r) / delta) + 2f)
            else -> 60f * (((r - g) / delta) + 4f)
        }
        return if (hue < 0f) hue + 360f else hue
    }

    @Test
    fun `dark and light success roles differ`() {
        assertNotEquals(
            customColorsForScheme(darkTheme = false, contrast = 0.0).success,
            customColorsForScheme(darkTheme = true, contrast = 0.0).success,
        )
    }

    @Test
    fun `contrast setting now affects the success roles`() {
        // The bead's core complaint: at contrast 1.0 every other role adjusted and success
        // did not. These must differ now.
        assertNotEquals(
            customColorsForScheme(darkTheme = false, contrast = 0.0).success,
            customColorsForScheme(darkTheme = false, contrast = 1.0).success,
        )
        assertNotEquals(
            customColorsForScheme(darkTheme = true, contrast = 0.0).successContainer,
            customColorsForScheme(darkTheme = true, contrast = 1.0).successContainer,
        )
    }

    @Test
    fun `success stays in the green family across modes`() {
        for (dark in listOf(false, true)) {
            for (contrast in listOf(0.0, 0.5, 1.0)) {
                val hue = hueOf(customColorsForScheme(dark, contrast).success)
                assertTrue(
                    "success hue $hue (dark=$dark contrast=$contrast) should be green (60..180)",
                    hue in 60f..180f,
                )
            }
        }
    }
}

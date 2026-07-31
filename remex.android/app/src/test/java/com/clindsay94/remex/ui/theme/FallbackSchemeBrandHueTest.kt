package com.clindsay94.remex.ui.theme

import androidx.compose.ui.graphics.Color
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * RemEx-2xsy: the static fallback schemes (API < 31 / dynamic-color-off path) must derive from
 * the amber RemEx brand seed, not the Compose-template purple. The path can't be exercised on a
 * modern emulator (dynamicColor defaults true and the fallback only runs below API 31), so this
 * asserts the hue directly: amber sits near 37 degrees, template purple near 260.
 */
class FallbackSchemeBrandHueTest {

    /** Standard RGB→HSV hue, pure Kotlin so the test runs on the local JVM. */
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
    fun `fallback primaries carry the warm brand hue, not template purple`() {
        val lightHue = hueOf(LightColorScheme.primary)
        val darkHue = hueOf(DarkColorScheme.primary)
        // Warm amber family (broad band for tonal-spot hue drift); template purple is ~250-290.
        assertTrue("light primary hue $lightHue should be warm (<120), not purple", lightHue < 120f)
        assertTrue("dark primary hue $darkHue should be warm (<120), not purple", darkHue < 120f)
    }
}

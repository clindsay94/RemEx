package com.clindsay94.remex.ui.theme

import androidx.compose.ui.graphics.Color
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

/**
 * The seed -> splash palette mapping (RemEx-alwfa.1): pure and JVM-testable, same as
 * [FallbackSchemeBrandHueTest] and [CustomColorsForSchemeTest] — [colorSchemeFromSeed] and
 * `Hct` run fine on the local JVM, only `android.graphics.Color.parseColor` (used by
 * [buildSplashScheme]'s custom-palette arm, not exercised here) needs a real device/Robolectric.
 */
class SplashPaletteResolverTest {

    @Test
    fun `resolve maps scheme surface, primary and tertiary onto the splash palette`() {
        val scheme = colorSchemeFromSeed(Color(0xFF3B6B4A), darkTheme = true, style = "tonal_spot", contrast = 0.0)

        val palette = SplashPaletteResolver.resolve(scheme)

        assertEquals(scheme.surface, palette.backdrop)
        assertEquals(scheme.primary, palette.markStart)
        assertEquals(scheme.tertiary, palette.markEnd)
    }

    @Test
    fun `brand amber accent is kept when it has enough contrast against the backdrop`() {
        // A near-black dark-theme surface contrasts comfortably against the amber accent.
        val scheme = colorSchemeFromSeed(Color(0xFF3B6B4A), darkTheme = true, style = "tonal_spot", contrast = 0.0)

        val palette = SplashPaletteResolver.resolve(scheme)

        assertEquals(SplashBrandAmber, palette.accent)
    }

    @Test
    fun `accent falls back to tertiary when amber fails contrast against the backdrop`() {
        // Light-theme surfaces sit near-white; a bright amber accent contrasts poorly against
        // near-white (~1.6:1), well under the floor, so this must fall back to tertiary.
        val scheme = colorSchemeFromSeed(Color(0xFF3B6B4A), darkTheme = false, style = "tonal_spot", contrast = 0.0)

        val palette = SplashPaletteResolver.resolve(scheme)

        assertEquals(scheme.tertiary, palette.accent)
        assertNotEquals(SplashBrandAmber, palette.accent)
    }

    @Test
    fun `falls back to the static brand scheme when no personalization has loaded yet`() {
        val fromNull = SplashPaletteResolver.resolveOrFallback(null, darkTheme = true)
        val fromStaticScheme = SplashPaletteResolver.resolve(DarkColorScheme)

        assertEquals(fromStaticScheme, fromNull)
    }

    @Test
    fun `resolveOrFallback honors the provided scheme over the static fallback`() {
        val scheme = colorSchemeFromSeed(Color(0xFF3B6B4A), darkTheme = true, style = "tonal_spot", contrast = 0.0)

        val palette = SplashPaletteResolver.resolveOrFallback(scheme, darkTheme = true)

        assertEquals(scheme.surface, palette.backdrop)
    }

    @Test
    fun `contrastRatio is symmetric and matches known extremes`() {
        val blackWhite = SplashPaletteResolver.contrastRatio(Color.Black, Color.White)
        val whiteBlack = SplashPaletteResolver.contrastRatio(Color.White, Color.Black)

        assertEquals(21.0, blackWhite, 0.05)
        assertEquals(blackWhite, whiteBlack, 0.0001)
    }

    @Test
    fun `reduced motion cuts instead of crossfading`() {
        assertEquals(SplashExitTransition.CUT, splashExitTransitionFor(0f))
    }

    @Test
    fun `normal animator scale crossfades`() {
        assertEquals(SplashExitTransition.CROSSFADE, splashExitTransitionFor(1f))
        assertEquals(SplashExitTransition.CROSSFADE, splashExitTransitionFor(0.5f))
    }
}

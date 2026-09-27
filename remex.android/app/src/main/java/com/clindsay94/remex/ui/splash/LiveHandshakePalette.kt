package com.clindsay94.remex.ui.splash

import androidx.compose.material3.ColorScheme
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.graphics.luminance
import com.clindsay94.remex.ui.screens.SplashBrand
import com.clindsay94.remex.ui.theme.SplashBrandAmber
import com.clindsay94.remex.ui.theme.SplashPaletteResolver

/**
 * The Live Handshake palette (RemEx-8g6n0), resolved from the app's M3 scheme so it follows every
 * theming axis (seed x style x mode x contrast) through the scheme itself.
 *
 * The field shader is additive light on a dark ground (dots, rings and the portal rim all ADD
 * colour), so it needs a night backdrop in both modes. A dark scheme supplies it directly
 * (surfaceContainerLowest / surfaceContainer). A light scheme supplies it through its inverse
 * roles, which M3 defines for exactly this — a dark surface inside a light theme: inverseSurface
 * for the ground, inversePrimary for the lattice, inverseOnSurface for text. The splash then opens
 * into the light app through the portal.
 *
 * The accent is the brand amber, as on every splash, unless it fails [SplashPaletteResolver]'s
 * contrast floor against the ground, in which case it falls back to the scheme's own accent role.
 */
data class LiveHandshakePalette(
    val bg0: Color,
    val bg1: Color,
    val primary: Color,
    val accent: Color,
    val markA: Color,
    val markB: Color,
    val ink: Color,
    val muted: Color,
) {
    companion object {
        private const val MinAccentContrast = 3.0

        fun from(scheme: ColorScheme): LiveHandshakePalette {
            val dark = scheme.surface.luminance() < 0.5f
            val bg0: Color
            val bg1: Color
            val primary: Color
            val ink: Color
            val muted: Color
            val fallbackAccent: Color
            if (dark) {
                bg0 = scheme.surfaceContainerLowest
                bg1 = scheme.surfaceContainer
                primary = scheme.primary
                ink = scheme.onSurface
                muted = scheme.onSurfaceVariant
                fallbackAccent = scheme.tertiary
            } else {
                bg0 = lerp(scheme.inverseSurface, Color.Black, 0.55f)
                bg1 = scheme.inverseSurface
                primary = scheme.inversePrimary
                ink = scheme.inverseOnSurface
                muted = lerp(scheme.inverseOnSurface, scheme.inverseSurface, 0.35f)
                fallbackAccent = scheme.inversePrimary
            }
            val accent = if (SplashPaletteResolver.contrastRatio(SplashBrandAmber, bg1) >= MinAccentContrast) {
                SplashBrandAmber
            } else {
                fallbackAccent
            }
            return LiveHandshakePalette(
                bg0 = bg0,
                bg1 = bg1,
                primary = primary,
                accent = accent,
                // The brand window card, tinted a touch toward the seed so it belongs to the scheme.
                markA = lerp(SplashBrand.WindowFill, primary, 0.03f),
                markB = lerp(SplashBrand.WindowFill, primary, 0.16f),
                ink = ink,
                muted = muted,
            )
        }
    }
}

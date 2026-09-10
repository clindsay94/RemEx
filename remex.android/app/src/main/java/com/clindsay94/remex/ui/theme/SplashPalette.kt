package com.clindsay94.remex.ui.theme

import android.content.Context
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.graphics.toArgb
import com.clindsay94.remex.data.ThemeSnapshot
import com.clindsay94.remex.data.ThemeSyncSeedResolver
import kotlin.math.max
import kotlin.math.min

/**
 * `Theme.kt`'s `BrandSeed` (`Color(0xFFFFB63D)`), duplicated the same way
 * [ThemeSyncSeedResolver]'s `BrandSeedHex` duplicates it: `BrandSeed` is `private`, and widening
 * it for this one caller is not worth doing. If the brand seed ever changes,
 * `FallbackSchemeBrandHueTest`, `ThemeSyncSeedResolver.BrandSeedHex` and this constant all need
 * updating together; nothing enforces that link at compile time.
 */
val SplashBrandAmber = Color(0xFFFFB63D)

/** WCAG-style floor for keeping the fixed amber accent instead of falling back to tertiary. */
private const val MinAccentContrast = 3.0

/**
 * The seed-derived splash exit palette (RemEx-alwfa.1, Connor's decision (c) 2026-09-07):
 * the backdrop is the resolved scheme's surface tone, the mark is painted with a primary ->
 * tertiary gradient, and the brand amber accent is kept fixed UNLESS it fails contrast against
 * the recoloured backdrop, in which case it falls back to tertiary.
 */
data class SplashPalette(
    val backdrop: Color,
    val markStart: Color,
    val markEnd: Color,
    val accent: Color
)

object SplashPaletteResolver {

    /**
     * Pure and JVM-testable: maps an already-resolved [ColorScheme] onto the splash palette. The
     * caller is responsible for building [scheme] through the SAME three arms
     * [com.clindsay94.remex.ui.theme.RemExTheme] itself uses (see [buildSplashScheme]) so the
     * exit phase never shows a colour the content behind it is about to contradict.
     */
    fun resolve(scheme: ColorScheme): SplashPalette {
        val accent = if (contrastRatio(SplashBrandAmber, scheme.surface) >= MinAccentContrast) {
            SplashBrandAmber
        } else {
            scheme.tertiary
        }
        return SplashPalette(
            backdrop = scheme.surface,
            markStart = scheme.primary,
            markEnd = scheme.tertiary,
            accent = accent
        )
    }

    /**
     * [resolve], falling back to the app's static, `BrandSeed`-derived scheme
     * ([DarkColorScheme]/[LightColorScheme], Theme.kt:75-77) when no [scheme] could be resolved
     * yet — e.g. the splash's exit animation fires before personalization ever loaded. Mirrors
     * [ThemeSyncSeedResolver]'s own "no seed stored" fallback (`staticSeedHex`'s default arm):
     * the static scheme, never a synthesized placeholder.
     */
    fun resolveOrFallback(scheme: ColorScheme?, darkTheme: Boolean): SplashPalette =
        resolve(scheme ?: if (darkTheme) DarkColorScheme else LightColorScheme)

    /** WCAG 2.x relative-luminance contrast ratio: (L_lighter + 0.05) / (L_darker + 0.05). */
    internal fun contrastRatio(a: Color, b: Color): Double {
        val la = a.luminance() + 0.05
        val lb = b.luminance() + 0.05
        return max(la, lb) / min(la, lb)
    }
}

/**
 * Builds the [ColorScheme] the splash exit phase should paint with, mirroring
 * [com.clindsay94.remex.ui.theme.RemExTheme]'s own `colorScheme` `when` (Theme.kt:525-544)
 * arm-for-arm:
 *
 * 1. `themePalette == "custom"` -> [colorSchemeFromSeed] from the SAME chroma-substituted seed
 *    [ThemeSyncSeedResolver.staticSeedHex] computes (reused, not duplicated).
 * 2. Dynamic colour on (and not custom) -> the real [dynamicDarkColorScheme]/
 *    [dynamicLightColorScheme] for this device, called directly rather than reconstructed from
 *    a single seed hex — this is the "may use the system accent attributes" option RemEx-alwfa.1
 *    offered: it guarantees the exit phase is pixel-identical to the content it crossfades into,
 *    with no synthesis mismatch.
 * 3. Otherwise -> the cached static [DarkColorScheme]/[LightColorScheme] (BrandSeed, tonal_spot),
 *    exactly what that branch paints — never [colorSchemeFromSeed] again with the user's
 *    `themeStyle`, which that branch ignores.
 */
fun buildSplashScheme(context: Context, snapshot: ThemeSnapshot, darkTheme: Boolean): ColorScheme =
    when {
        snapshot.themePalette.equals("custom", ignoreCase = true) -> {
            val seedArgb = runCatching {
                android.graphics.Color.parseColor(ThemeSyncSeedResolver.staticSeedHex(snapshot))
            }.getOrDefault(SplashBrandAmber.toArgb())
            colorSchemeFromSeed(Color(seedArgb), darkTheme, snapshot.themeStyle, snapshot.themeContrast.toDouble())
        }
        snapshot.dynamicColor ->
            if (darkTheme) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
        else -> if (darkTheme) DarkColorScheme else LightColorScheme
    }

/**
 * Which transition the splash's recoloured exit phase uses, mirroring
 * [motionSchemeForAnimatorScale] (Theme.kt:430): reduced motion (animator duration scale 0) cuts
 * straight to content instead of crossfading through the seed-coloured phase.
 */
enum class SplashExitTransition { CROSSFADE, CUT }

internal fun splashExitTransitionFor(animatorDurationScale: Float): SplashExitTransition =
    if (animatorDurationScale == 0f) SplashExitTransition.CUT else SplashExitTransition.CROSSFADE

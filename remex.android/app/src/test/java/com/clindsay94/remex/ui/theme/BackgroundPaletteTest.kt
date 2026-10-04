package com.clindsay94.remex.ui.theme

import androidx.compose.material3.ColorScheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.compositeOver
import com.clindsay94.remex.data.BackgroundStyles
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Colour-role mapping and the contrast promise of the app background (RemEx-pp4cm.17): only scheme
 * roles, never a colour of its own, and `onBackground` text stays above [BackgroundPalette.targetContrast]
 * (AAA, or 90% of its own contrast when the scheme starts below that) over the strongest possible
 * background at full intensity, in every theme style and contrast level, including monochrome and
 * contrast 1.0.
 */
class BackgroundPaletteTest {

    private val seeds = listOf(0xFF6750A4, 0xFF00796B, 0xFFE65100, 0xFF1565C0, 0xFF7F7F7F, 0xFFFFD600)
        .map { Color(it.toInt()) }
    private val variants = listOf(
        "tonal_spot", "vibrant", "expressive", "neutral", "monochrome", "fidelity", "content",
    )
    private val contrasts = listOf(-1.0, -0.5, 0.0, 0.5, 1.0)

    /** Every scheme the app can draw for these seeds: both modes, all styles, contrast -1..1. */
    private fun allSchemes(): List<Pair<String, ColorScheme>> = buildList {
        for (seed in seeds) for (dark in listOf(false, true)) for (variant in variants) for (contrast in contrasts) {
            add("seed=${seed.value} dark=$dark $variant c=$contrast" to colorSchemeFromSeed(seed, dark, variant, contrast))
        }
        add("static light" to lightColorScheme())
        add("static dark" to darkColorScheme())
    }

    @Test
    fun `None draws nothing and every other style names at least one role`() {
        assertTrue(BackgroundPalette.roles(BackgroundStyles.None).isEmpty())
        assertEquals(0f, BackgroundPalette.maxAlpha(BackgroundStyles.None), 0f)
        assertEquals(0f, BackgroundPalette.safeMaxAlpha(BackgroundStyles.None, lightColorScheme()), 0f)
        for (style in BackgroundStyles.All.drop(1)) {
            assertTrue(style, BackgroundPalette.roles(style).isNotEmpty())
            assertTrue(style, BackgroundPalette.maxAlpha(style) > 0f)
        }
    }

    @Test
    fun `the colours are the active scheme's own roles, in the declared order`() {
        val scheme = lightColorScheme(
            primary = Color(0xFF111111), secondary = Color(0xFF222222), tertiary = Color(0xFF333333),
            outline = Color(0xFF444444), outlineVariant = Color(0xFF555555),
        )
        assertEquals(listOf(scheme.outline, scheme.primary), BackgroundPalette.colors(BackgroundStyles.Grain, scheme))
        assertEquals(listOf(scheme.outlineVariant, scheme.primary), BackgroundPalette.colors(BackgroundStyles.Grid, scheme))
        assertEquals(listOf(scheme.outlineVariant, scheme.tertiary), BackgroundPalette.colors(BackgroundStyles.Hexagons, scheme))
        assertEquals(listOf(scheme.primary, scheme.tertiary, scheme.secondary), BackgroundPalette.colors(BackgroundStyles.Aurora, scheme))
        assertEquals(listOf(scheme.primary, scheme.secondary, scheme.tertiary), BackgroundPalette.colors(BackgroundStyles.Mesh, scheme))
        // Switching the scheme switches the colours: nothing is cached or hardcoded.
        val other = darkColorScheme(primary = Color(0xFFAAAAAA), outline = Color(0xFFBBBBBB))
        assertEquals(listOf(other.outline, other.primary), BackgroundPalette.colors(BackgroundStyles.Grain, other))
    }

    @Test
    fun `layer opacity scales with intensity and never passes the style's cap`() {
        val scheme = colorSchemeFromSeed(Color(0xFF6750A4.toInt()), darkTheme = true)
        for (style in BackgroundStyles.All.drop(1)) {
            val safe = BackgroundPalette.safeMaxAlpha(style, scheme)
            assertTrue(style, safe in 0f..BackgroundPalette.maxAlpha(style))
            assertEquals(safe, BackgroundPalette.layerAlpha(style, 1f, scheme), 1e-6f)
            assertEquals(safe, BackgroundPalette.layerAlpha(style, 9f, scheme), 1e-6f)
            assertTrue(BackgroundPalette.layerAlpha(style, 0.5f, scheme) < BackgroundPalette.layerAlpha(style, 0.75f, scheme))
            assertTrue(BackgroundPalette.layerAlpha(style, 0f, scheme) > 0f)
        }
        // Both design caps stay well under half opacity, so a background never competes with content.
        assertTrue(BackgroundPalette.TEXTURE_MAX_ALPHA in 0.05f..0.5f)
        assertTrue(BackgroundPalette.ANIMATED_MAX_ALPHA in 0.05f..0.5f)
        assertEquals(0f, BackgroundPalette.layerAlpha(BackgroundStyles.None, 1f, scheme), 0f)
    }

    @Test
    fun `onBackground text keeps its contrast over the strongest background at full intensity`() {
        var worstMargin = Double.MAX_VALUE
        var worstAt = ""
        var checked = 0
        for ((name, scheme) in allSchemes()) {
            val target = BackgroundPalette.targetContrast(scheme)
            for (style in BackgroundStyles.All.drop(1)) {
                val alpha = BackgroundPalette.layerAlpha(style, 1f, scheme)
                // Worst case: every pixel is the role colour that pulls the background closest to
                // the text, at the layer's full opacity (a real texture covers a fraction of that).
                for (role in BackgroundPalette.roles(style)) {
                    val blended = role.colorIn(scheme).copy(alpha = alpha).compositeOver(scheme.background)
                    val margin = BackgroundPalette.contrastRatio(scheme.onBackground, blended) / target
                    checked++
                    if (margin < worstMargin) {
                        worstMargin = margin
                        worstAt = "$name $style ${role.name}"
                    }
                }
            }
        }
        assertTrue("no schemes were checked", checked > 5000)
        // A hair of slack for the bisection's resolution.
        assertTrue("worst margin over the target $worstMargin at $worstAt", worstMargin >= 0.995)
    }

    @Test
    fun `a scheme that starts readable stays at AAA or better, one that starts low loses at most a tenth`() {
        for ((name, scheme) in allSchemes()) {
            val baseline = BackgroundPalette.contrastRatio(scheme.onBackground, scheme.background)
            val target = BackgroundPalette.targetContrast(scheme)
            if (baseline >= BackgroundPalette.READABLE_RATIO) {
                assertEquals(name, BackgroundPalette.READABLE_RATIO, target, 1e-9)
            } else {
                assertEquals(name, baseline * BackgroundPalette.KEEP_LOW_BASELINE, target, 1e-9)
            }
        }
    }

    @Test
    fun `the contrast guard binds - the bare cap would break it on some schemes`() {
        // Anti-vacuity: without safeMaxAlpha the check above would fail for real schemes, so it can fail.
        var broken = 0
        var lowered = 0
        for ((_, scheme) in allSchemes()) {
            val target = BackgroundPalette.targetContrast(scheme)
            for (style in BackgroundStyles.All.drop(1)) {
                if (BackgroundPalette.safeMaxAlpha(style, scheme) < BackgroundPalette.maxAlpha(style) - 1e-4f) lowered++
                for (role in BackgroundPalette.roles(style)) {
                    val bare = role.colorIn(scheme).copy(alpha = BackgroundPalette.maxAlpha(style)).compositeOver(scheme.background)
                    if (BackgroundPalette.contrastRatio(scheme.onBackground, bare) < target) broken++
                }
            }
        }
        assertTrue("the bare cap never broke the promise, so the guard is untested", broken > 0)
        assertTrue("the guard never lowered an opacity", lowered > 0)
    }

    @Test
    fun `the strongest schemes still show the background, not a vanishing one`() {
        // Monochrome at contrast 1.0 is the harshest scheme; the layer must stay visible there.
        for (dark in listOf(false, true)) {
            val scheme = colorSchemeFromSeed(Color(0xFF6750A4.toInt()), dark, "monochrome", 1.0)
            for (style in BackgroundStyles.All.drop(1)) {
                assertTrue("$style dark=$dark", BackgroundPalette.safeMaxAlpha(style, scheme) >= 0.10f)
            }
        }
    }
}

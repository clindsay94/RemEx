package com.clindsay94.remex.ui.routines

import androidx.compose.material3.ColorScheme
import androidx.compose.ui.graphics.Color
import com.clindsay94.remex.ui.theme.SplashPaletteResolver
import com.clindsay94.remex.ui.theme.colorSchemeFromSeed
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * R-UX-49 RoutineStatusContrastTest (RemEx-pp0rt.6): every role pair the Routines UI paints text or
 * status icons with keeps its contrast across seeds, styles (monochrome included), light and dark,
 * and the app's contrast setting (0.0-1.0). Schemes come from the app's own [colorSchemeFromSeed],
 * the ratio from [SplashPaletteResolver.contrastRatio] (WCAG 2.x), so this measures what ships.
 */
class RoutineStatusContrastTest {
    private val seeds =
        listOf(
            0xFFF5A623, // amber, near the brand seed
            0xFF3F7FBF, // blue
            0xFF2E7D32, // green
            0xFFC62828, // red: the error role's own hue
            0xFF6A1B9A, // purple
            0xFFFFEB3B, // yellow: the light-tone hue that fails first
            0xFF808080, // grey
        ).map { Color(it) }
    private val styles = listOf("tonal_spot", "monochrome", "vibrant", "expressive", "neutral", "fidelity")
    private val contrasts = listOf(0.0, 0.5, 1.0)

    private data class Pair(val name: String, val ink: (ColorScheme) -> Color, val ground: (ColorScheme) -> Color, val min: Double)

    /** Text on its container: WCAG AA body text, 4.5:1. */
    private val textPairs =
        listOf(
            // Chain tokens, the Runs-on chip, step badges, the Pause banner.
            Pair("onSecondaryContainer/secondaryContainer", { it.onSecondaryContainer }, { it.secondaryContainer }, 4.5),
            // The destructive tint: discards-work chain tokens and step cards (R-UX-54).
            Pair("onErrorContainer/errorContainer", { it.onErrorContainer }, { it.errorContainer }, 4.5),
            // The chain token of the step running now.
            Pair("onPrimary/primary", { it.onPrimary }, { it.primary }, 4.5),
            // The WHEN card.
            Pair("onPrimaryContainer/primaryContainer", { it.onPrimaryContainer }, { it.primaryContainer }, 4.5),
            // Read-only and store-reset notices.
            Pair("onTertiaryContainer/tertiaryContainer", { it.onTertiaryContainer }, { it.tertiaryContainer }, 4.5),
            // Outcome text beside its icon, cards and history rows.
            Pair("onSurfaceVariant/surface", { it.onSurfaceVariant }, { it.surface }, 4.5),
            Pair("onSurfaceVariant/surfaceContainerLow", { it.onSurfaceVariant }, { it.surfaceContainerLow }, 4.5),
        )

    /** Status icons and the role-coloured status and problem text beside them. */
    private val statusPairs =
        listOf(
            Pair("primary/surface (succeeded)", { it.primary }, { it.surface }, 4.5),
            Pair("error/surface (failed, problems)", { it.error }, { it.surface }, 4.5),
            Pair("error/surfaceContainerLow (destructive verbs in sheets)", { it.error }, { it.surfaceContainerLow }, 4.5),
            Pair("tertiary/surface (running, warnings)", { it.tertiary }, { it.surface }, 4.5),
            Pair("primary/surfaceContainerLow (card icons)", { it.primary }, { it.surfaceContainerLow }, 4.5),
        )

    private fun failures(pairs: List<Pair>): List<String> {
        val out = mutableListOf<String>()
        for (seed in seeds) for (style in styles) for (dark in listOf(false, true)) for (contrast in contrasts) {
            val scheme = colorSchemeFromSeed(seed, dark, style, contrast)
            for (p in pairs) {
                val ratio = SplashPaletteResolver.contrastRatio(p.ink(scheme), p.ground(scheme))
                if (ratio < p.min) out += "${p.name} %.2f < ${p.min} (seed ${seed.value.toString(16).take(8)}, $style, dark=$dark, contrast=$contrast)".format(ratio)
            }
        }
        return out
    }

    @Test
    fun `routine text keeps 4_5 to 1 on its container for every seed, style, mode and contrast`() {
        val bad = failures(textPairs)
        assertTrue("Routines text pairs below 4.5:1:\n" + bad.take(20).joinToString("\n"), bad.isEmpty())
    }

    @Test
    fun `routine status colours keep 4_5 to 1 on their surface for every seed, style, mode and contrast`() {
        // They also colour short text (a step's status, a problem line), so they are held to the
        // text bar rather than the 3:1 icon bar.
        val bad = failures(statusPairs)
        assertTrue("Routines status pairs below 4.5:1:\n" + bad.take(20).joinToString("\n"), bad.isEmpty())
    }

    @Test
    fun `the harness can fail - a decorative role is below the text bar somewhere`() {
        // outlineVariant is for dividers, never text: if this "passes" everywhere the schemes are not
        // being built and the two tests above prove nothing.
        val control = Pair("outlineVariant/surface", { it.outlineVariant }, { it.surface }, 4.5)
        assertTrue(failures(listOf(control)).isNotEmpty())
    }
}

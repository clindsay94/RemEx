package com.clindsay94.remex.ui.theme

import androidx.compose.material3.ColorScheme
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.compositeOver
import androidx.compose.ui.graphics.luminance
import com.clindsay94.remex.data.BackgroundIntensity
import com.clindsay94.remex.data.BackgroundStyles

/**
 * The colour roles an app background may be drawn from, and how strongly (RemEx-pp4cm.17). A
 * background never has a colour of its own: every pixel is one of these roles of the ACTIVE
 * [ColorScheme] laid over `background` at low opacity, so it follows seed, dynamic and static colour,
 * light and dark, every theme style and every contrast level.
 *
 * Text contrast is protected twice. Cards and other surfaces stay opaque, so text on them never sees
 * the layer. Text drawn straight on the background does, so [maxAlpha] caps how far any role can
 * move the background, and `BackgroundPaletteTest` proves `onBackground` stays readable at full
 * intensity for the monochrome style and contrast 1.0, the harshest cases.
 */
enum class BackgroundRole {
    Primary,
    Secondary,
    Tertiary,
    Outline,
    OutlineVariant;

    fun colorIn(scheme: ColorScheme): Color =
        when (this) {
            Primary -> scheme.primary
            Secondary -> scheme.secondary
            Tertiary -> scheme.tertiary
            Outline -> scheme.outline
            OutlineVariant -> scheme.outlineVariant
        }
}

object BackgroundPalette {
    /** Opacity ceiling of a still texture's layer at full intensity. */
    const val TEXTURE_MAX_ALPHA = 0.32f

    /** Opacity ceiling of an animated style's layer at full intensity (they paint soft, broad shapes). */
    const val ANIMATED_MAX_ALPHA = 0.30f

    /** The roles [style] draws from, most prominent first. Empty for [BackgroundStyles.None]. */
    fun roles(style: String): List<BackgroundRole> =
        when (BackgroundStyles.effective(style)) {
            BackgroundStyles.Grain -> listOf(BackgroundRole.Outline, BackgroundRole.Primary)
            BackgroundStyles.Dots -> listOf(BackgroundRole.Outline, BackgroundRole.Primary)
            BackgroundStyles.Grid -> listOf(BackgroundRole.OutlineVariant, BackgroundRole.Primary)
            BackgroundStyles.Topographic -> listOf(BackgroundRole.Outline, BackgroundRole.Secondary)
            BackgroundStyles.Hexagons -> listOf(BackgroundRole.OutlineVariant, BackgroundRole.Tertiary)
            BackgroundStyles.Carbon -> listOf(BackgroundRole.Outline, BackgroundRole.Primary)
            BackgroundStyles.Aurora -> listOf(BackgroundRole.Primary, BackgroundRole.Tertiary, BackgroundRole.Secondary)
            BackgroundStyles.Mesh -> listOf(BackgroundRole.Primary, BackgroundRole.Secondary, BackgroundRole.Tertiary)
            BackgroundStyles.Starfield -> listOf(BackgroundRole.Primary, BackgroundRole.Tertiary)
            else -> emptyList()
        }

    /** The layer's opacity ceiling for [style], reached at intensity 1.0. Zero for None. */
    fun maxAlpha(style: String): Float =
        when {
            !BackgroundStyles.isVisible(style) -> 0f
            BackgroundStyles.isAnimated(style) -> ANIMATED_MAX_ALPHA
            else -> TEXTURE_MAX_ALPHA
        }

    /**
     * The contrast ratio text drawn straight on the background may never fall below, in the
     * worst case of a role covering every pixel at full layer opacity. 7:1 is WCAG AAA for body
     * text, so a scheme that starts above it (contrast 0 and up) keeps AAA text, while the layer
     * still has room to show.
     */
    const val READABLE_RATIO = 7.0

    /**
     * A scheme that starts BELOW [READABLE_RATIO] (contrast -1, or a washed-out dynamic scheme)
     * has no headroom, so the layer may cost its text at most 10%.
     */
    const val KEEP_LOW_BASELINE = 0.9

    /** The contrast ratio `onBackground` must keep on [scheme]: see [READABLE_RATIO] and [KEEP_LOW_BASELINE]. */
    fun targetContrast(scheme: ColorScheme): Double =
        minOf(contrastRatio(scheme.onBackground, scheme.background) * KEEP_LOW_BASELINE, READABLE_RATIO)

    /**
     * The strongest opacity [style] may reach on [scheme]: the design cap [maxAlpha], lowered if
     * that would let any of the style's roles, laid over `background` at full coverage, push
     * `onBackground` below [targetContrast]. Found per scheme, so it holds for dynamic and custom
     * colour as well as the static fallback, in light and dark, at any contrast level.
     */
    fun safeMaxAlpha(style: String, scheme: ColorScheme): Float {
        val cap = maxAlpha(style)
        if (cap <= 0f) return 0f
        val target = targetContrast(scheme)
        var safe = cap
        for (role in roles(style)) {
            val color = role.colorIn(scheme)
            if (ratioAt(scheme, color, safe) >= target) continue
            // Contrast only falls as the role's share grows, so bisect for the largest share that holds.
            var lo = 0f
            var hi = safe
            repeat(BISECTION_STEPS) {
                val mid = (lo + hi) / 2f
                if (ratioAt(scheme, color, mid) >= target) lo = mid else hi = mid
            }
            safe = lo
        }
        return safe
    }

    /** The opacity the whole layer is drawn at for [intensity] (clamped to the slider's range) on [scheme]. */
    fun layerAlpha(style: String, intensity: Float, scheme: ColorScheme): Float =
        safeMaxAlpha(style, scheme) * BackgroundIntensity.clamp(intensity)

    private fun ratioAt(scheme: ColorScheme, role: Color, alpha: Float): Double =
        contrastRatio(scheme.onBackground, role.copy(alpha = alpha).compositeOver(scheme.background))

    /** WCAG contrast ratio of two opaque colours. */
    internal fun contrastRatio(a: Color, b: Color): Double {
        val la = a.luminance().toDouble()
        val lb = b.luminance().toDouble()
        return (maxOf(la, lb) + 0.05) / (minOf(la, lb) + 0.05)
    }

    private const val BISECTION_STEPS = 14

    /** [style]'s roles resolved against [scheme], in the same order as [roles]. */
    fun colors(style: String, scheme: ColorScheme): List<Color> = roles(style).map { it.colorIn(scheme) }
}

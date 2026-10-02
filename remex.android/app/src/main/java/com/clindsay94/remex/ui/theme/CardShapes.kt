package com.clindsay94.remex.ui.theme

import androidx.compose.foundation.shape.CutCornerShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import kotlin.math.roundToInt

/**
 * The tile shapes a card can take (RemEx-kq10x.5, cohesion spec decision 6).
 *
 * Only shapes that cannot clip a card's content are offered: expressive shapes are accents (the
 * connection orb, the splash), never containers (Android UI refresh spec, "Principles"). The old
 * 0-24 morph slider let a tile become a blob, pentagon, flower or clover, which cut the label and
 * the value off at the edges.
 *
 * Stored values keep their old float encoding, so nothing in DataStore had to be rewritten. The
 * migration happens when a value is READ: [sanitize] maps anything that is not one of [options]
 * (a removed shape, a fractional morph position, the "inherit" sentinel, NaN) to the rounded
 * rectangle. That makes every reader safe without a one-shot migration that could be skipped.
 */
object CardShapes {
    /** Kept at the old slider's top index so a saved "Rounded Rectangle" still reads as itself. */
    const val ROUNDED_RECTANGLE = 24f

    /** One slot past the old 0-24 range, so no value saved before this change can land on it. */
    const val CUT_CORNER = 25f

    /** The choices the pickers offer, default first. */
    val options: List<Float> = listOf(ROUNDED_RECTANGLE, CUT_CORNER)

    /**
     * The corner radius default, in dp. Matches the PC's Personalize > Surfaces > Corner Radius
     * default (`DashboardProfile.CornerRadius = 16`), so a card has the same corners on both apps.
     */
    const val DEFAULT_CORNER_RADIUS_DP = 16

    /** Inner padding of every card. Constant now that no card shape intrudes on its content. */
    const val INNER_PADDING_DP = 8

    /** Any stored or animated shape value, resolved to one of [options]. Never throws. */
    fun sanitize(index: Float): Float {
        if (index.isNaN() || index.isInfinite()) return ROUNDED_RECTANGLE
        val snapped = index.roundToInt().toFloat()
        return if (snapped in options) snapped else ROUNDED_RECTANGLE
    }

    /** The user-facing name of a (sanitized) shape value. */
    fun nameRes(index: Float): Int = when (sanitize(index)) {
        CUT_CORNER -> R.string.shape_cut_corner
        else -> R.string.shape_rounded_rectangle
    }

    /**
     * The Compose shape for a stored value. The cut is half the corner radius: a 45-degree cut of
     * the full radius reaches further into the card than a quarter circle of the same radius, and at
     * the 36dp maximum it would reach the label's top-left corner.
     */
    fun shapeFor(index: Float, cornerRadiusDp: Int): Shape = when (sanitize(index)) {
        CUT_CORNER -> CutCornerShape((cornerRadiusDp / 2).dp)
        else -> RoundedCornerShape(cornerRadiusDp.dp)
    }
}

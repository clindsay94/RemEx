package com.clindsay94.remex.ui.theme

import androidx.compose.foundation.shape.CutCornerShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import kotlin.math.roundToInt

/**
 * The tile shapes a card can take (RemEx-kq10x.5 cut them to two; RemEx-pp4cm.14 brought a curated
 * set back, because two shapes across so many categories is not remarkable).
 *
 * Every shape here is a SAFE container: its largest centred inscribed rectangle is measured
 * ([CardShapeSafety]) and the card pads its content to that rectangle ([shapeSafeArea]), so the
 * label and the value can never clip. [MIN_SAFE_FRACTION] is the floor, and a test holds every
 * offered polygon to it. The 0-24 morph slider's blobs, hearts, triangles and diamonds stay gone:
 * they leave too little of the card (a circle caps at 0.707; a diamond is 0.5).
 *
 * Stored values keep their old float encoding. The six polygons use the index they always had in
 * [materialShapesList], so a shape saved before the cut-back (and still sitting in DataStore, since
 * the cut-back migrated on READ) reads as itself again. [sanitize] maps everything else to the
 * nearest offered shape in character ([LEGACY_FALLBACKS]) and never throws.
 */
object CardShapes {
    /** Kept at the old slider's top index so a saved "Rounded Rectangle" still reads as itself. */
    const val ROUNDED_RECTANGLE = 24f

    /** One slot past the old 0-24 range, so no value saved before the cut-back can land on it. */
    const val CUT_CORNER = 25f

    // The polygons keep their materialShapesList indices.
    const val ARCH = 5f
    const val SLANTED = 8f
    const val CLAM_SHELL = 10f
    const val CLOVER = 18f
    const val SUNNY = 20f
    const val PIXEL_CIRCLE = 23f

    /**
     * The smallest safe fraction a polygon may have to be offered: the circle is the mathematical
     * floor for any round container (0.707), so this admits the circle class and rejects hearts,
     * triangles, diamonds and the spiky stars.
     */
    const val MIN_SAFE_FRACTION = 0.70f

    /** The offered polygons, roomiest first. */
    val polygonOptions: List<Float> = listOf(SLANTED, CLOVER, CLAM_SHELL, ARCH, SUNNY, PIXEL_CIRCLE)

    /** The choices the pickers offer, default first. */
    val options: List<Float> = listOf(ROUNDED_RECTANGLE, CUT_CORNER) + polygonOptions

    /**
     * Where a shape that is no longer offered lands, by character: smooth round shapes and the square
     * stay a rounded rectangle (what they have read as since the cut-back), angular ones become a cut
     * corner, scalloped ones their nearest offered cousin. Keys are the old `materialShapesList`
     * indices that are not offered themselves.
     */
    private val LEGACY_FALLBACKS: Map<Int, Float> = mapOf(
        0 to ROUNDED_RECTANGLE, // Circle
        1 to ROUNDED_RECTANGLE, // Square
        2 to CUT_CORNER, // Triangle
        3 to CUT_CORNER, // Diamond
        4 to CUT_CORNER, // Pentagon
        6 to ROUNDED_RECTANGLE, // Semi-circle
        7 to ROUNDED_RECTANGLE, // Pill
        9 to CLAM_SHELL, // Fan
        11 to CUT_CORNER, // Gem
        12 to ROUNDED_RECTANGLE, // Heart
        13 to CLOVER, // Flower
        14 to ROUNDED_RECTANGLE, // Puffy
        15 to ROUNDED_RECTANGLE, // Puffy diamond
        16 to ROUNDED_RECTANGLE, // Ghostish
        17 to ROUNDED_RECTANGLE, // Oval
        19 to CLOVER, // 8-leaf clover
        21 to SUNNY, // Soft burst
        22 to SUNNY, // Soft boom
    )

    /**
     * The corner radius default, in dp. Matches the PC's Personalize > Surfaces > Corner Radius
     * default (`DashboardProfile.CornerRadius = 16`), so a card has the same corners on both apps.
     */
    const val DEFAULT_CORNER_RADIUS_DP = 16

    /** Inner padding of every card (on top of the shape's own safe area). */
    const val INNER_PADDING_DP = 8

    /**
     * The gap between cards, in dp: between Home's cards and between Sensors grid cells, so the two
     * screens share one rhythm (RemEx-wqo7a.7).
     */
    const val CARD_SPACING_DP = 12

    /**
     * Any stored or animated shape value, resolved to one of [options]. Never throws. A saved value
     * that is offered stays; a fractional morph position snaps to the nearest index first; a removed
     * shape maps through [LEGACY_FALLBACKS]; anything out of range (the "inherit" sentinel, NaN)
     * reads as the rounded rectangle.
     */
    fun sanitize(index: Float): Float {
        if (index.isNaN() || index.isInfinite()) return ROUNDED_RECTANGLE
        val snapped = index.roundToInt()
        val asFloat = snapped.toFloat()
        if (asFloat in options) return asFloat
        return LEGACY_FALLBACKS[snapped] ?: ROUNDED_RECTANGLE
    }

    /** True when [index] (sanitized) is one of the stretched-polygon shapes. */
    fun isPolygon(index: Float): Boolean = sanitize(index) in polygonOptions

    /** The user-facing name of a (sanitized) shape value. */
    fun nameRes(index: Float): Int = when (sanitize(index)) {
        CUT_CORNER -> R.string.shape_cut_corner
        SLANTED -> R.string.shape_slanted
        CLOVER -> R.string.shape_clover_4_leaf
        CLAM_SHELL -> R.string.shape_clam_shell
        ARCH -> R.string.shape_arch
        SUNNY -> R.string.shape_sunny
        PIXEL_CIRCLE -> R.string.shape_pixel_circle
        else -> R.string.shape_rounded_rectangle
    }

    /**
     * The fraction of the card (per side, centred) that content may use for a (sanitized) shape: 1.0
     * for the rounded rectangle and the cut corner (their corners are handled by the card padding
     * and the half-radius cut), the measured inscribed-rectangle fraction for a polygon.
     */
    fun safeFraction(index: Float): Float {
        val s = sanitize(index)
        return if (s in polygonOptions) polygonSafeFractions.getValue(s) else 1f
    }

    /** Measured once per process; a handful of polygons, a few milliseconds. */
    private val polygonSafeFractions: Map<Float, Float> by lazy {
        polygonOptions.associateWith { CardShapeSafety.safeFraction(materialShapesList[it.toInt()]) }
    }

    /**
     * [shapeFor] for a full-width STRIP (a search field, a bottom sheet): a polygon stretched across a
     * whole row would clip the text in it, so a polygon choice reads as the rounded rectangle there.
     */
    fun stripShapeFor(index: Float, cornerRadiusDp: Int): Shape =
        if (isPolygon(index)) RoundedCornerShape(cornerRadiusDp.dp) else shapeFor(index, cornerRadiusDp)

    /**
     * The Compose shape for a stored value. The cut is half the corner radius: a 45-degree cut of
     * the full radius reaches further into the card than a quarter circle of the same radius, and at
     * the 36dp maximum it would reach the label's top-left corner. Polygons ignore the radius.
     */
    fun shapeFor(index: Float, cornerRadiusDp: Int): Shape = when (val s = sanitize(index)) {
        CUT_CORNER -> CutCornerShape((cornerRadiusDp / 2).dp)
        in polygonOptions -> polygonShape(s.toInt())
        else -> RoundedCornerShape(cornerRadiusDp.dp)
    }
}

package com.clindsay94.remex.ui.screens

import kotlin.math.abs

/**
 * The contrast slider's detents (RemEx-4kv0g.16, phone half): a drag within [RADIUS] of -1, 0 or 1
 * lands exactly on it, so "standard" and the two extremes are easy to hit. The same rule and radius as
 * the PC's `CustomizationViewModel.SnapContrastToDetent` (strictly less than 0.05), so a contrast set
 * on either side reads the same on the other.
 *
 * A drag is never trapped by a detent: the slider reports each move from the finger's absolute
 * position, so moving past the radius leaves it.
 */
object ContrastDetents {
    val DETENTS = floatArrayOf(-1f, 0f, 1f)
    const val RADIUS = 0.05f

    fun snap(value: Float): Float {
        for (detent in DETENTS) {
            if (abs(value - detent) < RADIUS) return detent
        }
        return value
    }
}

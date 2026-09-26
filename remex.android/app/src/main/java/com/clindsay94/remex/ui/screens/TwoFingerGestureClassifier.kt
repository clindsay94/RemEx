package com.clindsay94.remex.ui.screens

import kotlin.math.abs
import kotlin.math.hypot

/** What a two-finger gesture on the remote-desktop surface is doing, once it has been decided. */
internal enum class TwoFingerIntent {
    /** Fingers travel together: wheel-scroll the host (or pan the view when zoomed in). */
    Scroll,

    /** Finger spacing changes: zoom the local view. */
    Pinch,
}

/**
 * Decides whether a two-finger gesture is a scroll or a pinch (live-check D1).
 *
 * The old rule compared ONE frame's deltas in raw pixels, pinch first: a span change over 5 px in a
 * single frame made it a pinch. Two fingers dragged together never keep a perfectly constant span,
 * and at 3x density 5 px is under 2 dp, so a quick two-finger scroll routinely locked as a zoom.
 *
 * Now the decision is made against where the fingers were when the second one landed (the
 * baseline), and only once the gesture has moved past a slop window in dp. Past the slop it is a
 * pinch only when the span change clearly dominates the centre's travel: a scroll moves the centre
 * with a near-constant span; a pinch changes the span while the centre stays (both fingers moving)
 * or moves half as far (one finger anchored, ratio 2). The caller locks the result for the rest of
 * the gesture.
 */
internal object TwoFingerGestureClassifier {
    /** How far (span change or centre travel) the gesture must move before it is classified. */
    const val DECISION_SLOP_DP = 12f

    /**
     * Pinch only when the span change exceeds the centre's travel by this factor. Above 1 biases an
     * ambiguous gesture towards scroll; a one-finger-anchored pinch still scores 2.
     */
    const val PINCH_DOMINANCE = 1.25f

    /**
     * @param startSpan distance between the fingers when the gesture started.
     * @param startCenterX,startCenterY midpoint of the fingers when the gesture started.
     * @param span,centerX,centerY the same measurements now.
     * @param decisionSlopPx [DECISION_SLOP_DP] in pixels.
     * @return the intent, or null while the gesture is still inside the slop window.
     */
    fun classify(
        startSpan: Float,
        startCenterX: Float,
        startCenterY: Float,
        span: Float,
        centerX: Float,
        centerY: Float,
        decisionSlopPx: Float,
    ): TwoFingerIntent? {
        val spanChange = abs(span - startSpan)
        val travel = hypot(centerX - startCenterX, centerY - startCenterY)
        if (spanChange < decisionSlopPx && travel < decisionSlopPx) return null
        return if (spanChange > travel * PINCH_DOMINANCE) TwoFingerIntent.Pinch
        else TwoFingerIntent.Scroll
    }
}

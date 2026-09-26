package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.TwoFingerGestureClassifier
import com.clindsay94.remex.ui.screens.TwoFingerIntent
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Two-finger scroll vs pinch on the remote-desktop surface (live-check D1: "two-finger scroll often
 * zooms instead"). Coordinates are pixels; SLOP stands in for [TwoFingerGestureClassifier.DECISION_SLOP_DP]
 * at 3x density.
 */
class TwoFingerGestureClassifierTest {

    private companion object {
        const val SLOP = 36f
        const val START_SPAN = 400f
        const val CX = 500f
        const val CY = 800f
    }

    private fun classify(span: Float, dx: Float, dy: Float) =
            TwoFingerGestureClassifier.classify(
                    startSpan = START_SPAN,
                    startCenterX = CX,
                    startCenterY = CY,
                    span = span,
                    centerX = CX + dx,
                    centerY = CY + dy,
                    decisionSlopPx = SLOP,
            )

    @Test
    fun `undecided while both span change and travel are inside the slop`() {
        assertNull(classify(span = START_SPAN + 20f, dx = 0f, dy = 25f))
        assertNull(classify(span = START_SPAN, dx = 0f, dy = 0f))
    }

    @Test
    fun `fingers travelling together with a wobbling span is a scroll`() {
        // The old per-frame rule called this a pinch: 12 px of span drift in one frame.
        assertEquals(TwoFingerIntent.Scroll, classify(span = START_SPAN - 12f, dx = 0f, dy = 60f))
        assertEquals(TwoFingerIntent.Scroll, classify(span = START_SPAN + 20f, dx = 40f, dy = 0f))
    }

    @Test
    fun `diagonal scroll is a scroll`() {
        assertEquals(TwoFingerIntent.Scroll, classify(span = START_SPAN + 8f, dx = 30f, dy = 30f))
    }

    @Test
    fun `symmetric spread or squeeze is a pinch`() {
        assertEquals(TwoFingerIntent.Pinch, classify(span = START_SPAN + 40f, dx = 0f, dy = 0f))
        assertEquals(TwoFingerIntent.Pinch, classify(span = START_SPAN - 40f, dx = 2f, dy = -3f))
    }

    @Test
    fun `one finger anchored while the other moves is a pinch`() {
        // Moving finger travels 40 px along the axis between them: span +40, centre +20.
        assertEquals(TwoFingerIntent.Pinch, classify(span = START_SPAN + 40f, dx = 20f, dy = 0f))
    }

    @Test
    fun `span change that does not dominate travel stays a scroll`() {
        // Ratio 1.1 is under PINCH_DOMINANCE: ambiguous gestures bias to scroll.
        assertEquals(TwoFingerIntent.Scroll, classify(span = START_SPAN + 44f, dx = 0f, dy = 40f))
    }

    @Test
    fun `constant-span horizontal scroll is a scroll`() {
        assertEquals(TwoFingerIntent.Scroll, classify(span = START_SPAN, dx = -50f, dy = 0f))
    }
}

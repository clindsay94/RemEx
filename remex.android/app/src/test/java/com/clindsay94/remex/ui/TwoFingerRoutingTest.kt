package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.TwoFingerGestureClassifier
import com.clindsay94.remex.ui.screens.TwoFingerIntent
import com.clindsay94.remex.ui.screens.TwoFingerRoute
import com.clindsay94.remex.ui.screens.TwoFingerRouting
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Where a two-finger frame goes (RemEx-pp4cm.10). Portrait opens fit-to-height at about 3x zoom,
 * and the old rule panned the view instead of scrolling the PC whenever zoom > 1.05. Two fingers
 * must scroll the PC at any zoom; only a pinch zooms and pans the view.
 */
class TwoFingerRoutingTest {

    private fun classifyThenRoute(span: Float, dx: Float, dy: Float): TwoFingerRoute =
            TwoFingerRouting.route(
                    TwoFingerGestureClassifier.classify(
                            startSpan = 400f,
                            startCenterX = 500f,
                            startCenterY = 800f,
                            span = span,
                            centerX = 500f + dx,
                            centerY = 800f + dy,
                            decisionSlopPx = 36f,
                    )
            )

    @Test
    fun `a scroll intent always scrolls the PC`() {
        assertEquals(TwoFingerRoute.ScrollHost, TwoFingerRouting.route(TwoFingerIntent.Scroll))
    }

    @Test
    fun `a pinch zooms and pans the view and never scrolls the PC`() {
        assertEquals(TwoFingerRoute.ZoomAndPan, TwoFingerRouting.route(TwoFingerIntent.Pinch))
    }

    @Test
    fun `an undecided gesture does nothing`() {
        assertEquals(TwoFingerRoute.None, TwoFingerRouting.route(null))
    }

    @Test
    fun `a portrait two-finger drag reaches the PC as a scroll`() {
        // The fit-to-height portrait case: fingers travel together with a little span wobble.
        assertEquals(TwoFingerRoute.ScrollHost, classifyThenRoute(span = 408f, dx = 0f, dy = -80f))
    }

    @Test
    fun `a spread does not reach the PC as a scroll`() {
        assertEquals(TwoFingerRoute.ZoomAndPan, classifyThenRoute(span = 460f, dx = 0f, dy = 0f))
    }
}

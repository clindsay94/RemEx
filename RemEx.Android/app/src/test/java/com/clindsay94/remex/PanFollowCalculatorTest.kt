package com.clindsay94.remex

import com.clindsay94.remex.ui.screens.PanFollowCalculator
import org.junit.Assert.assertEquals
import org.junit.Test

class PanFollowCalculatorTest {

    // 1000x1000 view box, 2x zoom -> maxPan = 1000*(2-1)/2 = 500. margin = 1000*0.15 = 150.
    private val w = 1000f
    private val h = 1000f

    @Test
    fun zoomOne_isNoOp() {
        val (px, py) = PanFollowCalculator.compute(
            cursorLocalX = 10f, cursorLocalY = 10f,
            panX = 42f, panY = -7f, zoom = 1f, imageWidth = w, imageHeight = h,
        )
        assertEquals(42f, px, 0.001f)
        assertEquals(-7f, py, 0.001f)
    }

    @Test
    fun cursorInCenter_doesNotPan() {
        val (px, py) = PanFollowCalculator.compute(
            cursorLocalX = 500f, cursorLocalY = 500f,
            panX = 0f, panY = 0f, zoom = 2f, imageWidth = w, imageHeight = h,
        )
        assertEquals(0f, px, 0.001f)
        assertEquals(0f, py, 0.001f)
    }

    @Test
    fun cursorNearLeftEdge_pansRight_increasingPanX() {
        // cursorLocalX=50 is inside the 150 deadzone -> target localX=150 -> delta +100.
        val (px, _) = PanFollowCalculator.compute(
            cursorLocalX = 50f, cursorLocalY = 500f,
            panX = 0f, panY = 0f, zoom = 2f, imageWidth = w, imageHeight = h,
        )
        assertEquals(100f, px, 0.001f)
    }

    @Test
    fun cursorNearRightEdge_pansLeft_decreasingPanX() {
        // cursorLocalX=950 > (1000-150)=850 -> target 850 -> delta -100.
        val (px, _) = PanFollowCalculator.compute(
            cursorLocalX = 950f, cursorLocalY = 500f,
            panX = 0f, panY = 0f, zoom = 2f, imageWidth = w, imageHeight = h,
        )
        assertEquals(-100f, px, 0.001f)
    }

    @Test
    fun cursorNearTopEdge_pansDown_increasingPanY() {
        // cursorLocalY=50 is inside the 150 deadzone -> target localY=150 -> delta +100.
        val (_, py) = PanFollowCalculator.compute(
            cursorLocalX = 500f, cursorLocalY = 50f,
            panX = 0f, panY = 0f, zoom = 2f, imageWidth = w, imageHeight = h,
        )
        assertEquals(100f, py, 0.001f)
    }

    @Test
    fun cursorInTopLeftCorner_pansBothAxesPositive() {
        // Left edge (x=50 -> +100 panX) AND top edge (y=50 -> +100 panY) in one call.
        val (px, py) = PanFollowCalculator.compute(
            cursorLocalX = 50f, cursorLocalY = 50f,
            panX = 0f, panY = 0f, zoom = 2f, imageWidth = w, imageHeight = h,
        )
        assertEquals(100f, px, 0.001f)
        assertEquals(100f, py, 0.001f)
    }

    @Test
    fun cursorNearBottomEdge_pansUp_decreasingPanY() {
        val (_, py) = PanFollowCalculator.compute(
            cursorLocalX = 500f, cursorLocalY = 950f,
            panX = 0f, panY = 0f, zoom = 2f, imageWidth = w, imageHeight = h,
        )
        assertEquals(-100f, py, 0.001f)
    }

    @Test
    fun resultIsClampedToMaxPan() {
        // Cursor pinned to far left (0) while pan already near the +max bound: delta would push
        // panX past +500, must clamp to +500.
        val (px, _) = PanFollowCalculator.compute(
            cursorLocalX = 0f, cursorLocalY = 500f,
            panX = 480f, panY = 0f, zoom = 2f, imageWidth = w, imageHeight = h,
        )
        assertEquals(500f, px, 0.001f)
    }

    @Test
    fun zeroImageSize_isNoOp() {
        val (px, py) = PanFollowCalculator.compute(
            cursorLocalX = 5f, cursorLocalY = 5f,
            panX = 3f, panY = 4f, zoom = 2f, imageWidth = 0f, imageHeight = 0f,
        )
        assertEquals(3f, px, 0.001f)
        assertEquals(4f, py, 0.001f)
    }
}

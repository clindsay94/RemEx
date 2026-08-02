package com.clindsay94.remex

import com.clindsay94.remex.widget.SparklinePoints
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the sparkline's coordinate arithmetic (RemEx-0akv).
 *
 * Glance cannot compose a Canvas, so the widget draws a bitmap - but the arithmetic needs no Canvas,
 * and these are the cases that actually break.
 */
class SparklinePointsTest {

    private val width = 100f
    private val height = 40f

    @Test
    fun `a rising series draws upward on screen`() {
        // Y IS INVERTED: screen coordinates grow DOWNWARD while values grow upward, so a high
        // sample must map to a SMALL y. Getting this wrong renders the chart upside down, which is
        // not obviously wrong on a sparkline - it just quietly tells the user their CPU is idle
        // when it is pinned.
        val points = SparklinePoints.map(listOf(0f, 50f, 100f), width, height)

        assertEquals(3, points.size)
        assertTrue("the low sample should sit near the bottom", points[0].y > points[1].y)
        assertTrue("the high sample should sit near the top", points[2].y < points[1].y)
        assertEquals(0f, points[2].y, 0.001f)
        assertEquals(height, points[0].y, 0.001f)
    }

    @Test
    fun `a flat series is a flat line through the middle, not NaN`() {
        // THE COMMON CASE, NOT AN EDGE CASE. An idle machine reports the same CPU percentage for
        // thirty consecutive samples. Scaling by the range divides by zero and produces NaN for
        // every point - the bitmap draws nothing, and the widget looks broken exactly when the
        // machine is behaving.
        val points = SparklinePoints.map(listOf(7f, 7f, 7f, 7f), width, height)

        assertEquals(4, points.size)
        for (point in points) {
            assertTrue("y was NaN or infinite: ${point.y}", point.y.isFinite())
            assertEquals(height / 2f, point.y, 0.001f)
        }
    }

    @Test
    fun `a single sample is a dot rather than a divide by zero`() {
        // Spreading points across the width divides by (size - 1), which is zero here. That yields
        // NaN or Infinity and draws nothing - or worse, garbage that looks like corrupted data.
        val points = SparklinePoints.map(listOf(42f), width, height)

        assertEquals(1, points.size)
        assertTrue(points[0].x.isFinite() && points[0].y.isFinite())
        assertEquals(width / 2f, points[0].x, 0.001f)
    }

    @Test
    fun `no samples means no points`() {
        assertTrue(SparklinePoints.map(emptyList(), width, height).isEmpty())
    }

    @Test
    fun `a zero or negative canvas yields nothing rather than throwing`() {
        // A widget can be measured at zero during layout, and a chart helper that throws takes the
        // whole widget update down rather than skipping one frame.
        assertTrue(SparklinePoints.map(listOf(1f, 2f), 0f, height).isEmpty())
        assertTrue(SparklinePoints.map(listOf(1f, 2f), width, 0f).isEmpty())
        assertTrue(SparklinePoints.map(listOf(1f, 2f), -5f, height).isEmpty())
    }

    @Test
    fun `points span the full width, first to last`() {
        val points = SparklinePoints.map(listOf(1f, 2f, 3f, 4f, 5f), width, height)

        assertEquals(0f, points.first().x, 0.001f)
        assertEquals(width, points.last().x, 0.001f)
    }

    @Test
    fun `x increases monotonically so the polyline never doubles back`() {
        val points = SparklinePoints.map((1..30).map { it.toFloat() }, width, height)

        for (i in 1 until points.size) {
            assertTrue("x went backwards at $i", points[i].x > points[i - 1].x)
        }
    }

    @Test
    fun `the stroke inset keeps an extreme value off the canvas edge`() {
        // Without it, a line at the maximum is drawn centred on y=0 and the top half of the stroke
        // is clipped away - the peak looks thinner than the rest of the line.
        val inset = 2f
        val points = SparklinePoints.map(listOf(0f, 100f), width, height, strokeInset = inset)

        assertEquals(inset, points[1].y, 0.001f)
        assertEquals(height - inset, points[0].y, 0.001f)
        assertEquals(inset, points[0].x, 0.001f)
        assertEquals(width - inset, points[1].x, 0.001f)
    }

    @Test
    fun `an inset larger than the canvas yields nothing rather than inverted bounds`() {
        // Inset is applied to both edges, so a large one can cross over. Inverted bounds would
        // produce points outside the bitmap and a stroke drawn who-knows-where.
        assertTrue(SparklinePoints.map(listOf(1f, 2f), width, 4f, strokeInset = 10f).isEmpty())
    }

    @Test
    fun `negative samples are handled, since not every sensor starts at zero`() {
        // Temperature deltas and clock offsets go negative. Normalizing against min rather than
        // assuming a zero floor is what makes that work.
        val points = SparklinePoints.map(listOf(-10f, 0f, 10f), width, height)

        assertEquals(height, points[0].y, 0.001f)
        assertEquals(height / 2f, points[1].y, 0.001f)
        assertEquals(0f, points[2].y, 0.001f)
    }
}

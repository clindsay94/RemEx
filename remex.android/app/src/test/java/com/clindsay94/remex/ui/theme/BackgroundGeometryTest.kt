package com.clindsay94.remex.ui.theme

import kotlin.math.abs
import kotlin.math.hypot
import kotlin.math.sqrt
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** The arithmetic behind the still textures (RemEx-pp4cm.17). */
class BackgroundGeometryTest {

    @Test
    fun `contour of a circle field lies on the circle`() {
        val field = { x: Float, y: Float -> hypot(x - 0.5f, y - 0.5f) }
        val segments = BackgroundGeometry.contourSegments(cells = 64, level = 0.3f, field = field)
        assertTrue("the contour is not empty", segments.size > 50)
        for (s in segments) {
            assertEquals(0.3f, hypot(s.x0 - 0.5f, s.y0 - 0.5f), 0.01f)
            assertEquals(0.3f, hypot(s.x1 - 0.5f, s.y1 - 0.5f), 0.01f)
        }
    }

    @Test
    fun `a level outside the field's range has no contour`() {
        assertTrue(BackgroundGeometry.contourSegments(16, 5f) { _, _ -> 1f }.isEmpty())
    }

    @Test
    fun `the topographic field repeats every tile so the texture has no seam`() {
        for (u in listOf(0f, 0.13f, 0.5f, 0.87f)) for (v in listOf(0f, 0.29f, 0.71f)) {
            val here = BackgroundGeometry.topographicField(u, v)
            assertEquals(here, BackgroundGeometry.topographicField(u + 1f, v), 1e-3f)
            assertEquals(here, BackgroundGeometry.topographicField(u, v + 1f), 1e-3f)
        }
        // And it has real relief: contour levels used by the tile all cross it.
        val values = (0..40).flatMap { i -> (0..40).map { j -> BackgroundGeometry.topographicField(i / 40f, j / 40f) } }
        assertTrue(values.min() < -0.6f && values.max() > 0.6f)
    }

    @Test
    fun `a regular hexagon has six equal sides`() {
        val side = 10f
        val v = BackgroundGeometry.hexagonVertices(0f, 0f, side, (sqrt(3.0) * side).toFloat())
        assertEquals(6, v.size)
        for (i in 0 until 6) {
            val a = v[i]
            val b = v[(i + 1) % 6]
            assertEquals(side, hypot(a.first - b.first, a.second - b.second), 0.01f)
        }
    }

    @Test
    fun `hexagon centres tile the plane two per repeat`() {
        val side = 22f
        val h = (sqrt(3.0) * side).toFloat()
        val centres = BackgroundGeometry.hexagonCentres(side, 3 * side, h)
        assertTrue(centres.isNotEmpty())
        // Offset columns are half a row apart, so no two centres coincide.
        assertEquals(centres.size, centres.map { (it.first * 1000).toInt() to (it.second * 1000).toInt() }.toSet().size)
        assertTrue(centres.any { it.first == 0f && it.second == 0f })
        assertTrue(centres.any { abs(it.first - 1.5f * side) < 1e-3f && abs(it.second - h / 2f) < 1e-3f })
    }
}

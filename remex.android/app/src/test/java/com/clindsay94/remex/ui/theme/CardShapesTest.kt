package com.clindsay94.remex.ui.theme

import androidx.compose.foundation.shape.CutCornerShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.ui.screens.DashboardShapes
import org.junit.Assert.assertEquals
import org.junit.Test

/** RemEx-kq10x.5: only content-safe tile shapes, and every removed shape reads as the rounded rectangle. */
class CardShapesTest {

    @Test
    fun `offered shapes are the rounded rectangle first then the cut corner`() {
        assertEquals(listOf(CardShapes.ROUNDED_RECTANGLE, CardShapes.CUT_CORNER), CardShapes.options)
    }

    @Test
    fun `every removed morph shape falls back to the rounded rectangle`() {
        // 0..23 were Circle, Square, Triangle, Diamond, Pentagon ... Clover, Sunny, Pixel Circle.
        for (old in 0..23) {
            assertEquals("old index $old", CardShapes.ROUNDED_RECTANGLE, CardShapes.sanitize(old.toFloat()))
        }
    }

    @Test
    fun `fractional morph positions, inherit, NaN and out of range values fall back too`() {
        listOf(5.3f, 18.4f, 23.4f, -1f, 26f, 99f, Float.NaN, Float.POSITIVE_INFINITY).forEach {
            assertEquals("value $it", CardShapes.ROUNDED_RECTANGLE, CardShapes.sanitize(it))
        }
    }

    @Test
    fun `kept shapes read back as themselves`() {
        assertEquals(CardShapes.ROUNDED_RECTANGLE, CardShapes.sanitize(24f))
        assertEquals(CardShapes.CUT_CORNER, CardShapes.sanitize(25f))
        assertEquals(CardShapes.CUT_CORNER, CardShapes.sanitize(25.2f)) // mid-animation snaps
    }

    @Test
    fun `cardShape renders a saved removed shape as a rounded rectangle, never a morph`() {
        assertEquals(RoundedCornerShape(16.dp), cardShape(18f, 16)) // legacy Clover4Leaf
        assertEquals(RoundedCornerShape(16.dp), cardShape(DashboardShapes.SHAPE_PRESET_INHERIT, 16))
        assertEquals(CutCornerShape(8.dp), cardShape(CardShapes.CUT_CORNER, 16))
    }

    @Test
    fun `default corner radius matches the PC default`() {
        // remex.core/Models/DashboardProfile.cs: CornerRadius = 16.
        assertEquals(16, CardShapes.DEFAULT_CORNER_RADIUS_DP)
    }

    @Test
    fun `every dashboard category defaults to the rounded rectangle`() {
        DashboardShapes.CardCategory.entries.forEach {
            assertEquals(it.name, CardShapes.ROUNDED_RECTANGLE, DashboardShapes.defaultShapeFor(it))
        }
    }
}

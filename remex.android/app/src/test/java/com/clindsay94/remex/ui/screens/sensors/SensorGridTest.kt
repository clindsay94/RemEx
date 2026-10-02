package com.clindsay94.remex.ui.screens.sensors

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The Sensors grid packer and geometry (RemEx-wqo7a.7): no overlaps, aligned edges, 2 and 4 columns. */
class SensorGridTest {

    private val mixed =
            listOf(
                    "a" to CardSpan.ONE_BY_ONE,
                    "b" to CardSpan.TWO_BY_TWO,
                    "c" to CardSpan.ONE_BY_ONE,
                    "d" to CardSpan.TWO_BY_ONE,
                    "e" to CardSpan.ONE_BY_ONE,
                    "f" to CardSpan.TWO_BY_TWO,
                    "g" to CardSpan.ONE_BY_ONE,
                    "h" to CardSpan.ONE_BY_ONE,
            )

    private fun cells(p: GridPlacement): List<Pair<Int, Int>> =
            (p.row until p.row + p.rowSpan).flatMap { r -> (p.col until p.col + p.colSpan).map { c -> r to c } }

    private fun assertNoOverlapsWithin(placements: List<GridPlacement>, columns: Int) {
        val all = placements.flatMap(::cells)
        assertEquals("two cards share a cell: $placements", all.size, all.toSet().size)
        placements.forEach { p ->
            assertTrue("$p starts off the grid", p.col >= 0 && p.row >= 0)
            assertTrue("$p runs past column $columns", p.col + p.colSpan <= columns)
        }
    }

    @Test
    fun `two columns - every card placed once, none overlapping`() {
        val placements = SensorGrid.pack(mixed, columns = 2)
        assertEquals(mixed.map { it.first }, placements.map { it.id })
        assertNoOverlapsWithin(placements, 2)
    }

    @Test
    fun `four columns - every card placed once, none overlapping`() {
        val placements = SensorGrid.pack(mixed, columns = 4)
        assertEquals(mixed.map { it.first }, placements.map { it.id })
        assertNoOverlapsWithin(placements, 4)
    }

    @Test
    fun `first fit fills an earlier gap with a later one-cell card`() {
        // a(1x1) at (0,0); b(2x1) does not fit beside it in 2 columns, so it goes to row 1 and leaves
        // (0,1) free for c.
        val placements = SensorGrid.pack(listOf("a" to CardSpan.ONE_BY_ONE, "b" to CardSpan.TWO_BY_ONE, "c" to CardSpan.ONE_BY_ONE), 2)
        assertEquals(GridPlacement("a", 0, 0, 1, 1), placements[0])
        assertEquals(GridPlacement("b", 0, 1, 2, 1), placements[1])
        assertEquals(GridPlacement("c", 1, 0, 1, 1), placements[2])
    }

    @Test
    fun `a span wider than the grid is clamped to it`() {
        val placements = SensorGrid.pack(listOf("wide" to CardSpan.TWO_BY_TWO), columns = 1)
        assertEquals(GridPlacement("wide", 0, 0, 1, 2), placements.single())
    }

    @Test
    fun `edges align - every card edge sits on a column or row line`() {
        for (columns in listOf(2, 4)) {
            val width = if (columns == 2) 380f else 760f
            val gutter = 12f
            val cell = SensorGrid.cellWidth(width, columns, gutter)
            val row = SensorGrid.rowHeight(cell)
            val columnLefts = (0 until columns).map { SensorGrid.x(it, cell, gutter) }
            val columnRights = columnLefts.map { it + cell }
            val placements = SensorGrid.pack(mixed, columns)
            val rows = SensorGrid.rowCount(placements)
            val rowTops = (0 until rows).map { SensorGrid.y(it, row, gutter) }
            val rowBottoms = rowTops.map { it + row }
            placements.forEach { p ->
                val left = SensorGrid.x(p.col, cell, gutter)
                val right = left + SensorGrid.extent(p.colSpan, cell, gutter)
                val top = SensorGrid.y(p.row, row, gutter)
                val bottom = top + SensorGrid.extent(p.rowSpan, row, gutter)
                assertTrue("$p left edge off the column lines", columnLefts.any { near(it, left) })
                assertTrue("$p right edge off the column lines", columnRights.any { near(it, right) })
                assertTrue("$p top edge off the row lines", rowTops.any { near(it, top) })
                assertTrue("$p bottom edge off the row lines", rowBottoms.any { near(it, bottom) })
            }
            // The rightmost column ends exactly at the available width.
            assertTrue(near(columnRights.last(), width))
            assertTrue(near(SensorGrid.height(rows, row, gutter), rowBottoms.last()))
        }
    }

    @Test
    fun `columns switch at the medium width class`() {
        assertEquals(2, SensorGrid.columnsFor(411f))
        assertEquals(2, SensorGrid.columnsFor(599.9f))
        assertEquals(4, SensorGrid.columnsFor(600f))
        assertEquals(4, SensorGrid.columnsFor(840f))
    }

    @Test
    fun `row height is 0_85 of a cell`() {
        assertTrue(near(SensorGrid.rowHeight(200f), 170f))
    }

    @Test
    fun `indexAt finds the card under a point and null over a gap`() {
        val placements = SensorGrid.pack(listOf("a" to CardSpan.ONE_BY_ONE, "b" to CardSpan.TWO_BY_ONE), 2)
        val cell = 100f
        val row = 85f
        assertEquals(0, SensorGrid.indexAt(placements, 50f, 40f, cell, row, 12f))
        assertEquals(1, SensorGrid.indexAt(placements, 150f, 130f, cell, row, 12f))
        // (1,0) is empty: b went to the next row.
        assertNull(SensorGrid.indexAt(placements, 160f, 40f, cell, row, 12f))
    }

    @Test
    fun `the resize cycle is 1x1 then 2x1 then 2x2 then back`() {
        assertEquals(CardSpan.TWO_BY_ONE, CardSpan.ONE_BY_ONE.next())
        assertEquals(CardSpan.TWO_BY_TWO, CardSpan.TWO_BY_ONE.next())
        assertEquals(CardSpan.ONE_BY_ONE, CardSpan.TWO_BY_TWO.next())
        assertFalse(CardSpan.entries.any { it.cols == 1 && it.rows == 2 })
    }

    private fun near(a: Float, b: Float) = kotlin.math.abs(a - b) < 0.01f
}

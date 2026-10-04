package com.clindsay94.remex.ui.screens.sensors

import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.mutablePreferencesOf
import com.clindsay94.remex.data.SettingsManager
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** The Sensors "Grid width" choice (RemEx-pp4cm.16): Auto, 2, 3 or 4 columns. */
class GridWidthTest {

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
                    "i" to CardSpan.TWO_BY_ONE,
            )

    private fun cells(p: GridPlacement): List<Pair<Int, Int>> =
            (p.row until p.row + p.rowSpan).flatMap { r -> (p.col until p.col + p.colSpan).map { c -> r to c } }

    private fun assertSound(placements: List<GridPlacement>, columns: Int) {
        val all = placements.flatMap(::cells)
        assertEquals("two cards share a cell at $columns columns: $placements", all.size, all.toSet().size)
        placements.forEach { assertTrue("$it runs past column $columns", it.col >= 0 && it.col + it.colSpan <= columns) }
    }

    // --- packing at 2, 3 and 4 columns ----------------------------------------------------

    @Test
    fun `every column count places every card once, in order, with no overlap`() {
        for (columns in 2..4) {
            val placements = SensorGrid.pack(mixed, columns)
            assertEquals("order at $columns columns", mixed.map { it.first }, placements.map { it.id })
            assertSound(placements, columns)
        }
    }

    @Test
    fun `three columns - a wide card that does not fit leaves its gap for a later small card`() {
        // a(1x1) at (0,0); b(2x1) fits beside it at (1,0)? No: only 2 cells remain in row 0 -> col 1..2.
        val placements = SensorGrid.pack(
                listOf("a" to CardSpan.ONE_BY_ONE, "b" to CardSpan.TWO_BY_ONE, "c" to CardSpan.TWO_BY_ONE, "d" to CardSpan.ONE_BY_ONE),
                columns = 3,
        )
        assertEquals(GridPlacement("a", 0, 0, 1, 1), placements[0])
        assertEquals(GridPlacement("b", 1, 0, 2, 1), placements[1]) // fills the rest of row 0
        assertEquals(GridPlacement("c", 0, 1, 2, 1), placements[2]) // next row, left
        assertEquals(GridPlacement("d", 2, 1, 1, 1), placements[3]) // the one cell c left
    }

    @Test
    fun `a 2x2 card at three columns leaves a one-cell strip that small cards fill`() {
        val placements = SensorGrid.pack(
                listOf("big" to CardSpan.TWO_BY_TWO, "s1" to CardSpan.ONE_BY_ONE, "s2" to CardSpan.ONE_BY_ONE, "s3" to CardSpan.ONE_BY_ONE),
                columns = 3,
        )
        assertEquals(GridPlacement("big", 0, 0, 2, 2), placements[0])
        assertEquals(GridPlacement("s1", 2, 0, 1, 1), placements[1])
        assertEquals(GridPlacement("s2", 2, 1, 1, 1), placements[2])
        assertEquals(GridPlacement("s3", 0, 2, 1, 1), placements[3])
    }

    // --- stability when the count changes -------------------------------------------------

    @Test
    fun `changing the column count and back gives the original layout - spans are never rewritten`() {
        val at4 = SensorGrid.pack(mixed, 4)
        // Packing is a pure function of the stored spans and the count: nothing carries over.
        listOf(2, 3, 1, 2, 3).forEach { SensorGrid.pack(mixed, it) }
        assertEquals(at4, SensorGrid.pack(mixed, 4))
    }

    @Test
    fun `a span wider than the grid clamps and the order holds`() {
        // One column is below the picker's range, but a phone in a narrow split window can get there
        // through Auto: every 2-wide card must clamp to the grid, never overflow it.
        val placements = SensorGrid.pack(mixed, 1)
        assertEquals(mixed.map { it.first }, placements.map { it.id })
        assertSound(placements, 1)
        assertTrue(placements.all { it.colSpan == 1 })
        // Heights are untouched by clamping the width.
        assertEquals(mixed.map { it.second.rows }, placements.map { it.rowSpan })
    }

    @Test
    fun `adding a card never moves the ones before it`() {
        for (columns in 2..4) {
            for (n in 1 until mixed.size) {
                val shorter = SensorGrid.pack(mixed.take(n), columns)
                val longer = SensorGrid.pack(mixed.take(n + 1), columns)
                assertEquals("$columns columns, $n cards", shorter, longer.take(n))
            }
        }
    }

    @Test
    fun `removing the last card keeps the rest where they were`() {
        for (columns in 2..4) {
            val full = SensorGrid.pack(mixed, columns)
            assertEquals(full.dropLast(1), SensorGrid.pack(mixed.dropLast(1), columns))
        }
    }

    // --- the choice itself ----------------------------------------------------------------

    @Test
    fun `Auto is the width-class rule, the others ignore the window`() {
        listOf(360f, 411f, 599.9f, 600f, 840f).forEach {
            assertEquals(SensorGrid.columnsFor(it), SensorGrid.columnsFor(it, GridWidth.AUTO))
            assertEquals(2, SensorGrid.columnsFor(it, GridWidth.TWO))
            assertEquals(3, SensorGrid.columnsFor(it, GridWidth.THREE))
            assertEquals(4, SensorGrid.columnsFor(it, GridWidth.FOUR))
        }
        assertEquals(2, SensorGrid.columnsFor(411f, GridWidth.AUTO)) // a phone, as before
        assertEquals(4, SensorGrid.columnsFor(840f, GridWidth.AUTO)) // a tablet, as before
    }

    @Test
    fun `stored numbers are distinct and read back`() {
        assertEquals(GridWidth.entries.size, GridWidth.entries.map { it.stored }.toSet().size)
        GridWidth.entries.forEach { assertEquals(it, GridWidth.fromStored(it.stored)) }
    }

    @Test
    fun `an unknown or missing stored value reads as Auto`() {
        listOf(null, -1, 1, 5, 99, Int.MAX_VALUE).forEach { assertEquals("value $it", GridWidth.AUTO, GridWidth.fromStored(it)) }
    }

    // --- persistence round trip -----------------------------------------------------------

    @Test
    fun `the choice survives a write and a read through DataStore preferences`() {
        GridWidth.entries.forEach { width ->
            val prefs = mutablePreferencesOf()
            SettingsManager.writeSensorGridWidth(prefs, width)
            assertEquals(width, SettingsManager.readSensorGridWidth(prefs))
        }
    }

    @Test
    fun `a fresh install reads Auto, and a later write replaces the earlier choice`() {
        val prefs = mutablePreferencesOf()
        assertEquals(GridWidth.AUTO, SettingsManager.readSensorGridWidth(prefs))
        SettingsManager.writeSensorGridWidth(prefs, GridWidth.THREE)
        SettingsManager.writeSensorGridWidth(prefs, GridWidth.TWO)
        assertEquals(GridWidth.TWO, SettingsManager.readSensorGridWidth(prefs))
    }

    @Test
    fun `the stored key and numbers are the contract`() {
        val prefs = mutablePreferencesOf()
        SettingsManager.writeSensorGridWidth(prefs, GridWidth.FOUR)
        assertEquals(4, prefs[intPreferencesKey("sensor_grid_columns")])
        SettingsManager.writeSensorGridWidth(prefs, GridWidth.AUTO)
        assertEquals(0, prefs[intPreferencesKey("sensor_grid_columns")])
        // A value this build does not know (say, from a newer one) is Auto rather than a crash.
        prefs[intPreferencesKey("sensor_grid_columns")] = 7
        assertEquals(GridWidth.AUTO, SettingsManager.readSensorGridWidth(prefs))
        assertNotEquals(GridWidth.AUTO, GridWidth.fromStored(2))
    }
}

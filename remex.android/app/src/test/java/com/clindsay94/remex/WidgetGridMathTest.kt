package com.clindsay94.remex

import com.clindsay94.remex.widget.WidgetGridMath
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the widget grid sizing (live-check A2, A4, A5, A6).
 *
 * The widgets used to compute "how many items fit" and silently drop the rest, and sized cells with
 * a floor that was taller than the minimum widget, so the bottom row was cut off. The grids now scroll,
 * so the math only has to answer two questions: how many columns, and how tall a cell can be while a
 * whole row still fits the space it is given.
 */
class WidgetGridMathTest {

    @Test
    fun `columns never exceed what Glance's fixed grid accepts`() {
        assertEquals(WidgetGridMath.MAX_GRID_COLUMNS, WidgetGridMath.columns(2000f, 40f))
        assertEquals(5, WidgetGridMath.MAX_GRID_COLUMNS)
    }

    @Test
    fun `columns honour a tighter caller cap`() {
        assertEquals(4, WidgetGridMath.columns(1000f, 100f, maxColumns = 4))
    }

    @Test
    fun `columns is at least one even when nothing fits`() {
        assertEquals(1, WidgetGridMath.columns(0f, 90f))
        assertEquals(1, WidgetGridMath.columns(50f, 90f))
        assertEquals(1, WidgetGridMath.columns(-10f, 90f))
        assertEquals(1, WidgetGridMath.columns(200f, 0f))
    }

    @Test
    fun `columns floor the fit rather than round it up`() {
        assertEquals(2, WidgetGridMath.columns(199f, 94f))
        assertEquals(3, WidgetGridMath.columns(282f, 94f))
    }

    @Test
    fun `a single row at the minimum widget height fits whole`() {
        // Remote control at its 40dp minimum: 4dp outer padding top and bottom leaves 32dp, and
        // each cell has 2dp padding above and below the button.
        val gridHeight = 40f - 2 * 4f
        val button = WidgetGridMath.buttonHeight(gridHeight, rows = 3, cellPaddingDp = 2f)
        assertTrue("button $button + padding must fit in $gridHeight", button + 4f <= gridHeight)
        assertTrue("button must stay tappable, got $button", button >= WidgetGridMath.BUTTON_FLOOR_DP)
    }

    @Test
    fun `rows that all fit stretch to fill, capped`() {
        // 2 rows in 120dp: 60 per row, minus 4 padding = 56.
        assertEquals(56f, WidgetGridMath.buttonHeight(120f, rows = 2, cellPaddingDp = 2f), 0.001f)
        // 1 row in 300dp would be absurd; cap it.
        assertEquals(
            WidgetGridMath.BUTTON_STRETCH_MAX_DP,
            WidgetGridMath.buttonHeight(300f, rows = 1, cellPaddingDp = 2f),
            0.001f,
        )
    }

    @Test
    fun `rows that do not all fit keep a comfortable height and scroll`() {
        // 5 rows in 100dp would be 16dp buttons. Keep them comfortable; the grid scrolls.
        assertEquals(
            WidgetGridMath.BUTTON_COMFORT_DP,
            WidgetGridMath.buttonHeight(100f, rows = 5, cellPaddingDp = 2f),
            0.001f,
        )
    }

    @Test
    fun `comfortable height still shrinks to the space when even one row would not fit`() {
        // 30dp of grid, 2dp padding: 26dp button, below comfort but it is the whole row.
        assertEquals(26f, WidgetGridMath.buttonHeight(30f, rows = 4, cellPaddingDp = 2f), 0.001f)
    }

    @Test
    fun `zero rows does not divide by zero`() {
        val h = WidgetGridMath.buttonHeight(100f, rows = 0, cellPaddingDp = 2f)
        assertTrue(h.isFinite())
    }

    @Test
    fun `icon downscale keeps small icons and bounds large ones`() {
        assertEquals(96 to 48, WidgetGridMath.scaledIconSize(96, 48, maxPx = 192))
        assertEquals(192 to 96, WidgetGridMath.scaledIconSize(512, 256, maxPx = 192))
        assertEquals(96 to 192, WidgetGridMath.scaledIconSize(256, 512, maxPx = 192))
        assertEquals(1 to 192, WidgetGridMath.scaledIconSize(1, 4000, maxPx = 192))
    }

    // Live-check A7: a fixed 224px cap sat just under the host's 256px icons, and ~35 selected apps
    // on a 720x1600 xhdpi phone blew the launcher's bitmap ceiling ("Can't load widget").

    @Test
    fun `icon px follows the cell dp and the screen density`() {
        assertEquals(96, WidgetGridMath.iconPx(48f, 2f))
        assertEquals(144, WidgetGridMath.iconPx(48f, 3f))
        assertEquals(112, WidgetGridMath.iconPx(56f, 2f))
        assertEquals(63, WidgetGridMath.iconPx(42f, 1.5f))
        assertEquals(74, WidgetGridMath.iconPx(56f, 1.3125f)) // tvdpi-like: rounds UP, 73.5 -> 74
        assertEquals(1, WidgetGridMath.iconPx(0f, 2f))
        assertEquals(1, WidgetGridMath.iconPx(48f, 0f))
    }

    @Test
    fun `bitmap budget is the launcher's screen x 4 x 1point5 ceiling`() {
        assertEquals(6_912_000L, WidgetGridMath.bitmapBudgetBytes(720, 1600))
        assertEquals(27_648_000L, WidgetGridMath.bitmapBudgetBytes(1440, 3200))
        assertEquals(0L, WidgetGridMath.bitmapBudgetBytes(0, 1600))
        assertEquals((6_912_000L * WidgetGridMath.ICON_BUDGET_FRACTION).toLong(),
            WidgetGridMath.iconBudgetBytes(720, 1600))
    }

    @Test
    fun `thirty-five host-size icons on the reported phone no longer exceed the ceiling`() {
        val budget = WidgetGridMath.iconBudgetBytes(720, 1600)
        // Old behaviour: 224px caps -> 35 x 224 x 224 x 4 = 7,024,640 bytes, over the WHOLE 6.9 MB.
        assertTrue(35L * 224 * 224 * 4 > WidgetGridMath.bitmapBudgetBytes(720, 1600))
        // Now: sized to the largest cell at xhdpi (56dp -> 112px), all 35 fit inside the icon share.
        val px = WidgetGridMath.iconPx(56f, 2f)
        val bytes = List<Long?>(35) { px.toLong() * px * 4 }
        assertTrue(WidgetGridMath.iconsWithinBudget(bytes, budget).all { it })
    }

    @Test
    fun `icons past the budget fall back without failing the rest`() {
        val drawn = WidgetGridMath.iconsWithinBudget(listOf(400L, 400L, null, 300L, 100L, 1L), 900L)
        assertEquals(listOf(true, true, false, false, true, false), drawn)
    }

    @Test
    fun `however many apps are selected the admitted icons stay within budget`() {
        val budget = 1_000_000L
        val bytes = List<Long?>(500) { 50_176L }
        val drawn = WidgetGridMath.iconsWithinBudget(bytes, budget)
        val spent = bytes.zip(drawn).filter { it.second }.sumOf { it.first!! }
        assertTrue(spent <= budget)
        assertEquals(19, drawn.count { it })
    }
}

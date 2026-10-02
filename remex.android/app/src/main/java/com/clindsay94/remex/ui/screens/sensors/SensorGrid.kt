package com.clindsay94.remex.ui.screens.sensors

/**
 * How many grid cells a Sensors card covers (RemEx-wqo7a.7). Three sizes only, cycled in edit mode
 * by [next]: one cell, a wide card, a big card.
 */
enum class CardSpan(val cols: Int, val rows: Int) {
    ONE_BY_ONE(1, 1),
    TWO_BY_ONE(2, 1),
    TWO_BY_TWO(2, 2);

    /** The resize cycle: 1x1 -> 2x1 -> 2x2 -> 1x1. */
    fun next(): CardSpan = entries[(ordinal + 1) % entries.size]

    companion object {
        /** The closest span to a saved column/row count; anything unreadable is one cell. */
        fun of(cols: Int, rows: Int): CardSpan =
                when {
                    cols >= 2 && rows >= 2 -> TWO_BY_TWO
                    cols >= 2 -> TWO_BY_ONE
                    else -> ONE_BY_ONE
                }
    }
}

/** One card's cell rectangle: top-left [col]/[row], covering [colSpan] x [rowSpan] cells. */
data class GridPlacement(val id: String, val col: Int, val row: Int, val colSpan: Int, val rowSpan: Int)

/**
 * The Sensors grid (RemEx-wqo7a.7): an aligned grid that replaced the free-form canvas, where cards
 * could overlap, drift off-screen and never quite line up.
 *
 * Pure: the packer and the geometry are where the alignment either holds or does not, so both are
 * proven off-device (SensorGridTest). The screen only multiplies these numbers out.
 */
object SensorGrid {

    const val COMPACT_COLUMNS = 2
    const val EXPANDED_COLUMNS = 4

    /** Window width, in dp, from which the grid has [EXPANDED_COLUMNS]: the M3 medium width class. */
    const val EXPANDED_MIN_WIDTH_DP = 600f

    /** Row height as a fraction of a cell's width, so a 1x1 card is slightly wider than tall. */
    const val ROW_HEIGHT_RATIO = 0.85f

    fun columnsFor(widthDp: Float): Int = if (widthDp >= EXPANDED_MIN_WIDTH_DP) EXPANDED_COLUMNS else COMPACT_COLUMNS

    /**
     * First-fit, row-major: each card, in order, takes the first top-left cell (scanning rows top to
     * bottom, columns left to right) where its whole rectangle is free. A wide card that does not fit
     * at the end of a row leaves the gap for a later one-cell card, so the grid stays dense. A span
     * wider than the grid is clamped to it. No two placements ever overlap.
     */
    fun pack(items: List<Pair<String, CardSpan>>, columns: Int): List<GridPlacement> {
        require(columns > 0) { "columns must be positive" }
        val occupied = ArrayList<BooleanArray>()
        fun free(row: Int, col: Int): Boolean = row >= occupied.size || !occupied[row][col]
        fun fits(row: Int, col: Int, colSpan: Int, rowSpan: Int): Boolean {
            for (r in row until row + rowSpan) for (c in col until col + colSpan) if (!free(r, c)) return false
            return true
        }

        val placements = ArrayList<GridPlacement>(items.size)
        for ((id, span) in items) {
            val colSpan = span.cols.coerceIn(1, columns)
            val rowSpan = span.rows.coerceAtLeast(1)
            var row = 0
            var placedCol = -1
            while (placedCol < 0) {
                for (col in 0..columns - colSpan) {
                    if (fits(row, col, colSpan, rowSpan)) {
                        placedCol = col
                        break
                    }
                }
                if (placedCol < 0) row++
            }
            while (occupied.size < row + rowSpan) occupied += BooleanArray(columns)
            for (r in row until row + rowSpan) for (c in placedCol until placedCol + colSpan) occupied[r][c] = true
            placements += GridPlacement(id, placedCol, row, colSpan, rowSpan)
        }
        return placements
    }

    /** Rows the packed grid uses: the bottom edge of its lowest card. */
    fun rowCount(placements: List<GridPlacement>): Int = placements.maxOfOrNull { it.row + it.rowSpan } ?: 0

    /** Width of one cell when [columns] cells and the gutters between them fill [availableWidth]. */
    fun cellWidth(availableWidth: Float, columns: Int, gutter: Float): Float =
            ((availableWidth - gutter * (columns - 1)) / columns).coerceAtLeast(0f)

    fun rowHeight(cellWidth: Float): Float = cellWidth * ROW_HEIGHT_RATIO

    /** Left edge of column [col]. */
    fun x(col: Int, cellWidth: Float, gutter: Float): Float = col * (cellWidth + gutter)

    /** Top edge of row [row]. */
    fun y(row: Int, rowHeight: Float, gutter: Float): Float = row * (rowHeight + gutter)

    /** Size covering [span] cells and the [span] - 1 gutters inside it. */
    fun extent(span: Int, cell: Float, gutter: Float): Float = span * cell + (span - 1) * gutter

    /** Total height of [rows] rows with gutters between them (none after the last). */
    fun height(rows: Int, rowHeight: Float, gutter: Float): Float = if (rows <= 0) 0f else extent(rows, rowHeight, gutter)

    /**
     * The grid index a dragged card should move to: the card whose rectangle holds the point
     * ([pointX], [pointY]), or null over a gap. [placements] is in card order, so the index is the
     * position in it.
     */
    fun indexAt(
            placements: List<GridPlacement>,
            pointX: Float,
            pointY: Float,
            cellWidth: Float,
            rowHeight: Float,
            gutter: Float,
    ): Int? {
        placements.forEachIndexed { index, p ->
            val left = x(p.col, cellWidth, gutter)
            val top = y(p.row, rowHeight, gutter)
            val right = left + extent(p.colSpan, cellWidth, gutter)
            val bottom = top + extent(p.rowSpan, rowHeight, gutter)
            if (pointX in left..right && pointY in top..bottom) return index
        }
        return null
    }
}

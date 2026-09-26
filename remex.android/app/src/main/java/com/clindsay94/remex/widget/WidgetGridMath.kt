package com.clindsay94.remex.widget

import kotlin.math.ceil
import kotlin.math.max
import kotlin.math.roundToInt

/**
 * Sizing arithmetic for the widget grids, kept pure so it can be unit-tested (live-check A2, A4-A6).
 *
 * The widgets used to work out how many items fit their current size and silently drop the rest,
 * and sized cells with a floor taller than the widget's own minimum height, so the bottom row was
 * clipped. They now lay out through Glance's `LazyVerticalGrid`, which scrolls, so everything the
 * user selected is reachable. What is left for this object is: how many columns, and how tall a
 * button can be while one whole row still fits the space it is given.
 *
 * Values are plain dp floats rather than `Dp` so the tests need nothing from Compose.
 */
internal object WidgetGridMath {

    /** `GridCells.Fixed` in Glance accepts 1..5 columns (the RemoteViews GridView ceiling). */
    const val MAX_GRID_COLUMNS: Int = 5

    /** Below this a button stops being comfortable to hit; a grid that cannot fit it scrolls. */
    const val BUTTON_COMFORT_DP: Float = 36f

    /** Rows that all fit stretch to fill the widget, but not past this. */
    const val BUTTON_STRETCH_MAX_DP: Float = 60f

    /** The smallest button ever produced, however little space there is. */
    const val BUTTON_FLOOR_DP: Float = 20f

    /** How many columns of at least [minCellWidthDp] fit in [availableWidthDp], within 1..[maxColumns]. */
    fun columns(availableWidthDp: Float, minCellWidthDp: Float, maxColumns: Int = MAX_GRID_COLUMNS): Int {
        val cap = maxColumns.coerceIn(1, MAX_GRID_COLUMNS)
        if (availableWidthDp <= 0f || minCellWidthDp <= 0f) return 1
        return (availableWidthDp / minCellWidthDp).toInt().coerceIn(1, cap)
    }

    /**
     * Height of one button in a grid of [rows] rows given [gridHeightDp] of space, where each cell adds
     * [cellPaddingDp] above and below its button.
     *
     * - Every row fits at a comfortable height: stretch to fill, up to [BUTTON_STRETCH_MAX_DP].
     * - They do not: keep [BUTTON_COMFORT_DP] and let the grid scroll, but never taller than a single
     *   row can be, so the minimum-size widget shows one whole row instead of a clipped one (A2).
     */
    fun buttonHeight(gridHeightDp: Float, rows: Int, cellPaddingDp: Float): Float {
        val padding = cellPaddingDp * 2
        val singleRow = gridHeightDp - padding
        val perRow = if (rows > 0) gridHeightDp / rows - padding else singleRow
        if (perRow >= BUTTON_COMFORT_DP) return perRow.coerceAtMost(BUTTON_STRETCH_MAX_DP)
        return singleRow.coerceAtMost(BUTTON_COMFORT_DP).coerceAtLeast(BUTTON_FLOOR_DP)
    }

    /**
     * Pixel size an icon of [widthPx] x [heightPx] should be drawn at so its longer side is at most
     * [maxPx], preserving aspect ratio. Icons already within bounds are returned unchanged.
     *
     * The launcher grid now renders EVERY selected app instead of only the ones that fit, and a widget's
     * RemoteViews carry their bitmaps across a binder call with a hard memory ceiling; a host that sends
     * large icons must not be able to push the whole widget over it.
     */
    fun scaledIconSize(widthPx: Int, heightPx: Int, maxPx: Int): Pair<Int, Int> {
        val longest = max(widthPx, heightPx)
        if (longest <= maxPx || longest <= 0) return widthPx to heightPx
        val scale = maxPx.toFloat() / longest
        return max(1, (widthPx * scale).roundToInt()) to max(1, (heightPx * scale).roundToInt())
    }

    /**
     * Pixel edge an icon drawn at [iconDp] needs on a screen of [density] (live-check A7): 48dp is
     * 96px at xhdpi and 144px at xxhdpi. Decoding any bigger only spends the bitmap budget below on
     * pixels the launcher throws away. The old fixed 224px cap sat just under the host's 256px icons,
     * so it barely bounded anything on the phones where the budget is smallest.
     */
    fun iconPx(iconDp: Float, density: Float): Int {
        if (iconDp <= 0f || density <= 0f) return 1
        return max(1, ceil(iconDp * density).toInt())
    }

    /**
     * Share of the launcher's RemoteViews bitmap ceiling the app icons may use. Under half because
     * Glance's `SizeMode.Exact` can send a portrait AND a landscape layout in one update, and the
     * frame, corner clips and text need room too.
     */
    const val ICON_BUDGET_FRACTION: Float = 0.4f

    /**
     * The launcher's hard ceiling on a widget's bitmap memory, from the screen it runs on:
     * width x height x 4 bytes x 1.5 (AppWidgetServiceImpl). Past it the whole widget renders as
     * "Can't load widget" (live-check A7), so this is sized against, never approached.
     */
    fun bitmapBudgetBytes(screenWidthPx: Int, screenHeightPx: Int): Long {
        if (screenWidthPx <= 0 || screenHeightPx <= 0) return 0L
        return (screenWidthPx.toLong() * screenHeightPx * 4L * 3L) / 2L
    }

    /** What the app icons may spend of [bitmapBudgetBytes]: [ICON_BUDGET_FRACTION] of it. */
    fun iconBudgetBytes(screenWidthPx: Int, screenHeightPx: Int): Long =
        (bitmapBudgetBytes(screenWidthPx, screenHeightPx) * ICON_BUDGET_FRACTION).toLong()

    /**
     * Which icons, in display order, may be drawn as bitmaps within [budgetBytes] (live-check A7).
     * Each one is admitted if it still fits beside the ones already admitted; the rest fall back to
     * the letter tile, so however many apps are selected the widget cannot exceed the ceiling and
     * fail as a whole. A null entry (no icon) costs nothing and is reported as not drawn.
     */
    fun iconsWithinBudget(iconBytes: List<Long?>, budgetBytes: Long): List<Boolean> {
        var spent = 0L
        return iconBytes.map { bytes ->
            if (bytes == null || bytes <= 0L) return@map false
            if (spent + bytes > budgetBytes) return@map false
            spent += bytes
            true
        }
    }
}

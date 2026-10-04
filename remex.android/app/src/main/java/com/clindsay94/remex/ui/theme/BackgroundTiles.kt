package com.clindsay94.remex.ui.theme

import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Canvas
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.ImageBitmapConfig
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.CanvasDrawScope
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.translate
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.LayoutDirection
import com.clindsay94.remex.data.BackgroundStyles
import kotlin.math.max
import kotlin.math.roundToInt
import kotlin.math.sqrt
import kotlin.random.Random

/**
 * Draws one repeating tile of a still texture into an [ImageBitmap] (RemEx-pp4cm.17). The layer then
 * fills the screen by repeating that bitmap as a shader, so a texture is rasterised once per
 * density and colour set and never again while the user scrolls or the layer's opacity changes.
 *
 * Tiles are painted in full-strength role colours; the layer's opacity (intensity) is applied when
 * the tile is drawn, never baked in, so the slider never forces a re-render.
 */
internal object BackgroundTiles {

    /** The tile for still [style] at [density] (pixels per dp), coloured from [colors]. */
    fun render(style: String, colors: List<Color>, density: Float): ImageBitmap {
        val size = tileSizePx(style, density)
        val bitmap = ImageBitmap(size.first, size.second, ImageBitmapConfig.Argb8888)
        CanvasDrawScope().draw(
            Density(density),
            LayoutDirection.Ltr,
            Canvas(bitmap),
            Size(size.first.toFloat(), size.second.toFloat()),
        ) {
            drawTile(style, colors, density)
        }
        return bitmap
    }

    /** The tile's width and height in pixels. Sizes are chosen so the pattern's cells divide them exactly. */
    fun tileSizePx(style: String, density: Float): Pair<Int, Int> =
        when (style) {
            BackgroundStyles.Grain -> 256 to 256
            BackgroundStyles.Dots -> dpPx(24f, density, 2).let { it to it }
            BackgroundStyles.Grid -> dpPx(120f, density, 5).let { it to it }
            BackgroundStyles.Topographic -> dpPx(400f, density, 1).let { it to it }
            BackgroundStyles.Hexagons -> {
                val w = dpPx(66f, density, 3)
                w to (sqrt(3.0) * (w / 3f)).roundToInt()
            }
            BackgroundStyles.Carbon -> dpPx(10f, density, 2).let { it to it }
            else -> 1 to 1
        }

    /** [dp] in pixels, rounded to a multiple of [multiple] (at least one multiple). */
    private fun dpPx(dp: Float, density: Float, multiple: Int): Int =
        max(multiple, (dp * density / multiple).roundToInt() * multiple)

    private fun DrawScope.drawTile(style: String, colors: List<Color>, density: Float) {
        val primary = colors.getOrElse(0) { Color.Gray }
        val accent = colors.getOrElse(1) { primary }
        when (style) {
            BackgroundStyles.Grain -> drawGrain(primary, accent, density)
            BackgroundStyles.Dots -> drawDots(primary, accent, density)
            BackgroundStyles.Grid -> drawGrid(primary, accent, density)
            BackgroundStyles.Topographic -> drawTopographic(primary, accent, density)
            BackgroundStyles.Hexagons -> drawHexagons(primary, accent, density)
            BackgroundStyles.Carbon -> drawCarbon(primary, accent, density)
        }
    }

    private fun DrawScope.drawGrain(primary: Color, accent: Color, density: Float) {
        val random = Random(SEED)
        val speck = max(1f, density * 0.75f)
        val count = (size.width * size.height * 0.028f).toInt()
        repeat(count) {
            val x = random.nextFloat() * size.width
            val y = random.nextFloat() * size.height
            val color = if (random.nextFloat() < 0.18f) accent else primary
            drawRect(color, Offset(x, y), Size(speck, speck), alpha = 0.25f + 0.75f * random.nextFloat())
        }
    }

    private fun DrawScope.drawDots(primary: Color, accent: Color, density: Float) {
        val half = size.width / 2f
        drawCircle(primary, radius = 1.3f * density, center = Offset(half / 2f, half / 2f))
        drawCircle(accent, radius = 1.0f * density, center = Offset(half * 1.5f, half * 1.5f), alpha = 0.7f)
    }

    private fun DrawScope.drawGrid(primary: Color, accent: Color, density: Float) {
        val cell = size.width / 5f
        val hairline = max(1f, density)
        for (k in 0 until 5) {
            val at = k * cell
            val color = if (k == 0) accent else primary
            val alpha = if (k == 0) 0.9f else 0.55f
            val width = if (k == 0) hairline * 1.5f else hairline
            drawLine(color, Offset(at, 0f), Offset(at, size.height), strokeWidth = width, alpha = alpha)
            drawLine(color, Offset(0f, at), Offset(size.width, at), strokeWidth = width, alpha = alpha)
        }
    }

    private fun DrawScope.drawTopographic(primary: Color, accent: Color, density: Float) {
        val tile = size.width
        val thin = Stroke(width = max(1f, density * 0.9f), cap = StrokeCap.Round)
        val thick = Stroke(width = max(1.5f, density * 1.5f), cap = StrokeCap.Round)
        for (k in -6..6) {
            val index = k % 5 == 0
            val segments = BackgroundGeometry.contourSegments(CONTOUR_CELLS, k * 0.2f, BackgroundGeometry::topographicField)
            val color = if (index) accent else primary
            val stroke = if (index) thick else thin
            // Nine copies, so a line crossing the tile edge keeps its full width on both sides.
            for (dx in -1..1) {
                for (dy in -1..1) {
                    translate(dx * tile, dy * tile) {
                        for (s in segments) {
                            drawLine(
                                color,
                                Offset(s.x0 * tile, s.y0 * tile),
                                Offset(s.x1 * tile, s.y1 * tile),
                                strokeWidth = stroke.width,
                                cap = StrokeCap.Round,
                                alpha = if (index) 0.95f else 0.7f,
                            )
                        }
                    }
                }
            }
        }
    }

    private fun DrawScope.drawHexagons(primary: Color, accent: Color, density: Float) {
        val side = size.width / 3f
        val line = Stroke(width = max(1f, density))
        for ((i, centre) in BackgroundGeometry.hexagonCentres(side, size.width, size.height).withIndex()) {
            val corners = BackgroundGeometry.hexagonVertices(centre.first, centre.second, side, size.height)
            val path = Path().apply {
                moveTo(corners[0].first, corners[0].second)
                for (c in corners.drop(1)) lineTo(c.first, c.second)
                close()
            }
            if (i % 5 == 0) drawPath(path, accent, alpha = 0.18f)
            drawPath(path, primary, alpha = 0.85f, style = line)
        }
    }

    private fun DrawScope.drawCarbon(primary: Color, accent: Color, density: Float) {
        val half = size.width / 2f
        val sheen = max(1f, density)
        for (qx in 0..1) {
            for (qy in 0..1) {
                val origin = Offset(qx * half, qy * half)
                val cell = Size(half, half)
                // Alternate the weave: warp cells shade top to bottom, weft cells left to right.
                val warp = (qx + qy) % 2 == 0
                val brush = if (warp) {
                    androidx.compose.ui.graphics.Brush.verticalGradient(
                        listOf(primary.copy(alpha = 0.85f), primary.copy(alpha = 0.1f)),
                        startY = origin.y,
                        endY = origin.y + half,
                    )
                } else {
                    androidx.compose.ui.graphics.Brush.horizontalGradient(
                        listOf(primary.copy(alpha = 0.85f), primary.copy(alpha = 0.1f)),
                        startX = origin.x,
                        endX = origin.x + half,
                    )
                }
                drawRect(brush, origin, cell)
                if (warp) drawRect(accent, origin, Size(half, sheen), alpha = 0.35f)
            }
        }
    }

    private const val SEED = 7
    private const val CONTOUR_CELLS = 72
}

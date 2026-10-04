package com.clindsay94.remex.ui.theme

import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.sin

/**
 * The shapes behind the still textures, kept free of Android and Compose so they can be unit
 * tested. All of it is plain arithmetic on floats.
 */
internal object BackgroundGeometry {

    /** One straight piece of a contour or an outline: (x0, y0) to (x1, y1). */
    data class Segment(val x0: Float, val y0: Float, val x1: Float, val y1: Float)

    /**
     * The six corners of a flat-topped hexagon centred on ([cx], [cy]) with side [side], squashed to
     * a total height of [height]. A regular hexagon has `height = side * sqrt(3)`; the tile renderer
     * passes a whole-pixel height so the pattern repeats without a seam.
     */
    fun hexagonVertices(cx: Float, cy: Float, side: Float, height: Float): List<Pair<Float, Float>> {
        val ry = height / SQRT3
        return (0 until 6).map { k ->
            val a = k * PI / 3.0
            (cx + side * cos(a).toFloat()) to (cy + ry * sin(a).toFloat())
        }
    }

    /**
     * The centres of the hexagons whose outline touches a tile of [tileWidth] x [tileHeight] when the
     * tile is repeated: two per tile (a flat-topped grid advances 1.5 sides across and half a row
     * down), plus the neighbours that spill over the edges.
     */
    fun hexagonCentres(side: Float, tileWidth: Float, tileHeight: Float): List<Pair<Float, Float>> {
        val centres = mutableListOf<Pair<Float, Float>>()
        for (col in -1..2) {
            val x = col * 1.5f * side
            val offset = if (col % 2 != 0) tileHeight / 2f else 0f
            for (row in -1..2) {
                centres += x to (row * tileHeight + offset)
            }
        }
        return centres.filter { (x, y) ->
            x >= -side && x <= tileWidth + side && y >= -tileHeight && y <= tileHeight * 2
        }
    }

    /**
     * A smooth field that repeats exactly every 1.0 in both directions, so the contour lines drawn
     * from it tile without a seam. Values stay roughly within -1.1..1.1.
     */
    fun topographicField(u: Float, v: Float): Float {
        val tau = 2.0 * PI
        val a = 0.55 * sin(tau * u + 1.7 * sin(tau * v))
        val b = 0.35 * sin(tau * 2.0 * v + 2.1 * sin(tau * 2.0 * u + 1.0))
        val c = 0.25 * sin(tau * (3.0 * u + 2.0 * v) + 0.7)
        return (a + b + c).toFloat()
    }

    /**
     * Marching squares: the straight pieces of the contour `field == level` over a [cells] x [cells]
     * grid covering the unit square, in unit coordinates. [field] is sampled at the grid corners.
     * Where a cell is ambiguous (all four edges crossed) the two pieces are paired in a fixed way,
     * which is fine for a decorative line.
     */
    fun contourSegments(cells: Int, level: Float, field: (Float, Float) -> Float): List<Segment> {
        require(cells > 0) { "cells must be positive" }
        val n = cells + 1
        val values = FloatArray(n * n) { i -> field((i % n).toFloat() / cells, (i / n).toFloat() / cells) - level }
        val out = ArrayList<Segment>()
        for (j in 0 until cells) {
            for (i in 0 until cells) {
                val v00 = values[j * n + i]
                val v10 = values[j * n + i + 1]
                val v11 = values[(j + 1) * n + i + 1]
                val v01 = values[(j + 1) * n + i]
                // Crossings on the top, right, bottom and left edges, in that order.
                val pts = ArrayList<Pair<Float, Float>>(4)
                fun cross(a: Float, b: Float): Float = a / (a - b)
                val x0 = i.toFloat()
                val y0 = j.toFloat()
                if ((v00 < 0f) != (v10 < 0f)) pts += (x0 + cross(v00, v10)) / cells to y0 / cells
                if ((v10 < 0f) != (v11 < 0f)) pts += (x0 + 1f) / cells to (y0 + cross(v10, v11)) / cells
                if ((v01 < 0f) != (v11 < 0f)) pts += (x0 + cross(v01, v11)) / cells to (y0 + 1f) / cells
                if ((v00 < 0f) != (v01 < 0f)) pts += x0 / cells to (y0 + cross(v00, v01)) / cells
                if (pts.size == 2) {
                    out += Segment(pts[0].first, pts[0].second, pts[1].first, pts[1].second)
                } else if (pts.size == 4) {
                    out += Segment(pts[0].first, pts[0].second, pts[1].first, pts[1].second)
                    out += Segment(pts[2].first, pts[2].second, pts[3].first, pts[3].second)
                }
            }
        }
        return out
    }

    private val SQRT3 = kotlin.math.sqrt(3.0).toFloat()
}

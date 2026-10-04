package com.clindsay94.remex.ui.theme

import androidx.graphics.shapes.RoundedPolygon

/**
 * Measures how much of a card a shape leaves usable for content (RemEx-pp4cm.14).
 *
 * A shaped card is drawn by stretching its polygon to fill the card's bounds exactly
 * ([MorphPolygonShape]). That stretch is an affine map, so the largest centred rectangle that fits
 * inside the shape is the same FRACTION of the card at every card size and aspect ratio. We measure
 * that fraction once per shape, in the unit square, and the card pads its content by it
 * ([shapeSafeArea]); no per-shape magic numbers to drift from the geometry.
 */
object CardShapeSafety {
    /** Points sampled along each cubic when flattening an outline. */
    private const val SAMPLES_PER_CUBIC = 24

    /** Points sampled along each side of the test rectangle. */
    private const val SAMPLES_PER_SIDE = 48

    private const val BOUNDARY_EPS = 1e-4f

    /** Bisection steps; 2^-20 is far finer than a pixel. */
    private const val BISECTION_STEPS = 20

    /**
     * A closed outline as interleaved x,y pairs, scaled so its bounds are exactly the unit square.
     * The same normalisation [MorphPolygonShape.createOutline] applies at draw time.
     */
    fun normalizedOutline(polygon: RoundedPolygon): FloatArray {
        val pts = ArrayList<Float>()
        for (cubic in polygon.cubics) {
            for (i in 0 until SAMPLES_PER_CUBIC) {
                // Cubic.pointOnCurve is internal to graphics-shapes; this is the same Bezier.
                val t = i / SAMPLES_PER_CUBIC.toFloat()
                val u = 1f - t
                val b0 = u * u * u
                val b1 = 3f * u * u * t
                val b2 = 3f * u * t * t
                val b3 = t * t * t
                pts.add(b0 * cubic.anchor0X + b1 * cubic.control0X + b2 * cubic.control1X + b3 * cubic.anchor1X)
                pts.add(b0 * cubic.anchor0Y + b1 * cubic.control0Y + b2 * cubic.control1Y + b3 * cubic.anchor1Y)
            }
        }
        val out = pts.toFloatArray()
        return normalize(out)
    }

    /** Maps an interleaved x,y outline so its bounding box is exactly the unit square. */
    fun normalize(points: FloatArray): FloatArray {
        require(points.size >= 6 && points.size % 2 == 0) { "An outline needs at least three points" }
        var minX = Float.MAX_VALUE
        var minY = Float.MAX_VALUE
        var maxX = -Float.MAX_VALUE
        var maxY = -Float.MAX_VALUE
        for (i in points.indices step 2) {
            minX = minOf(minX, points[i]); maxX = maxOf(maxX, points[i])
            minY = minOf(minY, points[i + 1]); maxY = maxOf(maxY, points[i + 1])
        }
        val w = (maxX - minX).coerceAtLeast(1e-6f)
        val h = (maxY - minY).coerceAtLeast(1e-6f)
        return FloatArray(points.size) { i ->
            if (i % 2 == 0) (points[i] - minX) / w else (points[i] - minY) / h
        }
    }

    /**
     * The side, as a fraction of the card, of the largest rectangle centred on the card (and so
     * with the card's own aspect ratio) that lies wholly inside the outline. 1.0 for a plain
     * rectangle, about 0.707 for an ellipse, 0.5 for a diamond.
     *
     * [outline] must be normalised ([normalizedOutline]). The test is exact for a simple closed
     * outline: the rectangle is inside if its whole boundary is inside AND no outline point sits
     * strictly inside the rectangle.
     */
    fun safeFraction(outline: FloatArray): Float {
        var lo = 0f
        var hi = 1f
        if (fits(outline, 1f)) return 1f
        repeat(BISECTION_STEPS) {
            val mid = (lo + hi) / 2f
            if (fits(outline, mid)) lo = mid else hi = mid
        }
        return lo
    }

    /** [safeFraction] of a polygon, measured as drawn. */
    fun safeFraction(polygon: RoundedPolygon): Float = safeFraction(normalizedOutline(polygon))

    private fun fits(outline: FloatArray, s: Float): Boolean {
        val left = 0.5f - s / 2f
        val right = 0.5f + s / 2f
        val top = left
        val bottom = right
        // A vertex strictly inside the rectangle means the outline cuts into it.
        val eps = 1e-5f
        for (i in outline.indices step 2) {
            val x = outline[i]
            val y = outline[i + 1]
            if (x > left + eps && x < right - eps && y > top + eps && y < bottom - eps) return false
        }
        // Sampled a hair inside the rectangle: a side lying exactly ON the outline (a plain
        // rectangle) is on the boundary, where ray casting is ambiguous.
        val l = left + BOUNDARY_EPS
        val r = right - BOUNDARY_EPS
        val tp = top + BOUNDARY_EPS
        val bt = bottom - BOUNDARY_EPS
        for (k in 0..SAMPLES_PER_SIDE) {
            val t = k / SAMPLES_PER_SIDE.toFloat()
            val x = l + (r - l) * t
            val y = tp + (bt - tp) * t
            if (!contains(outline, x, tp) || !contains(outline, x, bt) ||
                !contains(outline, l, y) || !contains(outline, r, y)
            ) {
                return false
            }
        }
        return true
    }

    /** Even-odd ray-casting point-in-polygon. */
    private fun contains(outline: FloatArray, px: Float, py: Float): Boolean {
        var inside = false
        val n = outline.size / 2
        var j = n - 1
        for (i in 0 until n) {
            val xi = outline[2 * i]
            val yi = outline[2 * i + 1]
            val xj = outline[2 * j]
            val yj = outline[2 * j + 1]
            if ((yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi) inside = !inside
            j = i
        }
        return inside
    }
}

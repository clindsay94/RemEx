package com.clindsay94.remex.widget

/**
 * A point in the sparkline's pixel space.
 *
 * @param x pixels from the left edge.
 * @param y pixels from the TOP edge — screen coordinates, not chart coordinates.
 */
data class SparklinePoint(val x: Float, val y: Float)

/**
 * Maps telemetry samples onto a sparkline's pixel canvas (RemEx-0akv).
 *
 * The widget shows bare numbers while the app owns nine chart renderers, and a sparkline on the home
 * screen is the difference between a widget that gets kept and one that gets removed. Glance cannot
 * compose a Canvas, so the bitmap is drawn with `android.graphics.Canvas` — but the ARITHMETIC does
 * not need a Canvas, and keeping it here means the cases that actually break can be proven without
 * a device.
 */
object SparklinePoints {

    /**
     * Converts samples into points ready to stroke as a polyline.
     *
     * @param samples oldest first.
     * @param width canvas width in pixels.
     * @param height canvas height in pixels.
     * @param strokeInset half the stroke width, so a line at the extreme is not clipped in half by
     *   the canvas edge.
     */
    fun map(
        samples: List<Float>,
        width: Float,
        height: Float,
        strokeInset: Float = 0f
    ): List<SparklinePoint> {
        if (samples.isEmpty() || width <= 0f || height <= 0f) return emptyList()

        val top = strokeInset
        val bottom = height - strokeInset
        if (bottom <= top) return emptyList()

        // A SINGLE SAMPLE IS A DOT, NOT A LINE. Dividing by (size - 1) to spread points across the
        // width is the natural expression and it divides by zero here - which yields NaN or
        // Infinity and draws nothing, or worse, draws garbage that looks like corrupted data.
        if (samples.size == 1) {
            return listOf(SparklinePoint(width / 2f, midpoint(top, bottom)))
        }

        val min = samples.min()
        val max = samples.max()
        val range = max - min

        val usableWidth = width - (strokeInset * 2f)
        val step = usableWidth / (samples.size - 1)

        return samples.mapIndexed { index, value ->
            val x = strokeInset + (step * index)

            // A FLAT SERIES HAS A RANGE OF ZERO, AND THAT IS THE COMMON CASE, NOT AN EDGE CASE. An
            // idle machine reports the same CPU percentage for thirty consecutive samples. Scaling
            // by the range would divide by zero and produce NaN for every point; a flat line
            // through the middle is both correct and what the user expects to see.
            val normalized = if (range == 0f) 0.5f else (value - min) / range

            // Y IS INVERTED. Screen coordinates grow DOWNWARD while values grow upward, so a
            // high sample must map to a SMALL y. Getting this wrong renders the chart upside down,
            // which is not obviously wrong on a sparkline - it just quietly tells the user their
            // CPU is idle when it is pinned.
            val y = bottom - (normalized * (bottom - top))

            SparklinePoint(x, y)
        }
    }

    private fun midpoint(top: Float, bottom: Float) = top + ((bottom - top) / 2f)
}

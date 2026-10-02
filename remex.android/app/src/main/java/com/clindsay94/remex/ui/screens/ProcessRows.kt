package com.clindsay94.remex.ui.screens

/**
 * What one Processes row shows (RemEx-kq10x.5): the name and "PID n" on line 1, CPU and memory each
 * with a small bar on the right. Same layout as the PC's Processes page, which adds the path and
 * publisher lines the phone does not get.
 *
 * @property cpuText CPU share as a whole percentage, e.g. "12%".
 * @property ramText memory in whole megabytes, e.g. "512MB".
 * @property cpuFraction the CPU bar's fill, 0..1, relative to the busiest listed process.
 * @property ramFraction the memory bar's fill, 0..1, relative to the largest listed process.
 */
internal data class ProcessRowModel(
    val cpuText: String,
    val ramText: String,
    val cpuFraction: Float,
    val ramFraction: Float,
)

internal object ProcessRows {
    /**
     * The bar scale for a column: its largest value, or 1.0 when there is none or it is 0.0.
     * Review fix (P1-20): cpu/ram are rounded to whole numbers, so an idle host (every process under
     * 0.5%) floors every value to 0.0 and the max is 0.0, not null. Dividing by it gives NaN, which
     * coerceIn passes straight through into animateFloatAsState.
     */
    fun scaleMax(values: Iterable<Double>): Double = values.maxOrNull()?.takeIf { it > 0.0 } ?: 1.0

    fun rowFor(process: ProcessInfo, maxCpu: Double, maxRam: Double): ProcessRowModel =
        ProcessRowModel(
            cpuText = "${process.cpu.toInt()}%",
            ramText = "${process.ram.toInt()}MB",
            cpuFraction = fraction(process.cpu, maxCpu),
            ramFraction = fraction(process.ram, maxRam),
        )

    private fun fraction(value: Double, max: Double): Float {
        if (max <= 0.0 || value.isNaN() || max.isNaN()) return 0f
        return (value / max).toFloat().coerceIn(0f, 1f)
    }
}

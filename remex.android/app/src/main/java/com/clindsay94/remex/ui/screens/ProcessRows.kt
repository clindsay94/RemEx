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

    /**
     * Whether rows glide to their new place when the list re-sorts (phase 6, RemEx-wqo7a.8). Only
     * for the orders that hold still: by name or PID a row moves only when a process starts or
     * ends. By CPU or memory the order reshuffles on every refresh, and a list of rows sliding past
     * each other every few seconds is noise, so those rows just take their new places; processes
     * coming and going still fade in and out either way.
     */
    fun animatesReorder(sortField: ProcessSortField): Boolean =
        when (sortField) {
            ProcessSortField.NAME, ProcessSortField.PID -> true
            ProcessSortField.CPU, ProcessSortField.RAM -> false
        }

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

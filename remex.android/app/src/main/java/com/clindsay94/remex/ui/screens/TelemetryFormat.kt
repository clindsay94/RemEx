package com.clindsay94.remex.ui.screens

import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import com.clindsay94.remex.ui.telemetry.MetricKind
import com.clindsay94.remex.ui.telemetry.MetricUnits

/** [text] is the fully formatted "value+unit" string; [fraction] is 0..1, non-null only when the view is a gauge. */
data class Formatted(val text: String, val unit: String, val fraction: Float?)

/** The ONE formatter - guards against an Unknown/mismatched-unit sensor rendering garbage (e.g. "1089.0ms" on RAM). */
fun formatSensor(sensor: TelemetrySensor?): Formatted {
    if (sensor == null) return Formatted("--", "", null)
    val unit = MetricUnits.safeUnitFor(sensor.kind, sensor.unit)
    val decimals = when (sensor.kind) {
        MetricKind.RAM_USED_GB, MetricKind.RAM_TOTAL_GB -> 1
        MetricKind.VOLTAGE_V -> 2
        else -> 0
    }
    val text = "%.${decimals}f".format(sensor.value)
    return Formatted(if (unit.isEmpty()) text else "$text$unit", unit, null)
}

/** min/max/range window used to autoscale line/area/bar charts and the adaptive gauge fraction. */
data class Autoscale(val low: Float, val high: Float, val range: Float)

private fun computeAutoscale(history: List<Float>): Autoscale {
    val high = history.maxOrNull() ?: 1f
    val low = history.minOrNull() ?: 0f
    val range = (high - low).takeIf { it > 0f } ?: 1f
    return Autoscale(low, high, range)
}

fun rememberAutoscale(history: List<Float>): Autoscale = computeAutoscale(history)
fun rememberAutoscaleValue(history: List<Float>): Autoscale = computeAutoscale(history)

/** Replaces the broken coerceIn(0,100)/100 - per-metric, never blind /100. */
fun gaugeFraction(sensor: TelemetrySensor?, history: List<Float>): Float {
    val fraction = when (sensor?.kind) {
        MetricKind.CPU_LOAD, MetricKind.GPU_LOAD, MetricKind.RAM_LOAD -> sensor.value.toFloat() / 100f
        MetricKind.CPU_TEMP_C, MetricKind.GPU_TEMP_C, MetricKind.TEMP_C -> sensor.value.toFloat() / 100f
        null -> 0f
        else -> {
            val s = rememberAutoscaleValue(history)
            (sensor.value.toFloat() - s.low) / s.range
        }
    }
    return fraction.coerceIn(0f, 1f)
}

/** Change since the previous sample; 0 when there isn't one yet. */
fun trendDelta(history: List<Float>): Float =
    if (history.size < 2) 0f else history.last() - history[history.size - 2]

private val HEAT_KINDS = setOf(
    MetricKind.CPU_LOAD, MetricKind.GPU_LOAD, MetricKind.RAM_LOAD,
    MetricKind.CPU_TEMP_C, MetricKind.GPU_TEMP_C, MetricKind.TEMP_C, MetricKind.POWER_W
)

/** Rising load/temp/power reads as bad (error); rising throughput/other reads as neutral (primary). Falling is always muted. */
@Composable
fun trendColor(kind: MetricKind, sign: Int): Color = when {
    sign > 0 && kind in HEAT_KINDS -> MaterialTheme.colorScheme.error
    sign > 0 -> MaterialTheme.colorScheme.primary
    else -> MaterialTheme.colorScheme.onSurfaceVariant
}

/**
 * AUTO resolution (also seeds the curated starter set and the picker's "Auto" cell) - per-kind
 * smart default, falling back to a unit-string heuristic for Unknown/legacy-host sensors.
 */
fun bestDisplayModeFor(sensor: TelemetrySensor?): TelemetryDisplayMode = when (sensor?.kind) {
    MetricKind.CPU_LOAD, MetricKind.GPU_LOAD, MetricKind.RAM_LOAD -> TelemetryDisplayMode.RING_GAUGE
    MetricKind.CPU_TEMP_C, MetricKind.GPU_TEMP_C, MetricKind.TEMP_C -> TelemetryDisplayMode.LINE
    MetricKind.CLOCK_MHZ, MetricKind.POWER_W, MetricKind.DISK_RATE_MBS -> TelemetryDisplayMode.AREA
    MetricKind.NET_THROUGHPUT_MBPS, MetricKind.NET_DOWN_MBPS, MetricKind.NET_UP_MBPS -> TelemetryDisplayMode.AREA
    MetricKind.FAN_RPM -> TelemetryDisplayMode.BAR
    MetricKind.VOLTAGE_V -> TelemetryDisplayMode.LINE
    MetricKind.RAM_USED_GB, MetricKind.RAM_TOTAL_GB -> TelemetryDisplayMode.VALUE_SPARK
    else -> when {
        sensor?.unit == "%" -> TelemetryDisplayMode.RING_GAUGE
        sensor?.unit?.contains("°") == true -> TelemetryDisplayMode.LINE
        else -> TelemetryDisplayMode.VALUE_SPARK
    }
}

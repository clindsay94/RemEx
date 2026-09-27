package com.clindsay94.remex.ui.routines

import com.clindsay94.remex.routines.model.RoutineLimits
import com.clindsay94.remex.routines.model.RoutineTrigger
import com.clindsay94.remex.ui.telemetry.MetricKind
import org.json.JSONException
import org.json.JSONObject

/**
 * One sensor the connected PC reports, for the `pc.sensor` picker (routines S5, §6.3). [id] is the
 * PC's own `SensorReading.Id`, the exact key the PC's sensor source looks the reading up by; the
 * dashboard's curated card slugs are NOT used here, because the PC would never find them.
 */
data class RoutineSensorOption(
    val id: String,
    val name: String,
    val unit: String,
    val kind: MetricKind,
    val group: String,
    val value: Double,
)

/** The sensor a health template preselects (spec 4.1 "preselects by kind + name"). */
enum class RoutineSensorPreset(val kinds: Set<MetricKind>, val nameHints: List<String>, val unitHints: List<String>) {
    GPU_TEMP(setOf(MetricKind.GPU_TEMP_C), listOf("gpu"), listOf("°C", "C")),
    CPU_TEMP(setOf(MetricKind.CPU_TEMP_C), listOf("cpu", "package", "tctl", "tdie"), listOf("°C", "C")),
    RAM_LOAD(setOf(MetricKind.RAM_LOAD), listOf("memory", "ram"), listOf("%")),
}

/** The connected PC's sensor catalog, parsed from its telemetry frame. Pure: JVM-tested. */
object RoutineSensorCatalog {
    /** Sustain choices, 5 to 600 s (§6.3). */
    val sustainChoices: List<Int> = listOf(5, 10, 15, 30, 60, 120, 180, 300, 600)

    /**
     * Every sensor with a PC id and a name, sorted by group then name; null when [telemetryJson] is
     * missing or unreadable (the picker then says to connect).
     */
    fun parse(telemetryJson: String?): List<RoutineSensorOption>? {
        if (telemetryJson.isNullOrBlank()) return null
        return try {
            val sensors = JSONObject(telemetryJson).optJSONArray("sensors") ?: return emptyList()
            val out = LinkedHashMap<String, RoutineSensorOption>()
            for (i in 0 until sensors.length()) {
                val s = sensors.optJSONObject(i) ?: continue
                val id = s.optString("id")
                val name = s.optString("name")
                if (id.isBlank() || name.isBlank() || id.length > RoutineLimits.MAX_SENSOR_ID_LENGTH) continue
                out.putIfAbsent(
                    id,
                    RoutineSensorOption(
                        id = id,
                        name = name,
                        unit = s.optString("unit"),
                        kind = MetricKind.fromWire(s.optString("kind").ifBlank { null }),
                        group = s.optString("group"),
                        value = s.optDouble("value", Double.NaN),
                    ),
                )
            }
            out.values.sortedWith(compareBy({ it.group.lowercase() }, { it.name.lowercase() }))
        } catch (e: JSONException) {
            null
        }
    }

    /** The template's sensor: first by the PC's stamped kind, then by name and unit. Null when none fits. */
    fun preselect(options: List<RoutineSensorOption>?, preset: RoutineSensorPreset): RoutineSensorOption? {
        if (options.isNullOrEmpty()) return null
        options.firstOrNull { it.kind in preset.kinds }?.let { return it }
        return options.firstOrNull { option ->
            val name = option.name.lowercase()
            preset.nameHints.any { name.contains(it) } && preset.unitHints.any { option.unit.trim().endsWith(it) }
        }
    }

    /**
     * Spec 4.2 "Sensor" for one template: null when it needs no sensor or the catalog is unknown
     * (not the connected PC, or no frame yet), else whether THIS template's own preset finds one. A
     * CPU-only PC never satisfies the GPU template.
     */
    fun templateSensorAvailable(template: RoutineTemplate, options: List<RoutineSensorOption>?): Boolean? {
        val preset = template.sensorPreset ?: return null
        if (options == null) return null
        return preselect(options, preset) != null
    }

    /** [trigger] pointed at [option]: its id and a display snapshot of its name (≤ 64). */
    fun choose(trigger: RoutineTrigger, option: RoutineSensorOption): RoutineTrigger =
        trigger.copy(sensorId = option.id, sensorLabel = option.name.take(RoutineLimits.MAX_LABEL_LENGTH))

    /** A typed limit: a finite number, with either decimal separator; null otherwise. */
    fun parseLimit(text: String): Double? = text.trim().replace(',', '.').toDoubleOrNull()?.takeIf { it.isFinite() }

    /** A limit for the text field, without a trailing ".0". */
    fun formatLimit(value: Double?): String =
        when {
            value == null || !value.isFinite() -> ""
            value == Math.rint(value) && kotlin.math.abs(value) < 1e15 -> value.toLong().toString()
            else -> value.toString()
        }
}

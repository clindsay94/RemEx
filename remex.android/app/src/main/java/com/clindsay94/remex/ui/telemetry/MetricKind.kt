package com.clindsay94.remex.ui.telemetry

/**
 * Semantic role of a telemetry sensor, mirrored from the host's shared wire contract
 * (`remex.core/Messages/MetricKind.cs`). Lets a card bind to a metric by MEANING rather than a
 * fragile host name/unit string — the fix for the "1089.0ms" RAM bug, where a timing sensor
 * (unit "ms") won the RAM slot purely by a name-substring collision.
 *
 * Wire tokens are the C# enum member names VERBATIM (the host serializes with
 * `JsonStringEnumConverter` and no naming policy), so [fromWire] keys on e.g. "CpuLoad", "RamLoad".
 */
enum class MetricKind {
    UNKNOWN,
    CPU_LOAD, GPU_LOAD, RAM_LOAD,
    RAM_USED_GB, RAM_TOTAL_GB,
    CPU_TEMP_C, GPU_TEMP_C, TEMP_C,
    CLOCK_MHZ, POWER_W, FAN_RPM,
    NET_THROUGHPUT_MBPS, NET_DOWN_MBPS, NET_UP_MBPS,
    VOLTAGE_V, DISK_RATE_MBS;

    companion object {
        private val byWire: Map<String, MetricKind> = mapOf(
            "CpuLoad" to CPU_LOAD, "GpuLoad" to GPU_LOAD, "RamLoad" to RAM_LOAD,
            "RamUsedGb" to RAM_USED_GB, "RamTotalGb" to RAM_TOTAL_GB,
            "CpuTempC" to CPU_TEMP_C, "GpuTempC" to GPU_TEMP_C, "TempC" to TEMP_C,
            "ClockMhz" to CLOCK_MHZ, "PowerW" to POWER_W, "FanRpm" to FAN_RPM,
            "NetThroughputMbps" to NET_THROUGHPUT_MBPS, "NetDownMbps" to NET_DOWN_MBPS,
            "NetUpMbps" to NET_UP_MBPS, "VoltageV" to VOLTAGE_V, "DiskRateMBs" to DISK_RATE_MBS,
        )

        /** Tolerant of older hosts that don't stamp a kind: a blank/unknown token maps to [UNKNOWN]. */
        fun fromWire(raw: String?): MetricKind = raw?.let { byWire[it] } ?: UNKNOWN
    }
}

/**
 * Canonical units and curated-card matching, mirrored from `remex.core/Messages/MetricUnits.cs`.
 * A card should render the canonical unit for its kind — never a raw host string — so a mislabeled
 * or "ms" unit can never surface on a load/temperature card.
 */
object MetricUnits {
    fun canonical(kind: MetricKind): String = when (kind) {
        MetricKind.CPU_LOAD, MetricKind.GPU_LOAD, MetricKind.RAM_LOAD -> "%"
        MetricKind.RAM_USED_GB, MetricKind.RAM_TOTAL_GB -> "GB"
        MetricKind.CPU_TEMP_C, MetricKind.GPU_TEMP_C, MetricKind.TEMP_C -> "°C"
        MetricKind.CLOCK_MHZ -> "MHz"
        MetricKind.POWER_W -> "W"
        MetricKind.FAN_RPM -> "RPM"
        MetricKind.NET_THROUGHPUT_MBPS, MetricKind.NET_DOWN_MBPS, MetricKind.NET_UP_MBPS -> "Mbps"
        MetricKind.VOLTAGE_V -> "V"
        MetricKind.DISK_RATE_MBS -> "MB/s"
        MetricKind.UNKNOWN -> ""
    }

    /** Stable slug for the three curated default cards; null for every other kind. */
    fun cardSlug(kind: MetricKind): String? = when (kind) {
        MetricKind.CPU_LOAD -> "sensor:cpu"
        MetricKind.GPU_LOAD -> "sensor:gpu"
        MetricKind.RAM_USED_GB -> "sensor:ram"
        else -> null
    }

    private val SAFE_UNITS = setOf("%", "°C", "GB", "MHz", "W", "RPM", "Mbps", "V", "MB/s")

    /**
     * The unit a card may safely display: the canonical unit for a known kind; otherwise the host
     * unit only if it is a recognized safe unit; otherwise empty. Guarantees an Unknown sensor can
     * never render garbage such as "ms" on a curated card.
     */
    fun safeUnitFor(kind: MetricKind, hostUnit: String): String =
        canonical(kind).ifEmpty { if (hostUnit in SAFE_UNITS) hostUnit else "" }
}

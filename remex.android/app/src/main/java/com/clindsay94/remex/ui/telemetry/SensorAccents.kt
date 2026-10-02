package com.clindsay94.remex.ui.telemetry

import androidx.compose.material3.ColorScheme
import androidx.compose.ui.graphics.Color

/** The palette family a sensor card's category renders in. Mirrors the PC's `SensorFamily`. */
enum class SensorFamily { PRIMARY, SECONDARY, TERTIARY, NEUTRAL }

/**
 * The accent colours of one sensor card, all taken from the live [ColorScheme] so they hold under
 * every theming axis (seed / dynamic / static fallback x light-dark x themeStyle x contrast).
 *
 * @property series the sparkline / gauge / value colour (the PC's `CardSeries{Family}`).
 * @property chip the label chip's fill (the PC's `CardPlate{Family}Brush`).
 * @property onChip the label text on that chip (the PC's `CardInk{Family}Brush`).
 */
data class SensorAccent(val series: Color, val chip: Color, val onChip: Color)

/**
 * Sensor category -> accent colour, the SAME rule as the PC (RemEx-kq10x.5, cohesion spec
 * decision 7), so both apps colour the same sensor the same way:
 *
 * - `remex.core/Theming/SensorFamily.cs` `SensorFamilies.For` is the category -> family table, and
 *   [familyFor] is a row-for-row copy of it. `SensorAccentsTest` pins the same table as the PC's
 *   `SensorFamiliesTests`; change both together.
 * - `remex.desktop/Services/ThemeService.cs` turns a family into roles: series = the family's main
 *   role (primary / secondary / tertiary / outline), plate = its container (surfaceContainerHigh
 *   for neutral), ink = on-container (onSurface for neutral). [accentFor] uses the same roles.
 *
 * The accent goes on the label chip, the value and the sparkline/gauge, never on the card fill.
 */
object SensorAccents {
    /** Every [MetricKind] is listed explicitly, so a new kind is a compile error until it has a row. */
    fun familyFor(kind: MetricKind): SensorFamily = when (kind) {
        MetricKind.CPU_LOAD, MetricKind.CPU_TEMP_C -> SensorFamily.PRIMARY
        MetricKind.RAM_LOAD, MetricKind.RAM_USED_GB, MetricKind.RAM_TOTAL_GB -> SensorFamily.SECONDARY
        MetricKind.GPU_LOAD, MetricKind.GPU_TEMP_C -> SensorFamily.TERTIARY
        MetricKind.NET_THROUGHPUT_MBPS, MetricKind.NET_DOWN_MBPS, MetricKind.NET_UP_MBPS -> SensorFamily.PRIMARY
        MetricKind.DISK_RATE_MBS -> SensorFamily.SECONDARY
        MetricKind.POWER_W, MetricKind.VOLTAGE_V, MetricKind.FAN_RPM, MetricKind.TEMP_C,
        MetricKind.CLOCK_MHZ -> SensorFamily.TERTIARY
        MetricKind.UNKNOWN -> SensorFamily.NEUTRAL
    }

    /** The M3 role name of a family's series colour, spelled as the PC's `SensorFamilies.MainRole`. */
    fun mainRole(family: SensorFamily): String = when (family) {
        SensorFamily.PRIMARY -> "primary"
        SensorFamily.SECONDARY -> "secondary"
        SensorFamily.TERTIARY -> "tertiary"
        SensorFamily.NEUTRAL -> "outline"
    }

    /** The accent colours for [family], resolved against [scheme]. */
    fun accentFor(family: SensorFamily, scheme: ColorScheme): SensorAccent = when (family) {
        SensorFamily.PRIMARY ->
            SensorAccent(scheme.primary, scheme.primaryContainer, scheme.onPrimaryContainer)
        SensorFamily.SECONDARY ->
            SensorAccent(scheme.secondary, scheme.secondaryContainer, scheme.onSecondaryContainer)
        SensorFamily.TERTIARY ->
            SensorAccent(scheme.tertiary, scheme.tertiaryContainer, scheme.onTertiaryContainer)
        SensorFamily.NEUTRAL ->
            SensorAccent(scheme.outline, scheme.surfaceContainerHigh, scheme.onSurface)
    }

    /** Shorthand: the accent for a sensor of [kind]. */
    fun accentFor(kind: MetricKind, scheme: ColorScheme): SensorAccent = accentFor(familyFor(kind), scheme)
}

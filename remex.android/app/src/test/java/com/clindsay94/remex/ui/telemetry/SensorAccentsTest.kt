package com.clindsay94.remex.ui.telemetry

import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * RemEx-kq10x.5: the phone colours a sensor card by the SAME category rule as the PC. The table
 * below is the PC's `remex.core/Theming/SensorFamily.cs` `SensorFamilies.For`, row for row (pinned
 * on the PC side by `remex.core.tests/Theming/SensorFamiliesTests.cs`). Change both together.
 */
class SensorAccentsTest {

    private val pcTable: Map<MetricKind, SensorFamily> = mapOf(
        MetricKind.CPU_LOAD to SensorFamily.PRIMARY,
        MetricKind.CPU_TEMP_C to SensorFamily.PRIMARY,
        MetricKind.RAM_LOAD to SensorFamily.SECONDARY,
        MetricKind.RAM_USED_GB to SensorFamily.SECONDARY,
        MetricKind.RAM_TOTAL_GB to SensorFamily.SECONDARY,
        MetricKind.GPU_LOAD to SensorFamily.TERTIARY,
        MetricKind.GPU_TEMP_C to SensorFamily.TERTIARY,
        MetricKind.NET_THROUGHPUT_MBPS to SensorFamily.PRIMARY,
        MetricKind.NET_DOWN_MBPS to SensorFamily.PRIMARY,
        MetricKind.NET_UP_MBPS to SensorFamily.PRIMARY,
        MetricKind.DISK_RATE_MBS to SensorFamily.SECONDARY,
        MetricKind.POWER_W to SensorFamily.TERTIARY,
        MetricKind.VOLTAGE_V to SensorFamily.TERTIARY,
        MetricKind.FAN_RPM to SensorFamily.TERTIARY,
        MetricKind.TEMP_C to SensorFamily.TERTIARY,
        MetricKind.CLOCK_MHZ to SensorFamily.TERTIARY,
        MetricKind.UNKNOWN to SensorFamily.NEUTRAL,
    )

    @Test
    fun `the table covers every metric kind`() {
        // Anti-vacuity: a kind missing from the table would silently skip the comparison below.
        assertEquals(MetricKind.entries.toSet(), pcTable.keys)
    }

    @Test
    fun `every metric kind gets the same family as on the PC`() {
        pcTable.forEach { (kind, family) -> assertEquals(kind.name, family, SensorAccents.familyFor(kind)) }
    }

    @Test
    fun `main role names match the PC spelling`() {
        assertEquals("primary", SensorAccents.mainRole(SensorFamily.PRIMARY))
        assertEquals("secondary", SensorAccents.mainRole(SensorFamily.SECONDARY))
        assertEquals("tertiary", SensorAccents.mainRole(SensorFamily.TERTIARY))
        assertEquals("outline", SensorAccents.mainRole(SensorFamily.NEUTRAL))
    }

    @Test
    fun `accents come from scheme roles in light and dark`() {
        listOf(lightColorScheme(), darkColorScheme()).forEach { s ->
            assertEquals(SensorAccent(s.primary, s.primaryContainer, s.onPrimaryContainer),
                SensorAccents.accentFor(SensorFamily.PRIMARY, s))
            assertEquals(SensorAccent(s.secondary, s.secondaryContainer, s.onSecondaryContainer),
                SensorAccents.accentFor(SensorFamily.SECONDARY, s))
            assertEquals(SensorAccent(s.tertiary, s.tertiaryContainer, s.onTertiaryContainer),
                SensorAccents.accentFor(SensorFamily.TERTIARY, s))
            assertEquals(SensorAccent(s.outline, s.surfaceContainerHigh, s.onSurface),
                SensorAccents.accentFor(SensorFamily.NEUTRAL, s))
        }
    }

    @Test
    fun `a temperature sensor and a GPU sensor share the tertiary accent`() {
        val s = lightColorScheme()
        assertEquals(s.tertiary, SensorAccents.accentFor(MetricKind.TEMP_C, s).series)
        assertEquals(s.tertiary, SensorAccents.accentFor(MetricKind.GPU_LOAD, s).series)
    }
}

package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.TelemetrySensor
import com.clindsay94.remex.ui.screens.pruneAndAppendTelemetryHistory
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Perf audit P2-11: [pruneAndAppendTelemetryHistory] is the pure half of `updateTelemetryHistory`,
 * pulled out specifically so this logic can be exercised directly (DashboardViewModel is an
 * AndroidViewModel with no Robolectric in this module).
 */
class TelemetryHistoryPruneTest {

    private fun sensor(id: String, value: Double) =
        TelemetrySensor(id = id, name = id, category = "Other", value = value, unit = "")

    @Test
    fun `a sensor no longer reported is evicted from history`() {
        val current = mapOf("cpu" to listOf(10f, 20f), "gone" to listOf(1f, 2f, 3f))

        val updated = pruneAndAppendTelemetryHistory(current, listOf(sensor("cpu", 30.0)))

        assertFalse("a sensor absent from this tick's report must be dropped", updated.containsKey("gone"))
        assertTrue(updated.containsKey("cpu"))
    }

    @Test
    fun `a still-reported sensor keeps and appends to its history`() {
        val current = mapOf("cpu" to listOf(10f, 20f))

        val updated = pruneAndAppendTelemetryHistory(current, listOf(sensor("cpu", 30.0)))

        assertEquals(listOf(10f, 20f, 30f), updated["cpu"])
    }

    @Test
    fun `a brand new sensor id starts its own history`() {
        val updated = pruneAndAppendTelemetryHistory(emptyMap(), listOf(sensor("new", 5.0)))

        assertEquals(listOf(5f), updated["new"])
    }

    @Test
    fun `history is capped at 40 points per sensor`() {
        val current = mapOf("cpu" to (1..40).map { it.toFloat() })

        val updated = pruneAndAppendTelemetryHistory(current, listOf(sensor("cpu", 41.0)))

        assertEquals(40, updated["cpu"]!!.size)
        assertEquals(2f, updated["cpu"]!!.first()) // oldest point (1f) dropped, not the newest
        assertEquals(41f, updated["cpu"]!!.last())
    }

    @Test
    fun `multiple sensors are independently pruned and appended in one call`() {
        val current = mapOf(
            "cpu" to listOf(10f),
            "stale-drive" to listOf(99f),
        )

        val updated = pruneAndAppendTelemetryHistory(
            current,
            listOf(sensor("cpu", 11.0), sensor("gpu", 5.0)),
        )

        assertEquals(setOf("cpu", "gpu"), updated.keys)
        assertEquals(listOf(10f, 11f), updated["cpu"])
        assertEquals(listOf(5f), updated["gpu"])
    }
}

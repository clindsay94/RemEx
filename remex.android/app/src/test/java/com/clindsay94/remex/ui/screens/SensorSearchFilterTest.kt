package com.clindsay94.remex.ui.screens

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** The Add-cards and second-sensor search: name, subtitle or group, any case, blank shows everything. */
class SensorSearchFilterTest {
    private val items =
        listOf(
            AvailableCardItem("cpu", "CPU Package", "Temperature", "CPU"),
            AvailableCardItem("gpu", "GPU Core", "Temperature", "NVIDIA GeForce"),
            AvailableCardItem("ram", "Memory Used", "Load", "Memory"),
        )

    private fun ids(query: String) = filterAvailableCards(items, query).map { it.id }

    @Test
    fun `a blank query returns every sensor`() {
        assertEquals(listOf("cpu", "gpu", "ram"), ids(""))
        assertEquals(listOf("cpu", "gpu", "ram"), ids("   "))
    }

    @Test
    fun `it matches the name, the subtitle or the group, ignoring case`() {
        assertEquals(listOf("gpu"), ids("core"))
        assertEquals(listOf("cpu", "gpu"), ids("TEMPERATURE"))
        assertEquals(listOf("gpu"), ids("geforce"))
    }

    @Test
    fun `the query is trimmed and a miss returns nothing`() {
        assertEquals(listOf("ram"), ids("  memory "))
        assertTrue(ids("fan").isEmpty())
    }

    @Test
    fun `the second-sensor picker matches by name or group`() {
        assertTrue(matchesSensorQuery("nvidia", "GPU Core", "NVIDIA GeForce"))
        assertTrue(matchesSensorQuery("gpu c", "GPU Core", ""))
        assertTrue(matchesSensorQuery("", "anything"))
    }
}

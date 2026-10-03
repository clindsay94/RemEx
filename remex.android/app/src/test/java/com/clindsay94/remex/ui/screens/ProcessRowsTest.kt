package com.clindsay94.remex.ui.screens

import org.junit.Assert.assertEquals
import org.junit.Test

/** RemEx-kq10x.5: the Processes row model behind the name + PID line and the CPU/memory mini bars. */
class ProcessRowsTest {

    @Test
    fun `bars are relative to the busiest and largest listed process`() {
        val row = ProcessRows.rowFor(ProcessInfo(42, "game.exe", 25.0, 512.0), maxCpu = 50.0, maxRam = 2048.0)
        assertEquals("25%", row.cpuText)
        assertEquals("512MB", row.ramText)
        assertEquals(0.5f, row.cpuFraction, 0.0001f)
        assertEquals(0.25f, row.ramFraction, 0.0001f)
    }

    @Test
    fun `an idle host scales to 1 so the bars stay empty instead of NaN`() {
        val idle = listOf(0.0, 0.0, 0.0)
        assertEquals(1.0, ProcessRows.scaleMax(idle), 0.0)
        assertEquals(1.0, ProcessRows.scaleMax(emptyList()), 0.0)
        val row = ProcessRows.rowFor(ProcessInfo(1, "idle", 0.0, 0.0), ProcessRows.scaleMax(idle), ProcessRows.scaleMax(idle))
        assertEquals(0f, row.cpuFraction, 0f)
        assertEquals(0f, row.ramFraction, 0f)
    }

    @Test
    fun `fractions are clamped to the bar`() {
        val row = ProcessRows.rowFor(ProcessInfo(7, "spike", 300.0, -5.0), maxCpu = 100.0, maxRam = 100.0)
        assertEquals(1f, row.cpuFraction, 0f)
        assertEquals(0f, row.ramFraction, 0f)
    }

    @Test
    fun `rows glide to new places only in the orders that hold still`() {
        // RemEx-wqo7a.8: by name or PID a row moves only when a process starts or ends.
        assertEquals(true, ProcessRows.animatesReorder(ProcessSortField.NAME))
        assertEquals(true, ProcessRows.animatesReorder(ProcessSortField.PID))
        // By CPU or memory the order reshuffles on every refresh; sliding rows would be constant noise.
        assertEquals(false, ProcessRows.animatesReorder(ProcessSortField.CPU))
        assertEquals(false, ProcessRows.animatesReorder(ProcessSortField.RAM))
    }
}

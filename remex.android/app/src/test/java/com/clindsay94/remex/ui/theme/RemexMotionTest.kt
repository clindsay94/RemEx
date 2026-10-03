package com.clindsay94.remex.ui.theme

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** Phase 6 motion rules (RemEx-wqo7a.7): tab direction, reduced-motion collapse, value roll direction. */
class RemexMotionTest {

    @Test
    fun `adjacent tab click animates from where the pager is`() {
        assertEquals(RemexMotion.TabScrollPlan(snapTo = null, animate = true), RemexMotion.tabScrollPlan(0, 1, reducedMotion = false))
        assertEquals(RemexMotion.TabScrollPlan(snapTo = null, animate = true), RemexMotion.tabScrollPlan(2, 1, reducedMotion = false))
    }

    @Test
    fun `far tab click snaps to the target's neighbour on the side it comes from`() {
        // Forward (Home -> Control): slide in from the page before the target.
        assertEquals(RemexMotion.TabScrollPlan(snapTo = 2, animate = true), RemexMotion.tabScrollPlan(0, 3, reducedMotion = false))
        // Back (Control -> Home): slide in from the page after the target.
        assertEquals(RemexMotion.TabScrollPlan(snapTo = 1, animate = true), RemexMotion.tabScrollPlan(3, 0, reducedMotion = false))
        assertEquals(RemexMotion.TabScrollPlan(snapTo = 2, animate = true), RemexMotion.tabScrollPlan(3, 1, reducedMotion = false))
    }

    @Test
    fun `reduced motion jumps straight to the tab without animating`() {
        assertEquals(RemexMotion.TabScrollPlan(snapTo = 3, animate = false), RemexMotion.tabScrollPlan(0, 3, reducedMotion = true))
        assertEquals(RemexMotion.TabScrollPlan(snapTo = 1, animate = false), RemexMotion.tabScrollPlan(0, 1, reducedMotion = true))
    }

    @Test
    fun `the tab already showing does nothing`() {
        assertEquals(RemexMotion.TabScrollPlan(snapTo = null, animate = false), RemexMotion.tabScrollPlan(2, 2, reducedMotion = false))
        assertEquals(RemexMotion.TabScrollPlan(snapTo = null, animate = false), RemexMotion.tabScrollPlan(2, 2, reducedMotion = true))
    }

    @Test
    fun `leading number reads formatted readings in either decimal separator`() {
        assertEquals(45.2, RemexMotion.leadingNumber("45.2 °C")!!, 1e-9)
        assertEquals(45.2, RemexMotion.leadingNumber("45,2 °C")!!, 1e-9)
        assertEquals(-3.0, RemexMotion.leadingNumber("-3 dB")!!, 1e-9)
        assertEquals(1200.0, RemexMotion.leadingNumber("1200 RPM")!!, 1e-9)
        assertNull(RemexMotion.leadingNumber("--"))
        assertNull(RemexMotion.leadingNumber(""))
    }

    @Test
    fun `value rolls up when it rose, down when it fell, and fades otherwise`() {
        assertEquals(1, RemexMotion.valueTrend("45 °C", "46 °C"))
        assertEquals(-1, RemexMotion.valueTrend("46 %", "9 %"))
        assertEquals(0, RemexMotion.valueTrend("--", "46 %"))
        assertEquals(0, RemexMotion.valueTrend("46 %", "--"))
        assertEquals(0, RemexMotion.valueTrend("46.0 %", "46 %"))
    }
}

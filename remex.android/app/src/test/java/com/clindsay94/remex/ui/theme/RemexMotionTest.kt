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
    fun `a far tab click keeps its target selected while the snap settles on the neighbour`() {
        // Home (0) -> Control (3): the selection is the target, the plan snaps to Apps (2) first.
        var selected = 3
        val plan = RemexMotion.tabScrollPlan(current = 0, target = selected, reducedMotion = false)
        assertEquals(2, plan.snapTo)
        var target: Int? = if (plan.snapTo != null || plan.animate) selected else null

        // The snap settles the pager on Apps (the gap before the slide starts, nothing scrolling)...
        var sync = RemexMotion.settledTabSync(settledPage = plan.snapTo!!, tabScrollTarget = target, scrollInProgress = false)
        sync.select?.let { selected = it }
        if (sync.reached) target = null
        assertEquals("the bar must not fall back to the neighbour mid-slide", 3, selected)
        assertEquals(3, target)

        // ...and stays settled there for the whole slide.
        sync = RemexMotion.settledTabSync(settledPage = 2, tabScrollTarget = target, scrollInProgress = true)
        sync.select?.let { selected = it }
        assertEquals(3, selected)

        // The slide lands on Control: selected stays, and the target is let go.
        sync = RemexMotion.settledTabSync(settledPage = 3, tabScrollTarget = target, scrollInProgress = false)
        sync.select?.let { selected = it }
        if (sync.reached) target = null
        assertEquals(3, selected)
        assertNull(target)
    }

    @Test
    fun `a drag that grabs the pager mid-slide syncs only once its fling settles`() {
        // Home (0) -> Control (3) is sliding from Apps; the user grabs it, which drops the target.
        var selected = 3
        var target: Int? = null
        // While the drag/fling runs, settledPage still reads the page the scroll started from (2).
        var sync = RemexMotion.settledTabSync(settledPage = 2, tabScrollTarget = target, scrollInProgress = true)
        sync.select?.let { selected = it }
        assertEquals("a scroll in progress must not copy its start page into the bar", 3, selected)

        // The fling settles on Desktop (1): now the bar follows it.
        sync = RemexMotion.settledTabSync(settledPage = 1, tabScrollTarget = target, scrollInProgress = false)
        sync.select?.let { selected = it }
        if (sync.reached) target = null
        assertEquals(1, selected)
        assertNull(target)
    }

    @Test
    fun `with no tab click in flight every settle selects its page`() {
        assertEquals(RemexMotion.SettledTabSync(select = 2, reached = false), RemexMotion.settledTabSync(2, null, scrollInProgress = false))
        assertEquals(RemexMotion.SettledTabSync(select = 0, reached = false), RemexMotion.settledTabSync(0, null, scrollInProgress = false))
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

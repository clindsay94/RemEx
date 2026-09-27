package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.home.PresenceEvent
import com.clindsay94.remex.routines.home.PresenceRecord
import com.clindsay94.remex.routines.home.PresenceState
import com.clindsay94.remex.routines.home.PresenceStateMachine
import com.clindsay94.remex.routines.home.PresenceStep
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Spec §13.3 `PresenceStateMachineTest`, T9, T20, R-SEC-14: nothing fires from UNKNOWN, the arrive
 * settle, the leave debounce and its reset, and the 300 s opposite-transition gap.
 */
class PresenceStateMachineTest {
    private val t0 = 1_790_000_000_000L
    private val leave = mapOf("leave-3m" to 180, "leave-10m" to 600)
    private val none = emptyMap<String, Int>()

    private fun step(p: PresenceRecord, matches: Boolean, at: Long, debounces: Map<String, Int> = leave, foreign: Boolean = !matches): PresenceStep =
        PresenceStateMachine.step(p, matches, at, debounces, 180, foreign)

    private fun homeSince(at: Long) = PresenceRecord(PresenceState.HOME, changedAtUnixMs = at, homeSinceUnixMs = at)

    private fun awaySince(at: Long) = PresenceRecord(PresenceState.AWAY, changedAtUnixMs = at)

    @Test
    fun `T20 - UNKNOWN never fires, at home or away (reboot or first run at home)`() {
        val atHome = step(PresenceRecord(), matches = true, at = t0)
        assertEquals(PresenceState.HOME, atHome.presence.state)
        assertTrue(atHome.events.isEmpty())
        val away = step(PresenceRecord(), matches = false, at = t0, foreign = true)
        assertEquals(PresenceState.AWAY, away.presence.state)
        assertTrue(away.events.isEmpty())
        // And an UNKNOWN -> AWAY phone has not "left": no leave ever follows from it.
        val later = step(away.presence, matches = false, at = t0 + 3_600_000)
        assertTrue(later.events.isEmpty())
    }

    @Test
    fun `L9 - reboot with no network stays UNKNOWN, so the first home join is learned, not an arrive`() {
        val booted = step(PresenceRecord(), matches = false, at = t0, foreign = false)
        assertEquals(PresenceState.UNKNOWN, booted.presence.state)
        assertTrue(booted.events.isEmpty())
        val wifi = step(booted.presence, matches = true, at = t0 + 20_000)
        assertEquals(PresenceState.HOME, wifi.presence.state)
        assertTrue("joining home after a reboot must not wake the PC", wifi.events.isEmpty())
        assertTrue(step(wifi.presence, matches = true, at = t0 + 40_000).events.isEmpty())
    }

    @Test
    fun `L9 - reboot on a foreign Wi-Fi is stamped, so an arrive within 300 s is suppressed`() {
        val booted = step(PresenceRecord(), matches = false, at = t0, foreign = true)
        assertEquals(PresenceState.AWAY, booted.presence.state)
        assertEquals(t0, booted.presence.changedAtUnixMs)
        val settle = step(booted.presence, matches = true, at = t0 + 30_000)
        val arrived = step(settle.presence, matches = true, at = t0 + 40_000)
        assertEquals(listOf(PresenceEvent.Arrive(suppressed = true)), arrived.events)
    }

    @Test
    fun `arrive fires only after home has matched for 10 s`() {
        val first = step(awaySince(t0 - 3_600_000), matches = true, at = t0)
        assertTrue(first.events.isEmpty())
        assertEquals(PresenceState.AWAY, first.presence.state)
        assertEquals(t0 + 10_000, first.recheckAtUnixMs)

        val early = step(first.presence, matches = true, at = t0 + 9_999)
        assertTrue(early.events.isEmpty())

        val settled = step(first.presence, matches = true, at = t0 + 10_000)
        assertEquals(listOf(PresenceEvent.Arrive(suppressed = false)), settled.events)
        assertEquals(PresenceState.HOME, settled.presence.state)
        assertNull(settled.recheckAtUnixMs)
    }

    @Test
    fun `losing home during the settle starts it again`() {
        val first = step(awaySince(t0 - 3_600_000), matches = true, at = t0)
        val lost = step(first.presence, matches = false, at = t0 + 5_000)
        assertNull(lost.presence.arriveSinceUnixMs)
        val again = step(lost.presence, matches = true, at = t0 + 12_000)
        assertTrue(again.events.isEmpty())
        assertEquals(t0 + 22_000, again.recheckAtUnixMs)
    }

    @Test
    fun `each leave routine fires after its own debounce, once`() {
        val home = homeSince(t0 - 3_600_000)
        val lost = step(home, matches = false, at = t0)
        assertTrue(lost.events.isEmpty())
        assertEquals(t0 + 180_000, lost.recheckAtUnixMs)

        val threeMin = step(lost.presence, matches = false, at = t0 + 180_000)
        assertEquals(listOf(PresenceEvent.Leave("leave-3m", suppressed = false)), threeMin.events)
        assertEquals(PresenceState.AWAY, threeMin.presence.state)
        assertEquals(t0 + 600_000, threeMin.recheckAtUnixMs)

        val tenMin = step(threeMin.presence, matches = false, at = t0 + 600_000)
        assertEquals(listOf(PresenceEvent.Leave("leave-10m", suppressed = false)), tenMin.events)
        assertNull(tenMin.recheckAtUnixMs)

        assertTrue(step(tenMin.presence, matches = false, at = t0 + 3_600_000).events.isEmpty())
    }

    @Test
    fun `any match before the debounce ends resets the leave`() {
        val lost = step(homeSince(t0 - 3_600_000), matches = false, at = t0)
        val back = step(lost.presence, matches = true, at = t0 + 120_000)
        assertNull(back.presence.awaySinceUnixMs)
        assertEquals(PresenceState.HOME, back.presence.state)
        val lostAgain = step(back.presence, matches = false, at = t0 + 130_000)
        // The debounce restarted: nothing at the first deadline.
        assertTrue(step(lostAgain.presence, matches = false, at = t0 + 180_000).events.isEmpty())
        assertEquals(1, step(lostAgain.presence, matches = false, at = t0 + 310_000).events.size)
    }

    @Test
    fun `with no leave routine the state still turns AWAY at the default debounce`() {
        val lost = step(homeSince(t0 - 3_600_000), matches = false, at = t0, debounces = none)
        assertEquals(t0 + 180_000, lost.recheckAtUnixMs)
        val away = step(lost.presence, matches = false, at = t0 + 180_000, debounces = none)
        assertEquals(PresenceState.AWAY, away.presence.state)
        assertTrue(away.events.isEmpty())
    }

    @Test
    fun `an arrive within 300 s of leaving is a flap and is suppressed`() {
        val justLeft = awaySince(t0)
        val settle = step(justLeft, matches = true, at = t0 + 60_000)
        val arrived = step(settle.presence, matches = true, at = t0 + 70_000)
        assertEquals(listOf(PresenceEvent.Arrive(suppressed = true)), arrived.events)
        assertEquals(PresenceState.HOME, arrived.presence.state)
    }

    @Test
    fun `a leave within 300 s of arriving is a flap and is suppressed`() {
        val arrived = homeSince(t0)
        val lost = step(arrived, matches = false, at = t0 + 10_000)
        val left = step(lost.presence, matches = false, at = t0 + 190_000)
        assertEquals(listOf(PresenceEvent.Leave("leave-3m", suppressed = true)), left.events)
    }

    @Test
    fun `after the gap both directions fire again`() {
        val justLeft = awaySince(t0)
        val settle = step(justLeft, matches = true, at = t0 + 300_000)
        assertEquals(listOf(PresenceEvent.Arrive(suppressed = false)), step(settle.presence, matches = true, at = t0 + 310_000).events)
    }
}

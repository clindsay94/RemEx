package com.clindsay94.remex.routines

import com.clindsay94.remex.EstablishedConnection
import com.clindsay94.remex.HostInfoForConnection
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * `supportsRoutines` is read only from a `host_info` of the current connection to the targeted PC
 * (RemEx-pp0rt.5, S1c review item 4). The replaying `hostCapabilities` flow outlives its connection,
 * and reading it gave a fresh connection the previous PC's answer: a false `pc_too_old`, or a step
 * sent to a PC that never answers it (`step_timeout`).
 */
class RoutineHostCapabilitiesTest {
    private val current = EstablishedConnection("192.168.1.20", 5005, epoch = 7)
    private val routinesJson = """{"supportsRoutines":true}"""

    private fun check(info: HostInfoForConnection?, identity: String? = HOST, target: String = HOST, connection: EstablishedConnection? = current) =
        RoutineHostCapabilities.supportsRoutines(info, connection, identity, target)

    @Test
    fun `a host_info from this connection to the targeted PC answers`() {
        assertEquals(true, check(HostInfoForConnection(current, routinesJson)))
        assertEquals(false, check(HostInfoForConnection(current, """{"supportsRemoteDesktop":true}""")))
    }

    @Test
    fun `the previous connection's host_info is ignored, even from the same address`() {
        assertNull(check(HostInfoForConnection(current.copy(epoch = 6), routinesJson)))
    }

    @Test
    fun `a host_info is ignored when the authenticated PC is not the one the run targets`() {
        assertNull(check(HostInfoForConnection(current, routinesJson), target = OTHER_HOST))
        assertNull(check(HostInfoForConnection(current, routinesJson), identity = null))
    }

    @Test
    fun `nothing yet, or no authenticated connection, is no answer`() {
        assertNull(check(null))
        assertNull(check(HostInfoForConnection(current, routinesJson), connection = null))
    }
}

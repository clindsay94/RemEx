package com.clindsay94.remex.ui.screens

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Sweep P3 (RemEx-wqo7a.7): the process list says "needs pairing" instead of fetching forever, and
 * the Pair action's request to open Add a PC is acted on exactly once.
 */
class NeedsPairingStateTest {

    @After
    fun clearRequest() {
        ConnectionOpenRequests.consumeAddPc()
    }

    @Test
    fun `a connected PC that needs pairing shows the pairing state, not the spinner`() {
        assertEquals(
            TaskManagerListState.NEEDS_PAIRING,
            taskManagerListState(isConnected = true, needsPairing = true, hasProcesses = false, isRefreshing = true),
        )
        // Even a list from earlier is replaced: nothing on it can be acted on.
        assertEquals(
            TaskManagerListState.NEEDS_PAIRING,
            taskManagerListState(isConnected = true, needsPairing = true, hasProcesses = true, isRefreshing = false),
        )
    }

    @Test
    fun `the other states are unchanged`() {
        assertEquals(
            TaskManagerListState.DISCONNECTED,
            taskManagerListState(isConnected = false, needsPairing = false, hasProcesses = false, isRefreshing = false),
        )
        assertEquals(
            TaskManagerListState.LOADING,
            taskManagerListState(isConnected = true, needsPairing = false, hasProcesses = false, isRefreshing = true),
        )
        assertEquals(
            TaskManagerListState.LIST,
            taskManagerListState(isConnected = true, needsPairing = false, hasProcesses = true, isRefreshing = true),
        )
    }

    @Test
    fun `a search that matches nothing says so instead of showing an empty list`() {
        assertEquals(
            TaskManagerListState.NO_MATCHES,
            taskManagerListState(
                isConnected = true, needsPairing = false, hasProcesses = true, isRefreshing = false,
                hasVisibleProcesses = false, searchQuery = "zzz",
            ),
        )
        // A refresh in flight must not turn a no-match search back into the spinner: processes have arrived.
        assertEquals(
            TaskManagerListState.NO_MATCHES,
            taskManagerListState(
                isConnected = true, needsPairing = false, hasProcesses = true, isRefreshing = true,
                hasVisibleProcesses = false, searchQuery = "zzz",
            ),
        )
        // No query, or matches left, or nothing sent yet: not a no-match state.
        assertEquals(
            TaskManagerListState.LIST,
            taskManagerListState(
                isConnected = true, needsPairing = false, hasProcesses = true, isRefreshing = false,
                hasVisibleProcesses = false, searchQuery = "  ",
            ),
        )
        assertEquals(
            TaskManagerListState.LIST,
            taskManagerListState(
                isConnected = true, needsPairing = false, hasProcesses = true, isRefreshing = false,
                hasVisibleProcesses = true, searchQuery = "chr",
            ),
        )
        assertEquals(
            TaskManagerListState.LOADING,
            taskManagerListState(
                isConnected = true, needsPairing = false, hasProcesses = false, isRefreshing = true,
                hasVisibleProcesses = false, searchQuery = "chr",
            ),
        )
    }

    @Test
    fun `parseKillResponse reads success, a refusal's message, and treats anything unreadable as a failure`() {
        assertEquals(KillResponse(true, null), parseKillResponse("""{"success":true}"""))
        assertEquals(KillResponse(false, "denied"), parseKillResponse("""{"success":false,"message":"denied"}"""))
        assertEquals(KillResponse(false, null), parseKillResponse("""{"success":false,"message":"  "}"""))
        assertEquals(KillResponse(false, null), parseKillResponse(""))
        assertEquals(KillResponse(false, null), parseKillResponse(null))
        assertEquals(KillResponse(false, null), parseKillResponse("garbage"))
    }

    @Test
    fun `Apps says the PC needs pairing instead of a list it would refuse to launch from`() {
        assertEquals(
            AppLauncherBody.NeedsPairing,
            appLauncherBodyFor(isConnected = true, hasApps = false, refreshState = LauncherRefreshState.Refreshing, needsPairing = true),
        )
        assertEquals(
            AppLauncherBody.NeedsPairing,
            appLauncherBodyFor(isConnected = true, hasApps = true, refreshState = LauncherRefreshState.Idle, needsPairing = true),
        )
        // Disconnected, the flag means nothing: the last list (or the disconnected state) shows.
        assertEquals(
            AppLauncherBody.Apps,
            appLauncherBodyFor(isConnected = false, hasApps = true, refreshState = LauncherRefreshState.Idle, needsPairing = true),
        )
    }

    @Test
    fun `an Add a PC request is consumed once`() {
        assertFalse(ConnectionOpenRequests.consumeAddPc())
        ConnectionOpenRequests.requestAddPc(atMs = 1_000L)
        assertEquals(1_000L, ConnectionOpenRequests.addPc.value)
        assertTrue(ConnectionOpenRequests.consumeAddPc(atMs = 1_400L))
        assertFalse(ConnectionOpenRequests.consumeAddPc(atMs = 1_500L))
        assertEquals(null, ConnectionOpenRequests.addPc.value)
    }

    @Test
    fun `a request that never reached Connection does not open the sheet on a later visit`() {
        ConnectionOpenRequests.requestAddPc(atMs = 1_000L)
        // Connection is next opened long after the Pair tap: the stale request is dropped unopened.
        assertFalse(ConnectionOpenRequests.consumeAddPc(atMs = 1_000L + ConnectionOpenRequests.MAX_AGE_MS + 1))
        assertEquals(null, ConnectionOpenRequests.addPc.value)
        assertFalse(ConnectionOpenRequests.consumeAddPc(atMs = 1_000L + ConnectionOpenRequests.MAX_AGE_MS + 2))
    }
}

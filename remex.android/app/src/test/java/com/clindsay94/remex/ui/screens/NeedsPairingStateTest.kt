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
        ConnectionOpenRequests.requestAddPc()
        assertTrue(ConnectionOpenRequests.addPc.value)
        assertTrue(ConnectionOpenRequests.consumeAddPc())
        assertFalse(ConnectionOpenRequests.consumeAddPc())
        assertFalse(ConnectionOpenRequests.addPc.value)
    }
}

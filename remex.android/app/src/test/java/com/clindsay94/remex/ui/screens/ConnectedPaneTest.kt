package com.clindsay94.remex.ui.screens

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * One rule behind Desktop, Sensors and Commands (3.0 comb, needs-pairing-gaps, commands-no-offline;
 * RemEx-pp4cm.3): offline says so, "needs pairing" asks to pair, and only Wake works with no link.
 */
class ConnectedPaneTest {

    @Test
    fun `offline wins over needs pairing`() {
        assertEquals(ConnectedPaneState.Disconnected, ConnectedPane.state(isConnected = false, needsPairing = true))
        assertEquals(ConnectedPaneState.Disconnected, ConnectedPane.state(isConnected = false, needsPairing = false))
    }

    @Test
    fun `connected but unrecognised needs pairing`() {
        assertEquals(ConnectedPaneState.NeedsPairing, ConnectedPane.state(isConnected = true, needsPairing = true))
    }

    @Test
    fun `connected and recognised is ready`() {
        assertEquals(ConnectedPaneState.Ready, ConnectedPane.state(isConnected = true, needsPairing = false))
    }

    @Test
    fun `offline only wake is usable`() {
        assertTrue(ConnectedPane.isCommandAvailable(ConnectedPane.WAKE_ACTION, isConnected = false))
        assertFalse(ConnectedPane.isCommandAvailable("Lock", isConnected = false))
        assertFalse(ConnectedPane.isCommandAvailable("Shutdown", isConnected = false))
        assertTrue(ConnectedPane.isCommandAvailable("Lock", isConnected = true))
    }
}

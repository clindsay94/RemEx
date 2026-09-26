package com.clindsay94.remex

import com.clindsay94.remex.widget.WIDGET_CONNECT_TIMEOUT_MS
import com.clindsay94.remex.widget.WidgetConnectWait
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins what a Remote Control / App Launcher tap waits on during its one-shot connect (live-check A8).
 *
 * The tap only sends once the host has acked the reconnect proof: a command sent on a bare open
 * socket is refused as unpaired and vanishes (RemEx-0vpw5). A finished attempt with no connection
 * (no pin stored, host down) must end the wait at once rather than sit out the timeout.
 */
class WidgetConnectWaitTest {

    @Test
    fun `authenticated sends`() {
        assertEquals(true, WidgetConnectWait.verdict(authenticated = true, connected = true, connecting = false))
    }

    @Test
    fun `socket open but proof not yet acked keeps waiting`() {
        assertNull(WidgetConnectWait.verdict(authenticated = false, connected = true, connecting = false))
        assertNull(WidgetConnectWait.verdict(authenticated = false, connected = true, connecting = true))
    }

    @Test
    fun `attempt in flight keeps waiting`() {
        assertNull(WidgetConnectWait.verdict(authenticated = false, connected = false, connecting = true))
    }

    @Test
    fun `attempt over with no connection gives up at once`() {
        assertEquals(false, WidgetConnectWait.verdict(authenticated = false, connected = false, connecting = false))
    }

    @Test
    fun `the wait fits inside the goAsync broadcast window with room to spare`() {
        assertTrue(WIDGET_CONNECT_TIMEOUT_MS in 1_000L..6_000L)
    }
}

package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.DesktopSocketIdlePolicy
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Covers when a stream-less `/ws/desktop` socket (opened by the display-catalog preload or a window
 * request) may be closed (perf audit P0-7).
 *
 * The failure this guards is the silent-black-stream class: the catalog's reply triggers a waiting
 * DesktopStart on the same socket, and a close that lands after that start tears down the stream it
 * just brought up. So any running, pending or scheduled start must veto the close.
 */
class DesktopSocketIdlePolicyTest {

    private fun mayClose(
        open: Boolean = true,
        streaming: Boolean = false,
        pending: Boolean = false,
        scheduled: Boolean = false,
    ) = DesktopSocketIdlePolicy.mayClose(open, streaming, pending, scheduled)

    @Test
    fun `a side-request socket with no stream closes`() {
        assertTrue(mayClose())
    }

    @Test
    fun `nothing opened means nothing to close`() {
        assertFalse(mayClose(open = false))
    }

    @Test
    fun `a running stream owns the socket`() {
        assertFalse(mayClose(streaming = true))
    }

    @Test
    fun `a start waiting on the catalog reply vetoes the close`() {
        // The display query immediately followed by Start: the reply will start the stream on this
        // very socket, so closing now would race it.
        assertFalse(mayClose(pending = true))
    }

    @Test
    fun `a scheduled reconnect or display-switch restart vetoes the close`() {
        assertFalse(mayClose(scheduled = true))
    }

    @Test
    fun `the idle window is inside the P0-7 bounds`() {
        assertTrue(DesktopSocketIdlePolicy.IDLE_CLOSE_MS in 30_000L..60_000L)
    }
}

package com.clindsay94.remex

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Covers when the auto-reconnect heartbeat may try to reach the PC (perf audit P0-11).
 *
 * Both directions fail silently. Too permissive and a phone in a pocket with no signal wakes to
 * retry TLS and burst multicast forever. Too strict and the connection never comes back - the app
 * or widget just shows "disconnected" with nothing in the log saying the gate is why.
 */
class ReconnectGateTest {

    private fun allows(
            network: Boolean = true,
            foreground: Boolean = true,
            screenOn: Boolean = true,
            widget: Boolean = false,
    ) = ReconnectGate.allows(
            networkAvailable = network,
            foreground = foreground,
            screenInteractive = screenOn,
            widgetPlaced = widget,
    )

    @Test
    fun `a foreground app with a network reconnects`() {
        assertTrue(allows())
    }

    @Test
    fun `no network never reconnects, whatever else holds`() {
        for (foreground in listOf(true, false)) for (screenOn in listOf(true, false)) for (widget in listOf(true, false)) {
            assertFalse(
                    "fg=$foreground screen=$screenOn widget=$widget",
                    allows(network = false, foreground = foreground, screenOn = screenOn, widget = widget),
            )
        }
    }

    @Test
    fun `a backgrounded app with no widget does not reconnect`() {
        assertFalse(allows(foreground = false, screenOn = true, widget = false))
        assertFalse(allows(foreground = false, screenOn = false, widget = false))
    }

    @Test
    fun `a placed hardware widget keeps reconnecting in the background while the screen is on`() {
        assertTrue(allows(foreground = false, screenOn = true, widget = true))
    }

    @Test
    fun `a placed widget does not keep reconnecting once the screen is off`() {
        assertFalse(allows(foreground = false, screenOn = false, widget = true))
    }

    @Test
    fun `foreground reconnects regardless of the screen and widget signals`() {
        // Screen state is only a qualifier on the widget exception; a started UI is enough on its own.
        assertTrue(allows(foreground = true, screenOn = false, widget = false))
    }

    @Test
    fun `backoff doubles from the base interval and caps at five minutes`() {
        assertEquals(5_000L, ReconnectGate.backoffDelayMs(0))
        assertEquals(10_000L, ReconnectGate.backoffDelayMs(1))
        assertEquals(20_000L, ReconnectGate.backoffDelayMs(2))
        assertEquals(160_000L, ReconnectGate.backoffDelayMs(5))
        assertEquals(300_000L, ReconnectGate.backoffDelayMs(6))
        assertEquals(300_000L, ReconnectGate.backoffDelayMs(20))
    }

    @Test
    fun `backoff does not overflow on a very long failure streak`() {
        assertEquals(300_000L, ReconnectGate.backoffDelayMs(1_000))
        assertEquals(300_000L, ReconnectGate.backoffDelayMs(Int.MAX_VALUE))
    }
}

package com.clindsay94.remex

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Before
import org.junit.Test

/**
 * Regression coverage for the connection-state contract in [RemexClientManager].
 *
 * `review-report.md` claimed (incorrectly, as of the 2.0 branch state when the
 * report was filed) that [RemexClientManager.onConnectionStateChanged] with
 * `isConnected = false` and [RemexClientManager.onConnectionError] both leave
 * the internal `_isConnecting` flag stuck true, deadlocking the heartbeat retry
 * loop in [RemexClientManager.initialize]. The current implementation does
 * clear the flag in both paths — these tests pin that contract so a future
 * regression cannot silently re-introduce the deadlock.
 *
 * Note that [RemexClientManager] is a Kotlin `object` (process singleton). Each
 * test uses [RemexClientManager.setConnecting] to pre-set the flag rather than
 * relying on cross-test isolation; the existing `_isConnecting` is unset on a
 * fresh process by virtue of `MutableStateFlow(false)` default.
 */
class RemexClientManagerStateTests {

    @Before
    fun arrangeConnecting() {
        // Force `_isConnecting = true` so the assertions below verify that the
        // callbacks under test cleared it (vs. observing the default false).
        RemexClientManager.setConnecting(true)
        assertEquals(true, RemexClientManager.isConnecting.value)
    }

    @Test
    fun `onConnectionStateChanged(false) clears _isConnecting`() {
        RemexClientManager.onConnectionStateChanged(isConnected = false)

        assertEquals(false, RemexClientManager.isConnected.value)
        assertFalse(
            "_isConnecting must be cleared on disconnect callback — otherwise the heartbeat loop deadlocks.",
            RemexClientManager.isConnecting.value
        )
    }

    @Test
    fun `onConnectionStateChanged(true) clears _isConnecting and marks connected`() {
        RemexClientManager.onConnectionStateChanged(isConnected = true)

        assertEquals(true, RemexClientManager.isConnected.value)
        assertFalse(
            "_isConnecting must be cleared on successful connect callback.",
            RemexClientManager.isConnecting.value
        )

        // Cleanup so the singleton state doesn't leak into other tests in this module.
        RemexClientManager.onConnectionStateChanged(isConnected = false)
    }

    @Test
    fun `onConnectionError clears both _isConnecting and _isConnected`() {
        // Simulate the connected state as well so we verify both flags are knocked down.
        RemexClientManager.onConnectionStateChanged(isConnected = true)
        RemexClientManager.setConnecting(true)

        RemexClientManager.onConnectionError("simulated transport failure")

        assertFalse(
            "_isConnected must be cleared when the native client reports an error.",
            RemexClientManager.isConnected.value
        )
        assertFalse(
            "_isConnecting must be cleared on error so the heartbeat retry loop can attempt again.",
            RemexClientManager.isConnecting.value
        )
    }

    // ---- connectedHost: which PC connected, not merely that one did (RemEx-bz9t) ----
    //
    // The Known PCs list stamps `lastConnectedAtMillis` from this flow. It used to stamp from the
    // false-to-true edge of `isConnected` plus a settings lookup, and both halves could lie: a
    // StateFlow only promises its collector the latest value and drops one equal to the last it
    // delivered, so a collector not scheduled between the disconnect and the reconnect of a host
    // switch sees `true` after `true` and never re-fires; and the settings name the PC the user
    // asked for, which `connect` writes before the connection is attempted and may never reach.
    //
    // These tests are written against a collector that observed NOTHING in between, because that is
    // the worst case and the one that was failing. Distinct consecutive values are what make it
    // harmless: a slow collector may fall behind, but it can never mistake a new connection for the
    // one it already handled.

    @Test
    fun `a host switch is a new value even if the disconnect between is never observed`() {
        RemexClientManager.setPendingTarget("192.168.1.10", 5001)
        RemexClientManager.onConnectionStateChanged(isConnected = true)
        val first = RemexClientManager.connectedHost.value

        // The native client does close the old socket first — ConnectAsync awaits DisconnectAsync,
        // which raises ConnectionStateChanged(false) unconditionally. Deliberately NOT replayed
        // here: the bug was a collector that missed exactly this.
        RemexClientManager.setPendingTarget("192.168.1.20", 5002)
        RemexClientManager.onConnectionStateChanged(isConnected = true)
        val second = RemexClientManager.connectedHost.value

        assertNotNull("A successful connection must name the PC it reached.", first)
        assertNotNull("A successful connection must name the PC it reached.", second)
        assertNotEquals(
            "Switching PCs must produce a value a StateFlow collector will re-deliver — an equal " +
                "value is conflated away and the new PC is never stamped.",
            first,
            second
        )
        assertEquals("192.168.1.20", second?.host)
        assertEquals(5002, second?.port)

        RemexClientManager.onConnectionStateChanged(isConnected = false)
    }

    @Test
    fun `reconnecting to the same PC is still a new value`() {
        RemexClientManager.setPendingTarget("192.168.1.10", 5001)
        RemexClientManager.onConnectionStateChanged(isConnected = true)
        val first = RemexClientManager.connectedHost.value

        RemexClientManager.onConnectionStateChanged(isConnected = false)
        RemexClientManager.onConnectionStateChanged(isConnected = true)
        val second = RemexClientManager.connectedHost.value

        assertEquals("192.168.1.10", second?.host)
        assertNotEquals(
            "Host and port alone would compare equal across a reconnect to the same PC, so the " +
                "stamp would be skipped whenever the drop itself went unobserved.",
            first,
            second
        )

        RemexClientManager.onConnectionStateChanged(isConnected = false)
    }

    @Test
    fun `connectedHost is cleared while disconnected`() {
        RemexClientManager.setPendingTarget("192.168.1.10", 5001)
        RemexClientManager.onConnectionStateChanged(isConnected = true)

        RemexClientManager.onConnectionStateChanged(isConnected = false)

        assertNull(
            "Nothing is connected, so nothing may be stamped.",
            RemexClientManager.connectedHost.value
        )
    }

    @Test
    fun `aiming at a second PC does not name it until it answers`() {
        RemexClientManager.setPendingTarget("192.168.1.10", 5001)
        RemexClientManager.onConnectionStateChanged(isConnected = true)

        // The user tapped the other PC's row: settings are already rewritten, the connection has
        // not been made, and may never be. A stamp here would reorder the list around a PC that
        // never answered — the false-positive RemEx-k62t refused to ship.
        RemexClientManager.setPendingTarget("192.168.1.20", 5002)

        assertEquals(
            "Only an established connection may change which PC is named.",
            "192.168.1.10",
            RemexClientManager.connectedHost.value?.host
        )

        RemexClientManager.onConnectionStateChanged(isConnected = false)
    }

    @Test
    fun `a failed attempt names no PC`() {
        RemexClientManager.setPendingTarget("192.168.1.10", 5001)
        RemexClientManager.onConnectionStateChanged(isConnected = true)

        RemexClientManager.setPendingTarget("192.168.1.20", 5002)
        RemexClientManager.onConnectionError("simulated transport failure")

        assertNull(
            "A connection that failed must leave nothing to stamp.",
            RemexClientManager.connectedHost.value
        )
    }
}

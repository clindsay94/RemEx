package com.clindsay94.remex

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
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
}

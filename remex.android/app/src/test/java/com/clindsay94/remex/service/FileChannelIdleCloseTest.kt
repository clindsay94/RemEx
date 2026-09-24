package com.clindsay94.remex.service

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Covers the idle-close decision for the binary `/ws/files` channel (perf audit P0-7).
 *
 * The failure this guards against is the ack-before-complete guard (docs/REGRESSION-GUARDS.md,
 * "Never announce `file_transfer_complete` before the peer has acked the data") arriving by a new
 * road: an idle timer that closes the socket while a transfer's sink is still registered — i.e.
 * while its drain or completion handshake is in flight — produces the same "Transfer incomplete" as
 * announcing completion early. So a live sink or a held lease must beat any amount of quiet.
 */
class FileChannelIdleCloseTest {

    private val window = FILE_CHANNEL_FOREGROUND_IDLE_MS

    @Test
    fun `a closed channel stops the watcher`() {
        assertNull(fileChannelIdleDelayMs(open = false, liveSinks = 0, leases = 0, quietMs = 0, idleWindowMs = window))
        assertNull(fileChannelIdleDelayMs(open = false, liveSinks = 3, leases = 1, quietMs = 999_999, idleWindowMs = window))
    }

    @Test
    fun `an open channel with nothing on it closes once the window has passed`() {
        assertEquals(0L, fileChannelIdleDelayMs(open = true, liveSinks = 0, leases = 0, quietMs = window, idleWindowMs = window))
        assertEquals(0L, fileChannelIdleDelayMs(open = true, liveSinks = 0, leases = 0, quietMs = window * 10, idleWindowMs = window))
    }

    @Test
    fun `an idle channel inside its window waits exactly the remainder`() {
        assertEquals(window - 1_000L, fileChannelIdleDelayMs(open = true, liveSinks = 0, leases = 0, quietMs = 1_000L, idleWindowMs = window))
    }

    @Test
    fun `a registered sink is never idle, however long the quiet`() {
        // A sink stays registered until its drain and completion handshake finish; closing under it
        // is the RG ack-before-complete failure.
        val next = fileChannelIdleDelayMs(open = true, liveSinks = 1, leases = 0, quietMs = Long.MAX_VALUE / 2, idleWindowMs = window)
        assertEquals(window, next)
    }

    @Test
    fun `a held lease is never idle, however long the quiet`() {
        // The lease spans ensureConnected -> negotiate -> registerSink, and a resumed download's
        // re-hash, none of which has a sink yet.
        val next = fileChannelIdleDelayMs(open = true, liveSinks = 0, leases = 1, quietMs = Long.MAX_VALUE / 2, idleWindowMs = window)
        assertEquals(window, next)
    }

    @Test
    fun `backgrounding shortens the window but stays inside the P0-7 bounds`() {
        assertEquals(FILE_CHANNEL_FOREGROUND_IDLE_MS, fileChannelIdleWindowMs(appForeground = true))
        assertEquals(FILE_CHANNEL_BACKGROUND_IDLE_MS, fileChannelIdleWindowMs(appForeground = false))
        assertTrue(FILE_CHANNEL_BACKGROUND_IDLE_MS < FILE_CHANNEL_FOREGROUND_IDLE_MS)
        assertTrue(FILE_CHANNEL_FOREGROUND_IDLE_MS in 30_000L..60_000L)
        assertTrue(FILE_CHANNEL_BACKGROUND_IDLE_MS > 0L)
    }

    @Test
    fun `a busy channel is rechecked no later than one window from now`() {
        // The watcher polls while busy so the close lands within one window of the transfer ending.
        val bg = fileChannelIdleWindowMs(appForeground = false)
        assertEquals(bg, fileChannelIdleDelayMs(open = true, liveSinks = 2, leases = 1, quietMs = 0, idleWindowMs = bg))
    }
}

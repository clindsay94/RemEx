@file:OptIn(ExperimentalCoroutinesApi::class)

package com.clindsay94.remex

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * [PairingAckWatch] (sweep P3, RemEx-wqo7a.7): a connection whose reconnect proof the PC never
 * accepts reads as "needs pairing" after the grace, and an ack, a disconnect or a new connection
 * clears it. A PC too old to send the ack is never flagged. Virtual time only.
 */
class PairingAckWatchTest {
    private val a = EstablishedConnection("192.168.1.10", 5005, epoch = 1)
    private val b = EstablishedConnection("192.168.1.10", 5005, epoch = 2)

    @Test(timeout = 5_000)
    fun `a connection the PC never acks needs pairing once the grace runs out`() = runTest {
        val connected = MutableStateFlow<EstablishedConnection?>(null)
        val acked = MutableStateFlow<EstablishedConnection?>(null)
        val acking = MutableStateFlow<EstablishedConnection?>(null)
        val seen = mutableListOf<Boolean>()
        val job = launch { PairingAckWatch.needsPairing(connected, acked, acking, graceMs = 1_000).collect { seen += it } }
        runCurrent()

        connected.value = a
        acking.value = a
        runCurrent()
        advanceTimeBy(999)
        runCurrent()
        assertEquals(listOf(false), seen)

        advanceTimeBy(2)
        runCurrent()
        assertEquals(listOf(false, true), seen)
        job.cancel()
    }

    @Test(timeout = 5_000)
    fun `an ack inside the grace never shows the warning`() = runTest {
        val connected = MutableStateFlow<EstablishedConnection?>(a)
        val acked = MutableStateFlow<EstablishedConnection?>(null)
        val acking = MutableStateFlow<EstablishedConnection?>(a)
        val seen = mutableListOf<Boolean>()
        val job = launch { PairingAckWatch.needsPairing(connected, acked, acking, graceMs = 1_000).collect { seen += it } }
        runCurrent()
        advanceTimeBy(500)
        acked.value = a
        runCurrent()
        advanceTimeBy(5_000)
        runCurrent()
        assertFalse(seen.contains(true))
        job.cancel()
    }

    @Test(timeout = 5_000)
    fun `a late ack or a disconnect clears it`() = runTest {
        val connected = MutableStateFlow<EstablishedConnection?>(a)
        val acked = MutableStateFlow<EstablishedConnection?>(null)
        val acking = MutableStateFlow<EstablishedConnection?>(a)
        var latest = false
        val job = launch { PairingAckWatch.needsPairing(connected, acked, acking, graceMs = 1_000).collect { latest = it } }
        advanceTimeBy(1_001)
        runCurrent()
        assertTrue(latest)

        acked.value = a
        runCurrent()
        assertFalse(latest)

        acked.value = null
        connected.value = null
        runCurrent()
        advanceTimeBy(5_000)
        runCurrent()
        assertFalse("nothing is connected, so nothing needs pairing", latest)
        job.cancel()
    }

    @Test(timeout = 5_000)
    fun `the previous connection's ack does not vouch for a new one`() = runTest {
        val connected = MutableStateFlow<EstablishedConnection?>(a)
        val acked = MutableStateFlow<EstablishedConnection?>(a)
        val acking = MutableStateFlow<EstablishedConnection?>(a)
        var latest = false
        val job = launch { PairingAckWatch.needsPairing(connected, acked, acking, graceMs = 1_000).collect { latest = it } }
        runCurrent()
        assertFalse(latest)

        // A host switch / reconnect: new epoch, and the old ack is still what the flow holds.
        connected.value = b
        acking.value = b
        runCurrent()
        advanceTimeBy(1_001)
        runCurrent()
        assertTrue(latest)
        job.cancel()
    }

    @Test(timeout = 5_000)
    fun `a PC too old to send the ack is never flagged`() = runTest {
        val connected = MutableStateFlow<EstablishedConnection?>(a)
        val acked = MutableStateFlow<EstablishedConnection?>(null)
        // No host_info that says this PC acks, or only the previous connection's.
        val acking = MutableStateFlow<EstablishedConnection?>(null)
        val seen = mutableListOf<Boolean>()
        val job = launch { PairingAckWatch.needsPairing(connected, acked, acking, graceMs = 1_000).collect { seen += it } }
        runCurrent()
        advanceTimeBy(10_000)
        runCurrent()
        assertFalse(seen.contains(true))

        connected.value = b
        acking.value = a
        runCurrent()
        advanceTimeBy(10_000)
        runCurrent()
        assertFalse("an older connection's host_info says nothing about this one", seen.contains(true))
        job.cancel()
    }
}

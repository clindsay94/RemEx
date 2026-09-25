package com.clindsay94.remex.service

import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.currentTime
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Perf audit P4-4: the legacy v2 base64 loops pace themselves on the native outbound queue depth
 * instead of pushing a whole file into it. The queue itself stays unbounded (P0-12: a bounded
 * drop-on-full queue would punch holes in the upload, and DesktopInput / MediaSeek share it).
 */
@OptIn(ExperimentalCoroutinesApi::class)
class LegacyOutboundBackpressureTest {

    @Test
    fun belowTheHighWaterMark_returnsWithoutWaiting() = runTest {
        val polls = AtomicInteger()
        awaitLegacyOutboundRoom(depth = { polls.incrementAndGet(); 0 }, highWater = 4, pollMs = 5)
        assertEquals(1, polls.get())
        assertEquals(0L, currentTime)
    }

    @Test
    fun atOrAboveTheHighWaterMark_waitsUntilTheQueueDrains() = runTest {
        val depth = AtomicInteger(4)
        var returned = false
        val job = launch {
            awaitLegacyOutboundRoom(depth = { depth.get() }, highWater = 4, pollMs = 5)
            returned = true
        }
        advanceTimeBy(50)
        runCurrent()
        assertFalse("must hold while depth == highWater", returned)
        depth.set(3)
        advanceTimeBy(6)
        runCurrent()
        assertTrue(returned)
        job.join()
    }

    @Test
    fun isCancellable_soAUserCancelUnwindsAStalledUpload() = runTest {
        val job = launch { awaitLegacyOutboundRoom(depth = { 100 }, highWater = 4, pollMs = 5) }
        advanceTimeBy(20)
        job.cancel()
        runCurrent()
        assertTrue(job.isCancelled)
        assertTrue(job.isCompleted)
    }

    @Test
    fun theDefaultsKeepTheBacklogSmall() {
        // ~87 KB of base64 per 64 KiB chunk: 16 queued is ~1.4 MB, versus the whole file before.
        assertTrue(LEGACY_OUTBOUND_HIGH_WATER in 2..32)
        assertTrue(LEGACY_OUTBOUND_POLL_MS in 1L..20L)
    }
}

package com.clindsay94.remex.service

import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * RemEx-pp4cm.9: the wait for a download's last frame was bounded only by the six-hour ceiling, so
 * one lost frame held the queue's single running slot for hours and every file behind it sat on
 * "Queued". Silence now ends it.
 */
class DownloadStreamWaitTest {

    private val clock = { System.nanoTime() / 1_000_000L }

    @Test
    fun aStreamThatGoesQuiet_endsTheWaitWithFailure_longBeforeTheCeiling() = runBlocking {
        val done = CompletableDeferred<Boolean>()
        val startedAt = clock()

        val ok = withTimeout(5_000) {
            awaitDownloadStream(
                done = done,
                ceilingMs = 6L * 60 * 60 * 1000,
                stallMs = 150,
                lastActivityMs = { startedAt },
                clockMs = clock,
                pollMs = 20,
            )
        }

        assertFalse(ok)
    }

    @Test
    fun aStreamThatKeepsDeliveringFrames_isNotCutOff() = runBlocking {
        val done = CompletableDeferred<Boolean>()
        val last = AtomicLong(clock())
        val feeder = launch {
            repeat(10) {
                delay(40)
                last.set(clock())
            }
            done.complete(true)
        }

        val ok = withTimeout(5_000) {
            awaitDownloadStream(
                done = done,
                ceilingMs = 60_000,
                stallMs = 150,
                lastActivityMs = { last.get() },
                clockMs = clock,
                pollMs = 20,
            )
        }

        feeder.join()
        assertTrue("400 ms of steady frames outlives a 150 ms stall limit", ok)
    }

    @Test
    fun theStreamsOwnVerdictIsReturnedAsIs() = runBlocking {
        val failed = CompletableDeferred<Boolean>().apply { complete(false) }
        val passed = CompletableDeferred<Boolean>().apply { complete(true) }

        fun wait(done: CompletableDeferred<Boolean>) = runBlocking {
            awaitDownloadStream(done, 60_000, 60_000, { clock() }, clock, pollMs = 20)
        }

        assertEquals(false, wait(failed))
        assertEquals(true, wait(passed))
    }

    @Test
    fun theCeilingStillEndsAStreamThatKeepsTrickling() = runBlocking {
        val done = CompletableDeferred<Boolean>()

        val ok = withTimeout(5_000) {
            awaitDownloadStream(
                done = done,
                ceilingMs = 120,
                stallMs = 60_000,
                lastActivityMs = { clock() },
                clockMs = clock,
                pollMs = 20,
            )
        }

        assertFalse(ok)
    }
}

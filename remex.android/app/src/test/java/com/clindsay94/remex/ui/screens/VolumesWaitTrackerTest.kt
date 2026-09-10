@file:OptIn(ExperimentalCoroutinesApi::class)

package com.clindsay94.remex.ui.screens

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.async
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.currentTime
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * [VolumesWaitTracker] state transitions (RemEx-c7v4n): entering the pending wait, and all three ways
 * out of it. Every test drives [kotlinx.coroutines.test.TestCoroutineScheduler] virtual time via
 * `runTest` — no real `delay`, no `Dispatchers.Unconfined` (that dispatcher would hide the exact
 * ordering these tests pin: [VolumesWaitTracker.pending] flips true before either racer can resolve
 * it). Each `@Test` also carries a real-time JUnit timeout so a regression that reintroduces a genuine
 * (non-virtual) hang fails loudly instead of wedging the run.
 */
class VolumesWaitTrackerTest {

    @Test(timeout = 5_000)
    fun `awaitResponse flips pending true before it resolves`() = runTest {
        val tracker = VolumesWaitTracker(timeoutMs = 60_000)
        val connected = MutableStateFlow(true)
        assertFalse(tracker.pending.value)

        val result = async { tracker.awaitResponse(connected) }
        runCurrent() // let awaitResponse start and suspend on the still-unresolved deferred

        assertTrue(tracker.pending.value)

        tracker.onAnswered()
        assertEquals(VolumesWaitTracker.VolumesWaitOutcome.ANSWERED, result.await())
    }

    @Test(timeout = 5_000)
    fun `onAnswered before the timeout ends the wait as ANSWERED`() = runTest {
        val tracker = VolumesWaitTracker(timeoutMs = 60_000)
        val connected = MutableStateFlow(true)

        val result = async { tracker.awaitResponse(connected) }
        runCurrent()
        tracker.onAnswered()

        assertEquals(VolumesWaitTracker.VolumesWaitOutcome.ANSWERED, result.await())
        assertFalse(tracker.pending.value)
    }

    @Test(timeout = 5_000)
    fun `connection dropping mid-wait ends it as DISCONNECTED, not the full timeout`() = runTest {
        val tracker = VolumesWaitTracker(timeoutMs = 60_000)
        val connected = MutableStateFlow(true)

        val result = async { tracker.awaitResponse(connected) }
        runCurrent()
        connected.value = false // transport drops while the PC prompt is presumably still open

        assertEquals(VolumesWaitTracker.VolumesWaitOutcome.DISCONNECTED, result.await())
        assertFalse(tracker.pending.value)
        // Confirms this genuinely short-circuited the 60s ceiling rather than coincidentally winning
        // a race decided later: no virtual time was advanced to reach this outcome.
        assertEquals(0L, currentTime)
    }

    @Test(timeout = 5_000)
    fun `already disconnected at the start ends the wait as DISCONNECTED immediately`() = runTest {
        val tracker = VolumesWaitTracker(timeoutMs = 60_000)
        val connected = MutableStateFlow(false)

        val outcome = tracker.awaitResponse(connected)

        assertEquals(VolumesWaitTracker.VolumesWaitOutcome.DISCONNECTED, outcome)
        assertFalse(tracker.pending.value)
        assertEquals(0L, currentTime)
    }

    @Test(timeout = 5_000)
    fun `no answer and no disconnect ends the wait as TIMED_OUT once timeoutMs elapses`() = runTest {
        val tracker = VolumesWaitTracker(timeoutMs = 5_000)
        val connected = MutableStateFlow(true)

        val result = async { tracker.awaitResponse(connected) }
        advanceUntilIdle()

        assertEquals(VolumesWaitTracker.VolumesWaitOutcome.TIMED_OUT, result.await())
        assertFalse(tracker.pending.value)
    }

    @Test(timeout = 5_000)
    fun `an answer that lands after the timeout has already fired is a no-op`() = runTest {
        val tracker = VolumesWaitTracker(timeoutMs = 5_000)
        val connected = MutableStateFlow(true)

        val result = async { tracker.awaitResponse(connected) }
        advanceTimeBy(5_001)
        runCurrent()
        assertEquals(VolumesWaitTracker.VolumesWaitOutcome.TIMED_OUT, result.await())

        // A late host reply must not throw or resurrect a wait that already ended.
        tracker.onAnswered()
        assertFalse(tracker.pending.value)
    }

    @Test(timeout = 5_000)
    fun `the wait is reachable and always terminates across repeated requests on one tracker`() = runTest {
        val tracker = VolumesWaitTracker(timeoutMs = 1_000)
        val connected = MutableStateFlow(true)

        // Bounded to 3 iterations, one per exit path — not a probe for a defect, just proof that
        // finishing one wait leaves the tracker able to start and finish the next.
        repeat(3) { i ->
            assertFalse("pending must be false before request #$i starts", tracker.pending.value)
            val result = async { tracker.awaitResponse(connected) }
            runCurrent()
            assertTrue("pending must be true once request #$i is outstanding", tracker.pending.value)
            val outcome = when (i) {
                0 -> {
                    tracker.onAnswered()
                    result.await()
                }
                1 -> {
                    connected.value = false
                    val o = result.await()
                    connected.value = true // restore for the next iteration
                    o
                }
                else -> {
                    advanceUntilIdle()
                    result.await()
                }
            }
            assertFalse("pending must be false once request #$i has ended", tracker.pending.value)
            assertEquals(
                when (i) {
                    0 -> VolumesWaitTracker.VolumesWaitOutcome.ANSWERED
                    1 -> VolumesWaitTracker.VolumesWaitOutcome.DISCONNECTED
                    else -> VolumesWaitTracker.VolumesWaitOutcome.TIMED_OUT
                },
                outcome,
            )
        }
    }
}

package com.clindsay94.remex

import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * [awaitFirstOrNullWithTimeout] is the fix for RemEx-alwfa.1 review HIGH-1: a corrupt/locked
 * DataStore must neither crash the splash's exit listener (an uncaught exception from `first()`)
 * nor hold it open forever (a flow that never emits). Real, short, real-time timeouts here —
 * same idiom as [AbandonableCallTest] — rather than `runTest` virtual time, since the property
 * under test IS "does this actually return in wall-clock time," which virtual time would hide.
 */
class SplashPersonalizationAwaitTest {

    @Test
    fun `returns the first emission when the flow is fast`() = runBlocking {
        val result = awaitFirstOrNullWithTimeout(flowOf("seed"), timeoutMs = 200)

        assertEquals("seed", result)
    }

    @Test
    fun `a flow that never emits releases within the timeout instead of hanging`() = runBlocking {
        // THE INJECTION TARGET: dropping the withTimeoutOrNull wrapper turns this into a hang —
        // this flow suspends forever on purpose, the same way a locked DataStore file would.
        val neverEmits = flow<String> { awaitCancellation() }

        val elapsedMs = measureMs {
            val result = awaitFirstOrNullWithTimeout(neverEmits, timeoutMs = 100)
            assertNull(result)
        }

        // Generous margin over the 100ms budget so this isn't flaky under CI scheduling jitter,
        // but nowhere near "hung forever" if the timeout wrapper were ever removed.
        assertTrue("expected release near the 100ms budget, took ${elapsedMs}ms", elapsedMs < 1000)
    }

    @Test
    fun `a flow that throws is caught, reported once, and resolves to null`() = runBlocking {
        val boom = IllegalStateException("DataStore corrupt")
        val throwingFlow = flow<String> { throw boom }
        var reported: Throwable? = null

        val result = awaitFirstOrNullWithTimeout(throwingFlow, timeoutMs = 200) { e -> reported = e }

        assertNull(result)
        assertEquals(boom, reported)
    }

    @Test
    fun `onFailure is never called on a plain timeout, only on a real exception`() = runBlocking {
        var reported: Throwable? = null

        val result = awaitFirstOrNullWithTimeout(
            flow<String> { awaitCancellation() },
            timeoutMs = 50
        ) { e -> reported = e }

        assertNull(result)
        assertNull(reported)
    }

    private suspend fun measureMs(body: suspend () -> Unit): Long {
        val start = System.nanoTime()
        body()
        return (System.nanoTime() - start) / 1_000_000
    }
}

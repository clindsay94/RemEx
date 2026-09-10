package com.clindsay94.remex

import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Real behavioural tests for the mechanism that makes a caller's timeout mean something (RemEx-defb).
 *
 * Behavioural rather than source-text, which is why [abandonable] was extracted from
 * `RemexCoreClient` at all: the pairing natives cannot be called in a unit test, but the property
 * that actually broke — "a `withTimeout` around a blocking call does not end it" — is about the
 * wrapper, not about JNI. A `Thread.sleep` stands in for the native frame; both are uninterruptible
 * by coroutine cancellation, which is the only quality the test depends on.
 */
class AbandonableCallTest {

    @Test
    fun `a value comes back unchanged when nobody cancels`() = runBlocking {
        assertEquals("done", abandonable("ok", cancel = {}) { "done" })
    }

    @Test
    fun `an exception from the block reaches the caller`() = runBlocking {
        val boom = assertThrows(IllegalStateException::class.java) {
            runBlocking { abandonable<String>("boom", cancel = {}) { error("native said no") } }
        }
        assertEquals("native said no", boom.message)
    }

    @Test
    fun `the timeout ends the wait instead of waiting for the work`() {
        // THE WHOLE BUG, IN ONE ASSERTION. Before this wrapper, a withTimeout around a blocking call
        // returned only once the call did — a 15-second budget over a 90-second handshake reported
        // its timeout after ninety seconds. Here a 30-second block must be given up on in well under
        // a second.
        val released = CountDownLatch(1)
        val elapsed = measure {
            assertThrows(TimeoutCancellationException::class.java) {
                runBlocking {
                    withTimeout(200) {
                        abandonable("slow", cancel = { released.countDown() }) {
                            // Uninterruptible on purpose: this is what a JNI frame behaves like.
                            released.await(30, TimeUnit.SECONDS)
                            "too late"
                        }
                    }
                }
            }
        }

        assertTrue(
            "the caller waited ${elapsed}ms for a 200ms timeout — the timeout is not ending the wait",
            elapsed < 5_000,
        )
    }

    @Test
    fun `the work is told to stop, not merely left running`() {
        // The half that is easy to forget. Without it the abandoned call keeps the pairing lock for
        // the rest of its budget and the user's next attempt queues behind the one they gave up on,
        // which reads as a broken retry.
        val cancelled = CountDownLatch(1)
        val finished = CountDownLatch(1)

        assertThrows(TimeoutCancellationException::class.java) {
            runBlocking {
                withTimeout(200) {
                    abandonable("told", cancel = { cancelled.countDown() }) {
                        cancelled.await(10, TimeUnit.SECONDS)
                        finished.countDown()
                        "unwound"
                    }
                }
            }
        }

        assertTrue("cancel was never invoked", cancelled.await(5, TimeUnit.SECONDS))
        assertTrue("the work never unwound after being cancelled", finished.await(5, TimeUnit.SECONDS))
    }

    @Test
    fun `cancel runs exactly once, and not at all on the happy path`() {
        val cancels = AtomicInteger()

        runBlocking { abandonable("quiet", cancel = { cancels.incrementAndGet() }) { "fine" } }
        assertEquals("cancel must not run when nobody cancelled", 0, cancels.get())

        val released = CountDownLatch(1)
        assertThrows(TimeoutCancellationException::class.java) {
            runBlocking {
                withTimeout(200) {
                    abandonable("noisy", cancel = { cancels.incrementAndGet(); released.countDown() }) {
                        released.await(10, TimeUnit.SECONDS)
                        "late"
                    }
                }
            }
        }
        assertEquals("cancel must run exactly once", 1, cancels.get())
    }

    @Test
    fun `a cancel that throws does not become the caller's failure`() {
        // cancel runs on whichever thread requested cancellation, and the caller has already given up
        // by then. Letting it throw would replace an honest timeout with an unrelated crash.
        val reported = AtomicBoolean(false)
        val released = CountDownLatch(1)

        assertThrows(TimeoutCancellationException::class.java) {
            runBlocking {
                withTimeout(200) {
                    abandonable(
                        name = "throws",
                        cancel = { released.countDown(); error("cancel exploded") },
                        onCancelFailure = { reported.set(true) },
                    ) {
                        released.await(10, TimeUnit.SECONDS)
                        "late"
                    }
                }
            }
        }

        assertTrue("a failing cancel must still be reported", reported.get())
    }

    @Test
    fun `work finishing after the caller gave up is dropped rather than delivered`() {
        // The result arrives with nobody waiting for it. Delivering it late would resume a
        // continuation the runtime has already completed, which is a crash rather than a stale value.
        val released = CountDownLatch(1)
        val completedCleanly = AtomicBoolean(false)

        assertThrows(TimeoutCancellationException::class.java) {
            runBlocking {
                withTimeout(200) {
                    abandonable("late", cancel = { released.countDown() }) {
                        released.await(10, TimeUnit.SECONDS)
                        completedCleanly.set(true)
                        "nobody wants this"
                    }
                }
            }
        }

        // Give the abandoned thread room to finish and, if it were going to, to blow up.
        Thread.sleep(300)
        assertTrue("the abandoned work should still run to completion", completedCleanly.get())
        assertFalse("no thread should still be waiting", released.count > 0)
    }

    private fun measure(body: () -> Unit): Long {
        val started = System.nanoTime()
        body()
        return (System.nanoTime() - started) / 1_000_000
    }
}

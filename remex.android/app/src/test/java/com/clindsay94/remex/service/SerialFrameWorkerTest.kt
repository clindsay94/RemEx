package com.clindsay94.remex.service

import java.util.Collections
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Perf audit P4-6: the v3 download receiver hands each frame to [SerialFrameWorker] instead of
 * writing and hashing it on the OkHttp reader thread. What must hold for that to be safe: frames are
 * handled strictly in arrival order, off the offering thread, and a reader thread can NEVER be left
 * parked in [SerialFrameWorker.offer] - not after shutdown, not after the handler dies. A parked
 * reader thread is a /ws/files socket that silently stops delivering anything.
 */
class SerialFrameWorkerTest {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    @After
    fun tearDown() {
        scope.cancel()
    }

    @Test
    fun handlesEveryItem_inOrder_offTheOfferingThread() = runBlocking {
        val seen = Collections.synchronizedList(mutableListOf<Int>())
        val handlerThread = AtomicReference<Thread>()
        val allDone = CountDownLatch(1)
        val worker = SerialFrameWorker<Int>(scope, capacity = 4) {
            handlerThread.set(Thread.currentThread())
            seen.add(it)
            if (it == 99) allDone.countDown()
        }
        for (i in 0..99) assertTrue(worker.offer(i))
        assertTrue(allDone.await(5, TimeUnit.SECONDS))
        assertEquals((0..99).toList(), seen.toList())
        assertNotEquals(Thread.currentThread(), handlerThread.get())
        worker.shutdown()
    }

    @Test
    fun offerAfterShutdown_returnsFalse_withoutBlocking() = runBlocking {
        val worker = SerialFrameWorker<Int>(scope, capacity = 1) {}
        worker.shutdown()
        assertFalse(worker.offer(1))
        assertFalse(worker.offer(2))
    }

    @Test
    fun shutdown_releasesAReaderParkedOnAFullQueue() = runBlocking {
        val handlerEntered = CountDownLatch(1)
        val releaseHandler = CountDownLatch(1)
        val worker = SerialFrameWorker<Int>(scope, capacity = 1) {
            handlerEntered.countDown()
            releaseHandler.await(5, TimeUnit.SECONDS)
        }
        assertTrue(worker.offer(1)) // taken by the worker, which now blocks in the handler
        assertTrue(handlerEntered.await(5, TimeUnit.SECONDS))
        assertTrue(worker.offer(2)) // fills the one-slot queue

        val parkedResult = AtomicReference<Boolean?>(null)
        val reader = Thread { parkedResult.set(worker.offer(3)) }
        reader.start()
        Thread.sleep(100)
        assertTrue("offer should be parked on the full queue", reader.isAlive)

        val stopping = scope.launch { worker.shutdown() }
        reader.join(5_000)
        assertFalse("shutdown must release the parked reader", reader.isAlive)
        assertEquals(false, parkedResult.get())

        releaseHandler.countDown()
        stopping.join()
    }

    @Test
    fun aHandlerThatThrows_closesTheQueue_soTheReaderIsNeverParkedForever() = runBlocking {
        val threw = CountDownLatch(1)
        val worker = SerialFrameWorker<Int>(scope, capacity = 1) {
            threw.countDown()
            throw IllegalStateException("disk full")
        }
        assertTrue(worker.offer(1))
        assertTrue(threw.await(5, TimeUnit.SECONDS))

        val result = AtomicReference<Boolean?>(null)
        val reader = Thread {
            // Enough offers to fill any queue that is still open: one must come back false.
            var ok = true
            repeat(4) { if (ok) ok = worker.offer(it) }
            result.set(ok)
        }
        reader.start()
        reader.join(5_000)
        assertFalse("reader must not park behind a dead worker", reader.isAlive)
        assertEquals(false, result.get())
        worker.shutdown()
    }
}

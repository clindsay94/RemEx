package com.clindsay94.remex.service

import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

/**
 * Live-check C1/C3/C4: the drain loop runs ONE transfer at a time, and a cancelled or paused
 * transfer used to keep that slot for up to six hours (its receive loop waited on frames the host
 * had stopped sending), so every later transfer - including a re-download of the same file - sat
 * on "Queued". These tests drive [TransferQueueController] with a fake runner.
 */
class TransferQueueControllerTest {

    @get:Rule val tmp = TemporaryFolder()

    private fun controller(initial: List<QueuedTransfer> = emptyList()) =
        TransferQueueController(TransferQueueStore(tmp.root), MutableStateFlow(initial))

    private fun item(id: String, state: TransferState = TransferState.Queued) =
        QueuedTransfer(id = id, mode = FileTransferModes.DOWNLOAD, fileName = "$id.bin", size = 10, localUri = "content://x/$id", state = state)

    private fun TransferQueueController.stateOf(id: String) = snapshot().first { it.id == id }.state

    private suspend fun TransferQueueController.awaitState(id: String, state: TransferState) =
        withTimeout(5_000) { flow.first { q -> q.any { it.id == id && it.state == state } } }

    @Test
    fun cancellingTheRunningTransfer_freesTheQueueForTheNextOne() = runBlocking {
        val c = controller()
        c.enqueue(listOf(item("a"), item("b")))
        val ranB = CompletableDeferred<Unit>()
        val drain = launch(Dispatchers.Default) {
            c.drain(onIdle = {}) { t ->
                if (t.id == "a") awaitCancellation()
                if (t.id == "b") ranB.complete(Unit)
            }
        }
        c.awaitState("a", TransferState.Negotiating)

        val stopped = c.cancel("a")

        withTimeout(5_000) { ranB.await() }
        assertEquals(TransferState.Cancelled, c.stateOf("a"))
        assertTrue(stopped!!.job!!.isCancelled)
        drain.cancel()
    }

    @Test
    fun pausingTheRunningTransfer_stopsIt_andResumeRunsItAgain() = runBlocking {
        val c = controller()
        c.enqueue(listOf(item("a"), item("b")))
        val runs = ConcurrentHashMap<String, Int>()
        val ranB = CompletableDeferred<Unit>()
        val secondRunOfA = CompletableDeferred<Unit>()
        val drain = launch(Dispatchers.Default) {
            c.drain(onIdle = {}) { t ->
                val n = runs.merge(t.id, 1, Int::plus)!!
                when {
                    t.id == "a" && n == 1 -> awaitCancellation()
                    t.id == "a" -> secondRunOfA.complete(Unit)
                    else -> ranB.complete(Unit)
                }
            }
        }
        c.awaitState("a", TransferState.Negotiating)

        c.pause("a")
        withTimeout(5_000) { ranB.await() }
        assertEquals(TransferState.Paused, c.stateOf("a"))

        c.resume("a")
        withTimeout(5_000) { secondRunOfA.await() }
        drain.cancel()
    }

    @Test
    fun aRunWrite_neverResurrectsAUserStoppedTransfer() {
        val c = controller(listOf(item("a", TransferState.Active), item("b", TransferState.Active), item("q")))
        c.cancel("a")
        c.pause("b")

        c.updateFromRun("a") { it.copy(state = TransferState.Failed, error = "Transfer incomplete.") }
        c.updateFromRun("b") { it.copy(state = TransferState.Active) }
        c.updateFromRun("q") { it.copy(state = TransferState.Failed) }

        assertEquals(TransferState.Cancelled, c.stateOf("a"))
        assertEquals(TransferState.Paused, c.stateOf("b"))
        // Queued means "the user asked for a fresh run"; a stale run must not overwrite it either.
        assertEquals(TransferState.Queued, c.stateOf("q"))
    }

    @Test
    fun aRunThatFinishedDespiteAPause_isStillRecordedDone() {
        val c = controller(listOf(item("a", TransferState.Active)))
        c.pause("a")
        c.updateFromRun("a") { it.copy(state = TransferState.Done) }
        assertEquals(TransferState.Done, c.stateOf("a"))
    }

    @Test
    fun anItemCancelledBeforeItsTurn_isNeverRun() = runBlocking {
        val c = controller()
        c.enqueue(listOf(item("a"), item("b")))
        c.cancel("a")
        val ran = mutableListOf<String>()
        val ranB = CompletableDeferred<Unit>()
        val drain = launch(Dispatchers.Default) {
            c.drain(onIdle = {}) { t ->
                synchronized(ran) { ran += t.id }
                if (t.id == "b") ranB.complete(Unit)
            }
        }
        withTimeout(5_000) { ranB.await() }
        assertEquals(listOf("b"), synchronized(ran) { ran.toList() })
        drain.cancel()
    }

    @Test
    fun cancelAll_stopsEveryUnfinishedTransfer_andLeavesFinishedOnesAlone() = runBlocking {
        val c = controller(
            listOf(
                item("done", TransferState.Done),
                item("failed", TransferState.Failed),
                item("paused", TransferState.Paused),
            )
        )
        c.enqueue(listOf(item("run"), item("q1"), item("q2")))
        val drain = launch(Dispatchers.Default) { c.drain(onIdle = {}) { awaitCancellation() } }
        c.awaitState("run", TransferState.Negotiating)

        val stopped = c.cancelAll()

        assertEquals(setOf("paused", "run", "q1", "q2"), stopped.map { it.transfer.id }.toSet())
        assertTrue(stopped.first { it.transfer.id == "run" }.job!!.isCancelled)
        assertEquals(TransferState.Done, c.stateOf("done"))
        assertEquals(TransferState.Failed, c.stateOf("failed"))
        for (id in listOf("paused", "run", "q1", "q2")) assertEquals(TransferState.Cancelled, c.stateOf(id))
        drain.cancel()
    }

    @Test
    fun cancelAll_stillCancelsOnTheHost_aPausedThenResumedTransfer() = runBlocking {
        // A resumed row is Queued again here while the PC holds it as Paused (with an upload partial).
        // Keying the host cancel on "not Queued" skipped it and left that PC entry forever.
        val c = controller()
        c.enqueue(listOf(item("a")))
        val drain = launch(Dispatchers.Default) { c.drain(onIdle = {}) { awaitCancellation() } }
        c.awaitState("a", TransferState.Negotiating)
        c.pause("a")
        drain.cancel()
        drain.join()
        c.resume("a")
        c.enqueue(listOf(item("never")))
        assertEquals(TransferState.Queued, c.stateOf("a"))

        val stopped = c.cancelAll().associateBy { it.transfer.id }

        assertTrue(stopped.getValue("a").hostKnows)
        assertFalse(stopped.getValue("never").hostKnows)
    }

    @Test
    fun hostKnows_survivesAReload_andDefaultsFromStateForOldRows() {
        val c = controller(listOf(item("a", TransferState.Paused).copy(hostKnows = true)))
        c.resume("a")
        val reloaded = TransferQueueStore(tmp.root).load().single()
        assertEquals(TransferState.Queued, reloaded.state)
        assertTrue(reloaded.hostKnows)

        // A row written before the flag existed: past Queued means it was offered.
        val legacyPaused = item("p", TransferState.Paused).toJson().apply { remove("hostKnows") }
        val legacyQueued = item("q").toJson().apply { remove("hostKnows") }
        assertTrue(QueuedTransfer.fromJson(legacyPaused).hostKnows)
        assertFalse(QueuedTransfer.fromJson(legacyQueued).hostKnows)
    }

    @Test
    fun cancel_ofAFinishedTransfer_isANoOp() {
        val c = controller(listOf(item("d", TransferState.Done)))
        assertNull(c.cancel("d"))
        assertEquals(TransferState.Done, c.stateOf("d"))
    }

    @Test
    fun aRunnerThatThrows_marksItFailed_andTheQueueKeepsDraining() = runBlocking {
        val c = controller()
        c.enqueue(listOf(item("a"), item("b")))
        val ranB = CompletableDeferred<Unit>()
        val drain = launch(Dispatchers.Default) {
            c.drain(onIdle = {}) { t ->
                if (t.id == "a") throw IllegalStateException("boom")
                ranB.complete(Unit)
            }
        }
        withTimeout(5_000) { ranB.await() }
        assertEquals(TransferState.Failed, c.stateOf("a"))
        drain.cancel()
    }

    @Test
    fun enqueue_persistsTheWholeBatch() {
        val c = controller()
        c.enqueue((1..50).map { item("i$it") })
        assertEquals(50, TransferQueueStore(tmp.root).load().size)
        assertEquals(50, c.snapshot().size)
    }

    @Test
    fun recoverInterrupted_parksInFlightItemsAsPaused() {
        val c = controller(
            listOf(
                item("n", TransferState.Negotiating),
                item("a", TransferState.Active),
                item("v", TransferState.Verifying),
                item("q"),
            )
        )
        c.recoverInterrupted()
        assertEquals(TransferState.Paused, c.stateOf("n"))
        assertEquals(TransferState.Paused, c.stateOf("a"))
        assertEquals(TransferState.Paused, c.stateOf("v"))
        assertEquals(TransferState.Queued, c.stateOf("q"))
    }

    @Test
    fun clearFinished_returnsTheRemovedItems() {
        val c = controller(listOf(item("d", TransferState.Done), item("c", TransferState.Cancelled), item("q")))
        val removed = c.clearFinished()
        assertEquals(setOf("d", "c"), removed.map { it.id }.toSet())
        assertEquals(listOf("q"), c.snapshot().map { it.id })
    }
}

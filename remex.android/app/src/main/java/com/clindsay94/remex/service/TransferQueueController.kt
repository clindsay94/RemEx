package com.clindsay94.remex.service

import android.util.Log
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.supervisorScope

/**
 * The transfer queue's state machine and its one-at-a-time drain loop, split out of
 * [FileTransferEngine] so it can be tested without a socket (live-check C1/C3/C4).
 *
 * **A STOPPED TRANSFER MUST GIVE BACK THE DRAIN SLOT.** The drain runs one transfer at a time. Cancel
 * and pause used to only flip the row's state and send the control message; the transfer's own
 * coroutine kept waiting for data frames the host had just stopped sending, for up to the six-hour
 * transfer ceiling, and every later transfer - a re-download of the same file, the rest of a folder -
 * sat on "Queued" behind it. So each run is its own [Job], registered here under [lock] in the same
 * step that claims the row, and [cancel]/[pause]/[cancelAll] cancel it.
 *
 * **A RUN NEVER OVERWRITES THE USER.** [updateFromRun] ignores writes onto a Cancelled, Paused or
 * Queued row (one exception: Done onto Paused, because the file really did land), so a run that is
 * still unwinding cannot flip a cancelled transfer to Failed or a re-queued one to Active.
 *
 * Thread-safety: every "read [flow], mutate, assign" happens under [lock] (perf audit P1-32 review:
 * the store builds the persisted file from the in-memory list, so an unlocked read-modify-write can
 * silently drop a concurrent mutation from both).
 */
internal class TransferQueueController(
    private val store: TransferQueueStore,
    internal val flow: MutableStateFlow<List<QueuedTransfer>>,
    private val lock: Any = Any(),
) {
    /** The run currently holding the drain slot, by transfer id. Guarded by [lock]. */
    private val running = HashMap<String, Job>()

    /** A transfer the user stopped: its row as it was before, and its run if one was live. */
    class Stopped(val transfer: QueuedTransfer, val job: Job?) {
        /**
         * Whether the host has seen this transfer and so needs a cancel message. Not the same as
         * "not Queued": a paused-then-resumed row is Queued here while the host holds it as Paused
         * (with an upload partial), and skipping its cancel left that entry on the PC forever.
         */
        val hostKnows: Boolean get() = transfer.hostKnows || transfer.state != TransferState.Queued
    }

    fun snapshot(): List<QueuedTransfer> = flow.value

    fun enqueue(items: List<QueuedTransfer>) {
        if (items.isEmpty()) return
        // One write for the whole batch: a folder download used to rewrite the queue file once per
        // file, O(n^2) bytes for a large folder (live-check C3).
        synchronized(lock) { flow.value = store.upsertAll(flow.value, items) }
    }

    /**
     * A state write from the transfer's own run. Returns the row as it now stands, or null when the
     * row no longer exists.
     */
    fun updateFromRun(id: String, transform: (QueuedTransfer) -> QueuedTransfer): QueuedTransfer? =
        update(id) { current ->
            when (current.state) {
                TransferState.Cancelled, TransferState.Queued -> current
                TransferState.Paused -> transform(current).takeIf { it.state == TransferState.Done } ?: current
                else -> transform(current)
            }
        }

    /** In-memory-only progress (no disk write); per data frame. */
    fun progress(id: String, bytes: Long) {
        synchronized(lock) {
            flow.value = flow.value.map { if (it.id == id) it.copy(bytesTransferred = bytes) else it }
        }
    }

    fun cancel(id: String): Stopped? {
        val stopped =
            synchronized(lock) {
                val current = flow.value.firstOrNull { it.id == id } ?: return null
                if (current.state in Finished) return null
                flow.value = store.upsert(flow.value, current.copy(state = TransferState.Cancelled))
                Stopped(current, running[id])
            }
        // Outside the lock: cancelling runs completion handlers, which must not run under it.
        stopped.job?.cancel()
        return stopped
    }

    /** Cancels every unfinished transfer (Queued, Negotiating, Active, Verifying, Paused). */
    fun cancelAll(): List<Stopped> {
        val stopped =
            synchronized(lock) {
                val targets = flow.value.filter { it.state !in Finished }
                if (targets.isEmpty()) return emptyList()
                val ids = targets.mapTo(HashSet()) { it.id }
                val updated = flow.value.map { if (it.id in ids) it.copy(state = TransferState.Cancelled) else it }
                store.save(updated)
                flow.value = updated
                targets.map { Stopped(it, running[it.id]) }
            }
        stopped.forEach { it.job?.cancel() }
        return stopped
    }

    /**
     * Pauses a Queued, Negotiating or Active transfer. A live run is cancelled - the host stops
     * sending on pause, so the run could never finish. What happens to its local data is the
     * caller's business: an upload's staged source is kept for [resume]; a download's partial is
     * useless, because the host always restarts a download at offset 0 (see [FileTransferEngine.pause]).
     */
    fun pause(id: String): Stopped? {
        val stopped =
            synchronized(lock) {
                val current = flow.value.firstOrNull { it.id == id } ?: return null
                if (current.state !in Pausable) return null
                flow.value = store.upsert(flow.value, current.copy(state = TransferState.Paused))
                Stopped(current, running[id])
            }
        stopped.job?.cancel()
        return stopped
    }

    fun resume(id: String) {
        update(id) {
            if (it.state == TransferState.Paused || it.state == TransferState.Failed) {
                it.copy(state = TransferState.Queued, error = null)
            } else it
        }
    }

    /** Removes Done/Cancelled/Failed rows and returns them, so their local files can be deleted. */
    fun clearFinished(): List<QueuedTransfer> =
        synchronized(lock) {
            val removed = flow.value.filter { it.state in Finished }
            flow.value = store.pruneFinished(flow.value)
            removed
        }

    /**
     * Parks rows a previous process left mid-flight as Paused. The drain only picks Queued rows, so
     * without this they stayed "Starting…"/Active forever, and kept their partial files alive.
     */
    fun recoverInterrupted() {
        synchronized(lock) {
            if (flow.value.none { it.state in InFlight }) return
            val updated = flow.value.map { if (it.state in InFlight) it.copy(state = TransferState.Paused) else it }
            store.save(updated)
            flow.value = updated
        }
    }

    /**
     * FIFO, one transfer at a time. Never returns; cancel the calling coroutine to stop it. [run]
     * receives the row already moved to Negotiating.
     */
    suspend fun drain(onIdle: () -> Unit, run: suspend (QueuedTransfer) -> Unit) {
        supervisorScope {
            while (true) {
                // Paused is a held state (WP7): only Queued items are auto-picked. resume() re-queues.
                val next = flow.value.firstOrNull { it.state == TransferState.Queued }
                if (next == null) {
                    onIdle()
                    // Perf audit P0-8: suspend on the queue itself instead of polling.
                    flow.first { q -> q.any { it.state == TransferState.Queued } }
                    continue
                }

                var claimed: QueuedTransfer? = null
                val job =
                    launch(start = CoroutineStart.LAZY) {
                        val t = claimed ?: return@launch
                        try {
                            run(t)
                        } catch (e: CancellationException) {
                            throw e
                        } catch (e: Exception) {
                            Log.w(TAG, "Transfer ${t.id} failed", e)
                            updateFromRun(t.id) { it.copy(state = TransferState.Failed, error = e.message) }
                        }
                    }
                // Claim and register in ONE locked step, so a cancel lands either before the claim
                // (the claim then fails) or after the job is findable (the cancel then reaches it).
                synchronized(lock) {
                    val current = flow.value.firstOrNull { it.id == next.id }
                    if (current != null && current.state == TransferState.Queued) {
                        // hostKnows: the run offers it to the host next; see Stopped.hostKnows.
                        val negotiating = current.copy(state = TransferState.Negotiating, error = null, hostKnows = true)
                        flow.value = store.upsert(flow.value, negotiating)
                        running[current.id] = job
                        claimed = negotiating
                    }
                }
                if (claimed == null) {
                    job.cancel()
                    continue
                }
                try {
                    job.start()
                    job.join()
                } finally {
                    synchronized(lock) { if (running[next.id] === job) running.remove(next.id) }
                }
            }
        }
    }

    private fun update(id: String, transform: (QueuedTransfer) -> QueuedTransfer): QueuedTransfer? =
        synchronized(lock) {
            val current = flow.value.firstOrNull { it.id == id } ?: return null
            val next = transform(current)
            if (next != current) flow.value = store.upsert(flow.value, next)
            next
        }

    private companion object {
        const val TAG = "TransferQueue"

        val Finished = setOf(TransferState.Done, TransferState.Cancelled, TransferState.Failed)
        val Pausable = setOf(TransferState.Queued, TransferState.Negotiating, TransferState.Active)
        val InFlight = setOf(TransferState.Negotiating, TransferState.Active, TransferState.Verifying)
    }
}

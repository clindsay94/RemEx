package com.clindsay94.remex.service

import android.util.Log
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.channels.trySendBlocking
import kotlinx.coroutines.launch

/**
 * Moves per-frame work off a socket reader thread onto ONE worker coroutine, in arrival order
 * (perf audit P4-6).
 *
 * The v3 download receiver used to do its disk write, SHA-256 update and ack on OkHttp's reader
 * thread, so the socket was not read at all while a 256 KiB frame was being written and hashed. Here
 * the reader only enqueues; the worker does the work. The work itself is unchanged and still strictly
 * serial, so an ack is still sent only after the bytes it covers were written (REGRESSION-GUARDS
 * "Never announce file_transfer_complete before the peer has acked the data").
 *
 * **THE READER THREAD MUST NEVER BE LEFT PARKED.** [offer] blocks while the queue is full - that is
 * the back-pressure onto the socket, and the sender's unacked window already keeps the queue from
 * filling in practice - but it must come back once nothing will ever drain it again. So the queue is
 * cancelled on EVERY worker exit (a throw from [handle], a cancel, [shutdown]), and a cancelled queue
 * fails a blocked or future [offer] instead of holding it. A parked OkHttp reader is a `/ws/files`
 * socket that silently stops delivering every transfer on it.
 */
internal class SerialFrameWorker<T>(
    scope: CoroutineScope,
    capacity: Int,
    private val handle: (T) -> Unit,
) {
    private val queue = Channel<T>(capacity)

    private val job: Job =
        scope.launch {
            try {
                for (item in queue) handle(item)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                // Not rethrown: under a SupervisorJob scope an uncaught throw here would take down
                // the process, where the same throw on the reader thread used to fail only the
                // socket. The caller learns of it through offer() returning false.
                Log.e(TAG, "Frame worker stopped: ${e.message}", e)
            } finally {
                queue.cancel()
            }
        }

    /**
     * Enqueues [item] from the reader thread, blocking while the queue is full. Returns false once
     * the worker has stopped for any reason; the item was then NOT handled.
     */
    fun offer(item: T): Boolean = queue.trySendBlocking(item).isSuccess

    /** Stops the worker, dropping anything still queued, and waits for an in-flight item to finish. */
    suspend fun shutdown() {
        queue.cancel()
        job.cancelAndJoin()
    }

    private companion object {
        const val TAG = "SerialFrameWorker"
    }
}

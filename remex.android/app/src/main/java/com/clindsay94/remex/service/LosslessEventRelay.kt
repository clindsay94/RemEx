package com.clindsay94.remex.service

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.launch

/**
 * A multicast event stream that never drops an event, fed from a thread that cannot suspend
 * (RemEx-1iszs).
 *
 * The JNI callback thread can only `tryEmit`, and a `SharedFlow` that a non-suspending emitter can
 * always reach has to drop something once its buffer fills. For file-control messages that was the
 * wrong trade: a legacy v2 `file_transfer_chunk` carries file bytes, and the slowest subscriber
 * (`AndroidFileTransferHost`, which writes each chunk to disk inside its collector) let a fast PC
 * outrun 64 buffered messages and silently lose one, so the saved file came out short.
 *
 * Here the native thread drops each event into an unbounded, ordered inbox ([offer] never fails while
 * the relay is open), and a single pump coroutine re-emits them into a `SharedFlow` that SUSPENDS when
 * a subscriber falls behind. Every subscriber still sees every event, in arrival order; a slow one
 * slows the pump instead of costing anyone an event. The inbox only grows while a subscriber is
 * stalled, which for file transfers the sender's own ack window already bounds.
 */
internal class LosslessEventRelay<T>(scope: CoroutineScope, bufferCapacity: Int = DEFAULT_BUFFER) {
    private val inbox = Channel<T>(Channel.UNLIMITED)

    // BufferOverflow.SUSPEND is the default and is the whole point: emit waits for room.
    private val _events = MutableSharedFlow<T>(extraBufferCapacity = bufferCapacity)

    /** Every offered event, in order, to every subscriber present when it is pumped. */
    val events: SharedFlow<T> = _events.asSharedFlow()

    init {
        scope.launch { for (event in inbox) _events.emit(event) }
    }

    /** Queues [event] without blocking or suspending. Safe from any thread, including JNI callbacks. */
    fun offer(event: T): Boolean = inbox.trySend(event).isSuccess

    companion object {
        /** Room for a burst before the pump waits on the slowest subscriber. */
        const val DEFAULT_BUFFER = 64
    }
}

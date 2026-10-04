package com.clindsay94.remex.service

/**
 * Holds the data frames of a download that arrive before the receiver is ready for them, and
 * replays them, in order, once it is (RemEx-pp4cm.9).
 *
 * **THE SENDER DOES NOT WAIT FOR THE RECEIVER.** For a download this phone starts, the PC answers
 * `file_transfer_ready` on the control socket and begins streaming on `/ws/files` in the same
 * breath. The two sockets are independent, and the phone's sink used to be registered only AFTER it
 * had processed the ready (a queue-file write, the partial file, the writer) - so the first frames
 * could beat it. [FileTransferChannelClient.dispatch] drops a frame with no sink ("No sink for
 * transfer"), the dropped frame is never acknowledged, the PC gives up after its ack-idle window
 * ("The peer stopped acknowledging data", seen in the PC's own transfer record) and the phone waited
 * for a frame that was never coming while holding the one-at-a-time drain slot - so the rest of a
 * folder sat behind it.
 *
 * This is registered BEFORE the offer is sent, the same reason the complete-waiter is registered
 * before it. Until [arm] it buffers; after, it is a pass-through.
 *
 * Thread-safety: [onFrame] is called by the one socket reader thread and [arm] by the transfer's own
 * coroutine. Both hold [lock] while the buffer is being drained, so a frame that arrives mid-replay
 * waits and lands AFTER the buffered ones - order is what the receiver's offset check depends on.
 */
internal class EarlyFrameSink(private val maxBufferedFrames: Int) : FileFrameSink {
    private val lock = Any()
    private var buffered: MutableList<Pair<FileFrameEnvelope, ByteArray>>? = ArrayList()
    private var target: FileFrameSink? = null
    private var channelClosed = false
    private var overflowed = false

    override fun onFrame(envelope: FileFrameEnvelope, payload: ByteArray) {
        val live =
            synchronized(lock) {
                val armed = target
                if (armed == null) {
                    val held = buffered
                    // A sender within its ack window never gets here (see the caller's limit). Past
                    // it the frame is dropped and the transfer is failed at [arm], never half-kept.
                    if (held != null && held.size < maxBufferedFrames) held.add(envelope to payload)
                    else overflowed = true
                }
                armed
            }
        live?.onFrame(envelope, payload)
    }

    override fun onChannelClosed() {
        val live =
            synchronized(lock) {
                val armed = target
                if (armed == null) channelClosed = true
                armed
            }
        live?.onChannelClosed()
    }

    /**
     * Hands everything held so far to [sink], in arrival order, and routes every later frame straight
     * to it. A channel that dropped, or frames lost to the buffer limit, before this point is
     * reported to [sink] as a closed channel right after the replay, so the transfer fails instead of
     * waiting for bytes that are gone.
     */
    fun arm(sink: FileFrameSink) {
        synchronized(lock) {
            val held = buffered ?: return
            buffered = null
            for ((envelope, payload) in held) sink.onFrame(envelope, payload)
            target = sink
            if (channelClosed || overflowed) sink.onChannelClosed()
        }
    }

    /** Frames currently held; for tests. */
    val heldFrames: Int get() = synchronized(lock) { buffered?.size ?: 0 }
}

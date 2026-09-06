package com.clindsay94.remex.service

import java.io.ByteArrayInputStream
import java.security.MessageDigest
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

/**
 * Exercises [UploadSendLoop], the collaborator [FileTransferEngine.runUpload]'s ack-driven
 * backpressure was extracted into so it is testable without mutable global state (RemEx-yi7id, the
 * other half of RemEx-68wwl — see that bead's note on why `FileTransferEngine`, an `object`
 * singleton, could not be seamed the simpler way [FileHostHandler] was).
 *
 * `Dispatchers.Unconfined` throughout, same as [FileHostHandlerTest]'s equivalent backpressure
 * tests: it runs the loop inline on the test thread up to its first suspension point, so delivering
 * an ack (or closing the channel) from the test body afterward resumes it synchronously — no real
 * concurrency, no `Thread.sleep`, no flakiness from scheduling.
 */
class UploadSendLoopTest {

    /** Records every frame handed to `sendData`, so a test can assert on order and count. */
    private class RecordingSender(private val accept: Boolean = true) {
        val frames = mutableListOf<Triple<Long, Int, Boolean>>()

        val fn: suspend (String, Long, ByteArray, Int, Boolean) -> Boolean =
            { _, offset, _, count, isFinal ->
                if (accept) frames.add(Triple(offset, count, isFinal))
                accept
            }
    }

    // ── Backpressure: stop reading when too much is unacked (RemEx-yi7id) ─────
    //
    // NOT the completion drain — docs/REGRESSION-GUARDS.md exists partly because the two were once
    // confused: this one bounds outstanding unacked bytes mid-transfer; the drain (which stays in
    // FileTransferEngine.runUpload, untouched by this extraction) waits for the last ack before
    // announcing completion.

    @Test
    fun stopsReadingOnceTooMuchIsUnacked_thenResumesOnAck() = runBlocking {
        // Cap set below one chunk, so the first frame alone exceeds it — the second frame must not
        // go out until the peer acks. A negative assertion, for the same reason the drain's is:
        // "both frames arrived eventually" passes just as happily when backpressure never engaged.
        val twoChunks = ByteArray(FileTransferLimits.DATA_PAYLOAD_BYTES + 1024) { 'x'.code.toByte() }
        val sender = RecordingSender()
        val loop = UploadSendLoop(sender.fn, maxUnackedBytes = 100)
        val committed = AtomicLong(0)
        val ackSignal = Channel<Unit>(Channel.CONFLATED)
        val digest = MessageDigest.getInstance("SHA-256")

        val result =
            async(Dispatchers.Unconfined) {
                loop.run(
                    transferId = "up-bp",
                    input = ByteArrayInputStream(twoChunks),
                    size = twoChunks.size.toLong(),
                    initialSent = 0L,
                    committed = committed,
                    ackSignal = ackSignal,
                    digest = digest,
                    onProgress = {},
                )
            }

        assertEquals(
            "the sender must stop after the frame that breached the cap, not stream the whole file",
            1,
            sender.frames.size,
        )

        // The peer catches up; the rest follows.
        committed.set(FileTransferLimits.DATA_PAYLOAD_BYTES.toLong())
        ackSignal.trySend(Unit)

        assertEquals("the second frame goes out once there is room", 2, sender.frames.size)
        assertEquals(
            "the loop reports every byte sent once the stream is exhausted",
            twoChunks.size.toLong(),
            result.await(),
        )
    }

    @Test
    fun underTheCap_neverBlocksOnBackpressure() = runBlocking {
        // The control. Without it the test above would pass just as well against a loop that
        // blocked on every frame regardless of the cap — a different bug with the same symptom.
        val data = "hello".toByteArray()
        val sender = RecordingSender()
        val loop = UploadSendLoop(sender.fn) // default cap: FileTransferLimits.MAX_UNACKED_BYTES
        val committed = AtomicLong(0)
        val ackSignal = Channel<Unit>(Channel.CONFLATED)
        val digest = MessageDigest.getInstance("SHA-256")

        val sent =
            loop.run(
                transferId = "up-small",
                input = ByteArrayInputStream(data),
                size = data.size.toLong(),
                initialSent = 0L,
                committed = committed,
                ackSignal = ackSignal,
                digest = digest,
                onProgress = {},
            )

        assertEquals("five bytes is one frame, well under the 8 MB cap", 1, sender.frames.size)
        assertEquals(data.size.toLong(), sent)
    }

    @Test
    fun closedAckSignalWhileParked_throwsChannelClosed() {
        // Existing behaviour, preserved verbatim by the extraction: a channel closed while the loop
        // is parked on backpressure (peer/socket dropped) aborts the transfer loudly rather than
        // hanging forever.
        val twoChunks = ByteArray(FileTransferLimits.DATA_PAYLOAD_BYTES + 1024) { 'x'.code.toByte() }
        val sender = RecordingSender()
        val loop = UploadSendLoop(sender.fn, maxUnackedBytes = 100)
        val committed = AtomicLong(0)
        // Closed up front rather than mid-run: receiveCatching on an already-closed channel returns
        // closed immediately without suspending, exercising the same throw the real "peer dropped
        // while parked" case hits, deterministically and without needing a second coroutine.
        val ackSignal = Channel<Unit>(Channel.CONFLATED).apply { close() }
        val digest = MessageDigest.getInstance("SHA-256")

        val error =
            assertThrows(IllegalStateException::class.java) {
                runBlocking {
                    loop.run(
                        transferId = "up-closed",
                        input = ByteArrayInputStream(twoChunks),
                        size = twoChunks.size.toLong(),
                        initialSent = 0L,
                        committed = committed,
                        ackSignal = ackSignal,
                        digest = digest,
                        onProgress = {},
                    )
                }
            }

        assertEquals("Channel closed.", error.message)
    }
}

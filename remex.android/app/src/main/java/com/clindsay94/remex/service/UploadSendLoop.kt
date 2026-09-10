package com.clindsay94.remex.service

import java.io.InputStream
import java.security.MessageDigest
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.channels.Channel

/**
 * [FileTransferEngine.runUpload]'s ack-driven backpressure loop, extracted so it is testable
 * without mutable global state (RemEx-yi7id, the other half of RemEx-68wwl).
 *
 * `FileTransferEngine` is an `object` singleton that calls the `FileTransferChannelClient` singleton
 * directly, so — unlike [FileHostHandler], which already takes its channel and its
 * `maxUnackedBytes` cap as constructor parameters — it has no constructor to seam a defaulted
 * parameter onto. Mutable global state would have been worse than no coverage at all: test-order
 * dependent, and the exact failure mode this class exists to avoid. Extracting the loop into its own
 * collaborator gets the same seam [FileHostHandler] has, without either problem.
 *
 * Contains `runUpload`'s send loop VERBATIM: same frames, same order, same exceptions. The resume
 * re-hash and the completion drain stay in [FileTransferEngine.runUpload] — the drain in particular
 * is a regression guard (RemEx-y6x6, see `docs/REGRESSION-GUARDS.md`) that bounds a *different* wait
 * (for the last ack, before announcing completion) and must not be folded into this one (for room
 * under the cap, before the next frame is read).
 */
internal class UploadSendLoop(
    /** `FileTransferChannelClient::sendData` in production; a fake in tests. */
    private val sendData: suspend (transferId: String, offset: Long, buffer: ByteArray, count: Int, isFinal: Boolean) -> Boolean,
    /**
     * How many bytes may be outstanding unacked before the loop stops reading. Defaulted so
     * production is unchanged; tests lower it so the branch is reachable without streaming 8 MB
     * through a fake.
     *
     * **MUST EXCEED THE PEER'S ACK INTERVAL** against a real peer (see [FileHostHandler.maxUnackedBytes]'s
     * KDoc for why) — a smaller cap is valid only against a fake that acks by hand, which is what the
     * tests here do.
     */
    private val maxUnackedBytes: Long = FileTransferLimits.MAX_UNACKED_BYTES.toLong(),
) {
    /**
     * Streams [input] to EOF, sending [FileTransferLimits.DATA_PAYLOAD_BYTES]-sized frames via
     * [sendData], never letting unacked bytes exceed [maxUnackedBytes]. [committed] and [ackSignal]
     * are fed by the caller's [FileFrameSink] on inbound ACK/ERROR frames. [digest] is updated with
     * every byte read; [onProgress] is called with the running total after every frame. [sent] starts
     * at [initialSent] (bytes already accounted for by a resume re-hash) and the return value is the
     * final total, which is not the same as [size] on a "reader returns before the whole file" bug —
     * the caller decides what to do with that.
     */
    suspend fun run(
        transferId: String,
        input: InputStream,
        size: Long,
        initialSent: Long,
        committed: AtomicLong,
        ackSignal: Channel<Unit>,
        digest: MessageDigest,
        onProgress: (Long) -> Unit,
    ): Long {
        var sent = initialSent
        val buf = ByteArray(FileTransferLimits.DATA_PAYLOAD_BYTES)
        while (true) {
            val read = input.read(buf)
            if (read <= 0) break
            while (sent - committed.get() > maxUnackedBytes) {
                // receiveCatching is stable API (unlike isClosedForReceive); a closed channel means
                // the peer/socket dropped, so abort and let the caller mark this failed.
                if (ackSignal.receiveCatching().isClosed) {
                    throw IllegalStateException("Channel closed.")
                }
            }
            val isFinal = sent + read >= size
            if (!sendData(transferId, sent, buf, read, isFinal)) {
                throw IllegalStateException("Binary channel closed mid-transfer.")
            }
            digest.update(buf, 0, read)
            sent += read
            onProgress(sent)
        }
        return sent
    }
}

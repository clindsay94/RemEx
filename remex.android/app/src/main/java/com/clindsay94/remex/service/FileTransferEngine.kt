package com.clindsay94.remex.service

import android.content.Context
import android.net.Uri
import android.os.SystemClock
import android.provider.DocumentsContract
import android.provider.OpenableColumns
import android.util.Base64
import android.util.Log
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.security.PinnedHostStore
import java.io.File
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import org.json.JSONObject

/**
 * Android-initiated transfer engine (plan WP5). Drives the v3 negotiation on the native `/ws` control
 * plane (`file_transfer_offer/ready/complete/result/control` via [RemexCoreClient]) and the bulk data
 * on the shared binary `/ws/files` channel ([FileTransferChannelClient]). Supports offset-based resume
 * of UPLOADS (re-hash on resume) and ack-driven backpressure, exactly mirroring the PC host's
 * `TransferSessionManager`. Downloads do not resume: the offer carries only a `resumeRequested` flag,
 * not the receiver's offset, so the host (the sender) always answers `startOffset = 0`. Download
 * resume needs a protocol change; until then a paused download restarts from zero.
 *
 * Work is persisted to `filesDir/transfer_queue.json` via [TransferQueueStore] so it survives process
 * death; the [FileTransferJobService] (a User-Initiated Data Transfer job) keeps the process alive
 * while the queue drains, so a
 * transfer is not bound to `viewModelScope`. The UI (WP7) observes [queue] and calls the enqueue*
 * methods; WP8's share sheet enqueues push uploads the same way.
 */
object FileTransferEngine {
    private const val TAG = "FileTransferEngine"

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var started = false
    private lateinit var appContext: Context
    private lateinit var settings: SettingsManager
    private lateinit var queueStore: TransferQueueStore

    private val _queue = MutableStateFlow<List<QueuedTransfer>>(emptyList())
    val queue = _queue.asStateFlow()

    // Perf audit P1-32 review (round 1 HIGH): upsert/remove/pruneFinished/pruneStale now build the
    // persisted file from the caller's in-memory _queue.value instead of re-reading disk, so a
    // read-modify-write that isn't atomic can silently drop a concurrent mutation from BOTH the
    // StateFlow and the file, with no self-healing re-read to recover it (unlike before this fix).
    // enqueueUpload/enqueueDownload/cancel/clearFinished run on caller (ViewModel/UI) threads;
    // updateState and updateProgress run on the engine's own Dispatchers.IO scope for every data
    // frame - all of them must serialize on the same lock around "read _queue.value, mutate, assign".
    // Every mutation now goes through [controller], which is handed this lock.
    private val queueLock = Any()

    // Owns the queue state machine and the drain slot (live-check C1): cancel/pause stop the running
    // transfer's job instead of leaving it waiting on frames the host stopped sending.
    private lateinit var controller: TransferQueueController

    // Control-plane awaiters keyed by transferId (fulfilled from the fileTransferMessages collector).
    private val readyWaiters = ConcurrentHashMap<String, CompletableDeferred<ReadyInfo>>()
    private val resultWaiters = ConcurrentHashMap<String, CompletableDeferred<ResultInfo>>()
    private val completeWaiters = ConcurrentHashMap<String, CompletableDeferred<String>>()

    /** Callback the foreground service sets so it can stop itself once the queue is idle. */
    @Volatile var onQueueIdle: (() -> Unit)? = null

    private data class ReadyInfo(val accepted: Boolean, val startOffset: Long, val declineReason: String?)

    private data class ResultInfo(val verified: Boolean, val sha256: String?, val error: String?)

    fun start(ctx: Context) {
        if (started) return
        started = true
        appContext = ctx.applicationContext
        settings = SettingsManager(appContext)
        queueStore = TransferQueueStore(appContext.filesDir)
        // Perf audit P1-32: bound transfer_queue.json for an app the user never reopens by
        // dropping stale terminal entries at startup. See TransferQueueStore.pruneStale for why
        // this doesn't run on every terminal transition instead.
        synchronized(queueLock) { _queue.value = queueStore.pruneStale() }
        controller = TransferQueueController(queueStore, _queue, queueLock)
        // A previous process that died mid-transfer left its row Negotiating/Active/Verifying, which
        // the drain never picks: park it as Paused so the user can resume it (or cancel it).
        controller.recoverInterrupted()
        // Live-check C6: delete partials and staged share copies that no row owns any more (a cancel
        // or pruned row from an earlier run). Rows that can still resume keep theirs.
        scope.launch { sweepLocalFiles() }

        scope.launch {
            RemexClientManager.fileTransferMessages.collect { json -> onControlMessage(json) }
        }
        scope.launch {
            // Proactive staleness defence (RemEx-ix8d): a control-plane (re)connect implies the prior
            // /ws/files socket is a stale zombie (both channels target the same host over the same path),
            // so force-nuke it on every false->true transition. The next transfer then dials a FRESH
            // binary channel instead of streaming bytes into a silently-dead OkHttp socket. The first
            // connect is a harmless no-op (no channel open yet).
            var wasConnected = false
            RemexClientManager.isConnected.collect { connected ->
                if (connected && !wasConnected) FileTransferChannelClient.invalidate()
                wasConnected = connected
            }
        }
        scope.launch { drainLoop() }
    }

    // ── Public API (used by WP7 UI / WP8 share sheet) ─────────────────────────

    /** Enqueues an upload/push of a local content [localUri] to a shared root on the peer. */
    fun enqueueUpload(
        localUri: String,
        fileName: String,
        size: Long,
        destRoot: String,
        destRelativePath: String?,
        push: Boolean = false,
        peerId: String? = null,
    ): String {
        val t =
            QueuedTransfer(
                id = UUID.randomUUID().toString(),
                mode = if (push) FileTransferModes.PUSH else FileTransferModes.UPLOAD,
                fileName = fileName,
                size = size,
                localUri = localUri,
                destRoot = destRoot,
                destRelativePath = destRelativePath,
                peerId = peerId,
            )
        controller.enqueue(listOf(t))
        return t.id
    }

    /** One download for [enqueueDownloads]: a peer file (root/relative path) to local [destUri]. */
    data class DownloadRequest(
        val destUri: String,
        val fileName: String,
        val size: Long,
        val sourceRoot: String,
        val sourceRelativePath: String?,
        val peerId: String? = null,
    )

    /** Enqueues a download of a peer file (identified by root/relative path) to local [destUri]. */
    fun enqueueDownload(
        destUri: String,
        fileName: String,
        size: Long,
        sourceRoot: String,
        sourceRelativePath: String?,
        peerId: String? = null,
    ): String =
        enqueueDownloads(listOf(DownloadRequest(destUri, fileName, size, sourceRoot, sourceRelativePath, peerId)))
            .single()

    /**
     * Enqueues many downloads in one queue write and one queue emission (a folder download: one
     * rewrite of the queue file and one recomposition per file was O(n^2), live-check C3).
     */
    fun enqueueDownloads(requests: List<DownloadRequest>): List<String> {
        val transfers =
            requests.map { r ->
                QueuedTransfer(
                    id = UUID.randomUUID().toString(),
                    mode = FileTransferModes.DOWNLOAD,
                    fileName = r.fileName,
                    size = r.size,
                    localUri = r.destUri,
                    destRoot = r.sourceRoot,
                    destRelativePath = r.sourceRelativePath,
                    peerId = r.peerId,
                )
            }
        controller.enqueue(transfers)
        return transfers.map { it.id }
    }

    /**
     * Cancels a transfer: stops its run if it holds the drain slot (live-check C1 - it used to keep
     * waiting for frames the host had stopped sending, blocking every later transfer), then deletes its
     * partial or staged source and the empty download target once that run has unwound.
     */
    fun cancel(transferId: String) {
        sendControl(transferId, FileTransferControlActions.CANCEL)
        FileTransferChannelClient.unregisterSink(transferId)
        val stopped = controller.cancel(transferId) ?: return
        forgetRate(transferId)
        cleanUpAfterStop(listOf(stopped))
    }

    /**
     * Cancels every unfinished transfer (live-check C4). Only rows the host has already seen get a
     * cancel message; a never-offered Queued row does not, and a folder can queue hundreds of them.
     * A paused-then-resumed row is Queued too but the host knows it ([TransferQueueController.Stopped.hostKnows]).
     */
    fun cancelAll() {
        val stopped = controller.cancelAll()
        for (s in stopped) {
            if (s.hostKnows) {
                sendControl(s.transfer.id, FileTransferControlActions.CANCEL)
                FileTransferChannelClient.unregisterSink(s.transfer.id)
            }
            forgetRate(s.transfer.id)
        }
        cleanUpAfterStop(stopped)
    }

    // Query + deleteDocument is ContentResolver I/O and cancel() is often invoked straight from a UI
    // thread (or from a coroutine that is itself mid-cancellation), so hop onto the engine's own
    // long-lived scope rather than a `withContext` that would silently no-op the cleanup if the
    // caller's scope is already cancelled. The join comes first: the run may still be writing the
    // partial, or reading the staged source, until it has unwound.
    private fun cleanUpAfterStop(stopped: List<TransferQueueController.Stopped>) {
        if (stopped.isEmpty()) return
        scope.launch {
            for (s in stopped) {
                s.job?.join()
                discardLocalFiles(s.transfer)
                discardEmptyDownloadTarget(s.transfer)
            }
        }
    }

    /**
     * Pauses a transfer (plan WP7 queue control). A not-yet-started (Queued) item is held so the drain
     * loop skips it. A running one has its run stopped (live-check C1): the host cancels its sender on
     * PAUSE, so the run would otherwise wait out the six-hour ceiling while holding the drain slot.
     * [resume] re-queues the transfer. An UPLOAD keeps its source and re-negotiates from the host's
     * durable offset. A DOWNLOAD restarts from zero: the host always serves a download from offset 0
     * (the offer has no field for the receiver's offset; download resume needs a protocol change), so
     * its partial is not kept: [runDownload] deletes it as the stopped run unwinds.
     */
    fun pause(transferId: String) {
        sendControl(transferId, FileTransferControlActions.PAUSE)
        // A resumed transfer restarts its byte count; a live estimator would measure across the gap.
        forgetRate(transferId)
        controller.pause(transferId)
    }

    fun resume(transferId: String) {
        sendControl(transferId, FileTransferControlActions.RESUME)
        controller.resume(transferId)
    }

    fun clearFinished() {
        val removed = controller.clearFinished()
        // A cleared Failed row still owned its partial (it could have been resumed); nothing does now.
        if (removed.isNotEmpty()) scope.launch { removed.forEach { discardLocalFiles(it) } }
    }

    // ── Queue drain loop (FIFO, one active at a time) ─────────────────────────

    // Perf audit P0-8 (suspend on the queue instead of polling) lives on in TransferQueueController.
    private suspend fun drainLoop() {
        controller.drain(onIdle = { onQueueIdle?.invoke() }) { t -> runOne(t) }
    }

    // Leased for the whole transfer (perf audit P0-7): the binary channel's idle close must not fire
    // between ensureChannel and the sink registration that follows a successful negotiate - nor across
    // a resumed download's re-hash of its partial, which also runs before the sink goes up.
    private suspend fun runOne(t: QueuedTransfer) = FileTransferChannelClient.withLease { runOneLeased(t) }

    private suspend fun runOneLeased(t: QueuedTransfer) {
        try {
            // The controller already moved the row to Negotiating when it claimed the drain slot.
            if (!ensureChannel()) {
                updateState(t.id) { it.copy(state = TransferState.Failed, error = "Not connected.") }
                if (t.mode == FileTransferModes.DOWNLOAD) discardEmptyDownloadTarget(t)
                return
            }
            when (t.mode) {
                FileTransferModes.UPLOAD, FileTransferModes.PUSH -> runUpload(t)
                FileTransferModes.DOWNLOAD -> runDownload(t)
                else -> updateState(t.id) { it.copy(state = TransferState.Failed, error = "Unknown mode.") }
            }
        } catch (e: CancellationException) {
            // A user cancel or pause (or the process scope going away). Not a failure: the row already
            // says Cancelled/Paused, and the binary channel is healthy - invalidating it here would
            // tear down every other transfer's socket for nothing.
            throw e
        } catch (e: Exception) {
            Log.w(TAG, "Transfer ${t.id} failed", e)
            updateState(t.id) { it.copy(state = TransferState.Failed, error = e.message) }
            // A mid-stream failure (channel closed, broken pipe, ack stall) means the binary channel is
            // dead/stale — nuke it so the next transfer reconnects fresh instead of reusing the zombie
            // and streaming into the void again. (RemEx-ix8d.)
            FileTransferChannelClient.invalidate()
            // A push/upload that dies mid-stream (thrown here, not via runUpload's own terminal
            // branches) still owes the user a Share-to-PC result, same as runDownload's notify.
            if (t.mode != FileTransferModes.DOWNLOAD) notifyUploadFailed(t) else discardEmptyDownloadTarget(t)
        }
    }

    private suspend fun runUpload(t: QueuedTransfer) {
        val resume = t.bytesTransferred in 1 until t.size
        val ready = negotiate(t, resumeRequested = resume)
        if (ready == null) {
            // negotiate() already marked the transfer Failed with its reason.
            notifyUploadFailed(t)
            return
        }
        if (!ready.accepted) {
            updateState(t.id) { it.copy(state = TransferState.Failed, error = ready.declineReason ?: "Declined.") }
            notifyUploadFailed(t)
            return
        }
        val startOffset = TransferResumeLogic.senderStartOffset(ready.startOffset, t.size)
        updateState(t.id) { it.copy(state = TransferState.Active, bytesTransferred = startOffset) }

        val digest = MessageDigest.getInstance("SHA-256")
        // Backpressure: track the peer's committed offset via inbound ack frames on the channel.
        val committed = java.util.concurrent.atomic.AtomicLong(startOffset)
        val ackSignal = Channel<Unit>(Channel.CONFLATED)
        val sink =
            object : FileFrameSink {
                override fun onFrame(envelope: FileFrameEnvelope, payload: ByteArray) {
                    if (envelope.kind == FileFrameKinds.ACK) {
                        envelope.committedOffset?.let { committed.set(it) }
                        ackSignal.trySend(Unit)
                    } else if (envelope.kind == FileFrameKinds.ERROR) {
                        ackSignal.close()
                    }
                }

                override fun onChannelClosed() {
                    ackSignal.close()
                }
            }
        FileTransferChannelClient.registerSink(t.id, sink)
        try {
            val input =
                appContext.contentResolver.openInputStream(Uri.parse(t.localUri))
                    ?: throw IllegalStateException("Cannot open source file.")
            input.use {
                // On resume the receiver has our earlier bytes; we must re-hash them locally so the
                // final full-file SHA-256 matches (incremental hash state is not serialized — §1.3).
                var sent = 0L
                if (startOffset > 0) {
                    val reBuf = ByteArray(65536)
                    var remaining = startOffset
                    while (remaining > 0) {
                        val toRead = minOf(reBuf.size.toLong(), remaining).toInt()
                        val r = it.read(reBuf, 0, toRead)
                        if (r <= 0) break
                        digest.update(reBuf, 0, r)
                        remaining -= r
                        sent += r
                    }
                }
                // The send loop itself is extracted to UploadSendLoop (RemEx-yi7id): this object is a
                // singleton driving the FileTransferChannelClient singleton directly, so it has no
                // constructor to seam a defaulted cap onto the way FileHostHandler does — the
                // collaborator is what makes the loop (and its cap) unit-testable without mutable
                // global state.
                sent =
                    UploadSendLoop(FileTransferChannelClient::sendData).run(
                        transferId = t.id,
                        input = it,
                        size = t.size,
                        initialSent = sent,
                        committed = committed,
                        ackSignal = ackSignal,
                        digest = digest,
                        onProgress = { bytes -> updateProgress(t.id, bytes) },
                    )
                // Drain before completing (RemEx-y6x6): the bulk data frames and file_transfer_complete
                // travel on SEPARATE sockets (/ws/files vs the control /ws). If we announce completion the
                // instant the last frame is enqueued, the tiny complete overtakes the still-in-flight bulk
                // bytes — the host finalizes against a partial file and fails "Transfer incomplete" while the
                // data is literally still arriving (host logs showed complete processed BEFORE the first data
                // frame). Wait until the host has ACKed every byte; its Final-frame ack sets committed==size.
                while (committed.get() < t.size) {
                    if (ackSignal.receiveCatching().isClosed) {
                        throw IllegalStateException("Channel closed before the host acknowledged all data.")
                    }
                }
            }
            val sha = Base64.encodeToString(digest.digest(), Base64.NO_WRAP)
            updateState(t.id) { it.copy(state = TransferState.Verifying, sha256 = sha) }
            sendComplete(t.id, sha)
            val result = awaitResult(t.id)
            if (result != null && result.verified) {
                updateState(t.id) { it.copy(state = TransferState.Done) }
                // A Share-to-PC push reads from a private staged copy; it has landed, so drop it
                // (live-check C6). A content:// source the user picked is left alone.
                discardLocalFiles(t)
                // Confirm the send (parity with runDownload's completion notification).
                FileTransferNotificationManager.showTransferComplete(appContext, t.fileName, isDownload = false)
            } else {
                updateState(t.id) {
                    it.copy(state = TransferState.Failed, error = result?.error ?: "Verification failed.")
                }
                notifyUploadFailed(t)
            }
        } finally {
            FileTransferChannelClient.unregisterSink(t.id)
        }
    }

    /** Posts a Share-to-PC failure notification for [t]; the body names the file via localized text. */
    private fun notifyUploadFailed(t: QueuedTransfer) {
        val message = appContext.getString(R.string.file_transfer_notification_upload_failed, t.fileName)
        FileTransferNotificationManager.showTransferFailed(appContext, message)
    }

    private suspend fun runDownload(t: QueuedTransfer) {
        // Registered BEFORE the offer (live-check C3). For an empty file the host sends no data frame
        // and announces file_transfer_complete straight away - before this side used to start
        // listening, so the message was dropped and every empty file cost the full complete timeout.
        completeWaiters[t.id] = CompletableDeferred()
        try {
            runDownloadStream(t)
        } finally {
            completeWaiters.remove(t.id)
            // No download partial outlives its run, whatever ended it (success, failure, cancel,
            // pause). The host always serves a download from offset 0 - the offer carries no receiver
            // offset - so a kept partial could never be resumed from; it was deleted on the next run
            // anyway, after sitting in filesDir. Deleted HERE, inside the run, because the drain does
            // not start the next run (a quick resume of this row) until this one has unwound: a
            // cleanup launched elsewhere could delete the partial the re-run is writing.
            File(downloadDir(), TransferLocalFiles.partialFileName(t.id)).delete()
        }
    }

    private suspend fun runDownloadStream(t: QueuedTransfer) {
        val partial = File(downloadDir(), TransferLocalFiles.partialFileName(t.id))
        downloadDir().mkdirs()
        val existing = if (partial.exists()) partial.length() else 0L
        val resume = TransferResumeLogic.shouldRequestResume(existing, t.size)
        val ready = negotiate(t, resumeRequested = resume)
        if (ready == null) {
            // negotiate() already marked the transfer Failed ("Peer did not respond.").
            discardEmptyDownloadTarget(t)
            return
        }
        if (!ready.accepted) {
            updateState(t.id) { it.copy(state = TransferState.Failed, error = ready.declineReason ?: "Declined.") }
            discardEmptyDownloadTarget(t)
            return
        }
        val startOffset = TransferResumeLogic.receiverResumeOffset(ready.startOffset, t.size)
        if (startOffset == 0L && partial.exists()) partial.delete()
        updateState(t.id) { it.copy(state = TransferState.Active, bytesTransferred = startOffset) }

        val digest = MessageDigest.getInstance("SHA-256")
        val raf = java.io.RandomAccessFile(partial, "rw")
        raf.seek(startOffset)
        if (startOffset > 0) {
            // Re-hash the surviving partial so the end-of-stream compare is over the full file.
            java.io.RandomAccessFile(partial, "r").use { rh ->
                val buf = ByteArray(65536)
                var remaining = startOffset
                while (remaining > 0) {
                    val r = rh.read(buf, 0, minOf(buf.size.toLong(), remaining).toInt())
                    if (r <= 0) break
                    digest.update(buf, 0, r)
                    remaining -= r
                }
            }
        }

        val done = CompletableDeferred<Boolean>()
        val received = java.util.concurrent.atomic.AtomicLong(startOffset)
        val lastAcked = java.util.concurrent.atomic.AtomicLong(startOffset)
        // Perf audit P4-6: the write, hash and ack below run on this worker, NOT on the OkHttp reader
        // thread that delivers the frame, so the socket keeps being read while a frame is on its way
        // to disk. Still strictly serial and still ack-after-write. Sized to the sender's unacked
        // window, so in practice the reader never blocks in offer().
        val writer =
            SerialFrameWorker<Pair<FileFrameEnvelope, ByteArray>>(scope, DOWNLOAD_WRITE_QUEUE_FRAMES) { (envelope, payload) ->
                try {
                    synchronized(raf) {
                        if (envelope.offset != received.get()) {
                            FileTransferChannelClient.sendError(t.id, "Out-of-order frame.")
                            done.complete(false)
                        } else {
                            raf.write(payload)
                            digest.update(payload)
                            val now = received.addAndGet(payload.size.toLong())
                            if (envelope.final || now - lastAcked.get() >= FileTransferLimits.ACK_INTERVAL_BYTES) {
                                lastAcked.set(now)
                                FileTransferChannelClient.sendAck(t.id, now)
                            }
                            updateProgress(t.id, now)
                            if (envelope.final) done.complete(true)
                        }
                    }
                } catch (e: Exception) {
                    FileTransferChannelClient.sendError(t.id, e.message ?: "write failed")
                    done.complete(false)
                }
            }
        val sink =
            object : FileFrameSink {
                override fun onFrame(envelope: FileFrameEnvelope, payload: ByteArray) {
                    if (envelope.kind != FileFrameKinds.DATA) {
                        if (envelope.kind == FileFrameKinds.ERROR) done.complete(false)
                        return
                    }
                    // False only once the writer has stopped (shut down, or it died): nothing will
                    // write this frame, so the transfer cannot finish.
                    if (!writer.offer(envelope to payload)) done.complete(false)
                }

                override fun onChannelClosed() {
                    if (!done.isCompleted) done.complete(false)
                }
            }
        FileTransferChannelClient.registerSink(t.id, sink)
        // Nothing left to receive (an empty file, or a partial that already holds every byte): the host
        // sends no frame at all, so there is no Final frame to wait for (live-check C3).
        // That skip trusts the listing size, so the host's complete must then confirm it: see
        // TransferResumeLogic.downloadVerified.
        val skippedFrameWait = !TransferResumeLogic.expectsDataFrames(startOffset, t.size)
        if (skippedFrameWait) done.complete(true)
        try {
            // The PC sender's file_transfer_complete carries the authoritative full-file hash.
            val streamedOk = withTimeoutOrNull(TRANSFER_TIMEOUT_MS) { done.await() } ?: false
            // Stop the writer BEFORE raf is synced and closed below: on a failed or timed-out stream
            // it may still be mid-write. On success it is already idle - done completes from inside
            // the handler, after the final frame was written.
            writer.shutdown()
            if (streamedOk) {
                // The bar hits 100% here, but fsync + hash-wait + the full .part→SAF commit copy are
                // still ahead — surface the same Verifying state uploads already use so big files
                // don't sit on a frozen 100% Active bar (RemEx-hb1t.2).
                updateState(t.id) { it.copy(state = TransferState.Verifying) }
            }
            try { raf.fd.sync() } catch (e: Exception) {}
            raf.close()
            val expectedSha = awaitComplete(t.id)
            val actualSha = Base64.encodeToString(digest.digest(), Base64.NO_WRAP)
            val verified = TransferResumeLogic.downloadVerified(streamedOk, skippedFrameWait, expectedSha, actualSha)
            if (verified && commitDownload(partial, t.localUri)) {
                partial.delete()
                sendResult(t.id, true, actualSha, null)
                updateState(t.id) { it.copy(state = TransferState.Done, sha256 = actualSha) }
                // Open-after-download (WP8): surface an "Open" completion notification for the file.
                FileTransferNotificationManager.showDownloadComplete(appContext, t.fileName, t.localUri)
            } else {
                partial.delete()
                // A skipped wait with no complete from the host is unconfirmed, not a hash mismatch.
                val err =
                    if (!streamedOk || (skippedFrameWait && expectedSha == null)) "Transfer incomplete."
                    else "SHA-256 mismatch."
                sendResult(t.id, false, actualSha, err)
                updateState(t.id) { it.copy(state = TransferState.Failed, error = err) }
                discardEmptyDownloadTarget(t)
            }
        } finally {
            FileTransferChannelClient.unregisterSink(t.id)
            // Idempotent; here for the exception and cancellation paths, which skip the call above.
            withContext(NonCancellable) { writer.shutdown() }
            // Same paths: a cancel or pause lands while the stream is waiting, and the open handle
            // would otherwise leak. Closing twice is a no-op.
            try { raf.close() } catch (e: Exception) {}
        }
    }

    /**
     * Deletes the pre-created SAF destination document of a download that ended without a successful
     * commit (declined, timed out, failed, cancelled), so retries stop accumulating 0-byte ghosts
     * ("name.ext", "name (1).ext", …). Deletes ONLY when the document verifiably reports zero length:
     * the CreateDocument picker can hand back an EXISTING document the user chose to overwrite, and
     * until [commitDownload] runs it still holds the user's original bytes. (RemEx-hb1t.1)
     */
    private fun discardEmptyDownloadTarget(t: QueuedTransfer) {
        if (t.mode != FileTransferModes.DOWNLOAD) return
        try {
            val uri = Uri.parse(t.localUri)
            val size =
                appContext.contentResolver
                    .query(uri, arrayOf(OpenableColumns.SIZE), null, null, null)
                    ?.use { c -> if (c.moveToFirst() && !c.isNull(0)) c.getLong(0) else null }
            if (size == 0L) {
                DocumentsContract.deleteDocument(appContext.contentResolver, uri)
            }
        } catch (e: Exception) {
            // Best-effort cleanup: the ghost file is cosmetic; never let cleanup mask the real failure.
            Log.w(TAG, "Could not clean up empty download target", e)
        }
    }

    private fun commitDownload(partial: File, destUri: String): Boolean =
        try {
            appContext.contentResolver.openOutputStream(Uri.parse(destUri), "w")?.use { out ->
                partial.inputStream().use { it.copyTo(out, 65536) }
                true
            } ?: false
        } catch (e: Exception) {
            Log.w(TAG, "commitDownload failed", e)
            false
        }

    // ── Control-plane send + await ────────────────────────────────────────────

    private suspend fun negotiate(t: QueuedTransfer, resumeRequested: Boolean): ReadyInfo? {
        val waiter = CompletableDeferred<ReadyInfo>()
        readyWaiters[t.id] = waiter
        sendOffer(t, resumeRequested)
        // finally: a cancel or pause can now stop the run mid-wait (live-check C1).
        val ready =
            try {
                withTimeoutOrNull(NEGOTIATE_TIMEOUT_MS) { waiter.await() }
            } finally {
                readyWaiters.remove(t.id)
            }
        if (ready == null) {
            updateState(t.id) { it.copy(state = TransferState.Failed, error = "Peer did not respond.") }
            // No control reply within the window: the socket(s) to the host are very likely silently-dead
            // OkHttp zombies (send() keeps enqueueing into a half-open link, so bytes never leave the
            // device and the host receives nothing). Force-nuke the binary channel so the retry dials a
            // FRESH /ws/files socket instead of streaming into the void again. (RemEx-ix8d.)
            FileTransferChannelClient.invalidate()
        }
        return ready
    }

    private suspend fun awaitResult(id: String): ResultInfo? {
        val waiter = CompletableDeferred<ResultInfo>()
        resultWaiters[id] = waiter
        try {
            return withTimeoutOrNull(TRANSFER_TIMEOUT_MS) { waiter.await() }
        } finally {
            resultWaiters.remove(id)
        }
    }

    /** Uses the waiter [runDownload] registered before its offer, so an early complete is not lost. */
    private suspend fun awaitComplete(id: String): String? {
        val waiter = completeWaiters.getOrPut(id) { CompletableDeferred() }
        try {
            return withTimeoutOrNull(NEGOTIATE_TIMEOUT_MS) { waiter.await() }
        } finally {
            completeWaiters.remove(id)
        }
    }

    private fun onControlMessage(json: String) {
        val obj = try { JSONObject(json) } catch (e: Exception) { return }
        when (obj.optString("type")) {
            "file_transfer_ready" -> {
                val p = obj.optJSONObject("fileTransferReady") ?: return
                val id = p.optString("transferId")
                readyWaiters[id]?.complete(
                    ReadyInfo(
                        accepted = p.optBoolean("accepted", false),
                        startOffset = p.optLong("startOffset", 0L),
                        declineReason = if (p.has("declineReason")) p.optString("declineReason") else null,
                    )
                )
            }
            "file_transfer_result" -> {
                val p = obj.optJSONObject("fileTransferResult") ?: return
                val id = p.optString("transferId")
                resultWaiters[id]?.complete(
                    ResultInfo(
                        verified = p.optBoolean("verified", false),
                        sha256 = if (p.has("sha256")) p.optString("sha256") else null,
                        error = if (p.has("error")) p.optString("error") else null,
                    )
                )
            }
            "file_transfer_complete" -> {
                val p = obj.optJSONObject("fileTransferComplete") ?: return
                val id = p.optString("transferId")
                completeWaiters[id]?.complete(p.optString("sha256"))
            }
        }
    }

    private fun sendOffer(t: QueuedTransfer, resumeRequested: Boolean) {
        val payload =
            JSONObject().apply {
                put("transferId", t.id)
                put("mode", t.mode)
                if (t.sourcePath != null) put("sourcePath", t.sourcePath)
                if (t.destRoot != null) put("destRoot", t.destRoot)
                if (t.destRelativePath != null) put("destRelativePath", t.destRelativePath)
                put("fileName", t.fileName)
                put("size", t.size)
                put("resumeRequested", resumeRequested)
            }
        send("file_transfer_offer", "fileTransferOffer", payload)
    }

    private fun sendComplete(id: String, sha256: String) {
        send(
            "file_transfer_complete",
            "fileTransferComplete",
            JSONObject().apply {
                put("transferId", id)
                put("sha256", sha256)
            },
        )
    }

    private fun sendResult(id: String, verified: Boolean, sha256: String?, error: String?) {
        send(
            "file_transfer_result",
            "fileTransferResult",
            JSONObject().apply {
                put("transferId", id)
                put("verified", verified)
                if (sha256 != null) put("sha256", sha256)
                if (error != null) put("error", error)
            },
        )
    }

    private fun sendControl(id: String, action: String) {
        send(
            "file_transfer_control",
            "fileTransferControl",
            JSONObject().apply {
                put("transferId", id)
                put("action", action)
            },
        )
    }

    private fun send(type: String, payloadKey: String, payload: JSONObject) {
        val envelope =
            JSONObject().apply {
                put("type", type)
                put("protocolVersion", 3)
                put(payloadKey, payload)
            }
        RemexCoreClient.SendMessage(envelope.toString())
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private suspend fun ensureChannel(): Boolean {
        val host = settings.hostFlow.first()
        val port = settings.portFlow.first()
        if (host.isBlank()) return false
        val clientId = settings.getOrCreateClientId()

        // **ASKED OF THIS HOST, NOT OF THE WORLD (RemEx-5t4k9).** This shortcut used to be a bare
        // isOpen taken before any of the settings above were read, so a channel still open to a PC
        // the user had stopped using answered yes and ensureConnected - which does compare the host -
        // was never reached. This is the upload path, so the effect was that the offer was negotiated
        // with the PC the control plane had reconnected to while the bytes went down a socket to the
        // previous one. Found by review of the identical defect in AndroidFileTransferHost, which is
        // what the bead was actually filed for.
        if (FileTransferChannelClient.isOpenTo(host, port, clientId)) return true
        val spki = PinnedHostStore.getPin(appContext, host)?.takeIf { it.isNotBlank() } ?: return false
        return FileTransferChannelClient.ensureConnected(appContext, host, port, clientId, spki)
    }

    private fun downloadDir(): File = File(appContext.filesDir, TransferLocalFiles.DOWNLOAD_PARTIAL_DIR)

    private fun shareStagingRoot(): File = File(appContext.filesDir, TransferLocalFiles.SHARE_STAGING_DIR)

    /** Deletes [t]'s download partial or staged share copy (live-check C6). Never throws. */
    private fun discardLocalFiles(t: QueuedTransfer) {
        try {
            TransferLocalFiles.discard(t, downloadDir(), shareStagingRoot())
        } catch (e: Exception) {
            Log.w(TAG, "Could not delete local files of transfer ${t.id}", e)
        }
    }

    private fun sweepLocalFiles() {
        try {
            val deleted =
                TransferLocalFiles.sweep(downloadDir(), shareStagingRoot(), controller.snapshot(), System.currentTimeMillis())
            if (deleted.isNotEmpty()) Log.i(TAG, "Swept ${deleted.size} orphaned transfer file(s).")
        } catch (e: Exception) {
            Log.w(TAG, "Transfer file sweep failed", e)
        }
    }

    /** A state write from a transfer's own run; see [TransferQueueController.updateFromRun]. */
    private fun updateState(id: String, transform: (QueuedTransfer) -> QueuedTransfer) {
        val updated = controller.updateFromRun(id, transform) ?: return

        // RATE CLEANUP LIVES HERE RATHER THAN AT THE ELEVEN TERMINAL CALL SITES, because a
        // per-site version is a list that a future state transition silently drops off — and the
        // leak it would cause is unbounded: this is a process-lifetime singleton, so one orphaned
        // estimator per finished transfer accumulates for as long as the app runs.
        if (updated.state in TerminalStates) forgetRate(id)
    }

    private val TerminalStates = setOf(
        TransferState.Done,
        TransferState.Failed,
        TransferState.Cancelled,
    )

    /**
     * In-memory-only progress update (no disk write). Called per data frame, which would otherwise
     * thrash the queue file; the durable checkpoints are the state transitions via [updateState].
     */
    private fun updateProgress(id: String, bytes: Long) {
        // SystemClock.elapsedRealtime(), never System.currentTimeMillis(). A wall clock steps
        // backwards on an NTP correction, and the estimator correctly refuses a negative interval -
        // so a wall clock would not produce a wrong speed, it would silently stop producing one.
        estimatorFor(id).update(bytes, SystemClock.elapsedRealtime())

        controller.progress(id, bytes)
    }

    // ── Throughput and time-remaining (RemEx-qmiv) ────────────────────────────

    /**
     * One estimator per in-flight transfer, DELIBERATELY NOT PART OF [QueuedTransfer].
     *
     * The queue model is serialized to disk on every state transition, and a rate is the one thing
     * in this system that is meaningless the moment it is reloaded — restoring "12.4 MB/s" from a
     * file written before the app was killed would describe a transfer that is not running.
     * Throughput is live-only state, so it lives beside the queue rather than inside it.
     */
    private val rateEstimators = java.util.concurrent.ConcurrentHashMap<String, TransferRateEstimator>()

    private fun estimatorFor(id: String): TransferRateEstimator =
        rateEstimators.getOrPut(id) { TransferRateEstimator() }

    /** Current throughput for [transferId] in bytes per second, or null when it is not known. */
    fun bytesPerSecond(transferId: String): Double? =
        rateEstimators[transferId]?.bytesPerSecondAt(SystemClock.elapsedRealtime())

    /** Seconds until [transferId] finishes, or null when that cannot honestly be said. */
    fun secondsRemaining(transferId: String): Double? {
        val estimator = rateEstimators[transferId] ?: return null
        val item = _queue.value.firstOrNull { it.id == transferId } ?: return null

        return estimator.secondsRemainingAt(
            item.bytesTransferred, item.size.takeIf { it > 0L }, SystemClock.elapsedRealtime())
    }

    /**
     * Forgets a transfer's rate.
     *
     * Called on pause and on every terminal state. A paused transfer that resumed against a live
     * estimator would measure across the gap and read as a long stall — and since the gap can be
     * hours, the first ETA after resuming would be the absurd kind the display refuses to show.
     */
    private fun forgetRate(transferId: String) {
        rateEstimators.remove(transferId)
    }

    private const val NEGOTIATE_TIMEOUT_MS = 30_000L
    private const val TRANSFER_TIMEOUT_MS = 6L * 60 * 60 * 1000 // 6h ceiling for a single transfer

    // P4-6: the sender never has more than MAX_UNACKED_BYTES in flight, so at full-size frames this
    // many can ever be waiting for the writer.
    private const val DOWNLOAD_WRITE_QUEUE_FRAMES =
        FileTransferLimits.MAX_UNACKED_BYTES / FileTransferLimits.DATA_PAYLOAD_BYTES
}

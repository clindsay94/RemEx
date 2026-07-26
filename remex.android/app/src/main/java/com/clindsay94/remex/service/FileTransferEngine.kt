package com.clindsay94.remex.service

import android.content.Context
import android.net.Uri
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
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import org.json.JSONObject

/**
 * Android-initiated transfer engine (plan WP5). Drives the v3 negotiation on the native `/ws` control
 * plane (`file_transfer_offer/ready/complete/result/control` via [RemexCoreClient]) and the bulk data
 * on the shared binary `/ws/files` channel ([FileTransferChannelClient]). Supports offset-based resume
 * (re-hash on resume) and ack-driven backpressure, exactly mirroring the PC host's
 * `TransferSessionManager`.
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
        _queue.value = queueStore.load()

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
        _queue.value = queueStore.upsert(t)
        return t.id
    }

    /** Enqueues a download of a peer file (identified by root/relative path) to local [destUri]. */
    fun enqueueDownload(
        destUri: String,
        fileName: String,
        size: Long,
        sourceRoot: String,
        sourceRelativePath: String?,
        peerId: String? = null,
    ): String {
        val t =
            QueuedTransfer(
                id = UUID.randomUUID().toString(),
                mode = FileTransferModes.DOWNLOAD,
                fileName = fileName,
                size = size,
                localUri = destUri,
                destRoot = sourceRoot,
                destRelativePath = sourceRelativePath,
                peerId = peerId,
            )
        _queue.value = queueStore.upsert(t)
        return t.id
    }

    fun cancel(transferId: String) {
        sendControl(transferId, FileTransferControlActions.CANCEL)
        FileTransferChannelClient.unregisterSink(transferId)
        val t = _queue.value.firstOrNull { it.id == transferId }
        if (t != null && t.state != TransferState.Done) {
            // Query + deleteDocument is ContentResolver I/O; cancel() is often invoked straight from a
            // UI thread (or from a coroutine that is itself mid-cancellation), so hop onto the engine's
            // own long-lived scope rather than a `withContext` that would silently no-op the cleanup if
            // the caller's scope is already cancelled.
            scope.launch { discardEmptyDownloadTarget(t) }
        }
        updateState(transferId) { it.copy(state = TransferState.Cancelled) }
    }

    /**
     * Pauses a transfer (plan WP7 queue control). A not-yet-started (Queued) item is held so the drain
     * loop skips it; an already-Active item's Paused mark is best-effort — the current stream runs to
     * its natural end (Done/Failed) since the WP5 streaming loops don't yet cooperatively yield, and
     * [cancel] is the immediate stop. [resume] re-queues a paused (or previously failed) transfer,
     * which then re-negotiates from its last durable offset via the existing resume machinery.
     */
    fun pause(transferId: String) {
        sendControl(transferId, FileTransferControlActions.PAUSE)
        updateState(transferId) {
            if (it.state == TransferState.Queued ||
                it.state == TransferState.Negotiating ||
                it.state == TransferState.Active
            ) {
                it.copy(state = TransferState.Paused)
            } else it
        }
    }

    fun resume(transferId: String) {
        sendControl(transferId, FileTransferControlActions.RESUME)
        updateState(transferId) {
            if (it.state == TransferState.Paused || it.state == TransferState.Failed) {
                it.copy(state = TransferState.Queued, error = null)
            } else it
        }
    }

    fun clearFinished() {
        _queue.value = queueStore.pruneFinished()
    }

    // ── Queue drain loop (FIFO, one active at a time) ─────────────────────────

    private suspend fun drainLoop() {
        while (true) {
            // Paused is a held state (WP7): only Queued items are auto-picked. resume() re-queues.
            val next = _queue.value.firstOrNull { it.state == TransferState.Queued }
            if (next == null) {
                onQueueIdle?.invoke()
                // Wait for a new enqueue; poll cheaply.
                kotlinx.coroutines.delay(750)
                continue
            }
            runOne(next)
        }
    }

    private suspend fun runOne(t: QueuedTransfer) {
        try {
            updateState(t.id) { it.copy(state = TransferState.Negotiating, error = null) }
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
                val buf = ByteArray(FileTransferLimits.DATA_PAYLOAD_BYTES)
                while (true) {
                    val read = it.read(buf)
                    if (read <= 0) break
                    while (sent - committed.get() > FileTransferLimits.MAX_UNACKED_BYTES) {
                        // receiveCatching is stable API (unlike isClosedForReceive); a closed channel
                        // means the peer/socket dropped, so abort and let the queue mark this failed.
                        if (ackSignal.receiveCatching().isClosed) {
                            throw IllegalStateException("Channel closed.")
                        }
                    }
                    val isFinal = sent + read >= t.size
                    if (!FileTransferChannelClient.sendData(t.id, sent, buf, read, isFinal)) {
                        throw IllegalStateException("Binary channel closed mid-transfer.")
                    }
                    digest.update(buf, 0, read)
                    sent += read
                    updateProgress(t.id, sent)
                }
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
        val partial = File(downloadDir(), safeStem(t.id) + ".part")
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
        val sink =
            object : FileFrameSink {
                override fun onFrame(envelope: FileFrameEnvelope, payload: ByteArray) {
                    if (envelope.kind != FileFrameKinds.DATA) {
                        if (envelope.kind == FileFrameKinds.ERROR) done.complete(false)
                        return
                    }
                    try {
                        synchronized(raf) {
                            if (envelope.offset != received.get()) {
                                FileTransferChannelClient.sendError(t.id, "Out-of-order frame.")
                                done.complete(false)
                                return
                            }
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
                    } catch (e: Exception) {
                        FileTransferChannelClient.sendError(t.id, e.message ?: "write failed")
                        done.complete(false)
                    }
                }

                override fun onChannelClosed() {
                    if (!done.isCompleted) done.complete(false)
                }
            }
        FileTransferChannelClient.registerSink(t.id, sink)
        try {
            // The PC sender's file_transfer_complete carries the authoritative full-file hash.
            val streamedOk = withTimeoutOrNull(TRANSFER_TIMEOUT_MS) { done.await() } ?: false
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
            val verified = streamedOk && (expectedSha == null || expectedSha == actualSha)
            if (verified && commitDownload(partial, t.localUri)) {
                partial.delete()
                sendResult(t.id, true, actualSha, null)
                updateState(t.id) { it.copy(state = TransferState.Done, sha256 = actualSha) }
                // Open-after-download (WP8): surface an "Open" completion notification for the file.
                FileTransferNotificationManager.showDownloadComplete(appContext, t.fileName, t.localUri)
            } else {
                partial.delete()
                val err = if (!streamedOk) "Transfer incomplete." else "SHA-256 mismatch."
                sendResult(t.id, false, actualSha, err)
                updateState(t.id) { it.copy(state = TransferState.Failed, error = err) }
                discardEmptyDownloadTarget(t)
            }
        } finally {
            FileTransferChannelClient.unregisterSink(t.id)
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
        val ready = withTimeoutOrNull(NEGOTIATE_TIMEOUT_MS) { waiter.await() }
        readyWaiters.remove(t.id)
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
        val r = withTimeoutOrNull(TRANSFER_TIMEOUT_MS) { waiter.await() }
        resultWaiters.remove(id)
        return r
    }

    private suspend fun awaitComplete(id: String): String? {
        val waiter = CompletableDeferred<String>()
        completeWaiters[id] = waiter
        val sha = withTimeoutOrNull(NEGOTIATE_TIMEOUT_MS) { waiter.await() }
        completeWaiters.remove(id)
        return sha
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
        if (FileTransferChannelClient.isOpen) return true
        val host = settings.hostFlow.first()
        val port = settings.portFlow.first()
        if (host.isBlank()) return false
        val clientId = settings.getOrCreateClientId()
        val spki = PinnedHostStore.getPin(appContext, host)?.takeIf { it.isNotBlank() } ?: return false
        return FileTransferChannelClient.ensureConnected(appContext, host, port, clientId, spki)
    }

    private fun downloadDir(): File = File(appContext.filesDir, "transfers/outgoing")

    private fun updateState(id: String, transform: (QueuedTransfer) -> QueuedTransfer) {
        val current = _queue.value.firstOrNull { it.id == id } ?: return
        _queue.value = queueStore.upsert(transform(current))
    }

    /**
     * In-memory-only progress update (no disk write). Called per data frame, which would otherwise
     * thrash the queue file; the durable checkpoints are the state transitions via [updateState].
     */
    private fun updateProgress(id: String, bytes: Long) {
        _queue.value = _queue.value.map { if (it.id == id) it.copy(bytesTransferred = bytes) else it }
    }

    private fun safeStem(id: String): String =
        buildString { for (c in id) append(if (c.isLetterOrDigit() || c == '-' || c == '_') c else '_') }

    private const val NEGOTIATE_TIMEOUT_MS = 30_000L
    private const val TRANSFER_TIMEOUT_MS = 6L * 60 * 60 * 1000 // 6h ceiling for a single transfer
}

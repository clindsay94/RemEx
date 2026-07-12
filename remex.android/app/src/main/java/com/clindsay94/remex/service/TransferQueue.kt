package com.clindsay94.remex.service

import java.io.File
import org.json.JSONArray
import org.json.JSONObject

/**
 * Local, persisted lifecycle of a queued transfer. Mirrors the PC host's
 * `Remex.Core.Models.TransferState` enum name-for-name so the two sides agree on state vocabulary
 * (plan §1.4). The queue itself is device-local (not a wire message); only the enum names are shared.
 */
enum class TransferState {
    Queued,
    Negotiating,
    Active,
    Paused,
    Verifying,
    Done,
    Failed,
    Cancelled,
}

/**
 * One entry in the persistent transfer queue. `mode` uses [FileTransferModes] values. For an
 * upload/push the [localUri] is the content:// source on this device; for a download it is the
 * content:// (or file) destination to write. `destRoot`/`destRelativePath`/`sourcePath`/`fileName`
 * are the offer fields sent to the peer (see `FileTransferOffer` in remex.core).
 */
data class QueuedTransfer(
    val id: String,
    val mode: String,
    val fileName: String,
    val size: Long,
    /** content:// URI of the local file: source for upload/push, destination for download. */
    val localUri: String,
    val destRoot: String? = null,
    val destRelativePath: String? = null,
    val sourcePath: String? = null,
    /** Peer / host identifier this transfer targets (for display + routing). */
    val peerId: String? = null,
    val state: TransferState = TransferState.Queued,
    val bytesTransferred: Long = 0L,
    val sha256: String? = null,
    val error: String? = null,
    val createdAtMs: Long = System.currentTimeMillis(),
) {
    fun toJson(): JSONObject =
        JSONObject().apply {
            put("id", id)
            put("mode", mode)
            put("fileName", fileName)
            put("size", size)
            put("localUri", localUri)
            if (destRoot != null) put("destRoot", destRoot)
            if (destRelativePath != null) put("destRelativePath", destRelativePath)
            if (sourcePath != null) put("sourcePath", sourcePath)
            if (peerId != null) put("peerId", peerId)
            put("state", state.name)
            put("bytesTransferred", bytesTransferred)
            if (sha256 != null) put("sha256", sha256)
            if (error != null) put("error", error)
            put("createdAtMs", createdAtMs)
        }

    companion object {
        fun fromJson(obj: JSONObject): QueuedTransfer =
            QueuedTransfer(
                id = obj.optString("id"),
                mode = obj.optString("mode"),
                fileName = obj.optString("fileName"),
                size = obj.optLong("size", 0L),
                localUri = obj.optString("localUri"),
                destRoot = obj.optStringOrNull("destRoot"),
                destRelativePath = obj.optStringOrNull("destRelativePath"),
                sourcePath = obj.optStringOrNull("sourcePath"),
                peerId = obj.optStringOrNull("peerId"),
                state =
                    runCatching { TransferState.valueOf(obj.optString("state")) }
                        .getOrDefault(TransferState.Queued),
                bytesTransferred = obj.optLong("bytesTransferred", 0L),
                sha256 = obj.optStringOrNull("sha256"),
                error = obj.optStringOrNull("error"),
                createdAtMs = obj.optLong("createdAtMs", System.currentTimeMillis()),
            )
    }
}

private fun JSONObject.optStringOrNull(key: String): String? =
    if (has(key) && !isNull(key)) optString(key) else null

/**
 * Persists the transfer queue to `<dir>/transfer_queue.json` so transfers survive process death
 * (plan §1.4). Pure JVM (takes a [File] directory), so it is unit-testable without Android.
 *
 * Thread-safety: all mutating operations synchronize on this store and rewrite the whole file
 * atomically (write to a temp file, then rename). The queue is small (a handful of entries), so a
 * full rewrite per mutation is simplest and safe.
 */
class TransferQueueStore(private val dir: File) {

    private val file = File(dir, FILE_NAME)
    private val tmp = File(dir, "$FILE_NAME.tmp")
    private val lock = Any()

    fun load(): List<QueuedTransfer> =
        synchronized(lock) {
            if (!file.exists()) return emptyList()
            return try {
                val arr = JSONArray(file.readText())
                buildList {
                    for (i in 0 until arr.length()) {
                        add(QueuedTransfer.fromJson(arr.getJSONObject(i)))
                    }
                }
            } catch (e: Exception) {
                emptyList()
            }
        }

    fun save(transfers: List<QueuedTransfer>) {
        synchronized(lock) {
            if (!dir.exists()) dir.mkdirs()
            val arr = JSONArray()
            transfers.forEach { arr.put(it.toJson()) }
            tmp.writeText(arr.toString())
            // Atomic replace; fall back to copy if rename is unavailable.
            if (!tmp.renameTo(file)) {
                file.writeText(arr.toString())
                tmp.delete()
            }
        }
    }

    /** Inserts or replaces [transfer] by id, preserving queue order (new ids append at the tail). */
    fun upsert(transfer: QueuedTransfer): List<QueuedTransfer> =
        synchronized(lock) {
            val current = load().toMutableList()
            val idx = current.indexOfFirst { it.id == transfer.id }
            if (idx >= 0) current[idx] = transfer else current.add(transfer)
            save(current)
            current
        }

    fun remove(id: String): List<QueuedTransfer> =
        synchronized(lock) {
            val current = load().filterNot { it.id == id }
            save(current)
            current
        }

    /** Removes finished entries (Done/Cancelled) so the queue file does not grow unbounded. */
    fun pruneFinished(): List<QueuedTransfer> =
        synchronized(lock) {
            val current =
                load().filterNot {
                    it.state == TransferState.Done || it.state == TransferState.Cancelled
                }
            save(current)
            current
        }

    companion object {
        const val FILE_NAME = "transfer_queue.json"
    }
}

/**
 * Pure resume-offset arithmetic (plan §1.3). Extracted so the offset decisions can be unit-tested
 * without a live socket or filesystem. The full-file SHA-256 verify at the end remains the final
 * arbiter of integrity; these functions only decide where to (re)start streaming.
 */
object TransferResumeLogic {

    /**
     * Sender (upload/push) start offset. The receiver replies to our offer with `startOffset`; we
     * seek the local source there. Any out-of-range value (negative, or past the file end) forces a
     * clean restart from 0 — mirroring the host, which discards a partial on any identity mismatch.
     */
    fun senderStartOffset(readyStartOffset: Long, fileSize: Long): Long =
        if (readyStartOffset in 0..fileSize) readyStartOffset else 0L

    /**
     * Receiver (download) resume offset: the number of bytes already durably written to the local
     * `.part`. A partial longer than the expected size is corrupt → restart from 0.
     */
    fun receiverResumeOffset(partialLength: Long, expectedSize: Long): Long =
        if (partialLength in 0..expectedSize) partialLength else 0L

    /**
     * Whether an offer should request resume: only when we hold a non-empty, not-yet-complete
     * partial for this transfer.
     */
    fun shouldRequestResume(partialLength: Long, expectedSize: Long): Boolean =
        partialLength in 1 until expectedSize
}

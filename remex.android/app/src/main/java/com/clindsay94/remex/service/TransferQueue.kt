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
    /**
     * True once this row has been offered to the host (the drain claimed it). Stays true across
     * pause/resume: a paused-then-resumed row is Queued again while the host still holds it as
     * Paused, so a cancel must still reach the host (live-check C4 review).
     */
    val hostKnows: Boolean = false,
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
            put("hostKnows", hostKnows)
        }

    companion object {
        fun fromJson(obj: JSONObject): QueuedTransfer {
            val state =
                runCatching { TransferState.valueOf(obj.optString("state")) }
                    .getOrDefault(TransferState.Queued)
            return QueuedTransfer(
                id = obj.optString("id"),
                mode = obj.optString("mode"),
                fileName = obj.optString("fileName"),
                size = obj.optLong("size", 0L),
                localUri = obj.optString("localUri"),
                destRoot = obj.optStringOrNull("destRoot"),
                destRelativePath = obj.optStringOrNull("destRelativePath"),
                sourcePath = obj.optStringOrNull("sourcePath"),
                peerId = obj.optStringOrNull("peerId"),
                state = state,
                bytesTransferred = obj.optLong("bytesTransferred", 0L),
                sha256 = obj.optStringOrNull("sha256"),
                error = obj.optStringOrNull("error"),
                createdAtMs = obj.optLong("createdAtMs", System.currentTimeMillis()),
                // A row persisted before the flag existed: anything past Queued was offered.
                hostKnows = obj.optBoolean("hostKnows", state != TransferState.Queued),
            )
        }
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

    /**
     * Inserts or replaces [transfer] by id into [current], preserving queue order (new ids append
     * at the tail), and persists. [current] is the caller's already-in-memory queue - perf audit
     * P1-32: every mutation used to `load()` from disk first even though the caller (the engine's
     * StateFlow) already held the up-to-date list, doubling I/O on every state transition.
     */
    fun upsert(current: List<QueuedTransfer>, transfer: QueuedTransfer): List<QueuedTransfer> =
        synchronized(lock) {
            val updated = current.toMutableList()
            val idx = updated.indexOfFirst { it.id == transfer.id }
            if (idx >= 0) updated[idx] = transfer else updated.add(transfer)
            save(updated)
            updated
        }

    /**
     * [upsert] for many entries with ONE file write. A folder download enqueues hundreds of files;
     * one rewrite of the whole queue per file was O(n^2) bytes (live-check C3).
     */
    fun upsertAll(current: List<QueuedTransfer>, transfers: List<QueuedTransfer>): List<QueuedTransfer> =
        synchronized(lock) {
            val updated = current.toMutableList()
            val index = HashMap<String, Int>(updated.size * 2)
            updated.forEachIndexed { i, t -> index[t.id] = i }
            for (t in transfers) {
                val idx = index[t.id]
                if (idx != null) {
                    updated[idx] = t
                } else {
                    index[t.id] = updated.size
                    updated.add(t)
                }
            }
            save(updated)
            updated
        }

    /** Removes the entry with [id] from [current] and persists. See [upsert] for why [current] is passed in. */
    fun remove(current: List<QueuedTransfer>, id: String): List<QueuedTransfer> =
        synchronized(lock) {
            val updated = current.filterNot { it.id == id }
            save(updated)
            updated
        }

    /** Removes finished entries (Done/Cancelled/Failed) from [current] so the queue file does not grow unbounded. */
    fun pruneFinished(current: List<QueuedTransfer>): List<QueuedTransfer> =
        synchronized(lock) {
            val updated =
                current.filterNot {
                    it.state == TransferState.Done ||
                        it.state == TransferState.Cancelled ||
                        it.state == TransferState.Failed
                }
            save(updated)
            updated
        }

    /**
     * Drops Done/Cancelled/Failed entries older than [maxAgeMs] (default 7 days). Perf audit P1-32:
     * unlike the host (which removes Done/Cancelled the instant they land, RG:546), Android's
     * queue panel keeps finished entries visible until the user taps "clear finished"
     * ([FileManagerQueuePanel]), so pruning on every terminal transition would make completed
     * transfers vanish before the user ever saw them. Age-based pruning at startup instead bounds
     * `transfer_queue.json` for an app the user never opens, without touching that UX. FAILED is
     * pruned too here (unlike [pruneFinished]'s "clear finished" button semantics) because a
     * failure this stale is no longer something a resumed session should surface unprompted.
     */
    fun pruneStale(maxAgeMs: Long = SEVEN_DAYS_MS): List<QueuedTransfer> =
        synchronized(lock) {
            val cutoff = System.currentTimeMillis() - maxAgeMs
            val loaded = load()
            val updated =
                loaded.filterNot {
                    (it.state == TransferState.Done ||
                        it.state == TransferState.Cancelled ||
                        it.state == TransferState.Failed) &&
                        it.createdAtMs < cutoff
                }
            if (updated.size != loaded.size) save(updated)
            updated
        }

    companion object {
        const val FILE_NAME = "transfer_queue.json"
        const val SEVEN_DAYS_MS = 7L * 24 * 60 * 60 * 1000
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

    /**
     * Whether a download starting at [startOffset] will receive any data frame at all. The host's
     * sender only frames bytes it reads, so an empty file (or a partial that already holds every
     * byte) gets NO frame - not even a Final one - and goes straight to `file_transfer_complete`.
     * Waiting for a Final frame there held the one-at-a-time queue for the six-hour transfer
     * ceiling (live-check C3: a folder with one empty file left every later file on "Queued").
     */
    fun expectsDataFrames(startOffset: Long, expectedSize: Long): Boolean = startOffset < expectedSize

    /**
     * Whether a finished download may be committed. [expectedSha] is the host's
     * `file_transfer_complete` hash (null when it did not arrive in time); [actualSha] is the hash of
     * what this side holds.
     *
     * **When the frame wait was skipped ([skippedFrameWait], see [expectsDataFrames]) a missing
     * complete is a failure, not a pass.** The skip trusts the listing size, and a declared size can
     * be stale (the file grew) or a SAF "unknown" 0 (RG:403). The host's hash is then the ONLY evidence
     * that "nothing to receive" was true; for an empty file it must be the SHA-256 of empty input
     * ([EMPTY_SHA256_B64]), which is exactly what [actualSha] is when nothing was written. Without
     * this an absent complete committed an empty file as Done (live-check C3 review).
     */
    fun downloadVerified(streamedOk: Boolean, skippedFrameWait: Boolean, expectedSha: String?, actualSha: String): Boolean =
        when {
            !streamedOk -> false
            skippedFrameWait -> expectedSha != null && expectedSha == actualSha
            else -> expectedSha == null || expectedSha == actualSha
        }

    /** Base64 SHA-256 of zero bytes (hex e3b0c442...b855), the wire form of an empty file's hash. */
    const val EMPTY_SHA256_B64 = "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU="
}

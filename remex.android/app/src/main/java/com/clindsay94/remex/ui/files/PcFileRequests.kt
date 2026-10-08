package com.clindsay94.remex.ui.files

import com.clindsay94.remex.service.FileTransferLimits
import com.clindsay94.remex.ui.files.preview.RangeChunk
import com.clindsay94.remex.ui.files.preview.RangeReader
import java.io.IOException
import java.util.Base64
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.withTimeout
import org.json.JSONObject

/** The PC refused or failed a request; [hostMessage] is its own (English) reason, for the log. */
class PcRequestFailedException(val hostMessage: String) : IOException(hostMessage)

/** The PC did not answer in time: the connection dropped, or the PC is too old to know the request. */
class PcRequestTimeoutException(what: String) : IOException("The PC did not answer the $what request in time.")

/**
 * The phone's side of the two read-only requests the new File Transfer screen sends to the PC (file browser
 * redesign, 2026-10-08): `file_read_range_request` for previews and live tails, and `file_hash_request` for
 * the Integrity card. Each request carries a fresh id and is answered by the reply with the same id, so any
 * number can be in flight and a reply meant for someone else is left alone.
 *
 * Android-free on purpose (the sender and the message feed are passed in), so the correlation, the timeouts
 * and the reply checks are unit-tested on the JVM.
 *
 * @param send sends one envelope to the PC; false when it could not be sent at all.
 */
class PcFileRequests(private val send: (JSONObject) -> Boolean) {
    private val rangeWaiters = ConcurrentHashMap<String, CompletableDeferred<JSONObject>>()
    private val hashWaiters = ConcurrentHashMap<String, CompletableDeferred<JSONObject>>()

    /**
     * Offers one incoming file message. Returns true when it was a reply to a request made here (and so was
     * consumed), false for anything else, which the caller handles as before.
     */
    fun onMessage(obj: JSONObject): Boolean {
        val (waiters, key) = when (obj.optString("type")) {
            "file_read_range_response" -> rangeWaiters to "fileReadRangeResponse"
            "file_hash_response" -> hashWaiters to "fileHashResponse"
            else -> return false
        }
        val body = obj.optJSONObject(key) ?: return false
        val waiter = waiters.remove(body.optString("requestId")) ?: return false
        waiter.complete(body)
        return true
    }

    /** Fails every request still waiting, e.g. when the connection to the PC closes. */
    fun failAll(reason: String) {
        for (waiters in listOf(rangeWaiters, hashWaiters)) {
            for (id in waiters.keys.toList()) waiters.remove(id)?.completeExceptionally(IOException(reason))
        }
    }

    /**
     * Reads up to [length] bytes of a PC file: from [offset], or the last [length] when [fromEnd]. Throws
     * [PcRequestFailedException] with the PC's reason, or [PcRequestTimeoutException].
     */
    suspend fun readRange(
        rootId: String,
        relativePath: String,
        offset: Long,
        length: Int,
        fromEnd: Boolean,
        timeoutMs: Long = RANGE_TIMEOUT_MS,
    ): RangeChunk {
        val payload = JSONObject().apply {
            put("rootId", rootId)
            put("relativePath", relativePath)
            put("offset", if (fromEnd) 0L else offset)
            put("length", length.coerceIn(1, FileTransferLimits.READ_RANGE_MAX_BYTES))
            put("fromEnd", fromEnd)
        }
        val body = request("file_read_range_request", "fileReadRangeRequest", payload, rangeWaiters, timeoutMs, "preview")
        body.meaningful("errorMessage")?.let { throw PcRequestFailedException(it) }
        val data = body.meaningful("dataBase64")?.let { encoded ->
            try {
                Base64.getDecoder().decode(encoded)
            } catch (e: IllegalArgumentException) {
                throw IOException("The PC sent bytes that could not be read.", e)
            }
        } ?: ByteArray(0)
        val start = body.optLong("offset", 0L)
        if (!fromEnd && start != offset) throw IOException("The PC answered for a different part of the file.")
        if (data.size > FileTransferLimits.READ_RANGE_MAX_BYTES) throw IOException("The PC sent more than was asked for.")
        return RangeChunk(start, data, body.optLong("fileSize", 0L), body.optBoolean("eof", false))
    }

    /** A [RangeReader] bound to one PC file, for the preview loaders. */
    fun reader(rootId: String, relativePath: String): RangeReader =
        RangeReader { offset, length, fromEnd -> readRange(rootId, relativePath, offset, length, fromEnd) }

    /**
     * The PC's SHA-256 of one of its files, Base64 as on the wire. The PC reads the whole file to answer, so
     * the wait is long; [HASH_TIMEOUT_MS] matches the PC's own wait for a phone's hash.
     */
    suspend fun hash(rootId: String, relativePath: String, timeoutMs: Long = HASH_TIMEOUT_MS): String {
        val payload = JSONObject().apply {
            put("rootId", rootId)
            put("relativePath", relativePath)
        }
        val body = request("file_hash_request", "fileHashRequest", payload, hashWaiters, timeoutMs, "fingerprint")
        body.meaningful("errorMessage")?.let { throw PcRequestFailedException(it) }
        return body.meaningful("sha256") ?: throw PcRequestFailedException("The PC sent no fingerprint.")
    }

    private suspend fun request(
        type: String,
        payloadKey: String,
        payload: JSONObject,
        waiters: ConcurrentHashMap<String, CompletableDeferred<JSONObject>>,
        timeoutMs: Long,
        what: String,
    ): JSONObject {
        val requestId = UUID.randomUUID().toString().replace("-", "")
        payload.put("requestId", requestId)
        val waiter = CompletableDeferred<JSONObject>()
        waiters[requestId] = waiter
        try {
            val envelope = JSONObject().apply {
                put("type", type)
                put("protocolVersion", 3)
                put(payloadKey, payload)
            }
            if (!send(envelope)) throw IOException("The request could not be sent to the PC.")
            return withTimeout(timeoutMs) { waiter.await() }
        } catch (_: TimeoutCancellationException) {
            throw PcRequestTimeoutException(what)
        } finally {
            waiters.remove(requestId)
        }
    }

    private fun JSONObject.meaningful(key: String): String? =
        if (has(key) && !isNull(key)) optString(key).takeIf { it.isNotBlank() } else null

    companion object {
        /** One range is at most 1 MiB; a PC that has not answered in this long is not going to. */
        const val RANGE_TIMEOUT_MS = 30_000L

        /** Hashing reads the whole file on the PC: up to 30 minutes, the same wait the PC gives a phone. */
        const val HASH_TIMEOUT_MS = 30L * 60 * 1000
    }
}

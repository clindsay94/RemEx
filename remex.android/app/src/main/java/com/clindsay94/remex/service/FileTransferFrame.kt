package com.clindsay94.remex.service

import org.json.JSONObject

/**
 * Kotlin mirror of the PC host's `Remex.Core.Models.FileFrameEnvelope` + `FileFrameCodec`
 * (see `remex.core/Models/FileFrameEnvelope.cs`). Frames flow on the dedicated binary
 * `/ws/files` WebSocket channel (plan §1.1).
 *
 * Wire layout of one binary WebSocket message — **byte-for-byte** identical to the host:
 *
 * ```
 * [header-length: int32 little-endian][UTF-8 JSON FileFrameEnvelope][raw payload]
 * ```
 *
 * The JSON header object mirrors the C# record's `[JsonPropertyName]` values VERBATIM:
 * `kind`, `transferId`, `offset`, `length`, `final`, `committedOffset`, `error`. A single
 * mismatched key silently breaks cross-wire decoding, so do not rename these.
 */
data class FileFrameEnvelope(
    val kind: String,
    val transferId: String,
    val offset: Long = 0L,
    val length: Int = 0,
    val final: Boolean = false,
    /** Highest contiguous byte offset the receiver has durably committed (ack frames only). */
    val committedOffset: Long? = null,
    /** Human-readable reason (error frames only). */
    val error: String? = null,
) {
    fun toJson(): JSONObject =
        JSONObject().apply {
            put("kind", kind)
            put("transferId", transferId)
            put("offset", offset)
            put("length", length)
            put("final", final)
            if (committedOffset != null) put("committedOffset", committedOffset)
            if (error != null) put("error", error)
        }

    companion object {
        fun fromJson(obj: JSONObject): FileFrameEnvelope =
            FileFrameEnvelope(
                kind = obj.optString("kind"),
                transferId = obj.optString("transferId"),
                offset = obj.optLong("offset", 0L),
                length = obj.optInt("length", 0),
                final = obj.optBoolean("final", false),
                committedOffset =
                    if (obj.has("committedOffset") && !obj.isNull("committedOffset"))
                        obj.optLong("committedOffset")
                    else null,
                error =
                    if (obj.has("error") && !obj.isNull("error")) obj.optString("error") else null,
            )
    }
}

/** Mirrors `Remex.Core.Models.FileFrameKinds` string values VERBATIM. */
object FileFrameKinds {
    const val DATA = "data"
    const val ACK = "ack"
    const val ERROR = "error"
}

/** A decoded binary frame: its JSON header plus a defensive copy of the raw payload. */
data class DecodedFileFrame(val envelope: FileFrameEnvelope, val payload: ByteArray) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is DecodedFileFrame) return false
        return envelope == other.envelope && payload.contentEquals(other.payload)
    }

    override fun hashCode(): Int = 31 * envelope.hashCode() + payload.contentHashCode()
}

/**
 * Encodes/decodes binary frames for the `/ws/files` channel. Pure buffer math + org.json —
 * mirrors `Remex.Core.Models.FileFrameCodec`. The header-length prefix is a little-endian int32,
 * matching `BinaryPrimitives.WriteInt32LittleEndian` on the host.
 */
object FileFrameCodec {
    const val HEADER_LENGTH_PREFIX_SIZE = 4

    fun wrap(envelope: FileFrameEnvelope, payload: ByteArray): ByteArray {
        val header = envelope.toJson().toString().toByteArray(Charsets.UTF_8)
        val frame = ByteArray(HEADER_LENGTH_PREFIX_SIZE + header.size + payload.size)
        writeInt32Le(frame, 0, header.size)
        header.copyInto(frame, HEADER_LENGTH_PREFIX_SIZE)
        payload.copyInto(frame, HEADER_LENGTH_PREFIX_SIZE + header.size)
        return frame
    }

    /** Overload that avoids an extra copy when the payload is only part of a larger buffer. */
    fun wrap(envelope: FileFrameEnvelope, buffer: ByteArray, offset: Int, count: Int): ByteArray {
        val header = envelope.toJson().toString().toByteArray(Charsets.UTF_8)
        val frame = ByteArray(HEADER_LENGTH_PREFIX_SIZE + header.size + count)
        writeInt32Le(frame, 0, header.size)
        header.copyInto(frame, HEADER_LENGTH_PREFIX_SIZE)
        System.arraycopy(buffer, offset, frame, HEADER_LENGTH_PREFIX_SIZE + header.size, count)
        return frame
    }

    /**
     * Reads a frame produced by [wrap]. Returns null (without throwing) for any malformed frame —
     * a truncated prefix, an out-of-range header length, or invalid header JSON — mirroring the
     * host's `TryRead` tolerance.
     */
    fun tryRead(frame: ByteArray): DecodedFileFrame? {
        if (frame.size < HEADER_LENGTH_PREFIX_SIZE) return null
        val headerLength = readInt32Le(frame, 0)
        if (headerLength < 0 || HEADER_LENGTH_PREFIX_SIZE + headerLength.toLong() > frame.size) {
            return null
        }
        val json = String(frame, HEADER_LENGTH_PREFIX_SIZE, headerLength, Charsets.UTF_8)
        val envelope =
            try {
                FileFrameEnvelope.fromJson(JSONObject(json))
            } catch (e: Exception) {
                return null
            }
        val payloadStart = HEADER_LENGTH_PREFIX_SIZE + headerLength
        val payload = frame.copyOfRange(payloadStart, frame.size)
        return DecodedFileFrame(envelope, payload)
    }

    private fun writeInt32Le(b: ByteArray, off: Int, value: Int) {
        b[off] = (value and 0xFF).toByte()
        b[off + 1] = ((value shr 8) and 0xFF).toByte()
        b[off + 2] = ((value shr 16) and 0xFF).toByte()
        b[off + 3] = ((value shr 24) and 0xFF).toByte()
    }

    private fun readInt32Le(b: ByteArray, off: Int): Int =
        (b[off].toInt() and 0xFF) or
            ((b[off + 1].toInt() and 0xFF) shl 8) or
            ((b[off + 2].toInt() and 0xFF) shl 16) or
            ((b[off + 3].toInt() and 0xFF) shl 24)
}

/**
 * Numeric protocol limits shared with the PC host's `Remex.Core.Models.FileTransferLimits`
 * (see `remex.core/Models/FileTransferMessages.cs`). Mirrored VERBATIM; keep in lock-step.
 */
object FileTransferLimits {
    const val SEARCH_MAX_RESULTS = 200
    const val THUMBNAIL_DEFAULT_MAX_DIM = 128
    const val THUMBNAIL_MAX_BYTES = 96 * 1024
    const val DATA_PAYLOAD_BYTES = 256 * 1024
    const val ACK_INTERVAL_BYTES = 4 * 1024 * 1024
    const val MAX_UNACKED_BYTES = 8 * 1024 * 1024
    const val MANIFEST_MAX_ENTRIES_PER_PAGE = 1000
    const val MANIFEST_DEFAULT_ENTRIES_PER_PAGE = 500
    const val MANIFEST_MAX_TOTAL_ENTRIES = 200_000
    const val MANIFEST_COUNT_BUDGET_ENTRIES = 200_000
}

/** Mirrors `Remex.Core.Models.FileTransferModes`. */
object FileTransferModes {
    const val DOWNLOAD = "download"
    const val UPLOAD = "upload"
    const val PUSH = "push"
}

/** Mirrors `Remex.Core.Models.FileTransferControlActions`. */
object FileTransferControlActions {
    const val PAUSE = "pause"
    const val RESUME = "resume"
    const val CANCEL = "cancel"
}

/** Mirrors `Remex.Core.Models.FileManageOperations`. */
object FileManageOperations {
    const val DELETE = "delete"
    const val RENAME = "rename"
    const val COPY = "copy"
    const val MOVE = "move"
    const val MKDIR = "mkdir"
}

/** Mirrors `Remex.Core.Models.FileConsentKinds`. */
object FileConsentKinds {
    const val FULL_BROWSE = "full_browse"
    const val INCOMING_PUSH = "incoming_push"
}

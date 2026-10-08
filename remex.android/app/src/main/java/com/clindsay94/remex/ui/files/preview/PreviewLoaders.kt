package com.clindsay94.remex.ui.files.preview

import com.clindsay94.remex.service.FileTransferLimits
import java.io.ByteArrayOutputStream
import java.io.IOException

/**
 * One range of a previewed file. [offset] is where [data] starts in the file (for a read from the end, where
 * the tail began), [fileSize] the file's size when it was read, and [eof] true when [data] reaches the end.
 */
class RangeChunk(val offset: Long, val data: ByteArray, val fileSize: Long, val eof: Boolean)

/**
 * Reads one range of the previewed file: from [offset], or the last [length] bytes when fromEnd is true.
 * A PC file goes through `PcFileRequests.readRange`, a phone file through `LocalDocuments.readRange`, and tests
 * use an in-memory file. A failure is thrown as an [IOException] carrying the reason to show.
 */
fun interface RangeReader {
    suspend fun read(offset: Long, length: Int, fromEnd: Boolean): RangeChunk
}

/** The file is bigger than a preview will fetch; the screen offers "Save to phone" instead. */
class PreviewTooLargeException(val fileSize: Long, val limit: Long) :
    IOException("The file is $fileSize bytes; previews stop at $limit.")

/** Fetches a whole image for the full-size preview, in 1 MiB reads. Twin of the PC's `ImagePreviewLoader`. */
object ImagePreviewLoader {
    /**
     * All of the file's bytes, read in order. Refuses after the first read when the file is over [maxBytes], and
     * refuses a source that stops making progress rather than looping.
     *
     * @param progress 0..1 of the bytes fetched so far.
     */
    suspend fun load(
        read: RangeReader,
        progress: ((Float) -> Unit)? = null,
        maxBytes: Long = FileTransferLimits.PREVIEW_IMAGE_MAX_BYTES,
    ): ByteArray {
        var chunk = read.read(0, FileTransferLimits.READ_RANGE_MAX_BYTES, false)
        if (chunk.fileSize > maxBytes) throw PreviewTooLargeException(chunk.fileSize, maxBytes)

        val buffer = ByteArrayOutputStream(chunk.fileSize.coerceIn(0, maxBytes).toInt())
        while (true) {
            if (chunk.offset != buffer.size().toLong()) throw IOException("The bytes arrived out of order.")
            buffer.write(chunk.data)
            if (buffer.size() > maxBytes) throw PreviewTooLargeException(buffer.size().toLong(), maxBytes)
            if (chunk.fileSize > 0) progress?.invoke((buffer.size().toFloat() / chunk.fileSize).coerceIn(0f, 1f))
            if (chunk.eof) break
            if (chunk.data.isEmpty()) throw IOException("The file stopped before its end.")
            chunk = read.read(buffer.size().toLong(), FileTransferLimits.READ_RANGE_MAX_BYTES, false)
        }
        progress?.invoke(1f)
        return buffer.toByteArray()
    }
}

/** The start of a text file, decoded for the preview. */
data class TextPreview(val text: String, val isBinary: Boolean, val truncated: Boolean, val fileSize: Long)

/** Fetches and decodes the start of a text file, up to 2 MiB. Twin of the PC's `TextPreviewLoader`. */
object TextPreviewLoader {
    /**
     * The first [maxBytes] of the file, decoded. Stops after the first read when its leading bytes look binary,
     * so a mislabelled 2 GB file costs one read, not two megabytes.
     */
    suspend fun loadHead(read: RangeReader, maxBytes: Int = FileTransferLimits.PREVIEW_TEXT_MAX_BYTES): TextPreview {
        val bytes = ByteArrayOutputStream()
        var offset = 0L
        var chunk: RangeChunk
        do {
            val want = minOf(FileTransferLimits.READ_RANGE_MAX_BYTES, maxBytes - bytes.size())
            chunk = read.read(offset, want, false)
            if (offset == 0L && TextPreviewDecoder.looksBinary(chunk.data)) {
                return TextPreview("", isBinary = true, truncated = false, fileSize = chunk.fileSize)
            }
            bytes.write(chunk.data)
            offset += chunk.data.size
            if (chunk.data.isEmpty()) break
        } while (!chunk.eof && bytes.size() < maxBytes)

        val truncated = !chunk.eof
        val text = TextPreviewDecoder.decode(bytes.toByteArray(), startsMidFile = false, endsMidFile = truncated)
        return TextPreview(text, isBinary = false, truncated = truncated, fileSize = chunk.fileSize)
    }
}

/**
 * What one live-tail step changed.
 *
 * @property reset the file shrank or was replaced: throw away what is shown and use [lines].
 * @property lines complete lines to append (or, on a reset, the whole visible tail).
 * @property pendingLine the last line so far, still being written (no newline yet); replaces the previous one.
 */
data class TailUpdate(val reset: Boolean, val lines: List<String>, val pendingLine: String, val fileSize: Long)

/**
 * Live tail for a growing text file. Twin of the PC's `TextTail`: starts at the last 256 KiB, then on every poll
 * reads only what was added since. A file that shrinks restarts from its new tail; a line still being written is
 * pending until its newline arrives; a character split across two polls is never shown as U+FFFD; and a file
 * that never writes a newline keeps only the last [tailBytes] of its unfinished line.
 */
class TextTail(
    private val read: RangeReader,
    private val tailBytes: Int = FileTransferLimits.PREVIEW_TEXT_TAIL_BYTES,
) {
    private var nextOffset = 0L
    private var carry = ByteArray(0)
    private var started = false

    /** Reads the tail and returns it as a reset. Safe to call again to start over. */
    suspend fun start(): TailUpdate {
        val chunk = read.read(0, tailBytes, true)
        var data = chunk.data
        started = true
        carry = ByteArray(0)
        nextOffset = chunk.offset + data.size
        // A tail that starts mid-file drops its first, partial line.
        if (chunk.offset > 0) {
            val newline = TextPreviewDecoder.indexOf(data, '\n'.code.toByte(), 0, data.size)
            data = if (newline >= 0) data.copyOfRange(newline + 1, data.size) else ByteArray(0)
        }
        val (lines, pending) = split(data)
        return TailUpdate(reset = true, lines = lines, pendingLine = pending, fileSize = chunk.fileSize)
    }

    /** Reads what was added since the last call (or restarts when the file shrank). */
    suspend fun poll(): TailUpdate {
        if (!started) return start()
        val chunk = read.read(nextOffset, FileTransferLimits.READ_RANGE_MAX_BYTES, false)
        if (chunk.fileSize < nextOffset) return start()
        nextOffset += chunk.data.size
        val (lines, pending) = split(carry + chunk.data)
        return TailUpdate(reset = false, lines = lines, pendingLine = pending, fileSize = chunk.fileSize)
    }

    private fun split(bytes: ByteArray): Pair<List<String>, String> {
        var lastNewline = -1
        for (i in bytes.indices.reversed()) if (bytes[i] == '\n'.code.toByte()) { lastNewline = i; break }
        val complete = if (lastNewline >= 0) bytes.copyOfRange(0, lastNewline + 1) else ByteArray(0)
        carry = if (lastNewline >= 0) bytes.copyOfRange(lastNewline + 1, bytes.size) else bytes
        if (carry.size > tailBytes) {
            var start = carry.size - tailBytes
            while (start < carry.size && (carry[start].toInt() and 0b1100_0000) == 0b1000_0000) start++
            carry = carry.copyOfRange(start, carry.size)
        }

        val lines = if (complete.isEmpty()) {
            emptyList()
        } else {
            TextPreviewDecoder.utf8(complete, 0, complete.size).split('\n').dropLast(1).map { it.trimEnd('\r') }
        }
        val pending = TextPreviewDecoder.utf8(carry, 0, TextPreviewDecoder.completeUtf8Length(carry)).trimEnd('\r')
        return lines to pending
    }
}

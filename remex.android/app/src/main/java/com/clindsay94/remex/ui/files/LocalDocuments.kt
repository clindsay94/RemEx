package com.clindsay94.remex.ui.files

import android.content.ContentResolver
import android.net.Uri
import android.provider.DocumentsContract
import android.provider.DocumentsContract.Document
import com.clindsay94.remex.service.SharedPathPolicy
import com.clindsay94.remex.ui.files.preview.RangeChunk
import com.clindsay94.remex.ui.files.preview.RangeReader
import java.io.FileInputStream
import java.io.FileNotFoundException
import java.io.IOException
import java.io.InputStream
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext

/** One item in a phone folder, as `DocumentsContract` lists it. [sizeBytes] is -1 when the provider doesn't say. */
data class LocalEntry(
    val documentId: String,
    val name: String,
    val isDirectory: Boolean,
    val sizeBytes: Long,
    val modifiedMs: Long,
    val mimeType: String?,
)

/**
 * Pure helpers behind [LocalDocuments], unit-tested on the JVM: how a row becomes an entry, how a path is walked
 * to a document, and how a range or a hash is read from a stream.
 */
object LocalDocumentLogic {
    /** One child-document row as an entry; null for a row with no id or name, which can't be shown or opened. */
    fun entryOf(documentId: String?, name: String?, mimeType: String?, size: Long?, modified: Long?): LocalEntry? {
        if (documentId.isNullOrEmpty() || name.isNullOrEmpty()) return null
        val isDirectory = mimeType == Document.MIME_TYPE_DIR
        return LocalEntry(
            documentId = documentId,
            name = name,
            isDirectory = isDirectory,
            sizeBytes = if (isDirectory) -1L else size ?: -1L,
            modifiedMs = modified ?: 0L,
            mimeType = mimeType,
        )
    }

    /**
     * The document id at [relativePath] under the tree whose top document is [rootDocumentId], walking one
     * listing per level with [childrenOf]. Null when a name is unsafe ([SharedPathPolicy.segments]), missing, or
     * a file sits where a folder is needed. [cache] remembers paths already walked, so opening a folder costs
     * one listing, not one per level.
     */
    suspend fun resolve(
        rootDocumentId: String,
        relativePath: String,
        childrenOf: suspend (String) -> List<LocalEntry>,
        cache: MutableMap<String, String>? = null,
    ): String? {
        val segments = SharedPathPolicy.segments(relativePath) ?: return null
        var documentId = rootDocumentId
        var walked = ""
        for ((index, name) in segments.withIndex()) {
            walked = if (walked.isEmpty()) name else "$walked/$name"
            val known = cache?.get(walked)
            if (known != null) {
                documentId = known
                continue
            }
            val child = childrenOf(documentId).firstOrNull { it.name == name } ?: return null
            if (!child.isDirectory && index < segments.lastIndex) return null
            documentId = child.documentId
            cache?.put(walked, documentId)
        }
        return documentId
    }

    /**
     * Reads one range from [input], positioned at its start: up to [length] bytes from [offset], or the last
     * [length] when [fromEnd]. [size] is the file's size, or -1 when the provider doesn't know it; a tail of a
     * file of unknown size is then read to the end through a ring buffer, the way the phone's host does it.
     */
    suspend fun readRange(input: InputStream, size: Long, offset: Long, length: Int, fromEnd: Boolean): RangeChunk {
        require(length > 0) { "length must be positive" }
        if (fromEnd && size < 0) return tailOfUnknownSize(input, length)

        val start = when {
            fromEnd -> maxOf(0L, size - length)
            size >= 0 -> minOf(offset, size)
            else -> offset
        }
        skipTo(input, start)
        val buffer = ByteArray(if (size >= 0) minOf(length.toLong(), size - start).toInt() else length)
        var filled = 0
        while (filled < buffer.size) {
            currentCoroutineContext().ensureActive()
            val n = input.read(buffer, filled, buffer.size - filled)
            if (n < 0) break
            filled += n
        }
        val data = if (filled == buffer.size) buffer else buffer.copyOf(filled)
        val end = start + filled
        val eof = if (size >= 0) end >= size else filled < length || input.read() < 0
        // With no size from the provider, what has been seen is the best size there is: never less than the end
        // of this read, so a live tail never mistakes a long unknown-size file for one that shrank.
        return RangeChunk(start, data, if (size >= 0) size else end, eof)
    }

    /** The SHA-256 of everything [input] holds, reporting bytes read so far to [onProgress]. */
    suspend fun sha256(input: InputStream, onProgress: ((Long) -> Unit)? = null): ByteArray {
        val digest = MessageDigest.getInstance("SHA-256")
        val buffer = ByteArray(HASH_BUFFER_BYTES)
        var total = 0L
        while (true) {
            currentCoroutineContext().ensureActive()
            val n = input.read(buffer)
            if (n < 0) break
            digest.update(buffer, 0, n)
            total += n
            onProgress?.invoke(total)
        }
        return digest.digest()
    }

    private suspend fun skipTo(input: InputStream, start: Long) {
        if (start <= 0) return
        if (input is FileInputStream) {
            input.channel.position(start)
            return
        }
        var remaining = start
        val scratch = ByteArray(HASH_BUFFER_BYTES)
        while (remaining > 0) {
            currentCoroutineContext().ensureActive()
            val skipped = input.skip(remaining)
            if (skipped > 0) {
                remaining -= skipped
                continue
            }
            // skip() may return 0 without being at the end; a read tells the two apart.
            val n = input.read(scratch, 0, minOf(scratch.size.toLong(), remaining).toInt())
            if (n < 0) return
            remaining -= n
        }
    }

    private suspend fun tailOfUnknownSize(input: InputStream, length: Int): RangeChunk {
        val ring = ByteArray(length)
        var total = 0L
        val scratch = ByteArray(HASH_BUFFER_BYTES)
        while (true) {
            currentCoroutineContext().ensureActive()
            val n = input.read(scratch)
            if (n < 0) break
            for (i in 0 until n) ring[((total + i) % length).toInt()] = scratch[i]
            total += n
        }
        val count = minOf(total, length.toLong()).toInt()
        val start = total - count
        val data = ByteArray(count) { ring[((start + it) % length).toInt()] }
        return RangeChunk(start, data, total, eof = true)
    }

    private const val HASH_BUFFER_BYTES = 64 * 1024
}

/**
 * Lists, reads and hashes files in a phone folder the person granted (a shared folder or a phone-only bookmark),
 * by tree URI and '/'-separated path, the same addressing the PC side uses. Folders are listed with one
 * `DocumentsContract` child query each, not `DocumentFile.listFiles()`, which costs one IPC per child.
 */
class LocalDocuments(private val resolver: ContentResolver) {
    // tree URI -> (relative path -> document id). Cleared per tree when a listing shows a name is gone.
    private val ids = ConcurrentHashMap<String, MutableMap<String, String>>()

    /** The items in [relativePath] under [treeUri]. Throws [FileNotFoundException] when the folder is gone. */
    suspend fun list(treeUri: Uri, relativePath: String): List<LocalEntry> = withContext(Dispatchers.IO) {
        val entries = listChildren(treeUri, documentIdOf(treeUri, relativePath))
        // Remember every child's id, so opening, previewing or thumbnailing one costs no second listing.
        val cache = ids.getOrPut(treeUri.toString()) { ConcurrentHashMap() }
        val folder = relativePath.trim('/')
        for (entry in entries) cache[if (folder.isEmpty()) entry.name else "$folder/${entry.name}"] = entry.documentId
        entries
    }

    /** A content URI for the document at [relativePath], for opening, sharing or as a transfer's source. */
    suspend fun documentUri(treeUri: Uri, relativePath: String): Uri = withContext(Dispatchers.IO) {
        DocumentsContract.buildDocumentUriUsingTree(treeUri, documentIdOf(treeUri, relativePath))
    }

    /** One range of the file at [relativePath]. */
    suspend fun readRange(treeUri: Uri, relativePath: String, offset: Long, length: Int, fromEnd: Boolean): RangeChunk =
        withContext(Dispatchers.IO) {
            val uri = DocumentsContract.buildDocumentUriUsingTree(treeUri, documentIdOf(treeUri, relativePath))
            val size = sizeOf(uri)
            val input = resolver.openInputStream(uri) ?: throw FileNotFoundException(relativePath)
            input.use { LocalDocumentLogic.readRange(it, size, offset, length, fromEnd) }
        }

    /** A [RangeReader] bound to one phone file, for the preview loaders. */
    fun reader(treeUri: Uri, relativePath: String): RangeReader =
        RangeReader { offset, length, fromEnd -> readRange(treeUri, relativePath, offset, length, fromEnd) }

    /** The SHA-256 of the file at [relativePath], computed here on the phone. */
    suspend fun sha256(treeUri: Uri, relativePath: String, onProgress: ((Long) -> Unit)? = null): ByteArray =
        withContext(Dispatchers.IO) {
            val uri = DocumentsContract.buildDocumentUriUsingTree(treeUri, documentIdOf(treeUri, relativePath))
            val input = resolver.openInputStream(uri) ?: throw FileNotFoundException(relativePath)
            input.use { LocalDocumentLogic.sha256(it, onProgress) }
        }

    /** Forgets the paths walked under [treeUri], after the person changed files there. */
    fun invalidate(treeUri: Uri) {
        ids.remove(treeUri.toString())
    }

    private suspend fun documentIdOf(treeUri: Uri, relativePath: String): String {
        val root = DocumentsContract.getTreeDocumentId(treeUri)
        val cache = ids.getOrPut(treeUri.toString()) { ConcurrentHashMap() }
        return LocalDocumentLogic.resolve(root, relativePath, { listChildren(treeUri, it) }, cache)
            ?: run {
                // A stale cached id (moved or deleted) would keep failing; walk fresh next time.
                ids.remove(treeUri.toString())
                throw FileNotFoundException(relativePath)
            }
    }

    private fun listChildren(treeUri: Uri, parentDocumentId: String): List<LocalEntry> {
        val childrenUri = DocumentsContract.buildChildDocumentsUriUsingTree(treeUri, parentDocumentId)
        val cursor = resolver.query(childrenUri, PROJECTION, null, null, null)
            ?: throw IOException("This folder could not be listed.")
        return cursor.use { c ->
            buildList {
                while (c.moveToNext()) {
                    LocalDocumentLogic.entryOf(
                        documentId = c.getString(0),
                        name = c.getString(1),
                        mimeType = c.getString(2),
                        size = if (c.isNull(3)) null else c.getLong(3),
                        modified = if (c.isNull(4)) null else c.getLong(4),
                    )?.let(::add)
                }
            }
        }
    }

    private fun sizeOf(documentUri: Uri): Long =
        resolver.query(documentUri, arrayOf(Document.COLUMN_SIZE), null, null, null)?.use { c ->
            if (c.moveToFirst() && !c.isNull(0)) c.getLong(0) else -1L
        } ?: -1L

    private companion object {
        val PROJECTION = arrayOf(
            Document.COLUMN_DOCUMENT_ID,
            Document.COLUMN_DISPLAY_NAME,
            Document.COLUMN_MIME_TYPE,
            Document.COLUMN_SIZE,
            Document.COLUMN_LAST_MODIFIED,
        )
    }
}

/**
 * What the screen needs from phone folders, addressed by tree URI string and '/'-path. [LocalDocuments] is the
 * app's; tests use a map. Keeps `android.net.Uri` (a stub on the JVM) out of the state holders.
 */
interface PhoneFileSource {
    suspend fun list(rootUri: String, path: String): List<LocalEntry>
    fun reader(rootUri: String, path: String): RangeReader
    suspend fun sha256(rootUri: String, path: String, onProgress: ((Long) -> Unit)? = null): ByteArray
    suspend fun documentUri(rootUri: String, path: String): String
}

/** [PhoneFileSource] over [LocalDocuments]. */
class LocalPhoneFileSource(private val docs: LocalDocuments) : PhoneFileSource {
    override suspend fun list(rootUri: String, path: String) = docs.list(Uri.parse(rootUri), path)
    override fun reader(rootUri: String, path: String) = docs.reader(Uri.parse(rootUri), path)
    override suspend fun sha256(rootUri: String, path: String, onProgress: ((Long) -> Unit)?) =
        docs.sha256(Uri.parse(rootUri), path, onProgress)
    override suspend fun documentUri(rootUri: String, path: String) = docs.documentUri(Uri.parse(rootUri), path).toString()
}

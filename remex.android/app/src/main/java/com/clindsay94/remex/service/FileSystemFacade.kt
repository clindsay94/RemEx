package com.clindsay94.remex.service

import android.content.Context
import android.graphics.Bitmap
import android.net.Uri
import android.util.Size
import androidx.documentfile.provider.DocumentFile
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.io.OutputStream

/**
 * Thin abstraction over a single file/directory node so [FileHostHandler] can be unit-tested against
 * fakes instead of a live SAF `DocumentFile` (plan WP6). The production implementation
 * ([DocumentFileNode]) wraps `androidx.documentfile.provider.DocumentFile`; tests supply an in-memory
 * fake. Kept intentionally minimal — only the operations the host mirror needs.
 */
interface FileNode {
    val name: String
    val isDirectory: Boolean
    val length: Long
    val lastModifiedMs: Long
    val canRead: Boolean
    val canWrite: Boolean
    val mimeType: String?

    /**
     * A `content://` URI another app could be handed, or null when this node has none (RemEx-pwkc).
     *
     * Needed so a file that arrives from the PC can offer Open and Share: both take a URI, and the
     * rest of this interface deliberately hides where a node lives. Null on any implementation that
     * is not SAF-backed, and callers must treat that as "no actions", not as an error.
     */
    val contentUri: String?

    fun listChildren(): List<FileNode>
    fun findChild(name: String): FileNode?
    fun createDirectory(name: String): FileNode?
    fun createFile(mimeType: String, name: String): FileNode?
    fun delete(): Boolean
    fun renameTo(name: String): Boolean
}

/** A shared root exposed by the served device. */
data class RootDescriptor(
    val rootId: String,
    val displayName: String,
    val isWritable: Boolean,
    val canRename: Boolean,
    val canMove: Boolean,
    val canDelete: Boolean,
    val canRemoveRoot: Boolean,
)

/** A mounted volume exposed once full-browse consent is granted (mirrors `FileVolumeInfo`). */
data class VolumeDescriptor(
    val id: String,
    val label: String,
    val path: String,
    val totalBytes: Long,
    val freeBytes: Long,
    val kind: String,
)

/**
 * The set of filesystem operations the host mirror needs, decoupled from Android SAF. Path resolution
 * ([resolve]) walks a root's relative path; content streams and thumbnails are produced here so the
 * handler stays free of `ContentResolver`.
 */
interface FileSystemFacade {
    fun listRoots(): List<RootDescriptor>
    fun listVolumes(): List<VolumeDescriptor>

    /** Resolves a node under [rootId] by its '/'-separated [relativePath] (empty = the root itself). */
    fun resolve(rootId: String, relativePath: String): FileNode?

    fun openInput(node: FileNode): InputStream?
    fun openOutput(node: FileNode): OutputStream?

    /** Loads a JPEG thumbnail (images/videos), ≤ [maxBytes] encoded, or null when unavailable. */
    fun loadThumbnailJpeg(node: FileNode, maxDim: Int, maxBytes: Int): ByteArray?
}

/** Production [FileNode] backed by a SAF [DocumentFile]. */
class DocumentFileNode(
    private val context: Context,
    val doc: DocumentFile,
) : FileNode {
    override val name: String get() = doc.name ?: "Unnamed"
    override val isDirectory: Boolean get() = doc.isDirectory
    override val length: Long get() = if (doc.isDirectory) 0L else doc.length()
    override val lastModifiedMs: Long get() = doc.lastModified()
    override val canRead: Boolean get() = doc.canRead()
    override val canWrite: Boolean get() = doc.canWrite()
    override val mimeType: String? get() = doc.type
    override val contentUri: String get() = doc.uri.toString()

    override fun listChildren(): List<FileNode> =
        doc.listFiles().map { DocumentFileNode(context, it) }

    override fun findChild(name: String): FileNode? =
        doc.findFile(name)?.let { DocumentFileNode(context, it) }

    override fun createDirectory(name: String): FileNode? =
        doc.createDirectory(name)?.let { DocumentFileNode(context, it) }

    override fun createFile(mimeType: String, name: String): FileNode? =
        doc.createFile(mimeType, name)?.let { DocumentFileNode(context, it) }

    override fun delete(): Boolean = doc.delete()

    override fun renameTo(name: String): Boolean = doc.renameTo(name)
}

/**
 * Production [FileSystemFacade] over SAF tree URIs. Roots come from [SharedRootsProvider] (the
 * user-designated shared folder URIs); [openInput]/[openOutput]/[loadThumbnailJpeg] go through the
 * app's `ContentResolver`.
 */
class SafFileSystemFacade(
    private val context: Context,
    private val rootsProvider: SharedRootsProvider,
) : FileSystemFacade {

    override fun listRoots(): List<RootDescriptor> = rootsProvider.sharedRoots()

    override fun listVolumes(): List<VolumeDescriptor> = rootsProvider.fullBrowseVolumes()

    override fun resolve(rootId: String, relativePath: String): FileNode? {
        val rootUri = Uri.parse(rootId)
        var current = DocumentFile.fromTreeUri(context, rootUri) ?: return null
        if (!current.canRead()) return null
        val trimmed = relativePath.trim('/')
        if (trimmed.isNotEmpty()) {
            for (part in trimmed.split('/')) {
                if (part.isEmpty()) continue
                current = current.findFile(part) ?: return null
            }
        }
        return DocumentFileNode(context, current)
    }

    override fun openInput(node: FileNode): InputStream? {
        val doc = (node as? DocumentFileNode)?.doc ?: return null
        return context.contentResolver.openInputStream(doc.uri)
    }

    override fun openOutput(node: FileNode): OutputStream? {
        val doc = (node as? DocumentFileNode)?.doc ?: return null
        return context.contentResolver.openOutputStream(doc.uri, "w")
    }

    override fun loadThumbnailJpeg(node: FileNode, maxDim: Int, maxBytes: Int): ByteArray? {
        val doc = (node as? DocumentFileNode)?.doc ?: return null
        val type = doc.type ?: return null
        if (!(type.startsWith("image/") || type.startsWith("video/"))) return null
        val dim = maxDim.coerceIn(16, 512)
        val bmp: Bitmap? =
            try {
                context.contentResolver.loadThumbnail(doc.uri, Size(dim, dim), null)
            } catch (e: Exception) {
                null
            }
        bmp ?: return null
        // Compress to JPEG, stepping quality down until it fits the wire cap.
        var quality = 80
        while (quality >= 30) {
            val out = ByteArrayOutputStream()
            bmp.compress(Bitmap.CompressFormat.JPEG, quality, out)
            val bytes = out.toByteArray()
            if (bytes.size <= maxBytes) {
                bmp.recycle()
                return bytes
            }
            quality -= 15
        }
        bmp.recycle()
        return null
    }
}

/**
 * Supplies the served device's shared roots + full-browse volumes. Kept as a separate seam so the
 * host mirror does not depend on `SettingsManager`/DataStore directly (and so tests can supply a
 * fixed root set). The production implementation reads the user's shared-folder URIs + full-browse
 * grant from `SettingsManager`.
 */
interface SharedRootsProvider {
    fun sharedRoots(): List<RootDescriptor>
    fun fullBrowseVolumes(): List<VolumeDescriptor>
    fun isFullBrowseGranted(): Boolean
}

package com.clindsay94.remex.service

import java.io.File
import java.net.URI

/**
 * The local files a queued transfer owns, and the sweep that deletes the ones nothing owns any more
 * (live-check C6: ~880 MB of app data on Connor's phone).
 *
 * - Download partials: `filesDir/transfers/outgoing/<id>.part` ([FileTransferEngine]). Owned only
 *   while the row may still run (Queued, Negotiating, Active, Verifying). A download never resumes -
 *   the host always serves it from offset 0 (download resume needs a protocol change) - so a Paused or
 *   Failed row's partial is dead weight. The run deletes its partial however it ends; [sweep] catches
 *   one a killed process left behind.
 * - Share-to-PC staging copies: `filesDir/share_staging/<uuid>/<name>` (ShareToPcViewModel). The push
 *   reads its source from there. Deleted once the push lands or is cancelled, when the row is cleared,
 *   and by [sweep].
 *
 * `filesDir/transfers/incoming` (the phone-as-host receive staging, [FileHostHandler]) is NOT handled
 * here: the PC owns that transfer's queue row, so this side cannot tell a resumable partial from an
 * orphan. [FileHostHandler.cleanupOrphans] ages those out instead, and deletes them on a PC cancel.
 *
 * Pure JVM ([File] only) so it is unit-testable.
 */
internal object TransferLocalFiles {
    const val DOWNLOAD_PARTIAL_DIR = "transfers/outgoing"
    const val SHARE_STAGING_DIR = "share_staging"
    private const val PARTIAL_SUFFIX = ".part"

    /**
     * Files younger than this are never swept. ShareToPcViewModel stages a copy BEFORE it enqueues
     * the push, and the sweep reads a queue snapshot before it lists the directories; a young file
     * may belong to a row the snapshot has not seen yet.
     */
    const val SWEEP_GRACE_MS = 10L * 60 * 1000

    fun safeStem(id: String): String =
        buildString { for (c in id) append(if (c.isLetterOrDigit() || c == '-' || c == '_') c else '_') }

    fun partialFileName(transferId: String): String = safeStem(transferId) + PARTIAL_SUFFIX

    /**
     * Whether a row still owns its partial or staged source. An upload/push owns its staged source
     * while it can still be resumed or retried; a download owns its partial only while it may run,
     * because a download is never resumed from a partial (see the class doc).
     */
    fun ownsLocalData(t: QueuedTransfer): Boolean =
        if (t.mode == FileTransferModes.DOWNLOAD) {
            t.state in DownloadOwnerStates
        } else {
            t.state != TransferState.Done && t.state != TransferState.Cancelled
        }

    private val DownloadOwnerStates =
        setOf(TransferState.Queued, TransferState.Negotiating, TransferState.Active, TransferState.Verifying)

    /**
     * The per-share directory (`<stagingRoot>/<uuid>`) that [localUri] lives in, or null when it is
     * not a file under [stagingRoot] (a content:// source the user picked is never ours to delete).
     */
    fun stagingDirOf(localUri: String, stagingRoot: File): File? {
        if (!localUri.startsWith("file:")) return null
        val file = runCatching { File(URI(localUri)).canonicalFile }.getOrNull() ?: return null
        val root = runCatching { stagingRoot.canonicalFile }.getOrNull() ?: return null
        var dir: File? = file
        while (dir != null) {
            val parent = dir.parentFile ?: return null
            if (parent == root) return dir
            dir = parent
        }
        return null
    }

    /** Deletes [t]'s partial or staged source. Idempotent. */
    fun discard(t: QueuedTransfer, partialDir: File, stagingRoot: File) {
        if (t.mode == FileTransferModes.DOWNLOAD) {
            File(partialDir, partialFileName(t.id)).delete()
        } else {
            stagingDirOf(t.localUri, stagingRoot)?.deleteRecursively()
        }
    }

    /**
     * Deletes every download partial and staging directory that no row in [queue] owns, other than
     * ones modified within [graceMs] of [nowMs]. Returns what it deleted.
     */
    fun sweep(
        partialDir: File,
        stagingRoot: File,
        queue: List<QueuedTransfer>,
        nowMs: Long,
        graceMs: Long = SWEEP_GRACE_MS,
    ): List<File> {
        val owners = queue.filter { ownsLocalData(it) }
        val keepPartials =
            owners.filter { it.mode == FileTransferModes.DOWNLOAD }.mapTo(HashSet()) { partialFileName(it.id) }
        val keepStaging =
            owners.filter { it.mode != FileTransferModes.DOWNLOAD }
                .mapNotNullTo(HashSet()) { stagingDirOf(it.localUri, stagingRoot)?.name }
        val cutoff = nowMs - graceMs
        val deleted = mutableListOf<File>()

        partialDir.listFiles()?.forEach { f ->
            if (f.isFile && f.name.endsWith(PARTIAL_SUFFIX) && f.name !in keepPartials && f.lastModified() < cutoff) {
                if (f.delete()) deleted += f
            }
        }
        stagingRoot.listFiles()?.forEach { dir ->
            if (dir.name !in keepStaging && newestModified(dir) < cutoff) {
                if (dir.deleteRecursively()) deleted += dir
            }
        }
        return deleted
    }

    /** A staging directory's age is its newest entry's: the copy inside may still be being written. */
    private fun newestModified(f: File): Long {
        var newest = f.lastModified()
        if (f.isDirectory) f.listFiles()?.forEach { newest = maxOf(newest, newestModified(it)) }
        return newest
    }
}

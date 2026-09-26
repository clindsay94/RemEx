package com.clindsay94.remex.service

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

/**
 * Live-check C6: RemEx grew to ~880 MB of app data because download partials in
 * `filesDir/transfers/outgoing` and staged share copies in `filesDir/share_staging` were never
 * deleted on cancel, never after a push landed, and never swept. [TransferLocalFiles.sweep] is the
 * startup safety net; these tests pin what it may and may not delete.
 */
class TransferLocalFilesTest {

    @get:Rule val tmp = TemporaryFolder()

    private val now = 1_000_000_000_000L
    private val old = now - TransferLocalFiles.SWEEP_GRACE_MS - 1
    private val fresh = now - 1_000L

    private fun partialDir() = File(tmp.root, "transfers/outgoing").apply { mkdirs() }

    private fun stagingRoot() = File(tmp.root, "share_staging").apply { mkdirs() }

    private fun partial(id: String, modified: Long = old): File =
        File(partialDir(), TransferLocalFiles.partialFileName(id)).apply {
            writeBytes(ByteArray(16))
            setLastModified(modified)
        }

    private fun staged(dirName: String, fileName: String, modified: Long = old): File {
        val dir = File(stagingRoot(), dirName).apply { mkdirs() }
        val f = File(dir, fileName).apply { writeBytes(ByteArray(16)) }
        f.setLastModified(modified)
        dir.setLastModified(modified)
        return f
    }

    private fun download(id: String, state: TransferState) =
        QueuedTransfer(id = id, mode = FileTransferModes.DOWNLOAD, fileName = "$id.bin", size = 100, localUri = "content://x/$id", state = state)

    private fun push(id: String, file: File, state: TransferState) =
        QueuedTransfer(id = id, mode = FileTransferModes.PUSH, fileName = file.name, size = 16, localUri = file.toURI().toString(), state = state)

    @Test
    fun sweep_deletesPartialWithNoQueueItem() {
        val orphan = partial("gone")
        TransferLocalFiles.sweep(partialDir(), stagingRoot(), emptyList(), now)
        assertFalse(orphan.exists())
    }

    @Test
    fun sweep_keepsPartialsOfDownloadsThatMayStillRun() {
        val queued = partial("q")
        val active = partial("a")
        val verifying = partial("v")
        val queue = listOf(
            download("q", TransferState.Queued),
            download("a", TransferState.Active),
            download("v", TransferState.Verifying),
        )
        TransferLocalFiles.sweep(partialDir(), stagingRoot(), queue, now)
        assertTrue(queued.exists())
        assertTrue(active.exists())
        assertTrue(verifying.exists())
    }

    @Test
    fun sweep_deletesPartialsOfPausedAndFailedDownloads() {
        // The host always serves a download from offset 0 (the offer carries no receiver offset), so
        // a paused or failed download restarts from zero and its partial can never be used.
        val paused = partial("p")
        val failed = partial("f")
        val queue = listOf(download("p", TransferState.Paused), download("f", TransferState.Failed))
        TransferLocalFiles.sweep(partialDir(), stagingRoot(), queue, now)
        assertFalse(paused.exists())
        assertFalse(failed.exists())
    }

    @Test
    fun sweep_deletesPartialsOfCancelledAndDoneItems() {
        val cancelled = partial("c")
        val done = partial("d")
        val queue = listOf(download("c", TransferState.Cancelled), download("d", TransferState.Done))
        TransferLocalFiles.sweep(partialDir(), stagingRoot(), queue, now)
        assertFalse(cancelled.exists())
        assertFalse(done.exists())
    }

    @Test
    fun sweep_leavesFreshOrphansAlone() {
        // A file this new may belong to an item enqueued after the queue snapshot was taken.
        val young = partial("young", modified = fresh)
        TransferLocalFiles.sweep(partialDir(), stagingRoot(), emptyList(), now)
        assertTrue(young.exists())
    }

    @Test
    fun sweep_stagingDirKeptWhilePushPending_deletedOnceDoneOrUnreferenced() {
        val pending = staged("s1", "a b.jpg")
        val done = staged("s2", "c.jpg")
        val unreferenced = staged("s3", "d.jpg")
        val queue = listOf(push("u1", pending, TransferState.Queued), push("u2", done, TransferState.Done))

        val deleted = TransferLocalFiles.sweep(partialDir(), stagingRoot(), queue, now)

        assertTrue(pending.exists())
        assertFalse(done.parentFile!!.exists())
        assertFalse(unreferenced.parentFile!!.exists())
        assertEquals(2, deleted.size)
    }

    @Test
    fun sweep_keepsStagedSourceOfPausedAndFailedPushes() {
        // Unlike a download, an upload/push does resume (the host reports its durable offset).
        val paused = staged("s6", "p.jpg")
        val failed = staged("s7", "f.jpg")
        val queue = listOf(push("u6", paused, TransferState.Paused), push("u7", failed, TransferState.Failed))
        TransferLocalFiles.sweep(partialDir(), stagingRoot(), queue, now)
        assertTrue(paused.exists())
        assertTrue(failed.exists())
    }

    @Test
    fun sweep_freshUnreferencedStagingDirIsKept() {
        // ShareToPcViewModel stages BEFORE it enqueues; the sweep must not delete in that window.
        val staging = staged("s4", "e.jpg", modified = fresh)
        TransferLocalFiles.sweep(partialDir(), stagingRoot(), emptyList(), now)
        assertTrue(staging.exists())
    }

    @Test
    fun stagingDirOf_resolvesEncodedFileUriToItsShareDirectory() {
        val f = staged("s5", "with space #1.png")
        val dir = TransferLocalFiles.stagingDirOf(f.toURI().toString(), stagingRoot())
        assertEquals(f.parentFile!!.canonicalPath, dir!!.canonicalPath)
    }

    @Test
    fun stagingDirOf_ignoresContentUrisAndFilesOutsideTheRoot() {
        assertNull(TransferLocalFiles.stagingDirOf("content://media/external/1", stagingRoot()))
        val outside = tmp.newFile("elsewhere.txt")
        assertNull(TransferLocalFiles.stagingDirOf(outside.toURI().toString(), stagingRoot()))
        // The staging root itself is never a per-share directory.
        assertNull(TransferLocalFiles.stagingDirOf(stagingRoot().toURI().toString(), stagingRoot()))
    }

    @Test
    fun sweep_toleratesMissingDirectories() {
        val deleted = TransferLocalFiles.sweep(File(tmp.root, "nope"), File(tmp.root, "nope2"), emptyList(), now)
        assertTrue(deleted.isEmpty())
    }
}

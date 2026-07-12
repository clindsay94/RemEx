package com.clindsay94.remex.service

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

/** Verifies [TransferQueueStore] persistence + [TransferResumeLogic] arithmetic (plan §1.3/§1.4). */
class TransferQueueTest {

    @get:Rule val tmp = TemporaryFolder()

    private fun store() = TransferQueueStore(tmp.root)

    @Test
    fun upsert_thenReload_survivesNewStoreInstance() {
        val a =
            QueuedTransfer(
                id = "a",
                mode = FileTransferModes.UPLOAD,
                fileName = "photo.jpg",
                size = 1234,
                localUri = "content://x/photo.jpg",
                destRoot = "root1",
                destRelativePath = "sub",
            )
        store().upsert(a)

        // A brand-new store instance must read the persisted file (survives process death).
        val reloaded = store().load()
        assertEquals(1, reloaded.size)
        assertEquals("a", reloaded[0].id)
        assertEquals(FileTransferModes.UPLOAD, reloaded[0].mode)
        assertEquals("photo.jpg", reloaded[0].fileName)
        assertEquals(1234, reloaded[0].size)
        assertEquals("root1", reloaded[0].destRoot)
    }

    @Test
    fun upsert_replacesById_preservingOrder() {
        val s = store()
        s.upsert(QueuedTransfer("a", FileTransferModes.UPLOAD, "a.bin", 1, "u1"))
        s.upsert(QueuedTransfer("b", FileTransferModes.DOWNLOAD, "b.bin", 2, "u2"))
        val updated =
            s.upsert(
                QueuedTransfer(
                    "a",
                    FileTransferModes.UPLOAD,
                    "a.bin",
                    1,
                    "u1",
                    state = TransferState.Active,
                    bytesTransferred = 512,
                )
            )
        assertEquals(2, updated.size)
        assertEquals("a", updated[0].id) // order preserved
        assertEquals(TransferState.Active, updated[0].state)
        assertEquals(512, updated[0].bytesTransferred)
    }

    @Test
    fun pruneFinished_dropsDoneAndCancelled() {
        val s = store()
        s.upsert(QueuedTransfer("a", FileTransferModes.UPLOAD, "a", 1, "u", state = TransferState.Done))
        s.upsert(QueuedTransfer("b", FileTransferModes.UPLOAD, "b", 1, "u", state = TransferState.Active))
        s.upsert(QueuedTransfer("c", FileTransferModes.UPLOAD, "c", 1, "u", state = TransferState.Cancelled))
        val remaining = s.pruneFinished()
        assertEquals(1, remaining.size)
        assertEquals("b", remaining[0].id)
    }

    @Test
    fun remove_deletesById() {
        val s = store()
        s.upsert(QueuedTransfer("a", FileTransferModes.UPLOAD, "a", 1, "u"))
        s.upsert(QueuedTransfer("b", FileTransferModes.UPLOAD, "b", 1, "u"))
        val after = s.remove("a")
        assertEquals(1, after.size)
        assertEquals("b", after[0].id)
    }

    @Test
    fun load_missingFile_returnsEmpty() {
        assertTrue(store().load().isEmpty())
    }

    @Test
    fun senderStartOffset_clampsOutOfRange() {
        assertEquals(0L, TransferResumeLogic.senderStartOffset(-5, 100))
        assertEquals(0L, TransferResumeLogic.senderStartOffset(200, 100))
        assertEquals(50L, TransferResumeLogic.senderStartOffset(50, 100))
        assertEquals(100L, TransferResumeLogic.senderStartOffset(100, 100))
    }

    @Test
    fun receiverResumeOffset_rejectsOversizePartial() {
        assertEquals(0L, TransferResumeLogic.receiverResumeOffset(150, 100))
        assertEquals(30L, TransferResumeLogic.receiverResumeOffset(30, 100))
    }

    @Test
    fun shouldRequestResume_onlyForPartialProgress() {
        assertTrue(TransferResumeLogic.shouldRequestResume(50, 100))
        assertEquals(false, TransferResumeLogic.shouldRequestResume(0, 100))
        assertEquals(false, TransferResumeLogic.shouldRequestResume(100, 100))
    }
}

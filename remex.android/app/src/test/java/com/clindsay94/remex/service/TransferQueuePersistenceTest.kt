package com.clindsay94.remex.service

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class TransferQueuePersistenceTest {

    @get:Rule val tmp = TemporaryFolder()

    private fun store() = TransferQueueStore(tmp.root)

    private fun writeRaw(text: String) = File(tmp.root, TransferQueueStore.FILE_NAME).writeText(text)

    private fun row(id: String, state: String = "Queued", createdAtMs: Long? = 1_000L): String =
        buildString {
            append("""{"id":"$id","mode":"upload","fileName":"$id.bin","size":10,"localUri":"content://x/$id","state":"$state"""")
            if (createdAtMs != null) append(""","createdAtMs":$createdAtMs""")
            append("}")
        }

    @Test
    fun corruptFile_loadsEmpty_andNextSaveReplacesIt() {
        writeRaw("this is {not json")

        assertEquals(emptyList<QueuedTransfer>(), store().load())

        val t = QueuedTransfer("a", FileTransferModes.UPLOAD, "a.bin", 1, "content://x/a")
        store().save(listOf(t))
        assertEquals(listOf(t), store().load())
    }

    @Test
    fun unknownState_keepsTheRowAsQueued_andNeighboursIntact() {
        writeRaw("[${row("a", "Done")},${row("b", "Archived")},${row("c", "Paused")}]")

        val loaded = store().load()

        assertEquals(listOf("a", "b", "c"), loaded.map { it.id })
        assertEquals(TransferState.Done, loaded[0].state)
        assertEquals(TransferState.Queued, loaded[1].state)
        assertFalse(loaded[1].hostKnows)
        assertEquals(TransferState.Paused, loaded[2].state)
    }

    @Test
    fun missingCreatedAt_isStampedNow_soPruneStaleKeepsIt() {
        writeRaw("[${row("legacy", "Done", createdAtMs = null)},${row("old", "Done", createdAtMs = 1L)}]")
        val before = System.currentTimeMillis()

        val loaded = store().load()
        assertTrue(loaded.single { it.id == "legacy" }.createdAtMs >= before)

        val kept = store().pruneStale()
        assertEquals(listOf("legacy"), kept.map { it.id })
        assertEquals(listOf("legacy"), store().load().map { it.id })
    }

    @Test
    fun everyFieldRoundTrips_includingSetNullables() {
        val full =
            QueuedTransfer(
                id = "full",
                mode = FileTransferModes.DOWNLOAD,
                fileName = "movie.mkv",
                size = 5_000_000_000L,
                localUri = "content://dest/movie.mkv",
                destRoot = "videos",
                destRelativePath = "2026/october",
                sourcePath = "C:/Users/me/movie.mkv",
                peerId = "9f2c4be07a1d33e5",
                state = TransferState.Done,
                bytesTransferred = 5_000_000_000L,
                sha256 = "q83vEjRWeJA=",
                error = "transient: retried",
                createdAtMs = 1_700_000_000_123L,
                hostKnows = true,
                verified = true,
            )

        store().save(listOf(full))

        assertEquals(listOf(full), store().load())
    }

    @Test
    fun absentNullables_roundTripAsNull_notEmptyStrings() {
        val bare = QueuedTransfer("bare", FileTransferModes.UPLOAD, "a.bin", 0, "content://x/a", createdAtMs = 42L)

        store().save(listOf(bare))
        val loaded = store().load().single()

        assertEquals(bare, loaded)
        assertNull(loaded.destRoot)
        assertNull(loaded.destRelativePath)
        assertNull(loaded.sourcePath)
        assertNull(loaded.peerId)
        assertNull(loaded.sha256)
        assertNull(loaded.error)
        assertFalse(loaded.verified)
    }
}

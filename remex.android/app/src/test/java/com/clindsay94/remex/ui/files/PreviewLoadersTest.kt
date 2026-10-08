package com.clindsay94.remex.ui.files

import com.clindsay94.remex.service.FileTransferLimits
import com.clindsay94.remex.ui.files.preview.ImagePreviewLoader
import com.clindsay94.remex.ui.files.preview.PreviewTooLargeException
import com.clindsay94.remex.ui.files.preview.RangeChunk
import com.clindsay94.remex.ui.files.preview.RangeReader
import com.clindsay94.remex.ui.files.preview.TextPreviewLoader
import com.clindsay94.remex.ui.files.preview.TextTail
import java.io.IOException
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

/** The Kotlin loaders, case for case with the PC's `FilePreviewTests` (file browser redesign, 2026-10-08). */
class PreviewLoadersTest {
    /** An in-memory file a reader serves, which a test can grow, shrink or replace. */
    private class FakeFile(var bytes: ByteArray = ByteArray(0), val stallAfterReads: Int? = null) {
        val reads = mutableListOf<Triple<Long, Int, Boolean>>()

        val reader = RangeReader { offset, length, fromEnd ->
            reads += Triple(offset, length, fromEnd)
            val size = bytes.size.toLong()
            val start = if (fromEnd) maxOf(0L, size - length) else offset
            var count = (size - start).coerceIn(0L, length.toLong()).toInt()
            if (stallAfterReads != null && reads.size > stallAfterReads) count = 0
            val data = if (count > 0) bytes.copyOfRange(start.toInt(), start.toInt() + count) else ByteArray(0)
            val eof = if (count > 0) start + count >= size else start >= size && stallAfterReads == null
            RangeChunk(start, data, size, eof)
        }
    }

    // ── Image loader ──

    @Test
    fun anImage_IsFetchedWhole_InOneMegabyteReads_WithProgress() = runBlocking {
        val file = FakeFile(ByteArray(2_500_000) { it.toByte() })
        val reported = mutableListOf<Float>()

        val bytes = ImagePreviewLoader.load(file.reader, { reported += it })

        assertArrayEquals(file.bytes, bytes)
        assertEquals(listOf(0L, 1_048_576L, 2_097_152L), file.reads.map { it.first })
        assertEquals(1f, reported.last())
    }

    @Test
    fun anImageOverTheLimit_IsRefusedAfterOneRead() = runBlocking {
        val file = FakeFile(ByteArray(3_000_000))
        try {
            ImagePreviewLoader.load(file.reader, maxBytes = 2_000_000)
            fail("an over-limit image must be refused")
        } catch (e: PreviewTooLargeException) {
            assertEquals(3_000_000L, e.fileSize)
        }
        assertEquals("nothing past the first read for a file that will be refused", 1, file.reads.size)
    }

    @Test
    fun aSourceThatStopsSending_FailsTheImage_RatherThanLoopingForever() = runBlocking {
        val file = FakeFile(ByteArray(2_500_000), stallAfterReads = 1)
        try {
            ImagePreviewLoader.load(file.reader)
            fail("a stalled source must fail the image")
        } catch (_: IOException) {
        }
        assertTrue(file.reads.size < 5)
    }

    // ── Text head ──

    @Test
    fun textHead_ReadsUpToTheLimit_AndSaysWhenItStopped() = runBlocking {
        val file = FakeFile("x".repeat(3_000_000).toByteArray())

        val preview = TextPreviewLoader.loadHead(file.reader)

        assertFalse(preview.isBinary)
        assertTrue(preview.truncated)
        assertEquals(FileTransferLimits.PREVIEW_TEXT_MAX_BYTES, preview.text.length)
        assertEquals(3_000_000L, preview.fileSize)
    }

    @Test
    fun aBinaryFileWithATextName_CostsOneRead_AndIsSaidToBeBinary() = runBlocking {
        val file = FakeFile(ByteArray(3_000_000))

        val preview = TextPreviewLoader.loadHead(file.reader)

        assertTrue(preview.isBinary)
        assertEquals(1, file.reads.size)
    }

    @Test
    fun aShortFile_IsReadWhole_AndNotTruncated() = runBlocking {
        val file = FakeFile("one\ntwo\n".toByteArray())

        val preview = TextPreviewLoader.loadHead(file.reader)

        assertEquals("one\ntwo\n", preview.text)
        assertFalse(preview.truncated)
    }

    // ── Live tail ──

    @Test
    fun liveTail_StartsAtTheEnd_ThenAppendsOnlyWhatWasAdded() = runBlocking {
        val file = FakeFile((1..100).joinToString("") { "line $it\n" }.toByteArray())
        val tail = TextTail(file.reader, tailBytes = 40)

        val start = tail.start()
        assertTrue(start.reset)
        assertEquals("line 100", start.lines.last())
        assertTrue("the partial first line of a mid-file tail is dropped", start.lines.first().startsWith("line "))

        file.bytes += "line 101\nline 1".toByteArray()
        val update = tail.poll()
        assertFalse(update.reset)
        assertEquals(listOf("line 101"), update.lines)
        assertEquals("a line still being written is shown, not held back", "line 1", update.pendingLine)

        file.bytes += "02\n".toByteArray()
        assertEquals(listOf("line 102"), tail.poll().lines)
    }

    @Test
    fun liveTail_RestartsWhenTheFileShrinks() = runBlocking {
        val file = FakeFile("old one\nold two\n".toByteArray())
        val tail = TextTail(file.reader)
        tail.start()

        file.bytes = "new\n".toByteArray() // rotated
        val update = tail.poll()

        assertTrue(update.reset)
        assertEquals(listOf("new"), update.lines)
    }

    @Test
    fun liveTail_ACharacterSplitAcrossTwoPolls_JoinsUp() = runBlocking {
        val file = FakeFile("a\n".toByteArray())
        val tail = TextTail(file.reader)
        tail.start()
        val euro = "€\n".toByteArray()

        file.bytes += byteArrayOf(euro[0], euro[1])
        assertEquals("half a character is not shown", "", tail.poll().pendingLine)

        file.bytes += byteArrayOf(euro[2], euro[3])
        assertEquals(listOf("€"), tail.poll().lines)
    }

    @Test
    fun liveTail_ALineThatNeverEnds_DoesNotGrowWithoutBound() = runBlocking {
        val file = FakeFile("x\n".toByteArray())
        val tail = TextTail(file.reader, tailBytes = 64)
        tail.start()

        repeat(20) {
            file.bytes += "y".repeat(50).toByteArray()
            assertTrue(tail.poll().pendingLine.length <= 64)
        }
    }

    @Test
    fun liveTail_WindowsLineEndings_AreNotShown() = runBlocking {
        val file = FakeFile("a\r\nb\r\n".toByteArray())

        assertEquals(listOf("a", "b"), TextTail(file.reader).start().lines)
    }
}

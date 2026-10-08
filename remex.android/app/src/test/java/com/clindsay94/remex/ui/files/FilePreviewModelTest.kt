package com.clindsay94.remex.ui.files

import com.clindsay94.remex.service.FileTransferLimits
import com.clindsay94.remex.ui.files.preview.RangeChunk
import com.clindsay94.remex.ui.files.preview.RangeReader
import com.clindsay94.remex.ui.files.preview.SyntaxKind
import java.io.IOException
import java.security.MessageDigest
import java.util.Base64
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The phone's preview pane state, case for case with the PC's preview tests (file browser redesign, 2026-10-08). */
@OptIn(ExperimentalCoroutinesApi::class)
class FilePreviewModelTest {
    private val files = mutableMapOf<String, ByteArray>()
    private val reads = mutableListOf<String>()
    private val hashFailures = mutableSetOf<String>()
    private var hashGate: kotlinx.coroutines.CompletableDeferred<Unit>? = null
    private var hashesFinished = 0
    private var pcReadable = true
    private var pcHashable = true

    private val sources = object : PreviewSources<String> {
        override fun reader(target: PreviewTarget) = RangeReader { offset, length, fromEnd ->
            reads += target.path
            val bytes = files[target.path] ?: throw IOException("gone")
            val start = if (fromEnd) maxOf(0L, bytes.size - length.toLong()) else offset
            val count = (bytes.size - start).coerceIn(0L, length.toLong()).toInt()
            RangeChunk(start, if (count > 0) bytes.copyOfRange(start.toInt(), start.toInt() + count) else ByteArray(0), bytes.size.toLong(), start + count >= bytes.size)
        }

        override suspend fun hashBase64(target: PreviewTarget): String {
            hashGate?.await()
            hashesFinished++
            if (target.path in hashFailures) throw IOException("refused")
            val bytes = files[target.path] ?: throw IOException("gone")
            return Base64.getEncoder().encodeToString(MessageDigest.getInstance("SHA-256").digest(bytes))
        }

        override fun canRead(side: FileSide) = side == FileSide.Phone || pcReadable
        override fun canHash(side: FileSide) = side == FileSide.Phone || pcHashable

        override suspend fun decodeImage(bytes: ByteArray): String? = if (bytes.firstOrNull() == 0x89.toByte()) "image:${bytes.size}" else null
    }

    private fun target(path: String, size: Long = files[path]?.size?.toLong() ?: 0, side: FileSide = FileSide.Pc) =
        PreviewTarget(side, "docs", path, path.substringAfterLast('/'), isDirectory = false, sizeBytes = size, modifiedMs = 0)

    private fun TestScope.model() = FilePreviewModel(backgroundScope, sources)

    // advanceUntilIdle() skips backgroundScope work; moving the clock past the 150 ms pause runs it.
    private fun TestScope.settle() {
        advanceTimeBy(1_000)
        runCurrent()
    }

    @Test
    fun aLog_IsShownAsNumberedColouredLines_WithNoPhantomLastLine() = runTest(StandardTestDispatcher()) {
        files["agent.log"] = "2026-10-08 14:03:21 INFO start\n2026-10-08 14:03:24 ERROR boom\n".toByteArray()
        val preview = model()

        preview.show(target("agent.log"))
        settle()

        val ui = preview.ui.value
        assertEquals(PreviewState.Text, ui.state)
        assertEquals(listOf(1, 2), ui.lines.map { it.number })
        assertTrue(ui.lines[1].spans.any { it.kind == SyntaxKind.LogError })
        assertTrue(ui.canTail)
    }

    @Test
    fun aFileThatIsNotText_IsNotShownAsText() = runTest(StandardTestDispatcher()) {
        files["data.txt"] = byteArrayOf(0x41, 0, 0x42)
        files["data.blob"] = byteArrayOf(0x41, 0, 0x42)
        val preview = model()

        preview.show(target("data.txt")); settle()
        assertEquals("a text name with bytes inside says so", PreviewState.Binary, preview.ui.value.state)

        preview.show(target("data.blob")); settle()
        assertEquals("an unknown name just shows its details", PreviewState.Details, preview.ui.value.state)

        reads.clear()
        preview.show(target("setup.exe", size = 1000)); settle()
        assertEquals(PreviewState.Details, preview.ui.value.state)
        assertTrue("a known binary is never read", reads.isEmpty())
    }

    @Test
    fun images_AreDecodedWhole_OrRefusedBeforeAnyReadWhenTooBig() = runTest(StandardTestDispatcher()) {
        files["a.png"] = ByteArray(1000) { if (it == 0) 0x89.toByte() else 1 }
        files["fake.jpg"] = ByteArray(10) { 1 }
        val preview = model()

        preview.show(target("a.png")); settle()
        assertEquals(PreviewState.Image, preview.ui.value.state)
        assertEquals("image:1000", preview.ui.value.image)

        preview.show(target("fake.jpg")); settle()
        assertEquals("not decodable: details, not a broken image", PreviewState.Details, preview.ui.value.state)

        reads.clear()
        preview.show(target("huge.jpg", size = FileTransferLimits.PREVIEW_IMAGE_MAX_BYTES + 1)); settle()
        assertEquals(PreviewState.TooLarge, preview.ui.value.state)
        assertTrue(reads.isEmpty())
    }

    @Test
    fun flickingThroughFiles_OnlyLoadsTheOneThatStays() = runTest(StandardTestDispatcher()) {
        files["a.txt"] = "a".toByteArray()
        files["b.txt"] = "b".toByteArray()
        val preview = model()

        preview.show(target("a.txt"))
        advanceTimeBy(50)
        preview.show(target("b.txt"))
        settle()

        assertEquals(listOf("b.txt"), reads)
        assertEquals("b", preview.ui.value.lines.single().text)
    }

    @Test
    fun aFileThatCannotBeRead_SaysSo_AndCanBeTriedAgain() = runTest(StandardTestDispatcher()) {
        val preview = model()

        preview.show(target("missing.txt", size = 5)); settle()
        assertEquals(PreviewState.Failed, preview.ui.value.state)

        files["missing.txt"] = "back!".toByteArray()
        preview.show(target("missing.txt", size = 5)); settle()
        assertEquals(PreviewState.Text, preview.ui.value.state)
    }

    @Test
    fun live_StartsAtTheTail_ThenAddsWhatIsWritten_EveryTwoSeconds() = runTest(StandardTestDispatcher()) {
        files["app.log"] = "one\ntwo\n".toByteArray()
        val preview = model()
        preview.show(target("app.log")); settle()

        preview.setLive(true)
        runCurrent()
        assertTrue(preview.ui.value.live)
        assertEquals(listOf("one", "two"), preview.ui.value.lines.map { it.text })
        assertNull("a tail has no line numbers", preview.ui.value.lines.first().number)

        files["app.log"] = "one\ntwo\nthree\nfou".toByteArray()
        advanceTimeBy(FileTransferLimits.PREVIEW_TAIL_POLL_MS + 1)
        assertEquals(listOf("one", "two", "three"), preview.ui.value.lines.map { it.text })
        assertEquals("fou", preview.ui.value.pendingLine)

        preview.setLive(false)
        files["app.log"] = "one\ntwo\nthree\nfour\nfive\n".toByteArray()
        advanceTimeBy(10_000)
        assertEquals("stopped means stopped", 3, preview.ui.value.lines.size)
    }

    @Test
    fun aLongLiveTail_KeepsOnlyTheNewestLines() = runTest(StandardTestDispatcher()) {
        files["app.log"] = "x\n".toByteArray()
        val preview = FilePreviewModel(backgroundScope, sources, maxTailLines = 3)
        val t = target("app.log")
        preview.show(t); settle()

        preview.apply(t, com.clindsay94.remex.ui.files.preview.TailUpdate(false, listOf("a", "b", "c", "d"), "", 10))

        assertEquals(listOf("b", "c", "d"), preview.ui.value.lines.map { it.text })
    }

    @Test
    fun theFingerprint_IsShownAsHex_AndAPastedOneIsCompared() = runTest(StandardTestDispatcher()) {
        files["abc.txt"] = "abc".toByteArray()
        val preview = model()
        preview.show(target("abc.txt")); settle()

        preview.computeHash(); settle()
        assertEquals("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", preview.ui.value.hashHex)

        preview.compare("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")
        assertEquals(HashCompareOutcome.Match, preview.ui.value.compare)
        preview.compare("ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=")
        assertEquals("Base64 works too", HashCompareOutcome.Match, preview.ui.value.compare)
        preview.compare("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
        assertEquals(HashCompareOutcome.Mismatch, preview.ui.value.compare)
        preview.compare("not a hash")
        assertEquals(HashCompareOutcome.NotAHash, preview.ui.value.compare)
    }

    @Test
    fun aFingerprintThatFails_SaysSo() = runTest(StandardTestDispatcher()) {
        files["a.txt"] = "a".toByteArray()
        hashFailures += "a.txt"
        val preview = model()
        preview.show(target("a.txt")); settle()

        preview.computeHash(); settle()

        assertTrue(preview.ui.value.hashFailed)
        assertFalse(preview.ui.value.hashing)
        assertNull(preview.ui.value.hashHex)
    }

    @Test
    fun verifyAgainst_SaysIdenticalDifferentOrFailed() = runTest(StandardTestDispatcher()) {
        files["a.txt"] = "same".toByteArray()
        files["copy/a.txt"] = "same".toByteArray()
        files["other/a.txt"] = "changed".toByteArray()
        val preview = model()
        val phoneCopy = target("a.txt", side = FileSide.Phone)
        preview.show(phoneCopy); settle()

        preview.verifyAgainst(target("copy/a.txt")); settle()
        assertEquals(CounterpartOutcome.Identical, preview.ui.value.counterpart)
        assertTrue("the file's own fingerprint is shown on the way", preview.ui.value.hashHex != null)

        preview.verifyAgainst(target("other/a.txt")); settle()
        assertEquals(CounterpartOutcome.Different, preview.ui.value.counterpart)

        preview.verifyAgainst(target("gone.txt", size = 3)); settle()
        assertEquals(CounterpartOutcome.Failed, preview.ui.value.counterpart)
        assertFalse(preview.ui.value.verifying)
    }

    @Test
    fun aSlowAnswerForTheLastFile_NeverLandsOnTheNextOne() = runTest(StandardTestDispatcher()) {
        files["a.txt"] = "a".toByteArray()
        files["b.txt"] = "b".toByteArray()
        files["copy.txt"] = "a".toByteArray()
        val preview = model()
        preview.show(target("a.txt")); settle()
        val gate = kotlinx.coroutines.CompletableDeferred<Unit>()
        hashGate = gate
        preview.verifyAgainst(target("copy.txt")); settle()

        preview.show(target("b.txt")); settle()
        gate.complete(Unit); settle()

        assertEquals("b.txt", preview.ui.value.target?.path)
        assertNull("a's fingerprint is not b's", preview.ui.value.hashHex)
        assertEquals("a's cross-check is not b's", CounterpartOutcome.None, preview.ui.value.counterpart)
    }

    @Test
    fun anOlderPc_IsNeverAskedForAPreview_AndTheScreenSaysToUpdate() = runTest(StandardTestDispatcher()) {
        files["agent.log"] = "x\n".toByteArray()
        pcReadable = false
        pcHashable = false
        val preview = model()

        preview.show(target("agent.log")); settle()

        assertEquals(PreviewState.NeedsUpdate, preview.ui.value.state)
        assertTrue("nothing an old PC can't answer is sent", reads.isEmpty())
        assertFalse(preview.ui.value.canHash)
        assertFalse(preview.ui.value.canVerifyAcross)
        preview.computeHash(); settle()
        assertEquals(0, hashesFinished)
    }

    @Test
    fun aPhoneFile_CanBeHashed_ButNotCheckedAgainstAnOlderPc() = runTest(StandardTestDispatcher()) {
        files["a.txt"] = "a".toByteArray()
        pcHashable = false
        val preview = model()

        preview.show(target("a.txt", side = FileSide.Phone)); settle()

        assertTrue(preview.ui.value.canHash)
        assertFalse(preview.ui.value.canVerifyAcross)
        preview.verifyAgainst(target("a.txt")); settle()
        assertEquals(CounterpartOutcome.None, preview.ui.value.counterpart)
    }

    @Test
    fun movingToAnotherFile_StopsACheckStillRunning() = runTest(StandardTestDispatcher()) {
        files["a.txt"] = "a".toByteArray()
        files["b.txt"] = "b".toByteArray()
        files["copy.txt"] = "a".toByteArray()
        val preview = model()
        preview.show(target("a.txt")); settle()
        val gate = kotlinx.coroutines.CompletableDeferred<Unit>()
        hashGate = gate
        preview.verifyAgainst(target("copy.txt")); settle()

        preview.show(target("b.txt")); settle()
        gate.complete(Unit); settle()

        assertEquals("the cross-check was cancelled, not left hashing", 0, hashesFinished)
    }
}

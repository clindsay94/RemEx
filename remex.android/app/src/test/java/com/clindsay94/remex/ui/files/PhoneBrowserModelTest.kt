package com.clindsay94.remex.ui.files

import com.clindsay94.remex.ui.files.preview.RangeReader
import java.io.FileNotFoundException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The "This phone" side's open folder (file browser redesign, 2026-10-08). */
@OptIn(ExperimentalCoroutinesApi::class)
class PhoneBrowserModelTest {
    private val gates = mutableMapOf<String, CompletableDeferred<List<LocalEntry>>>()

    private val source = object : PhoneFileSource {
        override suspend fun list(rootUri: String, path: String): List<LocalEntry> =
            gates[path]?.await() ?: when (path) {
                "" -> listOf(LocalEntry("1", "Camera", true, -1, 0, null), LocalEntry("2", "a.jpg", false, 3, 7, "image/jpeg"))
                else -> throw FileNotFoundException(path)
            }
        override fun reader(rootUri: String, path: String) = RangeReader { _, _, _ -> error("unused") }
        override suspend fun sha256(rootUri: String, path: String, onProgress: ((Long) -> Unit)?) = ByteArray(32)
        override suspend fun documentUri(rootUri: String, path: String) = "$rootUri/$path"
    }

    @Test
    fun aFolder_IsListedAsTheSameRowsThePcSideUses() = runTest(UnconfinedTestDispatcher()) {
        val browser = PhoneBrowserModel(backgroundScope, source)

        browser.open("content://tree/DCIM", "/")

        val state = browser.state.value
        assertEquals("", state.path)
        assertFalse(state.loading)
        assertEquals(listOf("Camera", "a.jpg"), state.entries.map { it.name })
        assertEquals("an unknown folder size is shown as nothing, not -1", 0L, state.entries[0].sizeBytes)
        assertEquals(7L, state.entries[1].modifiedUnixMs)
        assertEquals("rows carry their document id: names need not be unique", listOf("1", "2"), state.entries.map { it.id })
    }

    @Test
    fun aFolderThatIsGone_SaysSo() = runTest(UnconfinedTestDispatcher()) {
        val browser = PhoneBrowserModel(backgroundScope, source)

        browser.open("content://tree/DCIM", "Deleted")

        assertTrue(browser.state.value.failed)
        assertTrue(browser.state.value.entries.isEmpty())
    }

    @Test
    fun aNewerFolder_WinsOverASlowerOlderOne() = runTest(UnconfinedTestDispatcher()) {
        val slow = CompletableDeferred<List<LocalEntry>>()
        gates["Slow"] = slow
        val browser = PhoneBrowserModel(backgroundScope, source)

        browser.open("content://tree/DCIM", "Slow")
        browser.open("content://tree/DCIM", "")
        slow.complete(listOf(LocalEntry("9", "late.txt", false, 1, 0, null)))

        assertEquals("", browser.state.value.path)
        assertEquals(listOf("Camera", "a.jpg"), browser.state.value.entries.map { it.name })
    }

    @Test
    fun clear_LeavesNothingOpen() = runTest(UnconfinedTestDispatcher()) {
        val browser = PhoneBrowserModel(backgroundScope, source)
        browser.open("content://tree/DCIM", "")

        browser.clear()

        assertNull(browser.state.value.rootId)
    }
}

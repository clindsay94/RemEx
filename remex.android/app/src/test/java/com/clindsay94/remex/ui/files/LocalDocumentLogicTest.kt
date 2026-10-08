package com.clindsay94.remex.ui.files

import java.io.ByteArrayInputStream
import java.io.InputStream
import java.security.MessageDigest
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Listing, walking, range reads and hashing of phone folders (file browser redesign, 2026-10-08). */
class LocalDocumentLogicTest {
    private val dir = "vnd.android.document/directory"

    // A tiny tree: root -> DCIM/ -> Camera/ -> IMG_1.jpg, and root -> notes.txt
    private val tree = mapOf(
        "root" to listOf(
            LocalEntry("d1", "DCIM", true, -1, 0, dir),
            LocalEntry("f1", "notes.txt", false, 5, 0, "text/plain"),
        ),
        "d1" to listOf(LocalEntry("d2", "Camera", true, -1, 0, dir)),
        "d2" to listOf(LocalEntry("f2", "IMG_1.jpg", false, 3, 0, "image/jpeg")),
    )
    private val listed = mutableListOf<String>()
    // Like a real provider, listing the children of a FILE is an error, not an empty folder.
    private val childrenOf: suspend (String) -> List<LocalEntry> = { id ->
        listed += id
        tree[id] ?: throw IllegalArgumentException("$id is not a directory")
    }

    @Test
    fun aRow_BecomesAnEntry_AndARowWithNoNameIsSkipped() {
        val folder = LocalDocumentLogic.entryOf("d", "DCIM", dir, 4096, 7)!!
        assertTrue(folder.isDirectory)
        assertEquals("a folder's size is not a file size", -1L, folder.sizeBytes)

        val file = LocalDocumentLogic.entryOf("f", "a.txt", "text/plain", null, null)!!
        assertFalse(file.isDirectory)
        assertEquals("no size from the provider is unknown, not zero", -1L, file.sizeBytes)

        assertNull(LocalDocumentLogic.entryOf("x", null, "text/plain", 1, 1))
        assertNull(LocalDocumentLogic.entryOf(null, "a", "text/plain", 1, 1))
    }

    @Test
    fun aPath_IsWalkedOneListingPerLevel_AndRememberedForNextTime() = runBlocking {
        val cache = HashMap<String, String>()

        assertEquals("f2", LocalDocumentLogic.resolve("root", "DCIM/Camera/IMG_1.jpg", childrenOf, cache))
        assertEquals(listOf("root", "d1", "d2"), listed)

        listed.clear()
        assertEquals("d2", LocalDocumentLogic.resolve("root", "/DCIM/Camera/", childrenOf, cache))
        assertTrue("a walked path costs no listing the second time", listed.isEmpty())
        assertEquals("root", LocalDocumentLogic.resolve("root", "", childrenOf, cache))
    }

    @Test
    fun aMissingName_AnUnsafePath_OrAFileUsedAsAFolder_ResolvesToNothing() = runBlocking {
        assertNull(LocalDocumentLogic.resolve("root", "DCIM/Screenshots", childrenOf))
        assertNull(LocalDocumentLogic.resolve("root", "DCIM/../notes.txt", childrenOf))
        assertNull(LocalDocumentLogic.resolve("root", "DCIM//Camera", childrenOf))
        assertNull(LocalDocumentLogic.resolve("root", "notes.txt/inside", childrenOf))
    }

    @Test
    fun aRange_IsReadFromItsOffset_OrFromTheEnd() = runBlocking {
        val bytes = ByteArray(100) { it.toByte() }

        val middle = LocalDocumentLogic.readRange(ByteArrayInputStream(bytes), 100, 10, 5, fromEnd = false)
        assertEquals(10L, middle.offset)
        assertArrayEquals(bytes.copyOfRange(10, 15), middle.data)
        assertFalse(middle.eof)

        val tail = LocalDocumentLogic.readRange(ByteArrayInputStream(bytes), 100, 0, 30, fromEnd = true)
        assertEquals(70L, tail.offset)
        assertArrayEquals(bytes.copyOfRange(70, 100), tail.data)
        assertTrue(tail.eof)

        val past = LocalDocumentLogic.readRange(ByteArrayInputStream(bytes), 100, 500, 5, fromEnd = false)
        assertEquals(0, past.data.size)
        assertTrue(past.eof)
    }

    @Test
    fun aFileOfUnknownSize_StillGivesARightTail_AndAnHonestSize() = runBlocking {
        val bytes = ByteArray(1000) { (it % 251).toByte() }

        val tail = LocalDocumentLogic.readRange(ByteArrayInputStream(bytes), -1, 0, 64, fromEnd = true)
        assertEquals(936L, tail.offset)
        assertArrayEquals(bytes.copyOfRange(936, 1000), tail.data)
        assertEquals(1000L, tail.fileSize)

        val head = LocalDocumentLogic.readRange(ByteArrayInputStream(bytes), -1, 0, 100, fromEnd = false)
        assertFalse(head.eof)
        assertTrue("never smaller than what was read, so a tail never thinks it shrank", head.fileSize >= 100)
    }

    @Test
    fun aStreamThatSkipsNothing_IsStillPositionedRight() = runBlocking {
        val bytes = ByteArray(50) { it.toByte() }
        val stubborn = object : InputStream() {
            private val inner = ByteArrayInputStream(bytes)
            override fun read(): Int = inner.read()
            override fun read(b: ByteArray, off: Int, len: Int): Int = inner.read(b, off, len)
            override fun skip(n: Long): Long = 0
        }

        val chunk = LocalDocumentLogic.readRange(stubborn, 50, 20, 5, fromEnd = false)
        assertArrayEquals(bytes.copyOfRange(20, 25), chunk.data)
    }

    @Test
    fun theHash_IsTheSha256OfEveryByte() = runBlocking {
        val bytes = ByteArray(200_000) { (it * 7).toByte() }
        var progress = 0L

        val hash = LocalDocumentLogic.sha256(ByteArrayInputStream(bytes)) { progress = it }

        assertArrayEquals(MessageDigest.getInstance("SHA-256").digest(bytes), hash)
        assertEquals(200_000L, progress)
    }
}

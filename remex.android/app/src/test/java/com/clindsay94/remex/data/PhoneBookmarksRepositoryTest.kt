package com.clindsay94.remex.data

import java.io.File
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** Phone-only bookmarks and the one "Access from your PC" list (file browser redesign, 2026-10-08). */
class PhoneBookmarksRepositoryTest {
    private class MapStore(shared: Set<String> = emptySet(), fullBrowse: String? = null) : PhoneFolderStore {
        override val sharedUris = MutableStateFlow(shared)
        override val bookmarkUris = MutableStateFlow(emptySet<String>())
        override val otherHeldUris = MutableStateFlow(setOfNotNull(fullBrowse))
        override suspend fun setShared(uri: String, shared: Boolean) {
            sharedUris.value = if (shared) sharedUris.value + uri else sharedUris.value - uri
        }
        override suspend fun setBookmarked(uri: String, bookmarked: Boolean) {
            bookmarkUris.value = if (bookmarked) bookmarkUris.value + uri else bookmarkUris.value - uri
        }
    }

    private val released = mutableListOf<String>()

    @Test
    fun aBookmark_IsNotSharedUntilThePersonSharesIt() = runBlocking {
        val store = MapStore()
        val repo = PhoneBookmarksRepository(store) { released += it }

        repo.addBookmark("content://tree/Music")

        assertTrue("the PC-facing list is untouched", store.sharedUris.value.isEmpty())
        assertEquals(listOf(PhoneFolder("content://tree/Music", isBookmark = true, isShared = false)), repo.folders.first())

        repo.setShared("content://tree/Music", true)
        assertEquals(setOf("content://tree/Music"), store.sharedUris.value)
        assertEquals(listOf(PhoneFolder("content://tree/Music", isBookmark = true, isShared = true)), repo.folders.first())
    }

    @Test
    fun unsharingABookmark_KeepsItOnThePhone_AndKeepsItsGrant() = runBlocking {
        val store = MapStore()
        val repo = PhoneBookmarksRepository(store) { released += it }
        repo.addBookmark("u")
        repo.setShared("u", true)

        repo.setShared("u", false)

        assertEquals(listOf(PhoneFolder("u", isBookmark = true, isShared = false)), repo.folders.first())
        assertTrue(released.isEmpty())
    }

    @Test
    fun removingABookmark_ReleasesItsGrant_UnlessItIsStillShared() = runBlocking {
        val store = MapStore(shared = setOf("shared"))
        val repo = PhoneBookmarksRepository(store) { released += it }
        repo.addBookmark("shared")
        repo.addBookmark("phone-only")

        repo.removeBookmark("shared")
        repo.removeBookmark("phone-only")

        assertEquals(listOf("phone-only"), released)
        assertEquals(listOf(PhoneFolder("shared", isBookmark = false, isShared = true)), repo.folders.first())
    }

    @Test
    fun theWholeDeviceBrowseGrant_IsNeverReleasedHere() = runBlocking {
        val store = MapStore(shared = setOf("root"), fullBrowse = "root")
        val repo = PhoneBookmarksRepository(store) { released += it }

        repo.setShared("root", false)

        assertTrue(released.isEmpty())
    }

    /**
     * THE CONSENT BOUNDARY. A phone-only bookmark must never reach the PC: everything that answers the PC
     * (the file host, its resolvers and the consent manager) reads the shared list, never the bookmark key. A
     * future "convenience" that let the host see bookmarks would silently share every folder the person only
     * meant to browse on the phone.
     */
    @Test
    fun nothingThatAnswersThePc_ReadsTheBookmarks() {
        val root = System.getProperty("remex.repoRoot")?.let(::File)
            ?: generateSequence(File(".").absoluteFile) { it.parentFile }.first { File(it, "remex.android").isDirectory }
        val service = File(root, "remex.android/app/src/main/java/com/clindsay94/remex/service")
        val files = service.listFiles { f -> f.extension == "kt" }.orEmpty()
        assertTrue("the scan found nothing to check", files.size > 10)
        assertTrue(files.any { it.name == "AndroidFileTransferHost.kt" })
        for (file in files) {
            val text = file.readText()
            assertTrue("${file.name} reads the phone-only bookmarks", "phoneBookmark" !in text && "phone_bookmark_uris" !in text)
        }
    }

    @Test
    fun aFolderAnUnfinishedTransferUses_KeepsItsGrant() {
        fun row(uri: String, state: com.clindsay94.remex.service.TransferState) =
            com.clindsay94.remex.service.QueuedTransfer("t$uri$state", "upload", "a", 1, uri, state = state)
        val tree = "content://com.android.externalstorage.documents/tree/primary%3AMusic"
        val queue = listOf(
            row("$tree/document/primary%3AMusic%2Fa.mp3", com.clindsay94.remex.service.TransferState.Paused),
            row("content://x/tree/done/document/d", com.clindsay94.remex.service.TransferState.Done),
            row("content://x/tree/gone/document/d", com.clindsay94.remex.service.TransferState.Cancelled),
            row("content://media/external/images/1", com.clindsay94.remex.service.TransferState.Active),
        )

        assertEquals(setOf(tree), PhoneBookmarksRepository.treesInUse(queue))
    }

    @Test
    fun sharedFoldersComeFirst_ThenPhoneOnlyBookmarks() {
        assertEquals(
            listOf(
                PhoneFolder("a", isBookmark = false, isShared = true),
                PhoneFolder("c", isBookmark = true, isShared = true),
                PhoneFolder("b", isBookmark = true, isShared = false),
            ),
            PhoneBookmarksRepository.merge(shared = setOf("c", "a"), bookmarks = setOf("b", "c")),
        )
    }
}

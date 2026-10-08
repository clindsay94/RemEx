package com.clindsay94.remex.data

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

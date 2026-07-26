package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.FileManagerLogic
import com.clindsay94.remex.ui.screens.RemoteFileEntry
import com.clindsay94.remex.ui.screens.SortField
import com.clindsay94.remex.ui.screens.SortOption
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * ViewModel state tests for the file-manager UI (plan WP7): sort ordering, breadcrumb parsing, and
 * multi-select reduction. Runs on the JVM against [FileManagerLogic] — no Android/Compose/socket.
 */
class FileManagerLogicTest {

    private fun file(name: String, size: Long = 0L, modified: Long = 0L) =
        RemoteFileEntry(name = name, isDirectory = false, sizeBytes = size, modifiedUnixMs = modified)

    private fun dir(name: String, modified: Long = 0L) =
        RemoteFileEntry(name = name, isDirectory = true, sizeBytes = 0L, modifiedUnixMs = modified)

    // ── Sort ──────────────────────────────────────────────────────────────────

    @Test
    fun sort_directoriesAlwaysBeforeFiles_regardlessOfField() {
        val input = listOf(file("apple.txt", size = 10), dir("zebra"), file("mango.txt", size = 5))
        val sorted = FileManagerLogic.sortEntries(input, SortOption(SortField.SIZE, ascending = true))
        assertEquals("zebra", sorted[0].name) // directory floats above files
        assertTrue(sorted[0].isDirectory)
        assertFalse(sorted[1].isDirectory)
    }

    @Test
    fun sort_byNameAscending_isCaseInsensitive() {
        val input = listOf(file("Banana"), file("apple"), file("Cherry"))
        val sorted = FileManagerLogic.sortEntries(input, SortOption(SortField.NAME, ascending = true))
        assertEquals(listOf("apple", "Banana", "Cherry"), sorted.map { it.name })
    }

    @Test
    fun sort_byNameDescending_reversesOrder() {
        val input = listOf(file("apple"), file("banana"), file("cherry"))
        val sorted = FileManagerLogic.sortEntries(input, SortOption(SortField.NAME, ascending = false))
        assertEquals(listOf("cherry", "banana", "apple"), sorted.map { it.name })
    }

    @Test
    fun sort_bySizeDescending_ordersLargestFirst() {
        val input = listOf(file("a", size = 100), file("b", size = 5000), file("c", size = 50))
        val sorted = FileManagerLogic.sortEntries(input, SortOption(SortField.SIZE, ascending = false))
        assertEquals(listOf("b", "a", "c"), sorted.map { it.name })
    }

    @Test
    fun sort_byDateAscending_ordersOldestFirst() {
        val input = listOf(file("new", modified = 300), file("old", modified = 100), file("mid", modified = 200))
        val sorted = FileManagerLogic.sortEntries(input, SortOption(SortField.DATE, ascending = true))
        assertEquals(listOf("old", "mid", "new"), sorted.map { it.name })
    }

    @Test
    fun sort_parentEntryAlwaysPinnedFirst() {
        val input = listOf(file("z.txt"), dir(".."), dir("alpha"))
        val sorted = FileManagerLogic.sortEntries(input, SortOption(SortField.NAME, ascending = false))
        assertEquals("..", sorted[0].name)
    }

    @Test
    fun nextSort_flipsDirectionOnSameField_switchesFreshOnDifferent() {
        val start = SortOption(SortField.NAME, ascending = true)
        val flipped = FileManagerLogic.nextSort(start, SortField.NAME)
        assertEquals(SortOption(SortField.NAME, ascending = false), flipped)

        val switched = FileManagerLogic.nextSort(flipped, SortField.SIZE)
        assertEquals(SortOption(SortField.SIZE, ascending = true), switched)
    }

    // ── Breadcrumbs ─────────────────────────────────────────────────────────────

    @Test
    fun breadcrumbs_rootOnly_forRootPath() {
        val crumbs = FileManagerLogic.buildBreadcrumbs("C Drive", "/")
        assertEquals(1, crumbs.size)
        assertEquals("C Drive", crumbs[0].label)
        assertEquals("/", crumbs[0].path)
    }

    @Test
    fun breadcrumbs_splitsNestedPath_withCumulativePaths() {
        val crumbs = FileManagerLogic.buildBreadcrumbs("Root", "Documents/Work/Reports")
        assertEquals(listOf("Root", "Documents", "Work", "Reports"), crumbs.map { it.label })
        assertEquals(listOf("/", "Documents", "Documents/Work", "Documents/Work/Reports"), crumbs.map { it.path })
    }

    @Test
    fun breadcrumbs_normalisesBackslashesAndTrimsSlashes() {
        val crumbs = FileManagerLogic.buildBreadcrumbs("Root", "\\Users\\Connor\\")
        assertEquals(listOf("Root", "Users", "Connor"), crumbs.map { it.label })
        assertEquals(listOf("/", "Users", "Users/Connor"), crumbs.map { it.path })
    }

    // ── Selection ────────────────────────────────────────────────────────────────

    @Test
    fun toggleSelection_addsThenRemoves() {
        val added = FileManagerLogic.toggleSelection(emptySet(), "a.txt")
        assertEquals(setOf("a.txt"), added)
        val removed = FileManagerLogic.toggleSelection(added, "a.txt")
        assertTrue(removed.isEmpty())
    }

    @Test
    fun toggleSelection_accumulatesDistinct() {
        var sel = FileManagerLogic.toggleSelection(emptySet(), "a")
        sel = FileManagerLogic.toggleSelection(sel, "b")
        assertEquals(setOf("a", "b"), sel)
    }

    // ── Path math ────────────────────────────────────────────────────────────────

    @Test
    fun combinePath_joinsRelativeToCurrent() {
        assertEquals("child", FileManagerLogic.combinePath("/", "child"))
        assertEquals("a/b/child", FileManagerLogic.combinePath("a/b", "child"))
        assertEquals("a/b/child", FileManagerLogic.combinePath("a\\b\\", "child"))
    }

    @Test
    fun parentPath_walksUpOneLevel() {
        assertEquals("/", FileManagerLogic.parentPath("/"))
        assertEquals("/", FileManagerLogic.parentPath("docs"))
        assertEquals("docs", FileManagerLogic.parentPath("docs/work"))
    }

    @Test
    fun isAtRoot_detectsRootPaths() {
        assertTrue(FileManagerLogic.isAtRoot("/"))
        assertTrue(FileManagerLogic.isAtRoot("\\"))
        assertFalse(FileManagerLogic.isAtRoot("docs"))
    }

    @Test
    fun isThumbnailCandidate_trueForImagesAndVideos_falseOtherwise() {
        assertTrue(FileManagerLogic.isThumbnailCandidate("holiday.JPG"))
        assertTrue(FileManagerLogic.isThumbnailCandidate("clip.mp4"))
        assertFalse(FileManagerLogic.isThumbnailCandidate("notes.txt"))
        assertFalse(FileManagerLogic.isThumbnailCandidate("folder"))
    }

    // ── Shared roots/entries parsing (RemEx-4xhj) ─────────────────────────────────
    // FileTransferViewModel and the Share-to-PC screen both parse this exact `file_roots_response` /
    // `file_browse_response` shape; these guard the shared parse both now delegate to.

    @Test
    fun parseSharedRoots_readsAllFields_andDefaultsMissingBooleansToFalse() {
        val arr =
            org.json.JSONArray(
                """
                [
                  {"rootId":"r1","displayName":"Documents","isWritable":true,"canRename":true,
                   "canMove":true,"canDelete":true,"canRemoveRoot":true},
                  {"rootId":"r2","displayName":"Read Only"}
                ]
                """.trimIndent()
            )
        val roots = FileManagerLogic.parseSharedRoots(arr)
        assertEquals(2, roots.size)
        assertEquals("r1", roots[0].rootId)
        assertTrue(roots[0].isWritable)
        assertTrue(roots[0].canRemoveRoot)
        assertEquals("r2", roots[1].rootId)
        assertFalse(roots[1].isWritable)
        assertFalse(roots[1].canRename)
    }

    @Test
    fun parseSharedRoots_nullArray_yieldsEmptyList() {
        assertTrue(FileManagerLogic.parseSharedRoots(null).isEmpty())
    }

    @Test
    fun parseFileEntries_readsAllFields() {
        val arr =
            org.json.JSONArray(
                """
                [
                  {"name":"Reports","isDirectory":true,"sizeBytes":0,"modifiedUnixMs":1000},
                  {"name":"notes.txt","isDirectory":false,"sizeBytes":42,"modifiedUnixMs":2000}
                ]
                """.trimIndent()
            )
        val entries = FileManagerLogic.parseFileEntries(arr)
        assertEquals(2, entries.size)
        assertEquals("Reports", entries[0].name)
        assertTrue(entries[0].isDirectory)
        assertEquals("notes.txt", entries[1].name)
        assertFalse(entries[1].isDirectory)
        assertEquals(42L, entries[1].sizeBytes)
        assertEquals(2000L, entries[1].modifiedUnixMs)
    }

    @Test
    fun parseFileEntries_nullArray_yieldsEmptyList() {
        assertTrue(FileManagerLogic.parseFileEntries(null).isEmpty())
    }
}

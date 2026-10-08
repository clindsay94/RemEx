package com.clindsay94.remex.ui.files

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** The File Transfer folder tree (file browser redesign, 2026-10-08). */
@OptIn(ExperimentalCoroutinesApi::class)
class FileTreeModelTest {
    // Folders per (side, root, path).
    private val folders = mutableMapOf(
        Triple(FileSide.Pc, "docs", "") to listOf("Taxes", "Logs"),
        Triple(FileSide.Pc, "docs", "Logs") to listOf("2025"),
        Triple(FileSide.Pc, "docs", "Logs/2025") to emptyList(),
        Triple(FileSide.Phone, "content://tree/DCIM", "") to listOf("Camera"),
    )
    private val calls = mutableListOf<Triple<FileSide, String, String>>()
    private var failNext = false

    private fun TestScope.model() = FileTreeModel(backgroundScope) { side, root, path ->
        calls += Triple(side, root, path)
        if (failNext) { failNext = false; throw IllegalStateException("offline") }
        folders[Triple(side, root, path)] ?: error("no such folder")
    }.also {
        it.setRoots(FileSide.Pc, listOf(TreeRootInfo("docs", "Documents"), TreeRootInfo("pics", "Pictures", isWritable = false)))
        it.setRoots(FileSide.Phone, listOf(TreeRootInfo("content://tree/DCIM", "DCIM", isShared = true)))
    }

    private fun FileTreeModel.row(label: String) = rows.value.single { it.label == label && it.kind != TreeRow.Kind.Device }

    @Test
    fun bothDevicesStartOpen_WithTheirTopLevelFolders() = runTest(UnconfinedTestDispatcher()) {
        val tree = model()

        val rows = tree.rows.value
        assertEquals(listOf(TreeRow.Kind.Device, TreeRow.Kind.Root, TreeRow.Kind.Device, TreeRow.Kind.Root, TreeRow.Kind.Root), rows.map { it.kind })
        assertEquals(listOf(FileSide.Phone, FileSide.Phone, FileSide.Pc, FileSide.Pc, FileSide.Pc), rows.map { it.side })
        assertEquals(listOf("", "DCIM", "", "Documents", "Pictures"), rows.map { it.label })
        assertTrue("nothing is listed until a row is opened", calls.isEmpty())
    }

    @Test
    fun openingAFolder_ListsItsFoldersOnce_SortedByName() = runTest(UnconfinedTestDispatcher()) {
        val tree = model()

        tree.toggle(tree.row("Documents"))
        assertEquals(listOf("Documents", "Logs", "Taxes"), tree.rows.value.filter { it.side == FileSide.Pc && it.kind != TreeRow.Kind.Device }.map { it.label }.take(3))
        assertEquals(2, tree.row("Logs").depth)

        tree.toggle(tree.row("Documents")) // close
        tree.toggle(tree.row("Documents")) // and open again: no second listing
        assertEquals(1, calls.size)
    }

    @Test
    fun aListingThatFails_SaysSo_AndOpeningAgainRetries() = runTest(UnconfinedTestDispatcher()) {
        val tree = model()
        failNext = true

        tree.toggle(tree.row("Documents"))
        assertTrue(tree.row("Documents").failed)
        assertFalse(tree.row("Documents").loading)

        tree.toggle(tree.row("Documents")) // close
        tree.toggle(tree.row("Documents")) // open: tries again
        assertFalse(tree.row("Documents").failed)
        assertEquals(2, calls.size)
        assertTrue(tree.rows.value.any { it.label == "Taxes" })
    }

    @Test
    fun aRowStaysLoading_UntilItsListingArrives() = runTest(UnconfinedTestDispatcher()) {
        val gate = CompletableDeferred<List<String>>()
        val tree = FileTreeModel(backgroundScope) { _, _, _ -> gate.await() }
        tree.setRoots(FileSide.Pc, listOf(TreeRootInfo("docs", "Documents")))

        tree.toggle(tree.row("Documents"))
        assertTrue(tree.row("Documents").loading)

        gate.complete(listOf("A"))
        assertFalse(tree.row("Documents").loading)
        assertTrue(tree.rows.value.any { it.label == "A" })
    }

    @Test
    fun aFolderWithNoFoldersInside_HasNoChevron() = runTest(UnconfinedTestDispatcher()) {
        val tree = model()
        tree.toggle(tree.row("Documents"))
        tree.toggle(tree.row("Logs"))
        tree.toggle(tree.row("2025"))

        assertFalse(tree.row("2025").expandable)
        assertTrue("not opened yet, so it might have some", tree.row("Taxes").expandable)
    }

    @Test
    fun reveal_OpensEveryLevelDownToTheFolderBeingBrowsed() = runTest(UnconfinedTestDispatcher()) {
        val tree = model()

        tree.reveal(FileSide.Pc, "docs", "/Logs/2025/")

        assertEquals(listOf(Triple(FileSide.Pc, "docs", ""), Triple(FileSide.Pc, "docs", "Logs")), calls)
        assertTrue(tree.row("Documents").expanded)
        assertTrue(tree.row("Logs").expanded)
        assertEquals(FileTreeModel.folderKey(FileSide.Pc, "docs", "Logs/2025"), tree.row("2025").key)
    }

    @Test
    fun aDisconnectedPc_HidesItsFolders_AndGetsThemBackAsTheyWere() = runTest(UnconfinedTestDispatcher()) {
        val tree = model()
        tree.toggle(tree.row("Documents"))

        tree.setAvailable(FileSide.Pc, false)
        assertTrue(tree.rows.value.none { it.side == FileSide.Pc && it.kind != TreeRow.Kind.Device })

        tree.setAvailable(FileSide.Pc, true)
        assertTrue(tree.row("Documents").expanded)
        assertTrue(tree.rows.value.any { it.label == "Logs" })
    }

    @Test
    fun aFolderNoLongerShared_ForgetsWhatWasOpenInside() = runTest(UnconfinedTestDispatcher()) {
        val tree = model()
        tree.toggle(tree.row("Documents"))

        tree.setRoots(FileSide.Pc, listOf(TreeRootInfo("pics", "Pictures")))
        tree.setRoots(FileSide.Pc, listOf(TreeRootInfo("docs", "Documents"), TreeRootInfo("pics", "Pictures")))

        assertFalse(tree.row("Documents").expanded)
    }

    @Test
    fun aListingFromTheContentsPane_UpdatesTheTreeWithNoRequestOfItsOwn() = runTest(UnconfinedTestDispatcher()) {
        val tree = model()
        tree.toggle(tree.row("Documents"))
        calls.clear()

        tree.setChildren(FileSide.Pc, "docs", "", listOf("Logs", "New", "New"))

        assertTrue(tree.rows.value.any { it.label == "New" })
        assertFalse("a deleted folder goes", tree.rows.value.any { it.label == "Taxes" })
        assertEquals("one row per name, even if a provider lists two", 1, tree.rows.value.count { it.label == "New" })
        assertTrue(calls.isEmpty())
    }
}

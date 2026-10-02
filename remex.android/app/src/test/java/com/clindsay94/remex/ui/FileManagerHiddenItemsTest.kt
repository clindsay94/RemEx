package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.FileManagerLogic
import com.clindsay94.remex.ui.screens.RemoteFileEntry
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * "Show hidden items" in Files (RemEx-wqo7a.6): which rows count as hidden, that hiding is
 * display-only, and that a folder download still brings hidden children across.
 */
class FileManagerHiddenItemsTest {

    private fun file(name: String, relativePath: String? = null) =
        RemoteFileEntry(name = name, isDirectory = false, sizeBytes = 1L, relativePath = relativePath)

    private fun dir(name: String) = RemoteFileEntry(name = name, isDirectory = true, sizeBytes = 0L)

    @Test
    fun dotFilesAndDotFolders_areHidden() {
        assertTrue(FileManagerLogic.isHiddenItemName(".git"))
        assertTrue(FileManagerLogic.isHiddenItemName(".bashrc"))
        assertTrue(FileManagerLogic.isHiddenItemName(".config"))
    }

    @Test
    fun windowsSystemItems_areHidden_whateverTheCase() {
        for (name in listOf(
            "\$Recycle.Bin", "\$RECYCLE.BIN", "Config.Msi", "System Volume Information",
            "Documents and Settings", "Recovery", "\$WinREAgent", "pagefile.sys", "HIBERFIL.SYS",
            "swapfile.sys", "desktop.ini", "Thumbs.db",
        )) {
            assertTrue("$name should be hidden", FileManagerLogic.isHiddenItemName(name))
        }
    }

    @Test
    fun ordinaryNames_stayVisible_includingLookalikes() {
        for (name in listOf(
            "Documents", "My Recovery Notes", "config.msi.backup", "photo.jpg", "Recycle Bin stuff",
            "budget.v2.xlsx",
        )) {
            assertFalse("$name should be visible", FileManagerLogic.isHiddenItemName(name))
        }
    }

    @Test
    fun parentRow_isNeverHidden() {
        assertFalse(FileManagerLogic.isHiddenItemName(FileManagerLogic.PARENT_ENTRY))
        val shown = FileManagerLogic.visibleEntries(listOf(dir(".."), dir(".git")), showHidden = false)
        assertEquals(listOf(".."), shown.map { it.name })
    }

    @Test
    fun hiddenItemsAreLeftOut_whenTheSwitchIsOff() {
        val entries = listOf(dir("Projects"), dir(".git"), dir("\$Recycle.Bin"), file("notes.txt"), file("desktop.ini"))
        val shown = FileManagerLogic.visibleEntries(entries, showHidden = false)
        assertEquals(listOf("Projects", "notes.txt"), shown.map { it.name })
    }

    @Test
    fun everythingIsShown_whenTheSwitchIsOn() {
        val entries = listOf(dir("Projects"), dir(".git"), dir("\$Recycle.Bin"), file("notes.txt"))
        assertEquals(entries, FileManagerLogic.visibleEntries(entries, showHidden = true))
    }

    @Test
    fun searchHitInsideAHiddenFolder_isHiddenToo() {
        val hit = file("config", relativePath = "Projects/.git/config")
        val visibleHit = file("readme.md", relativePath = "Projects/app/readme.md")
        val shown = FileManagerLogic.visibleEntries(listOf(hit, visibleHit), showHidden = false)
        assertEquals(listOf("readme.md"), shown.map { it.name })
    }

    @Test
    fun selectionDropsWhatIsNoLongerShown() {
        val visible = listOf(dir("Projects"), file("notes.txt"))
        val kept = FileManagerLogic.selectionWithin(setOf("notes.txt", ".git"), visible)
        assertEquals(setOf("notes.txt"), kept)
    }

    /**
     * The download half of the contract: hiding is display-only. A folder download walks the PC's
     * manifest, and every row in it, hidden ones included, has to reach the download queue, the
     * same as copying the folder in Windows Explorer.
     */
    @Test
    fun folderDownloadManifest_keepsHiddenChildren() {
        val rows = JSONArray().apply {
            put(JSONObject().put("relativePath", "Projects/app").put("isDirectory", true).put("sizeBytes", 0))
            put(JSONObject().put("relativePath", "Projects/app/.git").put("isDirectory", true).put("sizeBytes", 0))
            put(JSONObject().put("relativePath", "Projects/app/.git/config").put("isDirectory", false).put("sizeBytes", 120))
            put(JSONObject().put("relativePath", "Projects/app/.env").put("isDirectory", false).put("sizeBytes", 40))
            put(JSONObject().put("relativePath", "Projects/app/desktop.ini").put("isDirectory", false).put("sizeBytes", 10))
            put(JSONObject().put("relativePath", "Projects/app/main.kt").put("isDirectory", false).put("sizeBytes", 900))
        }
        val parsed = FileManagerLogic.parseManifestEntries(rows)
        assertEquals(
            listOf(
                "Projects/app", "Projects/app/.git", "Projects/app/.git/config", "Projects/app/.env",
                "Projects/app/desktop.ini", "Projects/app/main.kt",
            ),
            parsed.map { it.relativePath },
        )
        assertEquals(120L, parsed[2].sizeBytes)
        assertTrue(parsed[1].isDirectory)
    }

    @Test
    fun manifestParse_nullArrayIsEmpty() {
        assertTrue(FileManagerLogic.parseManifestEntries(null).isEmpty())
    }
}

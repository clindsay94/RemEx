package com.clindsay94.remex.ui

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** The Files screen asks before a PC delete: nothing may wire a delete button straight to the view model. */
class FilesScreenSourceShapeTest {

    private val root: File =
        System.getProperty("remex.repoRoot")?.let(::File)
            ?: generateSequence(File(".").absoluteFile) { it.parentFile }
                .first { File(it, "remex.android").isDirectory }

    private val screen: String =
        File(root, "remex.android/app/src/main/java/com/clindsay94/remex/ui/screens/FileTransferScreen.kt").readText()

    @Test
    fun noDeleteButtonIsBoundStraightToTheViewModel() {
        val direct = Regex("""onDelete\s*=\s*\{?\s*(\w+;\s*)*vm(::|\.)(deleteSelectedEntries|deleteEntry)""")
        assertFalse("a delete must open the confirmation first", direct.containsMatchIn(screen))
    }

    @Test
    fun bothDeleteButtonsOpenTheConfirmation_andOnlyItDeletes() {
        assertEquals("context menu and selection bar", 2, Regex("""pendingDelete = (?!null)""").findAll(screen).count())
        assertTrue(screen.contains("FileManagerConfirmDialog("))
        assertEquals(1, Regex("""vm\.deleteSelectedEntries\(\)""").findAll(screen).count())
        assertEquals(1, Regex("""vm\.deleteEntry\(""").findAll(screen).count())
    }
}

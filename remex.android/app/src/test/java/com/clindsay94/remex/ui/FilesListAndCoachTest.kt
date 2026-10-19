package com.clindsay94.remex.ui

import com.clindsay94.remex.R
import com.clindsay94.remex.service.FileTransferModes
import com.clindsay94.remex.service.QueuedTransfer
import com.clindsay94.remex.service.TransferState
import com.clindsay94.remex.ui.components.canOpenTransfer
import com.clindsay94.remex.ui.screens.FilesCoach
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** Files first-visit tips, the transfers panel's Open button, and the list rows' selection semantics. */
class FilesListAndCoachTest {

    private val root: File =
        System.getProperty("remex.repoRoot")?.let(::File)
            ?: generateSequence(File(".").absoluteFile) { it.parentFile }
                .first { File(it, "remex.android").isDirectory }

    private fun source(path: String): String =
        File(root, "remex.android/app/src/main/java/com/clindsay94/remex/$path").readText()

    private fun transfer(
        state: TransferState = TransferState.Done,
        mode: String = FileTransferModes.DOWNLOAD,
        localUri: String = "content://downloads/a.txt",
    ) = QueuedTransfer(id = "1", mode = mode, fileName = "a.txt", size = 1L, localUri = localUri, state = state)

    // ── Coach marks ───────────────────────────────────────────────────────────

    @Test
    fun coach_hasTwoSteps_andTheSecondIsLast() {
        assertEquals(2, FilesCoach.STEP_COUNT)
        assertFalse(FilesCoach.isLast(0))
        assertTrue(FilesCoach.isLast(1))
    }

    @Test
    fun coach_firstStepWording_followsTheLayout() {
        assertEquals(R.string.files_coach_tree_compact, FilesCoach.bodyRes(0, expanded = false))
        assertEquals(R.string.files_coach_tree_expanded, FilesCoach.bodyRes(0, expanded = true))
        assertNotEquals(FilesCoach.bodyRes(0, false), FilesCoach.bodyRes(0, true))
    }

    @Test
    fun coach_secondStepIsTheSameOnEveryLayout() {
        assertEquals(R.string.files_coach_select, FilesCoach.bodyRes(1, expanded = false))
        assertEquals(R.string.files_coach_select, FilesCoach.bodyRes(1, expanded = true))
    }

    @Test
    fun coach_ringsTheFolderButtonOnlyOnTheNarrowFirstStep() {
        assertTrue(FilesCoach.ringsNavIcon(0, expanded = false))
        assertFalse(FilesCoach.ringsNavIcon(0, expanded = true))
        assertFalse(FilesCoach.ringsNavIcon(1, expanded = false))
    }

    @Test
    fun coachFlag_defaultsToUnseen_soANewInstallGetsTheTips() {
        val settings = source("data/SettingsManager.kt")
        assertTrue(settings.contains("preferences[FILES_COACH_SEEN_KEY] ?: false"))
    }

    // ── Open a finished download ──────────────────────────────────────────────

    @Test
    fun canOpen_onlyForAFinishedDownloadWithALocalFile() {
        assertTrue(canOpenTransfer(transfer()))
        assertFalse(canOpenTransfer(transfer(state = TransferState.Failed)))
        assertFalse(canOpenTransfer(transfer(state = TransferState.Queued)))
        assertFalse(canOpenTransfer(transfer(mode = FileTransferModes.UPLOAD)))
        assertFalse(canOpenTransfer(transfer(localUri = "")))
        assertFalse(canOpenTransfer(transfer(localUri = "   ")))
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    @Test
    fun selectionBar_countUsesThePluralNotAConcatenation() {
        val toolbar = source("ui/components/FileManagerToolbar.kt")
        assertTrue(toolbar.contains("pluralStringResource(R.plurals.file_transfer_selected_count"))
        assertFalse(toolbar.contains("R.string.file_transfer_selected"))
    }

    @Test
    fun listRowsAndGridTiles_announceSelection_andLabelTheLongPress() {
        val items = source("ui/components/FileManagerFileItem.kt")
        assertEquals("row and tile", 2, Regex("""semantics \{ if \(isSelectionMode\) selected = isSelected \}""").findAll(items).count())
        assertEquals("row and tile", 2, Regex("""onLongClickLabel = selectLabel""").findAll(items).count())
    }
}

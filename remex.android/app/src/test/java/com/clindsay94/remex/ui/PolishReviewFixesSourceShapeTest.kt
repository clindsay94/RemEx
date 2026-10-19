package com.clindsay94.remex.ui

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 3.1 polish review fixes that live in Compose wiring this module can't run, so they're source-scanned
 * (same precedent as `SweepLaneKSourceShapeTest`).
 */
class PolishReviewFixesSourceShapeTest {

    private val root: File =
        System.getProperty("remex.repoRoot")?.let(::File)
            ?: File(".").absoluteFile.let { generateSequence(it) { p -> p.parentFile }
                .firstOrNull { File(it, "remex.android").isDirectory } }
            ?: error("could not locate the repository root")

    private fun source(relative: String): String {
        val file = File(root, "remex.android/app/src/main/java/com/clindsay94/remex/$relative")
        assertTrue("expected to find $relative at ${file.path}", file.isFile)
        return file.readText()
    }

    @Test
    fun `launch and end confirmations are one-shot events, not state that replays`() {
        val launcherVm = source("ui/screens/AppLauncherViewModel.kt")
        assertFalse(launcherVm.contains("MutableStateFlow<LaunchOutcome?>"))
        assertTrue(launcherVm.contains("MutableSharedFlow<LaunchOutcome>(extraBufferCapacity = 1"))
        val taskVm = source("ui/screens/TaskManagerViewModel.kt")
        assertFalse(taskVm.contains("_killResult = MutableStateFlow"))
        assertTrue(taskVm.contains("MutableSharedFlow<String>(extraBufferCapacity = 1"))

        assertTrue(source("ui/screens/AppLauncherScreen.kt").contains("rememberOneShotEvent(viewModel.launchOutcomes)"))
        assertTrue(source("ui/screens/TaskManagerScreen.kt").contains("rememberOneShotEvent(viewModel.killResults)"))
        val helper = source("ui/components/OneShotEvent.kt")
        assertTrue(helper.contains("repeatOnLifecycle(Lifecycle.State.STARTED)"))
    }

    @Test
    fun `Back closing a search also collapses the search field on both sides`() {
        val src = source("ui/screens/FileTransferScreen.kt")
        assertEquals(2, Regex("""CloseSearch -> \{[^}]*searchCloses\+\+ \}""").findAll(src).count())
        assertTrue(src.contains("searchResetKey = searchCloses,"))
        assertTrue(src.contains("searchResetKey = Triple(state.rootId, state.path, searchCloses),"))
    }

    @Test
    fun `shuffle and open-in-new draw local glyphs, not material-icons-extended`() {
        assertFalse(source("ui/screens/PersonalizationScreen.kt").contains("icons.filled.Shuffle"))
        assertFalse(source("ui/components/FileManagerQueuePanel.kt").contains("icons.automirrored.filled.OpenInNew"))
    }
}

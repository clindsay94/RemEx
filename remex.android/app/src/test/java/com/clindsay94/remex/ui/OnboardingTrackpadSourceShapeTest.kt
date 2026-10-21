package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.MouseButtons
import com.clindsay94.remex.ui.screens.trackpadReleaseButton
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 3.1 polish AN-6: tutorial replay and the trackpad keyboard button live in Compose, which this
 * module can't run, so the shapes that fix them are source-scanned (same precedent as
 * `SweepLaneKSourceShapeTest`).
 */
class OnboardingTrackpadSourceShapeTest {

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
    fun `replaying the tutorial never re-arms the first-run flag`() {
        // Replay moved from Settings to Help & diagnostics (RemEx-172ba).
        assertFalse(source("ui/screens/HelpScreen.kt").contains("resetOnboarding"))
        assertFalse(source("ui/screens/SettingsScreen.kt").contains("resetOnboarding"))
        assertFalse(source("data/SettingsManager.kt").contains("resetOnboarding"))
    }

    @Test
    fun `finishing a replay goes back where it was opened, and a first run still navigates Home`() {
        val nav = source("ui/navigation/AppNavigation.kt")
        val tutorial = nav.substring(nav.indexOf("TutorialScreen("))
        val block = tutorial.substring(0, tutorial.indexOf("composable<PrimaryNav>"))
        // Replay returns to Help, not to Home (RemEx-172ba); the decision is tutorialExit.
        assertTrue(block.contains("tutorialExit(navController.previousBackStackEntry != null)"))
        assertTrue(block.contains("TutorialExit.BackToLauncher -> navController.popBackStack()"))
        assertTrue("first run has no Home underneath, so it must still navigate", block.contains("navController.navigate(PrimaryNav)"))
    }

    @Test
    fun `tutorial Back steps to the previous page`() {
        val src = source("ui/screens/TutorialScreen.kt")
        assertTrue(src.contains("BackHandler(enabled = pagerState.currentPage > 0)"))
        assertTrue(src.contains("R.string.tutorial_page_of"))
    }

    @Test
    fun `the trackpad keyboard button forces the keyboard up`() {
        val src = source("ui/screens/RemoteMouseScreen.kt")
        assertTrue(src.contains("showSoftInput"))
        assertTrue(src.contains("keyboardController?.show()"))
    }

    @Test
    fun `holding the trackpad sends a right click on release`() {
        assertEquals(MouseButtons.LEFT, trackpadReleaseButton(heldMillis = 120, longPressMillis = 400, dragged = false))
        assertEquals(MouseButtons.RIGHT, trackpadReleaseButton(heldMillis = 400, longPressMillis = 400, dragged = false))
        assertEquals(MouseButtons.RIGHT, trackpadReleaseButton(heldMillis = 2_000, longPressMillis = 400, dragged = false))
    }

    @Test
    fun `a held or slow drag never clicks`() {
        assertNull(trackpadReleaseButton(heldMillis = 2_000, longPressMillis = 400, dragged = true))
        assertNull(trackpadReleaseButton(heldMillis = 50, longPressMillis = 400, dragged = true))
    }

    @Test
    fun `the trackpad has no onLongPress to swallow a slow drag`() {
        val src = source("ui/screens/RemoteMouseScreen.kt")
        assertFalse("onLongPress consumes the rest of the touch and freezes the drag", src.contains("onLongPress"))
        assertTrue(src.contains("onDragStart = { trackpadDragged = true }"))
        assertTrue(src.contains("trackpadReleaseButton("))
    }
}

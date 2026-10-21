package com.clindsay94.remex.ui.navigation

import com.clindsay94.remex.R
import com.clindsay94.remex.ui.screens.HelpEntry
import com.clindsay94.remex.ui.screens.SettingsCategory
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** The Android IA after RemEx-172ba: Help & diagnostics, the slimmer Settings, and the shell's chrome rules. */
class HelpAndSettingsLayoutTest {

    @Test
    fun `Help lists FAQ, PC logs, Share diagnostics, Replay tutorial, then About`() {
        assertEquals(
                listOf(
                        HelpEntry.FAQ,
                        HelpEntry.PC_LOGS,
                        HelpEntry.SHARE_DIAGNOSTICS,
                        HelpEntry.REPLAY_TUTORIAL,
                        HelpEntry.ABOUT,
                ),
                HelpEntry.entries,
        )
    }

    @Test
    fun `every Help row has its own title and a line saying what it opens`() {
        assertEquals(HelpEntry.entries.size, HelpEntry.entries.map { it.titleRes }.toSet().size)
        assertEquals(HelpEntry.entries.size, HelpEntry.entries.map { it.bodyRes }.toSet().size)
        assertEquals(R.string.screen_pc_diagnostics_title, HelpEntry.PC_LOGS.titleRes)
    }

    @Test
    fun `Settings is Personalize, Input, Files and Notifications, in that order`() {
        assertEquals(
                listOf(
                        SettingsCategory.PERSONALIZATION,
                        SettingsCategory.INPUT,
                        SettingsCategory.FILE_TRANSFER,
                        SettingsCategory.NOTIFICATIONS,
                ),
                SettingsCategory.entries,
        )
    }

    @Test
    fun `Settings no longer embeds the PCs page or a Help category`() {
        val names = SettingsCategory.entries.map { it.name }
        assertFalse("CONNECTION" in names)
        assertFalse("HELP" in names)
        val settings = File("src/main/java/com/clindsay94/remex/ui/screens/SettingsScreen.kt").readText()
        assertFalse("Settings must not host ConnectionScreen", settings.contains("ConnectionScreen("))
        assertFalse(settings.contains("HelpTab"))
    }

    @Test
    fun `the pairing PIN screen hides the navigation bar like the QR scanner`() {
        assertTrue(PairingRoute::class in noNavChrome)
        assertTrue(Screen.QrScanner::class in noNavChrome)
        // The tabs and More destinations keep their chrome.
        assertFalse(Screen.Help::class in noNavChrome)
        assertFalse(Screen.Connection::class in noNavChrome)
    }

    @Test
    fun `a replayed tutorial goes back where it was opened, a first run lands on Home`() {
        assertEquals(TutorialExit.BackToLauncher, tutorialExit(hasScreenUnderneath = true))
        assertEquals(TutorialExit.FirstRunHome, tutorialExit(hasScreenUnderneath = false))
    }

    @Test
    fun `the stale drawer comment is gone, since there is no drawer`() {
        val nav = File("src/main/java/com/clindsay94/remex/ui/navigation/AppNavigation.kt").readText()
        assertFalse(nav.contains("NavigationDrawer on expanded"))
    }
}
